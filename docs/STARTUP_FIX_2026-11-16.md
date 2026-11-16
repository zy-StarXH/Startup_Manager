# ChatGPT / Tailscale 重启自启动排查与修复

## 本机证据

- `Logs/startupmanager.log` 在 2026-11-12 14:16:53 记录 ChatGPT 服务关闭成功；活动备份的服务名是 `CodexSandboxService.OpenAI.Codex`，修改前为自动启动。
- 本次只读查询显示，该服务和 Tailscale 服务均为 `DEMAND_START`（手动），没有证据表明启动类型写入失败或重启后变回自动。
- 当前 OpenAI 应用包为 `OpenAI.Codex_2p2nqsd0c76g0`，其清单另有 `windows.startupTask`，TaskId=`App`，名称 ChatGPT；Windows 保存的 `State=2`，仍允许登录启动。
- `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\Tailscale.lnk` 仍存在，目标为 `D:\Tailscale\tailscale-ipn.exe`。HKCU StartupApproved 已是 `03`，但不能据此断言公共快捷方式已移除或所有启动途径已关闭。现有日志没有对应 Tailscale 修改记录，无法还原当时的完整操作。

服务的“手动”类型允许其他调用方启动它。服务项与桌面程序登录入口必须分别处理。

## 修复行为（1.1.1）

- 补齐现有 MSIX StartupTask 提供程序：枚举、缺失状态时使用清单默认值、禁用、启用、备份和精确恢复。状态 0/1/2 可管理；策略状态 3/4 展示但锁定。读取应用包失败会报告扫描警告。
- 完善现有启动文件夹隔离实现。关闭时移动原文件到 `%LocalAppData%\StartupManager\DisabledStartupItems`，并写 StartupApproved；启用/恢复时放回。公共文件夹操作要求管理员权限，影响所有用户。
- 列表中，仍在启动文件夹的文件视为尚有启动入口；即使旧版写过禁用标记也允许再次关闭，并显示原因。安装器重新创建的文件不会被旧隔离副本隐藏，恢复不会覆盖同名文件。
- 修复隔离备份恢复方向错误，支持重复恢复和开启失败后回滚；恢复路径、范围和 ID 必须匹配实际启动目录。
- 日志记录操作名称、来源、请求状态、写入阶段和异常原因。界面说明手动服务仍可能被启动，不再保证“重启绝不会运行”。

## 实际执行及验证

- 已通过事务引擎关闭本机 ChatGPT 的登录启动任务，生成活动备份 `30f42beeb2dd45708d4116a3beb56c3e`，写后验证通过；原有服务和备份保持。
- Tailscale 的公共快捷方式需要用户在新版界面关闭并确认 UAC。本次自动化没有触发 UAC，也没有停止 Tailscale 网络服务。
- Release：209 项后端测试通过，1 项因当前非管理员无法创建临时计划任务而跳过。新增覆盖应用包状态、异常数据保护、清单默认值、策略锁定、状态恢复、公共启动文件恢复、开启回滚和同名文件冲突。
- 发布版 WPF/WebView2 实测：无扫描警告，ChatGPT 应用包开关为关闭、恢复中心含其备份；Tailscale 显示为待关闭的公共启动文件，详情包含移出机制和管理员提示。只读验收未触发 UAC。
- 尚未执行真实重启验收。关闭启动入口不会结束当前运行的应用或服务。

Microsoft StartupTask 状态定义：[StartupTaskState](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate)。
Tailscale 后台无人值守模式说明：[Run unattended](https://tailscale.com/docs/how-to/run-unattended)。
