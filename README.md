# 启动项管理器 v1.0.0

一个面向 Windows 11 的启动项管理工具。UI 采用已确认的 Fluent/WinUI 风格，
由原生 WPF 窗口承载 WebView2 界面；系统操作由 C# 后端完成，权限不足时按需
通过一次性 UAC 助手执行。

## 立即运行

- 便携版程序：`artifacts/publish/StartupManager/StartupManager.exe`
- 压缩包：`artifacts/publish/StartupManager.zip`
- 环境：Windows 11 x64；系统需具备 WebView2 Evergreen Runtime（Windows 11 通常已内置）。
- 发布目录是 self-contained，不要求另行安装 .NET。
- 当前构建未签名；如果 SmartScreen 提示，请确认来源后由你决定是否运行。

## 功能

- 真实扫描当前系统的启动配置：
  - HKCU / HKLM **Run**（含 32/64 位视图）
  - 用户 / 公共**启动文件夹**
  - 登录/开机触发的**计划任务**
  - 自动/延迟自动启动的 **Windows 服务**
- 启动页按 **启动项 / 服务项 / 计划任务** 三个分类展示，每个分类带数量；
  状态筛选、统计和“全部禁用”均作用于当前分类。
- 设置中可将界面字体/整体显示大小调整为 90% / 100% / 110% / 125%，
  即时生效并持久化，重启后保持。
- 真实改变系统状态：
  - 注册表与启动文件夹：写入 Windows **StartupApproved**（任务管理器使用同一机制），不删除原启动项。
  - 计划任务：设置任务计划程序的原生 **Enabled** 状态。
  - 服务：修改启动类型（自动 → 手动），不强制停止当前服务。
- MSIX 应用包服务（`SERVICE_PACKAGED_PROCESS`，例如 ChatGPT 的
  `CodexSandboxService.OpenAI.Codex`）：Windows 的 SCM 接口拒绝管理员修改。
  应用会在详情中明确标注“应用包”，并在取得用户允许后，通过管理员权限备份并
  写入服务注册表启动值（Start=手动），重启后生效；应用更新可能重置该值。
- 每次修改前先备份，修改后重新读取系统状态验证；验证失败会尝试回滚。
- 恢复中心可还原原始配置；批量禁用会跳过受保护项并显示真实计数。
- 系统保护：驱动、微软签名的自动服务、Windows 关键服务、`\Microsoft\Windows\*`
  系统任务、Windows 安全类启动项默认锁定，不能误操作。
- 仅因**发布者或程序路径无法确认**而锁定的服务会标记为“未验证”，可在详情中
  通过明确确认“允许管理”解除保护；该决定会持久化。真正的系统/驱动/安全/微软项
  没有这个入口，不能覆盖。允许后实际修改服务仍需 UAC，并照常备份和验证。
- 按需 UAC：普通注册表/启动文件夹切换不需要管理员；服务或受 ACL 限制的任务
  才启动一次提权助手。取消 UAC 不会产生任何更改。
- 搜索、按状态筛选、按来源/名称/范围排序、详情侧栏、恢复中心、设置持久化、
  单实例、系统托盘、可选开机自启。

## 已验证范围

- 后端单元/集成测试：**195 项通过，1 项安全跳过**。
  - 跳过项是需要创建真实计划任务的测试；当前会话非管理员，Task Scheduler 拒绝创建任务。
- Web UI 自动化：本次分类/字体改动新增 **26 项真实浏览器检查通过**
  （三个分类的计数、列表来源、状态范围，字体 100%→125% 的视口缩放），
  以及重启后字体保持 125%、恢复 100% 的持久化检查。
  上一版的 108 项交互检查覆盖了搜索、筛选、详情、恢复中心、设置与批量确认。
- **真实端到端**（对当前系统执行，使用一次性测试启动项）：
  - UI 开关 → 桥接 → 事务引擎 → StartupApproved 写入 0x03 → 备份 → 重扫 → 恢复。
  - 发布版应用同样通过；恢复中心从真实备份恢复成功。
  - 应用自启动设置真实写入/删除 HKCU Run 值；关闭服务扫描后真实条目数下降。
  - 测试数据已清理。
- WPF 原生宿主：自绘标题栏、拖动/缩放、最大化/还原、关闭到托盘、单实例激活、
  200% DPI、WebView2 导航和发布包启动均通过。
- 发布包中的一次性提权助手经过非提权直跑验证：请求路径/owner 校验、
  CoreComposition、StartupApproved 写入、备份、响应协议均通过。
- 使用 Windows computer use 对上一版原生界面做了验收：搜索、服务详情、
  一次真实启动项的关闭/恢复、忙碌提示、备份计数和恢复后的状态均通过。
- 新增分类与字体设置经过最终发布版真实验证：三个分类的计数与列表来源正确；
  字体 100%→125% 时 WebView CSS 视口从 1350 缩到 1080，设置写入
  `settings.json`，重启后仍为 125%，恢复 100% 正常。
- 使用截图中的真实 **ChatGPT（CodexSandboxService.OpenAI.Codex）** 服务验证未验证项
  流程：默认锁定，显示“未验证”和“允许管理”；确认后开关解锁、白名单写入设置；
  仍提示服务修改需要 UAC；恢复系统保护后重新锁定并从白名单移除。该流程不修改服务启动状态。
- UAC 提示位于 Windows 安全桌面，computer use 无法捕获或操作它；测试提示已
  安全取消、未执行任何提权操作。因此真实的服务/任务提权切换仍需你手工点击一次。

## 仍需人工验收

以下路径在当前会话中**没有对真实系统对象执行**，因为不能自动点击 UAC，
且普通权限无法创建测试计划任务：

- 真实服务的启动类型切换（需要 UAC）。
- 真实计划任务的 Enabled 切换（当前非管理员环境无法创建测试任务）。

代码路径已实现并有单元/模拟测试，但请你在首次使用时选一个**安全的第三方项**
实测一次：

1. 在启动项列表找到一个不关键的服务（例如你明确知道可停用的第三方服务），
   或创建一个一次性登录任务。
2. 关闭自启动；在 UAC 提示中确认。
3. 检查服务启动类型是否变为“手动”（或任务是否变为“已禁用”），然后在恢复中心还原。
4. 取消一次 UAC，确认界面提示取消且系统状态没有变化。

不确定某个服务是否安全时，不要操作它。系统保护项不会提供可点击开关。

## 已知限制

- 注册表/启动文件夹使用 StartupApproved 后，机器级（HKLM/公共文件夹）项的禁用
  只对当前登录用户生效；不会影响其他 Windows 用户。
- 第一版未纳入 UWP/商店应用启动任务和 RunOnce 项。
- 列表使用程序名称生成的字母色块；尚未从可执行文件提取真实图标。
- 未实现自动更新、深色主题、Windows 11 Snap Layouts 的鼠标悬停面板；
  窗口拖动到边缘的 Snap 和双击最大化可用。
- 服务与服务签名判断基于文件版本信息，不是 Authenticode 信任链。
- 对任务/服务的保护策略刻意偏保守，可能锁定一些你其实可以管理的第三方服务。
- MSIX 应用包服务的启动值兜底是注册表级兼容方式：需要 UAC、重启后才完全生效，
  Windows 服务管理器可能暂时仍显示旧值，应用更新或其他工具也可能重置它。
- ChatGPT 等 MSIX 应用还可能声明单独的 UWP StartupTask；v1 未管理 UWP 启动任务，
  如果服务关闭后应用仍自启，请在 Windows 设置 → 应用 → 启动中关闭对应应用。

## 数据位置

- 设置、备份、日志：`%LocalAppData%\StartupManager\`
- 备份文件：`%LocalAppData%\StartupManager\Backups\`
- WebView2 用户数据：`%LocalAppData%\StartupManager\WebView2\`
- 所有修改均有备份记录；活动备份不会被保留期清理删除。

## 从源码构建

仓库使用项目内的用户级 .NET 8 SDK，无需管理员权限，也不修改系统 PATH：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\bootstrap.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Test
powershell -NoProfile -ExecutionPolicy Bypass -File tools\publish.ps1 -Configuration Release -Zip
```

## 目录

```
src/StartupManager.Core/       扫描、备份、事务、保护、提权协议
src/StartupManager.App/        WPF + WebView2 宿主、桥接、托盘
src/StartupManager.Elevated/   一次性 UAC 助手
web/                           生产版界面
docs/                          契约、安全、测试、并行开发说明
tools/                         bootstrap/build/publish/QA 脚本
artifacts/                     发布产物
prototype.html                 已确认的 UI 原型（设计参考）
```

## 安全说明

启动项管理器只读取启动配置，不执行扫描到的命令。所有修改先备份、后验证；
失败会尝试回滚。系统关键服务、驱动与服务型安全组件默认锁定。详细规则见
`docs/SAFETY.md`，测试范围见 `docs/TESTING.md`。
