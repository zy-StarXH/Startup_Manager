using Microsoft.Win32;
using StartupManager.Core.Backup;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;
using StartupManager.Core.Providers.RegistryFolder;
using StartupManager.Core.Services;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class SystemIntegrationTests
{
    private sealed class NeverProtectedPolicy : IProtectionPolicy
    {
        public ProtectionDecision Evaluate(StartupItem item) => new(false, null, false);
    }

    [Fact]
    public async Task Registry_entry_can_be_disabled_and_restored_through_the_transaction_engine()
    {
        var valueName = "StartupManagerIT_" + Guid.NewGuid().ToString("N").Substring(0, 10);
        var root = Path.Combine(Path.GetTempPath(), "StartupManagerIT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths
        {
            DataDirectory = root,
            BackupDirectory = Path.Combine(root, "Backups"),
            LogDirectory = Path.Combine(root, "Logs"),
            IpcDirectory = Path.Combine(root, "Ipc"),
            SettingsFile = Path.Combine(root, "settings.json")
        };
        paths.EnsureCreated();

        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                Assert.NotNull(run);
                run!.SetValue(valueName, "cmd.exe /c exit", RegistryValueKind.String);
            }

            var registry = new RegistryStartupProvider();
            var folder = new StartupFolderProvider();
            var backups = new JsonBackupStore(paths);
            var service = new StartupManagerService(
                new IStartupProvider[] { registry, folder },
                new IStartupChangeProvider[] { registry, folder },
                new NeverProtectedPolicy(),
                backups,
                paths);

            var initial = await service.ScanAsync(CancellationToken.None);
            var target = Assert.Single(initial.Items, item => item.Name == valueName && item.Source == StartupSource.Registry);
            Assert.True(target.Enabled);

            var disable = await service.SetEnabledAsync(new[] { target.Id }, false, false, CancellationToken.None);
            Assert.True(disable.AllSucceeded, disable.Message);
            Assert.Single(disable.Results);
            Assert.True(disable.Results[0].Verified);

            var raw = StartupApproved.ReadApprovals(StartupApprovalFamily.Run);
            Assert.True(raw.ContainsKey(valueName));
            Assert.False(StartupApproved.IsEnabled(raw[valueName]));

            var afterDisable = await service.ScanAsync(CancellationToken.None);
            var disabled = Assert.Single(afterDisable.Items, item => item.Id == target.Id);
            Assert.False(disabled.Enabled);
            Assert.True(disabled.ManagedByApp);
            Assert.Single(await service.GetBackupsAsync(includeRestored: false, CancellationToken.None));

            var enable = await service.SetEnabledAsync(new[] { target.Id }, true, false, CancellationToken.None);
            Assert.True(enable.AllSucceeded, enable.Message);
            Assert.True(enable.Results[0].Verified);

            var rawAfter = StartupApproved.ReadApprovals(StartupApprovalFamily.Run);
            Assert.True(rawAfter.ContainsKey(valueName)); // enabling writes an enabled approval state
            Assert.True(StartupApproved.IsEnabled(rawAfter[valueName]));
            Assert.Empty(await service.GetBackupsAsync(includeRestored: false, CancellationToken.None));

            var afterEnable = await service.ScanAsync(CancellationToken.None);
            var enabled = Assert.Single(afterEnable.Items, item => item.Id == target.Id);
            Assert.True(enabled.Enabled);
        }
        finally
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch
            {
                // Best-effort cleanup.
            }

            try
            {
                StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);
            }
            catch
            {
                // Best-effort cleanup.
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
