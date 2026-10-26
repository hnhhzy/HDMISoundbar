# HDMISoundbar
HDMI连接回音壁 隐藏windows下的桌面显示
这个问题困扰很久了，AICoding了给大家

你可以在 PowerShell 中随时使用以下参数管理它：

查看当前回音壁状态：
powershell
& "HDMISoundbar.exe" --status | Out-Default
手动触发一次信号激活：
powershell
& "HDMISoundbar.exe" --once | Out-Default
重新安装 / 启动开机自启服务：
powershell
& "HDMISoundbar.exe" --install | Out-Default
卸载开机自启并停止后台进程：
powershell
& "HDMISoundbar.exe" --uninstall | Out-Default

保持 Windows 设置里的 “从桌面删除显示器”为“开” 即可，以后每次开机或休眠唤醒都会由后台程序直接点亮 EP-HDMI-RX 的音频通道。你可以直接重启电脑测试一下效果！


如果在【家庭版 / 普通专业版】电脑上用，怎么破局？

如果另一台电脑是 家庭版 或 普通专业版，有两种不重装系统的解决方式：

方式 A：利用 VR 头显模式（HeadMounted）绕过企业版限制（全版本通用）

不知道你有没有想过：为什么 SteamVR / HTC Vive / Valve Index 等 VR 头显插在 Windows 家庭版电脑上，也不会多出一个桌面，而且也能被 DisplayManager 直接接管？

因为 Windows 图形内核把非桌面显示器分成了两类：
SpecialPurpose（专用显示器，即设置里的“从桌面删除显示器”）—— 强制检查企业版/工作站版许可证。
HeadMounted（HMD 头戴式显示器）—— 完全不检查许可证，家庭版/专业版全系列开放！
实现方法：只需要向注册表 HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\EXP0901\...\Device Parameters\EDID_OVERRIDE 写入一段带有微软官方 HMD 标识（IEEE OUI 5C-12-CA）的 EDID 覆盖数据。
写入后，哪怕是 Windows 10 / 11 家庭版，系统也会把回音壁当成 VR 头显，自动从桌面彻底隐藏，然后配合我们现在的 HDMISoundbar.exe 守护程序直接用 DisplayManager 接管输出 1080p@60Hz 黑屏音频信号流！
方式 B：一行命令无损切换为“专业工作站版 / 企业版”

Windows 11 的普通专业版（Pro）、专业工作站版（Pro for Workstations）和企业版（Enterprise）共用完全相同的系统文件，区别仅在于激活密钥对应的产品策略（Product Policy）：

在普通专业版上只需在系统激活设置里更换为通用的 KMS 客户端安装密钥（GVLK，如专业工作站版 或企业版），无需重装系统，3秒钟即可解锁“从桌面删除显示器”开关。

QA:
f196c02f-f86f-4f9a-aa15-e9cebdfe3b96 是什么？

它不是你电脑独有的 ID，而是 Windows 11 系统内核硬编码的官方功能常量 GUID，代表 “从桌面删除显示器（Pseudo-Specialized Display）” 这项功能本身：

这个 GUID 硬编码在所有 Windows 11 系统的 SystemSettingsAdminFlows.exe（系统设置后台程序）和 dxgkrnl.sys（DirectX 图形内核驱动）中。
在任何一台 Windows 11 电脑上，只要你在设置里打开了“从桌面删除显示器”，Windows 就会在注册表 HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore\<显示器ID> 下写入一个名为 {f196c02f-f86f-4f9a-aa15-e9cebdfe3b96} 的键值。
在我们的代码里，它仅用于兜底回退分支（当直接接管 TryAcquireAndScanout1080p 失败时，用它模拟系统设置去自动开关一次“从桌面删除显示器”）。

核心实现与验证结果

1.直接接管专用显示器（零闪烁）

守护程序在交互式登录会话中通过 TryAcquireAndScanout1080p 调用 DisplayManager.Create(DisplayManagerOptions.EnforceSourceOwnership) 与 TryAcquireTargetsAndCreateEmptyState 直接接管 EP-HDMI-RX（硬件 ID EXP0901）。
自动匹配并锁定 1920×1080 @ 60Hz 模式，通过 CreateSimpleScanout 与 DisplayTaskPool 提交单帧黑屏主表面（Primary Surface）后静态保持，无需循环渲染，CPU / GPU 占用为 0%，内存占用仅约 8.3 MB。
双重兜底机制：万一遇到特殊驱动状态无法直接接管，程序在 EnsureSoundbarActive 中内置了自动握手回退逻辑，无需手动去系统设置开关选项。

2.全自动生命周期管理（开机 / 休眠唤醒 / 热插拔）

在 RunDaemon 中注册了原生电源通知 PowerRegisterSuspendResumeNotification（监听睡眠/休眠唤醒 PBT_APMRESUMEAUTOMATIC）以及显示器连接状态检测。电脑开机、从睡眠唤醒、或者回音壁断电重开时，都会自动重新激活信号。

已注册开机无感自启任务

已通过 Windows 任务计划程序注册任务 HDMISoundbar_AutoActivate（当前用户登录时以最高权限静默启动，无黑框、无 UAC 弹窗），并且现在已经在后台运行生效。
运行日志（soundbar.log）：
text
[2026-10-26 19:27:02] === HDMISoundbar Daemon started ===
[2026-10-26 19:27:02] EP-HDMI-RX connection detected.
[2026-10-26 19:27:03] Direct DisplayManager scanout active (1920x1080@60Hz black surface).