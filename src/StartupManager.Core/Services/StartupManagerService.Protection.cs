using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

public sealed partial class StartupManagerService
{
    public async Task<ChangeResult> SetUserOverrideAsync(
        string itemId,
        bool allowed,
        CancellationToken cancellationToken)
    {
        if (!StartupItemIds.IsValid(itemId))
        {
            return ChangeResult.Fail(itemId, "启动项标识无效。");
        }

        var batch = await SetUserOverridesAsync(new[] { itemId }, allowed, cancellationToken)
            .ConfigureAwait(false);

        var result = batch.Results.FirstOrDefault();
        return result ?? ChangeResult.Fail(itemId, batch.Message ?? "未能更新项目保护状态。");
    }

    /// <summary>
    /// Allows or restores management permission for several protected items in
    /// one operation. One scan resolves every target and one settings write
    /// persists the decision, so unlocking a group no longer costs a scan per
    /// item. Windows policy-controlled entries stay refused.
    /// </summary>
    public async Task<BatchChangeResult> SetUserOverridesAsync(
        IReadOnlyCollection<string> itemIds,
        bool allowed,
        CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return RejectOverrideBatch("未选择任何启动项。");
        }

        var ids = new List<string>(itemIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in itemIds)
        {
            if (!StartupItemIds.IsValid(rawId))
            {
                Log("userOverride", null, "已拒绝格式无效的启动项标识。");
                return RejectOverrideBatch("启动项标识无效，已取消本次操作。");
            }

            if (seen.Add(rawId!))
            {
                ids.Add(rawId!);
            }
        }

        var results = new List<ChangeResult>(ids.Count);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (allowed)
        {
            // Validate current provider/policy state rather than trusting a stale UI row.
            var outcome = await GetOutcomeForMutationAsync(SourcesForIds(ids), cancellationToken)
                .ConfigureAwait(false);
            var byId = new Dictionary<string, StartupItem>(StringComparer.Ordinal);
            foreach (var item in outcome.Items)
            {
                byId[item.Id] = item;
            }

            foreach (var id in ids)
            {
                if (!byId.TryGetValue(id, out var item))
                {
                    results.Add(ChangeResult.Fail(id, "启动项已不存在，请刷新后重试。"));
                    Log("userOverride", id, "允许管理失败：启动项已不存在。");
                    continue;
                }

                if (!item.CanOverride || (!item.Protected && !item.UserAllowed))
                {
                    results.Add(ChangeResult.Fail(
                        id,
                        "此项目不能解除工具保护；Windows 策略限制或安全检查失败无法覆盖。"));
                    Log("userOverride", id, $"允许管理失败：{item.Name} 不可覆盖。");
                    continue;
                }

                names[id] = item.Name;
                results.Add(new ChangeResult
                {
                    Success = true,
                    Verified = true,
                    ItemId = id,
                    Message = "已允许管理该项目。启动状态未改变，后续修改仍会备份、验证并按需请求管理员权限。"
                });
            }
        }
        else
        {
            // Revocation also works for items that disappeared after an update.
            foreach (var id in ids)
            {
                results.Add(new ChangeResult
                {
                    Success = true,
                    Verified = true,
                    ItemId = id,
                    Message = "已恢复工具保护；启动状态未改变，需要还原配置时请使用恢复中心。"
                });
            }
        }

        var succeededIds = results
            .Where(result => result.Success)
            .Select(result => result.ItemId!)
            .ToList();

        if (succeededIds.Count > 0)
        {
            var settings = GetSettingsSnapshot();
            if (allowed)
            {
                foreach (var id in succeededIds)
                {
                    if (!settings.UserAllowedItemIds.Contains(id, StringComparer.Ordinal))
                    {
                        settings.UserAllowedItemIds.Add(id);
                    }
                }
            }
            else
            {
                var removed = new HashSet<string>(succeededIds, StringComparer.Ordinal);
                settings.UserAllowedItemIds.RemoveAll(removed.Contains);
            }

            await SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        foreach (var result in results)
        {
            if (!result.Success)
            {
                continue;
            }

            var name = names.TryGetValue(result.ItemId ?? string.Empty, out var resolved)
                ? resolved
                : result.ItemId;
            Log(
                "userOverride",
                result.ItemId,
                $"{(allowed ? "允许管理" : "恢复工具保护")}：{name}；未更改启动配置。");
        }

        return new BatchChangeResult
        {
            Message = BuildOverrideBatchMessage(allowed, results),
            Results = results
        };
    }

    private static BatchChangeResult RejectOverrideBatch(string message)
    {
        return new BatchChangeResult
        {
            Message = message,
            Results = new[] { ChangeResult.Fail(null, message) }
        };
    }

    private static string BuildOverrideBatchMessage(bool allowed, IReadOnlyList<ChangeResult> results)
    {
        var succeeded = results.Count(result => result.Success);
        var failed = results.Count - succeeded;

        if (succeeded == 0)
        {
            var detail = results.FirstOrDefault(result => !result.Success)?.Error;
            return string.IsNullOrWhiteSpace(detail)
                ? "未更新任何项目的保护状态。"
                : detail;
        }

        var verb = allowed ? "已允许管理" : "已恢复工具保护";
        return failed == 0
            ? $"{verb} {succeeded} 项；启动状态未改变。"
            : $"{verb} {succeeded} 项，{failed} 项未更新。";
    }
}
