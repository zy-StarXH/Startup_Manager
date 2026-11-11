using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Elevation;
using StartupManager.Core.Models;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ElevationRequestValidationTests
{
    [Fact]
    public void Elevation_valid_set_enabled_request_passes()
    {
        var request = ValidSetEnabledRequest();

        var result = ElevationRequestValidator.Validate(request, DateTimeOffset.Now);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void Elevation_valid_restore_request_passes()
    {
        var request = new ElevatedRequest
        {
            Operation = ElevatedOperations.RestoreBackup,
            BackupId = Guid.NewGuid().ToString("N"),
            RequestId = Guid.NewGuid().ToString("N"),
            Nonce = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.Now.AddMinutes(5)
        };

        Assert.True(ElevationRequestValidator.Validate(request, DateTimeOffset.Now).IsValid);
    }

    [Fact]
    public void Elevation_expired_request_is_rejected()
    {
        var request = ValidSetEnabledRequest();
        request.ExpiresAt = DateTimeOffset.Now.AddMinutes(-10);

        var result = ElevationRequestValidator.Validate(request, DateTimeOffset.Now);

        Assert.False(result.IsValid);
        Assert.Contains("过期", result.Error);
    }

    [Fact]
    public void Elevation_unknown_operation_is_rejected()
    {
        var request = ValidSetEnabledRequest();
        request.Operation = "runCommand";

        var result = ElevationRequestValidator.Validate(request, DateTimeOffset.Now);

        Assert.False(result.IsValid);
        Assert.Contains("不支持", result.Error);
    }

    [Fact]
    public void Elevation_malformed_request_id_and_nonce_are_rejected()
    {
        var badId = ValidSetEnabledRequest();
        badId.RequestId = "../escape";
        Assert.False(ElevationRequestValidator.Validate(badId, DateTimeOffset.Now).IsValid);

        var badNonce = ValidSetEnabledRequest();
        badNonce.Nonce = "not-a-nonce";
        var result = ElevationRequestValidator.Validate(badNonce, DateTimeOffset.Now);
        Assert.False(result.IsValid);
        Assert.Contains("随机数", result.Error);
    }

    [Fact]
    public void Elevation_empty_or_malformed_item_ids_are_rejected()
    {
        var empty = ValidSetEnabledRequest();
        empty.ItemIds.Clear();
        Assert.False(ElevationRequestValidator.Validate(empty, DateTimeOffset.Now).IsValid);

        var malformed = ValidSetEnabledRequest();
        malformed.ItemIds = new List<string> { "svc-1" };
        Assert.False(ElevationRequestValidator.Validate(malformed, DateTimeOffset.Now).IsValid);

        var tooMany = ValidSetEnabledRequest();
        tooMany.ItemIds = Enumerable.Range(0, 65)
            .Select(index => TestIds.For(StartupSource.Registry, "id-" + index))
            .ToList();
        var result = ElevationRequestValidator.Validate(tooMany, DateTimeOffset.Now);
        Assert.False(result.IsValid);
        Assert.Contains("过多", result.Error);
    }

    [Fact]
    public void Elevation_restore_requires_valid_backup_guid()
    {
        var missing = new ElevatedRequest
        {
            Operation = ElevatedOperations.RestoreBackup,
            BackupId = null,
            RequestId = Guid.NewGuid().ToString("N"),
            Nonce = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.Now.AddMinutes(5)
        };
        Assert.False(ElevationRequestValidator.Validate(missing, DateTimeOffset.Now).IsValid);

        var traversal = ValidSetEnabledRequest();
        traversal.Operation = ElevatedOperations.RestoreBackup;
        traversal.BackupId = "../../Windows";
        Assert.False(ElevationRequestValidator.Validate(traversal, DateTimeOffset.Now).IsValid);
    }

    [Fact]
    public async Task Elevation_host_rejects_request_path_outside_ipc_directory()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var factoryCalls = 0;
        var host = new ElevationHost(paths.Paths, () =>
        {
            factoryCalls++;
            return manager;
        });

        var outsidePath = Path.Combine(
            paths.Root,
            "req-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(
            outsidePath,
            JsonSerializer.Serialize(ValidSetEnabledRequest(), JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(outsidePath, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("IPC", response.Message);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(manager.SetEnabledItemIds);
    }

    [Fact]
    public async Task Elevation_host_rejects_request_with_invalid_file_name()
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(paths.Paths.IpcDirectory);
        var manager = new RecordingStartupManager();
        var factoryCalls = 0;
        var host = new ElevationHost(paths.Paths, () =>
        {
            factoryCalls++;
            return manager;
        });

        var path = Path.Combine(paths.Paths.IpcDirectory, "evil.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(ValidSetEnabledRequest(), JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(path, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("文件名", response.Message);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Elevation_host_rejects_malformed_json_and_does_not_run_manager()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var factoryCalls = 0;
        var host = new ElevationHost(paths.Paths, () =>
        {
            factoryCalls++;
            return manager;
        });

        var requestPath = await WriteRequestFileAsync(paths, "{ not-json");

        var response = await host.HandleRequestFileAsync(requestPath, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("格式", response.Message);
        Assert.Equal(0, factoryCalls);

        var written = await ReadResponseAsync(requestPath);
        Assert.NotNull(written);
        Assert.False(written!.Success);
    }

    [Fact]
    public async Task Elevation_host_rejects_expired_request_without_running_manager()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var factoryCalls = 0;
        var host = new ElevationHost(paths.Paths, () =>
        {
            factoryCalls++;
            return manager;
        });

        var expired = ValidSetEnabledRequest();
        expired.ExpiresAt = DateTimeOffset.Now.AddMinutes(-10);
        var requestPath = await WriteRequestFileAsync(paths, JsonSerializer.Serialize(expired, JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(requestPath, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("过期", response.Message);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(manager.SetEnabledItemIds);

        var written = await ReadResponseAsync(requestPath);
        Assert.NotNull(written);
        Assert.Contains("过期", written!.Message);
    }

    [Fact]
    public async Task Elevation_host_executes_set_enabled_with_elevated_true()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var host = new ElevationHost(paths.Paths, () => manager);
        var request = ValidSetEnabledRequest();
        var requestPath = await WriteRequestFileAsync(
            paths,
            JsonSerializer.Serialize(request, JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(requestPath, CancellationToken.None);

        Assert.True(response.Success, response.Message);
        Assert.True(manager.SetEnabledIsElevated);
        Assert.False(manager.SetEnabledEnabled);
        Assert.Equal(request.ItemIds, manager.SetEnabledItemIds);
        Assert.Single(response.Results);

        var written = await ReadResponseAsync(requestPath);
        Assert.NotNull(written);
        Assert.True(written!.Success);
    }

    [Fact]
    public async Task Elevation_host_executes_restore_backup_with_elevated_true()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var host = new ElevationHost(paths.Paths, () => manager);
        var backupId = Guid.NewGuid().ToString("N");
        var request = new ElevatedRequest
        {
            Operation = ElevatedOperations.RestoreBackup,
            BackupId = backupId,
            RequestId = Guid.NewGuid().ToString("N"),
            Nonce = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTimeOffset.Now.AddMinutes(5)
        };
        var requestPath = await WriteRequestFileAsync(
            paths,
            JsonSerializer.Serialize(request, JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(requestPath, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal(1, manager.RestoreCalls);
        Assert.True(manager.RestoreIsElevated);
        Assert.Equal(backupId, manager.RestoreBackupId);
        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Elevation_host_rejects_bad_operation_without_running_manager()
    {
        using var paths = new TestPaths();
        var manager = new RecordingStartupManager();
        var factoryCalls = 0;
        var host = new ElevationHost(paths.Paths, () =>
        {
            factoryCalls++;
            return manager;
        });

        var request = ValidSetEnabledRequest();
        request.Operation = "runCommand";
        var requestPath = await WriteRequestFileAsync(
            paths,
            JsonSerializer.Serialize(request, JsonDefaults.Options));

        var response = await host.HandleRequestFileAsync(requestPath, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("不支持", response.Message);
        Assert.Equal(0, factoryCalls);
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

    private static async Task<string> WriteRequestFileAsync(TestPaths paths, string json)
    {
        Directory.CreateDirectory(paths.Paths.IpcDirectory);
        var requestPath = ElevationProtocol.CreateRequestPath(
            paths.Paths,
            Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(requestPath, json);
        return requestPath;
    }

    private static async Task<ElevatedResponse?> ReadResponseAsync(string requestPath)
    {
        var responsePath = ElevationProtocol.GetResponsePath(requestPath);
        if (!File.Exists(responsePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ElevatedResponse>(
            await File.ReadAllTextAsync(responsePath),
            JsonDefaults.Options);
    }
}
