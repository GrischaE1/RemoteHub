using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemoteHub.Views;

/// <summary>Colour tags that folders/connections can carry (inherited); used for the session frame, tabs and tree.</summary>
public static class ColorTags
{
    public sealed record Tag(string Id, string Label, Color Color);

    public static readonly IReadOnlyList<Tag> All =
    [
        new("Blue", "Blue", Color.FromRgb(0x3B, 0x82, 0xF6)),
        new("Green", "Green – e.g. test", Color.FromRgb(0x22, 0xC5, 0x5E)),
        new("Orange", "Orange – e.g. staging", Color.FromRgb(0xF5, 0x9E, 0x0B)),
        new("Red", "Red – e.g. production", Color.FromRgb(0xEF, 0x44, 0x44)),
        new("Purple", "Purple", Color.FromRgb(0xA8, 0x55, 0xF7)),
        new("Teal", "Teal", Color.FromRgb(0x14, 0xB8, 0xA6)),
        new("Pink", "Pink", Color.FromRgb(0xEC, 0x48, 0x99)),
        new("Yellow", "Yellow", Color.FromRgb(0xEA, 0xB3, 0x08)),
        new("Indigo", "Indigo", Color.FromRgb(0x63, 0x66, 0xF1)),
        new("Grey", "Grey", Color.FromRgb(0x94, 0xA3, 0xB8)),
    ];

    public const string None = "None";

    /// <summary>Palette id, or a custom colour "#RRGGBB".</summary>
    public static Color? Get(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == None) return null;
        if (All.FirstOrDefault(t => t.Id == id) is { } tag) return tag.Color;
        return TryParseHex(id, out var c) ? c : null;
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var t = (text ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(t, "^#[0-9a-fA-F]{6}$")) return false;
        color = (Color)ColorConverter.ConvertFromString(t);
        return true;
    }

    public static SolidColorBrush? Brush(string? id) => Get(id) is { } c ? Frozen(c) : null;

    public static SolidColorBrush Frozen(Color c, byte alpha = 255)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>
/// App "chrome": solid, slightly blue-tinted surfaces (instead of see-through Mica) so the window is easy to tell
/// apart from other apps and from the remote desktop, plus an accent-coloured window border on Windows 11.
/// </summary>
public static class AppChrome
{
    /// <summary>Brand accent used for the window border and the default session frame.</summary>
    public static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>The palette in use (set by <see cref="ApplyPalette"/>).</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the palette changed, for colours that aren't resources (syntax colouring).</summary>
    public static event Action? PaletteChanged;

    public static void ApplyPalette(bool dark)
    {
        var r = Application.Current.Resources;
        void Set(string key, string hex) => r[key] = ColorTags.Frozen((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            Set("ChromeBackgroundBrush", "#161B26");   // window + title bar
            Set("SidebarBackgroundBrush", "#1C2230");  // connection list
            Set("SessionAreaBrush", "#0F131B");        // behind sessions (darker than any remote desktop chrome)
            Set("SessionHeaderBrush", "#232A3A");
            Set("ChromeStrokeBrush", "#2E3850");
            Set("NoteCodeBackgroundBrush", "#121722");  // script blocks and inline code in notes
        }
        else
        {
            Set("ChromeBackgroundBrush", "#E3E9F3");
            Set("SidebarBackgroundBrush", "#EDF1F8");
            Set("SessionAreaBrush", "#CBD5E4");
            Set("SessionHeaderBrush", "#F7F9FC");
            Set("ChromeStrokeBrush", "#B7C3D6");
            Set("NoteCodeBackgroundBrush", "#F4F6FA");
        }
        r["BrandAccentBrush"] = ColorTags.Frozen(Accent);
        IsDark = dark;
        PaletteChanged?.Invoke();
    }

    // DWMWA_BORDER_COLOR (Windows 11 22000+). Ignored on older Windows.
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>Accent border while the window is active, muted when inactive.</summary>
    public static void UseAccentBorder(Window w)
    {
        void Apply(bool active)
        {
            var c = active ? Accent : Color.FromRgb(0x5A, 0x64, 0x78);
            SetBorder(w, c);
        }
        w.SourceInitialized += (_, _) => Apply(w.IsActive);
        w.Activated += (_, _) => Apply(true);
        w.Deactivated += (_, _) => Apply(false);
    }

    public static void SetBorder(Window w, Color c)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int colorRef = c.R | (c.G << 8) | (c.B << 16);
        try { DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int)); } catch { /* not supported */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
