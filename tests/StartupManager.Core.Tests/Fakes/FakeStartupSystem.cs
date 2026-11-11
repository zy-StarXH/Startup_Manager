using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

/// <summary>
/// A provider + change provider backed by an in-memory list. Every scan returns
/// clones, like a real provider; Apply/Restore mutate the backing list so a
/// rescan observes verified state.
/// </summary>
public sealed class FakeStartupSystem : IStartupProvider, IStartupChangeProvider
{
    private readonly List<StartupItem> _liveItems = new();

    public FakeStartupSystem(StartupSource source, List<string>? events = null)
    {
        Source = source;
        Events = events ?? new List<string>();
    }

    public StartupSource Source { get; }

    public List<string> Events { get; }

    public List<StartupItem> LiveItems => _liveItems;

    public Exception? ScanException { get; set; }

    public Exception? CaptureException { get; set; }

    public Exception? ApplyException { get; set; }

    public Exception? VerifyException { get; set; }

    public Exception? RestoreException { get; set; }

    public bool VerifyResult { get; set; } = true;

    public bool ApplyMutates { get; set; } = true;

    public bool RestoreMutates { get; set; } = true;

    public StateSnapshot CapturedState { get; set; } = new()
    {
        Kind = "testState",
        Json = "{\"enabled\":true}",
        Summary = "原始状态：启用"
    };

    public int ScanCalls { get; private set; }

    public int CaptureCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public int VerifyCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public List<(string ItemId, bool Enabled, bool IsElevated)> AppliedChanges { get; } = new();

    public List<BackupRecord> RestoredBackups { get; } = new();

    public void AddItem(StartupItem item) => _liveItems.Add(item);

    public Task<IReadOnlyList<StartupItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCalls++;
        if (ScanException is not null)
        {
            throw ScanException;
        }

        IReadOnlyList<StartupItem> items = _liveItems.Select(TestItems.Clone).ToList();
        return Task.FromResult(items);
    }

    public Task<StateSnapshot> CaptureStateAsync(StartupItem item, CancellationToken cancellationToken)
    {
        CaptureCalls++;
        if (CaptureException is not null)
        {
            throw CaptureException;
        }

        Events.Add("capture");
        return Task.FromResult(CapturedState);
    }

    public Task ApplyEnabledAsync(
        StartupItem item,
        bool enabled,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        ApplyCalls++;
        if (ApplyException is not null)
        {
            throw ApplyException;
        }

        Events.Add(enabled ? "apply:enable" : "apply:disable");
        AppliedChanges.Add((item.Id, enabled, isElevated));

        if (ApplyMutates)
        {
            var live = _liveItems.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (live is not null)
            {
                live.Enabled = enabled;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> VerifyEnabledAsync(
        StartupItem item,
        bool enabled,
        CancellationToken cancellationToken)
    {
        VerifyCalls++;
        if (VerifyException is not null)
        {
            throw VerifyException;
        }

        return Task.FromResult(VerifyResult);
    }

    public Task RestoreStateAsync(
        BackupRecord backup,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        RestoreCalls++;
        if (RestoreException is not null)
        {
            throw RestoreException;
        }

        Events.Add("restore");
        RestoredBackups.Add(backup);

        if (RestoreMutates)
        {
            var live = _liveItems.FirstOrDefault(candidate => candidate.Id == backup.ItemId);
            if (live is not null)
            {
                live.Enabled = true;
            }
            else
            {
                _liveItems.Add(new StartupItem
                {
                    Id = backup.ItemId,
                    Name = backup.DisplayName,
                    Source = backup.Source,
                    Scope = backup.Scope,
                    Enabled = true,
                    Location = backup.Location,
                    Command = backup.Command
                });
            }
        }

        return Task.CompletedTask;
    }
}
