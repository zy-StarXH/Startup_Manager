using System.Security.Principal;
using StartupManager.Core.Interfaces;
using StartupManager.Core.Models;

namespace StartupManager.Core.Services;

public sealed partial class StartupManagerService
{
    public async Task<StartupSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        var outcome = await ScanCoreAsync(cancellationToken, respectSettings: true).ConfigureAwait(false);
        StoreCachedOutcome(outcome);

        Log(
            "scan",
            null,
            $"完成：{outcome.Items.Count} 项，警告 {outcome.Warnings.Count} 条，管理员令牌={outcome.IsElevated}。");

        return CreateSnapshot(outcome);
    }

    private static StartupSnapshot CreateSnapshot(ScanOutcome outcome) => new()
    {
        Items = outcome.Items,
        ScannedAt = outcome.ScannedAt,
        Warnings = outcome.Warnings,
        IsElevated = outcome.IsElevated
    };

    public async Task RefreshSourcesForItemsAsync(
        IReadOnlyCollection<string> itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0)
        {
            return;
        }

        var sources = new HashSet<StartupSource>();
        foreach (var itemId in itemIds)
        {
            var source = StartupItemIds.TryGetSource(itemId);
            if (source.HasValue)
            {
                sources.Add(source.Value);
            }
        }

        var cached = GetCachedOutcome();
        if (cached is null || sources.Count == 0)
        {
            var full = await ScanCoreAsync(cancellationToken, respectSettings: true).ConfigureAwait(false);
            StoreCachedOutcome(full);
            return;
        }

        try
        {
            var scoped = await ScanCoreAsync(
                    cancellationToken,
                    respectSettings: true,
                    onlySources: sources)
                .ConfigureAwait(false);
            var merged = MergeScopedInto(cached, scoped, sources);
            var active = await _backupStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            StoreCachedOutcome(merged with { ActiveBackups = active });
            Log("scan", null, $"已按需更新 {sources.Count} 类来源的本地快照。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("scan", null, "按需刷新失败：" + ex.GetType().Name + "：" + ex.Message);
            ClearCachedOutcome();
        }
    }

    private ScanOutcome? GetCachedOutcome()
    {
        lock (_scanCacheGate)
        {
            return _cachedOutcome;
        }
    }

    private void StoreCachedOutcome(ScanOutcome outcome)
    {
        lock (_scanCacheGate)
        {
            _cachedOutcome = outcome;
        }
    }

    private void ClearCachedOutcome()
    {
        lock (_scanCacheGate)
        {
            _cachedOutcome = null;
        }
    }

    /// <summary>
    /// Reuses the last scan for a mutation when it is recent. Toggling several
    /// items in a row then costs one scan instead of one scan per toggle. When
    /// the cache is stale or missing, only the providers that own the targets
    /// are scanned (a one-shot UAC helper has no cache at all).
    /// </summary>
    private async Task<ScanOutcome> GetOutcomeForMutationAsync(
        IReadOnlySet<StartupSource> sources,
        CancellationToken cancellationToken)
    {
        var cached = GetCachedOutcome();
        if (cached is not null &&
            DateTimeOffset.UtcNow - cached.ScannedAt <= MutationCacheLifetime)
        {
            return cached;
        }

        if (sources.Count == 0)
        {
            var full = await ScanCoreAsync(cancellationToken, respectSettings: true).ConfigureAwait(false);
            StoreCachedOutcome(full);
            return full;
        }

        var scoped = await ScanCoreAsync(
                cancellationToken,
                respectSettings: true,
                onlySources: sources)
            .ConfigureAwait(false);

        if (cached is null)
        {
            // Do not publish a partial list as the machine snapshot. The host
            // always scans before mutating; this path is the elevated helper.
            return scoped;
        }

        var merged = MergeScopedInto(cached, scoped, sources);
        var active = await _backupStore.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var outcome = merged with { ActiveBackups = active };
        StoreCachedOutcome(outcome);
        return outcome;
    }

    /// <summary>Replaces the rows of the scanned sources in a cached outcome.</summary>
    private static ScanOutcome MergeScopedInto(
        ScanOutcome cached,
        ScanOutcome scoped,
        IReadOnlySet<StartupSource> sources)
    {
        var merged = cached.Items.Where(item => !sources.Contains(item.Source)).ToList();
        foreach (var item in scoped.Items)
        {
            if (sources.Contains(item.Source))
            {
                merged.Add(item);
            }
        }

        var liveIds = new HashSet<string>(cached.LiveIds, StringComparer.Ordinal);
        foreach (var item in cached.Items.Where(item => sources.Contains(item.Source)))
        {
            liveIds.Remove(item.Id);
        }

        foreach (var id in scoped.LiveIds)
        {
            liveIds.Add(id);
        }

        return cached with
        {
            Items = merged,
            LiveIds = liveIds,
            ActiveBackups = scoped.ActiveBackups
        };
    }

    /// <summary>
    /// Runs every included provider, applies protection rules, and overlays
    /// active backups that are no longer visible in the live scan as synthetic
    /// disabled items. Synthetic items make a disabled service restorable.
    /// <paramref name="onlySources"/> limits the scan to a subset of providers;
    /// callers must not store such a partial outcome as the shared snapshot.
    /// </summary>
    private async Task<ScanOutcome> ScanCoreAsync(
        CancellationToken cancellationToken,
        bool respectSettings,
        IReadOnlySet<StartupSource>? onlySources = null)
    {
        var settings = GetSettingsSnapshot();
        var items = new List<StartupItem>();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var providers = new List<IStartupProvider>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (respectSettings && !IsIncluded(settings, provider.Source))
            {
                continue;
            }

            if (onlySources is not null && !onlySources.Contains(provider.Source))
            {
                continue;
            }

            providers.Add(provider);
        }

        // Providers touch different Windows subsystems (registry, Startup
        // folders, Task Scheduler COM, SCM, package deployment). Running them
        // together turns the total into the slowest provider instead of a sum.
        var results = new ProviderScanResult[providers.Count];
        var scans = new Task[providers.Count];
        for (var index = 0; index < providers.Count; index++)
        {
            var slot = index;
            scans[index] = Task.Run(
                async () =>
                {
                    var provider = providers[slot];
                    try
                    {
                        var scanned = await provider.ScanAsync(cancellationToken).ConfigureAwait(false);
                        results[slot] = new ProviderScanResult(scanned, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        results[slot] = new ProviderScanResult(null, ex);
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(scans).ConfigureAwait(false);

        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            var result = results[index];
            if (result.Error is not null)
            {
                warnings.Add($"读取{SourceLabel(provider.Source)}失败，已跳过。");
                Log(
                    "scan",
                    SourceLabel(provider.Source),
                    "读取失败：" + result.Error.GetType().Name + "：" + result.Error.Message);
                continue;
            }

            if (result.Items is null)
            {
                continue;
            }

            foreach (var item in result.Items)
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

            if (onlySources is not null && !onlySources.Contains(backup.Source))
            {
                continue;
            }

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
            // Keep per-item permissions consistent after a disabled service
            // leaves the live scan. The recovery center remains independently
            // available to restore an existing backup even after relocking.
            ApplyProtection(synthetic, settings);
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

    /// <summary>
    /// Re-evaluates the cached rows after a permission change so the UI does not
    /// need a rescan to show that an unlock took effect.
    /// </summary>
    private void ApplyProtectionToCachedItems(AppSettings settings)
    {
        var outcome = GetCachedOutcome();
        if (outcome is null)
        {
            return;
        }

        foreach (var item in outcome.Items)
        {
            ApplyProtection(item, settings);
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
                StartupSource.PackagedApp => "packagedStartupTask",
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

    private sealed record ProviderScanResult(IReadOnlyList<StartupItem>? Items, Exception? Error);

    private sealed record ScanOutcome(
        IReadOnlyList<StartupItem> Items,
        IReadOnlySet<string> LiveIds,
        IReadOnlyList<BackupRecord> ActiveBackups,
        IReadOnlyList<string> Warnings,
        DateTimeOffset ScannedAt,
        bool IsElevated);
}
