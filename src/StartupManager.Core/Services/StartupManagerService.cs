using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

/// <summary>
/// Transactional startup manager. Every mutation captures state, persists a
/// backup, applies the smallest OS change, verifies it, and only then reports
/// success. Provider failures are converted to user-safe warnings.
/// </summary>
public sealed partial class StartupManagerService : IStartupManager
{
    private readonly IReadOnlyList<IStartupProvider> _providers;
    private readonly IReadOnlyDictionary<StartupSource, IStartupChangeProvider> _changeProviders;
    private readonly IProtectionPolicy _protectionPolicy;
    private readonly IBackupStore _backupStore;
    private readonly AppPaths _paths;
    private readonly Action<string>? _logSink;
    private readonly object _settingsGate = new();
    private AppSettings _settings;

    public StartupManagerService(
        IEnumerable<IStartupProvider> providers,
        IEnumerable<IStartupChangeProvider> changeProviders,
        IProtectionPolicy protectionPolicy,
        IBackupStore backupStore,
        AppPaths paths,
        Action<string>? logSink = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(changeProviders);
        ArgumentNullException.ThrowIfNull(protectionPolicy);
        ArgumentNullException.ThrowIfNull(backupStore);
        ArgumentNullException.ThrowIfNull(paths);

        _providers = providers.Where(provider => provider is not null).ToArray();
        _protectionPolicy = protectionPolicy;
        _backupStore = backupStore;
        _paths = paths;
        _logSink = logSink;

        var changeMap = new Dictionary<StartupSource, IStartupChangeProvider>();
        foreach (var provider in changeProviders)
        {
            if (provider is null)
            {
                continue;
            }

            if (changeMap.TryAdd(provider.Source, provider))
            {
                continue;
            }

            Log("init", SourceLabel(provider.Source), "存在重复的可写提供程序，已忽略后注册者。");
        }

        _changeProviders = changeMap;

        try
        {
            _paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            Log("init", null, "创建数据目录失败：" + ex.GetType().Name + "：" + ex.Message);
        }

        _settings = SettingsStore.Load(_paths.SettingsFile, message => Log("settings", null, message));
        Log("init", null, $"服务已初始化，提供程序 {_providers.Count} 个，可写 {_changeProviders.Count} 个。");
    }

    public AppSettings Settings
    {
        get
        {
            lock (_settingsGate)
            {
                return SettingsStore.Clone(_settings);
            }
        }
    }

    public string BackupDirectory => _backupStore.BackupDirectory;

    public string LogDirectory => _paths.LogDirectory;

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = SettingsStore.Normalize(settings);
        await SettingsStore.SaveAsync(_paths.SettingsFile, normalized, cancellationToken).ConfigureAwait(false);

        lock (_settingsGate)
        {
            _settings = normalized;
        }

        Log("saveSettings", null, $"已保存设置（备份保留 {normalized.BackupRetentionDays} 天）。");

        try
        {
            await _backupStore.PruneAsync(normalized.BackupRetentionDays, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("saveSettings", null, "清理旧备份失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    private AppSettings GetSettingsSnapshot()
    {
        lock (_settingsGate)
        {
            return SettingsStore.Clone(_settings);
        }
    }

    private IStartupChangeProvider? GetChangeProvider(StartupSource source)
    {
        return _changeProviders.TryGetValue(source, out var provider) ? provider : null;
    }

    private static bool IsIncluded(AppSettings settings, StartupSource source) => source switch
    {
        StartupSource.Registry => settings.IncludeRegistry,
        StartupSource.Folder => settings.IncludeStartupFolders,
        StartupSource.Task => settings.IncludeScheduledTasks,
        StartupSource.Service => settings.IncludeServices,
        _ => false
    };

    private static string SourceLabel(StartupSource source) => source switch
    {
        StartupSource.Registry => "注册表启动项",
        StartupSource.Folder => "启动文件夹",
        StartupSource.Task => "计划任务",
        StartupSource.Service => "系统服务",
        _ => "启动项"
    };

    private void Log(string operation, string? item, string result)
    {
        try
        {
            _logSink?.Invoke($"[{operation}] {item ?? "-"}：{result}");
        }
        catch (Exception)
        {
            // A diagnostic sink must never break a change.
        }

        AppLog.Append(_paths.LogDirectory, operation, item, result);
    }
}
