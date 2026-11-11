using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

public interface IBackupStore
{
    string BackupDirectory { get; }

    Task<BackupRecord> CreateAsync(StartupItem item, StateSnapshot state, CancellationToken cancellationToken);
    Task<BackupRecord?> GetAsync(string backupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRecord>> GetActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BackupRecord>> GetAllAsync(CancellationToken cancellationToken);
    Task UpdateAsync(BackupRecord record, CancellationToken cancellationToken);
    Task PruneAsync(int retentionDays, CancellationToken cancellationToken);
}
