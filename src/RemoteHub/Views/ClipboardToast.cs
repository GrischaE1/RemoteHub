using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Services;
using Button = Wpf.Ui.Controls.Button;

namespace RemoteHub.Views;

/// <summary>
/// Small always-on-top notice shown at the bottom centre of the app window (a separate window, so it is visible
/// above the RDP session). It never takes focus, so the password field in the Windows Security dialog stays focused.
/// </summary>
public sealed class ClipboardToast : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _countdown = new() { Margin = new Thickness(0, 2, 0, 0), FontSize = 12 };
    private readonly (int X, int Y, int W, int H)? _anchor;
    private readonly bool _clipboardMode;
    private int _remaining;

    /// <summary>"Password copied" notice with countdown and "Clear now". <paramref name="appWindow"/>: window to centre in.</summary>
    public static ClipboardToast Copied(string connectionName, int seconds, IntPtr dialog, IntPtr appWindow) =>
        new($"Stored password for \"{connectionName}\" copied", "Press Ctrl+V in the password field, then OK.",
            seconds, ("Clear now", () => SecureClipboard.ClearIfOurs()), dialog, WindowBounds(appWindow), clipboardMode: true);

    /// <summary>Screen rectangle of a visible, not minimized window; null otherwise.</summary>
    private static (int, int, int, int)? WindowBounds(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd) && GetWindowRect(hwnd, out var r) ? (r.L, r.T, r.R - r.L, r.B - r.T) : null;

    private ClipboardToast(string titleText, string hintText, int seconds, (string Text, Action Click)? button,
                           IntPtr dialog, (int X, int Y, int W, int H)? anchor, bool clipboardMode)
    {
        _anchor = anchor;
        _remaining = seconds;
        _clipboardMode = clipboardMode;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var title = new TextBlock { Text = titleText, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { Text = hintText, Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(hint);
        if (clipboardMode) text.Children.Add(_countdown);
        var row = new DockPanel();
        if (button is { } b)
        {
            var btn = new Button { Content = b.Text, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            btn.Click += (_, _) => { b.Click(); Close(); };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
        }
        row.Children.Add(text);

        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 12, 12), BorderThickness = new Thickness(1), Child = row, MaxWidth = 560 };
        card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BrandAccentBrush");
        card.SetResourceReference(TextElement_Foreground, "TextFillColorPrimaryBrush");
        Content = card;

        UpdateText();
        _tick.Tick += (_, _) =>
        {
            _remaining--;
            UpdateText();
            if (_remaining <= 0 || (_clipboardMode && !SecureClipboard.HoldsSecret) || !CredentialPromptWatcher.IsStillOpen(dialog)) Close();
        };
        Loaded += (_, _) => { Place(); _tick.Start(); };
        Closed += (_, _) => _tick.Stop();
    }

    private static readonly DependencyProperty TextElement_Foreground = System.Windows.Documents.TextElement.ForegroundProperty;

    private void UpdateText() => _countdown.Text = $"The clipboard is cleared in {_remaining} s (and right after a successful login).";

    /// <summary>Bottom centre of the app window (or of the screen when the app is minimized).</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var me)) return;
        int w = me.R - me.L, h = me.B - me.T;
        int x, y;
        if (_anchor is { } a)
        {
            x = a.X + (a.W - w) / 2;
            y = a.Y + a.H - h - (int)(28 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        }
        else
        {
            var wa = SystemParameters.WorkArea; // fallback: bottom centre of the primary screen
            var dpi = VisualTreeHelper.GetDpi(this);
            x = (int)((wa.Width * dpi.DpiScaleX - w) / 2);
            y = (int)(wa.Height * dpi.DpiScaleY - h - 24);
        }
        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, 0x0001 | 0x0010 /* SWP_NOSIZE | SWP_NOACTIVATE */);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
