using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// "Page deleted · Undo": a bar at the bottom of a view that offers to undo the last delete for eight seconds. Shared by
/// Notes, Scripts and the connection tree (docs/scripts-view-design.md §4.9).
/// </summary>
public sealed class UndoBar : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _undo = new() { Content = "Undo", MinWidth = 0, Padding = new Thickness(12, 4, 12, 4) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Action? _action;

    public UndoBar()
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 8, 8, 8);
        Margin = new Thickness(0, 0, 0, 16);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        BorderThickness = new Thickness(1);
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "SessionHeaderBrush");
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_text);
        row.Children.Add(_undo);
        Child = row;
        _undo.Click += (_, _) => { var a = _action; Hide(); a?.Invoke(); };
        _timer.Tick += (_, _) => Hide();
    }

    /// <summary>What the bar says ("Script deleted").</summary>
    public string Text => _text.Text;

    public void Show(string text, Action undo)
    {
        _action = undo;
        _text.Text = text;
        Visibility = Visibility.Visible;
        _timer.Stop();
        _timer.Start();
    }

    public void Hide()
    {
        _timer.Stop();
        _action = null;
        Visibility = Visibility.Collapsed;
    }
}
