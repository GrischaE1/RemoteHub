using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteHub.Agent;

/// <summary>
/// An app's icon as the Start menu list sends it (docs/app-tabs-design.md §6.5, §6.6): 32×32 PNG, at most
/// <see cref="MaxBytes"/> – larger ones are left out and RemoteHub shows a generic icon. Encoded here (stored rows,
/// zlib) so the agent needs no imaging library; read from an icon handle (Start menu shortcuts, by the service) or a
/// bitmap handle (Store apps, by store-apps as the user).
/// </summary>
internal static class IconPng
{
    /// <summary>RemoteHub's limit for an icon from a device (IconImages.MaxDeviceIconBytes).</summary>
    public const int MaxBytes = 8 * 1024;

    public const int Size = 32;

    private const int MaxSide = 256;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>The bytes start like a PNG (RemoteHub decodes them defensively anyway).</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);

    /// <summary>A PNG of a top-down 32-bit BGRA image with straight alpha.</summary>
    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || bgra.Length < width * height * 4) throw new ArgumentException("Not an image of that size.");
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (int y = 0, o = 0; y < height; y++)
        {
            raw[o++] = 0; // no filter
            for (var x = 0; x < width; x++, o += 4)
            {
                var i = y * stride + x * 4;
                (raw[o], raw[o + 1], raw[o + 2], raw[o + 3]) = (bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]);
            }
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        Chunk(png, "IHDR", header);
        using (var data = new MemoryStream())
        {
            using (var z = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
            Chunk(png, "IDAT", data.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed.AsSpan(4));
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc32(typed));
        s.Write(four);
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    })];

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// The icon as PNG, or null when it can't be read or is larger than <see cref="MaxBytes"/>. An icon without an alpha
    /// channel takes its transparency from its mask. The handle stays the caller's.
    /// </summary>
    public static byte[]? FromIcon(nint icon)
    {
        if (icon == 0 || !GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.Color == 0 || Pixels(info.Color) is not { } color) return null; // a monochrome icon: none
            var px = color.Pixels;
            if (!HasAlpha(px))
            {
                var mask = info.Mask != 0 ? Pixels(info.Mask) : null;
                for (var i = 0; i < px.Length; i += 4)
                    px[i + 3] = mask is { } m && m.Width == color.Width && i + 2 < m.Pixels.Length && m.Pixels[i] != 0 ? (byte)0 : (byte)255;
            }
            return Capped(Encode(color.Width, color.Height, px));
        }
        finally
        {
            if (info.Color != 0) DeleteObject(info.Color);
            if (info.Mask != 0) DeleteObject(info.Mask);
        }
    }

    /// <summary>
    /// A 32-bit bitmap with premultiplied alpha (IShellItemImageFactory) as PNG, or null as for <see cref="FromIcon"/>.
    /// The handle stays the caller's.
    /// </summary>
    public static byte[]? FromBitmap(nint bitmap)
    {
        if (bitmap == 0 || Pixels(bitmap) is not { } image) return null;
        var px = image.Pixels;
        if (!HasAlpha(px))
            for (var i = 3; i < px.Length; i += 4) px[i] = 255;
        else
            for (var i = 0; i < px.Length; i += 4)
            {
                var a = px[i + 3];
                if (a is 0 or 255) continue;
                for (var c = 0; c < 3; c++) px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
            }
        return Capped(Encode(image.Width, image.Height, px));
    }

    private static byte[]? Capped(byte[] png) => png.Length <= MaxBytes ? png : null;

    private static bool HasAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return true;
        return false;
    }

    /// <summary>The bitmap's pixels as top-down 32-bit BGRA, or null when it can't be read or is larger than 256×256.</summary>
    private static (int Width, int Height, byte[] Pixels)? Pixels(nint bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        var (w, h) = (bm.Width, Math.Abs(bm.Height));
        if (w is < 1 or > MaxSide || h is < 1 or > MaxSide) return null;
        var header = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = CreateCompatibleDC(0);
        try { return GetDIBits(dc, bitmap, 0, (uint)h, px, ref header, 0 /* DIB_RGB_COLORS */) == h ? (w, h, px) : null; }
        finally { DeleteDC(dc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool IsIcon;
        public int HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out ICONINFO info);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}
