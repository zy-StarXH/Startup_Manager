using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;
using StartupManager.Core.Providers.RegistryFolder;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class StartupFolderProviderTests
{
    [Fact]
    public async Task Scan_finds_supported_files_in_injected_folders_and_skips_others()
    {
        using var folders = new TempFolders();
        var suffix = Guid.NewGuid().ToString("N");
        var userCmd = Path.Combine(folders.User, $"StartupManagerTest_{suffix}.cmd");
        var userUrl = Path.Combine(folders.User, $"StartupManagerTest_{suffix}.url");
        var userExe = Path.Combine(folders.User, $"StartupManagerTest_{suffix}.exe");
        var commonCmd = Path.Combine(folders.Common, $"StartupManagerTestCommon_{suffix}.cmd");

        File.WriteAllText(userCmd, "@echo off\r\nexit /b 0\r\n");
        File.WriteAllText(userUrl, "[InternetShortcut]\r\nURL=https://example.com/startup-test\r\n");
        File.WriteAllText(userExe, "not a real PE file");
        File.WriteAllText(commonCmd, "@echo off\r\nexit /b 0\r\n");
        File.WriteAllText(Path.Combine(folders.User, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(folders.User, "notes.txt"), "not a startup file");
        Directory.CreateDirectory(Path.Combine(folders.User, "Nested"));
        File.WriteAllText(Path.Combine(folders.User, "Nested", "nested.cmd"), "exit /b 0");

        var provider = new StartupFolderProvider(folders.User, folders.Common, Path.Combine(folders.Root, "Quarantine"));

        var items = await provider.ScanAsync(CancellationToken.None);

        Assert.Equal(4, items.Count);
        Assert.DoesNotContain(items, item => item.Id.StartsWith("reg-"));
        Assert.All(items, item =>
        {
            Assert.Equal(StartupSource.Folder, item.Source);
            Assert.Equal(item.Scope == StartupScope.AllUsers, item.RequiresAdmin);
            Assert.Equal("startupFolderQuarantine", item.Mechanism);
            Assert.StartsWith("dir-", item.Id);
            Assert.NotNull(item.LastModified);
        });
        Assert.DoesNotContain(items, item => item.Name.Equals("desktop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, item => item.Name.Equals("notes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, item => item.Name.Equals("nested", StringComparison.OrdinalIgnoreCase));

        var cmdItem = items.Single(item => item.Location == userCmd);
        Assert.Equal(Path.GetFileNameWithoutExtension(userCmd), cmdItem.Name);
        Assert.Equal(StartupScope.CurrentUser, cmdItem.Scope);
        Assert.True(cmdItem.Enabled);
        Assert.Equal(userCmd, cmdItem.Command);
        Assert.Equal(userCmd, cmdItem.TargetPath);
        Assert.Contains("当前用户启动文件夹", cmdItem.SubLocation);

        var urlItem = items.Single(item => item.Location == userUrl);
        Assert.Equal("https://example.com/startup-test", urlItem.Command);
        Assert.Null(urlItem.TargetPath);
        Assert.True(urlItem.Enabled);
        Assert.StartsWith("快捷方式 · ", urlItem.SubLocation);

        var exeItem = items.Single(item => item.Location == userExe);
        Assert.Equal(userExe, exeItem.TargetPath);
        Assert.Equal(userExe, exeItem.Command);

        var commonItem = items.Single(item => item.Location == commonCmd);
        Assert.Equal(StartupScope.AllUsers, commonItem.Scope);
        Assert.Contains("公共启动文件夹", commonItem.SubLocation);

        var secondScan = await provider.ScanAsync(CancellationToken.None);
        Assert.Equal(
            items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal),
            secondScan.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Disabled_folder_entry_can_be_captured_enabled_and_restored()
    {
        using var folders = new TempFolders();
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.cmd";
        var path = Path.Combine(folders.User, fileName);
        File.WriteAllText(path, "@echo off\r\nexit /b 0\r\n");

        // Keep non-zero reserved bytes to prove raw approval data is preserved.
        var original = new byte[]
        {
            0x03, 0x01, 0x00, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88
        };
        var provider = new StartupFolderProvider(
            folders.User,
            folders.Common,
            Path.Combine(folders.Root, "Quarantine"));

        try
        {
            StartupApproved.WriteApproval(
                StartupApprovalFamily.StartupFolder,
                fileName,
                original);

            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Location == path);
            Assert.True(item.Enabled);
            Assert.Contains("入口仍存在", item.SubLocation);
            Assert.NotNull(item.LastModified);

            var state = await provider.CaptureStateAsync(item, CancellationToken.None);
            Assert.Equal(StartupFolderProvider.ProviderStateKind, state.Kind);
            Assert.Contains(fileName, state.Summary);
            Assert.Contains("原本已禁用", state.Summary);

            var captured = JsonSerializer.Deserialize<FolderApprovalState>(
                state.Json,
                JsonDefaults.Options);
            Assert.NotNull(captured);
            Assert.Equal("CurrentUser", captured!.Scope);
            Assert.Equal(path, captured.FullPath);
            Assert.Equal(fileName, captured.ValueName);
            Assert.Equal("StartupFolder", captured.ApprovalFamily);
            Assert.True(captured.ApprovalValueExisted);
            Assert.Equal(Convert.ToBase64String(original), captured.ApprovalRawBase64);
            Assert.Equal(path, captured.Command);

            await provider.ApplyEnabledAsync(item, enabled: true, isElevated: false, CancellationToken.None);
            Assert.True(await provider.VerifyEnabledAsync(item, enabled: true, CancellationToken.None));
            Assert.True(File.Exists(path));
            Assert.False(StartupFolderQuarantine.IsQuarantined(
                Path.Combine(folders.Root, "Quarantine"), item.Id, path));
            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.StartupFolder,
                fileName,
                out var enabledBytes,
                out _));
            Assert.NotNull(enabledBytes);
            Assert.Equal(0x02, enabledBytes![0]);

            await provider.RestoreStateAsync(
                new BackupRecord { StateKind = state.Kind, StateJson = state.Json },
                isElevated: false,
                CancellationToken.None);

            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.StartupFolder,
                fileName,
                out var restored,
                out var restoredExists));
            Assert.True(restoredExists);
            Assert.Equal(original, restored);
            Assert.True(File.Exists(path));

            await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);
            Assert.True(await provider.VerifyEnabledAsync(item, enabled: false, CancellationToken.None));
            var rescanned = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Location == path);
            Assert.False(rescanned.Enabled);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(StartupFolderQuarantine.GetPath(
                Path.Combine(folders.Root, "Quarantine"),
                item.Id,
                path)));
        }
        finally
        {
            StartupApproved.DeleteApproval(StartupApprovalFamily.StartupFolder, fileName);
        }
    }

    [Fact]
    public async Task Missing_folders_produce_an_empty_scan()
    {
        var root = Path.Combine(Path.GetTempPath(), "StartupManagerTests", Guid.NewGuid().ToString("N"));
        var provider = new StartupFolderProvider(
            Path.Combine(root, "MissingUser"),
            Path.Combine(root, "MissingCommon"));

        var items = await provider.ScanAsync(CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Apply_throws_when_the_file_disappeared_after_scan()
    {
        using var folders = new TempFolders();
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.cmd";
        var path = Path.Combine(folders.User, fileName);
        File.WriteAllText(path, "@echo off\r\nexit /b 0\r\n");
        var provider = new StartupFolderProvider(folders.User, folders.Common, Path.Combine(folders.Root, "Quarantine"));

        try
        {
            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Location == path);

            File.Delete(path);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.ApplyEnabledAsync(item, enabled: false, isElevated: false, CancellationToken.None));

            Assert.False(StartupApproved.TryReadApproval(
                StartupApprovalFamily.StartupFolder,
                fileName,
                out _,
                out var exists));
            Assert.False(exists);
        }
        finally
        {
            StartupApproved.DeleteApproval(StartupApprovalFamily.StartupFolder, fileName);
        }
    }

    [Fact]
    public async Task Broken_shortcut_degrades_to_the_file_path()
    {
        using var folders = new TempFolders();
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.lnk";
        var path = Path.Combine(folders.User, fileName);
        File.WriteAllText(path, "this is not a valid shell link");
        var provider = new StartupFolderProvider(folders.User, folders.Common, Path.Combine(folders.Root, "Quarantine"));

        var items = await provider.ScanAsync(CancellationToken.None);

        var item = Assert.Single(items, candidate => candidate.Location == path);
        Assert.Equal(path, item.Command);
        Assert.Equal(path, item.TargetPath);
        Assert.Equal(PublisherResolver.UnknownPublisher, item.Publisher);
        Assert.True(item.Enabled);
    }

    [Fact]
    public void Provider_exposes_a_true_parameterless_constructor_for_integration()
    {
        Assert.NotNull(typeof(StartupFolderProvider).GetConstructor(Type.EmptyTypes));

        var provider = new StartupFolderProvider();
        Assert.Equal(StartupSource.Folder, provider.Source);
    }

    [Fact]
    public async Task Scan_resolves_lnk_target_and_arguments_through_wscript_shell()
    {
        using var folders = new TempFolders();
        var targets = Path.Combine(folders.Root, "Targets");
        Directory.CreateDirectory(targets);
        var targetPath = Path.Combine(targets, "TargetApp.exe");
        File.WriteAllText(targetPath, "not a real PE file");

        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.lnk";
        var path = Path.Combine(folders.User, fileName);
        Assert.True(
            TryCreateShortcut(path, targetPath, "--startup-test"),
            "WScript.Shell is required to create the fixture shortcut.");

        var provider = new StartupFolderProvider(folders.User, folders.Common, Path.Combine(folders.Root, "Quarantine"));
        var item = Assert.Single(
            await provider.ScanAsync(CancellationToken.None),
            candidate => candidate.Location == path);

        Assert.Equal(targetPath, item.TargetPath);
        Assert.True(
            item.Command.Contains(targetPath, StringComparison.OrdinalIgnoreCase),
            $"Expected the shortcut target in '{item.Command}'.");
        Assert.Contains("--startup-test", item.Command);
    }

    [Fact]
    public async Task Disable_moves_shortcut_to_quarantine_and_enable_restores_it()
    {
        using var folders = new TempFolders();
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.cmd";
        var path = Path.Combine(folders.User, fileName);
        File.WriteAllText(path, "@echo off\r\nexit /b 0\r\n");
        var provider = new StartupFolderProvider(
            folders.User,
            folders.Common,
            Path.Combine(folders.Root, "Quarantine"));

        try
        {
            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Location == path);
            await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);

            Assert.False(File.Exists(path));
            Assert.True(StartupFolderQuarantine.IsQuarantined(
                Path.Combine(folders.Root, "Quarantine"), item.Id, path));
            Assert.True(await provider.VerifyEnabledAsync(item, false, CancellationToken.None));
            var scannedAfterDisable = await provider.ScanAsync(CancellationToken.None);
            Assert.True(
                scannedAfterDisable.Any(candidate => candidate.Id == item.Id),
                "Resolved original: " + StartupFolderQuarantine.FindOriginalPath(
                    Path.Combine(folders.Root, "Quarantine"),
                    Path.GetFileName(StartupFolderQuarantine.GetPath(
                        Path.Combine(folders.Root, "Quarantine"), item.Id, path))) +
                "\nScanned: " + string.Join(" | ", scannedAfterDisable.Select(candidate => candidate.Id + "=" + candidate.Location)) +
                "\nQuarantine: " + string.Join("\n", Directory.GetFiles(Path.Combine(folders.Root, "Quarantine")).Select(file =>
                    file + " | " + File.ReadAllText(file))));
            var disabled = Assert.Single(scannedAfterDisable, candidate => candidate.Id == item.Id);
            Assert.False(disabled.Enabled);
            Assert.Equal(path, disabled.Metadata["fullPath"]);
            Assert.Equal("startupFolderQuarantine", disabled.Mechanism);

            await provider.ApplyEnabledAsync(disabled, true, false, CancellationToken.None);
            Assert.True(File.Exists(path));
            Assert.False(StartupFolderQuarantine.IsQuarantined(
                Path.Combine(folders.Root, "Quarantine"), item.Id, path));
            Assert.True(await provider.VerifyEnabledAsync(item, true, CancellationToken.None));
        }
        finally
        {
            StartupApproved.DeleteApproval(StartupApprovalFamily.StartupFolder, fileName);
        }
    }

    [Fact]
    public async Task Real_public_tailscale_shortcut_is_discovered_with_valid_metadata()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        var path = Path.Combine(common, "Tailscale.lnk");
        if (!File.Exists(path))
        {
            return;
        }

        var provider = new StartupFolderProvider();
        var item = Assert.Single(
            await provider.ScanAsync(CancellationToken.None),
            candidate => string.Equals(candidate.Location, path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Tailscale.lnk", item.Metadata["approvalValueName"]);
        Assert.Equal(StartupScope.AllUsers, item.Scope);
        Assert.Contains("tailscale-ipn.exe", item.TargetPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disable_restore_and_enable_rollback_preserve_the_original_file_and_scope()
    {
        using var folders = new TempFolders();
        var quarantine = Path.Combine(folders.Root, "Quarantine");
        var provider = new StartupFolderProvider(folders.User, folders.Common, quarantine);
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.cmd";
        var path = Path.Combine(folders.Common, fileName);
        const string contents = "original file contents";
        File.WriteAllText(path, contents);
        try
        {
            var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
            Assert.True(item.RequiresAdmin);
            var beforeDisable = await provider.CaptureStateAsync(item, CancellationToken.None);
            await provider.ApplyEnabledAsync(item, false, true, CancellationToken.None);
            var restarted = new StartupFolderProvider(folders.User, folders.Common, quarantine);
            var disabled = Assert.Single(await restarted.ScanAsync(CancellationToken.None));
            Assert.False(disabled.Enabled);
            Assert.Equal(StartupScope.AllUsers, disabled.Scope);
            var beforeEnable = await restarted.CaptureStateAsync(disabled, CancellationToken.None);
            await restarted.ApplyEnabledAsync(disabled, true, true, CancellationToken.None);

            var rollback = new BackupRecord { ItemId = item.Id, StateKind = beforeEnable.Kind, StateJson = beforeEnable.Json };
            await restarted.RestoreStateAsync(rollback, true, CancellationToken.None);
            await restarted.RestoreStateAsync(rollback, true, CancellationToken.None);
            Assert.True(await restarted.VerifyEnabledAsync(item, false, CancellationToken.None));
            Assert.False(File.Exists(path));

            var restore = new BackupRecord { ItemId = item.Id, StateKind = beforeDisable.Kind, StateJson = beforeDisable.Json };
            await restarted.RestoreStateAsync(restore, true, CancellationToken.None);
            await restarted.RestoreStateAsync(restore, true, CancellationToken.None);
            Assert.Equal(contents, File.ReadAllText(path));
            Assert.True(await restarted.VerifyEnabledAsync(item, true, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(quarantine));
        }
        finally { StartupApproved.DeleteApproval(StartupApprovalFamily.StartupFolder, fileName); }
    }

    [Fact]
    public async Task Recreated_startup_file_is_visible_and_never_overwritten_by_restore()
    {
        using var folders = new TempFolders();
        var provider = new StartupFolderProvider(folders.User, folders.Common, Path.Combine(folders.Root, "Quarantine"));
        var fileName = $"StartupManagerTest_{Guid.NewGuid():N}.cmd";
        var path = Path.Combine(folders.User, fileName);
        File.WriteAllText(path, "original");
        try
        {
            var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
            var state = await provider.CaptureStateAsync(item, CancellationToken.None);
            await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);
            File.WriteAllText(path, "recreated by installer");
            Assert.True(Assert.Single(await provider.ScanAsync(CancellationToken.None)).Enabled);
            Assert.False(await provider.VerifyEnabledAsync(item, false, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RestoreStateAsync(
                new BackupRecord { ItemId = item.Id, StateKind = state.Kind, StateJson = state.Json },
                false, CancellationToken.None));
            Assert.Equal("recreated by installer", File.ReadAllText(path));
        }
        finally { StartupApproved.DeleteApproval(StartupApprovalFamily.StartupFolder, fileName); }
    }

    private static bool TryCreateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });
            if (shortcut is null)
            {
                return false;
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: new object[] { targetPath });
            shortcutType.InvokeMember(
                "Arguments",
                BindingFlags.SetProperty,
                binder: null,
                target: shortcut,
                args: new object[] { arguments });
            shortcutType.InvokeMember(
                "Save",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shortcut,
                args: null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
        catch (Exception)
        {
            // Best effort; the RCW is still collected.
        }
    }

    private sealed class TempFolders : IDisposable
    {
        public TempFolders()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "StartupManagerTests",
                Guid.NewGuid().ToString("N"));
            User = Path.Combine(Root, "UserStartup");
            Common = Path.Combine(Root, "CommonStartup");
            Directory.CreateDirectory(User);
            Directory.CreateDirectory(Common);
        }

        public string Root { get; }

        public string User { get; }

        public string Common { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // Temp cleanup is best effort.
            }
        }
    }
}
