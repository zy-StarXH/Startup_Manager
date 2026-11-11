using System.Text.Json;
using Microsoft.Win32;
using StartupManager.Core.Models;
using StartupManager.Core.Providers;
using StartupManager.Core.Providers.RegistryFolder;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class RegistryStartupProviderTests
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public async Task Scan_capture_disable_enable_restore_roundtrip_for_hkcu_run_value()
    {
        var valueName = "StartupManagerTest_" + Guid.NewGuid().ToString("N");
        var command = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c exit";
        var provider = new RegistryStartupProvider();

        try
        {
            WriteRunValue(valueName, command);

            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Name == valueName);

            Assert.StartsWith("reg-", item.Id);
            Assert.Equal(StartupSource.Registry, item.Source);
            Assert.Equal(StartupScope.CurrentUser, item.Scope);
            Assert.True(item.Enabled);
            Assert.False(item.RequiresAdmin);
            Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", item.Location);
            Assert.Equal("值名称：" + valueName, item.SubLocation);
            Assert.Equal(command, item.Command);
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), item.TargetPath);
            Assert.Equal("startupApproved", item.Mechanism);
            Assert.Null(item.LastModified);
            Assert.False(string.IsNullOrWhiteSpace(item.Publisher));
            Assert.True(item.Metadata.ContainsKey("approvalFamily"));

            var state = await provider.CaptureStateAsync(item, CancellationToken.None);
            Assert.Equal(RegistryStartupProvider.ProviderStateKind, state.Kind);
            Assert.Contains(valueName, state.Summary);
            Assert.Contains("原本已启用", state.Summary);

            var captured = JsonSerializer.Deserialize<RegistryApprovalState>(
                state.Json,
                JsonDefaults.Options);
            Assert.NotNull(captured);
            Assert.Equal("HKCU", captured!.Hive);
            Assert.Equal("Registry64", captured.View);
            Assert.Equal(RunKeyPath, captured.KeyPath);
            Assert.Equal(valueName, captured.ValueName);
            Assert.Equal("Run", captured.ApprovalFamily);
            Assert.False(captured.ApprovalValueExisted);
            Assert.Null(captured.ApprovalRawBase64);
            Assert.Equal(command, captured.RunCommand);

            await provider.ApplyEnabledAsync(item, enabled: false, isElevated: false, CancellationToken.None);

            Assert.True(await provider.VerifyEnabledAsync(item, enabled: false, CancellationToken.None));
            Assert.False(await provider.VerifyEnabledAsync(item, enabled: true, CancellationToken.None));

            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out var disabledBytes,
                out var disabledExists));
            Assert.True(disabledExists);
            Assert.NotNull(disabledBytes);
            Assert.Equal(12, disabledBytes!.Length);
            Assert.Equal(0x03, disabledBytes[0]);

            var disabledItem = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Name == valueName);
            Assert.Equal(item.Id, disabledItem.Id);
            Assert.False(disabledItem.Enabled);
            Assert.NotNull(disabledItem.LastModified);

            await provider.ApplyEnabledAsync(
                disabledItem,
                enabled: true,
                isElevated: false,
                CancellationToken.None);

            Assert.True(await provider.VerifyEnabledAsync(disabledItem, enabled: true, CancellationToken.None));
            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out var enabledBytes,
                out _));
            Assert.NotNull(enabledBytes);
            Assert.Equal(0x02, enabledBytes![0]);

            var backup = new BackupRecord
            {
                StateKind = state.Kind,
                StateJson = state.Json
            };
            await provider.RestoreStateAsync(backup, isElevated: false, CancellationToken.None);

            // No approval value existed before capture, so restore deletes it again.
            Assert.False(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out _,
                out var restoredExists));
            Assert.False(restoredExists);
            Assert.True(await provider.VerifyEnabledAsync(item, enabled: true, CancellationToken.None));
        }
        finally
        {
            DeleteRunValue(valueName);
            StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);
        }
    }

    [Fact]
    public async Task Restore_writes_the_exact_original_approval_bytes()
    {
        var valueName = "StartupManagerTest_" + Guid.NewGuid().ToString("N");
        var command = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c exit";
        var original = new byte[] { 0x02, 0x01, 0x00, 0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80 };
        var provider = new RegistryStartupProvider();

        try
        {
            WriteRunValue(valueName, command);
            StartupApproved.WriteApproval(StartupApprovalFamily.Run, valueName, original);

            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Name == valueName);
            Assert.True(item.Enabled);

            var state = await provider.CaptureStateAsync(item, CancellationToken.None);
            var captured = JsonSerializer.Deserialize<RegistryApprovalState>(
                state.Json,
                JsonDefaults.Options);
            Assert.NotNull(captured);
            Assert.True(captured!.ApprovalValueExisted);
            Assert.Equal(Convert.ToBase64String(original), captured.ApprovalRawBase64);
            Assert.Contains("原本已启用", state.Summary);

            await provider.ApplyEnabledAsync(item, enabled: false, isElevated: false, CancellationToken.None);
            Assert.True(await provider.VerifyEnabledAsync(item, enabled: false, CancellationToken.None));

            await provider.RestoreStateAsync(
                new BackupRecord { StateKind = state.Kind, StateJson = state.Json },
                isElevated: false,
                CancellationToken.None);

            Assert.True(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out var restored,
                out var restoredExists));
            Assert.True(restoredExists);
            Assert.Equal(original, restored);
        }
        finally
        {
            DeleteRunValue(valueName);
            StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);
        }
    }

    [Fact]
    public async Task Apply_throws_when_the_run_value_disappeared_after_scan()
    {
        var valueName = "StartupManagerTest_" + Guid.NewGuid().ToString("N");
        var command = "\"" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + "\" /c exit";
        var provider = new RegistryStartupProvider();

        try
        {
            WriteRunValue(valueName, command);
            var item = Assert.Single(
                await provider.ScanAsync(CancellationToken.None),
                candidate => candidate.Name == valueName);

            DeleteRunValue(valueName);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.ApplyEnabledAsync(item, enabled: false, isElevated: false, CancellationToken.None));

            // Safety: no orphan approval value is created for a vanished source.
            Assert.False(StartupApproved.TryReadApproval(
                StartupApprovalFamily.Run,
                valueName,
                out _,
                out var exists));
            Assert.False(exists);
        }
        finally
        {
            DeleteRunValue(valueName);
            StartupApproved.DeleteApproval(StartupApprovalFamily.Run, valueName);
        }
    }

    [Fact]
    public void Provider_exposes_a_parameterless_constructor_for_integration()
    {
        Assert.NotNull(typeof(RegistryStartupProvider).GetConstructor(Type.EmptyTypes));
        Assert.Equal(StartupSource.Registry, new RegistryStartupProvider().Source);
    }

    [Fact]
    public async Task Restore_rejects_a_backup_from_another_provider()
    {
        var provider = new RegistryStartupProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.RestoreStateAsync(
                new BackupRecord { StateKind = "folderApproval", StateJson = "{}" },
                isElevated: false,
                CancellationToken.None));
    }

    private static void WriteRunValue(string valueName, string command)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var runKey = baseKey.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the HKCU Run key.");
        runKey.SetValue(valueName, command, RegistryValueKind.String);
    }

    private static void DeleteRunValue(string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var runKey = baseKey.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Best-effort cleanup; the unique value name prevents collisions.
        }
    }
}
