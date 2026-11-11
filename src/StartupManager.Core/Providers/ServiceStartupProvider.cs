using System.Globalization;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Protection;

namespace StartupManager.Core.Providers;

/// <summary>
/// Windows service startup source. Scans only automatic Win32 services
/// (including delayed automatic) and changes the start type between Automatic
/// and Manual. Services are never started or stopped.
/// </summary>
public sealed class ServiceStartupProvider : IStartupProvider, IStartupChangeProvider
{
    private const string StateKind = "serviceStartType";
    private const string Mechanism = "serviceStartType";
    private const string ServicesRegistryRoot = @"SYSTEM\CurrentControlSet\Services\";

    public StartupSource Source => StartupSource.Service;

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

    private static IReadOnlyList<StartupItem> Scan(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scManager = ServiceNative.OpenManager();
        try
        {
            var services = ServiceNative.EnumerateWin32Services(scManager, cancellationToken);
            var items = new List<StartupItem>(services.Count);

            foreach (var service in services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ServiceConfiguration? configuration;
                try
                {
                    configuration = ServiceNative.TryQuery(scManager, service.ServiceName, service.DisplayName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // One unreadable service must not hide the rest of the machine.
                    continue;
                }

                if (configuration is null)
                {
                    continue;
                }

                var current = configuration.Value;
                if (current.StartType != ServiceStartTypes.Automatic)
                {
                    continue;
                }

                if ((current.ServiceType & ServiceNativeMethods.SERVICE_WIN32) == 0)
                {
                    // Defense in depth: drivers are never startup candidates.
                    continue;
                }

                var executablePath = ServiceCommandLine.ExtractExecutablePath(current.BinaryPath);
                var publisher = PublisherLookup.TryGetPublisher(executablePath);
                var displayName = current.DisplayName.Trim();
                if (displayName.Length == 0)
                {
                    displayName = current.ServiceName;
                }

                var isPackaged = (current.ServiceType & ServiceNativeMethods.SERVICE_PACKAGED_PROCESS) != 0;
                var effectiveStartType = current.StartType;
                if (isPackaged)
                {
                    // MSIX packaged services can keep reporting the SCM's cached
                    // automatic start type after our registry fallback. The
                    // registry Start value is what boot will actually read.
                    var registryStart = TryReadRegistryStart(current.ServiceName);
                    if (registryStart.HasValue)
                    {
                        effectiveStartType = registryStart.Value;
                    }
                }

                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ServiceName"] = current.ServiceName,
                    ["DisplayName"] = displayName,
                    ["StartType"] = effectiveStartType.ToString(CultureInfo.InvariantCulture),
                    ["OriginalStartType"] = current.StartType.ToString(CultureInfo.InvariantCulture),
                    ["DelayedAutoStart"] = current.DelayedAutoStart ? "true" : "false",
                    ["ServiceType"] = current.ServiceType.ToString(CultureInfo.InvariantCulture),
                    ["PackagedService"] = isPackaged ? "true" : "false",
                    ["BinaryPath"] = current.BinaryPath,
                    ["Description"] = current.Description
                };

                items.Add(new StartupItem
                {
                    Id = ServiceCommandLine.CreateServiceId(current.ServiceName),
                    Name = displayName,
                    Publisher = publisher,
                    Source = StartupSource.Service,
                    Scope = StartupScope.System,
                    Enabled = effectiveStartType == ServiceStartTypes.Automatic,
                    Protected = false,
                    IsPackagedService = isPackaged,
                    RequiresAdmin = true,
                    Location = "服务：" + current.ServiceName,
                    SubLocation = ServiceStartTypes.BuildSubLocation(
                        effectiveStartType,
                        current.DelayedAutoStart,
                        current.ServiceName) + (isPackaged ? " · 应用包服务" : string.Empty),
                    Command = ServiceCommandLine.BuildDisplayCommand(current.BinaryPath),
                    TargetPath = executablePath,
                    LastModified = null,
                    Mechanism = Mechanism,
                    Metadata = metadata
                });
            }

            return items;
        }
        finally
        {
            ServiceNative.CloseManager(scManager);
        }
    }

    private static StateSnapshot CaptureState(StartupItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureServiceItem(item);
        var serviceName = GetServiceName(item);

        var scManager = ServiceNative.OpenManager();
        try
        {
            var configuration = ServiceNative.TryQuery(scManager, serviceName);
            if (configuration is null)
            {
                throw new InvalidOperationException($"服务“{serviceName}”不存在或已被卸载，已取消修改。");
            }

            var current = configuration.Value;
            var isPackaged = IsPackagedService(item);
            var effectiveStartType = isPackaged
                ? TryReadRegistryStart(serviceName) ?? current.StartType
                : current.StartType;

            var state = new ServiceBackupState
            {
                ServiceName = serviceName,
                DisplayName = current.DisplayName,
                StartType = effectiveStartType,
                DelayedAutoStart = current.DelayedAutoStart,
                UseRegistryFallback = isPackaged,
                CapturedAt = DateTimeOffset.Now
            };

            return new StateSnapshot
            {
                Kind = StateKind,
                Json = JsonSerializer.Serialize(state, JsonDefaults.Options),
                Summary = ServiceStartTypes.BuildSummary(effectiveStartType, current.DelayedAutoStart)
            };
        }
        finally
        {
            ServiceNative.CloseManager(scManager);
        }
    }

    private static void ApplyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureServiceItem(item);
        var serviceName = GetServiceName(item);

        var expectedStartType = enabled
            ? ResolveEnableStartType(item)
            : ServiceStartTypes.Manual;

        if (IsPackagedService(item))
        {
            // Packaged services deny SERVICE_CHANGE_CONFIG to administrators.
            // The service registry key's Start value is writable by admins; boot
            // reads it. This fallback is explicit in the UI.
            WriteRegistryStart(serviceName, expectedStartType);
            var actual = TryReadRegistryStart(serviceName);
            if (actual != expectedStartType)
            {
                throw new InvalidOperationException(
                    $"服务“{serviceName}”的应用包启动注册表值写入后仍为" +
                    $"“{ServiceStartTypes.ToChineseLabel(actual ?? 255u)}”。");
            }

            return;
        }

        var scManager = ServiceNative.OpenManager();
        try
        {
            bool? delayedFlag = null;
            if (enabled &&
                expectedStartType == ServiceStartTypes.Automatic &&
                ResolveDelayedAutoStart(item))
            {
                delayedFlag = true;
            }

            ServiceNative.ChangeStartType(scManager, serviceName, expectedStartType, delayedFlag);

            var verified = ServiceNative.TryQuery(scManager, serviceName);
            if (verified is null)
            {
                throw new InvalidOperationException($"服务“{serviceName}”在修改后已不存在。");
            }

            if (verified.Value.StartType != expectedStartType)
            {
                throw new InvalidOperationException(
                    $"服务“{serviceName}”修改后重新读取启动类型仍为" +
                    $"“{ServiceStartTypes.ToChineseLabel(verified.Value.StartType, verified.Value.DelayedAutoStart)}”。");
            }
        }
        finally
        {
            ServiceNative.CloseManager(scManager);
        }
    }

    private static bool VerifyEnabled(StartupItem item, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureServiceItem(item);
        var serviceName = GetServiceName(item);
        var expectedStartType = enabled
            ? ResolveEnableStartType(item)
            : ServiceStartTypes.Manual;

        if (IsPackagedService(item))
        {
            return TryReadRegistryStart(serviceName) == expectedStartType;
        }

        var scManager = ServiceNative.OpenManager();
        try
        {
            var configuration = ServiceNative.TryQuery(scManager, serviceName);
            return configuration is not null && configuration.Value.StartType == expectedStartType;
        }
        finally
        {
            ServiceNative.CloseManager(scManager);
        }
    }

    private static void RestoreState(BackupRecord backup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(backup);

        if (backup.Source != StartupSource.Service)
        {
            throw new ArgumentException("备份记录不是服务类型，无法由服务提供程序恢复。", nameof(backup));
        }

        var state = DeserializeState(backup);
        var serviceName = (state.ServiceName ?? string.Empty).Trim();
        if (serviceName.Length == 0)
        {
            throw new InvalidOperationException("备份记录缺少服务名称，无法恢复。");
        }

        if (state.UseRegistryFallback)
        {
            WriteRegistryStart(serviceName, state.StartType);
            var actual = TryReadRegistryStart(serviceName);
            if (actual != state.StartType)
            {
                throw new InvalidOperationException(
                    $"恢复应用包服务“{serviceName}”后，注册表启动值仍为" +
                    $"“{ServiceStartTypes.ToChineseLabel(actual ?? 255u)}”，与备份不一致。");
            }

            return;
        }

        var scManager = ServiceNative.OpenManager();
        try
        {
            var existing = ServiceNative.TryQuery(scManager, serviceName);
            if (existing is null)
            {
                throw new InvalidOperationException($"服务“{serviceName}”不存在或已被卸载，无法恢复启动类型。");
            }

            ServiceNative.ChangeStartType(
                scManager,
                serviceName,
                state.StartType,
                state.DelayedAutoStart);

            var verified = ServiceNative.TryQuery(scManager, serviceName);
            if (verified is null)
            {
                throw new InvalidOperationException($"恢复服务“{serviceName}”后无法重新读取配置。");
            }

            if (verified.Value.StartType != state.StartType)
            {
                throw new InvalidOperationException(
                    $"恢复服务“{serviceName}”后重新读取的启动类型为" +
                    $"“{ServiceStartTypes.ToChineseLabel(verified.Value.StartType, verified.Value.DelayedAutoStart)}”，" +
                    "与备份不一致。");
            }

            if (state.StartType == ServiceStartTypes.Automatic &&
                verified.Value.DelayedAutoStart != state.DelayedAutoStart)
            {
                throw new InvalidOperationException(
                    $"恢复服务“{serviceName}”后延迟启动状态与备份不一致。");
            }
        }
        finally
        {
            ServiceNative.CloseManager(scManager);
        }
    }

    private static ServiceBackupState DeserializeState(BackupRecord backup)
    {
        if (string.IsNullOrWhiteSpace(backup.StateJson))
        {
            throw new InvalidOperationException("备份记录缺少服务状态，无法恢复。");
        }

        try
        {
            var state = JsonSerializer.Deserialize<ServiceBackupState>(backup.StateJson, JsonDefaults.Options);
            if (state is null)
            {
                throw new InvalidOperationException("备份记录中的服务状态无法解析，无法恢复。");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("备份记录中的服务状态已损坏，无法恢复。", exception);
        }
    }

    private static uint ResolveEnableStartType(StartupItem item)
    {
        // Prefer the original start type captured in metadata; fall back to Automatic.
        if (TryGetUInt(item, "OriginalStartType", out var original) &&
            original <= ServiceStartTypes.Automatic)
        {
            return original;
        }

        if (TryGetUInt(item, "StartType", out var startType) &&
            startType <= ServiceStartTypes.Automatic)
        {
            return startType;
        }

        return ServiceStartTypes.Automatic;
    }

    private static bool ResolveDelayedAutoStart(StartupItem item) =>
        TryGetBool(item, "DelayedAutoStart") || TryGetBool(item, "Delayed");

    private static bool TryGetUInt(StartupItem item, string key, out uint value)
    {
        value = 0;
        return item.Metadata.TryGetValue(key, out var raw) &&
            !string.IsNullOrWhiteSpace(raw) &&
            uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetBool(StartupItem item, string key)
    {
        return item.Metadata.TryGetValue(key, out var raw) &&
            !string.IsNullOrWhiteSpace(raw) &&
            bool.TryParse(raw, out var value) &&
            value;
    }

    private static void EnsureServiceItem(StartupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Source != StartupSource.Service)
        {
            throw new ArgumentException("该启动项不是服务，无法由服务提供程序处理。", nameof(item));
        }
    }

    private static bool IsPackagedService(StartupItem item)
    {
        if (item.IsPackagedService)
        {
            return true;
        }

        return TryGetUInt(item, "ServiceType", out var serviceType) &&
            (serviceType & ServiceNativeMethods.SERVICE_PACKAGED_PROCESS) != 0;
    }

    /// <summary>
    /// Reads the service's registry Start value. Returns null when the key or
    /// value is unavailable; never treats an access failure as "enabled".
    /// </summary>
    private static uint? TryReadRegistryStart(string serviceName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ServicesRegistryRoot + serviceName, writable: false);
            var value = key?.GetValue("Start");
            return value switch
            {
                int number when number >= 0 => (uint)number,
                uint number => number,
                _ => null
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static void WriteRegistryStart(string serviceName, uint startType)
    {
        if (string.IsNullOrWhiteSpace(serviceName) ||
            serviceName.Contains('\\') ||
            serviceName.Contains('/'))
        {
            throw new InvalidOperationException("服务名称无效，未修改启动注册表值。");
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ServicesRegistryRoot + serviceName, writable: true)
                ?? throw new InvalidOperationException(
                    $"服务注册表项不存在：{ServicesRegistryRoot}{serviceName}");
            key.SetValue("Start", (int)startType, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"修改应用包服务“{serviceName}”的启动注册表值需要管理员权限。",
                ex);
        }
    }

    private static string GetServiceName(StartupItem item)
    {
        if (item.Metadata.TryGetValue("ServiceName", out var metadataName) &&
            !string.IsNullOrWhiteSpace(metadataName))
        {
            return metadataName.Trim();
        }

        var location = item.Location?.Trim() ?? string.Empty;
        const string prefix = "服务：";
        if (location.StartsWith(prefix, StringComparison.Ordinal) &&
            location.Length > prefix.Length)
        {
            return location.Substring(prefix.Length).Trim();
        }

        throw new InvalidOperationException(
            $"无法确定启动项“{item.Name}”对应的服务名称，已取消修改。");
    }
}
