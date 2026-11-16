using System.Security;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Providers.RegistryFolder;

namespace StartupManager.Core.Providers;

/// <summary>
/// Per-user MSIX windows.startupTask declarations. These tasks are separate
/// from packaged services: disabling an app's service does not disable its
/// logon task. Windows stores the state used by Task Manager in SystemAppData.
/// </summary>
public sealed class PackagedAppStartupProvider : IStartupProvider, IStartupChangeProvider
{
    public const string ProviderStateKind = "packagedStartupTask";
    private const string StateRoot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    private const string Mechanism = "packagedStartupTask";
    private readonly string _stateRoot;
    private readonly Func<IReadOnlyList<PackageIdentity>> _readPackages;

    public PackagedAppStartupProvider() : this(StateRoot, ReadCurrentUserPackages) { }

    internal PackagedAppStartupProvider(string stateRoot, Func<IReadOnlyList<PackageIdentity>> readPackages)
    {
        _stateRoot = stateRoot;
        _readPackages = readPackages;
    }

    public StartupSource Source => StartupSource.PackagedApp;

    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<StartupItem>>(() => Scan(cancellationToken), cancellationToken);

    public Task<StateSnapshot> CaptureStateAsync(StartupItem item, CancellationToken cancellationToken) =>
        Task.Run(() => CaptureState(item, cancellationToken), cancellationToken);

    public Task ApplyEnabledAsync(StartupItem item, bool enabled, bool isElevated, CancellationToken cancellationToken) =>
        Task.Run(() => ApplyEnabled(item, enabled, cancellationToken), cancellationToken);

    public Task<bool> VerifyEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken) =>
        Task.Run(() => VerifyEnabled(item, enabled, cancellationToken), cancellationToken);

    public Task RestoreStateAsync(BackupRecord backup, bool isElevated, CancellationToken cancellationToken) =>
        Task.Run(() => RestoreState(backup, cancellationToken), cancellationToken);

    private IReadOnlyList<StartupItem> Scan(CancellationToken cancellationToken)
    {
        var items = new List<StartupItem>();
        foreach (var task in EnumerateTasks(cancellationToken))
        {
            var state = ReadState(task) ?? task.DefaultState;
            var command = task.Executable.Length == 0
                ? task.PackageFamilyName
                : task.Executable;
            items.Add(new StartupItem
            {
                Id = task.Id,
                Name = task.DisplayName,
                Publisher = task.Publisher,
                Source = StartupSource.PackagedApp,
                Scope = StartupScope.CurrentUser,
                Enabled = state == 2 || state == 4,
                RequiresAdmin = false,
                Protected = state is 3 or 4,
                ProtectionReason = state is 3 or 4 ? "此启动任务由系统策略控制，无法在此修改。" : null,
                Location = @"HKCU\" + GetTaskKeyPath(task),
                SubLocation = $"任务 ID：{task.TaskId} · 应用包：{task.PackageFamilyName}",
                Command = command,
                TargetPath = task.TargetPath,
                Mechanism = Mechanism,
                Metadata = CreateMetadata(task)
            });
        }

        return items;
    }

    private IReadOnlyList<PackagedStartupTask> EnumerateTasks(CancellationToken cancellationToken)
    {
        var tasks = new Dictionary<string, PackagedStartupTask>(StringComparer.OrdinalIgnoreCase);
        var packages = _readPackages();
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.InstallLocation.Length == 0)
            {
                continue;
            }

            var manifestPath = Path.Combine(package.InstallLocation, "AppxManifest.xml");
            XDocument manifest;
            try
            {
                manifest = XDocument.Load(manifestPath, LoadOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }

            var publisher = ReadManifestPublisher(manifest) ?? package.PublisherDisplayName;
            foreach (var element in manifest.Descendants().Where(
                         node => node.Name.LocalName == "StartupTask" &&
                                 node.Parent?.Name.LocalName == "Extension" &&
                                 string.Equals(
                                     (string?)node.Parent.Attribute("Category"),
                                     "windows.startupTask",
                                     StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var taskId = ((string?)element.Attribute("TaskId") ?? string.Empty).Trim();
                if (!IsValidTaskId(taskId))
                {
                    continue;
                }

                var appId = FindAppUserModelId(element, package.FamilyName);
                var executable = ((string?)element.Parent?.Attribute("Executable") ??
                    (string?)element.Ancestors().FirstOrDefault(node => node.Name.LocalName == "Application")?.Attribute("Executable") ?? string.Empty).Trim();
                var id = CreateId(package.FamilyName, taskId);
                var task = new PackagedStartupTask(
                    id,
                    package.FamilyName,
                    appId,
                    taskId,
                    ResolveDisplayName(element, package),
                    publisher,
                    executable,
                    ResolveTarget(package.InstallLocation, executable),
                    string.Equals((string?)element.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase) ? 2 : 0);
                var state = ReadState(task) ?? task.DefaultState;
                if (state is < 0 or > 4)
                {
                    continue;
                }
                tasks[id] = task;
            }
        }

        return tasks.Values.OrderBy(task => task.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private StateSnapshot CaptureState(StartupItem item, CancellationToken cancellationToken)
    {
        var task = ResolveTask(item);
        cancellationToken.ThrowIfCancellationRequested();
        var live = EnumerateTasks(cancellationToken).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, task.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("该应用包启动任务已不存在，请刷新后重试。");
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(GetTaskKeyPath(task), writable: false);
        var state = ReadDword(key, "State");
        var userEnabled = ReadDword(key, "UserEnabledStartupOnce");
        var snapshot = new PackagedStartupTaskState
        {
            PackageFamilyName = task.PackageFamilyName,
            AppId = task.AppId,
            TaskId = task.TaskId,
            KeyExisted = key is not null,
            State = state,
            UserEnabledStartupOnce = userEnabled
        };
        return new StateSnapshot
        {
            Kind = ProviderStateKind,
            Json = JsonSerializer.Serialize(snapshot, JsonDefaults.Options),
            Summary = $"应用包启动任务：{task.TaskId}；原本已{((state ?? live.DefaultState) is 2 or 4 ? "启用" : "禁用")}"
        };
    }

    private void ApplyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        var task = ResolveTask(item);
        cancellationToken.ThrowIfCancellationRequested();
        var live = EnumerateTasks(cancellationToken).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, task.Id, StringComparison.OrdinalIgnoreCase));
        if (live is null)
        {
            throw new InvalidOperationException("该应用包启动任务已不存在，请刷新后重试。");
        }

        var state = ReadState(live) ?? live.DefaultState;
        if (state is 3 or 4)
            throw new InvalidOperationException("此应用包启动任务由系统策略控制，未做任何更改。");

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.CreateSubKey(GetTaskKeyPath(task), writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("找不到应用包启动任务的 Windows 状态，未做任何更改。");
        }

        try
        {
            key.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
            if (enabled)
            {
                key.DeleteValue("UserEnabledStartupOnce", throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException("无法更改应用包的 Windows 登录启动状态。", ex);
        }
    }

    private bool VerifyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        var task = ResolveTask(item);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadState(task) == (enabled ? 2 : 1);
    }

    private void RestoreState(BackupRecord backup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(backup.StateKind, ProviderStateKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("备份状态类型不是应用包启动任务，无法恢复。");
        }

        var state = JsonSerializer.Deserialize<PackagedStartupTaskState>(backup.StateJson, JsonDefaults.Options)
            ?? throw new InvalidOperationException("应用包启动任务备份为空，无法恢复。");
        var task = new PackagedStartupTask(
            CreateId(state.PackageFamilyName, state.TaskId),
            state.PackageFamilyName,
            state.AppId,
            state.TaskId,
            string.Empty, string.Empty, string.Empty, null);
        if (!IsValidFamilyName(task.PackageFamilyName) || !IsValidTaskId(task.TaskId) ||
            (!string.IsNullOrEmpty(backup.ItemId) && !string.Equals(task.Id, backup.ItemId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("应用包启动任务备份的定位信息无效，无法恢复。");
        }

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = state.KeyExisted
            ? hive.CreateSubKey(GetTaskKeyPath(task), writable: true)
            : hive.OpenSubKey(GetTaskKeyPath(task), writable: true);
        if (key is null)
        {
            return;
        }

        if (state.State.HasValue)
        {
            key.SetValue("State", state.State.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("State", throwOnMissingValue: false);
        }

        if (state.UserEnabledStartupOnce.HasValue)
        {
            key.SetValue("UserEnabledStartupOnce", state.UserEnabledStartupOnce.Value, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("UserEnabledStartupOnce", throwOnMissingValue: false);
        }

        if (ReadDword(key, "State") != state.State ||
            ReadDword(key, "UserEnabledStartupOnce") != state.UserEnabledStartupOnce)
            throw new InvalidOperationException("应用包启动任务恢复后与备份不一致。");
    }

    private Dictionary<string, string> CreateMetadata(PackagedStartupTask task) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PackageFamilyName"] = task.PackageFamilyName,
            ["AppId"] = task.AppId,
            ["TaskId"] = task.TaskId,
            ["StateKeyPath"] = GetTaskKeyPath(task)
        };

    private static PackagedStartupTask ResolveTask(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Source != StartupSource.PackagedApp ||
            !item.Metadata.TryGetValue("PackageFamilyName", out var family) ||
            !item.Metadata.TryGetValue("AppId", out var appId) ||
            !item.Metadata.TryGetValue("TaskId", out var taskId) ||
            !IsValidFamilyName(family) || !IsValidTaskId(taskId))
        {
            throw new InvalidOperationException("应用包启动项缺少有效的定位信息，请刷新后重试。");
        }

        var id = CreateId(family, taskId);
        if (!string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("应用包启动项标识与定位信息不一致，请刷新后重试。");
        }

        return new PackagedStartupTask(id, family, appId, taskId, item.Name, item.Publisher, string.Empty, null);
    }

    internal static string CreateId(string packageFamilyName, string taskId) =>
        StartupItemIds.Compute("pkg-", $"{packageFamilyName.ToLowerInvariant()}|{taskId.ToLowerInvariant()}");

    private string GetTaskKeyPath(PackagedStartupTask task) => $@"{_stateRoot}\{task.PackageFamilyName}\{task.TaskId}";

    private int? ReadState(PackagedStartupTask task)
    {
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(GetTaskKeyPath(task), writable: false);
        return ReadDword(key, "State");
    }

    private static int? ReadDword(RegistryKey? key, string name)
    {
        try
        {
            var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return raw switch
            {
                null => null,
                int value => value,
                _ => throw new InvalidOperationException($"应用包启动状态 {name} 不是 DWORD，未做任何更改。")
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException($"无法读取应用包启动状态 {name}。", ex);
        }
    }

    private static IReadOnlyList<PackageIdentity> ReadCurrentUserPackages()
    {
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            var packages = new List<PackageIdentity>();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (!IsValidFamilyName(package.Id.FamilyName))
                {
                    continue;
                }

                packages.Add(new PackageIdentity(
                    package.Id.FamilyName,
                    package.Id.Name,
                    package.InstalledLocation.Path,
                    package.DisplayName,
                    package.PublisherDisplayName));
            }

            return packages;
        }
        catch (Exception ex) when (ex is TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            throw new InvalidOperationException("无法枚举当前用户的应用包，应用包启动项扫描未完成。", ex);
        }
    }

    internal sealed record PackageIdentity(
        string FamilyName,
        string Name,
        string InstallLocation,
        string DisplayName,
        string PublisherDisplayName);

    private static string? ReadManifestPublisher(XDocument manifest)
    {
        var publisher = ((string?)manifest.Root?.Element(manifest.Root.Name.Namespace + "Identity")?.Attribute("Publisher"))?.Trim();
        if (publisher is null || publisher.Length == 0)
        {
            return null;
        }

        return string.Join(", ", publisher.Split(',')
            .Select(part => part.Trim())
            .Where(part => part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(part => part[3..]));
    }

    private static string ResolveDisplayName(XElement element, PackageIdentity package)
    {
        var display = ((string?)element.Attribute("DisplayName") ?? string.Empty).Trim();
        if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
        {
            display = package.DisplayName;
        }

        return display.Length > 0 ? display : package.DisplayName.Length > 0 ? package.DisplayName : package.Name;
    }

    private static string FindAppUserModelId(XElement element, string familyName)
    {
        var application = element.Ancestors().FirstOrDefault(node => node.Name.LocalName == "Application");
        var appId = ((string?)application?.Attribute("Id") ?? "App").Trim();
        return familyName + "!" + appId;
    }

    private static string? ResolveTarget(string installLocation, string executable)
    {
        if (executable.Length == 0 || installLocation.Length == 0 ||
            Path.IsPathRooted(executable) || executable.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(installLocation, executable.Replace('/', '\\')));
        return path.StartsWith(installLocation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(path)
            ? path
            : null;
    }

    private static bool IsValidFamilyName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsValidTaskId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');

    private sealed record PackagedStartupTask(
        string Id,
        string PackageFamilyName,
        string AppId,
        string TaskId,
        string DisplayName,
        string Publisher,
        string Executable,
        string? TargetPath,
        int DefaultState = 0);

    private sealed class PackagedStartupTaskState
    {
        public string PackageFamilyName { get; set; } = string.Empty;
        public string AppId { get; set; } = string.Empty;
        public string TaskId { get; set; } = string.Empty;
        public bool KeyExisted { get; set; }
        public int? State { get; set; }
        public int? UserEnabledStartupOnce { get; set; }
    }
}
