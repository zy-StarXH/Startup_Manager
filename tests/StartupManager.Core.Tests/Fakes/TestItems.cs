using StartupManager.Core.Models;

namespace StartupManager.Core.Tests.Fakes;

public static class TestItems
{
    public static StartupItem Create(
        StartupSource source,
        string? id = null,
        bool enabled = true,
        bool requiresAdmin = false,
        string name = "测试启动项",
        StartupScope? scope = null)
    {
        id ??= TestIds.NewId(source);

        return new StartupItem
        {
            Id = id,
            Name = name,
            Publisher = "测试发布者",
            Source = source,
            Scope = scope ?? (source == StartupSource.Service ? StartupScope.System : StartupScope.CurrentUser),
            Enabled = enabled,
            RequiresAdmin = requiresAdmin,
            Location = source == StartupSource.Service
                ? "服务：测试"
                : @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run",
            SubLocation = source == StartupSource.Service ? "自动" : "TestValue",
            Command = "\"C:\\Test\\app.exe\"",
            TargetPath = @"C:\Test\app.exe",
            Mechanism = source switch
            {
                StartupSource.Registry or StartupSource.Folder => "startupApproved",
                StartupSource.Task => "scheduledTask",
                StartupSource.Service => "serviceStartType",
                _ => null
            }
        };
    }

    public static StartupItem Clone(StartupItem item)
    {
        return new StartupItem
        {
            Id = item.Id,
            Name = item.Name,
            Publisher = item.Publisher,
            Source = item.Source,
            Scope = item.Scope,
            Enabled = item.Enabled,
            Protected = item.Protected,
            ProtectionReason = item.ProtectionReason,
            CanOverride = item.CanOverride,
            UserAllowed = item.UserAllowed,
            IsPackagedService = item.IsPackagedService,
            RequiresAdmin = item.RequiresAdmin,
            ManagedByApp = item.ManagedByApp,
            Location = item.Location,
            SubLocation = item.SubLocation,
            Command = item.Command,
            TargetPath = item.TargetPath,
            Icon = item.Icon,
            LastModified = item.LastModified,
            Mechanism = item.Mechanism,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
