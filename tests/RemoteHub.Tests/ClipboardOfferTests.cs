using System.IO;
using System.Linq;
using System.Threading;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Copies offered on this PC's clipboard (delayed rendering): fetched only when something pastes.</summary>
[Collection("Clipboard")]
public class ClipboardOfferTests
{
    [SkippableFact]
    public void Offered_files_are_fetched_only_when_a_paste_reads_them()
    {
        var file = Path.Combine(Path.GetTempPath(), "rh-offer-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "offered");
        var fetches = 0;
        var offer = new RemoteHub.Agent.DesktopTransfer(5, RemoteHub.Agent.DesktopTransferKind.Files, [new RemoteHub.Agent.DesktopTransferItem("a.txt", 7)], 7);
        try
        {
            ClipboardOffer.Instance.Offer(offer, _ => { Interlocked.Increment(ref fetches); return new OfferedContent(Items: [file]); });
            Skip.IfNot(SpinWait.SpinUntil(() => ClipboardOffer.Instance.OwnsClipboard, 3000), $"no clipboard in this session (0x{RemoteHub.Agent.VirtualFiles.LastError:X8})");
            Thread.Sleep(2000); // Explorer (if open) reads the new clipboard: the list only
            Assert.Equal(0, Volatile.Read(ref fetches));
            string? content = null;
            var t = new Thread(() =>
            {
                Assert.Equal(0, VirtualFilesTests.OleGetClipboard(out var data));
                content = System.Text.Encoding.UTF8.GetString(VirtualFilesTests.Read(data, "FileContents", 0, System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM));
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Assert.Equal("offered", content);
            Assert.Equal(1, Volatile.Read(ref fetches));
        }
        finally { File.Delete(file); }
    }

    [SkippableFact]
    public void An_offered_image_is_fetched_when_pasted_and_then_kept()
    {
        var fetches = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var offer = new RemoteHub.Agent.DesktopTransfer(6, RemoteHub.Agent.DesktopTransferKind.Image, [new RemoteHub.Agent.DesktopTransferItem("image.png", 1)], 1);
        var png = Png();
        // like AgentDesktopSession's fetch: programs that read every copy (clipboard sync, history, an agent's helper on this PC)
        // get nothing and aren't a paste
        ClipboardOffer.Instance.Offer(offer, paster =>
        {
            if (ClipboardOffer.BackgroundReader(paster)) return null;
            fetches.Enqueue(paster);
            return new OfferedContent(Image: png, Png: true);
        });
        Skip.IfNot(SpinWait.SpinUntil(() => ClipboardOffer.Instance.OwnsClipboard, 3000), "no clipboard in this session");
        Thread.Sleep(500);
        Assert.Empty(fetches);
        int Paste()
        {
            var width = 0;
            var t = new Thread(() => width = System.Windows.Clipboard.GetImage()?.PixelWidth ?? 0);
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return width;
        }
        Assert.Equal(2, Paste());
        Assert.Single(fetches);
        Assert.StartsWith("testhost", fetches.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Paste()); // pasted again: kept
        Assert.Single(fetches);
    }

    private static byte[] Png()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var s = new MemoryStream();
        encoder.Save(s);
        return s.ToArray();
    }

    [Theory]
    [InlineData("rdpclip (812)", true)]
    [InlineData("svchost (1312)", true)]
    [InlineData("RemoteHubAgent (12156)", true)] // the desktop helper of an agent on this PC
    [InlineData("explorer (4321)", false)]
    [InlineData("unknown", false)]
    public void Background_readers_are_not_pastes(string paster, bool background) =>
        Assert.Equal(background, ClipboardOffer.BackgroundReader(paster));

    [Fact]
    public void A_png_becomes_a_bottom_up_dib()
    {
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255 }, 8);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        var dib = DeviceImage.ToDib(DeviceImage.Decode(png.ToArray())!);
        Assert.Equal(40 + 16, dib.Length);
        Assert.Equal(2, BitConverter.ToInt32(dib, 8)); // height positive: bottom-up
        Assert.Equal(new byte[] { 7, 8, 9, 255 }, dib[40..44]); // first stored row = bottom row of the picture
    }

    /// <summary>What a paste of an offered image gets: (width of the image, the PNG format's bytes).</summary>
    private static (int Width, byte[]? Png) PasteImage()
    {
        (int, byte[]?) result = default;
        var t = new Thread(() =>
        {
            var png = System.Windows.Clipboard.GetData("PNG") is MemoryStream s ? s.ToArray() : null;
            result = (System.Windows.Clipboard.GetImage()?.PixelWidth ?? 0, png);
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    [SkippableFact]
    public void A_pasted_image_is_made_from_its_pixels_never_the_devices_bytes()
    {
        // A BMP with bytes after the picture: a program reading the device's file would get them, the paste mustn't.
        var bmp = DeviceImageTests.Encode(new System.Windows.Media.Imaging.BmpBitmapEncoder(), 3, 2);
        byte[] crafted = [.. bmp, .. "trailing bytes of the device"u8];
        var offer = new RemoteHub.Agent.DesktopTransfer(7, RemoteHub.Agent.DesktopTransferKind.Image, [new RemoteHub.Agent.DesktopTransferItem("image.bmp", crafted.Length)], crafted.Length);
        ClipboardOffer.Instance.Offer(offer, paster => ClipboardOffer.BackgroundReader(paster) ? null : new OfferedContent(Image: crafted));
        Skip.IfNot(SpinWait.SpinUntil(() => ClipboardOffer.Instance.OwnsClipboard, 3000), "no clipboard in this session");

        var (width, png) = PasteImage();
        Assert.Equal(3, width);
        Assert.NotNull(png);
        Assert.Equal(DeviceImage.Kind.Png, DeviceImage.Header(png)!.Value.Kind); // a PNG RemoteHub made, from a BMP
        Assert.Equal(DeviceImage.Decode(crafted)!.Bgra, DeviceImage.Decode(png)!.Bgra);
    }

    [SkippableFact]
    public void An_offered_image_in_another_format_pastes_as_nothing()
    {
        var gif = DeviceImageTests.Encode(new System.Windows.Media.Imaging.GifBitmapEncoder(), 2, 2);
        var offer = new RemoteHub.Agent.DesktopTransfer(8, RemoteHub.Agent.DesktopTransferKind.Image, [new RemoteHub.Agent.DesktopTransferItem("image.png", gif.Length)], gif.Length);
        ClipboardOffer.Instance.Offer(offer, paster => ClipboardOffer.BackgroundReader(paster) ? null : new OfferedContent(Image: gif, Png: true));
        Skip.IfNot(SpinWait.SpinUntil(() => ClipboardOffer.Instance.OwnsClipboard, 3000), "no clipboard in this session");

        var (width, png) = PasteImage();
        Assert.Equal(0, width);
        Assert.Null(png);
    }
}
