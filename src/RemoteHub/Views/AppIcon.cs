using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteHub.Views;

/// <summary>
/// The app icon at a given size. WPF takes the first frame of an .ico (16 px) and scales it up, which looks blurry;
/// this picks the frame that fits the size and the screen scaling (the icon has 16–256 px frames).
/// </summary>
public static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/app.ico");

    /// <param name="size">Size in device-independent pixels.</param>
    /// <param name="visual">Used to read the screen scaling (DPI); null = 100 %.</param>
    public static ImageSource? Get(double size, Visual? visual = null)
    {
        try
        {
            var scale = visual is null ? 1.0 : VisualTreeHelper.GetDpi(visual).DpiScaleX;
            var needed = size * Math.Max(1.0, scale);
            var frames = BitmapDecoder.Create(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            // Smallest frame that is at least as big as needed; otherwise the biggest one.
            return frames.Where(f => f.PixelWidth >= needed).OrderBy(f => f.PixelWidth).FirstOrDefault()
                   ?? frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }

    /// <summary>Sets the icon on an image now and again when the window moves to a screen with other scaling.</summary>
    public static void Apply(System.Windows.Controls.Image image, double size)
    {
        image.Width = image.Height = size;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Source = Get(size);
        image.Loaded += (_, _) => image.Source = Get(size, image);
        image.DpiChanged += (_, _) => image.Source = Get(size, image);
    }

    /// <summary>
    /// The window's own icon (taskbar, Alt+Tab). Without it Windows reads the icon from RemoteHub.exe, which can come
    /// up empty when the exe is started from a network share. WPF picks the small and large frames from the .ico.
    /// </summary>
    public static void ApplyTo(Window window)
    {
        try { window.Icon = BitmapFrame.Create(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
        catch { /* the exe's icon is used */ }
    }

    /// <summary>For title bars (WPF-UI ImageIcon).</summary>
    public static void Apply(Wpf.Ui.Controls.ImageIcon icon, double size)
    {
        icon.Width = icon.Height = size;
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.Source = Get(size);
        icon.Loaded += (_, _) => icon.Source = Get(size, icon);
    }
}
