namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Extracts the executable path from a Run value for display and publisher lookup.
/// The command is never executed. Only rooted paths are returned; bare executable
/// names such as "rundll32.exe" are intentionally reported as unknown.
/// </summary>
public static class CommandLineParser
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".com", ".bat", ".cmd" };

    public static string? TryGetExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.Length == 0)
        {
            return null;
        }

        var token = ExtractFirstToken(expanded).Trim().Trim('"').Trim();
        if (token.Length == 0 || !IsRootedPath(token))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(token);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return token;
        }
    }

    private static string ExtractFirstToken(string command)
    {
        if (command[0] == '"')
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : command[1..];
        }

        // Unquoted paths may contain spaces. Prefer the first known executable
        // extension when it is followed by whitespace, a quote, a comma, or the end.
        var bestIndex = int.MaxValue;
        var bestEnd = -1;
        foreach (var extension in ExecutableExtensions)
        {
            var index = command.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            while (index >= 0 && index < bestIndex)
            {
                var end = index + extension.Length;
                var boundary = end == command.Length ||
                               char.IsWhiteSpace(command[end]) ||
                               command[end] is '"' or ',';
                if (boundary)
                {
                    bestIndex = index;
                    bestEnd = end;
                    break;
                }

                index = command.IndexOf(extension, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (bestEnd > 0)
        {
            return command[..bestEnd];
        }

        var whitespace = command.IndexOfAny(new[] { ' ', '\t' });
        return whitespace > 0 ? command[..whitespace] : command;
    }

    private static bool IsRootedPath(string path)
    {
        try
        {
            return Path.IsPathRooted(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
