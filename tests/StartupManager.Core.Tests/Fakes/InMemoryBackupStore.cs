using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

/// <summary>
/// In-memory IBackupStore for service tests. It clones on the way in and out so
/// a service must call UpdateAsync for changes to become visible, just like the
/// JSON store.
/// </summary>
public sealed class InMemoryBackupStore : IBackupStore
{
    private readonly Dictionary<string, BackupRecord> _records = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public InMemoryBackupStore(string? backupDirectory = null)
    {
        BackupDirectory = backupDirectory
            ?? Path.Combine(Path.GetTempPath(), "StartupManagerInMemory", Guid.NewGuid().ToString("N"));
    }

    public string BackupDirectory { get; }

    public int CreateCount { get; private set; }

    public int UpdateCount { get; private set; }

    public int PruneCount { get; private set; }

    public int LastRetentionDays { get; private set; }

    public bool FailCreate { get; set; }

    public List<string> Events { get; } = new();

    public Task<BackupRecord> CreateAsync(
        StartupItem item,
        StateSnapshot state,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (FailCreate)
            {
                throw new IOException("fake backup failure");
            }

            CreateCount++;
            Events.Add("backup:create");

            var record = new BackupRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ItemId = item.Id,
                Source = item.Source,
                Scope = item.Scope,
                RequiresAdmin = item.RequiresAdmin,
                DisplayName = item.Name,
                Location = item.Location,
                Command = item.Command,
                CreatedAt = DateTimeOffset.Now,
                Status = BackupStatus.Active,
                Summary = state.Summary,
                StateKind = state.Kind,
                StateJson = state.Json
            };

            _records[record.Id] = Clone(record);
            return Task.FromResult(Clone(record));
        }
    }

    public Task<BackupRecord?> GetAsync(string backupId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _records.TryGetValue(backupId, out var record) ? Clone(record) : null);
        }
    }

    public Task<IReadOnlyList<BackupRecord>> GetActiveAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<BackupRecord> active = _records.Values
                .Where(record => record.Status == BackupStatus.Active)
                .OrderByDescending(record => record.CreatedAt)
                .Select(Clone)
                .ToList();
            return Task.FromResult(active);
        }
    }

    public Task<IReadOnlyList<BackupRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<BackupRecord> all = _records.Values
                .OrderByDescending(record => record.CreatedAt)
                .Select(Clone)
                .ToList();
            return Task.FromResult(all);
        }
    }

    public Task UpdateAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            UpdateCount++;
            _records[record.Id] = Clone(record);
        }

        return Task.CompletedTask;
    }

    public Task PruneAsync(int retentionDays, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            PruneCount++;
            LastRetentionDays = retentionDays;

            if (retentionDays <= 0)
            {
                return Task.CompletedTask;
            }

            var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
            foreach (var record in _records.Values.ToList())
            {
                if (record.Status != BackupStatus.Active && record.CreatedAt < cutoff)
                {
                    _records.Remove(record.Id);
                }
            }
        }

        return Task.CompletedTask;
    }

    public void Add(BackupRecord record)
    {
        lock (_gate)
        {
            _records[record.Id] = Clone(record);
        }
    }

    public IReadOnlyList<BackupRecord> Snapshot()
    {
        lock (_gate)
        {
            return _records.Values.Select(Clone).ToList();
        }
    }

    private static BackupRecord Clone(BackupRecord record)
    {
        return new BackupRecord
        {
            Id = record.Id,
            ItemId = record.ItemId,
            Source = record.Source,
            Scope = record.Scope,
            RequiresAdmin = record.RequiresAdmin,
            DisplayName = record.DisplayName,
            Location = record.Location,
            Command = record.Command,
            CreatedAt = record.CreatedAt,
            RestoredAt = record.RestoredAt,
            VerifiedAt = record.VerifiedAt,
            Status = record.Status,
            Summary = record.Summary,
            Error = record.Error,
            StateKind = record.StateKind,
            StateJson = record.StateJson
        };
    }
}
