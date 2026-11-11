using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

/// <summary>
/// <paramref name="CanOverride"/> is true only for items that are locked because
/// their publisher/path could not be verified. OS-critical, driver, security and
/// Microsoft-owned items are never overridable.
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
