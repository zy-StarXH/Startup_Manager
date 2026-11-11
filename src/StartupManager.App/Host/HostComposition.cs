using StartupManager.Core;
using StartupManager.Core.Elevation;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>
/// INTEGRATION SEAM for the lead agent.
///
/// The app deliberately does not construct providers itself. CoreComposition is
/// owned by the lead; until it is wired, <see cref="CoreComposition.CreateManager"/>
/// throws and the host reports an unavailable-backend error to the web UI instead
/// of inventing demo data.
///
/// To wire the elevation helper once workstream C lands, assign:
///   HostComposition.ElevationCoordinatorFactory = () => new ElevationCoordinator(AppPaths.CreateDefault());
/// (or whatever concrete type/implements IElevationCoordinator). Until it is set,
/// every command that needs UAC fails cleanly without launching a helper.
/// </summary>
internal static class HostComposition
{
    /// <summary>
    /// INTEGRATION SEAM: defaults to the Core composition root. The lead may replace
    /// this with their own factory if they compose providers in another place.
    /// </summary>
    public static Func<IStartupManager> ManagerFactory { get; set; } = CoreComposition.CreateManager;

    /// <summary>
    /// One-shot UAC coordinator. The helper executable is copied next to the app
    /// by the publish script; the coordinator resolves it from AppContext.BaseDirectory.
    /// </summary>
    public static Func<IElevationCoordinator?> ElevationCoordinatorFactory { get; set; } =
        () => new ElevationCoordinator(AppPaths.CreateDefault());

    public static IStartupManager CreateManager() => ManagerFactory();

    public static IElevationCoordinator? CreateElevationCoordinator() => ElevationCoordinatorFactory?.Invoke();
}
