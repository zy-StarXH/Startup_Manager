using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Services;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ServiceSettingsTests
{
    [Fact]
    public async Task Service_settings_default_when_file_is_missing_or_corrupt()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);

        var manager = CreateService(paths, store);
        var defaults = manager.Settings;

        Assert.True(defaults.IncludeRegistry);
        Assert.True(defaults.IncludeStartupFolders);
        Assert.True(defaults.IncludeScheduledTasks);
        Assert.True(defaults.IncludeServices);
        Assert.Equal(30, defaults.BackupRetentionDays);

        await File.WriteAllTextAsync(paths.Paths.SettingsFile, "{ this is not valid json");

        var corrupt = CreateService(paths, store);
        Assert.Equal(30, corrupt.Settings.BackupRetentionDays);
        Assert.True(corrupt.Settings.IncludeScheduledTasks);
    }

    [Fact]
    public async Task Service_save_settings_persists_prunes_and_reloads()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var manager = CreateService(paths, store);

        var settings = new AppSettings
        {
            IncludeRegistry = false,
            IncludeStartupFolders = true,
            IncludeScheduledTasks = false,
            IncludeServices = true,
            StartWithWindows = true,
            MinimizeToTray = false,
            BackupRetentionDays = 7
        };

        await manager.SaveSettingsAsync(settings, CancellationToken.None);

        Assert.True(File.Exists(paths.Paths.SettingsFile));
        var onDisk = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(paths.Paths.SettingsFile),
            JsonDefaults.Options);

        Assert.NotNull(onDisk);
        Assert.False(onDisk!.IncludeRegistry);
        Assert.False(onDisk.IncludeScheduledTasks);
        Assert.True(onDisk.IncludeServices);
        Assert.True(onDisk.StartWithWindows);
        Assert.False(onDisk.MinimizeToTray);
        Assert.Equal(7, onDisk.BackupRetentionDays);

        Assert.Equal(1, store.PruneCount);
        Assert.Equal(7, store.LastRetentionDays);

        var reloaded = CreateService(paths, store);
        Assert.False(reloaded.Settings.IncludeRegistry);
        Assert.Equal(7, reloaded.Settings.BackupRetentionDays);

        var copy = manager.Settings;
        copy.IncludeRegistry = true;
        Assert.False(manager.Settings.IncludeRegistry);
    }

    [Fact]
    public async Task Service_settings_normalizes_out_of_range_retention()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var manager = CreateService(paths, store);

        await manager.SaveSettingsAsync(
            new AppSettings { BackupRetentionDays = -5 },
            CancellationToken.None);
        Assert.Equal(30, manager.Settings.BackupRetentionDays);

        await manager.SaveSettingsAsync(
            new AppSettings { BackupRetentionDays = 5000 },
            CancellationToken.None);
        Assert.Equal(3650, manager.Settings.BackupRetentionDays);
    }

    [Fact]
    public async Task Service_settings_include_flags_control_scan()
    {
        using var paths = new TestPaths();
        var store = new InMemoryBackupStore(paths.Paths.BackupDirectory);
        var serviceSystem = new FakeStartupSystem(StartupSource.Service, store.Events);
        serviceSystem.AddItem(TestItems.Create(StartupSource.Service));

        var manager = new StartupManagerService(
            new IStartupProvider[] { serviceSystem },
            new IStartupChangeProvider[] { serviceSystem },
            new FakeProtectionPolicy(),
            store,
            paths.Paths,
            _ => { });

        await manager.SaveSettingsAsync(
            new AppSettings { IncludeServices = false },
            CancellationToken.None);

        var snapshot = await manager.ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Items);
        Assert.Equal(0, serviceSystem.ScanCalls);
    }

    private static StartupManagerService CreateService(TestPaths paths, InMemoryBackupStore store)
    {
        var system = new FakeStartupSystem(StartupSource.Registry, store.Events);
        return new StartupManagerService(
            new IStartupProvider[] { system },
            new IStartupChangeProvider[] { system },
            new FakeProtectionPolicy(),
            store,
            paths.Paths,
            _ => { });
    }
}
