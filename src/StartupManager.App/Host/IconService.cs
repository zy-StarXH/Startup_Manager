using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using StartupManager.Core.Models;

namespace StartupManager.App.Host;

/// <summary>
/// Extracts shell icons for startup executables into data-URI PNGs. Extraction
/// failures are expected (packaged apps, missing files, ACLs) and result in the
/// web UI's letter-tile fallback. The service never launches the executable.
/// </summary>
internal sealed class IconService
{
    private const int IconSize = 64;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public void Enrich(IEnumerable<StartupItem> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Icon))
            {
                continue;
            }

            var targetPath = item.TargetPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                continue;
            }

            if (!_cache.TryGetValue(targetPath, out var dataUri))
            {
                dataUri = TryLoad(targetPath);
                _cache[targetPath] = dataUri;
            }

            if (!string.IsNullOrWhiteSpace(dataUri))
            {
                item.Icon = dataUri;
            }
        }
    }

    private static string? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return TryShellImage(path) ?? TryAssociatedIcon(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryShellImage(string path)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (factory is null)
            {
                return null;
            }

            var hr = factory.GetImage(
                new NativeSize(IconSize, IconSize),
                ShellItemImageFactoryFlags.IconOnly | ShellItemImageFactoryFlags.BiggerSizeOk,
                out bitmapHandle);
            if (hr != 0 || bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            using var image = Image.FromHbitmap(bitmapHandle);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (factory is not null)
            {
                try { Marshal.ReleaseComObject(factory); } catch { /* best effort */ }
            }
        }
    }

    private static string? TryAssociatedIcon(string path)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, ShellItemImageFactoryFlags flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize
    {
        public NativeSize(int width, int height)
        {
            cx = width;
            cy = height;
        }

        public readonly int cx;
        public readonly int cy;
    }

    [Flags]
    private enum ShellItemImageFactoryFlags : uint
    {
        ResizeToFit = 0x00000000,
        BiggerSizeOk = 0x00000001,
        MemoryOnly = 0x00000002,
        IconOnly = 0x00000004,
        ThumbnailOnly = 0x00000008,
        InCacheOnly = 0x00000010,
        CropToSquare = 0x00000020,
        WideThumbnails = 0x00000040,
        IconBackground = 0x00000080,
        ScaleUp = 0x00000100
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
