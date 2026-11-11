using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

public static class TestBackups
{
    public static BackupRecord Create(
        StartupSource source,
        string itemId,
        bool requiresAdmin = false,
        BackupStatus status = BackupStatus.Active,
        DateTimeOffset? createdAt = null,
        string displayName = "TeamViewer 服务",
        string? id = null)
    {
        return new BackupRecord
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            ItemId = itemId,
            Source = source,
            Scope = source == StartupSource.Service ? StartupScope.System : StartupScope.CurrentUser,
            RequiresAdmin = requiresAdmin,
            DisplayName = displayName,
            Location = source == StartupSource.Service ? "服务：TeamViewer" : @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            Command = "\"C:\\Program Files\\TeamViewer\\TeamViewer_Service.exe\"",
            CreatedAt = createdAt ?? DateTimeOffset.Now,
            Status = status,
            Summary = "启动类型：自动",
            StateKind = source == StartupSource.Service ? "serviceStartType" : "startupApproval",
            StateJson = "{\"value\":\"original\"}"
        };
    }
}
