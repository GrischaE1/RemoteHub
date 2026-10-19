using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using ListView = Wpf.Ui.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace RemoteHub.Views;

/// <summary>One key in the registry tree. Sub keys are loaded when the key is expanded or selected.</summary>
public sealed partial class RegNode : ObservableObject
{
    public RegNode(string name, string path, bool hasChildren)
    {
        Name = name;
        Path = path;
        SetHasChildren(hasChildren);
    }

    private RegNode() { Name = ""; Path = ""; IsPlaceholder = true; }

    public string Name { get; }
    public string Path { get; }
    public bool IsPlaceholder { get; }
    public bool IsHive => !Path.Contains('\\') && !IsPlaceholder;
    public bool Loaded { get; set; }
    public ObservableCollection<RegNode> Children { get; } = [];
    public string Symbol => IsHive ? "Database24" : IsExpanded ? "FolderOpen24" : "Folder24";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Symbol));

    /// <summary>Unloaded keys with sub keys get a placeholder child so the tree shows an expander.</summary>
    public void SetHasChildren(bool has)
    {
        if (Loaded) return;
        if (has && Children.Count == 0) Children.Add(new RegNode());
        else if (!has && Children is [{ IsPlaceholder: true }]) Children.Clear();
    }
}

/// <summary>
/// Tab content of a registry session: key tree on the left, values of the selected key on the right, address bar on top.
/// Only the key that is opened gets read. Every change is a single explicit action; deletes ask first.
/// </summary>
public sealed class RegistryView : Border, IRecordableView, IFilterableView
{
    public FrameworkElement RecordingArea { get; }

    private const string DefaultStart = @"HKEY_LOCAL_MACHINE\SOFTWARE";
    private readonly RegistrySession _session;
    private readonly Border _headerStripe = new() { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _address = new() { PlaceholderText = @"Key, e.g. HKLM\SOFTWARE\Microsoft – Enter opens it", ClearButtonEnabled = false };
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly ListView _values = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter values (Ctrl+F)", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24), Width = 260 };
    private readonly Dictionary<GridViewColumn, (string Header, RegSortColumn Sort)> _columns = [];
    private List<RegValue> _valueList = [];
    private ListCollectionView? _valueView;
    private RegSortColumn _sortBy = RegSortColumn.Name;
    private bool _descending;
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(4, 6, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly System.Windows.Controls.ProgressBar _busy = new() { IsIndeterminate = true, Height = 2, Visibility = Visibility.Hidden };
    private readonly ObservableCollection<RegNode> _hives = [];
    private RegNode? _current;
    private string? _requested;
    private int _token;
    private int _busyCount;
    private bool _started;

    /// <summary>A key was opened (argument: its canonical path).</summary>
    public event Action<string>? Navigated;

    public RegistryView(RegistrySession session, Action reconnect, Action close)
    {
        _session = session;
        DataContext = session;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(2);
        SetResourceReference(BackgroundProperty, "SessionAreaBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Child = root;
        root.Children.Add(RemoteToolChrome.Header(session, SymbolRegular.CubeTree24, "Registry", _headerStripe));

        var cell = new Grid();
        Grid.SetRow(cell, 1);
        root.Children.Add(cell);
        RecordingArea = cell;
        var body = new DockPanel { Margin = new Thickness(10, 10, 10, 8) };
        cell.Children.Add(body);
        cell.Children.Add(RemoteToolChrome.Overlay(reconnect, close));

        // ---- toolbar + address bar
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var up = RemoteToolChrome.Tool(SymbolRegular.ArrowUp24, "Parent key (Backspace)", () => { if (_current is { IsHive: false } c) _ = Navigate(RemoteRegistry.Parent(c.Path)); });
        var refresh = RemoteToolChrome.Tool(SymbolRegular.ArrowClockwise24, "Refresh (F5)", () => _ = Refresh());
        var newMenu = RemoteToolChrome.Tool(SymbolRegular.Add24, "New key or value", () => { });
        newMenu.Click += (_, _) => { var m = NewMenu(); m.PlacementTarget = newMenu; m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; m.IsOpen = true; };
        var export = RemoteToolChrome.Tool(SymbolRegular.ArrowExportLtr24, "Export the key to a .reg file on this PC", () => _ = Export());
        DockPanel.SetDock(up, Dock.Left);
        foreach (var b in new[] { export, newMenu, refresh }) { DockPanel.SetDock(b, Dock.Right); b.Margin = new Thickness(4, 0, 0, 0); bar.Children.Add(b); }
        bar.Children.Insert(0, up);
        // The filter narrows the values of the shown key and selects the first match, so Enter edits it.
        _filter.Margin = new Thickness(4, 0, 0, 0);
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _filter.Text = ""; _values.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down) { _values.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && SelectedValues is [var one]) { _ = Modify(one); e.Handled = true; }
        };
        DockPanel.SetDock(_filter, Dock.Right);
        bar.Children.Add(_filter);
        // In a detached window (no main window around it) the view handles Ctrl+F itself.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { FocusFilter(); e.Handled = true; } };
        _address.Margin = new Thickness(4, 0, 0, 0);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = Navigate(_address.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowPath(); e.Handled = true; }
        };
        bar.Children.Add(_address);
        DockPanel.SetDock(bar, Dock.Top);
        body.Children.Add(bar);
        DockPanel.SetDock(_busy, Dock.Top);
        body.Children.Add(_busy);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_status);
        ResetStatusColour();

        // ---- tree | values
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 160 });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        body.Children.Add(split);

        BuildTree();
        split.Children.Add(_tree);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        split.Children.Add(splitter);
        BuildValues();
        Grid.SetColumn(_values, 2);
        split.Children.Add(_values);

        foreach (var h in RemoteRegistry.Hives) _hives.Add(NewNode(h, h, true));

        session.Ready += () => _ = OnReady();
        session.FocusRequested += () => Dispatcher.BeginInvoke(() => _values.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        session.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(RegistrySession.AccentBrush)) UpdateFrame(); };
        UpdateFrame();
    }

    public string CurrentPath => _current?.Path ?? "";

    private void UpdateFrame()
    {
        _headerStripe.Background = _session.AccentBrush;
        SetResourceReference(BorderBrushProperty, "ChromeStrokeBrush");
    }

    private async Task OnReady()
    {
        // First connect: the last key of this connection; after a reconnect: the key that was open.
        var start = _started ? CurrentPath : _session.StartPath;
        _started = true;
        foreach (var h in _hives) { h.Loaded = false; h.Children.Clear(); h.SetHasChildren(true); h.IsExpanded = false; }
        if (!await Navigate(string.IsNullOrWhiteSpace(start) ? DefaultStart : start)) await Navigate(DefaultStart);
    }

    // ================================================================== tree

    private RegNode NewNode(string name, string path, bool hasChildren)
    {
        var n = new RegNode(name, path, hasChildren);
        n.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RegNode.IsExpanded) && n.IsExpanded && !n.Loaded) _ = LoadChildren(n);
        };
        return n;
    }

    private void BuildTree()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(RegNode.Symbol)));
        icon.SetValue(SymbolIcon.FontSizeProperty, 15.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RegNode.Name)));
        panel.AppendChild(icon);
        panel.AppendChild(text);
        _tree.ItemTemplate = new HierarchicalDataTemplate(typeof(RegNode)) { ItemsSource = new Binding(nameof(RegNode.Children)), VisualTree = panel };

        var style = new Style(typeof(TreeViewItem), (Style?)Application.Current?.TryFindResource(typeof(TreeViewItem)));
        style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding(nameof(RegNode.IsExpanded)) { Mode = BindingMode.TwoWay }));
        style.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding(nameof(RegNode.IsSelected)) { Mode = BindingMode.TwoWay }));
        _tree.ItemContainerStyle = style;
        _tree.ItemsSource = _hives;

        _tree.SelectedItemChanged += (_, e) =>
        {
            // Selected by code (Navigate) the key is already loading or shown.
            if (e.NewValue is RegNode { IsPlaceholder: false } n && n != _current && !string.Equals(n.Path, _requested, StringComparison.OrdinalIgnoreCase)) _ = ShowKey(n);
        };
        _tree.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (MainWindow.FindAncestor<TreeViewItem>(e.OriginalSource) is { } tvi) { tvi.IsSelected = true; tvi.Focus(); }
        };
        _tree.ContextMenuOpening += (_, e) =>
        {
            if (_current is null) { e.Handled = true; return; }
            _tree.ContextMenu = KeyMenu(_current);
        };
        _tree.ContextMenu = new ContextMenu();
        _tree.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.F5: _ = Refresh(); e.Handled = true; break;
                case Key.F2 when _current is { } c: _ = RenameKey(c); e.Handled = true; break;
                case Key.Delete when _current is { } c: _ = DeleteKey(c); e.Handled = true; break;
            }
        };
    }

    /// <summary>Applies a fresh list of sub keys in place, so expanded and selected keys stay as they are.</summary>
    private void Merge(RegNode n, List<RegSubKey> keys)
    {
        var names = keys.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = n.Children.Count - 1; i >= 0; i--)
            if (n.Children[i].IsPlaceholder || !names.Contains(n.Children[i].Name)) n.Children.RemoveAt(i);
        for (var i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (i < n.Children.Count && string.Equals(n.Children[i].Name, k.Name, StringComparison.OrdinalIgnoreCase)) { n.Children[i].SetHasChildren(k.HasChildren); continue; }
            n.Children.Insert(i, NewNode(k.Name, RemoteRegistry.Combine(n.Path, k.Name), k.HasChildren));
        }
        n.Loaded = true;
    }

    private async Task<bool> LoadChildren(RegNode n)
    {
        var l = await Fetch(n.Path);
        if (l is null) { n.IsExpanded = false; return false; }
        Merge(n, l.Keys);
        return true;
    }

    private async Task<RegListing?> Fetch(string path)
    {
        if (!_session.IsConnected) return null;
        Busy(true);
        try { return await _session.ListAsync(path); }
        catch (Exception ex) { SetError(ex.Message); return null; }
        finally { Busy(false); }
    }

    /// <summary>Opens a key typed or pasted into the address bar: expands the tree down to it and selects it.</summary>
    public async Task<bool> Navigate(string text)
    {
        string path;
        try { path = RemoteRegistry.Normalize(text); }
        catch (ArgumentException ex) { SetError(ex.Message); ShowPath(); return false; }
        if (path.Length == 0) { SetError("Choose a hive, e.g. HKEY_LOCAL_MACHINE."); return false; }

        var parts = path.Split('\\');
        var node = _hives.First(h => h.Name == parts[0]);
        foreach (var part in parts.Skip(1))
        {
            if (!node.Loaded && !await LoadChildren(node)) return false;
            var next = node.Children.FirstOrDefault(c => !c.IsPlaceholder && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                await Select(node);
                SetError($"Key not found: {RemoteRegistry.Combine(node.Path, part)}");
                return false;
            }
            node.IsExpanded = true;
            node = next;
        }
        return await Select(node);
    }

    private async Task<bool> Select(RegNode node)
    {
        var shown = ShowKey(node);
        node.IsSelected = true;
        var ok = await shown;
        _ = Dispatcher.BeginInvoke(() => BringIntoView(node), System.Windows.Threading.DispatcherPriority.Loaded);
        return ok;
    }

    /// <summary>Lists one key: refreshes its sub keys in the tree and shows its values.</summary>
    private async Task<bool> ShowKey(RegNode node)
    {
        var token = ++_token;
        _requested = node.Path;
        var l = await Fetch(node.Path);
        if (token != _token) return false;
        _requested = null;
        if (l is null) return false;
        var keep = _current == node ? _values.SelectedItems.Cast<RegValue>().Select(v => v.Name).ToHashSet() : null;
        // Another key starts unfiltered; a refresh of the same one keeps the filter.
        if (_current != node && _filter.Text.Length > 0) _filter.Text = "";
        _current = node;
        Merge(node, l.Keys);
        _valueList = l.Values;
        _valueView = new ListCollectionView(l.Values) { CustomSort = new ValueOrder(_sortBy, _descending), Filter = o => o is RegValue v && Shown(v) };
        _values.ItemsSource = _valueView;
        if (keep is { Count: > 0 }) foreach (var v in l.Values.Where(v => keep.Contains(v.Name))) _values.SelectedItems.Add(v);
        ShowPath();
        ResetStatusColour();
        _status.Text = $"{l.Keys.Count} keys, {l.Values.Count(v => v.IsSet)} values" +
                       (node.Path.StartsWith("HKEY_CURRENT_USER", StringComparison.Ordinal) ? "  ·  HKEY_CURRENT_USER is the profile of the account used for remoting" : "");
        Navigated?.Invoke(node.Path);
        return true;
    }

    private Task Refresh() => _current is { } c ? ShowKey(c) : Navigate(DefaultStart);

    private void BringIntoView(RegNode node)
    {
        ItemsControl parent = _tree;
        foreach (var name in node.Path.Split('\\'))
        {
            parent.UpdateLayout();
            var item = parent.Items.Cast<RegNode>().FirstOrDefault(n => n.Name == name);
            if (item is null || parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) return;
            parent = tvi;
        }
        ((TreeViewItem)parent).BringIntoView();
    }

    private void ShowPath() => _address.Text = _current?.Path ?? "";

    private void Busy(bool on)
    {
        _busyCount = Math.Max(0, _busyCount + (on ? 1 : -1));
        _busy.Visibility = _busyCount > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private void ResetStatusColour() => _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

    private void SetError(string text)
    {
        _status.Text = text;
        _status.Foreground = Brushes.IndianRed;
    }

    // ================================================================== values

    private void BuildValues()
    {
        var gv = new Wpf.Ui.Controls.GridView();
        void Column(GridViewColumn c, string header, RegSortColumn sort) { c.Header = header; _columns[c] = (header, sort); gv.Columns.Add(c); }
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 220, CellTemplate = ValueNameTemplate() }, "Name", RegSortColumn.Name);
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 120, DisplayMemberBinding = new Binding(nameof(RegValue.TypeName)) }, "Type", RegSortColumn.Type);
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 420, DisplayMemberBinding = new Binding(nameof(RegValue.DisplayData)) }, "Data", RegSortColumn.Data);
        _values.View = gv;
        // Clicking a header sorts by it; clicking it again reverses the order. The default value stays on top.
        _values.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: { } col } || !_columns.TryGetValue(col, out var c)) return;
            _descending = c.Sort == _sortBy && !_descending;
            _sortBy = c.Sort;
            ShowSort();
            if (_valueView is not null) _valueView.CustomSort = new ValueOrder(_sortBy, _descending);
        }));
        ShowSort();
        VirtualizingPanel.SetIsVirtualizing(_values, true);
        _values.MouseDoubleClick += (_, e) => { if (ValueAt(e.OriginalSource) is { } v) _ = Modify(v); };
        _values.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ValueAt(e.OriginalSource) is { } v && !_values.SelectedItems.Contains(v)) { _values.SelectedItems.Clear(); _values.SelectedItem = v; }
            else if (ValueAt(e.OriginalSource) is null) _values.SelectedItems.Clear();
        };
        _values.ContextMenuOpening += (_, e) =>
        {
            if (_current is null) { e.Handled = true; return; }
            _values.ContextMenu = ValueMenu(SelectedValues);
        };
        _values.ContextMenu = new ContextMenu();
        _values.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter when SelectedValues is [var one]: _ = Modify(one); e.Handled = true; break;
                case Key.F2 when SelectedValues is [{ IsDefault: false } one]: _ = RenameValue(one); e.Handled = true; break;
                case Key.Delete when SelectedValues is { Count: > 0 } sel: _ = DeleteValues(sel); e.Handled = true; break;
                case Key.F5: _ = Refresh(); e.Handled = true; break;
                case Key.Back when _current is { IsHive: false } c: _ = Navigate(RemoteRegistry.Parent(c.Path)); e.Handled = true; break;
            }
        };
    }

    private List<RegValue> SelectedValues => _values.SelectedItems.Cast<RegValue>().ToList();

    private static RegValue? ValueAt(object source) => MainWindow.FindAncestor<ListViewItem>(source)?.DataContext as RegValue;

    private static DataTemplate ValueNameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(RegValue.Symbol)));
        icon.SetValue(SymbolIcon.FontSizeProperty, 15.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RegValue.DisplayName)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    // ================================================================== menus

    private static System.Windows.Controls.MenuItem Item(string header, SymbolRegular icon, Action action, bool enabled = true, string? gesture = null) =>
        RemoteToolChrome.Item(header, icon, action, enabled, gesture);

    private static readonly (RegKind Kind, string Label)[] NewKinds =
    [
        (RegKind.String, "String value (REG_SZ)"), (RegKind.ExpandString, "Expandable string value (REG_EXPAND_SZ)"),
        (RegKind.MultiString, "Multi-string value (REG_MULTI_SZ)"), (RegKind.DWord, "DWORD (32-bit) value"),
        (RegKind.QWord, "QWORD (64-bit) value"), (RegKind.Binary, "Binary value"),
    ];

    private void AddNewItems(ItemsControl into)
    {
        var ok = _session.IsConnected && _current is not null;
        into.Items.Add(Item("Key…", SymbolRegular.FolderAdd24, () => _ = NewKey(), ok));
        into.Items.Add(new Separator());
        foreach (var (kind, label) in NewKinds)
            into.Items.Add(Item(label + "…", kind is RegKind.DWord or RegKind.QWord ? SymbolRegular.NumberSymbol24 : kind == RegKind.Binary ? SymbolRegular.Code24 : SymbolRegular.TextT24,
                () => _ = NewValue(kind), ok));
    }

    private ContextMenu NewMenu()
    {
        var m = new ContextMenu();
        AddNewItems(m);
        return m;
    }

    private ContextMenu KeyMenu(RegNode key)
    {
        var ok = _session.IsConnected;
        var m = new ContextMenu();
        if (!key.Loaded || key.Children.Count > 0)
            m.Items.Add(Item(key.IsExpanded ? "Collapse" : "Expand", key.IsExpanded ? SymbolRegular.ChevronUp24 : SymbolRegular.ChevronDown24, () => key.IsExpanded = !key.IsExpanded));
        var add = new System.Windows.Controls.MenuItem { Header = "New", Icon = new SymbolIcon(SymbolRegular.Add24) };
        AddNewItems(add);
        m.Items.Add(add);
        m.Items.Add(new Separator());
        var prot = RemoteRegistry.IsProtected(key.Path);
        m.Items.Add(Item("Rename…", SymbolRegular.Rename24, () => _ = RenameKey(key), ok && !prot, "F2"));
        m.Items.Add(Item("Delete…", SymbolRegular.Delete24, () => _ = DeleteKey(key), ok && !prot, "Del"));
        m.Items.Add(Item("Export…", SymbolRegular.ArrowExportLtr24, () => _ = Export(), ok && !key.IsHive));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Copy key path", SymbolRegular.Copy24, () => CopyText(key.Path)));
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(), ok, "F5"));
        return m;
    }

    private ContextMenu ValueMenu(List<RegValue> sel)
    {
        var ok = _session.IsConnected;
        var m = new ContextMenu();
        if (sel.Count > 0)
        {
            m.Items.Add(Item("Modify…", SymbolRegular.Edit24, () => _ = Modify(sel[0]), ok && sel.Count == 1 && sel[0].CanEdit, "Enter"));
            m.Items.Add(Item("Rename…", SymbolRegular.Rename24, () => _ = RenameValue(sel[0]), ok && sel is [{ IsDefault: false, CanEdit: true }], "F2"));
            m.Items.Add(Item(sel.Count > 1 ? $"Delete {sel.Count} values…" : "Delete…", SymbolRegular.Delete24, () => _ = DeleteValues(sel), ok && sel.Any(v => v.IsSet), "Del"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Copy name", SymbolRegular.Copy24, () => CopyText(string.Join(Environment.NewLine, sel.Select(v => v.Name)))));
            m.Items.Add(Item("Copy data", SymbolRegular.Copy24, () => CopyText(string.Join(Environment.NewLine, sel.Select(v => v.Kind == RegKind.MultiString ? v.Data.Replace("\n", Environment.NewLine) : v.Data)))));
            m.Items.Add(new Separator());
        }
        var add = new System.Windows.Controls.MenuItem { Header = "New", Icon = new SymbolIcon(SymbolRegular.Add24) };
        AddNewItems(add);
        m.Items.Add(add);
        m.Items.Add(Item("Refresh", SymbolRegular.ArrowClockwise24, () => _ = Refresh(), ok, "F5"));
        return m;
    }

    private static void CopyText(string text)
    {
        if (text.Length > 0) try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ================================================================== actions

    private Window? Owner => Window.GetWindow(this);
    private string Where => _session.Connection.Name;

    /// <summary>Runs one change on the remote computer; errors go to the status line.</summary>
    private async Task<bool> Do(string what, Func<Task> op)
    {
        if (!_session.IsConnected) return false;
        Busy(true);
        try
        {
            await op();
            Services.DiagLog.Write($"Registry: {what}");
            return true;
        }
        catch (Exception ex) { SetError($"{what} failed: {ex.Message}"); return false; }
        finally { Busy(false); }
    }

    private async Task Modify(RegValue v)
    {
        if (_current is not { } key || !_session.IsConnected) return;
        if (!v.CanEdit) { SetError("Values of this type can't be edited here."); return; }
        var dlg = new RegValueDialog($"{key.Path}  ·  {Where}", v, v.Kind) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        if (await Do("edit value", () => _session.SetValueAsync(key.Path, v.Name, v.Kind, dlg.Data, createOnly: false))) await ShowKey(key);
    }

    private async Task NewValue(RegKind kind)
    {
        if (_current is not { } key || !_session.IsConnected) return;
        var dlg = new RegValueDialog($"{key.Path}  ·  {Where}", null, kind) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.ValueName;
        if (await Do("new value", () => _session.SetValueAsync(key.Path, name, kind, dlg.Data, createOnly: true)))
        {
            await ShowKey(key);
            SelectValue(name);
        }
    }

    public void FocusFilter() { _filter.Focus(); _filter.SelectAll(); }

    private bool Shown(RegValue v) => _filter.Text.Trim().Length == 0 || RegValue.Matches(v, _filter.Text);

    /// <summary>Narrows the values to the filter and selects the first match.</summary>
    private void ApplyFilter()
    {
        if (_valueView is null) return;
        _valueView.Refresh();
        if (_filter.Text.Trim().Length > 0)
        {
            _values.SelectedItems.Clear();
            if (_valueView.Cast<RegValue>().FirstOrDefault() is { } first) { _values.SelectedItem = first; _values.ScrollIntoView(first); }
        }
    }

    /// <summary>The sorted column's header carries an arrow for its direction.</summary>
    private void ShowSort()
    {
        foreach (var (col, (header, sort)) in _columns)
            col.Header = sort == _sortBy ? header + (_descending ? "  ▼" : "  ▲") : header;
    }

    private sealed class ValueOrder(RegSortColumn by, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) => x is RegValue a && y is RegValue b ? RegValue.Compare(a, b, by, descending) : 0;
    }

    private void SelectValue(string name)
    {
        if (_valueList.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } v)
        {
            // A value the filter hides (just created or renamed) is shown: the filter is cleared.
            if (!Shown(v)) _filter.Text = "";
            _values.SelectedItems.Clear();
            _values.SelectedItem = v;
            _values.ScrollIntoView(v);
        }
    }

    private async Task RenameValue(RegValue v)
    {
        if (_current is not { } key || v.IsDefault || !_session.IsConnected) return;
        var dlg = new InputDialog("Rename value", $"New name for \"{v.Name}\" in {key.Path}", v.Name) { Owner = Owner };
        if (dlg.ShowDialog() != true || dlg.Value == v.Name) return;
        if (dlg.Value.Length == 0) { SetError("Enter a name for the value."); return; }
        if (await Do("rename value", () => _session.RenameValueAsync(key.Path, v.Name, dlg.Value)))
        {
            await ShowKey(key);
            SelectValue(dlg.Value);
        }
    }

    private async Task DeleteValues(List<RegValue> sel)
    {
        sel = sel.Where(v => v.IsSet).ToList();
        if (_current is not { } key || sel.Count == 0 || Owner is not { } owner || !_session.IsConnected) return;
        var list = string.Join("\n", sel.Take(8).Select(v => v.DisplayName)) + (sel.Count > 8 ? $"\n… and {sel.Count - 8} more" : "");
        var text = $"Delete {(sel.Count == 1 ? "this value" : $"these {sel.Count} values")} in {key.Path} on {Where}?\n\n{list}\n\n" +
                   "Deleting registry values can stop programs or Windows from working. This can't be undone.";
        if (!await Dialogs.Confirm(owner, "Delete registry value", text, "Delete")) return;
        await Do($"delete {sel.Count} value(s)", () => _session.DeleteValuesAsync(key.Path, sel.Select(v => v.Name)));
        await ShowKey(key);
    }

    private async Task NewKey()
    {
        if (_current is not { } parent || !_session.IsConnected) return;
        var dlg = new InputDialog("New key", $"Name of the new key below {parent.Path}", "New Key #1") { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (!RemoteRegistry.IsValidKeyName(name)) { SetError($"\"{name}\" is not a valid key name (no backslash, at most 255 characters)."); return; }
        string? created = null;
        if (await Do("new key", async () => created = await _session.CreateKeyAsync(parent.Path, name)) && created is not null)
        {
            parent.IsExpanded = true;
            await Navigate(created);
        }
    }

    private async Task RenameKey(RegNode key)
    {
        if (!_session.IsConnected) return;
        if (RemoteRegistry.IsProtected(key.Path)) { SetError("Hives and the keys directly below them can't be renamed here."); return; }
        var dlg = new InputDialog("Rename key", $"New name for \"{key.Name}\" on {Where}", key.Name) { Owner = Owner };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (name == key.Name) return;
        if (!RemoteRegistry.IsValidKeyName(name)) { SetError($"\"{name}\" is not a valid key name (no backslash, at most 255 characters)."); return; }
        string? renamed = null;
        if (await Do("rename key", async () => renamed = await _session.RenameKeyAsync(key.Path, name)) && renamed is not null)
        {
            var parent = RemoteRegistry.Parent(key.Path);
            await Navigate(parent);
            await Navigate(renamed);
        }
    }

    private async Task DeleteKey(RegNode key)
    {
        if (!_session.IsConnected || Owner is not { } owner) return;
        if (RemoteRegistry.IsProtected(key.Path)) { SetError("Hives and the keys directly below them can't be deleted here."); return; }
        var text = $"Permanently delete this key and everything below it on {Where}?\n\n{key.Path}\n\n" +
                   "Deleting registry keys can stop programs or Windows from working. This can't be undone – use Export first if you may need it again.";
        if (!await Dialogs.Confirm(owner, "Delete registry key", text, "Delete")) return;
        if (await Do("delete key", () => _session.DeleteKeyAsync(key.Path)))
            await Navigate(RemoteRegistry.Parent(key.Path));
    }

    private async Task Export()
    {
        if (_current is not { IsHive: false } key || !_session.IsConnected) { SetError("Choose a key below a hive to export."); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export registry key",
            FileName = $"{_session.Connection.Name} - {key.Name}.reg".Replace(':', '_'),
            Filter = "Registration files (*.reg)|*.reg",
            DefaultExt = ".reg",
        };
        if (dlg.ShowDialog(Owner) != true) return;
        byte[]? bytes = null;
        if (await Do("export key", async () => bytes = await _session.ExportAsync(key.Path)) && bytes is not null)
        {
            try
            {
                await File.WriteAllBytesAsync(dlg.FileName, bytes);
                ResetStatusColour();
                _status.Text = $"Exported {key.Path} to {dlg.FileName} ({TransferItem.Size(bytes.Length)})";
            }
            catch (Exception ex) { SetError("Export failed: " + ex.Message); }
        }
    }
}
