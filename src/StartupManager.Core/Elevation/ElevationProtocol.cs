using System.Text.RegularExpressions;
using StartupManager.Core.Models;
using StartupManager.Core.Services;

namespace StartupManager.Core.Elevation;

/// <summary>
/// Typed one-shot IPC protocol between the host and StartupManager.Elevated.exe.
/// Only request/response JSON files inside AppPaths.IpcDirectory are accepted.
/// </summary>
public static partial class ElevationProtocol
{
    public const int MaxRequestFileBytes = 64 * 1024;
    public const int MaxResponseFileBytes = 512 * 1024;

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(1);

    private const string RequestPrefix = "req-";
    private const string RequestExtension = ".json";
    private const string ResponseExtension = ".response.json";

    [GeneratedRegex("^req-[A-Za-z0-9-]{1,64}\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestFileNameRegex();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdRegex();

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceRegex();

    public static bool IsValidRequestId(string? requestId)
    {
        return !string.IsNullOrEmpty(requestId) && RequestIdRegex().IsMatch(requestId);
    }

    public static bool IsValidNonce(string? nonce)
    {
        return !string.IsNullOrEmpty(nonce) && NonceRegex().IsMatch(nonce);
    }

    public static bool IsValidItemId(string? itemId) => StartupItemIds.IsValid(itemId);

    public static string CreateRequestPath(AppPaths paths, string requestId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!IsValidRequestId(requestId))
        {
            throw new ArgumentException("提权请求标识格式无效。", nameof(requestId));
        }

        if (string.IsNullOrWhiteSpace(paths.IpcDirectory))
        {
            throw new InvalidOperationException("IPC 目录未配置。");
        }

        return Path.Combine(
            Path.GetFullPath(paths.IpcDirectory),
            RequestPrefix + requestId + RequestExtension);
    }

    /// <summary>Maps req-&lt;id&gt;.json to req-&lt;id&gt;.response.json next to it.</summary>
    public static string GetResponsePath(string requestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);

        var fullPath = Path.GetFullPath(requestPath);
        var fileName = Path.GetFileName(fullPath);
        if (!fileName.EndsWith(RequestExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("提权请求文件扩展名无效。", nameof(requestPath));
        }

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("提权请求文件路径无效。", nameof(requestPath));

        return Path.Combine(directory, fileName[..^RequestExtension.Length] + ResponseExtension);
    }

    /// <summary>
    /// Accepts only an absolute, existing, non-reparse request file that is a
    /// direct child of the IPC directory and matches req-&lt;id&gt;.json.
    /// </summary>
    public static bool TryValidateRequestPath(
        AppPaths paths,
        string? requestPath,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (paths is null)
        {
            error = "提权请求路径无效。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestPath) || !Path.IsPathFullyQualified(requestPath))
        {
            error = "提权请求路径必须是绝对路径。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(paths.IpcDirectory))
        {
            error = "IPC 目录未配置。";
            return false;
        }

        string ipcDirectory;
        try
        {
            ipcDirectory = Path.GetFullPath(paths.IpcDirectory);
            fullPath = Path.GetFullPath(requestPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            error = "提权请求路径无效。";
            return false;
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!IsSameDirectory(parent, ipcDirectory))
        {
            error = "提权请求路径不在允许的 IPC 目录内。";
            return false;
        }

        if (!RequestFileNameRegex().IsMatch(Path.GetFileName(fullPath)))
        {
            error = "提权请求文件名无效。";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "提权请求文件不存在。";
            return false;
        }

        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                error = "提权请求文件无效。";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "无法读取提权请求文件。";
            return false;
        }

        return true;
    }

    private static bool IsSameDirectory(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }

        return string.Equals(
            TrimEndSeparators(left),
            TrimEndSeparators(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEndSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
