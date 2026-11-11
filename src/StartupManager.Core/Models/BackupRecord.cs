namespace StartupManager.Core.Models;

/// <summary>Persisted record of one reversible change.</summary>
public sealed class BackupRecord
{
    public string Id { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public StartupSource Source { get; init; }
    public StartupScope Scope { get; init; }
    public bool RequiresAdmin { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RestoredAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public BackupStatus Status { get; set; } = BackupStatus.Active;
    public string Summary { get; init; } = string.Empty;
    public string? Error { get; set; }

    /// <summary>Provider-defined state kind, e.g. registryApproval, taskState, serviceStartType.</summary>
    public string StateKind { get; init; } = string.Empty;

    /// <summary>Provider-owned JSON snapshot of the original state. Do not expose to the UI.</summary>
    public string StateJson { get; init; } = string.Empty;

    public BackupSummary ToSummary() => new()
    {
        Id = Id,
        ItemId = ItemId,
        Source = Source,
        Scope = Scope,
        RequiresAdmin = RequiresAdmin,
        DisplayName = DisplayName,
        Location = Location,
        Command = Command,
        CreatedAt = CreatedAt,
        RestoredAt = RestoredAt,
        Status = Status,
        Summary = Summary,
        Error = Error
    };
}

public sealed class BackupSummary
{
    public string Id { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public StartupSource Source { get; init; }
    public StartupScope Scope { get; init; }
    public bool RequiresAdmin { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RestoredAt { get; init; }
    public BackupStatus Status { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string? Error { get; init; }
}
