using System.Collections.Concurrent;
using System.Diagnostics;

namespace StartupManager.Core.Protection;

/// <summary>
/// Read-only publisher lookup for provider metadata. Uses only FileVersionInfo;
/// the file is never opened for execution. Results are cached briefly because a
/// full scan asks for the same binaries repeatedly and version-resource reads
/// can be slow when antivirus filters first access.
/// </summary>
internal static class PublisherLookup
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string TryGetPublisher(string? executablePath)
    {
        var normalized = NormalizePath(executablePath);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var now = Environment.TickCount64;
        if (Cache.TryGetValue(normalized, out var cached) && now - cached.StoredAt < CacheLifetime.TotalMilliseconds)
        {
            return cached.Publisher;
        }

        var publisher = ReadPublisher(normalized);
        Cache[normalized] = new CacheEntry(publisher, now);
        return publisher;
    }

    private static string ReadPublisher(string normalized)
    {
        try
        {
            if (!File.Exists(normalized))
            {
                return string.Empty;
            }

            var info = FileVersionInfo.GetVersionInfo(normalized);
            return (info.CompanyName ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var value = executablePath.Trim().Trim('"');
        if (value.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            value = value.Substring(4);
        }

        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            value = "%SystemRoot%\\" + value.Substring(@"\SystemRoot\".Length);
        }
        else if (value.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            value = "%SystemRoot%\\" + value;
        }

        return WindowsPathHeuristics.ExpandEnvironment(value);
    }

    private readonly record struct CacheEntry(string Publisher, long StoredAt);
}
