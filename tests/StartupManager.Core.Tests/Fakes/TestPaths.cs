using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

/// <summary>Isolated temp AppPaths for store/service/elevation tests.</summary>
public sealed class TestPaths : IDisposable
{
    public string Root { get; }

    public AppPaths Paths { get; }

    public TestPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), "StartupManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Paths = new AppPaths
        {
            DataDirectory = Root,
            BackupDirectory = Path.Combine(Root, "Backups"),
            LogDirectory = Path.Combine(Root, "Logs"),
            IpcDirectory = Path.Combine(Root, "Ipc"),
            SettingsFile = Path.Combine(Root, "settings.json")
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup of temp test data.
        }
    }
}
