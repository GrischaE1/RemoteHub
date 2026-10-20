using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using ListView = Wpf.Ui.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// Tab content for a list of remote items (processes, services): filter box, sortable columns, a status line and an
/// automatic refresh while the tab is visible. Rows are updated in place so selection, sorting and scroll position stay.
/// </summary>
public abstract class RemoteListView<TRow> : Border, IRecordableView, IFilterableView where TRow : class
{
    public FrameworkElement RecordingArea { get; }

    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24) };
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal };
    private readonly Button _pause;
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(4, 6, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly System.Windows.Controls.ProgressBar _busy = new() { IsIndeterminate = true, Height = 2, Visibility = Visibility.Hidden };
    private readonly Wpf.Ui.Controls.GridView _grid = new();
    private readonly Dictionary<GridViewColumn, (string Header, string Sort)> _columns = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Func<int> _intervalSeconds;
    private DateTime _lastLoad = DateTime.MinValue;
    private DateTime? _soon;
    private DateTime _errorUntil;
    private bool _loading, _again, _paused;
    private int _busyCount;
    private string _summary = "";

    protected readonly RemoteToolSession Session;
    protected readonly ObservableCollection<TRow> Rows = [];
    protected readonly ListView List = new() { BorderThickness = new Thickness(0) };
    protected readonly ListCollectionView View;

    protected RemoteListView(RemoteToolSession session, SymbolRegular icon, string kind, Func<int> intervalSeconds, Action reconnect, Action close)
    {
        Session = session;
        _intervalSeconds = intervalSeconds;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;
        root.Children.Add(RemoteToolChrome.Header(session, icon, kind, _headerStripe));

        var cell = new Grid();
        Grid.SetRow(cell, 1);
        root.Children.Add(cell);
        RecordingArea = cell;
        var body = new DockPanel { Margin = new Thickness(10, 10, 10, 8) };
        cell.Children.Add(body);
        cell.Children.Add(RemoteToolChrome.Overlay(reconnect, close));

        // ---- toolbar: item actions | filter | refresh + auto refresh
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var refresh = RemoteToolChrome.Tool(SymbolRegular.ArrowClockwise24, "Refresh now (F5)", () => _ = Refresh(manual: true));
        _pause = RemoteToolChrome.Tool(SymbolRegular.Pause24, "", () => { _paused = !_paused; UpdatePause(); if (!_paused) Tick(); });
        foreach (var b in new[] { _pause, refresh }) { DockPanel.SetDock(b, Dock.Right); b.Margin = new Thickness(4, 0, 0, 0); bar.Children.Add(b); }
        DockPanel.SetDock(_tools, Dock.Left);
        bar.Children.Add(_tools);
        _filter.Margin = new Thickness(0);
        _filter.MaxWidth = 420;
        _filter.HorizontalAlignment = HorizontalAlignment.Right;
        _filter.Width = 320;
        // Filtering selects the first match, so Enter opens it and the item actions apply to it.
        _filter.TextChanged += (_, _) =>
        {
            View!.Refresh();
            if (_filter.Text.Trim().Length > 0 && View.Cast<TRow>().FirstOrDefault() is { } first) { List.SelectedItems.Clear(); List.SelectedItem = first; List.ScrollIntoView(first); }
            UpdateStatus();
        };
        bar.Children.Add(_filter);
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        DockPanel.SetDock(_busy, Dock.Top);
        body.Children.Add(_busy);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        ResetStatusColour();

        // ---- list
        View = new ListCollectionView(Rows) { IsLiveSorting = true, Filter = o => o is TRow r && (_filter.Text.Trim() is not { Length: > 0 } f || Matches(r, f)) };
        List.ItemsSource = View;
        List.View = _grid;
        VirtualizingPanel.SetIsVirtualizing(List, true);
        VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
        List.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is GridViewColumnHeader { Column: { } col } && _columns.ContainsKey(col)) SortBy(col, toggle: true);
        }));
        List.MouseDoubleClick += (_, e) => { if (RowAt(e.OriginalSource) is { } r) Activate(r); };
        List.PreviewMouseRightButtonDown += (_, e) =>
        {
            var r = RowAt(e.OriginalSource);
            if (r is not null && !List.SelectedItems.Contains(r)) { List.SelectedItems.Clear(); List.SelectedItem = r; }
        };
        List.ContextMenuOpening += (_, e) =>
        {
            if (Menu(Selected) is { } m) List.ContextMenu = m;
            else e.Handled = true;
        };
        List.ContextMenu = new ContextMenu();
        List.SelectionChanged += (_, _) => SelectionChanged();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _ = Refresh(manual: true); e.Handled = true; }
            else if (e.Key == Key.Enter && Selected is [var one]) { Activate(one); e.Handled = true; }
            else OnKey(e);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _filter.Focus(); _filter.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.F5) { _ = Refresh(manual: true); e.Handled = true; }
        };
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _filter.Text = ""; List.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down) { List.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && Selected is [var match]) { Activate(match); e.Handled = true; }
        };
        body.Children.Add(List);

        session.Ready += () => { _lastLoad = DateTime.MinValue; _ = Refresh(manual: false); };
        session.FocusRequested += () => Dispatcher.BeginInvoke(() => List.Focus(), DispatcherPriority.Input);
        session.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(RemoteToolSession.AccentBrush)) _headerStripe.Background = Session.AccentBrush; };
        _headerStripe.Background = session.AccentBrush;
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => { if (IsVisible) Tick(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        UpdatePause();
    }

    // ================================================================== for the concrete views

    /// <summary>Loads the list from the remote computer and merges it into <see cref="Rows"/>. Returns the status line text.</summary>
    protected abstract Task<string> Load();
    protected abstract bool Matches(TRow row, string filter);
    protected abstract ContextMenu? Menu(List<TRow> selected);
    protected virtual void Activate(TRow row) { }
    protected virtual void OnKey(KeyEventArgs e) { }
    protected virtual void SelectionChanged() { }

    protected List<TRow> Selected => List.SelectedItems.Cast<TRow>().ToList();

    public void FocusFilter() { _filter.Focus(); _filter.SelectAll(); }
    protected Window? Owner => Window.GetWindow(this);
    protected string Where => Session.Connection.Name;

    private static readonly IValueConverter CellWidth = new FuncConverter(w => w is double d ? Math.Max(0, d - 14) : 0);

    private sealed class FuncConverter(Func<object?, object> f) : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) => f(value);
        public object ConvertBack(object? value, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    protected void AddTool(Button b)
    {
        b.Margin = new Thickness(0, 0, 4, 0);
        _tools.Children.Add(b);
    }

    /// <summary>Adds a column. <paramref name="sort"/> is the row property used for sorting (a raw value, not the display text).</summary>
    protected GridViewColumn AddColumn(string header, double width, string display, string sort, bool right = false, DataTemplate? template = null)
    {
        var col = new Wpf.Ui.Controls.GridViewColumn { Header = header, Width = width };
        if (template is not null) col.CellTemplate = template;
        else
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(display));
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            // Cells are only as wide as their text: numbers get the column width so they line up on the right.
            if (right)
            {
                text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
                text.SetBinding(WidthProperty, new Binding(nameof(GridViewColumn.ActualWidth)) { Source = col, Converter = CellWidth });
            }
            col.CellTemplate = new DataTemplate { VisualTree = text };
        }
        _grid.Columns.Add(col);
        _columns[col] = (header, sort);
        return col;
    }

    /// <summary>Sorts by a column; clicking the same header again reverses the order.</summary>
    protected void SortBy(GridViewColumn col, bool toggle, ListSortDirection? direction = null)
    {
        var (_, sort) = _columns[col];
        var current = View.SortDescriptions.FirstOrDefault();
        var dir = direction ?? (toggle && current.PropertyName == sort
            ? current.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending
            : ListSortDirection.Ascending);
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(new SortDescription(sort, dir));
            View.LiveSortingProperties.Clear();
            View.LiveSortingProperties.Add(sort);
        }
        foreach (var (c, (header, _)) in _columns)
            c.Header = c == col ? header + (dir == ListSortDirection.Ascending ? "  ▲" : "  ▼") : header;
    }

    /// <summary>Asks for one more refresh shortly (e.g. the first CPU figures need two samples).</summary>
    protected void RefreshSoon(TimeSpan delay) => _soon = DateTime.UtcNow + delay;

    /// <summary>Applies a fresh list: updates rows that are still there, adds new ones, removes rows that are gone.</summary>
    protected void Merge<TKey, TItem>(IEnumerable<TItem> items, Func<TItem, TKey> itemKey, Func<TRow, TKey> rowKey, Func<TItem, TRow> create, Action<TRow, TItem> update)
        where TKey : notnull
    {
        var fresh = new Dictionary<TKey, TItem>();
        foreach (var i in items) fresh.TryAdd(itemKey(i), i);
        for (var n = Rows.Count - 1; n >= 0; n--)
        {
            var k = rowKey(Rows[n]);
            if (fresh.Remove(k, out var item)) update(Rows[n], item);
            else Rows.RemoveAt(n);
        }
        foreach (var item in fresh.Values) Rows.Add(create(item));
    }

    public async Task Refresh(bool manual)
    {
        if (!Session.IsConnected) return;
        // A refresh is running: load once more right after it, so a change just made shows up.
        if (_loading) { _again = true; return; }
        _loading = true;
        _again = false;
        if (manual) { Busy(true); _errorUntil = default; }
        try
        {
            _summary = await Load();
            _lastLoad = DateTime.UtcNow;
            UpdateStatus();
            SelectionChanged();
        }
        catch (Exception ex)
        {
            _lastLoad = DateTime.UtcNow;
            if (Session.IsConnected) SetError(ex.Message);
        }
        finally
        {
            _loading = false;
            if (manual) Busy(false);
        }
        if (_again) await Refresh(manual: false);
    }

    private void Tick()
    {
        if (_paused || _loading || !IsVisible || !Session.IsConnected) return;
        var now = DateTime.UtcNow;
        var every = _intervalSeconds();
        if (_soon is { } s && now >= s) { _soon = null; _ = Refresh(manual: false); return; }
        if (_lastLoad == DateTime.MinValue || (every > 0 && now - _lastLoad >= TimeSpan.FromSeconds(every))) _ = Refresh(manual: false);
        UpdatePause();
    }

    private void UpdatePause()
    {
        var every = _intervalSeconds();
        _pause.IsEnabled = every > 0;
        _pause.Icon = new SymbolIcon(_paused || every <= 0 ? SymbolRegular.Play24 : SymbolRegular.Pause24);
        var tip = every <= 0 ? "Automatic refresh is off (Menu → Global defaults → Remote tools)"
                : _paused ? $"Automatic refresh is paused – click to refresh every {Every(every)} again"
                : $"Refreshes every {Every(every)} while this tab is visible – click to pause (Menu → Global defaults to change)";
        if (!Equals(_pause.ToolTip, tip)) { _pause.ToolTip = tip; System.Windows.Automation.AutomationProperties.SetName(_pause, tip); }
    }

    private static string Every(int seconds) => RemoteToolChrome.Every(seconds);

    private void UpdateStatus()
    {
        // An error stays readable for a while, even when the list refreshes meanwhile.
        if (_lastLoad == DateTime.MinValue || DateTime.UtcNow < _errorUntil) return;
        ResetStatusColour();
        var shown = View.Count;
        var filtered = _filter.Text.Trim().Length > 0 ? $"{shown} of {Rows.Count} shown  ·  " : "";
        _status.Text = $"{filtered}{_summary}  ·  updated {_lastLoad.ToLocalTime():T}" + (_paused ? " (paused)" : "");
    }

    protected void Busy(bool on)
    {
        _busyCount = Math.Max(0, _busyCount + (on ? 1 : -1));
        _busy.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void ResetStatusColour() => _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

    protected void SetError(string text)
    {
        _status.Text = text;
        _status.Foreground = Brushes.IndianRed;
        _errorUntil = DateTime.UtcNow.AddSeconds(15);
    }

    /// <summary>Runs one change on the remote computer, then refreshes the list. Errors go to the status line.</summary>
    protected async Task<bool> Do(string what, Func<Task> op)
    {
        if (!Session.IsConnected) return false;
        Busy(true);
        try
        {
            await op();
            Services.DiagLog.Write($"{Session.KindLabel}: {what}");
            return true;
        }
        catch (Exception ex) { SetError($"{Capitalise(what)} failed: {ex.Message}"); return false; }
        finally
        {
            Busy(false);
            await Refresh(manual: false);
        }
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    protected static System.Windows.Controls.MenuItem Item(string header, SymbolRegular icon, Action action, bool enabled = true, string? gesture = null) =>
        RemoteToolChrome.Item(header, icon, action, enabled, gesture);

    protected static void CopyText(string text)
    {
        if (text.Length > 0) try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private static TRow? RowAt(object source) => MainWindow.FindAncestor<ListViewItem>(source)?.DataContext as TRow;

    /// <summary>Cell with a small symbol or dot before the text.</summary>
    protected static DataTemplate IconTextTemplate(string text, string? symbol = null, string? dotBrush = null)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        if (symbol is not null)
        {
            var icon = new FrameworkElementFactory(typeof(SymbolIcon));
            icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(symbol));
            icon.SetValue(SymbolIcon.FontSizeProperty, 15.0);
            icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
            panel.AppendChild(icon);
        }
        if (dotBrush is not null)
        {
            var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            dot.SetValue(WidthProperty, 8.0);
            dot.SetValue(HeightProperty, 8.0);
            dot.SetValue(MarginProperty, new Thickness(2, 0, 9, 0));
            dot.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(dotBrush));
            panel.AppendChild(dot);
        }
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new Binding(text));
        t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(t);
        return new DataTemplate { VisualTree = panel };
    }
}
