extern alias agent;
using System.Collections.Generic;
using System.Linq;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>App tabs in the helper (docs/app-tabs-design.md §6.2, §6.3): the tab's monitor, what is filled, where windows go, which keys are dropped.</summary>
public class AppTabViewTests
{
    private static readonly A.ScreenRect Primary = new(0, 0, 1024, 768);
    private static readonly A.ScreenRect Tab = new(1024, 0, 2304, 720); // the tab's virtual monitor, 1280×720

    private static A.AppWindow W(int handle, A.ScreenRect bounds, bool opaque = true, bool shell = false) => new(handle, "app", bounds, opaque, shell);

    private static long Area(IEnumerable<A.ScreenRect> rects) => rects.Sum(r => (long)r.Width * r.Height);

    [Fact]
    public void The_tab_s_monitor_is_found_by_its_place_not_by_a_remembered_name()
    {
        A.DesktopMonitor[] monitors = [new(0, @"\\.\DISPLAY1", 0, 0, 1024, 768, true), new(1, @"\\.\DISPLAY12", 1024, 0, 1280, 720, false), new(2, @"\\.\DISPLAY11", 2304, 0, 1280, 720, false)];
        string[] driverOrder = [@"\\.\DISPLAY11", @"\\.\DISPLAY12"];
        Assert.Equal(@"\\.\DISPLAY12", A.AppTabView.Monitor(driverOrder, 1, monitors)!.Name);
        Assert.Equal(@"\\.\DISPLAY11", A.AppTabView.Monitor(driverOrder, 0, monitors)!.Name);
        Assert.Null(A.AppTabView.Monitor(driverOrder, 2, monitors)); // the driver is reloading: no picture rather than another monitor's
        Assert.Null(A.AppTabView.Monitor(driverOrder, -1, monitors));
    }

    [Fact]
    public void Only_the_app_s_visible_windows_are_shown_the_rest_of_the_monitor_is_filled()
    {
        var app = W(1, new(1124, 100, 1724, 500));
        var mask = A.AppTabView.Mask(Tab, [app], w => w.Handle == 1);
        Assert.Equal((long)Tab.Width * Tab.Height - 600 * 400, Area(mask)); // the background around it
        Assert.DoesNotContain(mask, r => !r.Intersect(app.Bounds).IsEmpty);
    }

    [Fact]
    public void Another_program_s_window_over_the_app_is_filled_even_when_see_through()
    {
        var app = W(1, Tab);
        var other = W(2, new(1500, 200, 1900, 600), opaque: false);
        var mask = A.AppTabView.Mask(Tab, [other, app], w => w.Handle == 1);
        Assert.Equal(400L * 400, Area(mask));
        Assert.Contains(mask, r => r.Contains(1700, 400));

        // under the app it is hidden by the app anyway
        Assert.Empty(A.AppTabView.Mask(Tab, [app, other], w => w.Handle == 1));
    }

    [Fact]
    public void The_app_s_own_see_through_menu_doesn_t_hide_its_window_below()
    {
        var menu = W(3, new(1200, 100, 1400, 400), opaque: false);
        Assert.Empty(A.AppTabView.Mask(Tab, [menu, W(1, Tab)], w => w.Handle is 1 or 3));
    }

    [Fact]
    public void The_privacy_screen_kept_out_of_the_picture_neither_covers_nor_is_filled()
    {
        var overlay = new A.AppWindow(7, "", Tab, Opaque: false, OutOfPicture: true);
        Assert.Empty(A.AppTabView.Mask(Tab, [overlay, W(1, Tab)], w => w.Handle == 1));
    }

    [Fact]
    public void Windows_off_the_monitor_don_t_count()
    {
        var mask = A.AppTabView.Mask(Tab, [W(2, Primary), W(1, Tab)], w => w.Handle == 1);
        Assert.Empty(mask);
    }

    [Fact]
    public void The_main_window_is_maximised_on_the_tab_s_monitor_and_other_windows_are_brought_to_it()
    {
        var main = W(1, new(100, 100, 700, 500));         // started on the primary monitor
        var dialog = W(2, new(200, 200, 500, 400));       // the app's dialog, also there
        var foreign = W(9, new(1200, 100, 1600, 400));    // another program that opened on the tab's monitor
        var moves = A.AppTabView.Placement(Tab, Primary, [main, dialog, foreign], w => w.Handle is 1 or 2, main: 1, maximized: _ => false, resizable: _ => true);

        Assert.Equal(new A.AppTabView.Move(1, Tab, true), moves.Single(m => m.Handle == 1));
        var d = moves.Single(m => m.Handle == 2).To;
        Assert.Equal((300, 200), (d.Width, d.Height));                                    // its own size
        Assert.True(A.AppTabView.OnMonitor(d, Tab));
        Assert.Equal(new A.ScreenRect(176, 100, 576, 400), moves.Single(m => m.Handle == 9).To); // the same place on the primary
    }

    [Fact]
    public void A_maximised_main_window_on_its_monitor_is_left_alone_and_a_fixed_size_one_is_centred()
    {
        var main = W(1, Tab);
        Assert.Empty(A.AppTabView.Placement(Tab, Primary, [main], _ => true, 1, _ => true, _ => true));

        var tool = W(1, new(10, 10, 410, 310));
        var moves = A.AppTabView.Placement(Tab, Primary, [tool], _ => true, 1, _ => false, resizable: _ => false);
        Assert.Equal(new A.AppTabView.Move(1, new A.ScreenRect(1464, 210, 1864, 510), false), Assert.Single(moves));
    }

    [Fact]
    public void A_window_larger_than_the_monitor_goes_to_its_top_left_and_shell_surfaces_stay()
    {
        var big = A.AppTabView.CentredIn(new A.ScreenRect(0, 0, 2000, 1000), Tab, Tab);
        Assert.Equal((Tab.Left, Tab.Top), (big.Left, big.Top));
        Assert.Empty(A.AppTabView.Placement(Tab, Primary, [W(5, Tab, shell: true)], _ => false, 0, _ => false, _ => true));
    }

    [Fact]
    public void Shell_keys_are_dropped_with_their_release_and_everything_else_goes_through()
    {
        var f = new A.ShellKeyFilter();
        static A.RemoteKey Down(ushort scan, bool ext = false) => new(scan, ext ? A.RemoteKeyFlags.Extended : A.RemoteKeyFlags.None);
        static A.RemoteKey Up(ushort scan, bool ext = false) => new(scan, (ext ? A.RemoteKeyFlags.Extended : A.RemoteKeyFlags.None) | A.RemoteKeyFlags.Up);

        Assert.False(f.Pass(Down(0x5B, true)));  // Windows key
        Assert.False(f.Pass(Down(0x20)));        // Win+D: the D isn't typed into the app either
        Assert.False(f.Pass(Up(0x20)));
        Assert.False(f.Pass(Up(0x5B, true)));
        Assert.True(f.Pass(Down(0x20)));         // D alone again
        Assert.True(f.Pass(Up(0x20)));

        Assert.True(f.Pass(Down(0x38)));         // Alt
        Assert.False(f.Pass(Down(0x0F)));        // Alt+Tab
        Assert.False(f.Pass(Up(0x0F)));
        Assert.False(f.Pass(Down(0x01)));        // Alt+Esc
        Assert.False(f.Pass(Up(0x01)));
        Assert.True(f.Pass(Up(0x38)));

        Assert.True(f.Pass(Down(0x1D)));         // Ctrl
        Assert.True(f.Pass(Down(0x2A)));         // Shift
        Assert.False(f.Pass(Down(0x01)));        // Ctrl+Shift+Esc
        Assert.False(f.Pass(Up(0x01)));
        Assert.True(f.Pass(Down(0x2E)));         // Ctrl+C goes through
        Assert.True(f.Pass(Up(0x2E)));
        Assert.True(f.Pass(Up(0x2A)));
        Assert.True(f.Pass(Up(0x1D)));

        Assert.True(f.Pass(Down(0x01)));         // Esc alone is the app's
        Assert.True(f.Pass(Up(0x01)));
        Assert.True(f.Pass(Down(0x0F)));         // and Tab
        Assert.True(f.Pass(Down(0x5B)));         // not extended: numpad scan code, not the Windows key
    }
}
