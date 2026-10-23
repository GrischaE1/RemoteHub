using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// What the console decodes from a device (decoder-isolation-design.md §3.5, §3.6): clipboard images only as PNG, BMP
/// or JPEG within the size limit, read from RemoteHub's own header parsing first; video messages checked before the
/// decoder sees them.
/// </summary>
public class DeviceImageTests
{
    /// <summary>A <paramref name="width"/> × <paramref name="height"/> picture (a colour per pixel) in the encoder's format.</summary>
    internal static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(i * 7), (byte)(i * 3), (byte)i, 255);
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static TheoryData<string> Formats => ["png", "bmp", "jpeg"];

    private static BitmapEncoder EncoderFor(string format) => format switch
    {
        "png" => new PngBitmapEncoder(),
        "bmp" => new BmpBitmapEncoder(),
        _ => new JpegBitmapEncoder { QualityLevel = 100 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Png_bmp_and_jpeg_are_read_with_the_size_their_header_states(string format)
    {
        var bytes = Encode(EncoderFor(format), 5, 3);
        var header = DeviceImage.Header(bytes)!.Value;
        Assert.Equal((5, 3), (header.Width, header.Height));
        Assert.Equal(format, header.Kind.ToString().ToLowerInvariant());
        var image = DeviceImage.Decode(bytes)!;
        Assert.Equal((5, 3, 5 * 3 * 4), (image.Width, image.Height, image.Bgra.Length));
    }

    [Fact]
    public void Png_and_bmp_keep_their_pixels_exactly()
    {
        var png = DeviceImage.Decode(Encode(new PngBitmapEncoder(), 4, 4))!;
        var bmp = DeviceImage.Decode(Encode(new BmpBitmapEncoder(), 4, 4))!;
        Assert.Equal(png.Bgra, bmp.Bgra);
        Assert.Equal(png.Bgra, DeviceImage.Decode(DeviceImage.ToPng(png))!.Bgra); // RemoteHub's own PNG of the pixels
    }

    [Fact]
    public void Other_formats_and_noise_are_not_decoded()
    {
        Assert.Null(DeviceImage.Decode(Encode(new GifBitmapEncoder(), 2, 2)));
        Assert.Null(DeviceImage.Decode(Encode(new TiffBitmapEncoder(), 2, 2)));
        Assert.Null(DeviceImage.Decode([]));
        Assert.Null(DeviceImage.Decode("BM"u8.ToArray()));
        Assert.Null(DeviceImage.Decode(new byte[64]));
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(1, 8193)]
    [InlineData(0, 10)]
    [InlineData(100_000, 100_000)]
    public void A_png_whose_header_states_a_size_outside_the_limit_is_refused_before_decoding(int width, int height)
    {
        var png = Encode(new PngBitmapEncoder(), 2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width); // the IHDR chunk (its CRC no longer fits: never read)
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        Assert.Null(DeviceImage.Header(png));
        Assert.Null(DeviceImage.Decode(png));
    }

    [Fact]
    public void A_bmp_states_its_size_in_either_header_and_may_be_top_down()
    {
        var bmp = Encode(new BmpBitmapEncoder(), 6, 4);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -4); // top-down
        Assert.Equal((6, 4), DeviceImage.Header(bmp) is { } h ? (h.Width, h.Height) : default);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 9000);
        Assert.Null(DeviceImage.Header(bmp));

        byte[] core = [(byte)'B', (byte)'M', .. new byte[12], 12, 0, 0, 0, 7, 0, 3, 0, 1, 0, 24, 0];
        Assert.Equal((DeviceImage.Kind.Bmp, 7, 3), DeviceImage.Header(core));
    }

    [Fact]
    public void A_jpeg_without_a_frame_header_before_its_scan_is_refused()
    {
        byte[] scanFirst = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 1, 2, 3, 4, 5, 6];
        Assert.Null(DeviceImage.Header(scanFirst));
        byte[] badLength = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01];
        Assert.Null(DeviceImage.Header(badLength));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Truncated_and_bit_flipped_images_give_pixels_or_null_never_an_exception(string format)
    {
        var good = Encode(EncoderFor(format), 16, 16);
        var random = new Random(4711);
        for (var n = 0; n < 300; n++)
        {
            var bad = n % 3 == 0 ? good[..random.Next(good.Length)] : (byte[])good.Clone();
            if (n % 3 != 0)
                for (var k = 0; k < 1 + random.Next(8); k++) bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
            var image = DeviceImage.Decode(bad);
            if (image is not null) Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        }
    }

    [Fact]
    public void A_dib_is_built_from_the_pixels_bottom_up()
    {
        var image = new DeviceImagePixels(1, 2, [1, 2, 3, 4, 5, 6, 7, 8]);
        var dib = DeviceImage.ToDib(image);
        Assert.Equal((40, 1, 2, (short)1, (short)32), (BitConverter.ToInt32(dib, 0), BitConverter.ToInt32(dib, 4), BitConverter.ToInt32(dib, 8), BitConverter.ToInt16(dib, 12), BitConverter.ToInt16(dib, 14)));
        Assert.Equal(new byte[] { 5, 6, 7, 8, 1, 2, 3, 4 }, dib[40..]);
    }

    private static byte[] Video(int width, int height, int data = 4) =>
        DesktopWire.Video(new VideoHeader(42, width, height, true), new byte[data]);

    [Fact]
    public void A_video_message_within_the_limits_goes_to_the_decoder()
    {
        Assert.Null(AgentDesktopSession.CheckVideo(Video(1920, 1080), out var h));
        Assert.Equal((42u, 1920, 1080, true), (h.FrameId, h.Width, h.Height, h.KeyFrame));
        Assert.Null(AgentDesktopSession.CheckVideo(Video(DesktopProtocol.MaxVideoWidth, DesktopProtocol.MaxVideoHeight), out _));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(DesktopProtocol.MaxVideoWidth + 1, 1080)]
    [InlineData(1920, DesktopProtocol.MaxVideoHeight + 1)]
    [InlineData(65535, 65535)]
    public void A_video_message_with_a_picture_size_outside_the_limits_is_a_bad_frame(int width, int height)
    {
        Assert.Contains("picture size", AgentDesktopSession.CheckVideo(Video(width, height), out var h));
        Assert.Equal(42u, h.FrameId); // still acknowledged
    }

    [Fact]
    public void A_video_message_shorter_than_its_header_or_without_data_is_a_bad_frame()
    {
        Assert.Contains("shorter than its header", AgentDesktopSession.CheckVideo(Video(1920, 1080)[..5], out _));
        Assert.Equal("no picture data", AgentDesktopSession.CheckVideo(Video(1920, 1080, data: 0), out _));
    }
}
