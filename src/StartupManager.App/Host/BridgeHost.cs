using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StartupManager.Core;
using StartupManager.Core.Elevation;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>
/// WebView2 bridge implementing docs/CONTRACT.md sections 4/5. All commands are
/// serialized; a mutation always reloads the snapshot and backups and sends a
/// full state message before its command result.
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    private const string UnavailableMessage =
        "本地服务尚未就绪（CoreComposition.CreateManager() 未接线），暂时无法读取或更改启动项。";

    private readonly WebView2 _webView;
    private readonly HostBackend _backend;
    private readonly IconService _icons = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private CoreWebView2? _core;
    private StartupSnapshot? _snapshot;
    private IReadOnlyList<BackupSummary> _backups = Array.Empty<BackupSummary>();
    private bool _disposed;

    public BridgeHost(WebView2 webView, HostBackend backend)
    {
        _webView = webView;
        _backend = backend;
    }

    public AppSettings CurrentSettings { get; private set; } = new();

    public void Attach()
    {
        var core = _webView.CoreWebView2
                   ?? throw new InvalidOperationException("WebView2 核心尚未初始化。");
        if (_core is not null)
        {
            return;
        }

        _core = core;
        core.WebMessageReceived += OnWebMessageReceived;

        try
        {
            var manager = _backend.Manager;
            if (manager is not null)
            {
                CurrentSettings = Clone(manager.Settings);
                ApplyUiFontScale(CurrentSettings.UiFontScale);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read initial settings.", ex);
        }

        AppLog.Info("Bridge attached.");
    }

    /// <summary>Used by the tray menu to rescan outside a web command.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed || _core is null)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(_cts.Token);
            try
            {
                if (_backend.Manager is null)
                {
                    SendUnavailableState();
                    return;
                }

                await RefreshCoreAsync(announceScan: true, requestId: null, _cts.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray rescan failed.", ex);
            SendError(null, "刷新失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_core is not null)
            {
                _core.WebMessageReceived -= OnWebMessageReceived;
            }
        }
        catch
        {
            // best effort
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // best effort
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.WebMessageAsJson;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read a web message.", ex);
            return;
        }

        await HandleMessageAsync(json);
    }

    private async Task HandleMessageAsync(string json)
    {
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = GetString(root, "type");
            if (string.Equals(type, "ready", StringComparison.Ordinal))
            {
                await _gate.WaitAsync(_cts.Token);
                try
                {
                    await OnReadyAsync(_cts.Token);
                }
                finally
                {
                    _gate.Release();
                }

                return;
            }

            if (!string.Equals(type, "command", StringComparison.Ordinal))
            {
                AppLog.Info($"Ignoring unknown bridge message type '{type ?? "<null>"}'.");
                SendError(null, "不支持的消息类型。");
                return;
            }

            requestId = GetString(root, "requestId");
            var command = GetString(root, "command");
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(command))
            {
                SendError(requestId, "命令格式无效：缺少 requestId 或 command。");
                return;
            }

            AppLog.Info($"Bridge command: {command} ({requestId}).");
            await _gate.WaitAsync(_cts.Token);
            try
            {
                await DispatchCommandAsync(root, requestId, command, _cts.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (JsonException ex)
        {
            AppLog.Error("Bridge message is not valid JSON.", ex);
            SendError(null, "消息格式无效。");
        }
        catch (Exception ex)
        {
            AppLog.Error("Bridge message handling failed.", ex);
            var message = "处理请求时发生错误：" + ex.Message;
            if (requestId is not null)
            {
                SendResult(requestId, false, message);
            }

            SendError(requestId, message);
        }
    }

    private async Task OnReadyAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("Web UI reported ready.");
        ApplyUiFontScale(CurrentSettings.UiFontScale);
        if (_backend.Manager is null)
        {
            SendUnavailableState();
            return;
        }

        await RefreshCoreAsync(announceScan: true, requestId: null, cancellationToken);
    }

    private async Task DispatchCommandAsync(
        JsonElement root,
        string requestId,
        string command,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "refresh":
                if (RequireManager(requestId) is null)
                {
                    return;
                }

                await RefreshCoreAsync(announceScan: true, requestId: requestId, cancellationToken);
                break;

            case "setEnabled":
                await SetEnabledAsync(root, requestId, cancellationToken);
                break;

            case "restoreBackup":
                await RestoreBackupAsync(root, requestId, cancellationToken);
                break;

            case "saveSettings":
                await SaveSettingsAsync(root, requestId, cancellationToken);
                break;

            case "setUserOverride":
                await SetUserOverrideAsync(root, requestId, cancellationToken);
                break;

            case "openItemLocation":
                await OpenItemLocationAsync(root, requestId);
                break;

            case "openBackupFolder":
                await OpenDirectoryAsync(requestId, GetBackupDirectory());
                break;

            case "openLogFolder":
                await OpenDirectoryAsync(requestId, GetLogDirectory());
                break;

            case "copyLocation":
                await CopyLocationAsync(root, requestId);
                break;

            default:
                var message = "不支持的命令：" + command;
                SendResult(requestId, false, message);
                SendError(requestId, message);
                break;
        }
    }

    private async Task RefreshCoreAsync(bool announceScan, string? requestId, CancellationToken cancellationToken)
    {
        if (announceScan)
        {
            Send(new { Type = "scanStarted" });
        }

        var ok = false;
        string? error = null;
        var warnings = new List<string>();
        StartupSnapshot? snapshot = null;
        IReadOnlyList<BackupSummary> backups = Array.Empty<BackupSummary>();
        try
        {
            var manager = _backend.Manager!;
            snapshot = await manager.ScanAsync(cancellationToken);
            backups = await LoadBackupsAsync(manager, warnings, cancellationToken);
            await EnrichIconsAsync(snapshot, cancellationToken);

            _snapshot = snapshot;
            _backups = backups;
            TryRefreshSettings(manager);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = "扫描启动项失败：" + ex.Message;
            AppLog.Error("Scan failed.", ex);
        }

        if (announceScan)
        {
            Send(new { Type = "scanFinished", Ok = ok, Message = error });
        }

        if (ok && snapshot is not null)
        {
            SendState(snapshot, backups, warnings.Count == 0 ? null : string.Join(" ", warnings));
        }

        if (requestId is not null)
        {
            if (ok)
            {
                SendResult(requestId, true, "已刷新。");
            }
            else
            {
                SendResult(requestId, false, error);
                SendError(requestId, error ?? "扫描失败。");
            }
        }
        else if (!ok)
        {
            SendError(null, error ?? "扫描失败。");
        }
    }

    private async Task SetEnabledAsync(JsonElement root, string requestId, CancellationToken cancellationToken)
    {
        var manager = RequireManager(requestId);
        if (manager is null)
        {
            return;
        }

        if (!root.TryGetProperty("itemIds", out var idsElement) || idsElement.ValueKind != JsonValueKind.Array)
        {
            Reject(requestId, "setEnabled 缺少 itemIds 数组。");
            return;
        }

        var ids = new List<string>();
        foreach (var element in idsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                Reject(requestId, "itemIds 必须由字符串组成。");
                return;
            }

            var id = element.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                Reject(requestId, "itemIds 中包含空 ID。");
                return;
            }

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            Reject(requestId, "未选择任何启动项。");
            return;
        }

        if (!root.TryGetProperty("enabled", out var enabledElement)
            || (enabledElement.ValueKind != JsonValueKind.True && enabledElement.ValueKind != JsonValueKind.False))
        {
            Reject(requestId, "setEnabled 缺少布尔值 enabled。");
            return;
        }

        var enabled = enabledElement.GetBoolean();
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            Reject(requestId, "尚未完成扫描，请刷新后重试。");
            return;
        }

        var byId = snapshot.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var targets = new List<StartupItem>(ids.Count);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item))
            {
                Reject(requestId, "启动项不存在或已变化，请刷新后重试。");
                return;
            }

            targets.Add(item);
        }

        var protectedItem = targets.FirstOrDefault(item => item.Protected);
        if (protectedItem is not null)
        {
            var detail = string.IsNullOrWhiteSpace(protectedItem.ProtectionReason)
                ? string.Empty
                : " " + protectedItem.ProtectionReason;
            Reject(requestId, $"“{protectedItem.Name}”受系统保护，无法更改。{detail}");
            return;
        }

        var requiresElevation = targets.Any(item => item.RequiresAdmin);
        if (requiresElevation && !_backend.IsElevated)
        {
            await RunElevatedSetEnabledAsync(requestId, ids, enabled, cancellationToken);
            return;
        }

        try
        {
            var result = await manager.SetEnabledAsync(ids, enabled, _backend.IsElevated, cancellationToken);
            if (result.RequiresElevation && !_backend.IsElevated)
            {
                await RunElevatedSetEnabledAsync(requestId, ids, enabled, cancellationToken);
                return;
            }

            var success = result.AllSucceeded;
            if (result.SucceededCount > 0)
            {
                await ReloadAfterMutationAsync(cancellationToken);
            }

            var message = BuildBatchMessage(result, enabled);
            SendResult(requestId, success, message, result.RequiresElevation, result.Canceled);
            if (!success)
            {
                SendError(requestId, message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("setEnabled failed.", ex);
            Reject(requestId, "更改启动项失败：" + ex.Message);
        }
    }

    private async Task RunElevatedSetEnabledAsync(
        string requestId,
        IReadOnlyList<string> ids,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var coordinator = _backend.Elevation;
        if (coordinator is null)
        {
            RejectWithFlags(requestId, "此操作需要管理员权限，但提权组件尚未接线。", requiresElevation: true, canceled: false);
            return;
        }

        var request = new ElevatedRequest
        {
            Operation = ElevatedOperations.SetEnabled,
            ItemIds = ids.ToList(),
            Enabled = enabled
        };
        await RunElevatedAsync(requestId, coordinator, request, cancellationToken);
    }

    private async Task RunElevatedRestoreAsync(string requestId, string backupId, CancellationToken cancellationToken)
    {
        var coordinator = _backend.Elevation;
        if (coordinator is null)
        {
            RejectWithFlags(requestId, "恢复此备份需要管理员权限，但提权组件尚未接线。", requiresElevation: true, canceled: false);
            return;
        }

        var request = new ElevatedRequest
        {
            Operation = ElevatedOperations.RestoreBackup,
            BackupId = backupId
        };
        await RunElevatedAsync(requestId, coordinator, request, cancellationToken);
    }

    private async Task RunElevatedAsync(
        string requestId,
        IElevationCoordinator coordinator,
        ElevatedRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await coordinator.ExecuteElevatedAsync(request, cancellationToken);
            if (response.Canceled)
            {
                const string canceledMessage = "UAC 已取消，未做任何更改。";
                SendResult(requestId, false, canceledMessage, requiresElevation: true, elevationCanceled: true);
                SendError(requestId, canceledMessage);
                return;
            }

            if (!response.Success)
            {
                var detail = response.Message;
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = response.Results.FirstOrDefault(result => !result.Success)?.Error;
                }

                var message = string.IsNullOrWhiteSpace(detail) ? "管理员操作未成功完成。" : detail;
                SendResult(requestId, false, message, requiresElevation: true, elevationCanceled: false);
                SendError(requestId, message);
                return;
            }

            await ReloadAfterMutationAsync(cancellationToken);
            var successMessage = string.IsNullOrWhiteSpace(response.Message) ? "更改已完成。" : response.Message;
            SendResult(requestId, true, successMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Elevated operation failed.", ex);
            var message = "无法完成管理员操作：" + ex.Message;
            SendResult(requestId, false, message, requiresElevation: true, elevationCanceled: false);
            SendError(requestId, message);
        }
    }

    private async Task RestoreBackupAsync(JsonElement root, string requestId, CancellationToken cancellationToken)
    {
        var manager = RequireManager(requestId);
        if (manager is null)
        {
            return;
        }

        var backupId = GetString(root, "backupId");
        if (string.IsNullOrWhiteSpace(backupId))
        {
            Reject(requestId, "restoreBackup 缺少 backupId。");
            return;
        }

        var backup = _backups.FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.Ordinal));
        if (backup is null)
        {
            try
            {
                var warnings = new List<string>();
                _backups = await LoadBackupsAsync(manager, warnings, cancellationToken);
                backup = _backups.FirstOrDefault(item => string.Equals(item.Id, backupId, StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not refresh backups before restore.", ex);
            }
        }

        if (backup is null)
        {
            Reject(requestId, "备份不存在或已失效，请刷新后重试。");
            return;
        }

        if (backup.Status != BackupStatus.Active)
        {
            Reject(requestId, "该备份已恢复或不可用。");
            return;
        }

        if (backup.RequiresAdmin && !_backend.IsElevated)
        {
            await RunElevatedRestoreAsync(requestId, backupId, cancellationToken);
            return;
        }

        try
        {
            var result = await manager.RestoreBackupAsync(backupId, _backend.IsElevated, cancellationToken);
            if (result.RequiresElevation && !_backend.IsElevated)
            {
                await RunElevatedRestoreAsync(requestId, backupId, cancellationToken);
                return;
            }

            if (result.Success)
            {
                await ReloadAfterMutationAsync(cancellationToken);
                SendResult(requestId, true, result.Message ?? $"已恢复“{backup.DisplayName}”。");
            }
            else
            {
                var message = result.Error ?? result.Message ?? "恢复失败。";
                SendResult(requestId, false, message, result.RequiresElevation, result.Canceled);
                SendError(requestId, message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("restoreBackup failed.", ex);
            Reject(requestId, "恢复备份失败：" + ex.Message);
        }
    }

    private async Task SetUserOverrideAsync(
        JsonElement root,
        string requestId,
        CancellationToken cancellationToken)
    {
        var manager = RequireManager(requestId);
        if (manager is null)
        {
            return;
        }

        var itemId = GetString(root, "itemId");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            Reject(requestId, "setUserOverride 缺少 itemId。");
            return;
        }

        if (!root.TryGetProperty("allowed", out var allowedElement)
            || (allowedElement.ValueKind != JsonValueKind.True && allowedElement.ValueKind != JsonValueKind.False))
        {
            Reject(requestId, "setUserOverride 缺少布尔值 allowed。");
            return;
        }

        var allowed = allowedElement.GetBoolean();
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            Reject(requestId, "尚未完成扫描，请刷新后重试。");
            return;
        }

        var item = snapshot.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            Reject(requestId, "启动项不存在或已变化，请刷新后重试。");
            return;
        }

        if (allowed && (!item.CanOverride || (!item.Protected && !item.UserAllowed)))
        {
            Reject(requestId, "此项目不属于可解除的未验证保护项。");
            return;
        }

        if (!allowed && !item.UserAllowed)
        {
            Reject(requestId, "此项目当前没有用户允许记录。");
            return;
        }

        try
        {
            var settings = Clone(manager.Settings);
            var ids = settings.UserAllowedItemIds;
            if (allowed)
            {
                if (!ids.Contains(itemId, StringComparer.Ordinal))
                {
                    ids.Add(itemId);
                }
            }
            else
            {
                ids.RemoveAll(id => string.Equals(id, itemId, StringComparison.Ordinal));
            }

            await manager.SaveSettingsAsync(settings, cancellationToken);
            CurrentSettings = Clone(manager.Settings);
            await ReloadAfterMutationAsync(cancellationToken);

            var message = allowed
                ? "已允许管理“" + item.Name + "”。修改时仍会先备份并验证。"
                : "已恢复“" + item.Name + "”的系统保护。";
            SendResult(requestId, true, message);
            AppLog.Info(allowed
                ? $"User allowed management of {item.Name}."
                : $"User restored protection for {item.Name}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("setUserOverride failed.", ex);
            Reject(requestId, "更新项目保护状态失败：" + ex.Message);
        }
    }

    private async Task SaveSettingsAsync(JsonElement root, string requestId, CancellationToken cancellationToken)
    {
        var manager = RequireManager(requestId);
        if (manager is null)
        {
            return;
        }

        if (!root.TryGetProperty("settings", out var settingsElement) || settingsElement.ValueKind != JsonValueKind.Object)
        {
            Reject(requestId, "saveSettings 缺少 settings 对象。");
            return;
        }

        AppSettings? settings;
        try
        {
            settings = settingsElement.Deserialize<AppSettings>(JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            AppLog.Error("Invalid settings payload.", ex);
            Reject(requestId, "设置格式无效。");
            return;
        }

        if (settings is null)
        {
            Reject(requestId, "设置内容为空。");
            return;
        }

        settings.BackupRetentionDays = settings.BackupRetentionDays <= 0
            ? 30
            : Math.Clamp(settings.BackupRetentionDays, 1, 3650);

        try
        {
            await manager.SaveSettingsAsync(settings, cancellationToken);
            CurrentSettings = Clone(settings);

            string? registrationError = null;
            try
            {
                StartupRegistration.Apply(settings.StartWithWindows);
            }
            catch (Exception ex)
            {
                registrationError = ex.Message;
                AppLog.Error("Could not update the start-with-Windows Run value.", ex);
            }

            TryRefreshSettings(manager);
            ApplyUiFontScale(CurrentSettings.UiFontScale);
            Send(new { Type = "settings", Settings = CurrentSettings });

            if (_snapshot is not null)
            {
                await ReloadAfterMutationAsync(cancellationToken);
            }

            if (registrationError is null)
            {
                SendResult(requestId, true, "设置已保存。");
            }
            else
            {
                var message = "设置已保存，但无法更新开机启动项：" + registrationError;
                SendResult(requestId, false, message);
                SendError(requestId, message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("saveSettings failed.", ex);
            Reject(requestId, "保存设置失败：" + ex.Message);
        }
    }

    private Task OpenItemLocationAsync(JsonElement root, string requestId)
    {
        var item = RequireItem(root, requestId);
        if (item is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            ShellLauncher.OpenItemLocation(item);
            SendResult(requestId, true, null);
        }
        catch (Exception ex)
        {
            AppLog.Error("openItemLocation failed.", ex);
            Reject(requestId, "无法打开位置：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task OpenDirectoryAsync(string requestId, string path)
    {
        try
        {
            ShellLauncher.OpenDirectory(path);
            SendResult(requestId, true, null);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open a directory.", ex);
            Reject(requestId, "无法打开文件夹：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task CopyLocationAsync(JsonElement root, string requestId)
    {
        var item = RequireItem(root, requestId);
        if (item is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var text = ShellLauncher.BuildLocationText(item);
            ShellLauncher.CopyTextToClipboard(text);
            SendResult(requestId, true, "已复制位置到剪贴板。");
        }
        catch (Exception ex)
        {
            AppLog.Error("copyLocation failed.", ex);
            Reject(requestId, "复制失败：" + ex.Message);
        }

        return Task.CompletedTask;
    }

    private StartupItem? RequireItem(JsonElement root, string requestId)
    {
        var itemId = GetString(root, "itemId");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            Reject(requestId, "缺少 itemId。");
            return null;
        }

        var snapshot = _snapshot;
        if (snapshot is null)
        {
            Reject(requestId, "尚未完成扫描，请刷新后重试。");
            return null;
        }

        var item = snapshot.Items.FirstOrDefault(candidate => string.Equals(candidate.Id, itemId, StringComparison.Ordinal));
        if (item is null)
        {
            Reject(requestId, "启动项不存在或已变化，请刷新后重试。");
        }

        return item;
    }

    private IStartupManager? RequireManager(string requestId)
    {
        var manager = _backend.Manager;
        if (manager is not null)
        {
            return manager;
        }

        Reject(requestId, BuildUnavailableMessage());
        return null;
    }

    private string BuildUnavailableMessage()
    {
        var detail = _backend.ManagerError;
        return string.IsNullOrWhiteSpace(detail) ? UnavailableMessage : UnavailableMessage + " 详情：" + detail;
    }

    private void SendUnavailableState()
    {
        var message = BuildUnavailableMessage();
        var snapshot = new StartupSnapshot
        {
            Items = Array.Empty<StartupItem>(),
            ScannedAt = DateTimeOffset.Now,
            Warnings = new[] { message },
            IsElevated = _backend.IsElevated
        };

        CurrentSettings = new AppSettings();
        SendState(snapshot, Array.Empty<BackupSummary>(), null);
        SendError(null, message);
    }

    private void SendState(StartupSnapshot snapshot, IReadOnlyList<BackupSummary> backups, string? extraWarning)
    {
        var warnings = snapshot.Warnings.ToList();
        if (!string.IsNullOrWhiteSpace(extraWarning))
        {
            warnings.Add(extraWarning);
        }

        var backupDirectory = _backend.Paths.BackupDirectory;
        var logDirectory = _backend.Paths.LogDirectory;
        var manager = _backend.Manager;
        if (manager is not null)
        {
            try
            {
                backupDirectory = manager.BackupDirectory;
                logDirectory = manager.LogDirectory;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not read directories from IStartupManager.", ex);
            }
        }

        Send(new
        {
            Type = "state",
            Snapshot = new
            {
                Items = snapshot.Items.ToList(),
                ScannedAt = snapshot.ScannedAt,
                Warnings = warnings,
                IsElevated = _backend.IsElevated,
                EnabledCount = snapshot.EnabledCount,
                DisabledCount = snapshot.DisabledCount,
                ProtectedCount = snapshot.ProtectedCount,
                AdminCount = snapshot.AdminCount
            },
            Backups = backups.ToList(),
            Settings = CurrentSettings,
            System = new
            {
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                BackupDirectory = backupDirectory,
                LogDirectory = logDirectory
            }
        });
    }

    private async Task ReloadAfterMutationAsync(CancellationToken cancellationToken)
    {
        var manager = _backend.Manager;
        if (manager is null)
        {
            return;
        }

        try
        {
            var snapshot = await manager.ScanAsync(cancellationToken);
            var warnings = new List<string>();
            var backups = await LoadBackupsAsync(manager, warnings, cancellationToken);
            await EnrichIconsAsync(snapshot, cancellationToken);
            _snapshot = snapshot;
            _backups = backups;
            TryRefreshSettings(manager);
            SendState(snapshot, backups, warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Post-change rescan failed.", ex);
            SendError(null, "更改已提交，但刷新状态失败：" + ex.Message);
        }
    }

    private Task EnrichIconsAsync(StartupSnapshot snapshot, CancellationToken cancellationToken)
    {
        return Task.Run(() => _icons.Enrich(snapshot.Items), cancellationToken);
    }

    private static async Task<IReadOnlyList<BackupSummary>> LoadBackupsAsync(
        IStartupManager manager,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await manager.GetBackupsAsync(includeRestored: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load backups.", ex);
            warnings.Add("读取备份记录失败：" + ex.Message);
            return Array.Empty<BackupSummary>();
        }
    }

    private void TryRefreshSettings(IStartupManager manager)
    {
        try
        {
            CurrentSettings = Clone(manager.Settings);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read settings.", ex);
        }
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        IncludeRegistry = settings.IncludeRegistry,
        IncludeStartupFolders = settings.IncludeStartupFolders,
        IncludeScheduledTasks = settings.IncludeScheduledTasks,
        IncludeServices = settings.IncludeServices,
        StartWithWindows = settings.StartWithWindows,
        MinimizeToTray = settings.MinimizeToTray,
        BackupRetentionDays = settings.BackupRetentionDays,
        UiFontScale = settings.UiFontScale,
        UserAllowedItemIds = (settings.UserAllowedItemIds ?? new List<string>()).ToList()
    };

    private void ApplyUiFontScale(double scale)
    {
        var clamped = double.IsFinite(scale) ? Math.Clamp(scale, 0.8, 1.5) : 1.0;

        void Apply()
        {
            try
            {
                _webView.ZoomFactor = clamped;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not apply the UI font scale.", ex);
            }
        }

        try
        {
            if (_webView.Dispatcher.CheckAccess())
            {
                Apply();
            }
            else
            {
                _webView.Dispatcher.Invoke(Apply);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not dispatch the UI font scale.", ex);
        }
    }

    private string GetBackupDirectory()
    {
        var manager = _backend.Manager;
        if (manager is not null)
        {
            try
            {
                return manager.BackupDirectory;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not read the backup directory.", ex);
            }
        }

        return _backend.Paths.BackupDirectory;
    }

    private string GetLogDirectory()
    {
        var manager = _backend.Manager;
        if (manager is not null)
        {
            try
            {
                return manager.LogDirectory;
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not read the log directory.", ex);
            }
        }

        return _backend.Paths.LogDirectory;
    }

    private static string BuildBatchMessage(BatchChangeResult result, bool enabled)
    {
        var verb = enabled ? "已启用" : "已关闭";
        if (result.AllSucceeded)
        {
            return $"{verb} {result.SucceededCount} 项。";
        }

        var failed = result.Results.FirstOrDefault(change => !change.Success);
        var detail = failed?.Error ?? failed?.Message;
        if (result.SucceededCount == 0)
        {
            return string.IsNullOrWhiteSpace(detail) ? "未做任何更改。" : "未做任何更改。" + detail;
        }

        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail;
        return $"{verb} {result.SucceededCount} 项，{result.FailedCount} 项失败。" + suffix;
    }

    private void Reject(string requestId, string message)
    {
        SendResult(requestId, false, message);
        SendError(requestId, message);
    }

    private void RejectWithFlags(string requestId, string message, bool requiresElevation, bool canceled)
    {
        SendResult(requestId, false, message, requiresElevation, canceled);
        SendError(requestId, message);
    }

    private void SendResult(
        string? requestId,
        bool ok,
        string? message,
        bool requiresElevation = false,
        bool elevationCanceled = false)
    {
        Send(new
        {
            Type = "commandResult",
            RequestId = requestId,
            Ok = ok,
            Message = message,
            RequiresElevation = requiresElevation,
            ElevationCanceled = elevationCanceled
        });
    }

    private void SendError(string? requestId, string message)
    {
        Send(new
        {
            Type = "error",
            RequestId = requestId,
            Message = message
        });
    }

    private void Send(object payload)
    {
        if (_disposed)
        {
            return;
        }

        var core = _core;
        if (core is null)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not post a bridge message.", ex);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
