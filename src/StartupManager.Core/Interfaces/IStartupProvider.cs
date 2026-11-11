using StartupManager.Core.Models;

namespace StartupManager.Core.Interfaces;

/// <summary>Read-only startup source.</summary>
public interface IStartupProvider
{
    StartupSource Source { get; }
    Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken);
}

/// <summary>Read/write startup source. Implementations must be stateless between calls.</summary>
public interface IStartupChangeProvider
{
    StartupSource Source { get; }

    Task<StateSnapshot> CaptureStateAsync(StartupItem item, CancellationToken cancellationToken);

    Task ApplyEnabledAsync(StartupItem item, bool enabled, bool isElevated, CancellationToken cancellationToken);

    /// <summary>Re-reads the OS state and returns whether the requested state is really active.</summary>
    Task<bool> VerifyEnabledAsync(StartupItem item, bool enabled, CancellationToken cancellationToken);

    /// <summary>Restores original state from the backup record. The item may no longer be in a scan.</summary>
    Task RestoreStateAsync(BackupRecord backup, bool isElevated, CancellationToken cancellationToken);
}
