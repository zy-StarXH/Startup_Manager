namespace StartupManager.Core.Models;

public sealed class ChangeResult
{
    public bool Success { get; init; }
    public bool Canceled { get; init; }
    public bool RequiresElevation { get; init; }
    public bool Verified { get; init; }
    public string? ItemId { get; init; }
    public string? BackupId { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }

    public static ChangeResult Ok(string itemId, string backupId, string message = "已关闭自启动。") => new()
    {
        Success = true,
        Verified = true,
        ItemId = itemId,
        BackupId = backupId,
        Message = message
    };

    public static ChangeResult Fail(string? itemId, string message, bool requiresElevation = false, bool canceled = false) => new()
    {
        ItemId = itemId,
        Message = message,
        Error = message,
        RequiresElevation = requiresElevation,
        Canceled = canceled
    };
}

public sealed class BatchChangeResult
{
    public bool RequiresElevation { get; init; }
    public bool Canceled { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<ChangeResult> Results { get; init; } = Array.Empty<ChangeResult>();

    public int SucceededCount => Results.Count(result => result.Success);
    public int FailedCount => Results.Count(result => !result.Success);
    public bool AllSucceeded => Results.Count > 0 && Results.All(result => result.Success);
}
