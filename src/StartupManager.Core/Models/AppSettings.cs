namespace StartupManager.Core.Models;

public sealed class AppSettings
{
    public bool IncludeRegistry { get; set; } = true;
    public bool IncludeStartupFolders { get; set; } = true;
    public bool IncludeScheduledTasks { get; set; } = true;
    public bool IncludeServices { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int BackupRetentionDays { get; set; } = 30;

    /// <summary>
    /// UI font/scale factor (0.8–1.5). Applied as the WebView2 ZoomFactor so
    /// text, controls, and spacing stay proportional and the layout reflows.
    /// </summary>
    public double UiFontScale { get; set; } = 1.0;

    /// <summary>
    /// Stable IDs of tool-protected items the user explicitly chose to manage.
    /// Windows policy-controlled tasks and failed safety checks ignore this list.
    /// </summary>
    public List<string> UserAllowedItemIds { get; set; } = new();
}
