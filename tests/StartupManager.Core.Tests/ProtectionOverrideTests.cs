using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Protection;
using StartupManager.Core.Services;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ProtectionOverrideTests
{
    [Theory]
    [InlineData(StartupSource.Service, "DiagTrack")]
    [InlineData(StartupSource.Service, "RpcSs")]
    [InlineData(StartupSource.Service, "WinDefend")]
    [InlineData(StartupSource.Task, "RecommendationTask")]
    [InlineData(StartupSource.Registry, "SecurityHealth")]
    [InlineData(StartupSource.Folder, "WindowsComponent")]
    public async Task Explicit_unlock_persists_and_keeps_backup_verification_uac_and_relock(
        StartupSource source, string name)
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(source, store.Events);
        var item = TestItems.Create(source, name: name, requiresAdmin: source is StartupSource.Service or StartupSource.Task);
        item.Metadata["ServiceName"] = name;
        item.Metadata["TaskPath"] = @"\Microsoft\Windows\Tests\" + name;
        if (source == StartupSource.Service) item.Metadata["ServiceType"] = "16";
        // Real protection rules, with only the OS mutation simulated.
        if (source is StartupSource.Registry or StartupSource.Folder) item.Metadata.Clear();
        var protectedItem = new StartupItem
        {
            Id = item.Id, Name = name, Source = source, Scope = item.Scope,
            Enabled = true, RequiresAdmin = item.RequiresAdmin,
            Publisher = "Microsoft Corporation", TargetPath = @"C:\Windows\System32\test.exe",
            Command = @"C:\Windows\System32\test.exe", Location = "服务：" + name,
            Metadata = item.Metadata
        };
        system.AddItem(protectedItem);
        StartupManagerService CreateManager() => new(new[] { system }, new[] { system },
            new StartupProtectionPolicy(), store, paths.Paths);
        var manager = CreateManager();
        var initial = Assert.Single((await manager.ScanAsync(CancellationToken.None)).Items);
        Assert.True(initial.Protected);
        Assert.True(initial.CanOverride);
        Assert.False((await manager.SetEnabledAsync(new[] { item.Id }, false, true, CancellationToken.None)).AllSucceeded);
        Assert.Equal(0, system.ApplyCalls);

        Assert.True((await manager.SetUserOverrideAsync(item.Id, true, CancellationToken.None)).Success);
        Assert.Equal(0, system.ApplyCalls);
        Assert.Equal(0, store.CreateCount);
        manager = CreateManager(); // Same permission in a freshly created elevated helper.
        var unlocked = Assert.Single((await manager.ScanAsync(CancellationToken.None)).Items);
        Assert.True(unlocked.UserAllowed);
        Assert.False(unlocked.Protected);
        Assert.True(unlocked.Enabled);
        if (item.RequiresAdmin)
        {
            Assert.True((await manager.SetEnabledAsync(new[] { item.Id }, false, false, CancellationToken.None)).RequiresElevation);
            Assert.Equal(0, system.ApplyCalls);
        }

        var result = await manager.SetEnabledAsync(new[] { item.Id }, false, true, CancellationToken.None);
        Assert.True(result.AllSucceeded);
        Assert.True(store.Events.IndexOf("backup:create") < store.Events.IndexOf("apply:disable"));
        Assert.True(system.VerifyCalls > 0);
        Assert.False(system.LiveItems.Single().Enabled);

        Assert.True((await manager.SetUserOverrideAsync(item.Id, false, CancellationToken.None)).Success);
        Assert.True(Assert.Single((await manager.ScanAsync(CancellationToken.None)).Items).Protected);
        Assert.False(system.LiveItems.Single().Enabled); // Relock changes no startup state.
        Assert.False((await manager.SetEnabledAsync(new[] { item.Id }, true, true, CancellationToken.None)).AllSucceeded);
        var backup = Assert.Single(store.Snapshot());
        Assert.True((await manager.RestoreBackupAsync(backup.Id, true, CancellationToken.None)).Success);
        Assert.True(system.LiveItems.Single().Enabled); // Recovery remains available while locked.
    }

    [Fact]
    public async Task Unlocked_item_still_rolls_back_when_os_write_is_denied()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var item = TestItems.Create(StartupSource.Service, requiresAdmin: true, name: "DiagTrack");
        item.Metadata["ServiceName"] = "RpcSs";
        system.AddItem(item);
        var manager = new StartupManagerService(new[] { system }, new[] { system }, new StartupProtectionPolicy(), store, paths.Paths);
        Assert.True((await manager.SetUserOverrideAsync(item.Id, true, CancellationToken.None)).Success);
        system.ApplyException = new UnauthorizedAccessException("Windows ACL refused the write");
        var result = await manager.SetEnabledAsync(new[] { item.Id }, false, true, CancellationToken.None);
        Assert.False(result.AllSucceeded);
        Assert.True(system.LiveItems.Single().Enabled);
        Assert.Equal(1, system.RestoreCalls);
        Assert.Equal(BackupStatus.Failed, Assert.Single(store.Snapshot()).Status);
    }

    [Fact]
    public async Task Synthetic_disabled_service_retains_permission_and_can_be_relocked()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var id = TestIds.NewId(StartupSource.Service);
        var item = TestItems.Create(StartupSource.Service, id, requiresAdmin: true, name: "RpcSs");
        item.Metadata["ServiceName"] = "RpcSs";
        system.AddItem(item);
        var manager = new StartupManagerService(new[] { system }, new[] { system }, new StartupProtectionPolicy(), store, paths.Paths);
        Assert.True((await manager.SetUserOverrideAsync(id, true, CancellationToken.None)).Success);
        Assert.True((await manager.SetEnabledAsync(new[] { id }, false, true, CancellationToken.None)).AllSucceeded);
        system.LiveItems.Clear();
        var row = Assert.Single((await manager.ScanAsync(CancellationToken.None)).Items);
        Assert.True(row.UserAllowed);
        Assert.True(row.CanOverride);
        Assert.False(row.Protected);
        Assert.True((await manager.SetUserOverrideAsync(id, false, CancellationToken.None)).Success);
        Assert.True(Assert.Single((await manager.ScanAsync(CancellationToken.None)).Items).Protected);
    }

    [Fact]
    public async Task Batch_unlock_and_relock_updates_all_items_without_extra_scans()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var services = new FakeStartupSystem(StartupSource.Service, store.Events);
        var tasks = new FakeStartupSystem(StartupSource.Task, store.Events);

        var serviceA = TestItems.Create(StartupSource.Service, name: "DiagTrack", requiresAdmin: true);
        serviceA.Metadata["ServiceName"] = "DiagTrack";
        serviceA.Metadata["ServiceType"] = "16";
        var serviceB = TestItems.Create(StartupSource.Service, name: "WpnUserService", requiresAdmin: true);
        serviceB.Metadata["ServiceName"] = "WpnUserService";
        serviceB.Metadata["ServiceType"] = "16";
        var task = TestItems.Create(StartupSource.Task, name: "SystemTask", requiresAdmin: true);
        task.Metadata["TaskPath"] = @"\Microsoft\Windows\Tests\SystemTask";
        services.AddItem(serviceA);
        services.AddItem(serviceB);
        tasks.AddItem(task);

        var policy = new FakeProtectionPolicy
        {
            Rule = _ => new ProtectionDecision(true, "工具保护", true, CanOverride: true)
        };
        var manager = new StartupManagerService(
            new IStartupProvider[] { services, tasks },
            new IStartupChangeProvider[] { services, tasks },
            policy,
            store,
            paths.Paths);

        var initial = await manager.ScanAsync(CancellationToken.None);
        Assert.Equal(3, initial.ProtectedCount);
        var scansBefore = services.ScanCalls + tasks.ScanCalls;
        var ids = initial.Items.Select(item => item.Id).ToArray();

        var unlock = await manager.SetUserOverridesAsync(ids, allowed: true, CancellationToken.None);

        Assert.True(unlock.AllSucceeded, unlock.Message);
        Assert.Equal(3, unlock.SucceededCount);
        Assert.Equal(scansBefore, services.ScanCalls + tasks.ScanCalls);
        Assert.Equal(3, manager.Settings.UserAllowedItemIds.Count);

        // The cached rows the host publishes are already unlocked.
        var cached = manager.CachedSnapshot!;
        Assert.All(cached.Items, item =>
        {
            Assert.False(item.Protected);
            Assert.True(item.UserAllowed);
        });

        var relock = await manager.SetUserOverridesAsync(ids.Take(2).ToArray(), allowed: false, CancellationToken.None);

        Assert.True(relock.AllSucceeded, relock.Message);
        Assert.Single(manager.Settings.UserAllowedItemIds);
        Assert.Equal(2, manager.CachedSnapshot!.ProtectedCount);
        Assert.Equal(1, manager.CachedSnapshot!.Items.Count(item => item.UserAllowed));
    }

    [Fact]
    public async Task Batch_unlock_reports_entries_that_windows_policy_keeps_locked()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var services = new FakeStartupSystem(StartupSource.Service, store.Events);

        var overridable = TestItems.Create(StartupSource.Service, name: "ThirdPartyService");
        overridable.Metadata["ServiceName"] = "ThirdPartyService";
        overridable.Metadata["ServiceType"] = "16";
        var policyLocked = TestItems.Create(StartupSource.Service, name: "PolicyLocked");
        policyLocked.Metadata["ServiceName"] = "PolicyLocked";
        policyLocked.Metadata["ServiceType"] = "16";
        services.AddItem(overridable);
        services.AddItem(policyLocked);

        var policy = new FakeProtectionPolicy
        {
            Rule = item => item.Name == "PolicyLocked"
                ? new ProtectionDecision(true, "Windows 策略限制", true, CanOverride: false)
                : new ProtectionDecision(true, "工具保护", true, CanOverride: true)
        };
        var manager = new StartupManagerService(
            new IStartupProvider[] { services },
            new IStartupChangeProvider[] { services },
            policy,
            store,
            paths.Paths);

        var initial = await manager.ScanAsync(CancellationToken.None);
        var ids = initial.Items.Select(item => item.Id).ToArray();

        var result = await manager.SetUserOverridesAsync(ids, allowed: true, CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("未更新", result.Message);
        var allowedId = Assert.Single(manager.Settings.UserAllowedItemIds);
        Assert.Equal(overridable.Id, allowedId);
    }

    [Fact]
    public async Task Restore_verification_scans_only_the_backups_source()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var services = new FakeStartupSystem(StartupSource.Service, store.Events);
        var registry = new FakeStartupSystem(StartupSource.Registry, store.Events);
        var service = TestItems.Create(StartupSource.Service, enabled: false, requiresAdmin: true);
        var registryItem = TestItems.Create(StartupSource.Registry, name: "OtherSource");
        services.AddItem(service);
        registry.AddItem(registryItem);

        var backup = TestBackups.Create(StartupSource.Service, service.Id, requiresAdmin: true);
        store.Add(backup);

        var manager = new StartupManagerService(
            new IStartupProvider[] { services, registry },
            new IStartupChangeProvider[] { services, registry },
            new FakeProtectionPolicy(),
            store,
            paths.Paths);

        await manager.ScanAsync(CancellationToken.None);
        var serviceScans = services.ScanCalls;
        var registryScans = registry.ScanCalls;

        var result = await manager.RestoreBackupAsync(backup.Id, isElevated: true, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verified);
        Assert.Equal(serviceScans + 1, services.ScanCalls);
        Assert.Equal(registryScans, registry.ScanCalls);
    }

    [Fact]
    public async Task Windows_policy_and_failed_safety_check_cannot_be_unlocked()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.PackagedApp, store.Events);
        var item = TestItems.Create(StartupSource.PackagedApp);
        item.Protected = true;
        item.ProtectionReason = "Windows 策略限制";
        system.AddItem(item);
        var manager = new StartupManagerService(new[] { system }, new[] { system }, new StartupProtectionPolicy(), store, paths.Paths);
        Assert.False((await manager.SetUserOverrideAsync(item.Id, true, CancellationToken.None)).Success);
        var failedPolicy = new FakeProtectionPolicy { Rule = _ => throw new InvalidOperationException("unreadable") };
        manager = new StartupManagerService(new[] { system }, new[] { system }, failedPolicy, store, paths.Paths);
        Assert.False((await manager.SetUserOverrideAsync(item.Id, true, CancellationToken.None)).Success);
        Assert.Empty(manager.Settings.UserAllowedItemIds);
    }

    [Fact]
    public async Task Invalid_unknown_ids_are_rejected_and_stale_permission_can_be_removed()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service);
        var manager = new StartupManagerService(new[] { system }, new[] { system }, new StartupProtectionPolicy(), store, paths.Paths);
        Assert.False((await manager.SetUserOverrideAsync("invalid", true, CancellationToken.None)).Success);
        var id = TestIds.NewId(StartupSource.Service);
        Assert.False((await manager.SetUserOverrideAsync(id, true, CancellationToken.None)).Success);
        await manager.SaveSettingsAsync(new AppSettings { UserAllowedItemIds = new() { id } }, CancellationToken.None);
        Assert.True((await manager.SetUserOverrideAsync(id, false, CancellationToken.None)).Success);
        Assert.Empty(manager.Settings.UserAllowedItemIds);
    }
}
