using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteHub.Services;

/// <summary>An image ready to store: PNG or JPEG, re-encoded, without metadata.</summary>
public sealed record PreparedImage(string Mime, int Width, int Height, byte[] Data);

/// <summary>The image can't be used (not an image, too large).</summary>
public sealed class NoteImageException(string message) : Exception(message);

/// <summary>
/// Checks and re-encodes images for notes (docs/notes-view-design.md §4.5.4). Only pixels are kept: re-encoding drops all
/// metadata (EXIF with GPS position, camera, author) and anything else the source file held.
/// </summary>
public static class NoteImages
{
    /// <summary>Largest image stored, after re-encoding.</summary>
    public const int MaxStoredBytes = 25 * 1024 * 1024;
    /// <summary>Largest file read at all.</summary>
    public const int MaxSourceBytes = 64 * 1024 * 1024;
    /// <summary>Images with a longer side are scaled down to it.</summary>
    public const int MaxSide = 16384;
    /// <summary>Images whose header claims more than this are refused before any pixel is decoded.</summary>
    public const int RefuseSide = 65535;
    public const long RefusePixels = 300_000_000;
    /// <summary>A photo whose PNG would be larger than this is stored as JPEG (quality 90).</summary>
    public const int PhotoPngLimit = 1024 * 1024;
    public const int MaxImagesPerPage = 200;

    public static PreparedImage Prepare(byte[] source)
    {
        if (source.Length == 0) throw new NoteImageException("The file is empty.");
        if (source.Length > MaxSourceBytes) throw new NoteImageException("The image is larger than 64 MB.");
        BitmapFrame frame;
        bool jpegSource;
        try
        {
            using var ms = new MemoryStream(source, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) throw new NoteImageException("The file holds no image.");
            frame = decoder.Frames[0];
            jpegSource = decoder is JpegBitmapDecoder;
            // The header's size, before any pixel is decoded: refuse what would take gigabytes of memory.
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > RefuseSide || frame.PixelHeight > RefuseSide
                || (long)frame.PixelWidth * frame.PixelHeight > RefusePixels)
                throw new NoteImageException("The image is too large.");
            BitmapSource pixels = frame;
            var longer = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longer > MaxSide)
            {
                var scale = (double)MaxSide / longer;
                pixels = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            pixels = new FormatConvertedBitmap(pixels, PixelFormats.Bgra32, null, 0);
            var data = Encode(new PngBitmapEncoder(), pixels);
            var mime = "image/png";
            if (jpegSource && data.Length > PhotoPngLimit)
            {
                data = Encode(new JpegBitmapEncoder { QualityLevel = 90 }, new FormatConvertedBitmap(pixels, PixelFormats.Bgr24, null, 0));
                mime = "image/jpeg";
            }
            if (data.Length > MaxStoredBytes) throw new NoteImageException("The image is larger than 25 MB even after compressing it.");
            return new PreparedImage(mime, pixels.PixelWidth, pixels.PixelHeight, data);
        }
        catch (NoteImageException) { throw; }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException or OverflowException or OutOfMemoryException)
        {
            throw new NoteImageException("The file isn't an image RemoteHub can read.");
        }
    }

    /// <summary>Prepares an image that is already decoded (pasted from the clipboard).</summary>
    public static PreparedImage Prepare(BitmapSource image)
    {
        if (image.PixelWidth > RefuseSide || image.PixelHeight > RefuseSide) throw new NoteImageException("The image is too large.");
        return Prepare(Encode(new PngBitmapEncoder(), new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0)));
    }

    /// <summary>A stored image for display (decoded once, frozen).</summary>
    public static BitmapSource? Load(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(data, writable: false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
