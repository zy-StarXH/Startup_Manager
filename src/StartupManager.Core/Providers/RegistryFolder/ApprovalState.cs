namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Provider-private backup state for a registry Run entry. Stored as StateJson
/// and never sent to the web UI. It contains enough information to locate the
/// source value and restore the exact StartupApproved bytes or absence thereof.
/// </summary>
public sealed class RegistryApprovalState
{
    public string Hive { get; set; } = string.Empty;

    /// <summary>Registry64 or Registry32 (the view used to find the Run value).</summary>
    public string View { get; set; } = string.Empty;

    /// <summary>Run key path relative to the hive.</summary>
    public string KeyPath { get; set; } = string.Empty;

    public string ValueName { get; set; } = string.Empty;

    /// <summary>Run or Run32.</summary>
    public string ApprovalFamily { get; set; } = string.Empty;

    /// <summary>True when an approval value existed before the change.</summary>
    public bool ApprovalValueExisted { get; set; }

    /// <summary>Raw approval bytes, base64 encoded, or null when no value existed.</summary>
    public string? ApprovalRawBase64 { get; set; }

    /// <summary>The Run command observed at capture time (diagnostics only).</summary>
    public string? RunCommand { get; set; }
}

/// <summary>
/// Provider-private backup state for a Startup-folder entry. The shortcut file is
/// never modified; restore only rewrites or deletes its StartupApproved value.
/// </summary>
public sealed class FolderApprovalState
{
    /// <summary>CurrentUser or AllUsers.</summary>
    public string Scope { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    /// <summary>StartupApproved value name, i.e. the file name with extension.</summary>
    public string ValueName { get; set; } = string.Empty;

    /// <summary>Always StartupFolder; kept for backup readability.</summary>
    public string ApprovalFamily { get; set; } = string.Empty;

    public bool ApprovalValueExisted { get; set; }

    public string? ApprovalRawBase64 { get; set; }

    /// <summary>The resolved command observed at capture time (diagnostics only).</summary>
    public string? Command { get; set; }
}
