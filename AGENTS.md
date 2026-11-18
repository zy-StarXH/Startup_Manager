# AGENTS.md · 工作区记忆

> 面向后续对话/代理的上下文复用文件。先读本文件，再按需读 `README.md` 和 `docs/`。
> 最后更新：2026-11-18（v1.3.0）

## 项目是什么

Windows 11 启动项管理器：WPF 原生窗口承载 WebView2 界面，C# 后端真实读写系统启动配置。
五类来源：注册表 Run（HKCU/HKLM，32/64 位）、启动文件夹（用户/公共）、MSIX 登录启动任务、
计划任务、自动启动的 Windows 服务。

- 方案：`StartupManager.sln`（Core / App / Elevated / 两个测试项目）
- 目标框架：`net8.0-windows10.0.26100.0`，self-contained win-x64
- 当前版本：**1.3.0**（`Directory.Build.props` 的 `VersionPrefix`）

## 命令（必须用项目内 SDK）

```bash
# 构建 / 测试（Git Bash）
cmd //c "D:\\Zcode\\projects\\自启动管理\\tools\\dotnet.cmd build StartupManager.sln -c Debug -v q --nologo"
cmd //c "D:\\Zcode\\projects\\自启动管理\\tools\\dotnet.cmd test StartupManager.sln -c Release --nologo -v q"

# 发布便携包（输出 artifacts/publish/StartupManager + StartupManager.zip）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish.ps1 -Configuration Release -Zip
```

- 不要直接调用系统 `dotnet`（未安装/版本不符）；`tools/dotnet.cmd` 会设置 `DOTNET_ROOT`、
  `NUGET_PACKAGES`。首次需 `tools/bootstrap.ps1`（本机已完成）。
- 构建输出：App 在 `src/StartupManager.App/bin/x64/<Config>/.../win-x64/StartupManager.exe`。
- Git Bash 里路径含中文，命令用引号；PowerShell 内联命令易被 bash 吞掉 `$_`/`$env`，
  复杂脚本请写成 `artifacts/*.ps1` 再 `-File` 执行（`artifacts/` 已 gitignore）。

## 架构与关键文件

```
src/StartupManager.Core/
  Services/StartupManagerService.{Scan,Changes,Protection}.cs  扫描/事务/保护（partial class）
  Providers/{RegistryStartupProvider,StartupFolderProvider,
             ScheduledTaskProvider,ServiceStartupProvider,
             PackagedAppStartupProvider}.cs                    五类来源的扫描与写入
  Providers/RegistryFolder/StartupApproved.cs                  StartupApproved 二进制读写
  Providers/ServiceNativeMethods.cs                            SCM P/Invoke
  Protection/StartupProtectionPolicy.cs                        默认锁定规则（可逐项解锁）
  Protection/PublisherLookup.cs                                文件版本发布者（带缓存）
  Backup/JsonBackupStore.cs, Backup/AtomicFile.cs               备份存储
  Elevation/{ElevationCoordinator,ElevationHost,Protocol}.cs   一次性 UAC 助手 IPC
  Interfaces/IStartupManager.cs                               宿主/助手唯一门面
src/StartupManager.App/Host/BridgeHost.cs                      WebView2 桥 + 命令分发
src/StartupManager.App/Host/IconService.cs                     Shell 图标提取（保留 alpha）
src/StartupManager.Elevated/Program.cs                         UAC 助手入口
web/{index.html,app.js,bridge.js,demo-data.js,styles.css}      生产 UI（无框架，原生 JS）
docs/CONTRACT.md                                               桥接协议/接口/不变量
docs/SAFETY.md                                                 安全模型与保护规则
docs/TESTING.md                                                测试与验收记录
```

数据目录：`%LocalAppData%\StartupManager\`（settings.json、Backups、Logs、Ipc、WebView2）。

## 变更流程（必须保持的不变量）

1. `SetEnabledAsync` 逐项执行：`CaptureStateAsync` → **先写备份** → `ApplyEnabledAsync` →
   `VerifyEnabledAsync` → 失败则 `RestoreStateAsync` 回滚并把备份标记 Failed。
2. 任何写入前都要由 provider 重新读取实时系统状态；不要因为 UI 快照而跳过。
3. 受保护项在 service 层再校验一次（不能只靠 UI）；Windows 策略锁定/安全检查失败不可覆盖。
4. 需要管理员时整批只弹一次 UAC；取消不得产生任何更改。提权助手会独立重新扫描和校验。
5. 修改成功后主机必须发送完整 `state`（快照 + 备份）再返回 `commandResult`。

## v1.3.0 性能设计（改代码前务必理解）

- **开关不做全量扫描**。`StartupManagerService` 缓存最近扫描（`CachedSnapshot`），
  `GetOutcomeForMutationAsync` 在 2 分钟内复用；写入验证后直接改 `Enabled`/`ManagedByApp`
  并重读活跃备份。`BridgeHost.ReloadAfterMutationAsync` 发布缓存快照。
- 缓存过期/不存在时，按 item id 前缀只扫描目标来源（`StartupItemIds.TryGetSource`：
  reg-/dir-/tsk-/svc-/pkg-）。提权助手同样走这条路。
- 提权完成后主机用 `RefreshSourcesForItemsAsync` 只刷新受影响来源。
- 只有“从备份重新启用会生成新实时条目”等结构性变化才触发全量扫描。
- `ScanCoreAsync` 并行运行五个 provider，结果按 provider 顺序合并；`onlySources` 用于局部扫描
  （这种部分结果不能存成共享快照）。
- 性能坑：MSIX 的 `Package.InstalledLocation` 每包约 15ms，必须用 `InstalledPath`；
  清单先 `ReadAllText` + 子串 `startupTask` 过滤再解析；服务扫描只为自动启动项读
  `SERVICE_CONFIG_DELAYED_AUTO_START_INFO`；`PublisherLookup` 有 5 分钟进程内缓存。
- 当前全量扫描约 2.3-3.2s，计划任务 COM 是唯一慢来源；开关实测 230-330ms。

## 受保护项与批量管理

- 默认锁定系统/驱动/安全/Microsoft/未验证项；工具锁可用 `userAllowedItemIds` 逐项解锁，
  Windows 策略锁定（如 MSIX state 3/4）永远不可覆盖。
- 后端：`SetUserOverrideAsync`（单个，内部走批量）/ `SetUserOverridesAsync`（批量，一次扫描
  + 一次设置写入，逐项结果）。桥接命令：`setUserOverride` / `setUserOverrides`。
- UI：详情页单项“允许管理/恢复工具保护”；工具栏“允许管理…/恢复保护…”作用于当前分类+
  搜索/筛选结果。改动 UI 时同步更新 `web/demo-data.js` 的演示实现。

## 测试与验收

- 基线：**230 项通过、1 项跳过**（224 Core + 6 App）。跳过项需要创建真实计划任务（非管理员）。
- 单元/集成测试用 `tests/StartupManager.Core.Tests/Fakes/`（`FakeStartupSystem`、
  `InMemoryBackupStore`、`TestPaths`）；不要碰真实系统服务。
- 演示 UI（无 WebView2）：
  ```bash
  # 启动 headless Edge（CDP 9234）打开 web/index.html，等 5-6 秒后运行：
  "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --headless=new \
    --remote-debugging-port=9234 --user-data-dir="D:/Zcode/projects/自启动管理/artifacts/edge-qa" \
    --window-size=1350,900 "file:///D:/Zcode/projects/自启动管理/web/index.html" &
  node tools/qa-protection.mjs        # 37 项检查（含批量解锁/恢复）
  ```
  结束测试后只关闭带 `edge-qa` 用户数据目录的 Edge 进程（按命令行匹配），不要 `taskkill` 所有 msedge。
- 原生宿主 UI：设置 `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=9233'`
  启动 App，再 `node tools/qa-native-toggle.mjs`（自建临时 HKCU 启动项、计时、自动清理）。
- 测试临时启动项命名用 `StartupManagerE2E` / `StartupManagerTest_<guid>`；结束必须删除
  Run 值、`StartupApproved\Run` 值，以及 `%LocalAppData%\StartupManager\Backups` 中匹配的备份。
- UAC 安全桌面无法自动化：真实提权切换仍需人工验收一次（挑安全的第三方项）。

## 约定与注意事项

- 用户可见文案、对话框、日志、文档都用中文；代码注释沿用现有英文风格。
- 版本号出现在：`Directory.Build.props`、`web/index.html` 侧栏、`web/app.js` 设置页关于、
  `tools/qa-protection.mjs` 的断言；发版时一起改，并新增 `docs/releases/vX.Y.Z.md`。
- `web/app.js` 是普通 IIFE，无框架/构建步骤；保持 ES5 风格函数写法。
- `StartupItem.Metadata` 绝不发往 Web UI（`[JsonIgnore]`）；`StateJson` 也不得进桥接消息。
- UI 不得乐观显示最终状态：先 pending，收到 `state` + `commandResult` 后再落定。
- 备份创建先于系统写入，这是安全不变量，不要为了“快”调换顺序。
- Git：默认不主动提交，除非用户要求；工作区行尾警告（LF→CRLF）属正常现象。
- 已知未做：RunOnce、自动更新、深色主题、Snap Layouts 悬停面板、Authenticode 信任链。

## 深度文档

- 协议/接口/消息格式：`docs/CONTRACT.md`
- 安全模型：`docs/SAFETY.md`
- 测试与验收记录：`docs/TESTING.md`
- 交接摘要：`docs/HANDOFF.md`
- 版本说明：`docs/releases/`
