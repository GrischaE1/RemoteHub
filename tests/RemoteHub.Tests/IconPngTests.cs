extern alias agent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The icons in the app list (docs/app-tabs-design.md §6.5, §6.6): the agent's own PNG encoder, and an icon read from a
/// Windows icon handle – as RemoteHub decodes them.
/// </summary>
public class IconPngTests
{
    private static BitmapSource Decode(byte[] png)
    {
        using var s = new MemoryStream(png);
        return new FormatConvertedBitmap(BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
    }

    [Fact]
    public void An_encoded_image_decodes_to_the_same_pixels()
    {
        // 3×2, BGRA with transparency: a decoder that read rows or channels wrongly would show it
        byte[] bgra =
        [
            255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0,
            10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120,
        ];
        var png = A.IconPng.Encode(3, 2, bgra);
        Assert.True(A.IconPng.IsPng(png));
        var image = Decode(png);
        Assert.Equal((3, 2), (image.PixelWidth, image.PixelHeight));
        var px = new byte[3 * 2 * 4];
        image.CopyPixels(px, 3 * 4, 0);
        Assert.Equal(bgra, px);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(257, 1)]
    public void Only_images_of_a_usable_size_are_encoded(int width, int height) =>
        Assert.Throws<ArgumentException>(() => A.IconPng.Encode(width, height, new byte[Math.Max(1, width * height * 4)]));

    [Fact]
    public void An_icon_handle_becomes_a_small_png_with_its_transparency()
    {
        var icon = LoadIconW(0, 32512); // IDI_APPLICATION: shared, not destroyed
        Assert.NotEqual(0, icon);
        var png = A.IconPng.FromIcon(icon);
        Assert.NotNull(png);
        Assert.InRange(png.Length, 1, A.IconPng.MaxBytes);
        var image = Decode(png);
        Assert.InRange(image.PixelWidth, 16, 256);
        var px = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(px, image.PixelWidth * 4, 0);
        Assert.Contains(Enumerable.Range(0, px.Length / 4), i => px[i * 4 + 3] == 0); // the corners are see-through
        Assert.Contains(Enumerable.Range(0, px.Length / 4), i => px[i * 4 + 3] == 255);
        Assert.Null(A.IconPng.FromIcon(0));
    }

    [DllImport("user32.dll")] private static extern nint LoadIconW(nint instance, nint name);
}
