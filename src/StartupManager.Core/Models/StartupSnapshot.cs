namespace StartupManager.Core.Models;

public sealed class StartupSnapshot
{
    public IReadOnlyList<StartupItem> Items { get; init; } = Array.Empty<StartupItem>();
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool IsElevated { get; init; }

    public int EnabledCount => Items.Count(item => item.Enabled);
    public int DisabledCount => Items.Count(item => !item.Enabled);
    public int ProtectedCount => Items.Count(item => item.Protected);
    public int AdminCount => Items.Count(item => !item.Protected && item.RequiresAdmin);
}
