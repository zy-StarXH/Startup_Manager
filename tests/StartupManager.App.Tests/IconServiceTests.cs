using System.Drawing;
using System.Runtime.InteropServices;
using StartupManager.App.Host;
using StartupManager.Core.Models;
using Xunit;

namespace StartupManager.App.Tests;

public sealed class IconServiceTests
{
    [Fact]
    public void Shell_bitmap_retains_transparency_colors_orientation_and_original_dimensions()
    {
        // Native shell pixels are premultiplied BGRA, with top-down rows.
        using var source = new NativeBitmap(3, 2, 32, new byte[]
        {
            0, 0, 0, 0,     0, 0, 128, 128,   0, 0, 0, 255,
            255, 0, 0, 255, 0, 128, 0, 128,   255, 255, 255, 255
        });
        using var decoded = Decode(IconService.EncodeShellBitmap(source.Handle));
        Assert.Equal(3, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(0, decoded.GetPixel(0, 0).A);
        Assert.Equal(Color.FromArgb(128, 255, 0, 0).ToArgb(), decoded.GetPixel(1, 0).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), decoded.GetPixel(2, 0).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), decoded.GetPixel(0, 1).ToArgb());
        Assert.Equal(Color.FromArgb(128, 0, 255, 0).ToArgb(), decoded.GetPixel(1, 1).ToArgb());
        Assert.Equal(Color.White.ToArgb(), decoded.GetPixel(2, 1).ToArgb());
    }

    [Fact]
    public void Opaque_black_artwork_is_not_removed_as_a_background_color()
    {
        using var source = new NativeBitmap(1, 1, 32, new byte[] { 0, 0, 0, 255 });
        using var decoded = Decode(IconService.EncodeShellBitmap(source.Handle));
        Assert.Equal(Color.Black.ToArgb(), decoded.GetPixel(0, 0).ToArgb());
        // Encoding borrows the native bitmap; the shell caller owns its lifetime.
        Assert.NotNull(IconService.EncodeShellBitmap(source.Handle));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void Bitmap_without_alpha_requests_the_legacy_icon_mask_fallback(ushort bits)
    {
        using var source = new NativeBitmap(1, 1, bits, new byte[] { 0, 0, 255, 0 });
        Assert.Null(IconService.EncodeShellBitmap(source.Handle));
    }

    [Fact]
    public void Invalid_bitmap_handle_falls_back_without_throwing()
    {
        Assert.Null(IconService.EncodeShellBitmap(IntPtr.Zero));
    }

    [Fact]
    public void Unavailable_icon_keeps_letter_fallback_and_existing_icon_is_unchanged()
    {
        var missing = new StartupItem { TargetPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") };
        var existing = new StartupItem { Icon = "data:image/png;base64,preserved", TargetPath = missing.TargetPath };
        new IconService().Enrich(new[] { missing, existing });
        Assert.Null(missing.Icon);
        Assert.Equal("data:image/png;base64,preserved", existing.Icon);
    }

    private static Bitmap Decode(string? uri)
    {
        Assert.NotNull(uri);
        Assert.StartsWith("data:image/png;base64,", uri);
        using var stream = new MemoryStream(Convert.FromBase64String(uri.Split(',')[1]));
        using var image = new Bitmap(stream);
        return new Bitmap(image);
    }

    private sealed class NativeBitmap : IDisposable
    {
        public IntPtr Handle { get; }

        public NativeBitmap(int width, int height, ushort bits, byte[] pixels)
        {
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width,
                Height = -height, Planes = 1, BitCount = bits
            };
            Handle = CreateDIBSection(IntPtr.Zero, ref info, 0, out var memory, IntPtr.Zero, 0);
            Assert.NotEqual(IntPtr.Zero, Handle);
            Marshal.Copy(pixels, 0, memory, pixels.Length);
        }

        public void Dispose() => DeleteObject(Handle);
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

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info,
        uint usage, out IntPtr pixels, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr bitmap);
}
