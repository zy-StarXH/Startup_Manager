using Microsoft.Win32;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;
using StartupManager.Core.Protection;
using StartupManager.Core.Providers;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class PackagedAppStartupProviderTests : IDisposable
{
    private readonly TestPaths _paths = new();
    private readonly string _root = @"Software\StartupManagerTests\Packages_" + Guid.NewGuid().ToString("N");
    private const string Family = "StartupManager.Test_abcd1234";
    private string KeyPath => _root + @"\" + Family + @"\App";

    private PackagedAppStartupProvider CreateProvider(bool defaultEnabled = true)
    {
        File.WriteAllText(Path.Combine(_paths.Root, "AppxManifest.xml"), $$"""
            <Package xmlns="urn:test" xmlns:uap5="urn:test:startup">
              <Identity Publisher="CN=Test Publisher" />
              <Applications><Application Id="App" Executable="test.exe">
                <Extensions><uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="App" Enabled="{{defaultEnabled.ToString().ToLowerInvariant()}}" DisplayName="Test App" />
                </uap5:Extension></Extensions>
              </Application></Applications>
            </Package>
            """);
        return new PackagedAppStartupProvider(_root, () => new[]
        {
            new PackagedAppStartupProvider.PackageIdentity(Family, "Test", _paths.Root, "Test App", "Test")
        });
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Enumerates_all_user_states_including_disabled_by_app(int state, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("State", state);
        var item = Assert.Single(await CreateProvider().ScanAsync(CancellationToken.None));
        Assert.Equal(enabled, item.Enabled);
        Assert.Equal(StartupSource.PackagedApp, item.Source);
        Assert.Equal("test.exe", item.Command);
        Assert.False(item.RequiresAdmin);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Manifest_default_is_used_when_windows_has_no_saved_state(bool enabled)
    {
        var provider = CreateProvider(enabled);
        var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
        Assert.Equal(enabled, item.Enabled);
        var captured = await provider.CaptureStateAsync(item, CancellationToken.None);
        Assert.Contains(enabled ? "原本已启用" : "原本已禁用", captured.Summary);
        await provider.ApplyEnabledAsync(item, !enabled, false, CancellationToken.None);
        Assert.True(await provider.VerifyEnabledAsync(item, !enabled, CancellationToken.None));
        await provider.RestoreStateAsync(Backup(item, captured), false, CancellationToken.None);
        Assert.Equal(enabled, Assert.Single(await provider.ScanAsync(CancellationToken.None)).Enabled);
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        Assert.Null(key?.GetValue("State"));
    }

    [Fact]
    public async Task Disable_survives_new_provider_and_restore_preserves_both_windows_values()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("State", 2);
        key.SetValue("UserEnabledStartupOnce", 0);
        var provider = CreateProvider();
        var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
        var captured = await provider.CaptureStateAsync(item, CancellationToken.None);
        await provider.ApplyEnabledAsync(item, false, false, CancellationToken.None);
        Assert.False(Assert.Single(await CreateProvider().ScanAsync(CancellationToken.None)).Enabled);
        Assert.Equal(1, key.GetValue("State"));
        await provider.RestoreStateAsync(Backup(item, captured), false, CancellationToken.None);
        Assert.Equal(2, key.GetValue("State"));
        Assert.Equal(0, key.GetValue("UserEnabledStartupOnce"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Policy_controlled_tasks_are_visible_locked_and_cannot_be_changed(int state)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("State", state);
        var provider = CreateProvider();
        var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
        var decision = new StartupProtectionPolicy().Evaluate(item);
        Assert.True(decision.IsProtected);
        Assert.False(decision.CanOverride);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ApplyEnabledAsync(item, false, false, CancellationToken.None));
        Assert.Equal(state, key.GetValue("State"));
    }

    [Fact]
    public async Task Removed_task_is_not_modified_using_stale_metadata()
    {
        var provider = CreateProvider();
        var item = Assert.Single(await provider.ScanAsync(CancellationToken.None));
        File.WriteAllText(Path.Combine(_paths.Root, "AppxManifest.xml"), "<Package />");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ApplyEnabledAsync(item, false, false, CancellationToken.None));
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        Assert.Null(key);
    }

    private static BackupRecord Backup(StartupItem item, StateSnapshot state) => new()
    {
        ItemId = item.Id, Source = item.Source, StateKind = state.Kind, StateJson = state.Json
    };

    [Fact]
    public async Task Malformed_windows_state_is_reported_and_preserved()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("State", "unexpected", RegistryValueKind.String);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider().ScanAsync(CancellationToken.None));
        Assert.Equal("unexpected", key.GetValue("State"));
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, false);
        _paths.Dispose();
    }
}
