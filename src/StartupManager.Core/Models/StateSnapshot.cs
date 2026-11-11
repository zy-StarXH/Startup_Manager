namespace StartupManager.Core.Models;

/// <summary>Opaque, provider-owned snapshot needed to undo a change.</summary>
public sealed class StateSnapshot
{
    public string Kind { get; init; } = string.Empty;
    public string Json { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}
