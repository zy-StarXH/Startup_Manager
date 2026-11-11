using System.Security.Principal;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

public sealed partial class StartupManagerService
{
    public async Task<StartupSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        var outcome = await ScanCoreAsync(cancellationToken, respectSettings: true).ConfigureAwait(false);

        Log(
            "scan",
            null,
            $"完成：{outcome.Items.Count} 项，警告 {outcome.Warnings.Count} 条，管理员令牌={outcome.IsElevated}。");

        return new StartupSnapshot
        {
            Items = outcome.Items,
            ScannedAt = outcome.ScannedAt,
            Warnings = outcome.Warnings,
            IsElevated = outcome.IsElevated
        };
    }

    /// <summary>
    /// Runs every included provider, applies protection rules, and overlays
    /// active backups that are no longer visible in the live scan as synthetic
    /// disabled items. Synthetic items make a disabled service restorable.
    /// </summary>
    private async Task<ScanOutcome> ScanCoreAsync(CancellationToken cancellationToken, bool respectSettings)
    {
        var settings = GetSettingsSnapshot();
        var items = new List<StartupItem>();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (respectSettings && !IsIncluded(settings, provider.Source))
            {
                continue;
            }

            try
            {
                var scanned = await provider.ScanAsync(cancellationToken).ConfigureAwait(false);
                if (scanned is null)
                {
                    continue;
                }

                foreach (var item in scanned)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item is null || string.IsNullOrWhiteSpace(item.Id))
                    {
                        Log("scan", null, "已忽略缺少标识的启动项。");
                        continue;
                    }

                    if (!liveIds.Add(item.Id))
                    {
                        Log("scan", item.Id, "已忽略重复的启动项。");
                        continue;
                    }

                    ApplyProtection(item, settings);
                    item.ManagedByApp = false;
                    items.Add(item);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"读取{SourceLabel(provider.Source)}失败，已跳过。");
                Log(
                    "scan",
                    SourceLabel(provider.Source),
                    "读取失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        IReadOnlyList<BackupRecord> activeBackups;
        try
        {
            activeBackups = await _backupStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activeBackups = Array.Empty<BackupRecord>();
            warnings.Add("读取备份记录失败，部分已关闭项可能不会显示。");
            Log("scan", null, "读取备份失败：" + ex.GetType().Name + "：" + ex.Message);
        }

        var byId = new Dictionary<string, StartupItem>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            byId[item.Id] = item;
        }

        foreach (var backup in activeBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (respectSettings && !IsIncluded(settings, backup.Source))
            {
                // A hidden category stays hidden even when a backup could synthesize a row.
                continue;
            }

            if (byId.TryGetValue(backup.ItemId, out var live))
            {
                // The item is still visible; it was changed by this app at some point.
                live.ManagedByApp = true;
                continue;
            }

            if (liveIds.Contains(backup.ItemId))
            {
                // A duplicated live id was skipped above; do not synthesize a second row.
                continue;
            }

            var synthetic = CreateSyntheticItem(backup);
            // A backup can only exist for an item that was allowed to change.
            // Keep the synthetic restore row manageable even if the current
            // policy no longer has enough live metadata to evaluate it safely.
            // The restore center applies the same rule independently.
            synthetic.Protected = false;
            synthetic.ProtectionReason = null;
            synthetic.RequiresAdmin |= backup.RequiresAdmin;
            synthetic.ManagedByApp = true;
            items.Add(synthetic);
            byId[synthetic.Id] = synthetic;
        }

        return new ScanOutcome(
            items,
            liveIds,
            activeBackups,
            warnings,
            DateTimeOffset.Now,
            DetectElevated());
    }

    private void ApplyProtection(StartupItem item, AppSettings settings)
    {
        try
        {
            var decision = _protectionPolicy.Evaluate(item);
            var userAllowed = decision.CanOverride &&
                settings.UserAllowedItemIds.Any(id => string.Equals(id, item.Id, StringComparison.Ordinal));

            item.CanOverride = decision.CanOverride;
            item.UserAllowed = userAllowed;
            item.Protected = decision.IsProtected && !userAllowed;
            item.ProtectionReason = item.Protected ? decision.Reason : null;
            item.RequiresAdmin = item.RequiresAdmin || decision.RequiresAdmin;
        }
        catch (Exception ex)
        {
            // Fail closed: an item whose safety cannot be evaluated must not be mutable.
            item.Protected = true;
            item.CanOverride = false;
            item.UserAllowed = false;
            item.ProtectionReason = "安全检查失败，已禁止修改。";
            item.RequiresAdmin = true;
            Log("protection", item.Id, "策略评估失败：" + ex.GetType().Name + "：" + ex.Message);
        }
    }

    private static StartupItem CreateSyntheticItem(BackupRecord backup)
    {
        return new StartupItem
        {
            Id = backup.ItemId,
            Name = string.IsNullOrWhiteSpace(backup.DisplayName) ? "已关闭的启动项" : backup.DisplayName,
            Publisher = string.Empty,
            Source = backup.Source,
            Scope = backup.Scope,
            Enabled = false,
            Protected = false,
            RequiresAdmin = backup.RequiresAdmin,
            ManagedByApp = true,
            Location = backup.Location,
            Command = backup.Command,
            Mechanism = backup.Source switch
            {
                StartupSource.Registry or StartupSource.Folder => "startupApproved",
                StartupSource.Task => "scheduledTask",
                StartupSource.Service => "serviceStartType",
                _ => null
            },
            LastModified = backup.CreatedAt
        };
    }

    private static bool DetectElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record ScanOutcome(
        IReadOnlyList<StartupItem> Items,
        IReadOnlySet<string> LiveIds,
        IReadOnlyList<BackupRecord> ActiveBackups,
        IReadOnlyList<string> Warnings,
        DateTimeOffset ScannedAt,
        bool IsElevated);
}
