using System.Text.Json;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Backup;

/// <summary>
/// One JSON file per backup record. The file name is the record GUID, which
/// makes path traversal impossible: a caller-supplied id is only ever accepted
/// when it parses as a GUID and is re-rendered in canonical "N" form.
/// StateJson is provider-private and lives only in these files; no API on this
/// class returns state to the web layer.
/// </summary>
public sealed class JsonBackupStore : IBackupStore
{
    private readonly string _directory;
    private readonly Action<string>? _logSink;

    public JsonBackupStore(AppPaths paths, Action<string>? logSink = null)
        : this(paths?.BackupDirectory ?? throw new ArgumentNullException(nameof(paths)), logSink)
    {
    }

    public JsonBackupStore(string backupDirectory, Action<string>? logSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _directory = Path.GetFullPath(backupDirectory);
        _logSink = logSink;

        Directory.CreateDirectory(_directory);
        if (!AtomicFile.TryRestrictDirectory(_directory))
        {
            Log("无法将备份目录权限限制为当前用户，已继续（尽力而为）。");
        }
    }

    public string BackupDirectory => _directory;

    public async Task<BackupRecord> CreateAsync(
        StartupItem item,
        StateSnapshot state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(item.Id))
        {
            throw new ArgumentException("启动项标识为空，无法创建备份。", nameof(item));
        }

        if (string.IsNullOrWhiteSpace(state.Kind))
        {
            throw new ArgumentException("备份状态类型为空，无法创建备份。", nameof(state));
        }

        if (string.IsNullOrWhiteSpace(state.Json))
        {
            throw new ArgumentException("备份状态为空，无法创建备份。", nameof(state));
        }

        try
        {
            using var _ = JsonDocument.Parse(state.Json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("备份状态不是有效的 JSON。", nameof(state), ex);
        }

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

        await WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
        Log($"已创建备份 {record.Id}（启动项 {record.ItemId}）。");
        return record;
    }

    public Task<BackupRecord?> GetAsync(string backupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeId(backupId, out var normalizedId))
        {
            Log("已拒绝格式无效的备份标识。");
            return Task.FromResult<BackupRecord?>(null);
        }

        return Task.FromResult(TryReadRecord(GetRecordPath(normalizedId), normalizedId));
    }

    public Task<IReadOnlyList<BackupRecord>> GetActiveAsync(CancellationToken cancellationToken)
    {
        var records = ReadAllRecords(cancellationToken)
            .Where(record => record.Status == BackupStatus.Active)
            .ToList();
        return Task.FromResult<IReadOnlyList<BackupRecord>>(records);
    }

    public Task<IReadOnlyList<BackupRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<BackupRecord>>(ReadAllRecords(cancellationToken));
    }

    public async Task UpdateAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalizeId(record.Id, out var normalizedId))
        {
            throw new ArgumentException("备份标识格式无效。", nameof(record));
        }

        var path = GetRecordPath(normalizedId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("备份记录不存在。", path);
        }

        await WriteRecordAsync(record, cancellationToken).ConfigureAwait(false);
        Log($"已更新备份 {normalizedId}，状态 {record.Status}。");
    }

    /// <summary>
    /// Deletes restored/failed records older than the retention window.
    /// Active records are never pruned: they represent a startup item that is
    /// still disabled and are required to make it visible/restorable again.
    /// A retention value of zero or less disables pruning.
    /// </summary>
    public Task PruneAsync(int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays <= 0)
        {
            Log("备份保留天数未启用，跳过清理。");
            return Task.CompletedTask;
        }

        var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
        var removed = 0;

        foreach (var record in ReadAllRecords(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.Status == BackupStatus.Active || record.CreatedAt >= cutoff)
            {
                continue;
            }

            try
            {
                File.Delete(GetRecordPath(record.Id));
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"清理备份 {record.Id} 失败：{ex.GetType().Name}。");
            }
        }

        Log($"已按 {retentionDays} 天保留期清理 {removed} 条备份记录。");
        return Task.CompletedTask;
    }

    private async Task WriteRecordAsync(BackupRecord record, CancellationToken cancellationToken)
    {
        if (!TryNormalizeId(record.Id, out var normalizedId))
        {
            throw new ArgumentException("备份标识格式无效。", nameof(record));
        }

        var json = JsonSerializer.Serialize(record, JsonDefaults.Indented);
        await AtomicFile.WriteAllTextAsync(
                GetRecordPath(normalizedId),
                json,
                restrictToCurrentUser: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private IReadOnlyList<BackupRecord> ReadAllRecords(CancellationToken cancellationToken)
    {
        var records = new List<BackupRecord>();

        string[] files;
        try
        {
            files = Directory.GetFiles(_directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Log($"读取备份目录失败：{ex.GetType().Name}。");
            return records;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileNameWithoutExtension(file);
            if (!TryNormalizeId(fileName, out var expectedId))
            {
                Log("已忽略文件名不是备份 GUID 的 JSON 文件。");
                continue;
            }

            var record = TryReadRecord(file, expectedId);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        records.Sort(static (left, right) =>
        {
            var byDate = right.CreatedAt.CompareTo(left.CreatedAt);
            return byDate != 0 ? byDate : string.CompareOrdinal(left.Id, right.Id);
        });

        return records;
    }

    private BackupRecord? TryReadRecord(string path, string expectedId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var record = JsonSerializer.Deserialize<BackupRecord>(json, JsonDefaults.Options);
            if (record is null)
            {
                Log($"已跳过无法解析的备份文件 {expectedId}。");
                return null;
            }

            if (!TryNormalizeId(record.Id, out var recordId) ||
                !string.Equals(recordId, expectedId, StringComparison.Ordinal))
            {
                Log($"已跳过标识与文件名不一致的备份文件 {expectedId}。");
                return null;
            }

            if (string.IsNullOrWhiteSpace(record.ItemId) ||
                string.IsNullOrWhiteSpace(record.StateKind) ||
                string.IsNullOrWhiteSpace(record.StateJson))
            {
                Log($"已跳过内容不完整的备份文件 {expectedId}。");
                return null;
            }

            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            Log($"已跳过损坏的备份文件 {expectedId}：{ex.GetType().Name}。");
            return null;
        }
    }

    private string GetRecordPath(string normalizedId) => Path.Combine(_directory, normalizedId + ".json");

    internal static bool TryNormalizeId(string? backupId, out string normalizedId)
    {
        normalizedId = string.Empty;
        if (string.IsNullOrWhiteSpace(backupId) || backupId.Length > 64)
        {
            return false;
        }

        if (!Guid.TryParse(backupId, out var guid))
        {
            return false;
        }

        normalizedId = guid.ToString("N");
        return true;
    }

    private void Log(string message)
    {
        try
        {
            _logSink?.Invoke(message);
        }
        catch (Exception)
        {
            // Logging must never break persistence.
        }
    }
}
