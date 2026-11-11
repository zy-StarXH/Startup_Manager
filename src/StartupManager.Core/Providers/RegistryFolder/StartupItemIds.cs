using System.Security.Cryptography;
using System.Text;
using StartupManager.Core.Models;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Stable identifiers for registry Run values and Startup-folder files.
/// Canonical strings match docs/CONTRACT.md:
/// registry = hive|view|keyPath|valueName, folder = scope|fullPath.
/// </summary>
public static class StartupItemIds
{
    public const string RegistryPrefix = "reg-";
    public const string FolderPrefix = "dir-";

    public static string CreateRegistryId(string hive, string view, string keyPath, string valueName) =>
        Compute(RegistryPrefix, $"{hive}|{view}|{keyPath}|{valueName}");

    public static string CreateFolderId(StartupScope scope, string fullPath) =>
        Compute(FolderPrefix, $"{scope}|{fullPath}");

    /// <summary>SHA-256 as lowercase hex, prefixed with <paramref name="prefix"/>.</summary>
    public static string Compute(string prefix, string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
