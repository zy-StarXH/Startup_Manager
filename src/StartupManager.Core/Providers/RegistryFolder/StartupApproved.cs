using System.Buffers.Binary;
using System.Security;
using Microsoft.Win32;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// Which StartupApproved subkey stores the enable/disable state of an entry.
/// </summary>
/// <remarks>
/// Windows Task Manager stores the approval state per user, even for entries that
/// come from HKLM or from the common Startup folder:
/// <list type="bullet">
/// <item><description>64-bit Run (HKCU and HKLM) -> HKCU\...\StartupApproved\Run</description></item>
/// <item><description>32-bit / WOW6432Node Run (HKCU and HKLM) -> HKCU\...\StartupApproved\Run32</description></item>
/// <item><description>User and common Startup folders -> HKCU\...\StartupApproved\StartupFolder</description></item>
/// </list>
/// Consequently a disabled HKLM Run value or common-folder shortcut is only
/// disabled for the current user, not for every account on the machine.
/// </remarks>
public enum StartupApprovalFamily
{
    Run,
    Run32,
    StartupFolder
}

/// <summary>
/// Reads and writes the 12-byte REG_BINARY values used by Windows StartupApproved.
/// The first byte is 0x02 for enabled and 0x03 for disabled; bytes 1-3 are reserved
/// and bytes 4-11 are a little-endian FILETIME. Unknown/malformed leading bytes are
/// treated as enabled because only an explicit 0x03 disables an entry.
/// </summary>
public static class StartupApproved
{
    public const string RootKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public const string ApprovalHiveName = "HKCU";
    public const int ValueLength = 12;
    public const byte EnabledFlag = 0x02;
    public const byte DisabledFlag = 0x03;

    /// <summary>Subkey path relative to the current-user hive.</summary>
    public static string GetKeyPath(StartupApprovalFamily family) => family switch
    {
        StartupApprovalFamily.Run => RootKeyPath + @"\Run",
        StartupApprovalFamily.Run32 => RootKeyPath + @"\Run32",
        StartupApprovalFamily.StartupFolder => RootKeyPath + @"\StartupFolder",
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown StartupApproved family.")
    };

    /// <summary>Full display path, e.g. HKCU\...\StartupApproved\Run.</summary>
    public static string GetDisplayKeyPath(StartupApprovalFamily family) =>
        ApprovalHiveName + @"\" + GetKeyPath(family);

    /// <summary>
    /// Registry view determines whether an entry is approved through Run or Run32.
    /// RegistryView.Default in this x64 app resolves to the 64-bit view.
    /// </summary>
    public static StartupApprovalFamily GetApprovalFamily(RegistryView view) =>
        view == RegistryView.Registry32
            ? StartupApprovalFamily.Run32
            : StartupApprovalFamily.Run;

    public static byte[] CreateEnabledBytes(DateTime timestampUtc) =>
        CreateStateBytes(enabled: true, timestampUtc);

    public static byte[] CreateDisabledBytes(DateTime timestampUtc) =>
        CreateStateBytes(enabled: false, timestampUtc);

    public static byte[] CreateStateBytes(bool enabled, DateTime timestampUtc)
    {
        var bytes = new byte[ValueLength];
        bytes[0] = enabled ? EnabledFlag : DisabledFlag;

        long fileTime;
        try
        {
            fileTime = timestampUtc.ToUniversalTime().ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            fileTime = 0;
        }

        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4, 8), fileTime);
        return bytes;
    }

    /// <summary>
    /// A missing approval value means enabled. Only an explicit disabled flag
    /// disables the entry; zero-length or unknown data is treated as enabled.
    /// </summary>
    public static bool IsEnabled(byte[]? rawApproval) =>
        rawApproval is null || rawApproval.Length == 0 || rawApproval[0] != DisabledFlag;

    /// <summary>
    /// Reads the FILETIME from bytes 4-11. Returns local time so the UI can show a
    /// real timestamp. A zero FILETIME (seen in real 0x02 values) yields false.
    /// </summary>
    public static bool TryGetTimestamp(byte[]? rawApproval, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (rawApproval is null || rawApproval.Length < ValueLength)
        {
            return false;
        }

        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(rawApproval.AsSpan(4, 8));
        if (fileTime <= 0)
        {
            return false;
        }

        try
        {
            var utc = DateTime.FromFileTimeUtc(fileTime);
            timestamp = new DateTimeOffset(utc).ToLocalTime();
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort bulk read used by scanning. Access failures are treated as
    /// "no approval values", i.e. entries appear enabled, which is the least
    /// destructive interpretation. Mutation paths use <see cref="TryReadApproval"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ReadApprovals(StartupApprovalFamily family)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var key = hive.OpenSubKey(GetKeyPath(family), writable: false);
            if (key is null)
            {
                return result;
            }

            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(valueName))
                {
                    continue;
                }

                try
                {
                    // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ untouched; approval
                    // values are REG_BINARY and are returned as byte[].
                    var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value is byte[] raw)
                    {
                        result[valueName] = raw;
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Skip only the unreadable value; the remaining entries are still useful.
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // The whole StartupApproved family is unreadable; treat it as absent.
        }

        return result;
    }

    /// <summary>
    /// Reads one approval value. Returns false when no value exists. If a value
    /// exists but is not REG_BINARY, <paramref name="exists"/> is true and
    /// <paramref name="rawApproval"/> is null so callers can refuse to overwrite it.
    /// Access errors propagate; capture/apply must not mistake them for "enabled".
    /// </summary>
    public static bool TryReadApproval(
        StartupApprovalFamily family,
        string valueName,
        out byte[]? rawApproval,
        out bool exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(valueName);

        rawApproval = null;
        exists = false;

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(GetKeyPath(family), writable: false);
        if (key is null)
        {
            return false;
        }

        object? value;
        try
        {
            value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (IOException ex)
        {
            // Access failures must not be mistaken for "no approval = enabled";
            // mutation callers wrap and abort instead of guessing.
            throw new InvalidOperationException(
                $"无法读取启动批准值“{valueName}”。",
                ex);
        }

        if (value is null)
        {
            return false;
        }

        exists = true;
        if (value is byte[] bytes)
        {
            rawApproval = bytes;
            return true;
        }

        return false;
    }

    public static void WriteApproval(
        StartupApprovalFamily family,
        string valueName,
        ReadOnlySpan<byte> rawApproval)
    {
        ArgumentException.ThrowIfNullOrEmpty(valueName);
        if (rawApproval.IsEmpty)
        {
            throw new ArgumentException("Approval bytes must not be empty.", nameof(rawApproval));
        }

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.CreateSubKey(GetKeyPath(family), writable: true)
            ?? throw new InvalidOperationException(
                $"无法打开 Windows 启动批准状态键：{GetDisplayKeyPath(family)}");

        key.SetValue(valueName, rawApproval.ToArray(), RegistryValueKind.Binary);
    }

    /// <summary>Removes an approval value; succeeds when the key or value is absent.</summary>
    public static void DeleteApproval(StartupApprovalFamily family, string valueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(valueName);

        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = hive.OpenSubKey(GetKeyPath(family), writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
