using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>The device's resolution in an agent desktop session: the sizes RemoteHub asks for and the agent accepts.</summary>
public class DesktopResolutionTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(3841, 2161, 3840, 2160)]     // odd: made even
    [InlineData(99999, 99999, 7680, 4320)]   // larger than the virtual display takes: the largest
    [InlineData(int.MaxValue, 900, 7680, 900)]
    [InlineData(320, 200, 320, 200)]         // the smallest
    [InlineData(319, 900, 0, 0)]             // too small: the device's own
    [InlineData(1600, 199, 0, 0)]
    [InlineData(-1600, 900, 0, 0)]
    [InlineData(1600, int.MinValue, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    public void Requested_sizes_from_the_wire_are_limited_to_what_the_agent_applies(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), DesktopProtocol.ValidFitSize(w, h));

    [Theory]
    [InlineData("device", DesktopResolutionKind.Device, 0, 0)]
    [InlineData("FIT", DesktopResolutionKind.Fit, 0, 0)]
    [InlineData("2560x1440", DesktopResolutionKind.Fixed, 2560, 1440)]
    [InlineData(" 2560 × 1600 ", DesktopResolutionKind.Fixed, 2560, 1600)]
    [InlineData("3840*2160", DesktopResolutionKind.Fixed, 3840, 2160)]
    [InlineData("1921 X 1081", DesktopResolutionKind.Fixed, 1920, 1080)]
    [InlineData("7680x4320", DesktopResolutionKind.Fixed, 7680, 4320)]
    public void Resolutions_are_read_from_settings_and_typed_sizes(string text, DesktopResolutionKind kind, int w, int h) =>
        Assert.Equal(new DesktopResolution(kind, w, h), DesktopResolution.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("big")]
    [InlineData("2560")]
    [InlineData("2560x1440x2")]
    [InlineData("-2560x1440")]
    [InlineData("2560.5x1440")]
    [InlineData("7682x4320")]    // larger than the agent takes: refused, not quietly made smaller
    [InlineData("300x200")]      // smaller than it takes
    [InlineData("99999999999x1440")]
    public void Sizes_the_agent_would_not_apply_are_refused(string? text) => Assert.Null(DesktopResolution.Parse(text));

    [Fact]
    public void Every_choice_is_stored_and_read_back_the_same()
    {
        foreach (var r in DesktopResolution.Presets.Append(DesktopResolution.Device).Append(DesktopResolution.Fit).Append(new(DesktopResolutionKind.Fixed, 2560, 1600)))
            Assert.Equal(r, DesktopResolution.Parse(r.Id));
    }

    [Fact]
    public void Settings_from_before_the_menu_keep_the_fit_to_window_choice()
    {
        Assert.Equal(DesktopResolution.Fit, DesktopResolution.FromSettings(null, fitToWindow: true));
        Assert.Equal(DesktopResolution.Device, DesktopResolution.FromSettings(null, fitToWindow: false));
        Assert.Equal(DesktopResolution.Device, DesktopResolution.FromSettings("garbage", fitToWindow: false));
        Assert.Equal(new DesktopResolution(DesktopResolutionKind.Fixed, 2560, 1440), DesktopResolution.FromSettings("2560x1440", fitToWindow: false));
        Assert.Equal(DesktopResolution.Fit, DesktopResolution.FromSettings(new UiState().AgentDesktopResolution, new UiState().AgentDesktopFitToWindow));
    }

    [Fact]
    public void The_settings_message_carries_the_chosen_size()
    {
        var s = new AgentDesktopSession(new Connection { Name = "pc", Host = "pc.contoso.local" })
        {
            Layout = new DesktopLayout([new DesktopMonitor(0, @"\\.\DISPLAY1", 0, 0, 1280, 720, true)], 0, 1280, 720, false, false),
        };
        s.WindowSizeChanged(1201, 701);

        s.Resolution = DesktopResolution.Fit;
        Assert.Equal(new DesktopSettings(0, 30, 60, 1200, 700), s.CurrentSettings());

        // Larger than the tab: the size as chosen, whatever the window is.
        s.Resolution = DesktopResolution.Presets[^1];
        Assert.Equal((3840, 2160), (s.CurrentSettings().FitWidth, s.CurrentSettings().FitHeight));
        s.WindowSizeChanged(800, 600);
        Assert.Equal((3840, 2160), (s.CurrentSettings().FitWidth, s.CurrentSettings().FitHeight));

        s.Resolution = DesktopResolution.Device;
        Assert.Equal((0, 0), (s.CurrentSettings().FitWidth, s.CurrentSettings().FitHeight));

        // View-only and the picture of all monitors never ask for a resolution (the agent refuses it too).
        s.Resolution = DesktopResolution.Presets[0];
        s.ViewOnly = true;
        Assert.Equal((0, 0), (s.CurrentSettings().FitWidth, s.CurrentSettings().FitHeight));
        s.ViewOnly = false;
        s.Layout = s.Layout! with { Monitor = DesktopProtocol.AllMonitors };
        Assert.Equal((0, 0), (s.CurrentSettings().FitWidth, s.CurrentSettings().FitHeight));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(150, 150)]
    [InlineData(112, 100)]   // between two steps: the nearer
    [InlineData(113, 125)]
    [InlineData(168, 175)]   // 168 dpi
    [InlineData(500, 500)]
    [InlineData(99, 0)]      // outside Windows' steps: the monitor's own
    [InlineData(501, 0)]
    [InlineData(0, 0)]
    [InlineData(-150, 0)]
    [InlineData(int.MaxValue, 0)]
    public void Requested_scales_are_one_of_the_steps_Windows_offers(int percent, int expected) =>
        Assert.Equal(expected, DesktopProtocol.ValidScale(percent));

    [Fact]
    public void Only_an_app_tab_asks_for_this_pcs_display_scale()
    {
        var layout = new DesktopLayout([new DesktopMonitor(0, @"\\.\DISPLAY1", 0, 0, 1280, 720, true)], 0, 1280, 720, false, false);
        var app = new AgentDesktopSession(new Connection { Name = "FS01", Host = "fs01.contoso.local" }, new ConnectionApp("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc"))
        {
            Layout = layout,
        };
        app.WindowSizeChanged(1800, 1000, 150);
        Assert.Equal(new DesktopSettings(0, 30, 60, 1800, 1000, 150), app.CurrentSettings());
        app.WindowSizeChanged(1800, 1000, 120); // moved to a monitor at 120 dpi: the nearest step
        Assert.Equal(125, app.CurrentSettings().Scale);
        app.WindowSizeChanged(1800, 1000, 50);
        Assert.Equal(0, app.CurrentSettings().Scale);

        // A desktop keeps the device's scale, whatever this PC runs at
        var desktop = new AgentDesktopSession(new Connection { Name = "FS01", Host = "fs01.contoso.local" }) { Layout = layout };
        desktop.WindowSizeChanged(1800, 1000, 150);
        Assert.Equal(new DesktopSettings(0, 30, 60, 1800, 1000), desktop.CurrentSettings());
    }

    [Fact]
    public void The_chosen_size_survives_the_wire_format()
    {
        var sent = new DesktopSettings(0, 30, 60, 3840, 2160, 150);
        var m = DesktopWire.Json(DesktopMessage.Settings, sent, DesktopJson.Default.DesktopSettings);
        Assert.Equal(DesktopMessage.Settings, DesktopWire.TypeOf(m));
        Assert.Equal(sent, DesktopWire.ReadJson(m, DesktopJson.Default.DesktopSettings));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]     // the area exactly
    [InlineData(1398, 900, 1399, 900)]       // the area is odd: the fitted size one pixel smaller
    [InlineData(1398, 898, 1399.5, 899.5)]   // fractional areas (125 % scaling)
    [InlineData(1920, 1080, 1919.6, 1080)]   // the area's size rounded down
    public void A_fitted_picture_a_pixel_or_two_smaller_than_the_tab_is_shown_unscaled(int pw, int ph, double aw, double ah) =>
        Assert.True(Views.PictureFit.ShowsNative(pw, ph, aw, ah));

    [Theory]
    [InlineData(1920, 1080, 1400, 900)]      // larger than the tab: scaled down
    [InlineData(1280, 720, 1920, 1080)]      // the device's own, much smaller: scaled up as before
    [InlineData(1920, 1080, 1919, 1080)]     // a whole pixel too wide
    [InlineData(0, 0, 1920, 1080)]           // no picture yet
    public void Other_pictures_are_scaled_to_the_tab(int pw, int ph, double aw, double ah) =>
        Assert.False(Views.PictureFit.ShowsNative(pw, ph, aw, ah));
}
