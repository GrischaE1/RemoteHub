using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using DataGrid = Wpf.Ui.Controls.DataGrid;

namespace RemoteHub.Views;

/// <summary>
/// The audit log on this PC: filter by time, kind of event, text and connection; check the hash chain; export to CSV.
/// Opened from Settings → Data, or for one connection from its context menu (then with its connection history).
/// </summary>
public sealed class AuditLogWindow : FluentWindow
{
    private static readonly (string Label, int? Days)[] Periods =
        [("Last 24 hours", 1), ("Last 7 days", 7), ("Last 30 days", 30), ("Last 90 days", 90), ("Last 365 days", 365), ("All entries", null)];

    private readonly ComboBox _period = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _category = new() { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _search = new() { PlaceholderText = "Search names, details, accounts", MinWidth = 240 };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        SelectionMode = DataGridSelectionMode.Extended, GridLinesVisibility = DataGridGridLinesVisibility.None, Margin = new Thickness(0, 12, 0, 0),
    };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _connectionChip = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 10, 0, 0),
                                                       HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Connection? _connection;
    private IReadOnlyList<AuditEntry> _entries = [];
    private int _refresh;

    public AuditLogWindow(Connection? connection = null)
    {
        _connection = connection;
        Title = "Audit log";
        Width = 1100; Height = 720; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var root = new DockPanel();
        var bar = new TitleBar { Title = "Audit log", ShowMinimize = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // Footer: status, verify, export, close.
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var footerDock = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var verify = new Button { Content = "Verify integrity", Icon = new SymbolIcon(SymbolRegular.ShieldCheckmark24), Margin = new Thickness(0, 0, 8, 0),
                                  ToolTip = "Checks that no entry was changed, removed or inserted since it was written (hash chain)." };
        verify.Click += async (_, _) => await Verify(verify);
        var export = new Button { Content = "Export CSV…", Icon = new SymbolIcon(SymbolRegular.ArrowExport24), Margin = new Thickness(0, 0, 8, 0),
                                  ToolTip = "The entries shown (with the current filter)." };
        export.Click += (_, _) => ExportCsv();
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 100 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(verify);
        buttons.Children.Add(export);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        footerDock.Children.Add(buttons);
        footerDock.Children.Add(_status);
        footer.Child = footerDock;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Filters.
        var body = new DockPanel { Margin = new Thickness(24, 4, 24, 12) };
        var filters = new WrapPanel();
        foreach (var (label, _) in Periods) _period.Items.Add(label);
        _period.SelectedIndex = connection is null ? 2 : 3;
        _category.Items.Add("All events");
        foreach (var (name, _) in AuditEvents.Categories) _category.Items.Add(name);
        _category.SelectedIndex = 0;
        filters.Children.Add(_period);
        filters.Children.Add(_category);
        filters.Children.Add(_search);
        var top = new StackPanel();
        top.Children.Add(filters);
        top.Children.Add(_connectionChip);
        top.Children.Add(_summary);
        var retention = new TextBlock
        {
            Text = $"Entries are kept for {AuditLog.RetentionDays} days{(AppPolicy.AuditRetentionDays is null ? "" : " (set by your organization)")}. " +
                   "Stored on this PC in the encrypted database – never passwords, clipboard or file contents.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75,
        };
        top.Children.Add(retention);
        DockPanel.SetDock(top, Dock.Top);
        body.Children.Add(top);

        Column("Time", nameof(AuditEntry.Time), 150);
        Column("Event", nameof(AuditEntry.EventLabel), 170);
        Column("Name", nameof(AuditEntry.Target), 200);
        Column("Details", nameof(AuditEntry.Detail), 0);
        Column("Device", nameof(AuditEntry.DeviceName), 120);
        Column("Account", nameof(AuditEntry.Account), 160);
        body.Children.Add(_grid);
        root.Children.Add(body);
        Content = root;

        _period.SelectionChanged += (_, _) => Refresh();
        _category.SelectionChanged += (_, _) => Refresh();
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Refresh(); };
        UpdateChip();
        Loaded += (_, _) => Refresh();
    }

    private void Column(string header, string path, double width)
    {
        var col = new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path) };
        col.Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star);
        col.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis),
                                                                      new Setter(ToolTipProperty, new System.Windows.Data.Binding(path)) } };
        _grid.Columns.Add(col);
    }

    private void UpdateChip()
    {
        if (_connection is null) { _connectionChip.Visibility = Visibility.Collapsed; _summary.Visibility = Visibility.Collapsed; return; }
        _connectionChip.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = $"Connection: {_connection.Name}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var clear = new Button { Icon = new SymbolIcon(SymbolRegular.Dismiss16), Padding = new Thickness(4), ToolTip = "Show all connections" };
        clear.Click += (_, _) => { _connection = null; UpdateChip(); Refresh(); };
        row.Children.Add(clear);
        _connectionChip.Child = row;
        _connectionChip.Visibility = Visibility.Visible;
    }

    private DateTime? FromUtc => Periods[Math.Max(0, _period.SelectedIndex)].Days is { } d ? DateTime.UtcNow.AddDays(-d) : null;

    private async void Refresh()
    {
        var token = ++_refresh;
        var from = FromUtc;
        var events = _category.SelectedIndex > 0 ? AuditEvents.Categories[_category.SelectedIndex - 1].Events : null;
        var text = _search.Text;
        var connection = _connection;
        _status.Text = "Loading…";
        try
        {
            var (entries, summary) = await Task.Run(() =>
                (AuditLog.Query(from, events, text, connection?.Id.ToString()),
                 connection is null ? null : ConnectionHistory.For(connection.Id, from ?? DateTime.UnixEpoch)));
            if (token != _refresh) return;
            _entries = entries;
            _grid.ItemsSource = entries;
            _status.Text = entries.Count >= 10_000 ? "The newest 10,000 entries are shown – narrow the filter to see older ones." : $"{entries.Count} entries";
            if (summary is not null)
            {
                var period = Periods[Math.Max(0, _period.SelectedIndex)].Label.ToLowerInvariant();
                _summary.Text = summary.Sessions == 0
                    ? "No sessions in this period."
                    : $"{summary.Sessions} session{(summary.Sessions == 1 ? "" : "s")} · {ViewModels.MainViewModel.Duration(summary.Total)} in total" +
                      (summary.LastUtc is { } last ? $" · last {Connection.When(last, DateTime.Now)}" : "") + $" ({period})";
                _summary.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            if (token == _refresh) _status.Text = "The audit log can't be read: " + ex.Message;
        }
    }

    private async Task Verify(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var result = await Task.Run(AuditLog.Verify);
            if (result.Problem is null)
                await Dialogs.Info(this, "Audit log intact", $"All {result.Checked} entries are unchanged and complete since they were written.");
            else
                await Dialogs.Info(this, "Audit log changed", $"{result.Problem}\n\n{result.Checked} entries were checked up to this point.");
        }
        catch (Exception ex) { await Dialogs.Info(this, "Verify integrity", ex.Message); }
        finally { button.IsEnabled = true; }
    }

    private void ExportCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"RemoteHub-audit-{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ToCsv(_entries), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = $"{_entries.Count} entries exported to {dlg.FileName}";
        }
        catch (Exception ex) { _ = Dialogs.Info(this, "Export failed", ex.Message); }
    }

    /// <summary>
    /// CSV with a header row. Values that a spreadsheet would run as a formula (starting with = + - @) get a leading
    /// apostrophe, so an exported name can't execute anything.
    /// </summary>
    internal static string ToCsv(IEnumerable<AuditEntry> entries)
    {
        static string F(string? v)
        {
            v ??= "";
            if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
            return v.IndexOfAny(['"', ',', ';', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }
        var sb = new StringBuilder("Time (UTC),Event,Event code,Name,Details,Device,Account,Connection id\r\n");
        foreach (var e in entries)
            sb.Append(string.Join(',', F(e.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss")), F(e.EventLabel), F(e.Event), F(e.Target), F(e.Detail),
                F(e.DeviceName), F(e.Account), F(e.ConnectionId))).Append("\r\n");
        return sb.ToString();
    }
}
