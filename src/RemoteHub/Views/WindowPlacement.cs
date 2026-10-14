using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RemoteHub.Services;

namespace RemoteHub.Views;

/// <summary>
/// Where popups and new windows open: centred on the window they belong to, as it is on screen. WPF and WPF-UI's message
/// box centre on the owner's <see cref="Window.Left"/>/<see cref="Window.Top"/>, which for a maximised window is its restored
/// position – so a dialog over a maximised RemoteHub could open off to the side. <see cref="Install"/> corrects every window
/// that is centred on its owner once it has its size.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Windows centred on a window that isn't their owner (<see cref="CenterOn"/>).</summary>
    private static readonly ConditionalWeakTable<Window, Window> Over = new();

    /// <summary>Once at start: every window that opens centred on its owner is placed over the owner as it is on screen.</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Window window) return;
            var over = Over.TryGetValue(window, out var o) ? o
                : window.WindowStartupLocation == WindowStartupLocation.CenterOwner ? window.Owner : null;
            if (over is null) return;
            // After the window's own placement (WPF-UI's message box centres itself when loaded).
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Center(window, over));
        }));

    /// <summary>
    /// Opens <paramref name="window"/> centred on <paramref name="over"/> without making it the owner (a detached tab must not
    /// stay above the main window or minimise with it; the lock window shows while the main window is hidden).
    /// </summary>
    public static void CenterOn(Window window, Window over)
    {
        if (window == over || Rect(over, over) is not { } r) return; // not shown yet: the window's own placement applies
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        // A first guess before it is shown, so it doesn't appear elsewhere first; corrected once it has its size.
        var w = double.IsNaN(window.Width) ? Math.Max(window.MinWidth, 400) : window.Width;
        var h = double.IsNaN(window.Height) ? Math.Max(window.MinHeight, 200) : window.Height;
        window.Left = r.Left + (r.Width - w) / 2;
        window.Top = r.Top + (r.Height - h) / 2;
        Over.AddOrUpdate(window, over);
    }

    private static void Center(Window window, Window over)
    {
        if (!window.IsVisible || window.WindowState != WindowState.Normal || Rect(over, window) is not { } r) return;
        window.Left = r.Left + (r.Width - window.ActualWidth) / 2;
        window.Top = Math.Max(r.Top, r.Top + (r.Height - window.ActualHeight) / 2);
    }

    /// <summary>Where <paramref name="over"/> is on screen, in <paramref name="units"/>' units (device pixels → its DPI).</summary>
    private static Rect? Rect(Window over, Window units)
    {
        var hwnd = new WindowInteropHelper(over).Handle;
        if (hwnd == IntPtr.Zero || !Gdi.GetWindowRect(hwnd, out var r)) return null;
        if ((PresentationSource.FromVisual(units) ?? PresentationSource.FromVisual(over))?.CompositionTarget is not { } target) return null;
        var m = target.TransformFromDevice;
        return new Rect(m.Transform(new Point(r.Left, r.Top)), m.Transform(new Point(r.Right, r.Bottom)));
    }
}
