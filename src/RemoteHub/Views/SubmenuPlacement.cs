using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RemoteHub.Views;

/// <summary>
/// Where a submenu opens. WPF-UI places it to the right of its item, 20 px up, inside a transparent shadow margin that
/// is larger at the bottom (12,10,12,30 in 4.3). With no room below, WPF moves it up so that its bottom meets the item's –
/// but the offset and the bottom margin still count, so the submenu ended up some 50 px above its item (New → Connection
/// near the bottom of the screen). <see cref="Install"/> places every submenu itself, with the margin and offset its
/// template has: below as before, or ending level with its item's bottom, and to the left where there is no room on the
/// right.
/// </summary>
public static class SubmenuPlacement
{
    /// <summary>Once at start, for every menu of the app.</summary>
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(MenuItem), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply(sender)));
        // A template that changes later (an item that gets its entries when the menu opens) gets a new popup.
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((_, e) => Apply(e.OriginalSource)));
    }

    internal static void Apply(object? source)
    {
        if (source is not MenuItem item || item.Template?.FindName("Popup", item) is not Popup popup) return;
        // A menu bar's items open below them; only submenus open beside their item.
        if (popup.Placement != PlacementMode.Right) return;
        var margin = item.Template.FindName("SubmenuBorder", item) is FrameworkElement border ? border.Margin : new Thickness();
        var offset = new Point(popup.HorizontalOffset, popup.VerticalOffset);
        popup.CustomPopupPlacementCallback = (size, target, _) =>
            Places(size, target, margin, offset, popup.Child is FrameworkElement { ActualWidth: > 0 } child ? size.Width / child.ActualWidth : 1);
        popup.Placement = PlacementMode.Custom;
    }

    /// <summary>
    /// The places WPF tries in order, relative to the item: right and down (WPF-UI's own place), right and up, left and
    /// down, left and up. WPF adds the popup's offset to each of them. Sizes come in the callback's units;
    /// <paramref name="scale"/> converts the template's margin and offset to them.
    /// </summary>
    internal static CustomPopupPlacement[] Places(Size popup, Size target, Thickness margin, Point offset, double scale)
    {
        var right = target.Width;
        // Mirrored, the menu's visible edge is as far beside the item as on the right.
        var left = -popup.Width + (margin.Right - margin.Left - 2 * offset.X) * scale;
        const double down = 0;
        // The visible bottom as far below the item's bottom as the visible top is above its top when it opens downwards.
        var up = target.Height - popup.Height + (margin.Bottom - margin.Top - 2 * offset.Y) * scale;
        // Where none fits whole (a submenu taller than the room above and below), WPF slides it up or down beside its item,
        // as it does for its own Right placement.
        return
        [
            new(new Point(right, down), PopupPrimaryAxis.Vertical),
            new(new Point(right, up), PopupPrimaryAxis.Vertical),
            new(new Point(left, down), PopupPrimaryAxis.Vertical),
            new(new Point(left, up), PopupPrimaryAxis.Vertical),
        ];
    }
}
