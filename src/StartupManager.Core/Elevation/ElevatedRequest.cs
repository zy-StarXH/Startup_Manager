namespace StartupManager.Core.Elevation;

public static class ElevatedOperations
{
    public const string SetEnabled = "setEnabled";
    public const string RestoreBackup = "restoreBackup";
}

/// <summary>
/// One-shot request handed to StartupManager.Elevated.exe.
/// The helper rescans the machine and only accepts item IDs; it never trusts
/// paths or arbitrary commands supplied by the UI.
/// </summary>
public sealed class ElevatedRequest
{
    public int Version { get; set; } = 1;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Nonce { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.Now.AddMinutes(5);
    public string Operation { get; set; } = ElevatedOperations.SetEnabled;
    public List<string> ItemIds { get; set; } = new();
    public bool Enabled { get; set; }
    public string? BackupId { get; set; }
}

public sealed class ElevatedResponse
{
    public bool Success { get; set; }
    public bool Canceled { get; set; }
    public string? Message { get; set; }
    public List<StartupManager.Core.Models.ChangeResult> Results { get; set; } = new();
}
