using System.Text.Json.Serialization;

namespace StartupManager.Core.Models;

/// <summary>
/// A single startup entry discovered on this machine.
/// The shape is serialized to the web UI with camelCase JSON.
/// </summary>
public sealed class StartupItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;

    public StartupSource Source { get; init; }
    public StartupScope Scope { get; init; }

    public bool Enabled { get; set; }
    public bool Protected { get; set; }
    public string? ProtectionReason { get; set; }

    /// <summary>Protected only because publisher/path could not be verified.</summary>
    public bool CanOverride { get; set; }

    /// <summary>The user explicitly allowed management of this unverified item.</summary>
    public bool UserAllowed { get; set; }

    /// <summary>True for an MSIX-packaged service whose start type is protected by the package.</summary>
    public bool IsPackagedService { get; set; }

    /// <summary>Changing this entry is expected to need an elevated token.</summary>
    public bool RequiresAdmin { get; set; }

    /// <summary>True when this disabled row was produced by this app and is still restorable.</summary>
    public bool ManagedByApp { get; set; }

    /// <summary>Human readable location, e.g. HKCU\...\Run or 任务计划程序库 \Folder\Task.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>Secondary location detail: value name, trigger, service start type, etc.</summary>
    public string? SubLocation { get; init; }

    /// <summary>Executable command line. Never executed by the app.</summary>
    public string Command { get; init; } = string.Empty;

    public string? TargetPath { get; init; }

    /// <summary>Optional data:image/png;base64,... extracted from the executable.</summary>
    public string? Icon { get; set; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>startupApproved | scheduledTask | serviceStartType</summary>
    public string? Mechanism { get; init; }

    /// <summary>Provider-private data. Never sent to the web UI.</summary>
    [JsonIgnore]
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
