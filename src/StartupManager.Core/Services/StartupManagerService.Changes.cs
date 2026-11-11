using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

public sealed partial class StartupManagerService
{
    public async Task<BatchChangeResult> SetEnabledAsync(
        IReadOnlyCollection<string> itemIds,
        bool enabled,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return RejectBatch("未选择任何启动项。");
        }

        var ids = new List<string>(itemIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in itemIds)
        {
            if (!StartupItemIds.IsValid(rawId))
            {
                Log("setEnabled", null, "已拒绝格式无效的启动项标识。");
                return RejectBatch("启动项标识无效，已取消本次操作。");
            }

            if (seen.Add(rawId!))
            {
                ids.Add(rawId!);
            }
        }

        var outcome = await ScanCoreAsync(cancellationToken, respectSettings: true).ConfigureAwait(false);

        var byId = new Dictionary<string, StartupItem>(StringComparer.Ordinal);
        foreach (var item in outcome.Items)
        {
            byId[item.Id] = item;
        }

        var targets = new List<StartupItem>(ids.Count);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item))
            {
                Log("setEnabled", id, "目标不存在，已取消整批操作。");
                return RejectBatch("部分启动项已不存在，请刷新后重试。");
            }

            targets.Add(item);
        }

        if (targets.Any(target => target.Protected))
        {
            Log("setEnabled", null, "批次包含受系统保护的启动项，已取消整批操作。");
            return RejectBatch("包含受系统保护的启动项，已取消本次操作。");
        }

        // One UAC prompt per batch: if any target needs elevation, do not apply
        // the non-admin targets either. Nothing has been mutated at this point.
        if (!isElevated && targets.Any(target => target.RequiresAdmin))
        {
            Log("setEnabled", null, "需要管理员权限，未做任何更改。");
            return new BatchChangeResult
            {
                RequiresElevation = true,
                Message = "需要管理员权限，请确认 UAC 提示。"
            };
        }

        var results = new List<ChangeResult>(targets.Count);
        var elevationRequired = false;
        string? elevationMessage = null;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ChangeResult result;
            try
            {
                var activeForItem = outcome.ActiveBackups
                    .Where(backup => string.Equals(backup.ItemId, target.Id, StringComparison.Ordinal))
                    .OrderByDescending(backup => backup.CreatedAt)
                    .ToList();

                result = enabled
                    ? await EnableItemAsync(target, outcome.LiveIds, activeForItem, isElevated, cancellationToken)
                        .ConfigureAwait(false)
                    : await DisableItemAsync(target, activeForItem, isElevated, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(
                    "setEnabled",
                    target.Id,
                    "未处理的异常：" + ex.GetType().Name + "：" + ex.Message);
                result = ChangeResult.Fail(target.Id, "操作失败，未做任何更改。");
            }

            results.Add(result);
            Log(
                "setEnabled",
                target.Id,
                result.Success ? "成功" : "失败：" + (result.Error ?? result.Message ?? "未知错误"));

            if (result.RequiresElevation && !isElevated)
            {
                elevationRequired = true;
                elevationMessage = result.Message ?? "需要管理员权限，请确认 UAC 提示。";
                break;
            }
        }

        var message = BuildBatchMessage(enabled, results, elevationRequired, elevationMessage);
        Log("setEnabled", null, "批次结束：" + message);

        return new BatchChangeResult
        {
            RequiresElevation = elevationRequired,
            Message = message,
            Results = results
        };
    }

    public async Task<ChangeResult> RestoreBackupAsync(
        string backupId,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backupId))
        {
            return ChangeResult.Fail(null, "备份标识无效。");
        }

        BackupRecord? backup;
        try
        {
            backup = await _backupStore.GetAsync(backupId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("restore", backupId, "读取备份失败：" + ex.GetType().Name);
            return ChangeResult.Fail(null, "读取备份失败。");
        }

        if (backup is null)
        {
            return ChangeResult.Fail(null, "找不到该备份。");
        }

        if (backup.Status != BackupStatus.Active)
        {
            return ChangeResult.Fail(backup.ItemId, "该备份已恢复或不可用。");
        }

        if (backup.RequiresAdmin && !isElevated)
        {
            return ChangeResult.Fail(backup.ItemId, "需要管理员权限，请确认 UAC 提示。", requiresElevation: true);
        }

        var provider = GetChangeProvider(backup.Source);
        if (provider is null)
        {
            return ChangeResult.Fail(backup.ItemId, "暂不支持恢复该类型启动项。");
        }

        // Restore is a return to the exact state that existed before this app
        // changed it. Do not re-apply the protection policy here: backups can
        // only exist for items that were allowed to change in the first place,
        // and a synthetic backup-only item has no live metadata for a fair policy
        // evaluation. Admin requirements were captured in the backup record.
        try
        {
            await provider.RestoreStateAsync(backup, isElevated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            Log("restore", backup.ItemId, "恢复时被拒绝访问。");
            return ElevatedOrFail(backup.ItemId, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            Log("restore", backup.ItemId, "恢复失败：" + ex.GetType().Name + "：" + ex.Message);
            await MarkFailedAsync(backup, "恢复失败。", cancellationToken).ConfigureAwait(false);
            return ChangeResult.Fail(backup.ItemId, "恢复失败，请刷新后检查系统状态。");
        }

        bool verified;
        try
        {
            var after = await ScanCoreAsync(cancellationToken, respectSettings: false).ConfigureAwait(false);
            verified = after.Items.Any(
                item => string.Equals(item.Id, backup.ItemId, StringComparison.Ordinal) && item.Enabled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("restore", backup.ItemId, "恢复后验证失败：" + ex.GetType().Name);
            verified = false;
        }

        if (!verified)
        {
            await MarkFailedAsync(backup, "恢复后未能验证为已开启。", cancellationToken).ConfigureAwait(false);
            Log("restore", backup.ItemId, "恢复未通过验证。");
            return ChangeResult.Fail(backup.ItemId, "恢复未能通过验证，请刷新后检查。");
        }

        backup.Status = BackupStatus.Restored;
        backup.RestoredAt = DateTimeOffset.Now;
        backup.VerifiedAt = DateTimeOffset.Now;
        backup.Error = null;
        await TryUpdateAsync(backup, cancellationToken).ConfigureAwait(false);

        Log("restore", backup.ItemId, "已恢复并通过验证。");
        return new ChangeResult
        {
            Success = true,
            Verified = true,
            ItemId = backup.ItemId,
            BackupId = backup.Id,
            Message = "已恢复自启动。"
        };
    }

    public async Task<IReadOnlyList<BackupSummary>> GetBackupsAsync(
        bool includeRestored,
        CancellationToken cancellationToken)
    {
        try
        {
            var records = includeRestored
                ? await _backupStore.GetAllAsync(cancellationToken).ConfigureAwait(false)
                : await _backupStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);

            return records
                .Select(record => record.ToSummary())
                .OrderByDescending(summary => summary.CreatedAt)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("backups", null, "读取备份列表失败：" + ex.GetType().Name + "：" + ex.Message);
            return Array.Empty<BackupSummary>();
        }
    }

    private async Task<ChangeResult> DisableItemAsync(
        StartupItem item,
        IReadOnlyList<BackupRecord> activeBackups,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        if (!item.Enabled)
        {
            Log("disable", item.Id, "已处于关闭状态，无操作。");
            return NoOp(item.Id, activeBackups.FirstOrDefault()?.Id, "已处于关闭状态。");
        }

        var provider = GetChangeProvider(item.Source);
        if (provider is null)
        {
            return ChangeResult.Fail(item.Id, "暂不支持修改该类型启动项。");
        }

        StateSnapshot capture;
        try
        {
            capture = await provider.CaptureStateAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            Log("disable", item.Id, "读取原始状态时被拒绝访问。");
            return ElevatedOrFail(item.Id, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            Log("disable", item.Id, "读取原始状态失败：" + ex.GetType().Name);
            return ChangeResult.Fail(item.Id, "无法读取原始状态，已取消更改。");
        }

        if (capture is null ||
            string.IsNullOrWhiteSpace(capture.Kind) ||
            string.IsNullOrWhiteSpace(capture.Json))
        {
            Log("disable", item.Id, "原始状态快照无效。");
            return ChangeResult.Fail(item.Id, "无法读取原始状态，已取消更改。");
        }

        // Invariant: the backup must be persisted before the OS is touched.
        BackupRecord backup;
        try
        {
            backup = await _backupStore.CreateAsync(item, capture, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("disable", item.Id, "创建备份失败：" + ex.GetType().Name);
            return ChangeResult.Fail(item.Id, "无法创建备份，已取消更改。");
        }

        try
        {
            await provider.ApplyEnabledAsync(item, false, isElevated, cancellationToken).ConfigureAwait(false);

            bool verified;
            try
            {
                verified = await provider.VerifyEnabledAsync(item, false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log("disable", item.Id, "验证失败：" + ex.GetType().Name);
                verified = false;
            }

            if (!verified)
            {
                var rolledBack = await TryRollbackAsync(provider, backup, isElevated, cancellationToken)
                    .ConfigureAwait(false);
                await MarkFailedAsync(
                        backup,
                        rolledBack ? "更改未能通过验证，已恢复原状态。" : "更改未能通过验证，恢复原状态失败。",
                        cancellationToken)
                    .ConfigureAwait(false);

                Log("disable", item.Id, "验证未通过，回滚" + (rolledBack ? "成功。" : "失败。"));
                return ChangeResult.Fail(
                    item.Id,
                    rolledBack
                        ? "更改未能通过验证，已恢复原状态。"
                        : "更改未能通过验证，且恢复原状态失败，请检查系统状态。");
            }

            backup.VerifiedAt = DateTimeOffset.Now;
            await TryUpdateAsync(backup, cancellationToken).ConfigureAwait(false);

            Log("disable", item.Id, "已关闭并通过验证。");
            return new ChangeResult
            {
                Success = true,
                Verified = true,
                ItemId = item.Id,
                BackupId = backup.Id,
                Message = "已关闭自启动。"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("disable", item.Id, "操作已取消，正在尝试恢复原状态。");
            await TryRollbackAsync(provider, backup, isElevated, CancellationToken.None).ConfigureAwait(false);
            await MarkFailedAsync(backup, "操作已取消，已尝试恢复原状态。", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            var rolledBack = await TryRollbackAsync(provider, backup, isElevated, cancellationToken)
                .ConfigureAwait(false);
            await MarkFailedAsync(
                    backup,
                    rolledBack ? "拒绝访问，已恢复原状态。" : "拒绝访问，恢复原状态失败。",
                    cancellationToken)
                .ConfigureAwait(false);

            Log("disable", item.Id, "应用更改时被拒绝访问，回滚" + (rolledBack ? "成功。" : "失败。"));
            return ElevatedOrFail(item.Id, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            var rolledBack = await TryRollbackAsync(provider, backup, isElevated, cancellationToken)
                .ConfigureAwait(false);
            await MarkFailedAsync(
                    backup,
                    rolledBack ? "更改失败，已恢复原状态。" : "更改失败，恢复原状态失败。",
                    cancellationToken)
                .ConfigureAwait(false);

            Log(
                "disable",
                item.Id,
                "应用更改失败：" + ex.GetType().Name + "，回滚" + (rolledBack ? "成功。" : "失败。"));

            return ChangeResult.Fail(
                item.Id,
                rolledBack
                    ? "更改失败，已恢复原状态。"
                    : "更改失败，且恢复原状态失败，请检查系统状态。");
        }
    }

    private async Task<ChangeResult> EnableItemAsync(
        StartupItem item,
        IReadOnlySet<string> liveIds,
        IReadOnlyList<BackupRecord> activeBackups,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        var latest = activeBackups.FirstOrDefault();
        var isBackupOnly = item.ManagedByApp && !liveIds.Contains(item.Id);

        if (isBackupOnly)
        {
            return await EnableFromBackupAsync(item, latest, activeBackups, isElevated, cancellationToken)
                .ConfigureAwait(false);
        }

        if (item.Enabled)
        {
            foreach (var backup in activeBackups)
            {
                await MarkRestoredAsync(backup, cancellationToken).ConfigureAwait(false);
            }

            Log("enable", item.Id, "已处于开启状态，无操作。");
            return NoOp(item.Id, latest?.Id, "已处于开启状态。");
        }

        var provider = GetChangeProvider(item.Source);
        if (provider is null)
        {
            return ChangeResult.Fail(item.Id, "暂不支持修改该类型启动项。");
        }

        // Every live enable is transactional too: persist the current disabled
        // state before touching the OS, so a failed or partial enable can be
        // undone. When this app disabled the item, the pre-existing active
        // backup still records the original enabled state for the UI.
        StateSnapshot capture;
        try
        {
            capture = await provider.CaptureStateAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return ElevatedOrFail(item.Id, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            Log("enable", item.Id, "读取原始状态失败：" + ex.GetType().Name);
            return ChangeResult.Fail(item.Id, "无法读取原始状态，已取消更改。");
        }

        if (capture is null ||
            string.IsNullOrWhiteSpace(capture.Kind) ||
            string.IsNullOrWhiteSpace(capture.Json))
        {
            return ChangeResult.Fail(item.Id, "无法读取原始状态，已取消更改。");
        }

        BackupRecord transactionBackup;
        try
        {
            transactionBackup = await _backupStore.CreateAsync(item, capture, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("enable", item.Id, "创建备份失败：" + ex.GetType().Name);
            return ChangeResult.Fail(item.Id, "无法创建备份，已取消更改。");
        }

        try
        {
            await provider.ApplyEnabledAsync(item, true, isElevated, cancellationToken).ConfigureAwait(false);

            bool verified;
            try
            {
                verified = await provider.VerifyEnabledAsync(item, true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log("enable", item.Id, "验证失败：" + ex.GetType().Name);
                verified = false;
            }

            if (!verified)
            {
                var rolledBack = await TryRollbackAsync(provider, transactionBackup, isElevated, cancellationToken)
                    .ConfigureAwait(false);
                await MarkFailedAsync(
                        transactionBackup,
                        rolledBack ? "开启未能通过验证，已恢复原状态。" : "开启未能通过验证，恢复原状态失败。",
                        cancellationToken)
                    .ConfigureAwait(false);

                Log("enable", item.Id, "开启未通过验证，回滚" + (rolledBack ? "成功。" : "失败。"));
                return ChangeResult.Fail(
                    item.Id,
                    rolledBack
                        ? "开启未能通过验证，已恢复原状态。"
                        : "开启未能通过验证，且恢复原状态失败，请检查系统状态。");
            }

            // The safety record captured the pre-enable state and is obsolete
            // once the enable is verified; close it as restored.
            await MarkRestoredAsync(transactionBackup, cancellationToken).ConfigureAwait(false);

            foreach (var backup in activeBackups)
            {
                await MarkRestoredAsync(backup, cancellationToken).ConfigureAwait(false);
            }

            Log("enable", item.Id, "已开启并通过验证。");
            return new ChangeResult
            {
                Success = true,
                Verified = true,
                ItemId = item.Id,
                BackupId = latest?.Id ?? transactionBackup.Id,
                Message = "已开启自启动。"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("enable", item.Id, "操作已取消，正在尝试恢复原状态。");
            await TryRollbackAsync(provider, transactionBackup, isElevated, CancellationToken.None)
                .ConfigureAwait(false);
            await MarkFailedAsync(transactionBackup, "操作已取消，已尝试恢复原状态。", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            var rolledBack = await TryRollbackAsync(provider, transactionBackup, isElevated, cancellationToken)
                .ConfigureAwait(false);
            await MarkFailedAsync(
                    transactionBackup,
                    rolledBack ? "拒绝访问，已恢复原状态。" : "拒绝访问，恢复原状态失败。",
                    cancellationToken)
                .ConfigureAwait(false);

            Log("enable", item.Id, "应用更改时被拒绝访问，回滚" + (rolledBack ? "成功。" : "失败。"));
            return ElevatedOrFail(item.Id, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            var rolledBack = await TryRollbackAsync(provider, transactionBackup, isElevated, cancellationToken)
                .ConfigureAwait(false);
            await MarkFailedAsync(
                    transactionBackup,
                    rolledBack ? "开启失败，已恢复原状态。" : "开启失败，恢复原状态失败。",
                    cancellationToken)
                .ConfigureAwait(false);

            Log("enable", item.Id, "开启失败：" + ex.GetType().Name + "：" + ex.Message);
            return ChangeResult.Fail(
                item.Id,
                rolledBack
                    ? "开启失败，已恢复原状态。"
                    : "开启失败，且恢复原状态失败，请检查系统状态。");
        }
    }

    private async Task<ChangeResult> EnableFromBackupAsync(
        StartupItem item,
        BackupRecord? backup,
        IReadOnlyList<BackupRecord> activeBackups,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        if (backup is null)
        {
            return ChangeResult.Fail(item.Id, "找不到可恢复的备份，请刷新后重试。");
        }

        var provider = GetChangeProvider(item.Source);
        if (provider is null)
        {
            return ChangeResult.Fail(item.Id, "暂不支持恢复该类型启动项。");
        }

        if (backup.RequiresAdmin && !isElevated)
        {
            return ChangeResult.Fail(item.Id, "需要管理员权限，请确认 UAC 提示。", requiresElevation: true);
        }

        try
        {
            await provider.RestoreStateAsync(backup, isElevated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            Log("enable", item.Id, "恢复备份时被拒绝访问。");
            return ElevatedOrFail(item.Id, isElevated, "需要管理员权限，请确认 UAC 提示。");
        }
        catch (Exception ex)
        {
            Log("enable", item.Id, "恢复备份失败：" + ex.GetType().Name + "：" + ex.Message);
            await RecordBackupErrorAsync(backup, "恢复失败，无法确认系统状态。", cancellationToken)
                .ConfigureAwait(false);
            return ChangeResult.Fail(item.Id, "恢复失败，请刷新后检查系统状态。");
        }

        bool verified;
        try
        {
            var after = await ScanCoreAsync(cancellationToken, respectSettings: false).ConfigureAwait(false);
            verified = after.Items.Any(
                candidate => string.Equals(candidate.Id, item.Id, StringComparison.Ordinal) && candidate.Enabled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("enable", item.Id, "恢复后验证失败：" + ex.GetType().Name);
            verified = false;
        }

        if (!verified)
        {
            await RecordBackupErrorAsync(backup, "恢复后未能验证为已开启。", cancellationToken)
                .ConfigureAwait(false);
            Log("enable", item.Id, "恢复未通过验证。");
            return ChangeResult.Fail(item.Id, "恢复未能通过验证，请刷新后检查。");
        }

        foreach (var active in activeBackups)
        {
            await MarkRestoredAsync(active, cancellationToken).ConfigureAwait(false);
        }

        Log("enable", item.Id, "已通过恢复备份开启并通过验证。");
        return new ChangeResult
        {
            Success = true,
            Verified = true,
            ItemId = item.Id,
            BackupId = backup.Id,
            Message = "已恢复自启动。"
        };
    }

    private async Task<bool> TryRollbackAsync(
        IStartupChangeProvider provider,
        BackupRecord backup,
        bool isElevated,
        CancellationToken cancellationToken)
    {
        try
        {
            await provider.RestoreStateAsync(backup, isElevated, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log(
                "rollback",
                backup.ItemId,
                "恢复原状态失败：" + ex.GetType().Name + "：" + ex.Message);
            return false;
        }
    }

    private async Task MarkFailedAsync(BackupRecord backup, string error, CancellationToken cancellationToken)
    {
        backup.Status = BackupStatus.Failed;
        backup.Error = error;
        await TryUpdateAsync(backup, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkRestoredAsync(BackupRecord backup, CancellationToken cancellationToken)
    {
        backup.Status = BackupStatus.Restored;
        backup.RestoredAt ??= DateTimeOffset.Now;
        backup.VerifiedAt ??= DateTimeOffset.Now;
        backup.Error = null;
        await TryUpdateAsync(backup, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordBackupErrorAsync(
        BackupRecord backup,
        string error,
        CancellationToken cancellationToken)
    {
        backup.Error = error;
        await TryUpdateAsync(backup, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryUpdateAsync(BackupRecord backup, CancellationToken cancellationToken)
    {
        try
        {
            await _backupStore.UpdateAsync(backup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("backup", backup.Id, "更新备份记录失败：" + ex.GetType().Name);
        }
    }

    private static ChangeResult NoOp(string itemId, string? backupId, string message)
    {
        return new ChangeResult
        {
            Success = true,
            Verified = true,
            ItemId = itemId,
            BackupId = backupId,
            Message = message
        };
    }

    private static ChangeResult ElevatedOrFail(string itemId, bool isElevated, string message)
    {
        return isElevated
            ? ChangeResult.Fail(itemId, "拒绝访问，未能完成更改。")
            : ChangeResult.Fail(itemId, message, requiresElevation: true);
    }

    private static BatchChangeResult RejectBatch(string message)
    {
        return new BatchChangeResult
        {
            Message = message,
            Results = new[] { ChangeResult.Fail(null, message) }
        };
    }

    private static string BuildBatchMessage(
        bool enabled,
        IReadOnlyList<ChangeResult> results,
        bool requiresElevation,
        string? elevationMessage)
    {
        if (requiresElevation)
        {
            return elevationMessage ?? "需要管理员权限，请确认 UAC 提示。";
        }

        if (results.Count == 0)
        {
            return "未处理任何启动项。";
        }

        var succeeded = results.Count(result => result.Success);
        var failed = results.Count - succeeded;

        if (failed == 0)
        {
            return enabled ? $"已开启 {succeeded} 项。" : $"已关闭 {succeeded} 项。";
        }

        return $"成功 {succeeded} 项，失败 {failed} 项。";
    }
}
