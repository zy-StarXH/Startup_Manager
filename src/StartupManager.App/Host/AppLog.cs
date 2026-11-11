using System.IO;
using System.Text;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>Very small append-only file log. Never throws into the UI.</summary>
internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = AppPaths.CreateDefault().LogDirectory;
    private static bool _directoryReady;

    public static string DirectoryPath => LogDirectory;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static string CurrentFile => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                if (!_directoryReady)
                {
                    Directory.CreateDirectory(LogDirectory);
                    _directoryReady = true;
                }

                var builder = new StringBuilder();
                builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message);
                if (exception is not null)
                {
                    builder.AppendLine().Append(exception);
                }

                File.AppendAllText(CurrentFile, builder.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
