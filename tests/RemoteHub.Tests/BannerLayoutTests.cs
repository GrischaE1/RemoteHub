extern alias agent;
using Ui = agent::RemoteHub.Agent.Win32HelperUi;

namespace RemoteHub.Tests;

/// <summary>
/// The banner's layout (FluentWindow.BannerSize): open, it shows its whole heading and subtitle – on one line where they
/// fit beside the buttons, otherwise taller with the text wrapped (D-BAN-08 on the lab's 1024 px screen).
/// </summary>
public class BannerLayoutTests
{
    private static Ui.FluentWindow Banner(float scale)
    {
        var window = new Ui.FluentWindow(null!, Ui.Theme.Current(), Ui.FluentKind.Banner)
        {
            Heading = @"Your screen is being viewed and controlled by DESKTOP-OUNPN53\PSAdmin · mouse and keyboard blocked",
            Subtitle = "Click \"Disconnect\" to end",
            Buttons = [new(4, "Hide screen", Ui.ButtonStyle.Secondary), new(5, "Hide apps…", Ui.ButtonStyle.Secondary), new(3, "Disconnect", Ui.ButtonStyle.Danger)],
        };
        window.UseScale(scale);
        return window;
    }

    [SkippableTheory]
    [InlineData(1024, 1f)]
    [InlineData(1280, 1.5f)]
    public void A_heading_too_long_for_the_screen_wraps_instead_of_being_cut(int screen, float scale)
    {
        var window = Banner(scale);
        try
        {
            Skip.If(window.BannerSize(100_000).W < screen, "GDI can't measure text here.");
            var (w, h) = window.BannerSize(screen);
            Assert.True(w <= screen - (int)Math.Round(24 * scale), $"{w} wide on a screen {screen} wide");
            Assert.True(window.Stacked(w));
            Assert.True(h > (int)Math.Round(46 * scale), $"{h} high");
        }
        finally { window.Destroy(); }
    }

    [SkippableFact]
    public void On_a_wide_screen_the_banner_stays_one_line_with_its_subtitle()
    {
        var window = Banner(1f);
        try
        {
            var (w, h) = window.BannerSize(2560);
            Skip.If(w < 600, "GDI can't measure text here.");
            Assert.False(window.Stacked(w));
            Assert.Equal(46, h);
        }
        finally { window.Destroy(); }
    }
}
