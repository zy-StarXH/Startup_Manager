using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Providers.RegistryFolder;

namespace StartupManager.Core.Providers;

/// <summary>
/// Enumerates HKCU/HKLM Run values in both 64-bit and WOW6432Node views and
/// toggles them through the per-user Windows StartupApproved state. Run values
/// themselves are never modified or deleted.
/// </summary>
public sealed class RegistryStartupProvider : IStartupProvider, IStartupChangeProvider
{
    public const string ProviderStateKind = "registryApproval";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Wow6432RunKeyPath =
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    private static readonly RunLocation[] RunLocations =
    {
        new("HKCU", RegistryHive.CurrentUser, RegistryView.Registry64),
        new("HKLM", RegistryHive.LocalMachine, RegistryView.Registry64),
        new("HKCU", RegistryHive.CurrentUser, RegistryView.Registry32),
        new("HKLM", RegistryHive.LocalMachine, RegistryView.Registry32)
    };

    public StartupSource Source => StartupSource.Registry;

    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<StartupItem>();
        // HKCU\Software is not registry-redirected, so both views open the same
        // physical HKCU Run key. De-duplicate by effective physical key + value.
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in RunLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ScanLocation(location, items, seenSources, cancellationToken);
            }
            catch (Exception ex) when (IsRegistryAccessException(ex))
            {
                // Unreadable hive/view must not hide the other startup sources.
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
        var currentCommand = ReadRunCommand(target);

        var approvalExists = false;
        byte[]? approval = null;
        try
        {
            StartupApproved.TryReadApproval(
                target.ApprovalFamily,
                target.ValueName,
                out approval,
                out approvalExists);
        }
        catch (Exception ex) when (IsRegistryAccessException(ex))
        {
            throw new InvalidOperationException(
                $"无法读取启动项“{target.ValueName}”的 Windows 启动批准状态，未做任何更改。",
                ex);
        }

        if (approvalExists && approval is null)
        {
            throw new InvalidOperationException(
                $"启动项“{target.ValueName}”的 Windows 启动批准状态格式异常，未做任何更改。");
        }

        var wasEnabled = StartupApproved.IsEnabled(approval);
        var state = new RegistryApprovalState
        {
            Hive = target.HiveName,
            View = target.View.ToString(),
            KeyPath = RunKeyPath,
            ValueName = target.ValueName,
            ApprovalFamily = target.ApprovalFamily.ToString(),
            ApprovalValueExisted = approvalExists,
            ApprovalRawBase64 = approval is null ? null : Convert.ToBase64String(approval),
            RunCommand = currentCommand
        };

        return Task.FromResult(new StateSnapshot
        {
            Kind = ProviderStateKind,
            Json = JsonSerializer.Serialize(state, JsonDefaults.Options),
            Summary = $"注册表值：{target.ValueName}；启动项原本已{(wasEnabled ? "启用" : "禁用")}"
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
        // Refuse to act when the Run value disappeared after the scan instead of
        // silently recreating it or writing an orphan approval value.
        _ = ReadRunCommand(target);

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = enabled
            ? StartupApproved.CreateEnabledBytes(DateTime.UtcNow)
            : StartupApproved.CreateDisabledBytes(DateTime.UtcNow);

        try
        {
            StartupApproved.WriteApproval(target.ApprovalFamily, target.ValueName, bytes);
        }
        catch (Exception ex) when (IsRegistryAccessException(ex) || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"无法{(enabled ? "启用" : "禁用")}注册表启动项“{target.ValueName}”，Windows 启动批准状态写入失败。",
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
        if (!RunValueExists(target))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var approvalExists = false;
        byte[]? approval = null;
        try
        {
            StartupApproved.TryReadApproval(
                target.ApprovalFamily,
                target.ValueName,
                out approval,
                out approvalExists);
        }
        catch (Exception ex) when (IsRegistryAccessException(ex))
        {
            throw new InvalidOperationException(
                $"无法验证启动项“{target.ValueName}”的 Windows 启动批准状态。",
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
                $"备份状态类型“{backup.StateKind}”无法由注册表启动项恢复。");
        }

        RegistryApprovalState? state;
        try
        {
            state = JsonSerializer.Deserialize<RegistryApprovalState>(
                backup.StateJson,
                JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("注册表启动项备份状态无法解析，未做任何更改。", ex);
        }

        if (state is null || string.IsNullOrWhiteSpace(state.ValueName))
        {
            throw new InvalidOperationException("注册表启动项备份状态缺少值名称，未做任何更改。");
        }

        if (!string.IsNullOrWhiteSpace(state.KeyPath) &&
            !string.Equals(state.KeyPath, RunKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("注册表启动项备份状态指向了不支持的键路径，未做任何更改。");
        }

        var view = ParseView(state.View);
        _ = ParseHive(state.Hive);
        var family = StartupApproved.GetApprovalFamily(view);
        if (!string.IsNullOrWhiteSpace(state.ApprovalFamily) &&
            !string.Equals(state.ApprovalFamily, family.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("注册表启动项备份状态中的启动批准映射不一致，未做任何更改。");
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
                throw new InvalidOperationException("注册表启动项备份中的启动批准数据已损坏，未做任何更改。", ex);
            }
        }

        if (state.ApprovalValueExisted != (original is not null))
        {
            throw new InvalidOperationException(
                "注册表启动项备份中的启动批准标记与原始数据不一致，未做任何更改。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Only the approval value is touched. The original Run value is never
            // recreated, rewritten, or deleted.
            if (original is not null)
            {
                StartupApproved.WriteApproval(family, state.ValueName, original);
            }
            else
            {
                StartupApproved.DeleteApproval(family, state.ValueName);
            }
        }
        catch (Exception ex) when (IsRegistryAccessException(ex) || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"无法恢复注册表启动项“{state.ValueName}”的 Windows 启动批准状态。",
                ex);
        }

        return Task.CompletedTask;
    }

    private static void ScanLocation(
        RunLocation location,
        List<StartupItem> items,
        HashSet<string> seenSources,
        CancellationToken cancellationToken)
    {
        var family = StartupApproved.GetApprovalFamily(location.View);
        var approvals = StartupApproved.ReadApprovals(family);
        var publisherCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: false);
        if (runKey is null)
        {
            return;
        }

        foreach (var valueName in runKey.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(valueName))
            {
                continue;
            }

            string command;
            try
            {
                var value = runKey.GetValue(
                    valueName,
                    null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value is not string text)
                {
                    // Skip non-string values: v1 only manages REG_SZ / REG_EXPAND_SZ.
                    continue;
                }

                command = text;
            }
            catch (Exception ex) when (IsRegistryAccessException(ex))
            {
                continue;
            }

            // The 32-bit view of HKLM points at the distinct WOW6432Node key;
            // for HKCU both views address the same physical Run key.
            var effectiveKeyPath =
                location.View == RegistryView.Registry32 && location.Hive == RegistryHive.LocalMachine
                    ? Wow6432RunKeyPath
                    : RunKeyPath;
            if (!seenSources.Add($"{location.HiveName}|{effectiveKeyPath}|{valueName}"))
            {
                continue;
            }

            var id = StartupItemIds.CreateRegistryId(
                location.HiveName,
                location.View.ToString(),
                RunKeyPath,
                valueName);

            approvals.TryGetValue(valueName, out var approval);
            var enabled = StartupApproved.IsEnabled(approval);
            var targetPath = CommandLineParser.TryGetExecutablePath(command);
            var publisher = PublisherResolver.Resolve(targetPath, publisherCache);
            DateTimeOffset? lastModified = StartupApproved.TryGetTimestamp(approval, out var timestamp)
                ? timestamp
                : null;

            items.Add(new StartupItem
            {
                Id = id,
                Name = valueName,
                Publisher = publisher,
                Source = StartupSource.Registry,
                Scope = location.Hive == RegistryHive.CurrentUser
                    ? StartupScope.CurrentUser
                    : StartupScope.AllUsers,
                Enabled = enabled,
                RequiresAdmin = false,
                Location = GetDisplayLocation(location),
                SubLocation = "值名称：" + valueName,
                Command = command,
                TargetPath = targetPath,
                LastModified = lastModified,
                Mechanism = "startupApproved",
                Metadata =
                {
                    ["hive"] = location.HiveName,
                    ["view"] = location.View.ToString(),
                    ["keyPath"] = RunKeyPath,
                    ["valueName"] = valueName,
                    ["approvalFamily"] = family.ToString(),
                    ["approvalValueName"] = valueName,
                    ["approvalKeyPath"] = StartupApproved.GetDisplayKeyPath(family)
                }
            });
        }
    }

    private static RegistryRunTarget ResolveTarget(StartupItem item)
    {
        if (item.Source != StartupSource.Registry)
        {
            throw new InvalidOperationException("该启动项不是注册表启动项，无法处理。");
        }

        if (!item.Metadata.TryGetValue("hive", out var hiveName) ||
            !item.Metadata.TryGetValue("view", out var viewName) ||
            !item.Metadata.TryGetValue("keyPath", out var keyPath) ||
            !item.Metadata.TryGetValue("valueName", out var valueName) ||
            string.IsNullOrWhiteSpace(valueName))
        {
            throw new InvalidOperationException("启动项缺少注册表定位信息，请重新扫描后再操作。");
        }

        var hive = ParseHive(hiveName);
        var normalizedView = ParseView(viewName);

        if (!string.Equals(keyPath, RunKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动项指向了不支持的注册表键路径，请重新扫描后再操作。");
        }

        var expectedId = StartupItemIds.CreateRegistryId(
            hiveName.ToUpperInvariant(),
            normalizedView.ToString(),
            RunKeyPath,
            valueName);
        if (!string.IsNullOrEmpty(item.Id) &&
            !string.Equals(item.Id, expectedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("启动项标识与注册表定位信息不一致，请重新扫描后再操作。");
        }

        return new RegistryRunTarget(
            hiveName.ToUpperInvariant(),
            hive,
            normalizedView,
            valueName,
            StartupApproved.GetApprovalFamily(normalizedView));
    }

    /// <summary>Reads the Run value and throws when it no longer exists.</summary>
    private static string ReadRunCommand(RegistryRunTarget target)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
            using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: false);
            if (runKey is null)
            {
                throw new InvalidOperationException(
                    $"注册表启动项“{target.ValueName}”已不存在，未做任何更改。");
            }

            var value = runKey.GetValue(
                target.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null)
            {
                throw new InvalidOperationException(
                    $"注册表启动项“{target.ValueName}”已不存在，未做任何更改。");
            }

            if (value is not string text)
            {
                throw new InvalidOperationException(
                    $"注册表启动项“{target.ValueName}”的值类型无法识别，未做任何更改。");
            }

            return text;
        }
        catch (Exception ex) when (IsRegistryAccessException(ex))
        {
            throw new InvalidOperationException(
                $"无法读取注册表启动项“{target.ValueName}”，未做任何更改。",
                ex);
        }
    }

    private static bool RunValueExists(RegistryRunTarget target)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Hive, target.View);
            using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: false);
            if (runKey is null)
            {
                return false;
            }

            var value = runKey.GetValue(
                target.ValueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is string;
        }
        catch (Exception ex) when (IsRegistryAccessException(ex))
        {
            return false;
        }
    }

    private static RegistryHive ParseHive(string hiveName) =>
        hiveName.ToUpperInvariant() switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            _ => throw new InvalidOperationException($"不支持的注册表配置单元：{hiveName}。")
        };

    private static RegistryView ParseView(string viewName)
    {
        if (Enum.TryParse<RegistryView>(viewName, ignoreCase: true, out var view) &&
            view is RegistryView.Registry64 or RegistryView.Registry32)
        {
            return view;
        }

        throw new InvalidOperationException($"不支持的注册表视图：{viewName}。");
    }

    private static string GetDisplayLocation(RunLocation location) =>
        location.HiveName + @"\" +
        (location.View == RegistryView.Registry32 ? Wow6432RunKeyPath : RunKeyPath);

    private static bool IsRegistryAccessException(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or ObjectDisposedException;

    private sealed record RunLocation(
        string HiveName,
        RegistryHive Hive,
        RegistryView View);

    private sealed record RegistryRunTarget(
        string HiveName,
        RegistryHive Hive,
        RegistryView View,
        string ValueName,
        StartupApprovalFamily ApprovalFamily);
}
