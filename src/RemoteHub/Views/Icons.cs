using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteHub.Views;

/// <summary>Built-in icons offered for folders and connections (Fluent symbol names).</summary>
public static class IconCatalog
{
    public sealed record Item(string? Symbol, string Label);

    public static readonly IReadOnlyList<Item> All =
    [
        new(null, "Default"),
        new("Desktop24", "Desktop"), new("Server24", "Server"), new("Database24", "Database"), new("Laptop24", "Laptop"),
        new("Window24", "Window"), new("Cloud24", "Cloud"), new("Globe24", "Web"), new("Shield24", "Firewall / security"),
        new("LockClosed24", "Locked"), new("Key24", "Key"), new("Fingerprint24", "Identity"), new("Router24", "Router"),
        new("VirtualNetwork24", "Network"), new("Storage24", "Storage"), new("HardDrive24", "Disk"), new("Print24", "Printer"),
        new("Mail24", "Mail"), new("Phone24", "Phone"), new("Code24", "Development"), new("Branch24", "Build / Git"),
        new("Beaker24", "Test"), new("Bug24", "Debug"), new("Rocket24", "Deploy"), new("Flash24", "Power"),
        new("Toolbox24", "Tools"), new("Wrench24", "Maintenance"), new("Settings24", "Settings"), new("Building24", "Office"),
        new("BuildingFactory24", "Factory / OT"), new("Home24", "Home"), new("People24", "Users"), new("Organization24", "Org"),
        new("Box24", "Box"), new("Cube24", "Container / VM"), new("Diagram24", "Diagram"), new("Lightbulb24", "Idea"), new("Star24", "Favourite"),
    ];
}

/// <summary>Custom icon images: load from a file (scaled to max 64 px, stored as PNG/base64) and display them.</summary>
public static class IconImages
{
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new();

    public static string LoadAsBase64Png(string path, int maxSize = 64)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First(); // .ico: largest frame
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static ImageSource? ToImage(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        return Cache.GetOrAdd(base64, b =>
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(Convert.FromBase64String(b));
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        });
    }
}

public sealed class Base64ImageConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        try { return IconImages.ToImage(value as string); }
        catch { return null; }
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
