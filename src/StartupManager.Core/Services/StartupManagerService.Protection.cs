using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

public sealed partial class StartupManagerService
{
    public async Task<ChangeResult> SetUserOverrideAsync(string itemId, bool allowed, CancellationToken cancellationToken)
    {
        if (!StartupItemIds.IsValid(itemId))
            return ChangeResult.Fail(itemId, "启动项标识无效。");

        string? name = null;
        if (allowed)
        {
            // Validate current provider/policy state rather than trusting a stale UI row.
            var scan = await ScanCoreAsync(cancellationToken, respectSettings: false).ConfigureAwait(false);
            var item = scan.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null)
                return ChangeResult.Fail(itemId, "启动项已不存在，请刷新后重试。");
            if (!item.CanOverride || (!item.Protected && !item.UserAllowed))
                return ChangeResult.Fail(itemId, "此项目不能解除工具保护；Windows 策略限制或安全检查失败无法覆盖。");
            name = item.Name;
        }

        var settings = GetSettingsSnapshot();
        if (allowed)
        {
            if (!settings.UserAllowedItemIds.Contains(itemId, StringComparer.Ordinal))
                settings.UserAllowedItemIds.Add(itemId);
        }
        else
        {
            // Revocation also works for items that disappeared after an update.
            settings.UserAllowedItemIds.RemoveAll(id => id == itemId);
        }
        await SaveSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
        Log("userOverride", itemId,
            $"{(allowed ? "允许管理" : "恢复工具保护")}：{name ?? itemId}；未更改启动配置。");
        return new ChangeResult
        {
            Success = true,
            Verified = true,
            ItemId = itemId,
            Message = allowed
                ? "已允许管理该项目。启动状态未改变，后续修改仍会备份、验证并按需请求管理员权限。"
                : "已恢复工具保护；启动状态未改变，需要还原配置时请使用恢复中心。"
        };
    }
}
