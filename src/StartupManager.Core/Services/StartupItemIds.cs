using System.Text.RegularExpressions;

namespace StartupManager.Core.Services;

/// <summary>
/// Validates the stable ids defined in docs/CONTRACT.md:
/// reg-/dir-/tsk-/svc- followed by a SHA-256 hex digest.
/// </summary>
internal static partial class StartupItemIds
{
    [GeneratedRegex("^(reg|dir|tsk|svc)-[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdRegex();

    public static bool IsValid(string? itemId)
    {
        return !string.IsNullOrEmpty(itemId) && ItemIdRegex().IsMatch(itemId);
    }
}
