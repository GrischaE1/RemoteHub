using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// Progress of an agent install or update: one row per step (spinner while it runs, a check mark when it's done, a cross
/// if it failed), a progress bar, and the result below. Steps are the reported lines ending in "…"; any other line is
/// a result or a note. Report may be called from any thread.
/// </summary>
public sealed class AgentInstallProgressWindow : FluentWindow
{
    private readonly StackPanel _steps = new();
    private readonly System.Windows.Controls.ProgressBar _bar = new() { Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 14) };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 100, IsCancel = true };
    private readonly int _expected;
    private (Grid Row, ContentControl Icon)? _running;
    private int _done;

    /// <param name="expectedSteps">How many steps the chosen way usually has (for the progress bar).</param>
    public AgentInstallProgressWindow(string title, string device, int expectedSteps)
    {
        _expected = Math.Max(1, expectedSteps);
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(22, 4, 22, 20) };
        body.Children.Add(new TextBlock { Text = device, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(_bar);
        body.Children.Add(_steps);
        body.Children.Add(_result);
        _close.Click += (_, _) => Close();
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Children = { _close } });

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        _bar.IsIndeterminate = true;
    }

    /// <summary>A step ("Copying …") or a result/note line.</summary>
    public void Report(string line) => Dispatcher.BeginInvoke(() => Add(line));

    /// <summary>The work ended: the running step gets its check mark (or cross), the bar shows the outcome.</summary>
    public void Finish(bool ok) => Dispatcher.BeginInvoke(() =>
    {
        CompleteRunning(ok);
        _bar.IsIndeterminate = false;
        _bar.Value = ok ? 1 : _bar.Value;
        _bar.Foreground = ok ? Brushes.MediumSeaGreen : Brushes.IndianRed;
        if (!ok) _result.Foreground = Brushes.IndianRed;
        _close.Content = ok ? "Done" : "Close";
        _close.Appearance = ControlAppearance.Primary;
    });

    private void Add(string line)
    {
        var text = line.Trim();
        if (text.EndsWith('…'))
        {
            CompleteRunning(true);
            var icon = new ContentControl { Width = 22, Height = 22, Content = new ProgressRing { IsIndeterminate = true, Width = 18, Height = 18 } };
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(icon);
            var label = new TextBlock { Text = text.TrimEnd('…'), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            _steps.Children.Add(row);
            _running = (row, icon);
            return;
        }
        // A failure ends the running step with a cross; anything else is a result or a note below the steps.
        var failed = text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase);
        if (failed) CompleteRunning(false);
        _result.Text += (_result.Text.Length > 0 ? "\n\n" : "") + text;
        if (failed) _result.Foreground = Brushes.IndianRed;
    }

    private void CompleteRunning(bool ok)
    {
        if (_running is not { } r) return;
        r.Icon.Content = new SymbolIcon(ok ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.DismissCircle24)
        {
            FontSize = 20, Filled = true, Foreground = ok ? Brushes.MediumSeaGreen : Brushes.IndianRed,
        };
        _running = null;
        if (!ok) return;
        _done++;
        _bar.IsIndeterminate = false;
        _bar.Value = Math.Min(0.95, _done / (double)Math.Max(_expected, _done + 1));
    }
}
