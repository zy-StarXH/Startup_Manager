using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Services;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ChangeServiceTests
{
    [Fact]
    public async Task Change_disable_writes_backup_before_apply_and_verifies()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.AllSucceeded);
        Assert.False(result.RequiresElevation);

        var change = Assert.Single(result.Results);
        Assert.True(change.Success);
        Assert.True(change.Verified);
        Assert.NotNull(change.BackupId);

        var backup = await fixture.Store.GetAsync(change.BackupId!, CancellationToken.None);
        Assert.NotNull(backup);
        Assert.Equal(BackupStatus.Active, backup!.Status);
        Assert.NotNull(backup.VerifiedAt);
        Assert.Equal(fixture.Item.Id, backup.ItemId);

        Assert.False(fixture.System.LiveItems.Single().Enabled);

        var backupIndex = fixture.Store.Events.IndexOf("backup:create");
        var applyIndex = fixture.Store.Events.IndexOf("apply:disable");
        Assert.True(backupIndex >= 0, "backup should be created");
        Assert.True(applyIndex > backupIndex, "backup must be persisted before apply");
    }

    [Fact]
    public async Task Change_disable_verification_failure_restores_original_and_marks_backup_failed()
    {
        using var fixture = new Fixture();
        fixture.System.VerifyResult = false;

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.AllSucceeded);
        var change = Assert.Single(result.Results);
        Assert.False(change.Success);
        Assert.False(change.Verified);
        Assert.Equal(1, fixture.System.RestoreCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);
        Assert.Contains("验证", change.Message);

        var backup = Assert.Single(fixture.Store.Snapshot());
        Assert.Equal(BackupStatus.Failed, backup.Status);
        Assert.NotNull(backup.Error);
    }

    [Fact]
    public async Task Change_disable_apply_failure_rolls_back_and_marks_backup_failed()
    {
        using var fixture = new Fixture();
        fixture.System.ApplyException = new InvalidOperationException("boom");

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.AllSucceeded);
        var change = Assert.Single(result.Results);
        Assert.False(change.Success);
        Assert.Equal(1, fixture.System.RestoreCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);

        var backup = Assert.Single(fixture.Store.Snapshot());
        Assert.Equal(BackupStatus.Failed, backup.Status);
    }

    [Fact]
    public async Task Change_capture_failure_aborts_without_backup_or_apply()
    {
        using var fixture = new Fixture();
        fixture.System.CaptureException = new InvalidOperationException("capture failed");

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Equal(0, fixture.Store.CreateCount);
        Assert.Equal(0, fixture.System.ApplyCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);
    }

    [Fact]
    public async Task Change_backup_store_failure_aborts_without_apply()
    {
        using var fixture = new Fixture();
        fixture.Store.FailCreate = true;

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Equal(0, fixture.System.ApplyCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);
        Assert.Contains("备份", Assert.Single(result.Results).Message);
    }

    [Fact]
    public async Task Change_disable_already_disabled_is_success_noop()
    {
        using var fixture = new Fixture(enabled: false);

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.AllSucceeded);
        var change = Assert.Single(result.Results);
        Assert.True(change.Success);
        Assert.True(change.Verified);
        Assert.Contains("已处于关闭状态", change.Message);
        Assert.Equal(0, fixture.Store.CreateCount);
        Assert.Equal(0, fixture.System.ApplyCalls);
    }

    [Fact]
    public async Task Change_admin_precheck_returns_elevation_and_mutates_nothing()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var registry = new FakeStartupSystem(StartupSource.Registry, store.Events);
        var registryItem = TestItems.Create(StartupSource.Registry, enabled: true);
        registry.AddItem(registryItem);

        var serviceSystem = new FakeStartupSystem(StartupSource.Service, store.Events);
        var serviceItem = TestItems.Create(StartupSource.Service, enabled: true, requiresAdmin: true);
        serviceSystem.AddItem(serviceItem);

        var policy = new FakeProtectionPolicy
        {
            Rule = item => new ProtectionDecision(false, null, item.RequiresAdmin)
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { registry, serviceSystem },
            new IStartupChangeProvider[] { registry, serviceSystem },
            policy,
            store);

        var result = await manager.SetEnabledAsync(
            new[] { registryItem.Id, serviceItem.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.RequiresElevation);
        Assert.Empty(result.Results);

        Assert.Equal(0, store.CreateCount);
        Assert.Equal(0, registry.CaptureCalls);
        Assert.Equal(0, registry.ApplyCalls);
        Assert.Equal(0, serviceSystem.CaptureCalls);
        Assert.Equal(0, serviceSystem.ApplyCalls);

        Assert.True(registry.LiveItems.Single().Enabled);
        Assert.True(serviceSystem.LiveItems.Single().Enabled);
    }

    [Fact]
    public async Task Change_protected_item_rejects_whole_batch_before_backup()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events);
        var normal = TestItems.Create(StartupSource.Registry);
        var protectedItem = TestItems.Create(StartupSource.Registry);
        system.AddItem(normal);
        system.AddItem(protectedItem);

        var policy = new FakeProtectionPolicy
        {
            Rule = item => item.Id == protectedItem.Id
                ? new ProtectionDecision(true, "系统任务", false)
                : new ProtectionDecision(false, null, false)
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var result = await manager.SetEnabledAsync(
            new[] { normal.Id, protectedItem.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Contains("保护", result.Message);
        Assert.Equal(0, store.CreateCount);
        Assert.Equal(0, system.ApplyCalls);
        Assert.True(system.LiveItems.All(item => item.Enabled));
    }

    [Fact]
    public async Task Change_unknown_malformed_and_empty_ids_are_rejected_without_mutation()
    {
        using var fixture = new Fixture();
        var unknown = TestIds.NewId(StartupSource.Registry);

        var unknownResult = await fixture.Service.SetEnabledAsync(
            new[] { unknown },
            enabled: false,
            isElevated: false,
            CancellationToken.None);
        Assert.Contains("不存在", unknownResult.Message);

        var malformedResult = await fixture.Service.SetEnabledAsync(
            new[] { "reg-1" },
            enabled: false,
            isElevated: false,
            CancellationToken.None);
        Assert.Contains("标识无效", malformedResult.Message);

        var emptyResult = await fixture.Service.SetEnabledAsync(
            Array.Empty<string>(),
            enabled: false,
            isElevated: false,
            CancellationToken.None);
        Assert.Contains("未选择", emptyResult.Message);

        Assert.Equal(0, fixture.Store.CreateCount);
        Assert.Equal(0, fixture.System.ApplyCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);
    }

    [Fact]
    public async Task Change_unexpected_unauthorized_returns_elevation_after_rollback()
    {
        using var fixture = new Fixture();
        fixture.System.ApplyException = new UnauthorizedAccessException("denied");

        var result = await fixture.Service.SetEnabledAsync(
            new[] { fixture.Item.Id },
            enabled: false,
            isElevated: false,
            CancellationToken.None);

        Assert.True(result.RequiresElevation);
        var change = Assert.Single(result.Results);
        Assert.True(change.RequiresElevation);
        Assert.False(change.Success);
        Assert.Equal(1, fixture.System.RestoreCalls);
        Assert.True(fixture.System.LiveItems.Single().Enabled);

        var backup = Assert.Single(fixture.Store.Snapshot());
        Assert.Equal(BackupStatus.Failed, backup.Status);
    }

    [Fact]
    public async Task Change_restore_requires_elevation_and_does_not_call_provider()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var item = TestItems.Create(StartupSource.Service, enabled: false, requiresAdmin: true);
        system.AddItem(item);

        var backup = TestBackups.Create(StartupSource.Service, item.Id, requiresAdmin: true);
        store.Add(backup);

        var policy = new FakeProtectionPolicy
        {
            Rule = candidate => new ProtectionDecision(false, null, candidate.RequiresAdmin)
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var result = await manager.RestoreBackupAsync(backup.Id, isElevated: false, CancellationToken.None);

        Assert.True(result.RequiresElevation);
        Assert.False(result.Success);
        Assert.Equal(0, system.RestoreCalls);

        var stored = await store.GetAsync(backup.Id, CancellationToken.None);
        Assert.Equal(BackupStatus.Active, stored!.Status);
    }

    [Fact]
    public async Task Change_restore_elevated_marks_backup_restored_after_verification()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Service, store.Events);
        var item = TestItems.Create(StartupSource.Service, enabled: false, requiresAdmin: true);
        system.AddItem(item);

        var backup = TestBackups.Create(StartupSource.Service, item.Id, requiresAdmin: true);
        store.Add(backup);

        var policy = new FakeProtectionPolicy
        {
            Rule = candidate => new ProtectionDecision(false, null, candidate.RequiresAdmin)
        };

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            policy,
            store);

        var result = await manager.RestoreBackupAsync(backup.Id, isElevated: true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Verified);
        Assert.Equal(1, system.RestoreCalls);

        var stored = await store.GetAsync(backup.Id, CancellationToken.None);
        Assert.Equal(BackupStatus.Restored, stored!.Status);
        Assert.NotNull(stored.RestoredAt);
        Assert.NotNull(stored.VerifiedAt);
    }

    [Fact]
    public async Task Change_restore_verification_failure_marks_backup_failed()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events)
        {
            RestoreMutates = false
        };
        var item = TestItems.Create(StartupSource.Registry, enabled: false);
        system.AddItem(item);

        var backup = TestBackups.Create(StartupSource.Registry, item.Id);
        store.Add(backup);

        var manager = CreateManager(
            paths,
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store);

        var result = await manager.RestoreBackupAsync(backup.Id, isElevated: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Verified);
        Assert.Contains("验证", result.Message);

        var stored = await store.GetAsync(backup.Id, CancellationToken.None);
        Assert.Equal(BackupStatus.Failed, stored!.Status);
        Assert.NotNull(stored.Error);
    }

    [Fact]
    public async Task Change_restore_unknown_backup_fails_safely()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.RestoreBackupAsync(
            Guid.NewGuid().ToString("N"),
            isElevated: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("找不到", result.Message);
        Assert.Equal(0, fixture.System.RestoreCalls);
    }

    [Fact]
    public async Task Change_backup_list_contains_summaries_without_state()
    {
        using var fixture = new Fixture();
        var backup = TestBackups.Create(StartupSource.Registry, fixture.Item.Id);
        fixture.Store.Add(backup);

        var summaries = await fixture.Service.GetBackupsAsync(includeRestored: true, CancellationToken.None);

        var summary = Assert.Single(summaries);
        Assert.Equal(backup.Id, summary.Id);
        Assert.Equal("启动类型：自动", summary.Summary);

        var json = JsonSerializer.Serialize(summary, JsonDefaults.Options);
        Assert.DoesNotContain("StateJson", json);
        Assert.DoesNotContain("stateJson", json);
        Assert.DoesNotContain("original", json);
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

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            StartupSource source = StartupSource.Registry,
            bool enabled = true,
            bool requiresAdmin = false)
        {
            Paths = new TestPaths();
            Store = new InMemoryBackupStore(Paths.Paths.BackupDirectory);
            System = new FakeStartupSystem(source, Store.Events);
            Item = TestItems.Create(source, TestIds.NewId(source), enabled, requiresAdmin);
            System.AddItem(Item);

            var policy = new FakeProtectionPolicy
            {
                Rule = candidate => new ProtectionDecision(false, null, candidate.RequiresAdmin)
            };

            Service = CreateManager(
                Paths,
                new IStartupProvider[] { System },
                new IStartupChangeProvider[] { System },
                policy,
                Store);
        }

        public TestPaths Paths { get; }

        public InMemoryBackupStore Store { get; }

        public FakeStartupSystem System { get; }

        public StartupItem Item { get; }

        public StartupManagerService Service { get; }

        public void Dispose() => Paths.Dispose();
    }
}
