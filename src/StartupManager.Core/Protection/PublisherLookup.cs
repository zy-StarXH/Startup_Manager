using System.Diagnostics;

namespace StartupManager.Core.Protection;

/// <summary>
/// Read-only publisher lookup for provider metadata. Uses only FileVersionInfo;
/// the file is never opened for execution.
/// </summary>
internal static class PublisherLookup
{
    public static string TryGetPublisher(string? executablePath)
    {
        var normalized = NormalizePath(executablePath);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

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
}
