using System.Text.Json;
using StartupManager.Core.Backup;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

/// <summary>Loads and atomically saves settings.json.</summary>
internal static class SettingsStore
{
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 3650;

    public static AppSettings Load(string? settingsFile, Action<string>? log)
    {
        var defaults = new AppSettings();

        if (string.IsNullOrWhiteSpace(settingsFile))
        {
            log?.Invoke("设置文件路径为空，使用默认设置。");
            return defaults;
        }

        try
        {
            if (!File.Exists(settingsFile))
            {
                return defaults;
            }

            var json = File.ReadAllText(settingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options);
            if (settings is null)
            {
                log?.Invoke("设置文件内容为空，使用默认设置。");
                return defaults;
            }

            return Normalize(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        {
            log?.Invoke($"读取设置失败，使用默认设置：{ex.GetType().Name}。");
            return defaults;
        }
    }

    public static async Task SaveAsync(string settingsFile, AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        ArgumentNullException.ThrowIfNull(settings);

        var json = JsonSerializer.Serialize(settings, JsonDefaults.Indented);
        await AtomicFile.WriteAllTextAsync(
                settingsFile,
                json,
                restrictToCurrentUser: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        var copy = Clone(settings);
        if (copy.BackupRetentionDays < 0)
        {
            copy.BackupRetentionDays = DefaultRetentionDays;
        }
        else if (copy.BackupRetentionDays > MaxRetentionDays)
        {
            copy.BackupRetentionDays = MaxRetentionDays;
        }

        if (!double.IsFinite(copy.UiFontScale))
        {
            copy.UiFontScale = 1.0;
        }

        copy.UiFontScale = Math.Clamp(copy.UiFontScale, 0.8, 1.5);
        copy.UserAllowedItemIds = (copy.UserAllowedItemIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return copy;
    }

    public static AppSettings Clone(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettings
        {
            IncludeRegistry = settings.IncludeRegistry,
            IncludeStartupFolders = settings.IncludeStartupFolders,
            IncludeScheduledTasks = settings.IncludeScheduledTasks,
            IncludeServices = settings.IncludeServices,
            StartWithWindows = settings.StartWithWindows,
            MinimizeToTray = settings.MinimizeToTray,
            BackupRetentionDays = settings.BackupRetentionDays,
            UiFontScale = settings.UiFontScale,
            UserAllowedItemIds = (settings.UserAllowedItemIds ?? new List<string>()).ToList()
        };
    }
}
