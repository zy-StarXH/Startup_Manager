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

            return EncodeShellBitmap(bitmapHandle);
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

    /// <summary>
    /// GDI+ FromHbitmap discards the shell bitmap's alpha channel. Copy the
    /// native premultiplied BGRA pixels instead, retaining transparent borders
    /// and antialiased edges. No color key is used: black may be real artwork.
    /// </summary>
    internal static string? EncodeShellBitmap(IntPtr bitmapHandle)
    {
        if (GetBitmapObject(bitmapHandle, Marshal.SizeOf<NativeBitmap>(), out var native) == 0 ||
            native.BitsPerPixel != 32 || native.Width is <= 0 or > 4096 || native.Height is <= 0 or > 4096)
        {
            return null;
        }

        var stride = checked(native.Width * 4);
        var pixels = new byte[checked(stride * native.Height)];
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = native.Width,
            Height = -native.Height, // Request top-down rows, regardless of native orientation.
            Planes = 1,
            BitCount = 32,
            SizeImage = (uint)pixels.Length
        };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            if (GetDIBits(dc, bitmapHandle, 0, (uint)native.Height, pixels, ref info, 0) != native.Height)
                return null;
        }
        finally { DeleteDC(dc); }

        // Legacy icons may carry only an AND mask, with no usable alpha.
        // Let ExtractAssociatedIcon handle that mask instead of guessing colors.
        var hasAlpha = false;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            hasAlpha |= pixels[offset] != 0;
        if (!hasAlpha) return null;

        using var bitmap = new Bitmap(native.Width, native.Height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, native.Width, native.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < native.Height; row++)
                Marshal.Copy(pixels, row * stride, IntPtr.Add(data.Scan0, row * data.Stride), stride);
        }
        finally { bitmap.UnlockBits(data); }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetBitmapObject(IntPtr bitmap, int size, out NativeBitmap result);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] pixels, ref BitmapInfoHeader info, uint usage);
}
