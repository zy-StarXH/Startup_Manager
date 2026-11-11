namespace StartupManager.Core.Protection;

/// <summary>
/// Path helpers shared by the providers and the protection policy.
/// These helpers never execute, start, or open the files they inspect.
/// </summary>
internal static class WindowsPathHeuristics
{
    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/');

    /// <summary>Expands environment variables without throwing on malformed input.</summary>
    public static string ExpandEnvironment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        try
        {
            return Environment.ExpandEnvironmentVariables(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>
    /// True when the path lives in the Windows directory (including %windir% and
    /// \SystemRoot\ forms) or in System32/SysWOW64/DriverStore of a Windows install.
    /// </summary>
    public static bool IsUnderWindowsDirectory(string? path)
    {
        var value = ExpandEnvironment(path);
        if (value.Length == 0)
        {
            return false;
        }

        if (value.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\??\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        value = value.Replace('/', '\\');
        if (WindowsDirectory.Length > 0 &&
            value.StartsWith(WindowsDirectory + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fall back to a segment check so synthetic paths and relocated Windows
        // directories are still recognised.
        return value.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
            || value.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
            || value.Contains(@"\Windows\System32", StringComparison.OrdinalIgnoreCase)
            || value.Contains(@"\Windows\SysWOW64", StringComparison.OrdinalIgnoreCase)
            || value.Contains(@"\Windows\DriverStore\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for driver binaries or INF DriverStore payloads.</summary>
    public static bool IsDriverStorePath(string? path)
    {
        var value = ExpandEnvironment(path).Replace('/', '\\');
        return value.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDriverFilePath(string? path)
    {
        var value = ExpandEnvironment(path).Replace('/', '\\');
        return value.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            || value.Contains(@"\System32\drivers\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts the executable from a display-only command line. Never executes it.</summary>
    public static string? ExtractExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var raw = command.Trim();
        if (raw.StartsWith('"'))
        {
            var close = raw.IndexOf('"', 1);
            return close > 1 ? raw.Substring(1, close - 1).Trim() : raw.Trim('"').Trim();
        }

        var exeIndex = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return raw.Substring(0, exeIndex + 4).Trim();
        }

        var space = raw.IndexOfAny(new[] { ' ', '\t' });
        return space > 0 ? raw.Substring(0, space).Trim() : raw;
    }

    public static bool ContainsToken(string? text, string token)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var beforeOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var after = found + token.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }

    public static bool IsMicrosoftPublisherName(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return false;
        }

        return publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            || publisher.Contains("微软", StringComparison.Ordinal)
            || publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
    }
}
