using System.Text;

namespace StartupManager.Core.Services;

/// <summary>
/// Append-only operation log. Never receives provider state JSON or commands;
/// callers pass ids, names, and short human-readable results only.
/// </summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void Append(string logDirectory, string operation, string? item, string result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logDirectory))
            {
                return;
            }

            Directory.CreateDirectory(logDirectory);
            var line = string.Concat(
                DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                "\t",
                Sanitize(operation),
                "\t",
                Sanitize(item ?? "-"),
                "\t",
                Sanitize(result),
                Environment.NewLine);

            lock (Gate)
            {
                File.AppendAllText(Path.Combine(logDirectory, "startupmanager.log"), line, Utf8NoBom);
            }
        }
        catch (Exception)
        {
            // Logging is best effort.
        }
    }

    private static string Sanitize(string value)
    {
        if (value.Length > 500)
        {
            value = value[..500];
        }

        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }
}
