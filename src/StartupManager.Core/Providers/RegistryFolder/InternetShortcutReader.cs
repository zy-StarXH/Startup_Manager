using System.Security;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>Reads the URL out of an Internet Shortcut (.url) file.</summary>
public static class InternetShortcutReader
{
    public static string? TryReadUrl(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = trimmed[4..].Trim();
                    if (url.Length > 0)
                    {
                        return url;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Unreadable shortcut: caller falls back to the file path.
        }

        return null;
    }
}
