using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Services;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ServiceScanTests
{
    [Fact]
    public async Task Service_scan_merges_backup_only_service_as_synthetic_disabled_item()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var policy = new FakeProtectionPolicy
        {
            Rule = item => new ProtectionDecision(false, null, item.Source == StartupSource.Service)
        };
        var manager = CreateManager(paths, new IStartupProvider[] { system }, new IStartupChangeProvider[] { system }, policy, store);

        var itemId = TestIds.NewId(StartupSource.Service);
        var backup = TestBackups.Create(
            StartupSource.Service,
            itemId,
            requiresAdmin: true,
            displayName: "TeamViewer 服务");
        store.Add(backup);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(itemId, item.Id);
        Assert.Equal("TeamViewer 服务", item.Name);
        Assert.Equal(StartupSource.Service, item.Source);
        Assert.Equal(StartupScope.System, item.Scope);
        Assert.False(item.Enabled);
        Assert.True(item.ManagedByApp);
        Assert.True(item.RequiresAdmin);
        Assert.Equal("serviceStartType", item.Mechanism);
        Assert.Equal(backup.Location, item.Location);
        Assert.Equal(backup.Command, item.Command);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task Service_scan_marks_live_item_managed_when_active_backup_exists()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events);
        var itemId = TestIds.NewId(StartupSource.Registry);
        system.AddItem(TestItems.Create(StartupSource.Registry, itemId, enabled: true));
        store.Add(TestBackups.Create(StartupSource.Registry, itemId));

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        var item = Assert.Single(snapshot.Items);
        Assert.True(item.ManagedByApp);
        Assert.True(item.Enabled);
    }

    [Fact]
    public async Task Service_scan_hides_backup_only_items_for_categories_disabled_in_settings()
    {
        using var paths = new TestPaths();
        var settings = new AppSettings { IncludeServices = false };
        await File.WriteAllTextAsync(
            paths.Paths.SettingsFile,
            JsonSerializer.Serialize(settings, JsonDefaults.Options));

        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        store.Add(TestBackups.Create(StartupSource.Service, TestIds.NewId(StartupSource.Service)));

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public async Task Service_scan_does_not_duplicate_synthetic_for_multiple_active_backups()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var itemId = TestIds.NewId(StartupSource.Service);
        store.Add(TestBackups.Create(StartupSource.Service, itemId, createdAt: DateTimeOffset.Now.AddDays(-2)));
        store.Add(TestBackups.Create(StartupSource.Service, itemId, createdAt: DateTimeOffset.Now.AddDays(-1)));

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        Assert.Single(snapshot.Items);
        Assert.True(snapshot.Items[0].ManagedByApp);
    }

    [Fact]
    public async Task Service_scan_skips_categories_disabled_in_settings()
    {
        using var paths = new TestPaths();
        var settings = new AppSettings { IncludeServices = false };
        await File.WriteAllTextAsync(
            paths.Paths.SettingsFile,
            JsonSerializer.Serialize(settings, JsonDefaults.Options));

        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        system.AddItem(TestItems.Create(StartupSource.Service));

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Items);
        Assert.Equal(0, system.ScanCalls);
    }

    [Fact]
    public async Task Service_scan_converts_provider_exception_to_warning()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var brokenService = new FakeStartupSystem(StartupSource.Service, store.Events)
        {
            ScanException = new InvalidOperationException("boom")
        };
        var registry = new FakeStartupSystem(StartupSource.Registry, store.Events);
        registry.AddItem(TestItems.Create(StartupSource.Registry));

        var policy = new FakeProtectionPolicy
        {
            Rule = item => new ProtectionDecision(
                item.Source == StartupSource.Registry,
                item.Source == StartupSource.Registry ? "受保护" : null,
                false)
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { brokenService, registry },
            new IStartupChangeProvider[] { registry },
            policy,
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        Assert.Single(snapshot.Items);
        Assert.True(snapshot.Items[0].Protected);
        Assert.Single(snapshot.Warnings);
        Assert.Contains("系统服务", snapshot.Warnings[0]);
    }

    [Fact]
    public async Task Service_scan_fails_closed_when_policy_throws()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events);
        system.AddItem(TestItems.Create(StartupSource.Registry));

        var policy = new FakeProtectionPolicy
        {
            Rule = _ => throw new InvalidOperationException("policy failure")
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        var item = Assert.Single(snapshot.Items);
        Assert.True(item.Protected);
        Assert.True(item.RequiresAdmin);
        Assert.Contains("安全检查失败", item.ProtectionReason);
    }

    [Fact]
    public async Task Service_enable_backup_only_item_restores_and_marks_backup_restored()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var itemId = TestIds.NewId(StartupSource.Service);
        var backup = TestBackups.Create(StartupSource.Service, itemId);
        store.Add(backup);

        var scan = await manager.ScanAsync(CancellationToken.None);
        Assert.True(Assert.Single(scan.Items).ManagedByApp);

        var result = await manager.SetEnabledAsync(
            new[] { itemId },
            enabled: true,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.AllSucceeded);
        var change = Assert.Single(result.Results);
        Assert.True(change.Success);
        Assert.True(change.Verified);
        Assert.Equal(backup.Id, change.BackupId);
        Assert.Equal(1, system.RestoreCalls);
        Assert.Equal(0, system.ApplyCalls);
        Assert.Equal(0, store.CreateCount);

        var stored = await store.GetAsync(backup.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(BackupStatus.Restored, stored!.Status);
        Assert.NotNull(stored.RestoredAt);

        var after = await manager.ScanAsync(CancellationToken.None);
        Assert.True(Assert.Single(after.Items).Enabled);
    }

    [Fact]
    public async Task Service_enable_live_item_marks_active_backup_restored()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events);
        var itemId = TestIds.NewId(StartupSource.Registry);
        system.AddItem(TestItems.Create(StartupSource.Registry, itemId, enabled: false));
        var backup = TestBackups.Create(StartupSource.Registry, itemId);
        store.Add(backup);

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var result = await manager.SetEnabledAsync(
            new[] { itemId },
            enabled: true,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, system.ApplyCalls);
        Assert.Equal(0, system.RestoreCalls);
        Assert.Equal(1, store.CreateCount);

        var createIndex = store.Events.IndexOf("backup:create");
        var applyIndex = store.Events.IndexOf("apply:enable");
        Assert.True(createIndex >= 0);
        Assert.True(applyIndex > createIndex, "enable must also be backed up before apply");

        var stored = await store.GetAsync(backup.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(BackupStatus.Restored, stored!.Status);
        Assert.True(system.LiveItems.Single().Enabled);

        // The new safety record for the pre-enable state is closed as restored too.
        Assert.All(
            store.Snapshot(),
            record => Assert.Equal(BackupStatus.Restored, record.Status));
    }

    [Fact]
    public async Task Allowlisted_unverified_item_is_scanned_as_user_allowed()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var itemId = TestIds.NewId(StartupSource.Service);
        system.AddItem(TestItems.Create(StartupSource.Service, itemId, requiresAdmin: true));

        var settings = new AppSettings
        {
            UserAllowedItemIds = new List<string> { itemId }
        };
        await File.WriteAllTextAsync(
            paths.Paths.SettingsFile,
            JsonSerializer.Serialize(settings, JsonDefaults.Options));

        var policy = new FakeProtectionPolicy
        {
            Rule = _ => new ProtectionDecision(true, "发布者未验证", true, CanOverride: true)
        };
        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);
        var item = Assert.Single(snapshot.Items);

        Assert.True(item.CanOverride);
        Assert.True(item.UserAllowed);
        Assert.False(item.Protected);
        Assert.True(item.RequiresAdmin);

        var result = await manager.SetEnabledAsync(new[] { itemId }, false, true, CancellationToken.None);
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task Allowlist_cannot_override_hard_system_protection()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var itemId = TestIds.NewId(StartupSource.Service);
        system.AddItem(TestItems.Create(StartupSource.Service, itemId, requiresAdmin: true));

        var settings = new AppSettings
        {
            UserAllowedItemIds = new List<string> { itemId }
        };
        await File.WriteAllTextAsync(
            paths.Paths.SettingsFile,
            JsonSerializer.Serialize(settings, JsonDefaults.Options));

        var policy = new FakeProtectionPolicy
        {
            Rule = _ => new ProtectionDecision(true, "Windows 核心服务", true, CanOverride: false)
        };
        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var snapshot = await manager.ScanAsync(CancellationToken.None);
        var item = Assert.Single(snapshot.Items);

        Assert.True(item.Protected);
        Assert.False(item.CanOverride);
        Assert.False(item.UserAllowed);

        var result = await manager.SetEnabledAsync(new[] { itemId }, false, true, CancellationToken.None);
        Assert.False(result.AllSucceeded);
    }

    private static StartupManagerService CreateManager(
        TestPaths paths,
        IEnumerable<IStartupProvider> providers,
        IEnumerable<IStartupChangeProvider> changeProviders,
        IProtectionPolicy policy,
        IBackupStore store)
    {
        return new StartupManagerService(
            providers,
            changeProviders,
            policy,
            store,
            paths.Paths,
            _ => { });
    }
}
