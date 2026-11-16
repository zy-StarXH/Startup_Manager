# 启动项管理器 · 项目交接文档

> 最后更新：2026-11-17  
> 当前版本：**1.1.1**  
> 仓库：<https://github.com/zy-StarXH/Startup_Manager>  
> 主分支：`main`，当前发布提交以 `git log` 为准（`v1.1.1` 标签）  
> 最新发布：<https://github.com/zy-StarXH/Startup_Manager/releases/tag/v1.1.1>

## 0. 交接摘要

这是一个 Windows 11 开机启动项管理工具。UI 采用用户确认过的 Fluent/WinUI 风格，
由 WPF 原生窗口承载 WebView2；C# 后端直接读取和修改真实系统状态，所有修改先备份、
再验证，失败时尝试回滚。

当前 `v1.1.1` 已发布，新增 MSIX 应用登录启动任务管理，并把启动文件夹禁用
改为“移出启动目录 + 可恢复存储”。最后验证结果：

- Release 构建成功，**209 项后端测试通过，1 项安全跳过**。
- Release UI 实测：启动项 30 项（29 个有图标）、服务项 94 项（93 个有图标）、
  计划任务 50 项；无扫描警告；界面版本显示 1.1.1。
- ChatGPT 的应用包 StartupTask 和服务分别显示且均为本工具禁用；Tailscale
  公共启动文件夹快捷方式已移出并可由恢复中心恢复。
- 尚未做真实重启验收；只读 UI 验收未触发 UAC。

下一阶段最值得处理的是：真实重启验收、普通服务/计划任务的人工 UAC 验收、
UAC 取消流程实测，以及把版本号和发布流程从手工步骤收敛成可重复脚本。

## 1. 用户需求与已完成的迭代

| 阶段 | 结果 |
|---|---|
| 原始需求 | 做一个真实管理 Windows 开机自启动项的简洁工具，界面参考微软扁平风格；先交 HTML 原型确认 UI。 |
| UI 原型 | `prototype.html` 已确认，后续生产 UI 基于该视觉。 |
| 正式实现 | .NET 8 + WPF + WebView2；注册表/启动文件夹、计划任务、服务四类真实来源。 |
| 分类视图 | “启动项”页内分为 **启动项 / 服务项 / 计划任务**，带数量、状态筛选和当前分类批量操作。 |
| 字体设置 | 设置中 90%/100%/110%/125%，通过 WebView2 `ZoomFactor` 生效并持久化。 |
| 未验证项 | 发布者/路径无法确认的服务默认锁定，但允许用户明确“允许管理”；硬系统保护不可覆盖。 |
| ChatGPT 关闭失败 | 定位为 MSIX `SERVICE_PACKAGED_PROCESS` 服务拒绝 SCM 修改，增加管理员注册表 `Start` 兜底、备份和验证。 |
| 真实图标 | Shell 提取 64×64 图标，失败回退字母色块；`v1.1.0` 已发布。 |
| 重启仍自启排查 | 发现 MSIX 应用还有独立 StartupTask；启动文件夹只写 StartupApproved 不能保证入口消失。 |
| v1.1.1 修复 | 新增应用包登录启动任务提供程序；启动文件夹改为移出到可恢复存储；真实重启验收仍待人工完成。 |

### 用户偏好与交互约定

- 界面和文档以中文为主。
- 不要显示虚假的“开机耗时/加速秒数”；只展示真实系统状态。
- 破坏性或需要 UAC 的动作必须明确告知，不能假装成功。
- 用户已经明确授权将项目推送到上述 GitHub 仓库并发布 Release。
- 本机用户数据（设置、白名单、备份、日志）必须保留，不能为了测试清理。

## 2. 当前交付状态

### v1.1.1 相对 v1.1.0

- 新增 `src/StartupManager.Core/Providers/PackagedAppStartupProvider.cs`：
  枚举 MSIX `windows.startupTask`，读取/修改/备份/恢复 Windows 保存的状态；
  状态 0/1/2 可管理，3/4 由策略控制并锁定。
- 新增 `src/StartupManager.Core/Providers/RegistryFolder/StartupFolderQuarantine.cs`：
  启动文件夹禁用时把原文件移入 `%LocalAppData%\StartupManager\DisabledStartupItems`，
  启用/恢复时放回；启动文件夹中存在文件即视为仍有启动入口。
- 新增 `StartupSource.PackagedApp` / `pkg-` 稳定 ID，接入扫描、保护、UAC、
  备份和“启动项”分类。
- 公共启动文件夹操作要求管理员权限；恢复不会覆盖安装器重新创建的同名文件。
- UI 明确“服务手动启动仍可能被其他程序/触发器启动”；日志增加操作阶段和异常原因。
- 程序版本、两个 manifest、侧栏/设置页版本更新为 1.1.1。
- v1.1.0 的真实图标和 `IconService` 保持不变。

### Git / Release

- `main` 当前发布提交以 `git log -1` 为准，标签 `v1.1.1`。
- Release 标题：`Startup Manager v1.1.1`
- 发布资产：
  - `StartupManager-win-x64-v1.1.1.zip`
  - `StartupManager-win-x64-v1.1.1.zip.sha256`
- SHA-256 以下载页面的校验文件为准；发布后应从 GitHub 重新下载核对。

### 已完成验证

- `tools/build.ps1 -Configuration Release -Test`
  - 通过 209，跳过 1（非管理员无法创建临时计划任务）。
- Debug/Release 原生 UI 均能显示真实图标。
- Release 宿主真实扫描当前机器：
  - 启动项 30 项，29 个有图标；服务 94 项，93 个有图标；计划任务 50 项。
  - ChatGPT 服务无图标资源，使用字母色块。
  - 无扫描警告，界面版本显示 1.1.1。
- 应用包启动任务：ChatGPT 的 `StartupTask` 显示为“应用包”，处于本工具禁用
  状态，并有活动备份；Tailscale 的公共启动文件夹项已移出并显示为禁用。
- 设置中的字体缩放、分类计数、详情、恢复中心和允许管理流程已在之前版本完成浏览器与原生宿主验证。
- 真实注册表启动项关闭/恢复链路已完成端到端验证。
- ChatGPT MSIX 应用包服务在人工 UAC 确认后成功写入注册表 `Start=3`，SCM 和注册表均重新读取为 `DEMAND_START`，并生成活动备份。
- 启动文件夹隔离、重复恢复、开启回滚和同名文件冲突已有隔离测试；真实公共文件夹
  的 UAC 修改未在本次自动化中执行。
- v1.1.1 发布 ZIP 应从 GitHub 重新下载并核对 SHA-256。

### 仍未完成的人工验收

- 真实重启后确认 ChatGPT / Tailscale 等入口不再启动。
- 普通（非应用包）第三方服务的 SCM `自动 → 手动` 切换。
- 真实计划任务的 `Enabled` 切换。
- 在真实 Windows 安全桌面上故意取消 UAC 的完整 UI 流程。

自动测试没有触发 UAC，也不应该自动触发 UAC。

## 3. 技术架构

### 项目结构

| 路径 | 作用 |
|---|---|
| `src/StartupManager.Core/` | 扫描、模型、保护策略、事务、备份、提权协议。无 WPF 依赖。 |
| `src/StartupManager.App/` | WPF + WebView2 宿主、桥接、系统托盘、原生标题栏、图标提取。 |
| `src/StartupManager.Elevated/` | 一次性 UAC 助手，只接受受校验的类型化请求。 |
| `tests/StartupManager.Core.Tests/` | xUnit；大部分使用临时 HKCU/内存/假提供程序，不修改真实服务。 |
| `web/` | 生产 UI，纯 HTML/CSS/JS，不直接访问系统。 |
| `tools/` | `bootstrap.ps1`、`build.ps1`、`publish.ps1`、原生 QA 脚本。 |
| `docs/` | `CONTRACT.md`（桥接/领域契约）、`SAFETY.md`、`TESTING.md`、`AGENT_BRIEF.md`。 |
| `prototype.html` | 用户确认过的 UI 设计参考。 |
| `artifacts/` | 本地发布产物，被 `.gitignore` 排除。 |

### 数据流

```text
web/index.html + app.js + bridge.js
        │  window.chrome.webview.postMessage(JSON)
        ▼
StartupManager.App/Host/BridgeHost.cs
        │  IStartupManager + UAC 路由 + 扫描后图标补全
        ▼
StartupManager.Core/Services/StartupManagerService
        │
        ├─ Scan：五个 provider + 保护策略 + 活动备份叠加
        ├─ Change：capture → persist backup → apply → verify → rollback
        └─ Backups：JSON 文件，状态 active/restored/failed
        │
        ├─ RegistryStartupProvider / StartupFolderProvider
        ├─ ScheduledTaskProvider（Task Scheduler COM）
        ├─ ServiceStartupProvider（SCM，特殊处理 MSIX 服务）
        └─ PackagedAppStartupProvider（MSIX StartupTask）
```

### 稳定 ID 与契约

详见 `docs/CONTRACT.md`，不要随意改动字段名或大小写：

- 注册表：`reg-` + `hive|view|keyPath|valueName` 的 SHA-256。
- 启动文件夹：`dir-` + `scope|fullPath` 的 SHA-256。
- 计划任务：`tsk-` + 完整任务路径的 SHA-256。
- 服务：`svc-` + 小写服务名的 SHA-256。
- MSIX StartupTask：`pkg-` + 小写 `packageFamilyName|taskId` 的 SHA-256。

启动文件夹现在以“文件是否仍在启动目录”判断 `enabled`；只有旧版
StartupApproved 禁用字节、但文件仍存在时，列表会显示仍有入口并允许再次关闭。
公共启动文件夹的禁用需要 UAC，影响所有用户。

桥接命令：`refresh`、`setEnabled`、`restoreBackup`、`saveSettings`、
`setUserOverride`、`openItemLocation`、`openBackupFolder`、`openLogFolder`、
`copyLocation`。请求必须带 `requestId`，未知命令和非法 ID 必须拒绝。

宿主在成功的修改后会重新扫描并先发送完整 `state`，再发送 `commandResult`；
UI 不应把乐观更新当成最终状态。

### 关键文件阅读顺序

1. `README.md`：用户视角和当前限制。
2. `docs/CONTRACT.md`：前后端/提供程序契约。
3. `docs/SAFETY.md`：安全规则和禁止事项。
4. `web/index.html`、`web/app.js`、`web/bridge.js`：UI 状态与交互。
5. `src/StartupManager.App/Host/BridgeHost.cs`：宿主入口、命令分发、UAC 路由。
6. `src/StartupManager.Core/Services/StartupManagerService.Scan.cs` 和 `.Changes.cs`：
   扫描、保护、事务、备份、回滚。
7. 五个 provider 和 `src/StartupManager.Core/Protection/StartupProtectionPolicy.cs`。
8. `src/StartupManager.Core/Providers/RegistryFolder/StartupFolderQuarantine.cs`：
   启动文件夹移出/恢复。
9. `src/StartupManager.Core/Providers/PackagedAppStartupProvider.cs`：MSIX StartupTask。
10. `src/StartupManager.Core/Elevation/` 与 `src/StartupManager.Elevated/Program.cs`。
11. `src/StartupManager.App/Host/IconService.cs`：v1.1.0 图标实现。

## 4. 功能与实现要点

### 4.1 扫描

- 注册表 Run：HKCU/HKLM、32/64 位视图。
- 启动文件夹：当前用户和公共启动文件夹，解析 `.lnk/.url/.exe/.bat/.cmd`；
  文件仍在启动目录即视为启用；隔离目录中的条目合成禁用行。
- MSIX StartupTask：读取当前用户应用包清单中的 `windows.startupTask`，以
  `SystemAppData` 中的 Windows 状态为准；状态 3/4 受策略锁定。
- 计划任务：只显示登录/开机触发的任务，读取原生启用状态。
- 服务：只显示自动/延迟自动服务；读取启动类型。
- 扫描失败按来源降级为 warning，不伪造数据。
- 活动备份对应的条目在实时扫描不可见时会合成一个“已由本工具禁用”的虚拟行，
  使其可以从恢复中心恢复；合成行不重新套用保护策略，但仍保留管理员要求。

### 4.2 真实修改机制

| 来源 | 修改 | 作用范围 |
|---|---|---|
| 注册表 | Windows `StartupApproved` 禁用字节 | 当前登录用户 |
| 启动文件夹 | 把原文件移入 `DisabledStartupItems`，并写 StartupApproved | 当前用户或所有用户（公共目录需 UAC） |
| MSIX StartupTask | 修改 SystemAppData 中的登录启动状态 | 当前登录用户 |
| 计划任务 | Task Scheduler 原生 `Enabled=false` | 任务 ACL / 系统 |
| 服务 | SCM 启动类型 Auto → Manual，不停止运行中的服务 | 系统 |

所有修改统一经过事务：

```text
重新扫描/确认目标
→ capture 原始 provider 状态
→ 先把 JSON 备份原子写入磁盘
→ apply 最小变更
→ 重新读取系统并 verify
→ 成功才标记 verified；失败则 restore 并标记 failed
```

### 4.3 保护与“允许管理”

- 硬保护：驱动、Windows 关键/安全服务、微软签名服务、Windows 目录服务、
  `\Microsoft\Windows\*` 任务、Windows 安全启动项。不可覆盖。
- 未验证服务：发布者或路径无法确认时锁定，但 `CanOverride=true`。
- 用户确认后把稳定 itemId 写入 `settings.userAllowedItemIds`；
  后续扫描只对策略仍标记 `CanOverride` 的项解除保护。
- 硬保护永远忽略 allowlist。
- 允许管理不等于跳过安全：仍然需要 UAC、备份、验证。
- MSIX StartupTask 的 Windows 策略状态 3/4 直接锁定，不提供“允许管理”。

相关文件：

- `src/StartupManager.Core/Protection/StartupProtectionPolicy.cs`
- `src/StartupManager.Core/Services/StartupManagerService.Scan.cs`
- `web/app.js` 中 `openUnlockDialog`、`confirmUnlock`、`setUserOverride`
- `web/index.html` 中允许管理弹窗
- `src/StartupManager.App/Host/BridgeHost.cs` 中 `SetUserOverrideAsync`

### 4.4 MSIX 应用包服务兜底

问题：ChatGPT 的 `CodexSandboxService.OpenAI.Codex` 类型为
`SERVICE_PACKAGED_PROCESS`，即使管理员也会被 SCM 拒绝
`SERVICE_CHANGE_CONFIG`。

实现：

- `ServiceStartupProvider` 识别 `SERVICE_PACKAGED_PROCESS`。
- 对这类服务读取
  `HKLM\SYSTEM\CurrentControlSet\Services\<name>\Start` 作为实际启动状态。
- 修改时写入 `Start=3`（手动）或恢复原值；不通过 SCM。
- `ServiceBackupState.UseRegistryFallback=true`，恢复时也走注册表路径。
- 写后立即读取验证；失败会抛错，不会报告假成功。
- UI 显示“应用包”标记，并说明需要重启后完全生效、应用更新可能重置。
- 只对识别出的 packaged service 使用这个兜底，不要扩大到普通服务。

相关文件：

- `src/StartupManager.Core/Providers/ServiceStartupProvider.cs`
- `src/StartupManager.Core/Providers/ServiceSupport.cs`
- `src/StartupManager.Core/Providers/ServiceNativeMethods.cs`
- `docs/SAFETY.md` 的 MSIX 段落

### 4.5 MSIX StartupTask（v1.1.1）

- 新增 `StartupSource.PackagedApp`，归入“启动项”分类，来源标签“应用包”。
- 提供程序枚举当前用户包清单中的 `windows.startupTask`，并读取
  `HKCU\Software\Classes\Local Settings\...\SystemAppData\<family>\<taskId>`
  的 `State` / `UserEnabledStartupOnce`。
- `State=0/1` 视为禁用，`2/4` 视为启用；`3/4` 是策略状态，展示但锁定。
- 修改时使用事务备份原始 DWORD；启用写 `2`，禁用写 `1`，启用时清除
  `UserEnabledStartupOnce`；恢复精确写回原值/原缺失状态。
- 应用包服务和 StartupTask 是两条独立路径：关闭服务不等于关闭桌面登录启动。
- 主要文件：`PackagedAppStartupProvider.cs`、`CoreComposition.cs`、
  `web/app.js`、`docs/CONTRACT.md`。

### 4.6 启动文件夹隔离（v1.1.1）

- 启动文件夹禁用不再只写 StartupApproved，而是把原文件移动到
  `%LocalAppData%\StartupManager\DisabledStartupItems`，再写禁用标记。
- 目录中只保存 `dir-<id>.<ext>` 和对应 `<id>.json` 清单（原路径）。
- 扫描时：文件仍在启动目录 → 启用；只在隔离目录 → 合成禁用行。
- 启用/恢复时把文件放回原位置；原位置已有同名文件时拒绝覆盖。
- 备份包含 `Quarantined`、`ManagesFile`、`QuarantineDirectory`；恢复路径必须是
  当前配置的启动目录，公共目录操作需要 UAC，影响所有用户。
- 相关文件：`StartupFolderProvider.cs`、`StartupFolderQuarantine.cs`。

### 4.7 真实图标（v1.1.0）

- `src/StartupManager.App/Host/IconService.cs`
- 在每次 scan/reload 后由 `BridgeHost` 调用 `EnrichIcons`。
- 优先用 Shell：
  `SHCreateItemFromParsingName` + `IShellItemImageFactory.GetImage`
  以 64×64、`IconOnly | BiggerSizeOk` 提取。
- 失败回退 `Icon.ExtractAssociatedIcon`。
- 结果转为 `data:image/png;base64,...` 写入 `StartupItem.Icon`。
- 按目标路径在进程内缓存；提取失败不缓存假图标，UI 自动回退为字母色块。
- 只读取图像，不启动目标程序。
- 当前 UI 使用 `appIconHtml` 判断 data URI；没有图标时保留原有色块。

### 4.8 UAC / 提权

- 主程序 `asInvoker`；只有必要操作才启动
  `StartupManager.Elevated.exe`。
- 请求写入用户专属 IPC 目录，包含操作类型、稳定 ID、nonce、过期时间和 owner。
- 助手重新校验请求路径、文件名校验、过期、nonce、操作类型，并自行重新扫描，
  不接受 UI 传入的任意路径或脚本。
- 一批操作只弹一次 UAC；取消不产生修改。
- 当前用户注册表/启动文件夹通常不需要管理员；公共启动文件夹、服务、受 ACL
  限制的任务需要 UAC。

相关文件：

- `src/StartupManager.Core/Elevation/`
- `src/StartupManager.Elevated/Program.cs`
- `src/StartupManager.App/Host/ProcessElevation.cs`
- `docs/SAFETY.md`

## 5. 本机状态（重要，勿清理）

以下为上次检查时的本机状态，仅用于提醒，不是代码事实；实际状态以运行中的扫描为准：

- 数据目录：`%LocalAppData%\StartupManager\`
  - `settings.json`
  - `Backups\`
  - `DisabledStartupItems\`（启动文件夹隔离文件）
  - `Logs\`
  - `Ipc\`
  - `WebView2\`
- ChatGPT 服务当前为手动启动：SCM `START_TYPE = 3 DEMAND_START`，注册表 `Start = 3`。
- ChatGPT 的 MSIX StartupTask 当前由本工具禁用，存在活动备份。
- Tailscale 的公共启动文件夹快捷方式已移出到隔离目录，存在活动备份；
  `C:\ProgramData\...\Startup\Tailscale.lnk` 已不存在。
- 存在多条 `active` 备份，包括 ChatGPT 服务/应用包任务、Tailscale 和其他启动
  文件夹/注册表项；另有早期失败记录。
- `settings.json` 的 `userAllowedItemIds` 当前包含 ChatGPT 服务。
- `minimizeToTray` 上次检查为 `false`，`uiFontScale` 为 `1`；用户可能再次修改。
- 不要把上述状态当作发布说明或自动化测试 fixture。
- **不要删除、覆盖或批量恢复 `%LocalAppData%\StartupManager`**，除非用户明确要求。

## 6. 构建、测试与发布

### 6.1 环境

- Windows 11 x64。
- 目标框架为 `net8.0-windows10.0.26100.0`（为 MSIX `PackageManager` API）；
  需要 Windows SDK 10.0.26100 目标包。
- 项目内用户级 .NET 8 SDK：`.tools\dotnet\dotnet.exe`，不要安装到全局 PATH。
- NuGet 缓存：`.nuget\packages`。
- Node.js、Chrome 和 WebView2 Runtime 用于 UI 验证。
- `gh` CLI **未安装**；GitHub API 通过 Git Credential Manager 中已有的凭据访问。
- 绝不打印、提交或把 GitHub token 写入脚本/日志。

### 6.2 构建与测试

在 PowerShell 中：

```powershell
cd 'D:\Zcode\projects\自启动管理'
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Release -Test
```

单独构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Debug
```

测试预期：

- 209 passed, 1 skipped。
- 跳过项：`Temporary_logon_task_can_be_disabled_restored_and_deleted`，
  因为当前会话不是管理员，Task Scheduler 拒绝创建临时任务。
- 不要为了让测试全绿而提升权限、改 UAC 或修改真实系统任务。

### 6.3 发布便携版

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1 -Configuration Release -Zip
```

输出：

- `artifacts\publish\StartupManager\`（self-contained 文件夹）
- `artifacts\publish\StartupManager.zip`
- 发布目录必须保留 `web\` 和 `StartupManager.Elevated.exe`。

发布前先关闭正在运行的 `StartupManager`，否则 DLL 会被占用，覆盖发布目录失败。

### 6.4 原生与 UI 验证

原生窗口 Smoke（不需要 UAC）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\qa-smoke.ps1 `
  -ExePath "artifacts\publish\StartupManager\StartupManager.exe"
```

它会验证标题栏按钮、DPI、窗口状态、关闭到托盘和单实例激活，并在结束时停止进程。
需要可交互桌面；不要在无人值守环境依赖它。

真实图标/版本检查（本机临时脚本，`_qa` 被 gitignore）：

```powershell
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = '--remote-debugging-port=9233'
Start-Process 'artifacts\publish\StartupManager\StartupManager.exe' `
  -WorkingDirectory 'artifacts\publish\StartupManager'
node _qa\release-check-1.1.1.mjs
```

测试期望：

- UI 版本必须为 `版本 1.1.1`，且页面实际停在“启动项”。
- 启动项分类应包含 `pkg-` 开头的应用包启动任务；ChatGPT 的 StartupTask
  应显示来源“应用包”。
- 启动项应有真实图标；没有图标资源的项（如 ChatGPT 服务）应保留字母色块。
- 扫描应无 warning；Tailscale 应显示为已移出/由本工具禁用（如果本机状态未变）。
- `_qa\release-check-1.1.1.mjs` 和原 `_qa\icons-test.mjs` 都只是本机临时测试，
  不进入仓库；修改图标或版本后要同步更新断言。

### 6.5 版本升级检查清单

版本号目前手工存在于多处，发布前全部同步：

- `Directory.Build.props` 的 `<VersionPrefix>`。
- `src/StartupManager.App/app.manifest` 的 `assemblyIdentity version`。
- `src/StartupManager.Elevated/app.manifest` 的 `assemblyIdentity version`。
- `web/index.html` 侧栏版本。
- `web/app.js` 设置页版本。
- `README.md` 标题/发布信息。
- 发布说明。

建议未来把 Web 显示版本由宿主注入或统一生成，避免漏改。

### 6.6 GitHub Release 流程

当前使用 HTTP API，因为 `gh` 不可用。本机临时脚本：

- `_qa\create-release.ps1`：从 Git Credential Manager 读取 token，创建/更新
  Release，上传 ZIP 和 SHA256。
- `_qa\release-notes-v1.1.0.md`：v1.1.0 发布说明（v1.1.1 可复用结构）。
- `_qa` 不进仓库；脚本不能在全新 clone 中直接依赖。这些脚本硬编码了
  v1.1.0，下一次发布前必须改成新版本号；版本/资产名也要同步。

推荐流程：

1. 更新版本、README、发布说明。
2. Release 构建和测试。
3. 发布 ZIP，验证版本、图标和启动。
4. 生成版本化资产：
   `StartupManager-win-x64-v<version>.zip` 和 `.sha256`。
5. `git add` / `commit`，`git push origin main`。
6. 创建并推送 annotated tag。
7. 用 API 创建 Release，上传 ZIP 和 checksum。
8. 从 Release 下载资产，重新计算 SHA-256 并核对。
9. 确认 Release 页是 Latest，资产可下载。

认证要求：

- 从 `git credential fill` 读取凭据；只在 PowerShell 变量中短暂使用。
- 不要 `Write-Host $token`，不要把 token 写入临时文件或提交。
- Release 说明含中文时，脚本本身应保持 ASCII，说明文件单独以 UTF-8 读取；
  发布 JSON 用 UTF-8 字节发送。
- 之前遇到的 GitHub 400/422 分别由中文脚本编码和 `Get-Content -Raw` 的
  PSObject 引起；使用 `[IO.File]::ReadAllText(..., UTF8)` 和 UTF-8 请求体。

## 7. 已知限制与建议优先级

### P0：需要真实人工验收

1. 真实重启，确认 ChatGPT/Tailscale 等服务、应用包任务和启动文件夹入口的行为。
2. 普通第三方自动服务的 SCM 自动/手动切换和恢复。
3. 真实登录/开机计划任务的禁用和恢复。
4. 故意取消一次真实 UAC，确认 UI 提示“已取消，未做任何更改”。

测试时选择明确的第三方安全项，不要用 Microsoft、驱动、安全组件或系统任务。

### P1：功能缺口

- **RunOnce 未纳入**。
- MSIX 应用更新可能重新声明/重置 StartupTask；服务和应用包任务必须分别处理。
- 普通服务的 SCM 路径和 packaged service 兜底是两条路径，未来修改时不要混用；
  启动文件夹的移出机制与旧版只写 StartupApproved 的备份需要兼容。
- 图标提取受文件可访问性和 MSIX 图标资源限制；失败必须继续回退，不要伪造图标。
- 真实公共启动文件夹修改需要 UAC；自动化只能验证非提权路径和只读状态。

### P2：工程质量

- 发布脚本和发布说明目前在 `_qa`（gitignored），建议把可复用部分
  正式放进 `tools/`，并避免每次手工复制 ZIP。
- 版本号多处重复，建议单一来源并注入 web UI。
- Authenticode 代码签名缺失；当前发布包未签名，SmartScreen 可能警告。
- `MainWindow.xaml.cs` 仍保留旧原型 shell 的隐藏标题栏脚本；生产 UI 不需要时
  可清理，但不要在未验证的情况下改坏 WebView 导航。
- 服务发布者判断基于文件版本信息，不是 Authenticode 信任链。
- 图标提取是扫描后的顺序任务；条目更多时可考虑并发/延迟加载，但必须保持
  UI 状态和错误回退稳定。
- 无自动更新、深色主题、Snap Layouts 悬停面板。

## 8. 给下一位 AI 的硬性约束

1. **先读**：`docs/CONTRACT.md`、`docs/SAFETY.md`、`docs/TESTING.md`、
   `README.md`，再改代码。
2. **不要执行**扫描到的启动命令；不要为了测试直接启动目标程序。
3. **不要自动触发 UAC**。需要管理员权限的验收只能让用户手动确认。
4. **不要停止运行中的服务**，不要改动驱动/安全/系统服务或真实系统任务。
5. **不要覆盖用户数据**：`%LocalAppData%\StartupManager` 下的设置、白名单、
   备份、日志和 `DisabledStartupItems` 都要保留；不要覆盖用户重新创建的启动文件。
6. **不要泄露 GitHub token**；只通过 Git Credential Manager/环境变量短暂使用。
7. 修改前后端契约时，同时更新 `docs/CONTRACT.md`、模型、序列化测试和前端。
8. 修改保护策略时，补充“硬保护不可覆盖”和“未验证服务可覆盖”的测试。
9. 任何 mutation 都必须保持：备份先落盘、apply 后重读验证、失败回滚、
   UI 只在宿主返回后显示最终状态。
10. 并行开发时遵循 `docs/AGENT_BRIEF.md` 的文件所有权和构建锁；构建统一走
    `tools/build.ps1`，避免多个进程争用 `bin/obj`。
11. 发布前先关闭正在运行的 `StartupManager`，并重新做 Release 构建、测试、
    图标验证和下载校验。
12. 对不可逆操作或会改变用户系统状态的操作，先向用户确认；用户已授权的仓库
    push/release 仍按现有流程执行。

## 9. 快速排障

| 现象 | 优先检查 |
|---|---|
| 图标不显示/加载失败 | `IconService.TryLoad` 是否返回 null；目标 `TargetPath` 是否为空；UI 是否有 `.app-ico.has-img img`；Release 是否需要重启。 |
| 应用启动后提示后端不可用 | `HostBackend.Manager` 解析失败；看 `%LocalAppData%\StartupManager\Logs\app-*.log`。 |
| 修改服务失败 | 是否 UAC 提权；是否 packaged service；看 `startupmanager.log` 和 `elevated.log`。 |
| UAC 取消后状态被改 | 不应发生；检查 `BridgeHost.RunElevatedAsync` 的取消结果和命令结果。 |
| 恢复中心条目不可恢复 | 对应备份是否 `active`；是否需要管理员；备份 `StateJson` 是否损坏。 |
| 启动文件夹项目消失/恢复失败 | 检查 `DisabledStartupItems\<id>.json` 和原文件；原位置已有同名文件时必须人工处理，不能覆盖。 |
| 应用包启动任务不显示 | 检查“扫描注册表及应用包启动项”开关、包清单是否有 `windows.startupTask`、SystemAppData 状态是否 0-4。 |
| 发布目录文件被占用 | 运行中的 `StartupManager` 进程；关闭后再 publish。 |
| Release 页面资产加载异常 | GitHub 页面动态区域偶发加载失败；用 API 或直接下载资产 URL 核验。 |

## 10. 参考链接

- 仓库：<https://github.com/zy-StarXH/Startup_Manager>
- v1.0.0：<https://github.com/zy-StarXH/Startup_Manager/releases/tag/v1.0.0>
- v1.1.0：<https://github.com/zy-StarXH/Startup_Manager/releases/tag/v1.1.0>
- v1.1.1：<https://github.com/zy-StarXH/Startup_Manager/releases/tag/v1.1.1>
- 安全规则：[`docs/SAFETY.md`](SAFETY.md)
- 桥接契约：[`docs/CONTRACT.md`](CONTRACT.md)
- 测试说明：[`docs/TESTING.md`](TESTING.md)
- 并行开发说明：[`docs/AGENT_BRIEF.md`](AGENT_BRIEF.md)
