using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>
/// Lazily resolves the Core facade and the elevation coordinator. Resolution
/// failures are cached and surfaced as a readable error instead of crashing.
/// </summary>
internal sealed class HostBackend
{
    private readonly object _sync = new();
    private bool _managerResolved;
    private IStartupManager? _manager;
    private string? _managerError;
    private bool _elevationResolved;
    private IElevationCoordinator? _elevation;

    public HostBackend()
    {
        Paths = AppPaths.CreateDefault();
        try
        {
            Paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not create the application data directories.", ex);
        }
    }

    public AppPaths Paths { get; }

    public bool IsElevated => ProcessElevation.IsElevated;

    public IStartupManager? Manager
    {
        get
        {
            lock (_sync)
            {
                if (_managerResolved)
                {
                    return _manager;
                }

                _managerResolved = true;
                try
                {
                    _manager = HostComposition.CreateManager();
                    AppLog.Info($"IStartupManager resolved: {_manager.GetType().FullName}");
                }
                catch (Exception ex)
                {
                    _managerError = ex.Message;
                    AppLog.Error("IStartupManager is unavailable.", ex);
                }

                return _manager;
            }
        }
    }

    public string? ManagerError
    {
        get
        {
            _ = Manager;
            lock (_sync)
            {
                return _managerError;
            }
        }
    }

    public IElevationCoordinator? Elevation
    {
        get
        {
            lock (_sync)
            {
                if (_elevationResolved)
                {
                    return _elevation;
                }

                _elevationResolved = true;
                try
                {
                    _elevation = HostComposition.CreateElevationCoordinator();
                    if (_elevation is not null)
                    {
                        AppLog.Info($"IElevationCoordinator resolved: {_elevation.GetType().FullName}");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("IElevationCoordinator is unavailable.", ex);
                }

                return _elevation;
            }
        }
    }
}
