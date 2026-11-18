using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

/// <summary>The single facade used by the WPF host and the elevated helper.</summary>
public interface IStartupManager
{
    AppSettings Settings { get; }
    string BackupDirectory { get; }
    string LogDirectory { get; }

    /// <summary>
    /// The most recent scan, already reflecting changes applied in memory by this
    /// manager. Null before the first scan. The host publishes this instead of
    /// paying for a second full scan after every toggle.
    /// </summary>
    StartupSnapshot? CachedSnapshot { get; }

    Task<StartupSnapshot> ScanAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Re-reads only the providers that own the given item ids and merges them
    /// into <see cref="CachedSnapshot"/>. Used after an elevated helper changed
    /// the OS in another process, so the host does not rescan every source.
    /// </summary>
    Task RefreshSourcesForItemsAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken);

    Task<ChangeResult> SetUserOverrideAsync(string itemId, bool allowed, CancellationToken cancellationToken);
    Task<BatchChangeResult> SetUserOverridesAsync(
        IReadOnlyCollection<string> itemIds,
        bool allowed,
        CancellationToken cancellationToken);
    Task<BatchChangeResult> SetEnabledAsync(IReadOnlyCollection<string> itemIds, bool enabled, bool isElevated, CancellationToken cancellationToken);
    Task<ChangeResult> RestoreBackupAsync(string backupId, bool isElevated, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupSummary>> GetBackupsAsync(bool includeRestored, CancellationToken cancellationToken);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
}
