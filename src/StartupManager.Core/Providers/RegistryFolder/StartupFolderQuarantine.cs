using System.Text.Json;
using StartupManager.Core.Models;

namespace StartupManager.Core.Providers.RegistryFolder;

/// <summary>
/// A folder outside every Windows Startup directory used to hold disabled
/// shortcuts. Moving the shortcut, rather than only setting an approval byte,
/// also covers launchers which do not honor StartupApproved.
/// </summary>
public static class StartupFolderQuarantine
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StartupManager", "DisabledStartupItems");

    public static string GetManifestPath(string directory, string itemId) =>
        Path.Combine(directory, itemId + ".json");

    public static string GetPath(string directory, string itemId, string originalPath)
    {
        if (string.IsNullOrWhiteSpace(itemId) || itemId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new InvalidOperationException("启动项标识无效，无法保存已关闭的启动文件。");
        }

        var extension = Path.GetExtension(originalPath);
        if (extension.Length > 16 || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
        {
            throw new InvalidOperationException("启动文件扩展名无效，无法保存已关闭的启动文件。");
        }

        return Path.Combine(directory, itemId + extension.ToLowerInvariant());
    }

    public static void Disable(string directory, string itemId, string originalPath)
    {
        Directory.CreateDirectory(directory);
        var quarantined = GetPath(directory, itemId, originalPath);
        if (File.Exists(quarantined))
        {
            throw new InvalidOperationException("该启动文件已有已关闭副本，未重复移动。请先刷新状态。");
        }

        var manifestPath = GetManifestPath(directory, itemId);
        if (File.Exists(manifestPath))
        {
            throw new InvalidOperationException("该启动文件已有已关闭记录，未重复移动。请先刷新状态。");
        }

        File.Move(originalPath, quarantined);
        try
        {
            var manifest = JsonSerializer.Serialize(
                new QuarantineManifest { OriginalPath = originalPath },
                JsonDefaults.Indented);
            File.WriteAllText(manifestPath, manifest);
        }
        catch
        {
            if (!File.Exists(originalPath) && File.Exists(quarantined))
            {
                File.Move(quarantined, originalPath);
            }

            throw;
        }
    }

    public static void Restore(string directory, string itemId, string originalPath)
    {
        var quarantined = GetPath(directory, itemId, originalPath);
        if (!File.Exists(quarantined))
        {
            if (File.Exists(originalPath))
            {
                return;
            }

            throw new InvalidOperationException("找不到已关闭的启动文件，无法恢复。");
        }

        if (File.Exists(originalPath))
        {
            throw new InvalidOperationException("原位置已存在同名启动文件，未覆盖它。请先检查启动文件夹。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        File.Move(quarantined, originalPath);
        File.Delete(GetManifestPath(directory, itemId));
    }

    public static string? FindOriginalPath(string directory, string fileName)
    {
        if (!fileName.StartsWith("dir-", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length <= 68)
        {
            return null;
        }

        var itemId = fileName[..68];
        var manifestPath = GetManifestPath(directory, itemId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<QuarantineManifest>(
                File.ReadAllText(manifestPath), JsonDefaults.Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.OriginalPath) ||
                !string.Equals(
                    Path.GetExtension(manifest.OriginalPath),
                    Path.GetExtension(fileName),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return manifest.OriginalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("无法读取已关闭启动文件的原路径记录。", ex);
        }
    }

    public sealed class QuarantineManifest
    {
        public string OriginalPath { get; set; } = string.Empty;
    }

    public static bool IsQuarantined(string directory, string itemId, string originalPath) =>
        File.Exists(GetPath(directory, itemId, originalPath));

    public static StartupItem? CreateDisabledItem(string directory, string itemId, string originalPath, StartupScope scope)
    {
        var quarantined = GetPath(directory, itemId, originalPath);
        if (!File.Exists(quarantined))
        {
            return null;
        }

        var valueName = Path.GetFileName(originalPath);
        StartupApproved.TryReadApproval(
            StartupApprovalFamily.StartupFolder,
            valueName,
            out var approval,
            out _);
        var command = originalPath;
        string? targetPath = originalPath;
        if (Path.GetExtension(originalPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            ShellShortcutResolver.TryResolve(quarantined, out var target, out var arguments))
        {
            targetPath = target;
            command = target;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                command += " " + arguments;
            }
        }

        return new StartupItem
        {
            Id = itemId,
            Name = Path.GetFileNameWithoutExtension(originalPath),
            Source = StartupSource.Folder,
            Scope = scope,
            Enabled = false,
            RequiresAdmin = scope == StartupScope.AllUsers,
            ManagedByApp = true,
            Location = originalPath,
            SubLocation = "快捷方式已移出 Windows 启动文件夹，可恢复",
            Command = command,
            TargetPath = targetPath,
            LastModified = File.GetLastWriteTime(quarantined),
            Mechanism = "startupFolderQuarantine",
            Metadata =
            {
                ["scope"] = scope.ToString(),
                ["fullPath"] = originalPath,
                ["folder"] = Path.GetDirectoryName(originalPath) ?? string.Empty,
                ["approvalFamily"] = StartupApprovalFamily.StartupFolder.ToString(),
                ["approvalValueName"] = valueName,
                ["quarantined"] = "true",
                ["approvalRawBase64"] = approval is null ? string.Empty : Convert.ToBase64String(approval)
            }
        };
    }

    public static FolderApprovalState Capture(string directory, string itemId, string originalPath, StartupScope scope)
    {
        var valueName = Path.GetFileName(originalPath);
        StartupApproved.TryReadApproval(
            StartupApprovalFamily.StartupFolder,
            valueName,
            out var approval,
            out var exists);
        var quarantined = GetPath(directory, itemId, originalPath);
        return new FolderApprovalState
        {
            Scope = scope.ToString(),
            FullPath = originalPath,
            ValueName = valueName,
            ApprovalFamily = StartupApprovalFamily.StartupFolder.ToString(),
            ApprovalValueExisted = exists,
            ApprovalRawBase64 = approval is null ? null : Convert.ToBase64String(approval),
            Command = originalPath,
            Quarantined = File.Exists(quarantined),
            ManagesFile = true,
            QuarantineDirectory = directory
        };
    }
}
