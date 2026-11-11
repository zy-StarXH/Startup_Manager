using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

/// <summary>The single facade used by the WPF host and the elevated helper.</summary>
public interface IStartupManager
{
    AppSettings Settings { get; }
    string BackupDirectory { get; }
    string LogDirectory { get; }

    Task<StartupSnapshot> ScanAsync(CancellationToken cancellationToken);
    Task<BatchChangeResult> SetEnabledAsync(IReadOnlyCollection<string> itemIds, bool enabled, bool isElevated, CancellationToken cancellationToken);
    Task<ChangeResult> RestoreBackupAsync(string backupId, bool isElevated, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupSummary>> GetBackupsAsync(bool includeRestored, CancellationToken cancellationToken);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
}
