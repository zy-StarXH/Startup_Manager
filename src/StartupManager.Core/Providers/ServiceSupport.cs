using System.Security.Cryptography;
using System.Text;
using StartupManager.Core.Protection;

namespace StartupManager.Core.Providers;

/// <summary>
/// Pure helpers for Windows service command lines and IDs.
/// These helpers never start, stop, or execute a service.
/// </summary>
public static class ServiceCommandLine
{
    public static string CreateServiceId(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            throw new ArgumentException("服务名称不能为空。", nameof(serviceName));
        }

        var normalized = serviceName.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "svc-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the executable path from a service ImagePath value. Handles
    /// quoted paths, unquoted paths with arguments, \SystemRoot\, \??\, and
    /// environment variables. Returns null when no path can be determined.
    /// </summary>
    public static string? ExtractExecutablePath(string? binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var raw = binaryPath.Trim();
        if (raw.StartsWith('"'))
        {
            var close = raw.IndexOf('"', 1);
            var quoted = close > 1 ? raw.Substring(1, close - 1) : raw.Trim('"');
            return Normalize(TrimToExecutable(quoted));
        }

        raw = raw.Replace('/', '\\');
        if (raw.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            raw = raw.Substring(4);
        }

        if (raw.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            raw = "%SystemRoot%\\" + raw.Substring(@"\SystemRoot\".Length);
        }
        else if (raw.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            raw = "%SystemRoot%\\" + raw;
        }

        var exeIndex = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        string candidate;
        if (exeIndex >= 0)
        {
            candidate = TrimToExecutable(raw);
        }
        else
        {
            var space = raw.IndexOfAny(new[] { ' ', '\t' });
            candidate = space > 0 ? raw.Substring(0, space).Trim() : raw;
        }

        return Normalize(candidate);
    }

    private static string TrimToExecutable(string value)
    {
        var searchFrom = 0;
        while (searchFrom < value.Length)
        {
            var index = value.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return value;
            }

            var end = index + 4;
            if (end == value.Length || char.IsWhiteSpace(value[end]) || value[end] == '"')
            {
                return value.Substring(0, end);
            }

            searchFrom = end;
        }

        return value;
    }

    /// <summary>Builds a display-only command line; the value is never executed.</summary>
    public static string BuildDisplayCommand(string? binaryPath)
    {
        var raw = (binaryPath ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (raw.StartsWith('"'))
        {
            return raw;
        }

        var executable = ExtractExecutablePath(raw);
        if (executable is not null &&
            executable.Length > 0 &&
            raw.StartsWith(executable, StringComparison.OrdinalIgnoreCase) &&
            executable.Contains(' '))
        {
            return "\"" + executable + "\"" + raw.Substring(executable.Length);
        }

        return raw;
    }

    private static string? Normalize(string path)
    {
        var expanded = PublisherLookup.NormalizePath(path);
        return expanded.Length == 0 ? null : expanded;
    }
}

public static class ServiceStartTypes
{
    public const uint Boot = 0;
    public const uint System = 1;
    public const uint Automatic = 2;
    public const uint Manual = 3;
    public const uint Disabled = 4;

    public static string ToChineseLabel(uint startType, bool delayedAutoStart = false) => startType switch
    {
        Boot => "引导",
        System => "系统",
        Automatic => delayedAutoStart ? "自动（延迟）" : "自动",
        Manual => "手动",
        Disabled => "已禁用",
        _ => "未知"
    };

    public static string BuildSummary(uint startType, bool delayedAutoStart) =>
        "启动类型：" + ToChineseLabel(startType, delayedAutoStart);

    public static string BuildSubLocation(uint startType, bool delayedAutoStart, string serviceName) =>
        $"启动类型：{ToChineseLabel(startType, delayedAutoStart)} · 服务名称：{serviceName}";
}

/// <summary>Provider-private service state persisted in a backup record.</summary>
public sealed class ServiceBackupState
{
    public string ServiceName { get; set; } = string.Empty;
    public uint StartType { get; set; }
    public bool DelayedAutoStart { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// MSIX packaged services deny SCM configuration changes even for admins;
    /// the provider uses the writable service registry Start value instead.
    /// </summary>
    public bool UseRegistryFallback { get; set; }
}
