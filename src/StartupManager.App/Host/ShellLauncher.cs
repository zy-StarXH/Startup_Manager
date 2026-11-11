using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>
/// Opens OS surfaces for inspection. Scanned command lines are never executed:
/// only validated item metadata (a path to select, a registry key name, or a
/// fixed system tool) is used.
/// </summary>
internal static class ShellLauncher
{
    public static void OpenItemLocation(StartupItem item)
    {
        switch (item.Source)
        {
            case StartupSource.Folder:
                OpenFolderItem(item);
                break;
            case StartupSource.Task:
                StartShell("taskschd.msc", null);
                break;
            case StartupSource.Service:
                StartShell("services.msc", null);
                break;
            case StartupSource.Registry:
            default:
                OpenRegistryItem(item);
                break;
        }
    }

    public static void OpenDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("目录路径为空。");
        }

        Directory.CreateDirectory(path);
        StartShell("explorer.exe", "\"" + path + "\"");
    }

    public static void OpenUrl(string url) => StartShell(url, null);

    public static string BuildLocationText(StartupItem item)
    {
        var text = item.Location;
        if (item.Source == StartupSource.Registry && !string.IsNullOrWhiteSpace(item.SubLocation))
        {
            text = text.TrimEnd('\\') + "\\" + item.SubLocation;
        }
        else if (item.Source == StartupSource.Folder && !string.IsNullOrWhiteSpace(item.TargetPath))
        {
            text = item.TargetPath;
        }

        return text;
    }

    public static void CopyTextToClipboard(string text)
    {
        COMException? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException ex)
            {
                lastError = ex;
                Thread.Sleep(60);
            }
        }

        throw new InvalidOperationException("剪贴板当前被其他程序占用，请稍后重试。", lastError);
    }

    private static void OpenFolderItem(StartupItem item)
    {
        var target = item.TargetPath;
        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            StartShell("explorer.exe", "/select,\"" + target + "\"");
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.Location) && Directory.Exists(item.Location))
        {
            OpenDirectory(item.Location);
            return;
        }

        throw new InvalidOperationException("找不到该启动项对应的文件或文件夹。");
    }

    private static void OpenRegistryItem(StartupItem item)
    {
        var keyPath = ToRegeditPath(item.Location);
        StartShell("regedit.exe", keyPath is null ? null : "\"" + keyPath + "\"");
    }

    private static string? ToRegeditPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var trimmed = location.Trim();
        var parts = trimmed.Split('\\', 2);
        var hive = parts[0].ToUpperInvariant();
        var rest = parts.Length > 1 ? parts[1] : string.Empty;
        var fullHive = hive switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => "HKEY_CURRENT_USER",
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKEY_LOCAL_MACHINE",
            "HKCR" or "HKEY_CLASSES_ROOT" => "HKEY_CLASSES_ROOT",
            "HKU" or "HKEY_USERS" => "HKEY_USERS",
            _ => null
        };

        if (fullHive is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(rest) ? fullHive : fullHive + "\\" + rest;
    }

    private static void StartShell(string fileName, string? arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            info.Arguments = arguments;
        }

        var process = Process.Start(info);
        process?.Dispose();
    }
}
