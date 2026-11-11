namespace StartupManager.Core.Models;

/// <summary>Where a startup entry lives and who owns its lifecycle.</summary>
public enum StartupSource
{
    Registry,
    Folder,
    Task,
    Service
}

/// <summary>Who the entry applies to.</summary>
public enum StartupScope
{
    CurrentUser,
    AllUsers,
    System
}

public enum BackupStatus
{
    Active,
    Restored,
    Failed
}
