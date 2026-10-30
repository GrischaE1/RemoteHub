using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Agent;
using RemoteHub.Models;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// "Apps on FS01" (docs/app-tabs-design.md §8.2): the apps in the device's Start menu, by its folders, and its Store apps
/// (§6.6), searched; ticked to add them to a connection – or, choosing one to open, picked and kept in the connection's
/// apps if wanted. Opens at once and reads the list from the device meanwhile: the Start menu shows as soon as it is
/// there, the Store apps are added when they arrive. Built like the other pickers (RunScriptWindow's pick page).
/// </summary>
public sealed class AppPickerWindow : FluentWindow
{
    private const int MaxShown = 500;
    private readonly string _device;
    private readonly Func<Action<DesktopAppList>, CancellationToken, Task> _load;
    private readonly Func<Exception, string> _explain;
    private readonly IReadOnlyList<ConnectionApp> _added;
    private readonly bool _single;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ContentControl _body = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search apps", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24), Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _primary;
    private readonly CheckBox _keep = new() { Content = "Keep it in the connection's apps", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly HashSet<DesktopListedApp> _ticked = [];
    private readonly HashSet<string> _closedGroups = [];
    private DesktopAppList? _apps;
    private int _loads;

    /// <param name="device">The connection's name ("FS01").</param>
    /// <param name="load">Reads the device's apps, handing over each part as it arrives (AgentDesktopSession.ListAppsAsync).</param>
    /// <param name="added">The connection's apps now: shown ticked and "Added".</param>
    /// <param name="singleChoice">One app to open (Enter or a double-click), with "Keep it in the connection's apps".</param>
    /// <param name="canKeep">The connection may be changed (policy AllowConnectionEditing): otherwise nothing is kept.</param>
    /// <param name="explain">A failure in words for the user (PsRemoting.Explain); null: its message.</param>
    public AppPickerWindow(string device, Func<Action<DesktopAppList>, CancellationToken, Task> load, IReadOnlyList<ConnectionApp> added, bool singleChoice,
        bool canKeep = true, Func<Exception, string>? explain = null)
    {
        _device = device;
        _load = load;
        _added = added;
        _single = singleChoice;
        _explain = explain ?? (ex => ex.GetBaseException().Message);
        ScriptWindowFrame.Apply(this, $"Apps on {device}", 640, 600);
        _primary = new Button { Content = singleChoice ? "Open" : "Add apps", Appearance = ControlAppearance.Primary, IsDefault = true, IsEnabled = false };
        _primary.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        var another = new Button { Content = "Not in the list? Add another app", Appearance = ControlAppearance.Transparent, VerticalAlignment = VerticalAlignment.Bottom };
        another.Click += (_, _) => { AddAnother = true; Close(); };

        var refresh = RemoteToolChrome.Tool(SymbolRegular.ArrowSync24, "Read the apps again", () => _ = Load());
        refresh.Margin = new Thickness(6, 0, 0, 8);
        refresh.VerticalAlignment = VerticalAlignment.Top;
        var top = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        top.Children.Add(refresh);
        top.Children.Add(_search);
        var head = new StackPanel();
        head.Children.Add(ScriptWindowFrame.Info(singleChoice
            ? $"The apps on {device}: its Start menu and Store apps. Enter or a double-click opens one in a tab of its own."
            : $"The apps on {device}: its Start menu and Store apps. Tick the apps to add."));
        head.Children.Add(top);

        var bottom = new StackPanel();
        if (singleChoice && canKeep) bottom.Children.Add(_keep);
        var actions = new DockPanel();
        var buttons = ScriptWindowFrame.Buttons(_primary, cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        actions.Children.Add(buttons);
        // Only where it can do something: opening an app from the menu (the Apps page has its own "Add another app").
        if (canKeep && singleChoice) actions.Children.Add(another);
        bottom.Children.Add(actions);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_body);
        ScriptWindowFrame.Fill(this, new Border { Padding = new Thickness(20, 4, 20, 20), Child = dock }, resizable: true);

        AutomationProperties.SetName(_list, "Apps");
        _search.TextChanged += (_, _) => FillList();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); e.Handled = true; } };
        _list.SelectionChanged += (_, e) =>
        {
            // A folder's header isn't a choice: it opens and closes the folder, and is never selected.
            if (_list.SelectedItem is ListBoxItem h && _headers.Values.Any(x => x.Item == h))
                _list.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            if (_single) _primary.IsEnabled = Selected() is not null;
        };
        // Space ticks the selected app, as a click on its box would.
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || _single || _list.SelectedItem is not ListBoxItem { Content: CheckBox { IsEnabled: true } tick }) return;
            tick.IsChecked = tick.IsChecked != true;
            e.Handled = true;
        };
        _list.MouseDoubleClick += (_, _) => { if (_single && Selected() is not null) Accept(); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && _single && Selected() is not null) { Accept(); e.Handled = true; } };
        Loaded += (_, _) => { _search.Focus(); _ = Load(); };
        Closed += (_, _) => _cancel.Cancel();
    }

    /// <summary>What was chosen: the ticked apps, or the one to open.</summary>
    public IReadOnlyList<ConnectionApp> Chosen { get; private set; } = [];

    /// <summary>Single choice: keep the opened app in the connection's apps.</summary>
    public bool Keep => _keep.IsChecked == true;

    /// <summary>"Not in the list? Add another app" was chosen: the caller opens the connection's Apps page.</summary>
    public bool AddAnother { get; private set; }

    private async Task Load()
    {
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(new ProgressRing { IsIndeterminate = true, Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 16) });
        p.Children.Add(ScriptWindowFrame.Info($"Reading the Start menu of {_device}…"));
        _body.Content = p;
        _primary.IsEnabled = false;
        _apps = null;
        var run = ++_loads; // parts of a list read before "Read the apps again" are dropped
        try
        {
            await _load(part => Dispatcher.InvokeAsync(() => { if (run == _loads) AddPart(part); }), _cancel.Token);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { /* closed */ }
        catch (Exception ex) when (run == _loads && _apps is not null)
        {
            // The Start menu is shown; only the Store apps didn't come: said under the list.
            _apps = _apps with { More = false, Note = ex is InvalidOperationException ? ex.Message : _explain(ex) };
            FillList();
        }
        catch (Exception ex) when (run == _loads)
        {
            // The device's own words when it refused; otherwise the connection's failure explained (as for the other tools).
            var failed = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
            failed.Children.Add(ScriptWindowFrame.Info(ex is InvalidOperationException ? ex.Message : _explain(ex)));
            var again = new Button { Content = "Try again", Icon = new SymbolIcon(SymbolRegular.ArrowSync24), HorizontalAlignment = HorizontalAlignment.Center };
            again.Click += (_, _) => _ = Load();
            failed.Children.Add(again);
            _body.Content = failed;
        }
    }

    private bool IsAdded(DesktopListedApp a) => _added.Any(x => x.SameApp(ConnectionApp.From(a)));

    // The list's items, made once per list from the device: searching and closing a folder only show and hide them –
    // rebuilding items while one of them animates (a tick just set) breaks WPF-UI's templates.
    private readonly List<(ListBoxItem Item, DesktopListedApp App)> _rows = [];
    private readonly Dictionary<string, (ListBoxItem Item, SymbolIcon Chevron, int Count)> _headers = [];
    private readonly ListBoxItem _note = new() { IsEnabled = false };
    private readonly ListBoxItem _later = new() { IsEnabled = false }; // Store apps still being read, or why there are none

    /// <summary>A part of the list from the device: the first one starts the list, later ones (the Store apps) are added below.</summary>
    private void AddPart(DesktopAppList part)
    {
        if (_apps is null)
        {
            _apps = part;
            _body.Content = _list;
            BuildList();
            return;
        }
        _apps = new DesktopAppList([.. _apps.Apps, .. part.Apps], _apps.Truncated || part.Truncated, part.More, part.Note ?? _apps.Note);
        AddRows(part.Apps);
        FillList();
    }

    /// <summary>The items for a list just read: the Start menu's own apps first, then its folders by name.</summary>
    private void BuildList()
    {
        _list.Items.Clear();
        _rows.Clear();
        _headers.Clear();
        if (_apps is not { } all) return;
        _list.Items.Add(_note);
        _list.Items.Add(_later);
        AddRows(all.Apps);
        FillList();
    }

    /// <summary>Rows for <paramref name="apps"/>, above the notes: the apps without a folder first, then the folders by name.</summary>
    private void AddRows(IEnumerable<DesktopListedApp> apps)
    {
        var at = _list.Items.IndexOf(_note);
        foreach (var group in apps.GroupBy(a => a.Group).OrderBy(g => g.Key.Length > 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            if (group.Key.Length > 0 && !_headers.ContainsKey(group.Key)) _list.Items.Insert(at++, GroupHeader(group.Key, group.Count()));
            foreach (var a in group)
            {
                var item = Row(a);
                _rows.Add((item, a));
                _list.Items.Insert(at++, item);
            }
        }
    }

    /// <summary>Shows what the search finds (at most <see cref="MaxShown"/>), the open folders' apps, and a note when nothing or not everything is shown.</summary>
    private void FillList()
    {
        if (_apps is not { } all) return;
        var text = _search.Text.Trim();
        bool Matches(DesktopListedApp a) => text.Length == 0 || a.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                            || a.Group.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                                            || a.Program.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        var found = 0;
        var foundIn = new HashSet<string>();
        foreach (var (item, a) in _rows)
        {
            var match = Matches(a) && found < MaxShown;
            if (match) { found++; foundIn.Add(a.Group); }
            item.Visibility = match && (text.Length > 0 || !_closedGroups.Contains(a.Group)) ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var (name, (item, chevron, count)) in _headers)
        {
            var closed = _closedGroups.Contains(name) && text.Length == 0;
            item.Visibility = foundIn.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
            chevron.Symbol = closed ? SymbolRegular.ChevronRight24 : SymbolRegular.ChevronDown24;
            AutomationProperties.SetName(item, $"{name}, {count} app{(count == 1 ? "" : "s")}, {(closed ? "closed" : "open")}");
        }
        var cut = all.Truncated || found == MaxShown;
        var waiting = all.Apps.Length == 0 && all.More; // nothing in the Start menu, the Store apps still to come
        _note.Content = ScriptWindowFrame.Info(all.Apps.Length == 0 ? $"No apps found on {_device}."
            : found == 0 ? "No app matches."
            : $"Only the first {(all.Truncated ? all.Apps.Length : MaxShown):N0} apps are shown – search to narrow them down.");
        _note.Visibility = (found == 0 && !waiting) || cut ? Visibility.Visible : Visibility.Collapsed;
        _later.Content = ScriptWindowFrame.Info(all.More ? $"Reading the Store apps on {_device}…" : all.Note ?? "");
        _later.Visibility = all.More || all.Note is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdatePrimary();
    }

    /// <summary>A Start menu folder, open or closed with a click, Space or Enter (while searching, everything found is shown).</summary>
    private ListBoxItem GroupHeader(string name, int count)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        var chevron = new SymbolIcon(SymbolRegular.ChevronDown24) { FontSize = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(chevron);
        row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        var item = new ListBoxItem { Content = row, Cursor = Cursors.Hand };
        _headers[name] = (item, chevron, count);
        void Toggle()
        {
            if (!_closedGroups.Remove(name)) _closedGroups.Add(name);
            FillList();
        }
        item.PreviewMouseLeftButtonUp += (_, e) => { Toggle(); e.Handled = true; };
        item.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Toggle(); e.Handled = true; } };
        return item;
    }

    private ListBoxItem Row(DesktopListedApp a)
    {
        var added = IsAdded(a);
        var note = added ? "Added" : a.Elevated ? "Runs as administrator" : "";
        var row = AppRows.Row(this, a.Name, a.Program, a.Arguments, a.Icon, note);
        var item = new ListBoxItem { Tag = a };
        if (_single) item.Content = row;
        else
        {
            var tick = new CheckBox { Content = row, IsChecked = added || _ticked.Contains(a), IsEnabled = !added, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            tick.Checked += (_, _) => { _ticked.Add(a); UpdatePrimary(); };
            tick.Unchecked += (_, _) => { _ticked.Remove(a); UpdatePrimary(); };
            item.Content = tick;
        }
        AutomationProperties.SetName(item, a.Name);
        return item;
    }

    private DesktopListedApp? Selected() => (_list.SelectedItem as ListBoxItem)?.Tag as DesktopListedApp;

    private void UpdatePrimary()
    {
        if (_single) _primary.IsEnabled = Selected() is not null;
        else
        {
            _primary.IsEnabled = _ticked.Count > 0;
            _primary.Content = _ticked.Count switch { 0 => "Add apps", 1 => "Add 1 app", var n => $"Add {n} apps" };
        }
    }

    private void Accept()
    {
        Chosen = _single
            ? Selected() is { } one ? [ConnectionApp.From(one)] : []
            : _apps?.Apps.Where(_ticked.Contains).Select(ConnectionApp.From).ToList() ?? [];
        if (Chosen.Count == 0) return;
        // Shown as a dialog (always, in RemoteHub): its result; otherwise (a test) it just closes with what was chosen.
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = true;
        else Close();
    }
}
