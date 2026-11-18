using System.Text.RegularExpressions;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

/// <summary>
/// Validates the stable ids defined in docs/CONTRACT.md:
/// reg-/dir-/tsk-/svc-/pkg- followed by a SHA-256 hex digest.
/// </summary>
internal static partial class StartupItemIds
{
    [GeneratedRegex("^(reg|dir|tsk|svc|pkg)-[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemIdRegex();

    public static bool IsValid(string? itemId)
    {
        return !string.IsNullOrEmpty(itemId) && ItemIdRegex().IsMatch(itemId);
    }

    /// <summary>Maps an id prefix to its provider source; used to limit scans.</summary>
    public static StartupSource? TryGetSource(string? itemId)
    {
        if (!IsValid(itemId))
        {
            return null;
        }

        return itemId!.Substring(0, 3) switch
        {
            "reg" => StartupSource.Registry,
            "dir" => StartupSource.Folder,
            "tsk" => StartupSource.Task,
            "svc" => StartupSource.Service,
            "pkg" => StartupSource.PackagedApp,
            _ => null
        };
    }
}
