namespace RemoteHub.Agent;

/// <summary>
/// What an app tab shows and lets through, worked out from the windows as they are (docs/app-tabs-design.md §6.2, §6.3) –
/// separate from the helper so it can be tested without a desktop. The helper asks it after every capture and before
/// every input event.
/// </summary>
internal static class AppTabView
{
    /// <summary>
    /// The monitor of the tab's place: the <paramref name="place"/>-th of the driver's monitors in the order Windows lists
    /// them (<paramref name="virtualInOrder"/>, its device names), which is the order the driver made them in. Found by
    /// place, never by a remembered name: every change of the driver's count names them all anew (§5.1). Null when the
    /// place isn't there (the driver is reloading).
    /// </summary>
    public static DesktopMonitor? Monitor(IReadOnlyList<string> virtualInOrder, int place, IReadOnlyList<DesktopMonitor> monitors) =>
        place >= 0 && place < virtualInOrder.Count
            ? monitors.FirstOrDefault(m => m.Name.Equals(virtualInOrder[place], StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// What to fill on the tab's monitor (screen pixels): everything that isn't visibly a window of the tab's app – the
    /// desktop background, another program's window, and the part of an app window that another window covers.
    /// <paramref name="topmostFirst"/> is the z-order. Another program's window counts as covering even when it is
    /// see-through, so nothing of it shows; the app's own see-through windows (menus, shadows) don't cover what is under
    /// them.
    /// </summary>
    public static List<ScreenRect> Mask(ScreenRect monitor, IReadOnlyList<AppWindow> topmostFirst, Func<AppWindow, bool> owned)
    {
        var covered = new List<ScreenRect>();
        var shown = new List<ScreenRect>();
        foreach (var w in topmostFirst)
        {
            var r = w.Bounds.Intersect(monitor);
            if (r.IsEmpty) continue;
            if (owned(w))
            {
                shown.AddRange(ScreenRect.Subtract(r, covered));
                if (w.Opaque) covered.Add(r);
            }
            else covered.Add(r);
        }
        return ScreenRect.Subtract(monitor, shown);
    }

    /// <summary>A window the helper moves: where to, and whether it is maximised there.</summary>
    public sealed record Move(IntPtr Handle, ScreenRect To, bool Maximize);

    /// <summary>
    /// Where windows go (§6.2): the app's main window (its first window: <paramref name="main"/>) maximised on the tab's
    /// monitor – a window that can't be resized is centred on it instead; the app's other windows whose centre is off the
    /// monitor moved onto it at their size, centred on the main window and kept inside the monitor; another program's
    /// window whose centre is on the monitor moved to the same place on <paramref name="primary"/>.
    /// </summary>
    /// <param name="maximized">Whether a window is maximised now.</param>
    /// <param name="resizable">Whether a window can be resized (WS_THICKFRAME).</param>
    public static List<Move> Placement(ScreenRect monitor, ScreenRect primary, IReadOnlyList<AppWindow> windows, Func<AppWindow, bool> owned,
        IntPtr main, Func<IntPtr, bool> maximized, Func<IntPtr, bool> resizable)
    {
        var moves = new List<Move>();
        var mainWindow = windows.FirstOrDefault(w => w.Handle == main && owned(w));
        var anchor = mainWindow is not null && OnMonitor(mainWindow.Bounds, monitor) ? mainWindow.Bounds : monitor;
        foreach (var w in windows)
        {
            if (owned(w))
            {
                if (w.Handle == main)
                {
                    if (resizable(w.Handle))
                    {
                        if (!maximized(w.Handle) || !OnMonitor(w.Bounds, monitor)) moves.Add(new Move(w.Handle, monitor, true));
                    }
                    else if (!OnMonitor(w.Bounds, monitor)) moves.Add(new Move(w.Handle, CentredIn(w.Bounds, monitor, monitor), false));
                }
                else if (!OnMonitor(w.Bounds, monitor)) moves.Add(new Move(w.Handle, CentredIn(w.Bounds, anchor, monitor), false));
            }
            else if (OnMonitor(w.Bounds, monitor) && !w.Shell)
                moves.Add(new Move(w.Handle, w.Bounds.Offset(primary.Left - monitor.Left, primary.Top - monitor.Top), false));
        }
        return moves;
    }

    /// <summary>The window's centre is on the monitor.</summary>
    internal static bool OnMonitor(ScreenRect window, ScreenRect monitor) =>
        monitor.Contains(window.Left + window.Width / 2, window.Top + window.Height / 2);

    /// <summary><paramref name="window"/>'s size, centred on <paramref name="anchor"/>, moved to lie within <paramref name="monitor"/> (its top left when larger).</summary>
    internal static ScreenRect CentredIn(ScreenRect window, ScreenRect anchor, ScreenRect monitor)
    {
        int w = window.Width, h = window.Height;
        var x = anchor.Left + (anchor.Width - w) / 2;
        var y = anchor.Top + (anchor.Height - h) / 2;
        x = Math.Max(monitor.Left, Math.Min(x, monitor.Right - w));
        y = Math.Max(monitor.Top, Math.Min(y, monitor.Bottom - h));
        return new ScreenRect(x, y, x + w, y + h);
    }
}

/// <summary>
/// Drops the shell's keys in an app tab (§6.3): the Windows keys alone and with anything, Ctrl+Esc, Alt+Tab, Alt+Esc and
/// Ctrl+Shift+Esc. They would open things on the primary monitor, which the tab doesn't show. A dropped key's release is
/// dropped too, so the device never sees half of it. One per connection; input comes in order on one thread.
/// </summary>
internal sealed class ShellKeyFilter
{
    // Scan codes (set 1); the Windows keys and the right-hand Ctrl and Alt are extended.
    private const ushort Esc = 0x01, Tab = 0x0F, Ctrl = 0x1D, Alt = 0x38, LeftWin = 0x5B, RightWin = 0x5C;

    // Ctrl+Shift+Esc needs no Shift of its own: it is dropped as Ctrl+Esc.
    private bool _ctrl, _alt, _win;
    private readonly HashSet<(ushort, bool)> _dropped = [];

    /// <summary>Whether <paramref name="key"/> may go to the device.</summary>
    public bool Pass(RemoteKey key)
    {
        var up = (key.Flags & RemoteKeyFlags.Up) != 0;
        var extended = (key.Flags & RemoteKeyFlags.Extended) != 0;
        var id = (key.ScanCode, extended);
        switch (key.ScanCode)
        {
            case Ctrl: _ctrl = !up; return true;
            case Alt: _alt = !up; return true;
        }
        var win = extended && key.ScanCode is LeftWin or RightWin;
        if (win) _win = !up;
        if (up) return !_dropped.Remove(id);
        // While a Windows key is held, the key it goes with is dropped too (Win+D would otherwise type a "d").
        var drop = win || _win
            || key.ScanCode == Esc && (_ctrl || _alt)
            || key.ScanCode == Tab && _alt;
        if (drop) _dropped.Add(id);
        return !drop;
    }
}
