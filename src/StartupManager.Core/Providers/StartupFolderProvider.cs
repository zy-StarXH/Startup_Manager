using System.Security;
using System.Text.Json;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Providers.RegistryFolder;

namespace StartupManager.Core.Providers;

/// <summary>
/// Enumerates the per-user and common Startup folders and toggles entries with
/// moving files into recoverable storage, alongside StartupApproved state.
/// Presence in the Startup directory is considered an active launch entry.
/// </summary>
public sealed class StartupFolderProvider : IStartupProvider, IStartupChangeProvider
{
    public const string ProviderStateKind = "folderApproval";

    private const string UserFolderDescription = "当前用户启动文件夹";
    private const string CommonFolderDescription = "公共启动文件夹";

    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".exe", ".bat", ".cmd" };

    private readonly string _userStartupFolder;
    private readonly string _commonStartupFolder;
    private readonly string _quarantineDirectory;

    /// <summary>
    /// Resolves the real per-user and common Startup folders. This is a true
    /// parameterless constructor so reflection/DI activation works.
    /// </summary>
    public StartupFolderProvider()
        : this(userStartupFolder: null, commonStartupFolder: null, quarantineDirectory: null)
    {
    }

    /// <summary>
    /// Folder paths are injectable for tests; null values fall back to the real
    /// Environment.SpecialFolder locations.
    /// </summary>
    public StartupFolderProvider(
        string? userStartupFolder,
        string? commonStartupFolder,
        string? quarantineDirectory = null)
    {
        _quarantineDirectory = NormalizeFolder(quarantineDirectory, StartupFolderQuarantine.DefaultDirectory);
        _userStartupFolder = NormalizeFolder(
            userStartupFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        _commonStartupFolder = NormalizeFolder(
            commonStartupFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
    }

    public StartupSource Source => StartupSource.Folder;

    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<StartupItem>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var approvals = StartupApproved.ReadApprovals(StartupApprovalFamily.StartupFolder);
        var publisherCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ScanFolder(
            _userStartupFolder,
            StartupScope.CurrentUser,
            UserFolderDescription,
            approvals,
            publisherCache,
            items,
            seenIds,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        ScanFolder(
            _commonStartupFolder,
            StartupScope.AllUsers,
            CommonFolderDescription,
            approvals,
            publisherCache,
            items,
            seenIds,
            cancellationToken);

        if (Directory.Exists(_quarantineDirectory))
        {
            foreach (var file in Directory.GetFiles(_quarantineDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                if (!SupportedExtensions.Contains(Path.GetExtension(fileName)))
                {
                    continue;
                }

                var separator = fileName.IndexOf('-');
                if (separator < 0 || !fileName.StartsWith("dir-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var itemId = fileName[..Math.Min(fileName.Length, 68)];
                if (itemId.Length != 68)
                {
                    continue;
                }

                var original = StartupFolderQuarantine.FindOriginalPath(_quarantineDirectory, fileName);
                if (original is null)
                {
                    continue;
                }

                var scope = GetScopeForPath(original);
                if (scope is null || !string.Equals(itemId,
                        StartupItemIds.CreateFolderId(scope.Value, original), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var item = StartupFolderQuarantine.CreateDisabledItem(_quarantineDirectory, itemId, original, scope.Value);
                if (item is not null && seenIds.Add(item.Id))
                {
                    items.Add(item);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StartupItem>>(items);
    }

    public Task<StateSnapshot> CaptureStateAsync(
        StartupItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(item);
        var wasQuarantined = StartupFolderQuarantine.IsQuarantined(_quarantineDirectory, item.Id, target.FullPath);
        if (wasQuarantined)
        {
            var quarantinedState = StartupFolderQuarantine.Capture(_quarantineDirectory, item.Id, target.FullPath, target.Scope);
            return Task.FromResult(new StateSnapshot
            {
                Kind = ProviderStateKind,
                Json = JsonSerializer.Serialize(quarantinedState, JsonDefaults.Options),
                Summary = "启动文件夹快捷方式已移出 Windows 启动目录"
            });
        }

        if (!File.Exists(target.FullPath))
        {
            throw new InvalidOperationException(
                $"启动文件夹条目“{target.ValueName}”已不存在，未做任何更改。");
        }

        var command = BuildCommand(target.FullPath, Path.GetExtension(target.FullPath)).Command;

        var approvalExists = false;
        byte[]? approval = null;
        try
        {
            StartupApproved.TryReadApproval(
                StartupApprovalFamily.StartupFolder,
                target.ValueName,
                out approval,
                out approvalExists);
        }
        catch (Exception ex) when (IsAccessException(ex))
        {
            throw new InvalidOperationException(
                $"无法读取启动文件夹条目“{target.ValueName}”的 Windows 启动批准状态，未做任何更改。",
                ex);
        }

        if (approvalExists && approval is null)
        {
            throw new InvalidOperationException(
                $"启动文件夹条目“{target.ValueName}”的 Windows 启动批准状态格式异常，未做任何更改。");
        }

        var wasEnabled = StartupApproved.IsEnabled(approval);
        var state = new FolderApprovalState
        {
            Scope = target.Scope.ToString(),
            FullPath = target.FullPath,
            ValueName = target.ValueName,
            ApprovalFamily = StartupApprovalFamily.StartupFolder.ToString(),
            ApprovalValueExisted = approvalExists,
            ApprovalRawBase64 = approval is null ? null : Convert.ToBase64String(approval),
            Command = command,
            ManagesFile = true,
            QuarantineDirectory = _quarantineDirectory
        };

        return Task.FromResult(new StateSnapshot
        {
            Kind = ProviderStateKind,
            Json = JsonSerializer.Serialize(state, JsonDefaults.Options),
            Summary = $"启动文件夹：{target.ValueName}；启动项原本已{(wasEnabled ? "启用" : "禁用")}"
        });
    }

    public Task ApplyEnabledAsync(
        StartupItem item,
        bool enabled,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(item);
        if (enabled)
        {
            if (!File.Exists(target.FullPath) &&
                !StartupFolderQuarantine.IsQuarantined(_quarantineDirectory, item.Id, target.FullPath))
            {
                throw new InvalidOperationException("找不到已关闭的启动文件，无法启用。");
            }
        }
        else if (!File.Exists(target.FullPath))
        {
            throw new InvalidOperationException(
                $"启动文件夹条目“{target.ValueName}”已不存在，未做任何更改。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = enabled
            ? StartupApproved.CreateEnabledBytes(DateTime.UtcNow)
            : StartupApproved.CreateDisabledBytes(DateTime.UtcNow);

        try
        {
            if (enabled)
            {
                StartupFolderQuarantine.Restore(_quarantineDirectory, item.Id, target.FullPath);
            }
            else
            {
                StartupFolderQuarantine.Disable(_quarantineDirectory, item.Id, target.FullPath);
            }

            StartupApproved.WriteApproval(
                StartupApprovalFamily.StartupFolder,
                target.ValueName,
                bytes);
        }
        catch (Exception ex) when (IsAccessException(ex) || ex is InvalidOperationException)
        {
            if (!enabled &&
                !File.Exists(target.FullPath) &&
                StartupFolderQuarantine.IsQuarantined(_quarantineDirectory, item.Id, target.FullPath))
            {
                try
                {
                    StartupFolderQuarantine.Restore(_quarantineDirectory, item.Id, target.FullPath);
                }
                catch
                {
                    // The transaction service attempts another restore from backup.
                }
            }

            throw new InvalidOperationException(
                $"无法{(enabled ? "启用" : "禁用")}启动文件夹条目“{target.ValueName}”，启动文件或 Windows 启动状态写入失败。",
                ex);
        }

        return Task.CompletedTask;
    }

    public Task<bool> VerifyEnabledAsync(
        StartupItem item,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var target = ResolveTarget(item);
        if (enabled)
        {
            if (!File.Exists(target.FullPath))
            {
                return Task.FromResult(false);
            }
        }
        else if (!StartupFolderQuarantine.IsQuarantined(_quarantineDirectory, item.Id, target.FullPath) ||
                 File.Exists(target.FullPath))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var approvalExists = false;
        byte[]? approval = null;
        try
        {
            StartupApproved.TryReadApproval(
                StartupApprovalFamily.StartupFolder,
                target.ValueName,
                out approval,
                out approvalExists);
        }
        catch (Exception ex) when (IsAccessException(ex))
        {
            throw new InvalidOperationException(
                $"无法验证启动文件夹条目“{target.ValueName}”的 Windows 启动批准状态。",
                ex);
        }

        if (approvalExists && approval is null)
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StartupApproved.IsEnabled(approval) == enabled);
    }

    public Task RestoreStateAsync(
        BackupRecord backup,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(backup.StateKind, ProviderStateKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"备份状态类型“{backup.StateKind}”无法由启动文件夹条目恢复。");
        }

        FolderApprovalState? state;
        try
        {
            state = JsonSerializer.Deserialize<FolderApprovalState>(
                backup.StateJson,
                JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("启动文件夹备份状态无法解析，未做任何更改。", ex);
        }

        if (state is null || string.IsNullOrWhiteSpace(state.ValueName) ||
            string.IsNullOrWhiteSpace(state.FullPath))
        {
            throw new InvalidOperationException("启动文件夹备份状态缺少值名称，未做任何更改。");
        }

        if (!string.IsNullOrWhiteSpace(state.ApprovalFamily) &&
            !string.Equals(
                state.ApprovalFamily,
                StartupApprovalFamily.StartupFolder.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动文件夹备份状态中的启动批准映射不一致，未做任何更改。");
        }

        if (!string.IsNullOrWhiteSpace(state.FullPath))
        {
            var expectedValueName = Path.GetFileName(state.FullPath);
            if (!string.Equals(expectedValueName, state.ValueName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("启动文件夹备份状态中的文件路径与值名称不一致，未做任何更改。");
            }
        }

        var actualScope = GetScopeForPath(state.FullPath);
        if (!Enum.TryParse<StartupScope>(state.Scope, ignoreCase: true, out var stateScope) ||
            actualScope != stateScope)
        {
            throw new InvalidOperationException("启动文件夹备份状态中的范围无效，未做任何更改。");
        }

        var restoreId = StartupItemIds.CreateFolderId(stateScope, state.FullPath);
        if (!string.IsNullOrEmpty(backup.ItemId) && !string.Equals(restoreId, backup.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("备份标识与启动文件路径不一致，未做任何更改。");
        }

        byte[]? original = null;
        if (state.ApprovalRawBase64 is { Length: > 0 })
        {
            try
            {
                original = Convert.FromBase64String(state.ApprovalRawBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("启动文件夹备份中的启动批准数据已损坏，未做任何更改。", ex);
            }
        }

        if (state.ApprovalValueExisted != (original is not null))
        {
            throw new InvalidOperationException(
                "启动文件夹备份中的启动批准标记与原始数据不一致，未做任何更改。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (state.ManagesFile || StartupFolderQuarantine.IsQuarantined(_quarantineDirectory, restoreId, state.FullPath))
            {
                var directory = state.QuarantineDirectory.Length > 0
                    ? state.QuarantineDirectory
                    : _quarantineDirectory;
                if (!string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(_quarantineDirectory)),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("备份中的启动文件保存位置不属于本工具，未恢复。");
                }

                var originalExists = File.Exists(state.FullPath);
                var quarantinedExists = StartupFolderQuarantine.IsQuarantined(directory, restoreId, state.FullPath);
                if (originalExists && quarantinedExists)
                {
                    throw new InvalidOperationException("原位置和已关闭副本同时存在，未覆盖任何文件。");
                }

                if (state.Quarantined && originalExists)
                {
                    StartupFolderQuarantine.Disable(directory, restoreId, state.FullPath);
                }
                else if (!state.Quarantined && quarantinedExists)
                {
                    StartupFolderQuarantine.Restore(directory, restoreId, state.FullPath);
                }
                else if (!originalExists && !quarantinedExists)
                {
                    throw new InvalidOperationException("启动文件及已关闭副本均已丢失，无法恢复。");
                }
            }

            // The approval bytes are restored exactly as they were captured.
            if (original is not null)
            {
                StartupApproved.WriteApproval(
                    StartupApprovalFamily.StartupFolder,
                    state.ValueName,
                    original);
            }
            else
            {
                StartupApproved.DeleteApproval(
                    StartupApprovalFamily.StartupFolder,
                    state.ValueName);
            }
        }
        catch (Exception ex) when (IsAccessException(ex) || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"无法恢复启动文件夹条目“{state.ValueName}”的 Windows 启动批准状态。",
                ex);
        }

        return Task.CompletedTask;
    }

    private static void ScanFolder(
        string folder,
        StartupScope scope,
        string description,
        IReadOnlyDictionary<string, byte[]> approvals,
        IDictionary<string, string> publisherCache,
        List<StartupItem> items,
        HashSet<string> seenIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsAccessException(ex))
        {
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (var fullPath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(fullPath))
            {
                continue;
            }

            var extension = Path.GetExtension(fullPath);
            if (!SupportedExtensions.Contains(extension))
            {
                // desktop.ini, unknown files, and directories are not startup entries.
                continue;
            }

            var id = StartupItemIds.CreateFolderId(scope, fullPath);
            if (!seenIds.Add(id))
            {
                continue;
            }

            var fileName = Path.GetFileName(fullPath);
            var name = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fileName;
            }

            var resolved = BuildCommand(fullPath, extension);
            var publisher = PublisherResolver.Resolve(resolved.TargetPath, publisherCache);

            approvals.TryGetValue(fileName, out var approval);
            DateTimeOffset? lastModified = null;
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Exists)
                {
                    lastModified = new DateTimeOffset(info.LastWriteTime);
                }
            }
            catch (Exception ex) when (IsAccessException(ex))
            {
                // The file is still displayed without a timestamp.
            }

            items.Add(new StartupItem
            {
                Id = id,
                Name = name,
                Publisher = publisher,
                Source = StartupSource.Folder,
                Scope = scope,
                Enabled = true,
                RequiresAdmin = scope == StartupScope.AllUsers,
                Location = fullPath,
                SubLocation = GetSubLocation(extension, description) +
                    (StartupApproved.IsEnabled(approval) ? string.Empty : " · Windows 已标记禁用，入口仍存在；可再次关闭以移出"),
                Command = resolved.Command,
                TargetPath = resolved.TargetPath,
                LastModified = lastModified,
                Mechanism = "startupFolderQuarantine",
                Metadata =
                {
                    ["scope"] = scope.ToString(),
                    ["fullPath"] = fullPath,
                    ["folder"] = folder,
                    ["approvalFamily"] = StartupApprovalFamily.StartupFolder.ToString(),
                    ["approvalValueName"] = fileName,
                    ["approvalKeyPath"] =
                        StartupApproved.GetDisplayKeyPath(StartupApprovalFamily.StartupFolder)
                }
            });
        }
    }

    private static ResolvedCommand BuildCommand(string fullPath, string extension)
    {
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            ShellShortcutResolver.TryResolve(fullPath, out var target, out var arguments) &&
            target.Length > 0)
        {
            var command = QuoteIfNeeded(target);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                command += " " + arguments;
            }

            return new ResolvedCommand(command, target);
        }

        if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var url = InternetShortcutReader.TryReadUrl(fullPath);
            if (!string.IsNullOrWhiteSpace(url))
            {
                // Internet shortcuts are not executable files, so there is no
                // target path for publisher lookup.
                return new ResolvedCommand(url, null);
            }

            return new ResolvedCommand(fullPath, null);
        }

        // Direct .exe/.bat/.cmd file, or an unresolvable .lnk.
        return new ResolvedCommand(fullPath, fullPath);
    }

    private static string QuoteIfNeeded(string path) =>
        path.Contains(' ') && !path.StartsWith('"')
            ? "\"" + path + "\""
            : path;

    private static string GetSubLocation(string extension, string description)
    {
        var kind = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? "快捷方式"
            : "启动文件";
        return $"{kind} · {description}";
    }

    private StartupScope? GetScopeForPath(string fullPath)
    {
        if (!Path.IsPathFullyQualified(fullPath) || !SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            return null;
        var parent = Path.GetDirectoryName(Path.GetFullPath(fullPath));
        if (string.Equals(parent, _userStartupFolder, StringComparison.OrdinalIgnoreCase)) return StartupScope.CurrentUser;
        if (string.Equals(parent, _commonStartupFolder, StringComparison.OrdinalIgnoreCase)) return StartupScope.AllUsers;
        return null;
    }

    private StartupFolderTarget ResolveTarget(StartupItem item)
    {
        if (item.Source != StartupSource.Folder)
        {
            throw new InvalidOperationException("该启动项不是启动文件夹条目，无法处理。");
        }

        if (!item.Metadata.TryGetValue("fullPath", out var fullPath) ||
            !item.Metadata.TryGetValue("scope", out var scopeName) ||
            !item.Metadata.TryGetValue("approvalValueName", out var valueName) ||
            string.IsNullOrWhiteSpace(fullPath) ||
            string.IsNullOrWhiteSpace(valueName))
        {
            throw new InvalidOperationException("启动项缺少启动文件夹定位信息，请重新扫描后再操作。");
        }

        var scope = Enum.TryParse<StartupScope>(scopeName, ignoreCase: true, out var parsedScope) &&
                    parsedScope is StartupScope.CurrentUser or StartupScope.AllUsers
            ? parsedScope
            : throw new InvalidOperationException($"不支持的启动项范围：{scopeName}。");

        if (fullPath.Contains("..", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(fullPath), valueName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动文件夹定位信息无效，请重新扫描后再操作。");
        }

        var expectedId = StartupItemIds.CreateFolderId(scope, fullPath);
        if (GetScopeForPath(fullPath) != scope)
            throw new InvalidOperationException("目标不属于配置的启动文件夹，未做任何更改。");
        if (!string.IsNullOrEmpty(item.Id) &&
            !string.Equals(item.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动项标识与启动文件夹定位信息不一致，请重新扫描后再操作。");
        }

        return new StartupFolderTarget(scope, fullPath, valueName);
    }

    private static string NormalizeFolder(string? configured, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    private static bool IsAccessException(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException;

    private sealed record StartupFolderTarget(
        StartupScope Scope,
        string FullPath,
        string ValueName);

    private readonly record struct ResolvedCommand(string Command, string? TargetPath);
}
