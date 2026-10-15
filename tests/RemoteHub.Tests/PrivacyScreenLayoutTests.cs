extern alias agent;
using Overlay = agent::RemoteHub.Agent.Win32HelperUi;

namespace RemoteHub.Tests;

/// <summary>
/// The privacy screen's layout (FluentWindow.Layout) at real sizes and scales: the words stay on the screen in every
/// fallback – full size, a shrunk logo, compact type, no logo – and the footnote shows only where its row fits.
/// </summary>
public class PrivacyScreenLayoutTests
{
    private const string Blocked = "Mouse and keyboard are blocked.";

    private static (Overlay.FluentWindow Window, Overlay.FluentWindow.OverlayLayout Layout) Lay(int w, int h, float scale, bool blocked)
    {
        var window = new Overlay.FluentWindow(null!, Overlay.Theme.Current(), Overlay.FluentKind.Overlay)
        {
            Subtitle = "PRIVACY MODE ACTIVE",
            Heading = "Remote Support Session in Progress",
            Body = "Your screen is temporarily hidden while an authorized administrator assists your device.",
            Notice = blocked ? Blocked : "",
            Footnote = "Secure, monitored session  •  Please do not power off your device",
        };
        window.UseScale(scale);
        var layout = window.Layout(w, h);
        Skip.If(layout.Heading.Bottom == layout.Heading.Top, "GDI can't measure text here.");
        return (window, layout);
    }

    private static void AssertOnScreen(Overlay.FluentWindow.OverlayLayout l, int w, int h, bool blocked)
    {
        var rows = new[] { l.Logo, l.Eyebrow, l.Heading, l.Body, l.Notice, l.Footnote };
        foreach (var (top, bottom) in rows) Assert.True(top >= 0 && bottom <= h && top <= bottom, $"{top}..{bottom} on a screen {h} high");
        for (var i = 1; i < rows.Length; i++) Assert.True(rows[i].Top >= rows[i - 1].Bottom, $"row {i} starts at {rows[i].Top}, above {rows[i - 1].Bottom}");
        Assert.True(l.Body.Bottom > l.Body.Top, "the body is shown");
        Assert.Equal(blocked, l.Notice.Bottom > l.Notice.Top);
        Assert.True(l.EyebrowWidth <= w, $"eyebrow {l.EyebrowWidth} wide on a screen {w} wide");
        Assert.True(l.Wide <= w && l.Inner <= l.Wide);
        if (l.ShowsFootnote) Assert.True(l.FootnoteWidth <= w && l.Divider >= l.Notice.Bottom && l.Divider < l.Footnote.Top);
    }

    [SkippableTheory]
    [InlineData(1920, 1080, 1f, false)]
    [InlineData(1920, 1080, 1f, true)]
    [InlineData(2560, 1440, 1.5f, true)]
    public void A_normal_screen_shows_everything_at_full_size(int w, int h, float scale, bool blocked)
    {
        var (window, l) = Lay(w, h, scale, blocked);
        try
        {
            AssertOnScreen(l, w, h, blocked);
            Assert.False(l.Compact);
            Assert.True(l.ShowsFootnote);
            Assert.Equal((int)Math.Round(220 * scale), l.Logo.Bottom - l.Logo.Top);
        }
        finally { window.Destroy(); }
    }

    [SkippableTheory]
    [InlineData(1366, 768, 1.5f, true)]
    [InlineData(1280, 720, 1.5f, false)]
    public void A_short_screen_shrinks_the_logo_first(int w, int h, float scale, bool blocked)
    {
        var (window, l) = Lay(w, h, scale, blocked);
        try
        {
            AssertOnScreen(l, w, h, blocked);
            Assert.False(l.Compact);
            var logo = l.Logo.Bottom - l.Logo.Top;
            Assert.True(logo > 0 && logo < (int)Math.Round(220 * scale), $"logo {logo}");
        }
        finally { window.Destroy(); }
    }

    [SkippableTheory]
    [InlineData(800, 450, 2f, true)]
    [InlineData(800, 450, 2f, false)]
    [InlineData(1024, 600, 2.5f, true)]
    public void A_very_short_screen_turns_compact_and_keeps_the_words(int w, int h, float scale, bool blocked)
    {
        var (window, l) = Lay(w, h, scale, blocked);
        try
        {
            AssertOnScreen(l, w, h, blocked);
            Assert.True(l.Compact);
        }
        finally { window.Destroy(); }
    }

    [SkippableTheory]
    [InlineData(800, 330, 2f, true)]
    [InlineData(1024, 400, 2.5f, false)]
    public void Where_even_compact_type_does_not_fit_the_logo_goes(int w, int h, float scale, bool blocked)
    {
        var (window, l) = Lay(w, h, scale, blocked);
        try
        {
            AssertOnScreen(l, w, h, blocked);
            Assert.True(l.Compact);
            Assert.Equal(l.Logo.Top, l.Logo.Bottom);
            Assert.False(l.ShowsFootnote);
        }
        finally { window.Destroy(); }
    }

    [SkippableTheory]
    [InlineData(600, 1200, 2.5f)]
    [InlineData(480, 900, 1.5f)]
    public void A_narrow_screen_leaves_out_a_footnote_that_would_not_fit_on_one_line(int w, int h, float scale)
    {
        var (window, l) = Lay(w, h, scale, blocked: true);
        try
        {
            AssertOnScreen(l, w, h, blocked: true);
            Assert.True(l.FootnoteWidth > w - (int)Math.Round(48 * scale)); // wider than the room between the paddings
            Assert.False(l.ShowsFootnote);
            Assert.True(l.Logo.Bottom - l.Logo.Top <= l.Wide / 2, "the logo is at most half the text width");
        }
        finally { window.Destroy(); }
    }
}
