using System.Diagnostics;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Resolves a display publisher from the executable's version resource.
/// Reading version info never executes the file.
/// </summary>
public static class PublisherResolver
{
    public const string UnknownPublisher = "未知发布者";

    public static string Resolve(
        string? executablePath,
        IDictionary<string, string>? cache = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return UnknownPublisher;
        }

        if (cache is not null && cache.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        var publisher = ReadPublisher(executablePath);
        if (cache is not null)
        {
            cache[executablePath] = publisher;
        }

        return publisher;
    }

    private static string ReadPublisher(string executablePath)
    {
        try
        {
            if (!File.Exists(executablePath))
            {
                return UnknownPublisher;
            }

            var info = FileVersionInfo.GetVersionInfo(executablePath);
            var company = info.CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? UnknownPublisher : company;
        }
        catch (Exception)
        {
            // Invalid PE files, access denied, and shell links all fall back here.
            return UnknownPublisher;
        }
    }
}
