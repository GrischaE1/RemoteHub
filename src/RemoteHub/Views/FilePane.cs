using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
/// One side of the file transfer tab: address bar, folder listing (only the shown folder is loaded) and drag &amp; drop.
/// The same control is used for this PC and the remote computer; listing and folder creation are passed in.
/// </summary>
public sealed class FilePane : DockPanel, IFilterableView
{
    private readonly Func<string, Task<Listing>> _list;
    private readonly Func<string, Task<string>> _mkdir;
    private readonly Func<string, string> _parent;
    private readonly Func<string, string, string> _join;
    private readonly TextBox _address = new() { PlaceholderText = "Path, e.g. C:\\Temp or %TEMP% – Enter opens it", ClearButtonEnabled = false };
    private readonly ListView _listView = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true, BorderThickness = new Thickness(0) };
    private readonly TextBox _filter = new() { PlaceholderText = "Filter (Ctrl+F)", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24) };
    private readonly ListFilter<FileEntry, FileSortColumn> _rows;
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(4, 6, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly System.Windows.Controls.ProgressBar _busy = new() { IsIndeterminate = true, Height = 2, Visibility = Visibility.Hidden };
    private int _navToken;
    private Point _dragStart;
    private FileEntry? _pressed;
    private bool _deferSelect;

    /// <param name="parent">The folder above a path (default: Windows paths, "" = drives).</param>
    /// <param name="join">A folder and a name joined (default: with a backslash).</param>
    public FilePane(string title, string symbol, Func<string, Task<Listing>> list, Func<string, Task<string>> mkdir,
                    Func<string, string>? parent = null, Func<string, string, string>? join = null, string? pathHint = null)
    {
        _list = list;
        _mkdir = mkdir;
        _parent = parent ?? RemoteFs.Parent;
        _join = join ?? ((folder, name) => folder.TrimEnd('\\') + "\\" + name);
        if (pathHint is not null) _address.PlaceholderText = pathHint;
        LastChildFill = true;

        // ---- title + address bar
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        titleRow.Children.Add(new SymbolIcon { Symbol = Enum.Parse<SymbolRegular>(symbol), FontSize = 16, Margin = new Thickness(0, 0, 6, 0) });
        titleRow.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(titleRow, Dock.Top);
        head.Children.Add(titleRow);

        var up = Tool(SymbolRegular.ArrowUp24, "Up one folder (Backspace)", () => _ = Navigate(_parent(CurrentPath)));
        var refresh = Tool(SymbolRegular.ArrowClockwise24, "Refresh (F5)", () => _ = Navigate(CurrentPath));
        var newFolder = Tool(SymbolRegular.FolderAdd24, "New folder", () => _ = NewFolder());
        DockPanel.SetDock(up, Dock.Left);
        DockPanel.SetDock(newFolder, Dock.Right);
        DockPanel.SetDock(refresh, Dock.Right);
        head.Children.Add(up);
        head.Children.Add(newFolder);
        head.Children.Add(refresh);
        _address.Margin = new Thickness(4, 0, 4, 0);
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = Navigate(_address.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowPath(); e.Handled = true; }
        };
        head.Children.Add(_address);
        DockPanel.SetDock(head, Dock.Top);
        Children.Add(head);

        // ---- filter: narrows the shown folder and selects the first match, so Enter opens it
        _filter.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(_filter, Dock.Top);
        Children.Add(_filter);
        // In a detached window (no main window around it) the pane handles Ctrl+F itself.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { FocusFilter(); e.Handled = true; } };

        DockPanel.SetDock(_busy, Dock.Top);
        Children.Add(_busy);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(_status);

        // ---- listing: a header click sorts by that column (folders stay above files)
        _rows = new ListFilter<FileEntry, FileSortColumn>(_listView, _filter, FileEntry.Matches, FileEntry.Compare, FileSortColumn.Name,
            f => { if (f.IsContainer) _ = Navigate(f.FullPath); });
        _rows.Changed += UpdateStatus;
        var gv = new Wpf.Ui.Controls.GridView();
        void Column(GridViewColumn c, string header, FileSortColumn sort) { gv.Columns.Add(c); _rows.Column(c, header, sort); }
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 260, CellTemplate = NameTemplate() }, "Name", FileSortColumn.Name);
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 90, DisplayMemberBinding = new Binding(nameof(FileEntry.SizeText)) }, "Size", FileSortColumn.Size);
        Column(new Wpf.Ui.Controls.GridViewColumn { Width = 140, DisplayMemberBinding = new Binding(nameof(FileEntry.ModifiedText)) }, "Modified", FileSortColumn.Modified);
        _listView.View = gv;
        VirtualizingPanel.SetIsVirtualizing(_listView, true);
        VirtualizingPanel.SetVirtualizationMode(_listView, VirtualizationMode.Recycling);
        _listView.MouseDoubleClick += (_, e) => { if (ItemAt(e.OriginalSource) is { IsContainer: true } f) _ = Navigate(f.FullPath); };
        _listView.KeyDown += List_KeyDown;
        _listView.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
        _listView.PreviewMouseLeftButtonUp += List_PreviewMouseLeftButtonUp;
        _listView.PreviewMouseMove += List_PreviewMouseMove;
        _listView.DragOver += (_, e) => { e.Effects = Target(e) is not null && (CanAccept?.Invoke(e.Data) ?? false) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _listView.Drop += (_, e) =>
        {
            if (Target(e) is { } folder && (CanAccept?.Invoke(e.Data) ?? false)) Dropped?.Invoke(e.Data, folder);
            e.Handled = true;
        };
        _listView.SelectionChanged += (_, _) => UpdateStatus();
        _listView.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemAt(e.OriginalSource) is { } f && !_listView.SelectedItems.Contains(f)) { _listView.SelectedItems.Clear(); _listView.SelectedItem = f; }
        };
        _listView.ContextMenuOpening += (_, e) =>
        {
            var menu = MenuFor?.Invoke(SelectedEntries);
            if (menu is null) { e.Handled = true; return; }
            _listView.ContextMenu = menu;
        };
        _listView.ContextMenu = new ContextMenu(); // placeholder so ContextMenuOpening fires
        Children.Add(_listView);
    }

    /// <summary>Current folder ("" = list of drives).</summary>
    public string CurrentPath { get; private set; } = "";
    public IReadOnlyList<FileEntry> Entries { get; private set; } = [];
    public List<FileEntry> SelectedEntries => _listView.SelectedItems.Cast<FileEntry>().ToList();

    /// <summary>Decides whether a drag can be dropped here.</summary>
    public Func<IDataObject, bool>? CanAccept { get; set; }
    /// <summary>Something was dropped onto this pane (argument: target folder).</summary>
    public event Action<IDataObject, string>? Dropped;
    /// <summary>The user starts dragging these entries out of the pane.</summary>
    public event Action<List<FileEntry>>? DragStarting;
    public event Action<string>? Navigated;
    /// <summary>Builds the right-click menu for the current selection (null = no menu).</summary>
    public Func<List<FileEntry>, ContextMenu?>? MenuFor { get; set; }
    /// <summary>F2 / Del on the selection.</summary>
    public event Action<FileEntry>? RenameRequested;
    public event Action<List<FileEntry>>? DeleteRequested;

    public void ShowError(string text) => SetError(text);

    public void FocusList() => Dispatcher.BeginInvoke(() => _listView.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    public void FocusFilter() => _rows.Focus();

    private static Button Tool(SymbolRegular icon, string tip, Action a)
    {
        var b = new Button { Icon = new SymbolIcon(icon), ToolTip = tip, Padding = new Thickness(8, 5, 8, 5), MinWidth = 0 };
        AutomationProperties_SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private static void AutomationProperties_SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static DataTemplate NameTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetBinding(OpacityProperty, new Binding(nameof(FileEntry.Opacity)));
        var icon = new FrameworkElementFactory(typeof(SymbolIcon));
        icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(FileEntry.Symbol)));
        icon.SetValue(SymbolIcon.FontSizeProperty, 15.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileEntry.Name)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(icon);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>Lists <paramref name="path"/>. On failure the previous folder stays and the error is shown.</summary>
    public async Task<bool> Navigate(string path)
    {
        var token = ++_navToken;
        _busy.Visibility = Visibility.Visible;
        try
        {
            var listing = await _list(path ?? "");
            if (token != _navToken) return false;
            var same = string.Equals(listing.Path, CurrentPath, StringComparison.OrdinalIgnoreCase);
            var keep = same ? SelectedEntries.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            CurrentPath = listing.Path;
            Entries = listing.Entries;
            // Another folder starts unfiltered; a refresh of the same one keeps the filter.
            _rows.SetRows(listing.Entries, same);
            if (keep is { Count: > 0 })
                foreach (var e in listing.Entries.Where(e => keep.Contains(e.Name))) _listView.SelectedItems.Add(e);
            ShowPath();
            UpdateStatus();
            Navigated?.Invoke(CurrentPath);
            return true;
        }
        catch (Exception ex)
        {
            if (token != _navToken) return false;
            ShowPath();
            SetError(ex.Message);
            return false;
        }
        finally { if (token == _navToken) _busy.Visibility = Visibility.Hidden; }
    }

    public Task<bool> Refresh() => Navigate(CurrentPath);

    private void ShowPath() => _address.Text = CurrentPath.Length == 0 ? "This PC" : CurrentPath;

    private void UpdateStatus()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var sel = _listView.SelectedItems.Count;
        var files = Entries.Count(e => e.Kind == EntryKind.File);
        var shown = _rows.Active ? _rows.Shown : -1;
        _status.Text = $"{Entries.Count - files} folders, {files} files" + (shown >= 0 ? $"  ·  {shown} shown" : "") +
                       (sel > 0 ? $"  ·  {sel} selected ({TransferItem.Size(SelectedEntries.Sum(e => e.Kind == EntryKind.File ? e.Size : 0))})" : "");
    }

    private void SetError(string text)
    {
        _status.Text = text;
        _status.Foreground = Brushes.IndianRed;
    }

    public async Task NewFolder()
    {
        if (CurrentPath.Length == 0) { SetError("Open a drive or folder first."); return; }
        var dlg = new InputDialog("New folder", $"Name of the new folder in {CurrentPath}", "New folder") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { SetError("That is not a valid folder name."); return; }
        try
        {
            await _mkdir(_join(CurrentPath, name));
            await Refresh();
        }
        catch (Exception ex) { SetError(ex.Message); }
    }

    // ---------------------------------------------------------------- input

    private static FileEntry? ItemAt(object source) => MainWindow.FindAncestor<ListViewItem>(source)?.DataContext as FileEntry;

    private string? Target(DragEventArgs e) =>
        ItemAt(e.OriginalSource) is { IsContainer: true } f ? f.FullPath : CurrentPath.Length > 0 ? CurrentPath : null;

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SelectedEntries is [{ IsContainer: true } f]: _ = Navigate(f.FullPath); e.Handled = true; break;
            case Key.Back: _ = Navigate(_parent(CurrentPath)); e.Handled = true; break;
            case Key.F5: _ = Refresh(); e.Handled = true; break;
            case Key.F2 when SelectedEntries is [{ Kind: not EntryKind.Drive } one]: RenameRequested?.Invoke(one); e.Handled = true; break;
            case Key.Delete when SelectedEntries.Where(x => x.Kind != EntryKind.Drive).ToList() is { Count: > 0 } del: DeleteRequested?.Invoke(del); e.Handled = true; break;
        }
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_listView);
        _pressed = ItemAt(e.OriginalSource);
        // Keep a multi-selection when the user presses on one of the selected items to drag them.
        _deferSelect = _pressed is not null && _listView.SelectedItems.Contains(_pressed) && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1;
        if (_deferSelect) e.Handled = true;
    }

    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressed is not null)
        {
            _listView.SelectedItems.Clear();
            _listView.SelectedItem = _pressed;
        }
        _deferSelect = false;
        _pressed = null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is null) return;
        var d = e.GetPosition(_listView) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!_listView.SelectedItems.Contains(_pressed)) { _listView.SelectedItems.Clear(); _listView.SelectedItem = _pressed; }
        var items = SelectedEntries.Where(x => x.Kind != EntryKind.Drive).ToList();
        _pressed = null;
        _deferSelect = false;
        if (items.Count > 0) DragStarting?.Invoke(items);
    }
}
