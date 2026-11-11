using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Backup;
using StartupManager.Core.Models;
using StartupManager.Core.Tests.Fakes;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class BackupStoreTests
{
    [Fact]
    public async Task Backup_round_trips_fields_and_keeps_state_private()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var itemId = TestIds.NewId(StartupSource.Service);
        var item = TestItems.Create(
            StartupSource.Service,
            itemId,
            enabled: true,
            requiresAdmin: true,
            name: "TeamViewer 服务");

        var state = new StateSnapshot
        {
            Kind = "serviceStartType",
            Json = "{\"startType\":4}",
            Summary = "启动类型：自动"
        };

        var before = DateTimeOffset.Now;
        var record = await store.CreateAsync(item, state, CancellationToken.None);

        Assert.True(Guid.TryParseExact(record.Id, "N", out _));
        Assert.Equal(itemId, record.ItemId);
        Assert.Equal(StartupSource.Service, record.Source);
        Assert.Equal(StartupScope.System, record.Scope);
        Assert.True(record.RequiresAdmin);
        Assert.Equal("TeamViewer 服务", record.DisplayName);
        Assert.Equal(item.Location, record.Location);
        Assert.Equal(item.Command, record.Command);
        Assert.Equal("启动类型：自动", record.Summary);
        Assert.Equal("serviceStartType", record.StateKind);
        Assert.Equal("{\"startType\":4}", record.StateJson);
        Assert.Equal(BackupStatus.Active, record.Status);
        Assert.Null(record.RestoredAt);
        Assert.Null(record.VerifiedAt);
        Assert.InRange(record.CreatedAt, before, DateTimeOffset.Now);

        var loaded = await store.GetAsync(record.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(record, loaded, BackupRecordComparer.Instance);

        var filePath = Path.Combine(store.BackupDirectory, record.Id + ".json");
        Assert.True(File.Exists(filePath));
        Assert.Contains("serviceStartType", await File.ReadAllTextAsync(filePath));

        var summaryJson = JsonSerializer.Serialize(loaded!.ToSummary(), JsonDefaults.Options);
        Assert.DoesNotContain("StateJson", summaryJson);
        Assert.DoesNotContain("stateJson", summaryJson);
        Assert.DoesNotContain("startType", summaryJson);

        Assert.Empty(Directory.GetFiles(store.BackupDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Backup_rejects_malformed_ids_and_path_traversal()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);

        Assert.Null(await store.GetAsync("../evil", CancellationToken.None));
        Assert.Null(await store.GetAsync("not-a-guid", CancellationToken.None));
        Assert.Null(await store.GetAsync(@"..\..\Windows\System32\config\SAM", CancellationToken.None));
        Assert.Null(await store.GetAsync(new string('a', 200), CancellationToken.None));
        Assert.Null(await store.GetAsync(Guid.NewGuid().ToString("D"), CancellationToken.None));

        var invalid = TestBackups.Create(StartupSource.Registry, TestIds.NewId(StartupSource.Registry));
        invalid = new BackupRecord
        {
            Id = "../../escape",
            ItemId = invalid.ItemId,
            Source = invalid.Source,
            Scope = invalid.Scope,
            DisplayName = invalid.DisplayName,
            CreatedAt = invalid.CreatedAt,
            StateKind = invalid.StateKind,
            StateJson = invalid.StateJson
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateAsync(invalid, CancellationToken.None));

        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(paths.Root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Backup_accepts_guid_formats_but_stores_canonical_name()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var item = TestItems.Create(StartupSource.Registry);

        var record = await store.CreateAsync(
            item,
            new StateSnapshot { Kind = "startupApproval", Json = "{\"bytes\":\"02\"}", Summary = "原始状态：启用" },
            CancellationToken.None);

        var dashed = Guid.Parse(record.Id).ToString("D");
        var loaded = await store.GetAsync(dashed.ToUpperInvariant(), CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(record.Id, loaded!.Id);
        Assert.Single(Directory.GetFiles(store.BackupDirectory, "*.json"));
    }

    [Fact]
    public async Task Backup_update_with_dashed_id_keeps_single_canonical_file()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var record = await store.CreateAsync(
            TestItems.Create(StartupSource.Registry),
            new StateSnapshot { Kind = "startupApproval", Json = "{\"enabled\":true}", Summary = "原始状态：启用" },
            CancellationToken.None);

        var dashed = TestBackups.Create(
            StartupSource.Registry,
            record.ItemId,
            status: BackupStatus.Restored,
            id: Guid.Parse(record.Id).ToString("D"));

        await store.UpdateAsync(dashed, CancellationToken.None);

        Assert.Single(Directory.GetFiles(store.BackupDirectory, "*.json"));
        Assert.True(File.Exists(Path.Combine(store.BackupDirectory, record.Id + ".json")));

        var loaded = await store.GetAsync(record.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(BackupStatus.Restored, loaded!.Status);
    }

    [Fact]
    public async Task Backup_skips_corrupt_and_mismatched_files()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var valid = await store.CreateAsync(
            TestItems.Create(StartupSource.Registry),
            new StateSnapshot { Kind = "startupApproval", Json = "{\"enabled\":true}", Summary = "原始状态：启用" },
            CancellationToken.None);

        var corruptId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            Path.Combine(store.BackupDirectory, corruptId + ".json"),
            "{ this is not json");

        var mismatched = TestBackups.Create(StartupSource.Task, TestIds.NewId(StartupSource.Task));
        await File.WriteAllTextAsync(
            Path.Combine(store.BackupDirectory, Guid.NewGuid().ToString("N") + ".json"),
            JsonSerializer.Serialize(mismatched, JsonDefaults.Indented));

        var incomplete = TestBackups.Create(StartupSource.Registry, TestIds.NewId(StartupSource.Registry));
        incomplete = new BackupRecord
        {
            Id = incomplete.Id,
            ItemId = incomplete.ItemId,
            Source = incomplete.Source,
            CreatedAt = incomplete.CreatedAt
        };
        await File.WriteAllTextAsync(
            Path.Combine(store.BackupDirectory, incomplete.Id + ".json"),
            JsonSerializer.Serialize(incomplete, JsonDefaults.Indented));

        await File.WriteAllTextAsync(Path.Combine(store.BackupDirectory, "notes.json"), "hello");

        var all = await store.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(valid.Id, all[0].Id);

        var active = await store.GetActiveAsync(CancellationToken.None);
        Assert.Single(active);

        Assert.Null(await store.GetAsync(corruptId, CancellationToken.None));
    }

    [Fact]
    public async Task Backup_update_persists_status_and_requires_existing_record()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var record = await store.CreateAsync(
            TestItems.Create(StartupSource.Registry),
            new StateSnapshot { Kind = "startupApproval", Json = "{\"enabled\":true}", Summary = "原始状态：启用" },
            CancellationToken.None);

        record.Status = BackupStatus.Failed;
        record.Error = "验证失败";
        await store.UpdateAsync(record, CancellationToken.None);

        var loaded = await store.GetAsync(record.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(BackupStatus.Failed, loaded!.Status);
        Assert.Equal("验证失败", loaded.Error);

        var missing = TestBackups.Create(
            StartupSource.Registry,
            TestIds.NewId(StartupSource.Registry),
            id: Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.UpdateAsync(missing, CancellationToken.None));
    }

    [Fact]
    public async Task Backup_prune_removes_old_non_active_records_and_keeps_active()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var old = DateTimeOffset.Now.AddDays(-40);

        var activeOld = TestBackups.Create(
            StartupSource.Service,
            TestIds.NewId(StartupSource.Service),
            createdAt: old);
        var restoredOld = TestBackups.Create(
            StartupSource.Registry,
            TestIds.NewId(StartupSource.Registry),
            status: BackupStatus.Restored,
            createdAt: old);
        var failedOld = TestBackups.Create(
            StartupSource.Task,
            TestIds.NewId(StartupSource.Task),
            status: BackupStatus.Failed,
            createdAt: old);
        var recentRestored = TestBackups.Create(
            StartupSource.Folder,
            TestIds.NewId(StartupSource.Folder),
            status: BackupStatus.Restored,
            createdAt: DateTimeOffset.Now);

        WriteRecord(store.BackupDirectory, activeOld);
        WriteRecord(store.BackupDirectory, restoredOld);
        WriteRecord(store.BackupDirectory, failedOld);
        WriteRecord(store.BackupDirectory, recentRestored);

        await store.PruneAsync(30, CancellationToken.None);

        var remaining = await store.GetAllAsync(CancellationToken.None);
        Assert.Contains(remaining, record => record.Id == activeOld.Id);
        Assert.Contains(remaining, record => record.Id == recentRestored.Id);
        Assert.DoesNotContain(remaining, record => record.Id == restoredOld.Id);
        Assert.DoesNotContain(remaining, record => record.Id == failedOld.Id);

        var stillOld = TestBackups.Create(
            StartupSource.Folder,
            TestIds.NewId(StartupSource.Folder),
            status: BackupStatus.Restored,
            createdAt: old);
        WriteRecord(store.BackupDirectory, stillOld);

        await store.PruneAsync(0, CancellationToken.None);
        Assert.NotNull(await store.GetAsync(stillOld.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Backup_rejects_invalid_provider_state_json()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);
        var item = TestItems.Create(StartupSource.Registry);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            item,
            new StateSnapshot { Kind = "startupApproval", Json = "{not-json", Summary = "x" },
            CancellationToken.None));

        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public void Backup_directory_acl_is_restricted_to_current_user()
    {
        using var paths = new TestPaths();
        var store = new JsonBackupStore(paths.Paths);

        try
        {
            var security = new DirectoryInfo(store.BackupDirectory).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);

            var currentSid = WindowsIdentity.GetCurrent().User;
            Assert.NotNull(currentSid);

            var allowRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow)
                .ToList();

            Assert.Contains(
                allowRules,
                rule => currentSid!.Equals(rule.IdentityReference) &&
                        rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // ACL APIs can be unavailable on non-NTFS test volumes; hardening is best effort.
        }
    }

    private static void WriteRecord(string directory, BackupRecord record)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, record.Id + ".json"),
            JsonSerializer.Serialize(record, JsonDefaults.Indented));
    }

    private sealed class BackupRecordComparer : IEqualityComparer<BackupRecord>
    {
        public static BackupRecordComparer Instance { get; } = new();

        public bool Equals(BackupRecord? x, BackupRecord? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return x.Id == y.Id &&
                   x.ItemId == y.ItemId &&
                   x.Source == y.Source &&
                   x.Scope == y.Scope &&
                   x.RequiresAdmin == y.RequiresAdmin &&
                   x.DisplayName == y.DisplayName &&
                   x.Location == y.Location &&
                   x.Command == y.Command &&
                   x.CreatedAt == y.CreatedAt &&
                   x.RestoredAt == y.RestoredAt &&
                   x.VerifiedAt == y.VerifiedAt &&
                   x.Status == y.Status &&
                   x.Summary == y.Summary &&
                   x.Error == y.Error &&
                   x.StateKind == y.StateKind &&
                   x.StateJson == y.StateJson;
        }

        public int GetHashCode(BackupRecord obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
