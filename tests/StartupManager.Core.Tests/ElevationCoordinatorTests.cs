using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Elevation;
using StartupManager.Core.Models;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ElevationCoordinatorTests
{
    [Fact]
    public async Task Elevation_coordinator_launches_runas_helper_and_reads_response()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher();
        var request = ValidSetEnabledRequest();

        launcher.Handler = async startInfo =>
        {
            var requestPath = FakeElevatedLauncher.RequestPathFrom(startInfo);
            Assert.True(File.Exists(requestPath), "request file should exist while the helper runs");

            var json = await File.ReadAllTextAsync(requestPath);
            var parsed = JsonSerializer.Deserialize<ElevatedRequest>(json, JsonDefaults.Options);
            Assert.NotNull(parsed);
            Assert.Equal(request.RequestId, parsed!.RequestId);
            Assert.Equal(request.Nonce, parsed.Nonce);
            Assert.Equal(ElevatedOperations.SetEnabled, parsed.Operation);
            Assert.Equal(request.ItemIds, parsed.ItemIds);
            Assert.False(parsed.Enabled);

            var response = new ElevatedResponse
            {
                Success = true,
                Message = "已关闭自启动。",
                Results = new List<ChangeResult>
                {
                    new() { Success = true, Verified = true, ItemId = request.ItemIds[0] }
                }
            };

            await File.WriteAllTextAsync(
                FakeElevatedLauncher.ResponsePathFor(requestPath),
                JsonSerializer.Serialize(response, JsonDefaults.Options));

            return 0;
        };

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(request, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Single(response.Results);
        Assert.Equal(1, launcher.RunCalls);
        Assert.NotNull(launcher.SeenStartInfo);

        var startInfo = launcher.SeenStartInfo!;
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.EndsWith("StartupManager.Elevated.exe", startInfo.FileName);
        Assert.Contains("--request", startInfo.Arguments);
        Assert.Contains("req-", startInfo.Arguments);

        Assert.Empty(Directory.GetFiles(paths.Paths.IpcDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Elevation_coordinator_maps_uac_cancellation_to_canceled_response()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher
        {
            Handler = _ => throw new Win32Exception(1223)
        };

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.True(response.Canceled);
        Assert.Contains("UAC", response.Message);
        Assert.Empty(Directory.GetFiles(paths.Paths.IpcDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Elevation_coordinator_reports_timeout_and_cleans_up()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher
        {
            Handler = _ => throw new TimeoutException()
        };

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.False(response.Canceled);
        Assert.Contains("限定时间", response.Message);
        Assert.Empty(Directory.GetFiles(paths.Paths.IpcDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Elevation_coordinator_reports_unexpected_launcher_failure()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher
        {
            Handler = _ => throw new InvalidOperationException("no process")
        };

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("无法启动", response.Message);
        Assert.Empty(Directory.GetFiles(paths.Paths.IpcDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Elevation_coordinator_fails_when_helper_is_missing()
    {
        using var paths = new TestPaths();
        var launcher = new FakeElevatedLauncher();
        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            Path.Combine(paths.Root, "missing", "StartupManager.Elevated.exe"));

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("提权助手", response.Message);
        Assert.Equal(0, launcher.RunCalls);
    }

    [Fact]
    public async Task Elevation_coordinator_fails_when_helper_writes_no_response()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher(); // exits 0 without writing a response

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("未返回", response.Message);
        Assert.Empty(Directory.GetFiles(paths.Paths.IpcDirectory, "*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Elevation_coordinator_rejects_invalid_request_without_launching()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher();
        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var expired = ValidSetEnabledRequest();
        expired.ExpiresAt = DateTimeOffset.Now.AddMinutes(-10);

        var response = await coordinator.ExecuteElevatedAsync(expired, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("过期", response.Message);
        Assert.Equal(0, launcher.RunCalls);
    }

    [Fact]
    public async Task Elevation_coordinator_returns_helper_reported_failure()
    {
        using var paths = new TestPaths();
        var helper = await CreateDummyHelperAsync(paths);
        var launcher = new FakeElevatedLauncher
        {
            Handler = async startInfo =>
            {
                var requestPath = FakeElevatedLauncher.RequestPathFrom(startInfo);
                var response = new ElevatedResponse
                {
                    Success = false,
                    Message = "受保护项已拒绝。"
                };
                await File.WriteAllTextAsync(
                    FakeElevatedLauncher.ResponsePathFor(requestPath),
                    JsonSerializer.Serialize(response, JsonDefaults.Options));
                return 1;
            }
        };

        var coordinator = new ElevationCoordinator(
            paths.Paths,
            launcher,
            TimeSpan.FromSeconds(10),
            helper);

        var response = await coordinator.ExecuteElevatedAsync(ValidSetEnabledRequest(), CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("受保护", response.Message);
    }

    private static async Task<string> CreateDummyHelperAsync(TestPaths paths)
    {
        var helper = Path.Combine(paths.Root, "StartupManager.Elevated.exe");
        await File.WriteAllTextAsync(helper, "dummy");
        return helper;
    }

    private static ElevatedRequest ValidSetEnabledRequest()
    {
        return new ElevatedRequest
        {
            Operation = ElevatedOperations.SetEnabled,
            RequestId = Guid.NewGuid().ToString("N"),
            Nonce = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.Now.AddMinutes(5),
            ItemIds = new List<string> { TestIds.NewId(StartupSource.Registry) },
            Enabled = false
        };
    }
}
