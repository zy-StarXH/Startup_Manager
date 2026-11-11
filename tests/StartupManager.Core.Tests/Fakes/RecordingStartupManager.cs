using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

/// <summary>Fake IStartupManager used by ElevationHost tests; no OS changes.</summary>
public sealed class RecordingStartupManager : IStartupManager
{
    public AppSettings Settings { get; set; } = new();

    public string BackupDirectory { get; set; } = "backups";

    public string LogDirectory { get; set; } = "logs";

    public int ScanCalls { get; private set; }

    public List<string> SetEnabledItemIds { get; } = new();

    public bool? SetEnabledEnabled { get; private set; }

    public bool? SetEnabledIsElevated { get; private set; }

    public BatchChangeResult SetEnabledResult { get; set; } = new()
    {
        Message = "已关闭 1 项。",
        Results = new[]
        {
            new ChangeResult
            {
                Success = true,
                Verified = true,
                ItemId = "reg-test",
                Message = "已关闭自启动。"
            }
        }
    };

    public int RestoreCalls { get; private set; }

    public string? RestoreBackupId { get; private set; }

    public bool? RestoreIsElevated { get; private set; }

    public ChangeResult RestoreResult { get; set; } = new()
    {
        Success = true,
        Verified = true,
        ItemId = "svc-test",
        Message = "已恢复自启动。"
    };

    public Task<StartupSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCalls++;
        return Task.FromResult(new StartupSnapshot());
    }

    public Task<BatchChangeResult> SetEnabledAsync(
        IReadOnlyCollection<string> itemIds,
        bool enabled,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        SetEnabledItemIds.AddRange(itemIds);
        SetEnabledEnabled = enabled;
        SetEnabledIsElevated = isElevated;
        return Task.FromResult(SetEnabledResult);
    }

    public Task<ChangeResult> RestoreBackupAsync(
        string backupId,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        RestoreCalls++;
        RestoreBackupId = backupId;
        RestoreIsElevated = isElevated;
        return Task.FromResult(RestoreResult);
    }

    public Task<IReadOnlyList<BackupSummary>> GetBackupsAsync(
        bool includeRestored,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BackupSummary> empty = Array.Empty<BackupSummary>();
        return Task.FromResult(empty);
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
