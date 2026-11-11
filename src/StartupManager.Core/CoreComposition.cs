using StartupManager.Core.Backup;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Protection;
using StartupManager.Core.Providers;
using StartupManager.Core.Services;

namespace StartupManager.Core;

/// <summary>
/// The single integration point for the desktop host and the elevated helper.
/// Constructs the real provider set, protection policy, backup store, and
/// transactional manager against the current user's profile.
/// </summary>
public static class CoreComposition
{
    public static IStartupManager CreateManager()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        var registry = new RegistryStartupProvider();
        var startupFolder = new StartupFolderProvider();
        var scheduledTasks = new ScheduledTaskProvider();
        var services = new ServiceStartupProvider();

        var providers = new IStartupProvider[]
        {
            registry,
            startupFolder,
            scheduledTasks,
            services
        };

        var changeProviders = new IStartupChangeProvider[]
        {
            registry,
            startupFolder,
            scheduledTasks,
            services
        };

        var backupStore = new JsonBackupStore(paths);
        return new StartupManagerService(
            providers,
            changeProviders,
            new StartupProtectionPolicy(),
            backupStore,
            paths);
    }
}
