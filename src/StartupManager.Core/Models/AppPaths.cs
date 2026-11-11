namespace StartupManager.Core.Models;

public sealed class AppPaths
{
    public string DataDirectory { get; init; } = string.Empty;
    public string BackupDirectory { get; init; } = string.Empty;
    public string LogDirectory { get; init; } = string.Empty;
    public string IpcDirectory { get; init; } = string.Empty;
    public string SettingsFile { get; init; } = string.Empty;

    public static AppPaths CreateDefault()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(local, "StartupManager");
        return new AppPaths
        {
            DataDirectory = data,
            BackupDirectory = Path.Combine(data, "Backups"),
            LogDirectory = Path.Combine(data, "Logs"),
            IpcDirectory = Path.Combine(data, "Ipc"),
            SettingsFile = Path.Combine(data, "settings.json")
        };
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(IpcDirectory);
    }
}
