using StartupManager.Core.Elevation;

namespace StartupManager.Core.Interfaces;

public interface IElevationCoordinator
{
    /// <summary>Launches StartupManager.Elevated.exe via UAC, waits for the one-shot result.</summary>
    Task<ElevatedResponse> ExecuteElevatedAsync(ElevatedRequest request, CancellationToken cancellationToken);
}
