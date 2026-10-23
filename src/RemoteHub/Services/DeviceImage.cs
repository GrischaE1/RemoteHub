using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteHub.Services;

/// <summary>An image from a device as pixels: BGRA, 32 bits, top-down, no padding.</summary>
public sealed record DeviceImagePixels(int Width, int Height, byte[] Bgra)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Decodes an image a device put on the clipboard. The device chooses every byte, and image decoders are native code
/// with memory-corruption bugs found every year (decoder-isolation-design.md §3.5, §3.6), so:
/// <list type="bullet">
/// <item>only PNG, BMP and JPEG, each with its own decoder – never WIC's auto-detection, which tries every installed
/// codec;</item>
/// <item>the size is read from the header by RemoteHub itself, before any decoder sees the bytes, and must be at most
/// <see cref="MaxSide"/> on each side; the decoded frame must have that same size;</item>
/// <item>colour profiles aren't read (one more parser);</item>
/// <item>what reaches this PC's clipboard is built from the pixels (<see cref="ToBitmapSource"/>, <see cref="ToPng"/>,
/// <see cref="ToDib"/>), never the device's bytes – so a crafted file can't reach the program the user pastes into.</item>
/// </list>
/// </summary>
public static class DeviceImage
{
    public const int MaxSide = 8192;

    public enum Kind { Png, Bmp, Jpeg }

    /// <summary>The format and size the header states, or null when it isn't a PNG, BMP or JPEG RemoteHub can read the size of.</summary>
    public static (Kind Kind, int Width, int Height)? Header(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 24 && b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])
            && b[12..16].SequenceEqual("IHDR"u8))
            return Fit(Kind.Png, BinaryPrimitives.ReadUInt32BigEndian(b[16..]), BinaryPrimitives.ReadUInt32BigEndian(b[20..]));

        if (b.Length >= 26 && b[0] == 'B' && b[1] == 'M')
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(b[14..]);
            if (size == 12) return Fit(Kind.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(b[18..]), BinaryPrimitives.ReadUInt16LittleEndian(b[20..]));
            if (size >= 40 && b.Length >= 26)
            {
                var (w, h) = (BinaryPrimitives.ReadInt32LittleEndian(b[18..]), BinaryPrimitives.ReadInt32LittleEndian(b[22..]));
                return w < 0 || h == int.MinValue ? null : Fit(Kind.Bmp, (uint)w, (uint)Math.Abs(h)); // a negative height is top-down
            }
            return null;
        }

        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            // The markers up to the frame header (SOFn) that holds the size; each but the standalone ones has a length.
            for (var i = 2; i + 4 <= b.Length;)
            {
                if (b[i] != 0xFF) return null;
                var marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; } // fill byte
                if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                if (marker is 0xD9 or 0xDA) return null; // end of image or scan data before a frame header
                var length = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
                if (length < 2) return null;
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return i + 9 <= b.Length && length >= 7
                        ? Fit(Kind.Jpeg, BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]))
                        : null;
                i += 2 + length;
            }
        }
        return null;
    }

    private static (Kind, int, int)? Fit(Kind kind, uint width, uint height) =>
        width is >= 1 and <= MaxSide && height is >= 1 and <= MaxSide ? (kind, (int)width, (int)height) : null;

    /// <summary>The image's pixels, or null when it isn't a PNG, BMP or JPEG within the limits or can't be decoded.</summary>
    public static DeviceImagePixels? Decode(byte[] bytes)
    {
        if (Header(bytes) is not var (kind, width, height)) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            const BitmapCreateOptions options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
            BitmapDecoder decoder = kind switch
            {
                Kind.Png => new PngBitmapDecoder(stream, options, BitmapCacheOption.OnLoad),
                Kind.Bmp => new BmpBitmapDecoder(stream, options, BitmapCacheOption.OnLoad),
                _ => new JpegBitmapDecoder(stream, options, BitmapCacheOption.OnLoad),
            };
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height) return null;
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * 4 * height];
            bgra.CopyPixels(pixels, width * 4, 0);
            return new DeviceImagePixels(width, height, pixels);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException or ArgumentException
                                       or InvalidOperationException or OverflowException or IOException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The pixels as a frozen WPF bitmap (for <c>Clipboard.SetImage</c>).</summary>
    public static BitmapSource ToBitmapSource(DeviceImagePixels image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        source.Freeze();
        return source;
    }

    /// <summary>The pixels as a new PNG file.</summary>
    public static byte[] ToPng(DeviceImagePixels image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource(image)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The pixels as a 32-bit packed DIB (CF_DIB): a BITMAPINFOHEADER RemoteHub writes, the rows bottom-up.</summary>
    public static byte[] ToDib(DeviceImagePixels image)
    {
        int w = image.Width, h = image.Height, stride = image.Stride;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);   // positive: bottom-up
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * h).CopyTo(dib, 20);
        for (var y = 0; y < h; y++) Buffer.BlockCopy(image.Bgra, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        return dib;
    }
}
