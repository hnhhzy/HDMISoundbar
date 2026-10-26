using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Windows.Devices.Display;
using Windows.Devices.Display.Core;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace HDMISoundbar;

internal static class Program
{
    private const string DefaultTargetMonitorName = "EP-HDMI-RX";
    private const string DefaultTargetHardwareId = "EXP0901";
    private const string TaskName = "HDMISoundbar_AutoActivate";
    private const string MutexName = @"Local\HDMISoundbar_Daemon_Mutex";

    private const int DISPLAYCONFIG_DEVICE_INFO_GET_MONITOR_INTERNAL_INFO = -7;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_MONITOR_SPECIALIZATION = 12;
    private const int DISPLAYCONFIG_DEVICE_INFO_SET_PSEUDO_SPECIALIZATION = -23;

    private static readonly Guid PseudoSpecializedGuid = new("f196c02f-f86f-4f9a-aa15-e9cebdfe3b96");
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "soundbar.log");

    // Held references if direct DisplayManager scanout acquisition succeeds
    private static DisplayManager? _heldManager;
    private static DisplayState? _heldState;
    private static DisplayDevice? _heldDevice;
    private static DisplaySource? _heldSource;
    private static DisplaySurface? _heldSurface;
    private static DisplayScanout? _heldScanout;
    private static DisplayTaskPool? _heldTaskPool;
    private static int _reconnectPending = 1;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DISPLAYCONFIG_SET_PSEUDO_SPECIALIZATION
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public Guid specializationGuid;
        public uint isSpecializationEnabled;
        public ulong token;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private const int DEVICE_NOTIFY_CALLBACK = 2;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(
        uint Flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS Recipient,
        out IntPtr RegistrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr RegistrationHandle);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfoRaw(IntPtr packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_PSEUDO_SPECIALIZATION packet);

    [DllImport("ntdll.dll")]
    private static extern int RtlHashUnicodeString(
        ref UNICODE_STRING String,
        byte CaseInSensitive,
        uint HashAlgorithm,
        out uint HashValue);

    public static int Main(string[] args)
    {
        if (args.Length > 0 && GetStdHandle(-11) == IntPtr.Zero)
        {
            AttachConsole(-1);
        }

        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "--daemon";

        switch (mode)
        {
            case "--status":
                PrintStatus();
                return 0;

            case "--once":
                Log("Running one-shot HDMI Soundbar activation...");
                bool ok = EnsureSoundbarActive(forceHandshake: true);
                Log(ok ? "One-shot activation succeeded." : "One-shot activation failed (target not found or error).");
                return ok ? 0 : 1;

            case "--install":
                return InstallScheduledTask();

            case "--uninstall":
                return UninstallScheduledTask();

            case "--daemon":
            default:
                return RunDaemon();
        }
    }

    private static int RunDaemon()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log("Another instance of HDMISoundbar daemon is already running. Exiting.");
            return 0;
        }

        Log("=== HDMISoundbar Daemon started ===");

        using var exitEvent = new ManualResetEventSlim(false);
        Interlocked.Exchange(ref _reconnectPending, 1);

        // Listen for Windows Sleep / Resume events via native PowerRegisterSuspendResumeNotification
        DeviceNotifyCallbackRoutine powerCallback = (_, type, _) =>
        {
            if (type == PBT_APMRESUMEAUTOMATIC)
            {
                Log("System resumed from sleep/hibernate. Scheduling HDMI Soundbar re-activation...");
                ReleaseDirectScanout();
                Interlocked.Exchange(ref _reconnectPending, 1);
            }
            return 0;
        };
        var subscribeParams = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
        {
            Callback = powerCallback,
            Context = IntPtr.Zero
        };
        PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref subscribeParams, out IntPtr powerNotifyHandle);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (powerNotifyHandle != IntPtr.Zero)
                PowerUnregisterSuspendResumeNotification(powerNotifyHandle);
            ReleaseDirectScanout();
            exitEvent.Set();
        };
        GC.KeepAlive(powerCallback);

        bool wasConnected = false;

        while (!exitEvent.IsSet)
        {
            try
            {
                bool isConnected = IsSoundbarConnected();

                if (isConnected && !wasConnected)
                {
                    Log("EP-HDMI-RX connection detected.");
                    Interlocked.Exchange(ref _reconnectPending, 1);
                }
                else if (!isConnected && wasConnected)
                {
                    Log("EP-HDMI-RX disconnected.");
                    ReleaseDirectScanout();
                }

                wasConnected = isConnected;

                if (isConnected && Interlocked.Exchange(ref _reconnectPending, 0) == 1)
                {
                    // Wait briefly after boot/resume/hotplug for NVIDIA driver readiness
                    Thread.Sleep(1000);
                    EnsureSoundbarActive(forceHandshake: true);
                    TrimMemory();
                }
            }
            catch (Exception ex)
            {
                Log($"Daemon loop error: {ex.Message}");
            }

            exitEvent.Wait(TimeSpan.FromSeconds(5));
        }

        return 0;
    }

    private static DisplayTarget? FindSoundbarTarget(DisplayManager manager)
    {
        var targets = manager.GetCurrentTargets();
        foreach (var target in targets)
        {
            if (!target.IsConnected)
                continue;

            var monitor = target.TryGetMonitor();
            string displayName = monitor?.DisplayName ?? string.Empty;
            string interfacePath = target.DeviceInterfacePath ?? string.Empty;

            if (displayName.Equals(DefaultTargetMonitorName, StringComparison.OrdinalIgnoreCase) ||
                interfacePath.Contains(DefaultTargetHardwareId, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }
        return null;
    }

    private static bool IsSoundbarConnected()
    {
        using var manager = DisplayManager.Create(DisplayManagerOptions.None);
        return FindSoundbarTarget(manager) != null;
    }

    private static void PrintStatus()
    {
        using var manager = DisplayManager.Create(DisplayManagerOptions.None);
        var target = FindSoundbarTarget(manager);
        if (target == null)
        {
            Console.WriteLine("Status: EP-HDMI-RX is NOT connected.");
            return;
        }

        var monitor = target.TryGetMonitor();
        var adapterId = new LUID { LowPart = target.Adapter.Id.LowPart, HighPart = target.Adapter.Id.HighPart };
        uint targetId = target.AdapterRelativeId;

        string dataStoreName = GetMonitorDataStoreName(adapterId, targetId) ?? "(Unknown)";
        ulong token = dataStoreName != "(Unknown)" ? ComputeSpecializationToken(dataStoreName, PseudoSpecializedGuid) : 0;

        var specPkt = new DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_MONITOR_SPECIALIZATION,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION>(),
                adapterId = adapterId,
                id = targetId
            }
        };
        DisplayConfigGetDeviceInfo(ref specPkt);
        bool isRemovedFromDesktop = (specPkt.value & 1) != 0;

        string resInfo = "Inactive";
        var stateResult = manager.TryReadCurrentStateForAllTargets();
        if (stateResult.ErrorCode == DisplayManagerResult.Success)
        {
            var path = stateResult.State.GetPathForTarget(target);
            if (path != null && path.TargetResolution != null)
            {
                resInfo = $"{path.TargetResolution.Value.Width}x{path.TargetResolution.Value.Height}, Status={path.Status}";
            }
        }

        Console.WriteLine("=== EP-HDMI-RX Status ===");
        Console.WriteLine($"  Monitor Name          : {monitor?.DisplayName}");
        Console.WriteLine($"  Adapter LUID          : {adapterId.LowPart}:{adapterId.HighPart}");
        Console.WriteLine($"  Target ID             : {targetId}");
        Console.WriteLine($"  UsageKind             : {target.UsageKind}");
        Console.WriteLine($"  MonitorDataStore Key  : {dataStoreName}");
        Console.WriteLine($"  Specialization Token  : {token}");
        Console.WriteLine($"  Removed From Desktop  : {isRemovedFromDesktop} (RawFlags=0x{specPkt.value:X})");
        Console.WriteLine($"  Signal Path           : {resInfo}");
        Console.WriteLine($"  Log File              : {LogFilePath}");
    }

    private static bool EnsureSoundbarActive(bool forceHandshake)
    {
        // Step 1: Attempt direct DisplayManager Custom Compositor acquisition & 1080p@60Hz black scanout
        if (TryAcquireAndScanout1080p())
        {
            Log("Direct DisplayManager scanout active (1920x1080@60Hz black surface).");
            return true;
        }

        // Step 2: If Windows blocks direct user-mode target acquisition (TargetAccessDenied because TestSigning is off),
        // use DisplayManager + DisplayConfig specialization handshake to clock up the 1080p@60Hz HDMI audio signal
        // and immediately remove EP-HDMI-RX from the desktop.
        using var manager = DisplayManager.Create(DisplayManagerOptions.None);
        var target = FindSoundbarTarget(manager);
        if (target == null)
        {
            Log("EP-HDMI-RX not found.");
            return false;
        }

        var adapterId = new LUID { LowPart = target.Adapter.Id.LowPart, HighPart = target.Adapter.Id.HighPart };
        uint targetId = target.AdapterRelativeId;

        string? dataStoreName = GetMonitorDataStoreName(adapterId, targetId);
        if (string.IsNullOrEmpty(dataStoreName))
        {
            Log("Failed to query MonitorDataStore name for EP-HDMI-RX.");
            return false;
        }

        ulong token = ComputeSpecializationToken(dataStoreName, PseudoSpecializedGuid);

        if (!forceHandshake)
        {
            var specPkt = new DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_GET_MONITOR_SPECIALIZATION,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_MONITOR_SPECIALIZATION>(),
                    adapterId = adapterId,
                    id = targetId
                }
            };
            DisplayConfigGetDeviceInfo(ref specPkt);
            bool isSpecialized = (specPkt.value & 1) != 0;
            if (isSpecialized)
                return true;
        }

        Log($"Activating EP-HDMI-RX (TargetId={targetId}, DataStore={dataStoreName})...");

        // 1) Disable "Remove from desktop" briefly so Windows connects the VidPn path and starts the 1080p@60Hz HDMI TMDS clock
        int errOff = SetPseudoSpecialization(adapterId, targetId, enable: false, token);
        if (errOff != 0)
        {
            Log($"SetPseudoSpecialization(false) failed with Win32 error {errOff}.");
            return false;
        }

        // 2) Wait for the HDMI audio/video handshake to complete
        Thread.Sleep(1500);

        // 3) Re-enable "Remove from desktop" so the desktop disappears while the HDMI signal clock stays alive
        int errOn = SetPseudoSpecialization(adapterId, targetId, enable: true, token);
        if (errOn != 0)
        {
            Log($"SetPseudoSpecialization(true) failed with Win32 error {errOn}.");
            return false;
        }

        Log("EP-HDMI-RX HDMI signal activated and removed from desktop successfully.");
        return true;
    }

    private static bool TryAcquireAndScanout1080p()
    {
        if (_heldScanout != null)
            return true;

        var manager = DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership);
        var target = FindSoundbarTarget(manager);
        if (target == null)
        {
            manager.Dispose();
            return false;
        }

        var acquireResult = manager.TryAcquireTargetsAndCreateEmptyState(new[] { target });
        if (acquireResult.ErrorCode != DisplayManagerResult.Success)
        {
            manager.Dispose();
            return false;
        }

        try
        {
            var state = acquireResult.State;
            var path = state.ConnectTarget(target);

            path.IsInterlaced = false;
            path.Scaling = DisplayPathScaling.Identity;
            path.SourcePixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

            var modes = path.FindModes(DisplayModeQueryOptions.None);
            var mode1080p60 = modes.FirstOrDefault(m =>
                m.TargetResolution.Width == 1920 &&
                m.TargetResolution.Height == 1080 &&
                Math.Abs((double)m.PresentationRate.VerticalSyncRate.Numerator / m.PresentationRate.VerticalSyncRate.Denominator - 60.0) < 1.0)
                ?? modes.FirstOrDefault();

            if (mode1080p60 != null)
            {
                path.ApplyPropertiesFromMode(mode1080p60);
            }
            else
            {
                path.SourceResolution = new SizeInt32 { Width = 1920, Height = 1080 };
                path.TargetResolution = new SizeInt32 { Width = 1920, Height = 1080 };
            }

            var applyResult = state.TryApply(DisplayStateApplyOptions.None);
            if (applyResult.Status != DisplayStateOperationStatus.Success)
            {
                manager.Dispose();
                return false;
            }

            var device = manager.CreateDisplayDevice(target.Adapter);
            var source = device.CreateScanoutSource(target);

            var primaryDesc = new DisplayPrimaryDescription(
                1920,
                1080,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXColorSpace.RgbFullG22NoneP709,
                false,
                new Direct3DMultisampleDescription { Count = 1, Quality = 0 });

            var surface = device.CreatePrimary(target, primaryDesc);
            var scanout = device.CreateSimpleScanout(source, surface, 0, 1);

            var taskPool = device.CreateTaskPool();
            var task = taskPool.CreateTask();
            task.SetScanout(scanout);
            taskPool.TryExecuteTask(task);

            _heldManager = manager;
            _heldState = state;
            _heldDevice = device;
            _heldSource = source;
            _heldSurface = surface;
            _heldScanout = scanout;
            _heldTaskPool = taskPool;
            return true;
        }
        catch (Exception ex)
        {
            Log($"Direct scanout exception: {ex.Message}");
            manager.Dispose();
            return false;
        }
    }

    private static void ReleaseDirectScanout()
    {
        _heldTaskPool = null;
        _heldScanout = null;
        _heldSurface = null;
        _heldSource = null;
        _heldDevice = null;
        _heldState = null;
        _heldManager?.Dispose();
        _heldManager = null;
    }

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch
        {
        }
    }

    private static string? GetMonitorDataStoreName(LUID adapterId, uint targetId)
    {
        const int packetSize = 944;
        IntPtr buf = Marshal.AllocHGlobal(packetSize);
        try
        {
            for (int i = 0; i < packetSize; i += 4)
                Marshal.WriteInt32(buf, i, 0);

            Marshal.WriteInt32(buf, 0, DISPLAYCONFIG_DEVICE_INFO_GET_MONITOR_INTERNAL_INFO);
            Marshal.WriteInt32(buf, 4, packetSize);
            Marshal.WriteInt32(buf, 8, (int)adapterId.LowPart);
            Marshal.WriteInt32(buf, 12, adapterId.HighPart);
            Marshal.WriteInt32(buf, 16, (int)targetId);

            int err = DisplayConfigGetDeviceInfoRaw(buf);
            if (err != 0)
                return null;

            return Marshal.PtrToStringUni(buf + 20);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static unsafe ulong ComputeSpecializationToken(string monitorDataStoreName, Guid guid)
    {
        string combined = monitorDataStoreName + guid.ToString("B");
        fixed (char* ptr = combined)
        {
            ushort totalByteLen = (ushort)(combined.Length * 2);
            ushort firstHalfByteLen = (ushort)((totalByteLen >> 1) & 0x7FFE);
            ushort secondHalfByteLen = (ushort)(totalByteLen - firstHalfByteLen);

            var u1 = new UNICODE_STRING
            {
                Length = firstHalfByteLen,
                MaximumLength = totalByteLen,
                Buffer = (IntPtr)ptr
            };
            RtlHashUnicodeString(ref u1, 1, 0, out uint lowHash);

            var u2 = new UNICODE_STRING
            {
                Length = secondHalfByteLen,
                MaximumLength = secondHalfByteLen,
                Buffer = (IntPtr)(ptr + (firstHalfByteLen / 2))
            };
            RtlHashUnicodeString(ref u2, 1, 0, out uint highHash);

            return ((ulong)highHash << 32) | lowHash;
        }
    }

    private static int SetPseudoSpecialization(LUID adapterId, uint targetId, bool enable, ulong token)
    {
        var pkt = new DISPLAYCONFIG_SET_PSEUDO_SPECIALIZATION
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_SET_PSEUDO_SPECIALIZATION,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_PSEUDO_SPECIALIZATION>(),
                adapterId = adapterId,
                id = targetId
            },
            specializationGuid = PseudoSpecializedGuid,
            isSpecializationEnabled = enable ? 1u : 0u,
            token = token
        };
        return DisplayConfigSetDeviceInfo(ref pkt);
    }

    private static int InstallScheduledTask()
    {
        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HDMISoundbar.exe");
        string args = $"--daemon";

        // Create a scheduled task that runs at user logon with highest privileges, no time limit, and allows running on battery
        string psScript = $@"
$action = New-ScheduledTaskAction -Execute '{exePath}' -Argument '{args}' -WorkingDirectory '{AppContext.BaseDirectory}'
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName '{TaskName}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName '{TaskName}'
";
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc?.WaitForExit();
        int code = proc?.ExitCode ?? -1;
        Console.WriteLine(code == 0
            ? $"Successfully installed and started auto-start task '{TaskName}' -> {exePath}"
            : $"Failed to install scheduled task (exit code {code}).");
        return code;
    }

    private static int UninstallScheduledTask()
    {
        string psScript = $@"
Stop-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process -Name 'HDMISoundbar' -ErrorAction SilentlyContinue | Where-Object {{ $_.Id -ne {Environment.ProcessId} }} | Stop-Process -Force -ErrorAction SilentlyContinue
";
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        proc?.WaitForExit();
        Console.WriteLine($"Uninstalled scheduled task '{TaskName}' and stopped background daemon.");
        return 0;
    }

    private static void Log(string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch
        {
            // Ignore file lock errors
        }
    }
}
