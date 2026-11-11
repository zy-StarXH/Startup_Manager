using System.IO;
using System.Text.Json;
using System.Windows;
using StartupManager.Core;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

internal sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Best-effort window placement persistence (DIPs, per user).</summary>
internal static class WindowPlacementStore
{
    private static string FilePath => Path.Combine(AppPaths.CreateDefault().DataDirectory, "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(path), JsonDefaults.Options);
            if (placement is null || placement.Width < 900 || placement.Height < 620)
            {
                return null;
            }

            return IsOnScreen(placement) ? placement : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not load window placement.", ex);
            return null;
        }
    }

    public static void Save(WindowPlacement placement)
    {
        try
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(placement, JsonDefaults.Indented));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save window placement.", ex);
        }
    }

    private static bool IsOnScreen(WindowPlacement placement)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var overlapWidth = Math.Min(placement.Left + placement.Width, virtualRight) - Math.Max(placement.Left, virtualLeft);
        var overlapHeight = Math.Min(placement.Top + placement.Height, virtualBottom) - Math.Max(placement.Top, virtualTop);
        return overlapWidth >= 120 && overlapHeight >= 120;
    }
}
