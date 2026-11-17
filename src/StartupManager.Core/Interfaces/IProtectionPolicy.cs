using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

/// <summary>
/// <paramref name="CanOverride"/> allows explicit per-item management of a
/// tool-protected entry. Windows policy restrictions and failed safety checks
/// cannot be overridden by this setting.
/// </summary>
public sealed record ProtectionDecision(
    bool IsProtected,
    string? Reason,
    bool RequiresAdmin,
    bool CanOverride = false);

public interface IProtectionPolicy
{
    ProtectionDecision Evaluate(StartupItem item);
}
