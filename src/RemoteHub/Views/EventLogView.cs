using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>One event in the list.</summary>
public sealed class EventRow(EventEntry e)
{
    public EventEntry Entry { get; } = e;
    public long RecordId => Entry.RecordId;
    public DateTime TimeUtc => Entry.TimeUtc;
    public string TimeText => Entry.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public EventLevel Level => Entry.Level;
    public string LevelText => RemoteEventLog.LevelName(Entry.Level);
    public Brush LevelBrush => Entry.Level switch
    {
        EventLevel.Critical or EventLevel.Error => SessionStateToBrush.RedBrush,
        EventLevel.Warning => SessionStateToBrush.AmberBrush,
        _ => SessionStateToBrush.GreyBrush,
    };
    public string Source => Entry.Source;
    public int EventId => Entry.EventId;
    public string Message => Entry.Message;
    public string FirstLine => Entry.Message.Split('\n', 2)[0].Trim();
}

/// <summary>
/// Event Viewer tab: one event log of the device at a time, filtered on the device by level, time and source, newest first
/// and 500 at a time; the selected event's message below the list. Read-only; refreshed by hand.
/// </summary>
public sealed class EventLogView : RemoteListView<EventRow>
{
    private readonly EventLogSession _session;
    private readonly ComboBox _log = new() { MinWidth = 240 };
    private readonly ComboBox _level = new() { MinWidth = 170 };
    private readonly ComboBox _time = new() { MinWidth = 150 };
    private readonly TextBox _source = new() { PlaceholderText = "Source (exact name)", ClearButtonEnabled = true, Width = 220 };
    private readonly TextBox _message = new()
    {
        IsReadOnly = true, ClearButtonEnabled = false, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
    };
    private readonly Wpf.Ui.Controls.Button _more, _export;
    private bool _logsLoaded, _hasMore, _loadingMore;
    private EventQuery? _query;

    private static readonly (string Label, EventLevel[] Levels)[] LevelChoices =
    [
        ("All levels", []),
        ("Critical", [EventLevel.Critical]),
        ("Errors and critical", [EventLevel.Critical, EventLevel.Error]),
        ("Warnings and errors", [EventLevel.Critical, EventLevel.Error, EventLevel.Warning]),
        ("Information", [EventLevel.Information]),
        ("Verbose", [EventLevel.Verbose]),
    ];

    private static readonly (string Label, TimeSpan? Span)[] TimeChoices =
    [
        ("Last hour", TimeSpan.FromHours(1)),
        ("Last 24 hours", TimeSpan.FromDays(1)),
        ("Last 7 days", TimeSpan.FromDays(7)),
        ("Last 30 days", TimeSpan.FromDays(30)),
        ("Any time", null),
    ];

    public EventLogView(EventLogSession session, Action reconnect, Action close)
        : base(session, SymbolRegular.DocumentBulletList24, "Event Viewer", null, reconnect, close)
    {
        _session = session;
        List.SelectionMode = SelectionMode.Single;

        // ---- filters: which events are read from the device
        foreach (var (label, _) in LevelChoices) _level.Items.Add(new ComboBoxItem { Content = label });
        foreach (var (label, _) in TimeChoices) _time.Items.Add(new ComboBoxItem { Content = label });
        _level.SelectedIndex = 0;
        _time.SelectedIndex = 1;
        System.Windows.Automation.AutomationProperties.SetName(_log, "Log");
        System.Windows.Automation.AutomationProperties.SetName(_level, "Level");
        System.Windows.Automation.AutomationProperties.SetName(_time, "Time");
        System.Windows.Automation.AutomationProperties.SetName(_source, "Source");
        _log.SelectionChanged += (_, _) => FiltersChanged();
        _level.SelectionChanged += (_, _) => FiltersChanged();
        _time.SelectionChanged += (_, _) => FiltersChanged();
        _source.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { FiltersChanged(); e.Handled = true; } };
        _source.TextChanged += (_, _) => { if (_source.Text.Length == 0) FiltersChanged(); };
        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (FrameworkElement f in new FrameworkElement[] { _log, _level, _time, _source })
        {
            f.Margin = new Thickness(0, 0, 4, 0);
            filters.Children.Add(f);
        }
        FilterRow(filters);

        // ---- toolbar
        _more = RemoteToolChrome.Tool(SymbolRegular.ArrowDown24, "Read the next 500 older events", () => _ = LoadMore());
        _export = RemoteToolChrome.Tool(SymbolRegular.ArrowExportLtr24, "Save the events this filter shows as an .evtx file on this PC", () => _ = Export());
        _more.Content = "Load more";
        _export.Content = "Export…";
        foreach (var b in new[] { _more, _export }) AddTool(b);

        AddColumn("Level", 130, "", nameof(EventRow.Level), template: IconTextTemplate(nameof(EventRow.LevelText), dotBrush: nameof(EventRow.LevelBrush)));
        var time = AddColumn("Date and time", 150, nameof(EventRow.TimeText), nameof(EventRow.TimeUtc));
        AddColumn("Source", 220, nameof(EventRow.Source), nameof(EventRow.Source));
        AddColumn("Event ID", 80, nameof(EventRow.EventId), nameof(EventRow.EventId), right: true);
        AddColumn("Message", 640, nameof(EventRow.FirstLine), nameof(EventRow.FirstLine));
        SortBy(time, toggle: false, System.ComponentModel.ListSortDirection.Descending);

        // ---- the selected event's message
        System.Windows.Automation.AutomationProperties.SetName(_message, "Event message");
        DetailPane(_message);
        SelectionChanged();
    }

    private EventRow? One => Selected is [var r] ? r : null;

    private void FiltersChanged()
    {
        if (_logsLoaded) _ = Refresh(manual: true);
    }

    private EventQuery Query()
    {
        var log = (_log.SelectedItem as ComboBoxItem)?.Tag as string ?? "Application";
        var levels = LevelChoices[Math.Max(0, _level.SelectedIndex)].Levels;
        var span = TimeChoices[Math.Max(0, _time.SelectedIndex)].Span;
        return new EventQuery(log, levels, span is { } s ? DateTime.UtcNow - s : null, _source.Text.Trim());
    }

    protected override async Task<string> Load()
    {
        if (!_logsLoaded) await LoadLogs();
        var q = _query = Query();
        var keep = One?.RecordId;
        var page = await _session.EventsAsync(q, before: 0);
        Rows.Clear();
        foreach (var e in page.Entries) Rows.Add(new EventRow(e));
        _hasMore = page.More;
        if (keep is { } id && Rows.FirstOrDefault(r => r.RecordId == id) is { } again) List.SelectedItem = again;
        return Summary();
    }

    private async Task LoadLogs()
    {
        var logs = await _session.LogsAsync();
        var current = (_log.SelectedItem as ComboBoxItem)?.Tag as string ?? "Application";
        _log.Items.Clear();
        foreach (var l in logs) _log.Items.Add(new ComboBoxItem { Content = $"{l.Name}  ({l.Count:N0})", Tag = l.Name });
        if (_log.Items.Count == 0) _log.Items.Add(new ComboBoxItem { Content = current, Tag = current });
        _log.SelectedItem = _log.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, current, StringComparison.OrdinalIgnoreCase))
                            ?? _log.Items[0];
        _logsLoaded = true;
    }

    private string Summary()
    {
        var q = _query;
        var range = TimeChoices[Math.Max(0, _time.SelectedIndex)].Label.ToLowerInvariant();
        return $"{Rows.Count} events" + (q is null ? "" : $"  ·  {q.Log}  ·  {range}") + (_hasMore ? "  ·  more available" : "");
    }

    private async Task LoadMore()
    {
        if (!Session.IsConnected || !_hasMore || _loadingMore || _query is not { } q || Rows.Count == 0) return;
        _loadingMore = true;
        SelectionChanged();
        Busy(true);
        try
        {
            var page = await _session.EventsAsync(q, before: Rows.Min(r => r.RecordId));
            foreach (var e in page.Entries) Rows.Add(new EventRow(e));
            _hasMore = page.More;
            SetSummary(Summary());
        }
        catch (Exception ex) { SetError("Load more failed: " + ex.Message); }
        finally
        {
            Busy(false);
            _loadingMore = false;
            SelectionChanged();
        }
    }

    protected override bool Matches(EventRow r, string f) =>
        r.Message.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Source.Contains(f, StringComparison.OrdinalIgnoreCase) ||
        r.LevelText.Equals(f, StringComparison.OrdinalIgnoreCase) || r.EventId.ToString(CultureInfo.InvariantCulture) == f;

    protected override void SelectionChanged()
    {
        _message.Text = One?.Message ?? "";
        _more.IsEnabled = Session.IsConnected && _hasMore && !_loadingMore;
        _export.IsEnabled = Session.IsConnected && _logsLoaded && !_loadingMore;
    }

    protected override void Activate(EventRow row) => ShowDetails(row);

    protected override ContextMenu? Menu(List<EventRow> sel)
    {
        if (sel is not [var r]) return null;
        var m = new ContextMenu();
        m.Items.Add(Item("Copy message", SymbolRegular.Copy24, () => CopyText(r.Message), r.Message.Length > 0));
        m.Items.Add(Item("Copy event ID", SymbolRegular.Copy24, () => CopyText(r.EventId.ToString(CultureInfo.InvariantCulture))));
        m.Items.Add(Item("Details…", SymbolRegular.Info24, () => ShowDetails(r), true, "Enter"));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(manual: true), Session.IsConnected, "F5"));
        return m;
    }

    private void ShowDetails(EventRow r)
    {
        if (Owner is not { } owner) return;
        var text = $"Log:  {_query?.Log}\nLevel:  {r.LevelText}\nDate and time:  {r.TimeText}\nSource:  {r.Source}\nEvent ID:  {r.EventId}\n" +
                   $"Record:  {r.RecordId}\n\n{r.Message}";
        _ = Dialogs.Info(owner, $"{r.Source} – event {r.EventId}", text);
    }

    private async Task Export()
    {
        if (!Session.IsConnected || _session.Target is not { } target) return;
        // What the list shows: the filters it was read with, not a source still being typed.
        var q = _query ?? Query();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export event log",
            FileName = $"{_session.Connection.Name} - {q.Log}.evtx".Replace(':', '_').Replace('/', '_').Replace('\\', '_'),
            Filter = "Event log files (*.evtx)|*.evtx",
            DefaultExt = ".evtx",
        };
        if (dlg.ShowDialog(Owner) != true) return;
        Busy(true);
        string? remote = null;
        try
        {
            var (path, size) = await _session.ExportAsync(q);
            remote = path;
            // The file comes over the same way a download does: streamed, checked and renamed once complete.
            var item = TransferItem.Download(path, dlg.FileName, Path.GetFileName(dlg.FileName), _session.Connection.Name, size);
            await FileDownloader.DownloadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target), overwrite: true, [item],
                a => Dispatcher.BeginInvoke(a));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (item.State != TransferState.Done) throw new IOException(item.StatusText.Replace("Failed – ", ""));
            var range = TimeChoices[Math.Max(0, _time.SelectedIndex)].Label.ToLowerInvariant();
            Services.DiagLog.Write($"{Session.KindLabel}: export log");
            Data.AuditLog.Write(Data.AuditEvents.EventLogExported, _session.Connection, $"{q.Log}, {range}, {TransferItem.Size(size)}");
            SetInfo($"Exported {q.Log} to {dlg.FileName} ({TransferItem.Size(size)})");
        }
        catch (Exception ex) { SetError("Export failed: " + ex.Message); }
        finally
        {
            Busy(false);
            if (remote is not null) try { await _session.DeleteExportAsync(remote); } catch { /* left in the account's temp folder */ }
        }
    }
}
