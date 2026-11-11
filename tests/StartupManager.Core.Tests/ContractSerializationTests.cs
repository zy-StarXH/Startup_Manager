using System.Text.Json;
using StartupManager.Core;
using StartupManager.Core.Models;
using Xunit;

namespace StartupManager.Core.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void Item_serializes_enums_as_camelCase_and_hides_metadata()
    {
        var item = new StartupItem
        {
            Id = "reg-1",
            Name = "示例",
            Publisher = "示例发布者",
            Source = StartupSource.Registry,
            Scope = StartupScope.CurrentUser,
            Enabled = true,
            Location = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            Command = "\"C:\\Example.exe\""
        };
        item.Metadata["secret"] = "not-for-ui";

        var json = JsonSerializer.Serialize(item, JsonDefaults.Options);

        Assert.Contains("\"source\":\"registry\"", json);
        Assert.Contains("\"scope\":\"currentUser\"", json);
        Assert.DoesNotContain("Metadata", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void Backup_summary_contains_restore_facing_fields()
    {
        var backup = new BackupRecord
        {
            Id = "b1",
            ItemId = "svc-1",
            Source = StartupSource.Service,
            Scope = StartupScope.System,
            RequiresAdmin = true,
            DisplayName = "TeamViewer 服务",
            Location = "服务：TeamViewer",
            Command = "\"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe\"",
            CreatedAt = DateTimeOffset.Parse("2026-11-11T12:00:00+08:00"),
            Summary = "启动类型：自动"
        };

        var json = JsonSerializer.Serialize(backup.ToSummary(), JsonDefaults.Options);

        Assert.Contains("\"source\":\"service\"", json);
        Assert.Contains("\"scope\":\"system\"", json);
        Assert.Contains("\"requiresAdmin\":true", json);
        Assert.DoesNotContain("StateJson", json);
    }
}
