using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>Where submenus open (Views/SubmenuPlacement.cs): beside their item, level with it also when they open upwards.</summary>
public class SubmenuPlacementTests
{
    // WPF-UI 4.3's submenu for a plain MenuItem: 20 px up, a shadow margin of 12,10,12,30 (Apply reads both from the template).
    private static readonly Thickness Margin = new(12, 10, 12, 30);
    private static readonly Point Offset = new(0, -20);

    [Fact]
    public void A_submenu_opens_right_and_down_first_and_mirrors_its_shadow_margin_when_it_flips()
    {
        var places = SubmenuPlacement.Places(new Size(200, 240), new Size(130, 35), Margin, Offset, 1);
        Assert.All(places, p => Assert.Equal(PopupPrimaryAxis.Vertical, p.PrimaryAxis)); // slid up or down beside the item where none fits
        // WPF adds the offset: down, the visible top is 10 above the item's top (-20 + 10).
        Assert.Equal(new Point(130, 0), places[0].Point);
        // Up: the visible bottom 10 below the item's bottom: 35 - 240 + 40 - 20 + 240 - 30 = 45 = 35 + 10.
        Assert.Equal(new Point(130, 35 - 240 + 60), places[1].Point);
        Assert.Equal(35 + 10, places[1].Point.Y + Offset.Y + 240 - Margin.Bottom);
        // Left: the visible right edge 12 left of the item, as the visible left edge is 12 right of it on the right.
        Assert.Equal(new Point(-200, 0), places[2].Point);
        Assert.Equal(-12, places[2].Point.X + 200 - Margin.Right);
        Assert.Equal(new Point(-200, 35 - 240 + 60), places[3].Point);
    }

    [Fact]
    public void The_template_margin_and_offset_follow_the_callbacks_units()
    {
        // At 150 % the callback's sizes are 1.5 times the element's.
        var places = SubmenuPlacement.Places(new Size(300, 360), new Size(195, 52.5), Margin, Offset, 1.5);
        Assert.Equal(52.5 - 360 + 60 * 1.5, places[1].Point.Y, 6);
    }

    [Fact]
    public void Only_submenus_get_the_placement_not_a_menu_bars_items()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var submenu = Item("Right");
                SubmenuPlacement.Apply(submenu);
                var popup = (Popup)submenu.Template.FindName("Popup", submenu);
                Assert.Equal(PlacementMode.Custom, popup.Placement);
                Assert.NotNull(popup.CustomPopupPlacementCallback);
                // Again (the menu opens a second time): nothing changes.
                var callback = popup.CustomPopupPlacementCallback;
                SubmenuPlacement.Apply(submenu);
                Assert.Same(callback, popup.CustomPopupPlacementCallback);

                var bar = Item("Bottom");
                SubmenuPlacement.Apply(bar);
                Assert.Equal(PlacementMode.Bottom, ((Popup)bar.Template.FindName("Popup", bar)).Placement);
                SubmenuPlacement.Apply(new MenuItem()); // no template part: left alone
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "the test thread did not end");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>A menu item with a template shaped like WPF-UI's: a popup named Popup around a border named SubmenuBorder.</summary>
    private static MenuItem Item(string placement)
    {
        var template = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='MenuItem'><Grid>" +
            $"<Popup Name='Popup' Placement='{placement}' VerticalOffset='-20'><Border Name='SubmenuBorder' Margin='12,10,12,30' /></Popup>" +
            "</Grid></ControlTemplate>");
        var item = new MenuItem { Template = template };
        item.ApplyTemplate();
        return item;
    }
}
