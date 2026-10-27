using System.Management.Automation.Runspaces;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace RemoteHub.Views;

/// <summary>What the Scripts view needs from the main window.</summary>
/// <param name="RunOn">Run a script on a connection the user picks; true = the draft under test (§6.4–§6.5).</param>
/// <param name="OpenCompletionSession">Opens a connection's session to complete against (§11, phase 4); null when not offered.</param>
public sealed record ScriptsHost(Window Owner, UiState Ui, Action SaveUi, Action<ScriptEntry, bool> RunOn, Func<string, string?> ConnectionName,
    Func<Task<(Runspace Runspace, string Name)?>>? OpenCompletionSession);

/// <summary>
/// The Scripts view (docs/scripts-view-design.md §4): folders, the scripts of the selected folder, and the script in the
/// code editor with its settings and versions. Typing is saved as a draft a second after it stops; "Ready to deploy"
/// makes it a version, and only versions run on devices. Names and text never go to the diagnostics log; the audit log
/// gets ids, version numbers and hashes only.
/// </summary>
public sealed class ScriptsView : Grid
{
    private const string ScriptDragFormat = "RemoteHub.Script";
    private const string FolderDragFormat = "RemoteHub.ScriptFolder";
    private const string FavouritesKey = "fav", RecentKey = "recent", MineKey = "mine", LibraryKey = "lib", StarterKey = "lib:starter";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly ScriptsHost _host;
    private readonly System.Windows.Controls.TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, SelectionMode = SelectionMode.Extended };
    private readonly TextBox _search = new() { PlaceholderText = "Search scripts (Ctrl+F)", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24) };
    private readonly ComboBox _colourFilter = new() { MinWidth = 120, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _listHeader = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 6) };

    // the script pane
    private readonly System.Windows.Controls.TextBox _name = new() { FontSize = NoteMarkdown.H1Size, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 4) };
    private readonly Button _colourButton = new() { MinWidth = 0, Padding = new Thickness(6), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Colour" };
    private readonly Border _colourDot = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
    private readonly ComboBox _language = new() { MinWidth = 130 };
    private readonly ComboBox _context = new() { MinWidth = 110 };
    private readonly ComboBox _risk = new() { MinWidth = 150 };
    private readonly ComboBox _os = new() { MinWidth = 130 };
    private readonly ComboBox _timeout = new() { MinWidth = 90 };
    private readonly StackPanel _psSettings = new() { Orientation = Orientation.Horizontal };
    private readonly System.Windows.Controls.TextBox _description = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, Margin = new Thickness(0, 6, 0, 8), MaxLength = ScriptStore.MaxDescriptionLength };
    private readonly ScriptEditor _editor = new();
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _versionText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _deploy = new() { Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Checkmark24), Content = "Ready to deploy", ToolTip = "Make the draft the version that runs on devices (Ctrl+Shift+D)" };
    private readonly Button _compare = new() { Icon = new SymbolIcon(SymbolRegular.ArrowSwap24), Content = "Compare", ToolTip = "Compare versions (Ctrl+Shift+C)", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _discard = new() { Content = "Discard draft", ToolTip = "Put the draft back to the deployed version", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _duplicate = new() { Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Copy24), Content = "Duplicate to My scripts" };
    private readonly Button _run = new() { Icon = new SymbolIcon(SymbolRegular.Play24), Content = "Run on…", ToolTip = "Run the deployed version on a device (F5)" };
    private readonly Button _completeAgainst = new() { Icon = new SymbolIcon(SymbolRegular.PlugConnected24), Content = "Complete from this PC", ToolTip = "Where IntelliSense gets its commands from: this PC, or a device's session" };
    private readonly StackPanel _versionBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _libraryBar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid _scriptCard = new();
    private readonly FrameworkElement _emptyScript;
    private readonly UndoBar _undoBar = new();
    private readonly ColumnDefinition _foldersColumn;
    private readonly ColumnDefinition _listColumn;
    private readonly DispatcherTimer _saveTimer = new() { Interval = SaveDelay };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private string _folderKey = MineKey;
    private ScriptEntry? _current;
    private bool _dirty;
    private bool _loading;
    private Point _dragStart;
    private Runspace? _remote;
    private string? _remoteName;

    public ScriptsView(ScriptsHost host)
    {
        _host = host;
        Focusable = false;
        _foldersColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(host.Ui.ScriptsFoldersWidth, 170, 500)), MinWidth = 170 };
        _listColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(host.Ui.ScriptsListWidth, 200, 560)), MinWidth = 200 };
        ColumnDefinitions.Add(_foldersColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(_listColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });

        Children.Add(Card(BuildFoldersPane(), 0, new Thickness(0, 0, 2, 10)));
        Children.Add(Splitter(1, () => { host.Ui.ScriptsFoldersWidth = _foldersColumn.ActualWidth; host.SaveUi(); }));
        Children.Add(Card(BuildListPane(), 2, new Thickness(2, 0, 2, 10)));
        Children.Add(Splitter(3, () => { host.Ui.ScriptsListWidth = _listColumn.ActualWidth; host.SaveUi(); }));
        _emptyScript = EmptyHint("Pick a script, or add one with New script (Ctrl+N).");
        Children.Add(Card(BuildScriptPane(), 4, new Thickness(2, 0, 10, 10)));
        SetColumnSpan(_undoBar, 5);
        Children.Add(_undoBar);

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ShowList(); };
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) Save(); };
        PreviewKeyDown += View_PreviewKeyDown;
        Unloaded += (_, _) => CloseRemote();

        EnsureDefault();
        Reload(host.Ui.LastScriptFolderId ?? MineKey);
        if (host.Ui.LastScriptId is { } last && ScriptCatalog.Find(last) is { } entry) Open(entry);
    }

    // ================================================================== layout

    private static Border Card(UIElement content, int column, Thickness margin)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 10, 8, 8), Margin = margin, Child = content };
        b.SetResourceReference(Border.BackgroundProperty, "SidebarBackgroundBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        SetColumn(b, column);
        return b;
    }

    private static GridSplitter Splitter(int column, Action done)
    {
        var s = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        s.DragCompleted += (_, _) => done();
        SetColumn(s, column);
        return s;
    }

    private static TextBlock EmptyHint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(20) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    private static Button LinkButton(SymbolRegular icon, string text, Action a)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new SymbolIcon(icon) { FontSize = 14, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
        b.Click += (_, _) => a();
        AutomationProperties.SetName(b, text);
        return b;
    }

    private UIElement BuildFoldersPane()
    {
        var dock = new DockPanel();
        var header = new TextBlock { Text = "Folders", Style = TryFindResource("SectionHeader") as Style, Margin = new Thickness(4, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(LinkButton(SymbolRegular.FolderAdd24, "New folder", () => NewFolder(SelectedFolderId)));
        if (Features.IsAllowed(Feature.ScriptImport)) bottom.Children.Add(LinkButton(SymbolRegular.ArrowImport24, "Import…", () => _ = Import(SelectedFolderId, null)));
        bottom.Children.Add(LinkButton(SymbolRegular.Delete24, "Recently deleted", ShowRecentlyDeleted));
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        _tree.SelectedItemChanged += (_, _) => { if (!_loading && _tree.SelectedItem is TreeViewItem { Tag: string key }) SelectFolder(key); };
        _tree.KeyDown += Tree_KeyDown;
        _tree.AllowDrop = true;
        _tree.Drop += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropFiles(SelectedFolderId, e); };
        AutomationProperties.SetName(_tree, "Script folders");
        dock.Children.Add(_tree);
        return dock;
    }

    private UIElement BuildListPane()
    {
        var dock = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        var add = new Button { Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Add24), ToolTip = "New PowerShell script (Ctrl+N) or SQL script" };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(new TextBlock { Text = "New script", VerticalAlignment = VerticalAlignment.Center });
        addRow.Children.Add(new SymbolIcon(SymbolRegular.ChevronDown24) { FontSize = 12, Margin = new Thickness(8, 1, 0, 0) });
        add.Content = addRow;
        AutomationProperties.SetName(add, "New script");
        add.Click += (_, _) =>
        {
            var m = new ContextMenu();
            m.Items.Add(RemoteToolChrome.Item("PowerShell script", SymbolRegular.WindowConsole20, () => NewScript(ScriptStore.PowerShell), gesture: "Ctrl+N"));
            m.Items.Add(RemoteToolChrome.Item("SQL script", SymbolRegular.Database24, () => NewScript("tsql")));
            m.PlacementTarget = add;
            m.Placement = PlacementMode.Bottom;
            m.IsOpen = true;
        };
        DockPanel.SetDock(add, Dock.Left);
        bar.Children.Add(add);
        var sort = RemoteToolChrome.Tool(SymbolRegular.ArrowSort24, "Sort scripts", () => { });
        sort.Click += (_, _) => { var m = SortMenu(); m.PlacementTarget = sort; m.Placement = PlacementMode.Bottom; m.IsOpen = true; };
        DockPanel.SetDock(sort, Dock.Right);
        bar.Children.Add(sort);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);

        _search.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(_search, "Search scripts");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; } else if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; _list.Focus(); e.Handled = true; } };
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(_search);
        AutomationProperties.SetName(_colourFilter, "Filter by colour");
        _colourFilter.SelectionChanged += (_, _) => { if (!_loading) ShowList(); };
        DockPanel.SetDock(_colourFilter, Dock.Top);
        dock.Children.Add(_colourFilter);
        DockPanel.SetDock(_listHeader, Dock.Top);
        dock.Children.Add(_listHeader);

        _list.SelectionChanged += (_, _) =>
        {
            if (_loading || _list.SelectedItems.Count != 1 || _list.SelectedItem is not ListBoxItem { Tag: ScriptEntry e }) return;
            Open(e);
        };
        _list.KeyDown += List_KeyDown;
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        _list.PreviewMouseMove += List_PreviewMouseMove;
        _list.ContextMenuOpening += (_, e) => { _list.ContextMenu = ListMenu(); if (_list.ContextMenu is null) e.Handled = true; };
        AutomationProperties.SetName(_list, "Scripts");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        dock.Children.Add(_list);
        return dock;
    }

    private UIElement BuildScriptPane()
    {
        _scriptCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _scriptCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _scriptCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- header: name, colour; settings; description
        _name.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _name.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextFillColorPrimaryBrush");
        _name.GotKeyboardFocus += (_, _) => { if (!_name.IsReadOnly) _name.SetResourceReference(Control.BorderBrushProperty, "ChromeStrokeBrush"); };
        _name.LostKeyboardFocus += (_, _) => _name.BorderBrush = Brushes.Transparent;
        _name.TextChanged += (_, _) => OnEdited();
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _editor.FocusEditor(); e.Handled = true; } };
        AutomationProperties.SetName(_name, "Script name");
        _colourDot.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
        _colourButton.Content = _colourDot;
        _colourButton.Click += (_, _) => ShowColourMenu(_colourButton);
        AutomationProperties.SetName(_colourButton, "Colour");
        var nameRow = new DockPanel();
        DockPanel.SetDock(_colourButton, Dock.Right);
        nameRow.Children.Add(_colourButton);
        nameRow.Children.Add(_name);

        foreach (var c in ScriptStore.Contexts) _context.Items.Add(new ComboBoxItem { Content = ScriptLabels.Context(c), Tag = c });
        foreach (var r in ScriptStore.Risks) _risk.Items.Add(new ComboBoxItem { Content = ScriptLabels.Risk(r), Tag = r });
        foreach (var o in ScriptStore.OsScopes) _os.Items.Add(new ComboBoxItem { Content = ScriptLabels.Os(o), Tag = o });
        foreach (var m in new[] { 1, 2, 5, 10, 15, 30, 60, 120 }) _timeout.Items.Add(new ComboBoxItem { Content = $"{m} min", Tag = m * 60 });
        foreach (var l in ScriptStore.SqlLanguages) _language.Items.Add(new ComboBoxItem { Content = ScriptLabels.Language(l), Tag = l });
        AutomationProperties.SetName(_context, "Runs as");
        AutomationProperties.SetName(_risk, "Risk");
        AutomationProperties.SetName(_os, "Operating systems");
        AutomationProperties.SetName(_timeout, "Timeout");
        AutomationProperties.SetName(_language, "SQL dialect");
        _context.ToolTip = "System: as NT AUTHORITY\\SYSTEM. User: as the user signed in on the device. Both: System, then User. As me: as the account RemoteHub signs in with.";
        _risk.ToolTip = "R2 scripts ask for the device's name before they run.";
        _os.ToolTip = "RemoteHub warns before running on another kind of Windows; it doesn't refuse.";
        foreach (var box in new[] { _context, _risk, _os, _timeout }) box.SelectionChanged += (_, _) => OnSettingChanged();
        _language.SelectionChanged += (_, _) =>
        {
            if (_loading || _current?.Own is not { } s || _language.SelectedItem is not ComboBoxItem { Tag: string l }) return;
            ScriptStore.SetLanguage(s.Id, l);
            _editor.SetLanguage(l);
            RefreshCurrent();
        };
        _psSettings.Children.Add(Labelled("Runs as", _context));
        _psSettings.Children.Add(Labelled("Risk", _risk));
        _psSettings.Children.Add(Labelled("OS", _os));
        _psSettings.Children.Add(Labelled("Timeout", _timeout));
        var settings = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        settings.Children.Add(Labelled("Language", _language));
        settings.Children.Add(_psSettings);
        _description.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _description.TextChanged += (_, _) => OnEdited();
        AutomationProperties.SetName(_description, "Description");
        var descriptionRow = new DockPanel();
        var synopsis = RemoteToolChrome.Tool(SymbolRegular.TextDescription24, "Use the script's synopsis", UseSynopsis);
        DockPanel.SetDock(synopsis, Dock.Right);
        descriptionRow.Children.Add(synopsis);
        descriptionRow.Children.Add(_description);

        var head = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        head.Children.Add(nameRow);
        head.Children.Add(settings);
        head.Children.Add(descriptionRow);
        _scriptCard.Children.Add(head);

        _editor.Margin = new Thickness(4, 0, 0, 0);
        _editor.TextChanged += OnEdited;
        _editor.ErrorsChanged += _ => UpdateStatus();
        _editor.SecretHintChanged += hint =>
        {
            _hint.Text = hint ?? "";
            _hint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        };
        _editor.CompletionRunspace = () => _remote;
        SetRow(_editor, 1);
        _scriptCard.Children.Add(_editor);

        // ---- actions, status, versions
        var actions = new DockPanel { Margin = new Thickness(4, 8, 0, 0), LastChildFill = false };
        _run.Click += (_, _) => RunCurrent(draft: false);
        var copy = RemoteToolChrome.Tool(SymbolRegular.Copy24, "Copy the script", CopyScript);
        var more = RemoteToolChrome.Tool(SymbolRegular.MoreHorizontal24, "More actions for this script", () => { });
        more.Click += (_, _) => { var m = ScriptMenu(); m.PlacementTarget = more; m.Placement = PlacementMode.Bottom; m.IsOpen = true; };
        actions.Children.Add(_run);
        actions.Children.Add(copy);
        actions.Children.Add(more);
        if (_host.OpenCompletionSession is not null)
        {
            _completeAgainst.Margin = new Thickness(8, 0, 0, 0);
            _completeAgainst.Click += (_, _) => CompletionSourceMenu();
            DockPanel.SetDock(_completeAgainst, Dock.Right);
            actions.Children.Add(_completeAgainst);
        }
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "ScriptWarningBrush");
        _versionText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _deploy.Click += (_, _) => Deploy();
        _compare.Click += (_, _) => CompareCurrent();
        _discard.Click += (_, _) => _ = DiscardDraft();
        _versionBar.Children.Add(_deploy);
        _versionBar.Children.Add(_compare);
        _versionBar.Children.Add(_discard);
        _versionBar.Children.Add(_versionText);
        _duplicate.Click += (_, _) => DuplicateToMine();
        _libraryBar.Children.Add(_duplicate);
        var libraryText = new TextBlock { Text = "A library script: read-only. Duplicate it to change it.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        libraryText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _libraryBar.Children.Add(libraryText);
        var foot = new StackPanel();
        foot.Children.Add(actions);
        foot.Children.Add(_status);
        foot.Children.Add(_hint);
        foot.Children.Add(_versionBar);
        foot.Children.Add(_libraryBar);
        SetRow(foot, 2);
        _scriptCard.Children.Add(foot);

        var host = new Grid();
        host.Children.Add(_scriptCard);
        host.Children.Add(_emptyScript);
        return host;
    }

    private static FrameworkElement Labelled(string label, FrameworkElement control)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 10, 4) };
        var t = new TextBlock { Text = label, Margin = new Thickness(2, 0, 0, 2) };
        t.SetResourceReference(StyleProperty, "Caption");
        p.Children.Add(t);
        p.Children.Add(control);
        return p;
    }

    // ================================================================== loading

    /// <summary>First start: one folder, so a new script has somewhere to go.</summary>
    private static void EnsureDefault()
    {
        if (ScriptStore.Folders().Count == 0) ScriptStore.CreateFolder(null, "Scripts", "Blue");
    }

    /// <summary>Rebuilds the folder tree (after a change elsewhere) and keeps the selection when it still exists.</summary>
    public void Reload(string? select = null)
    {
        Save();
        _loading = true;
        try
        {
            select ??= _folderKey;
            _tree.Items.Clear();
            var folders = ScriptStore.Folders();
            var favourites = ScriptCatalog.Favourites(_host.Ui.FavoriteScripts);
            if (favourites.Count > 0)
            {
                var fav = FavouritesNode();
                fav.IsExpanded = !_host.Ui.ScriptsCollapsedFolders.Contains(FavouritesKey);
                Track(fav, FavouritesKey);
                foreach (var f in favourites)
                {
                    var link = Node("link:" + f.Key, f.Name, f.IsLibrary ? SymbolRegular.Library24 : SymbolRegular.Code24, f.Color, link: true);
                    link.ToolTip = (f.IsLibrary ? f.FolderPath : "My scripts › " + f.FolderPath) + " – a link: the script stays in its folder";
                    link.ContextMenu = FavouriteLinkMenu(f);
                    fav.Items.Add(link);
                }
                _tree.Items.Add(fav);
            }
            _tree.Items.Add(Node(RecentKey, "Recently run", SymbolRegular.History24, null, bold: true));
            var mine = Node(MineKey, "My scripts", SymbolRegular.Folder24, null, bold: true);
            mine.IsExpanded = !_host.Ui.ScriptsCollapsedFolders.Contains(MineKey);
            Track(mine, MineKey);
            mine.ContextMenu = RootMenu();
            mine.AllowDrop = true;
            mine.Drop += (_, e) => DropOn(null, e);
            AddFolders(mine, null, folders, 0);
            _tree.Items.Add(mine);
            if (ScriptCatalog.LibraryAllowed && ScriptLibrary.All.Count > 0)
            {
                var lib = Node(LibraryKey, "Library", SymbolRegular.Library24, null, bold: true);
                lib.ToolTip = "The scripts that come with RemoteHub – read-only";
                lib.IsExpanded = !_host.Ui.ScriptsCollapsedFolders.Contains(LibraryKey);
                Track(lib, LibraryKey);
                lib.Items.Add(Node(StarterKey, "Starter set", SymbolRegular.Sparkle24, null));
                foreach (var (key, name) in ScriptLibrary.Areas.Where(a => ScriptLibrary.All.Any(s => s.Area == a.Key)))
                    lib.Items.Add(Framed(Node("lib:" + key, name, SymbolRegular.Folder24, ScriptLibrary.AreaColor(key)), ScriptLibrary.AreaColor(key)));
                _tree.Items.Add(lib);
            }
            if (!Select(_tree.Items, select)) Select(_tree.Items, MineKey);
            _folderKey = (_tree.SelectedItem as TreeViewItem)?.Tag as string ?? MineKey;
            FillColourFilter();
        }
        finally { _loading = false; }
        ShowList();
    }

    private void AddFolders(TreeViewItem parent, string? parentId, IReadOnlyList<ScriptFolderInfo> folders, int depth)
    {
        if (depth > ScriptTransfer.MaxDepth) return;
        foreach (var f in folders.Where(f => f.ParentId == parentId).OrderBy(f => f.Sort).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = Framed(Node(f.Id, f.Name, SymbolRegular.Folder24, f.Color), f.Color);
            item.IsExpanded = !_host.Ui.ScriptsCollapsedFolders.Contains(f.Id);
            Track(item, f.Id);
            item.ContextMenu = FolderMenu(f);
            item.AllowDrop = true;
            item.Drop += (_, e) => DropOn(f.Id, e);
            item.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(ScriptDragFormat) || e.Data.GetDataPresent(FolderDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            item.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
            item.PreviewMouseMove += (_, e) => StartDrag(e, item, FolderDragFormat, f.Id);
            AddFolders(item, f.Id, folders, depth + 1);
            parent.Items.Add(item);
        }
    }

    private void Track(TreeViewItem item, string key)
    {
        item.Expanded += (_, e) => { if (e.OriginalSource == item && _host.Ui.ScriptsCollapsedFolders.Remove(key)) _host.SaveUi(); };
        item.Collapsed += (_, e) => { if (e.OriginalSource == item && !_host.Ui.ScriptsCollapsedFolders.Contains(key)) { _host.Ui.ScriptsCollapsedFolders.Add(key); _host.SaveUi(); } };
    }

    private static TreeViewItem Node(string key, string name, SymbolRegular icon, string? colour, bool bold = false, bool link = false)
    {
        // As the connection tree draws its rows: a 16 px icon in the item's colour, 8 to the name, 2 above and below.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        if (link)
        {
            var arrow = new SymbolIcon(SymbolRegular.ArrowUpRight16) { FontSize = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            arrow.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(arrow);
        }
        var symbol = new SymbolIcon(icon) { FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (ColorTags.Brush(colour) is { } brush) symbol.Foreground = brush;
        row.Children.Add(symbol);
        row.Children.Add(new TextBlock { Text = name, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new TreeViewItem { Header = row, Tag = key };
        AutomationProperties.SetName(item, name);
        return item;
    }

    /// <summary>The Favourites group's header, as in the connection tree: a filled star in the accent colour.</summary>
    private static TreeViewItem FavouritesNode()
    {
        var item = Node(FavouritesKey, "Favourites", SymbolRegular.Star24, null, bold: true);
        var star = (SymbolIcon)((StackPanel)item.Header).Children[0];
        star.Filled = true;
        star.SetResourceReference(Control.ForegroundProperty, "SystemAccentColorPrimaryBrush");
        return item;
    }

    /// <summary>What the TagBrush bindings of the shared folder template read.</summary>
    private sealed record FolderLook(Brush? TagBrush);

    /// <summary>Shows a folder as the connection tree does – a header row, its contents beside a guide line in its colour (Themes/Styles.xaml).</summary>
    private TreeViewItem Framed(TreeViewItem item, string? colour)
    {
        if (TryFindResource("FolderItemTemplate") is ControlTemplate frame)
        {
            item.Template = frame;
            item.DataContext = new FolderLook(ColorTags.Brush(colour));
        }
        return item;
    }

    private static bool Select(ItemCollection items, string key)
    {
        foreach (var i in items.OfType<TreeViewItem>())
        {
            if (Equals(i.Tag, key)) { i.IsSelected = true; i.BringIntoView(); return true; }
            if (Select(i.Items, key)) { i.IsExpanded = true; return true; }
        }
        return false;
    }

    /// <summary>The folder new scripts and folders go into: the selected one, or the first.</summary>
    private string? SelectedFolderId =>
        _folderKey is FavouritesKey or RecentKey or MineKey || _folderKey.StartsWith("lib", StringComparison.Ordinal) || _folderKey.StartsWith("link:", StringComparison.Ordinal)
            ? null : _folderKey;

    private void SelectFolder(string key)
    {
        if (key.StartsWith("link:", StringComparison.Ordinal))
        {
            // A favourite link opens its script; the list shows the favourites.
            var target = key[5..];
            _folderKey = FavouritesKey;
            ShowList(target);
            if (ScriptCatalog.Find(target) is { } e) Open(e);
            return;
        }
        Save();
        _folderKey = key;
        if (!key.StartsWith("lib", StringComparison.Ordinal) && key is not (FavouritesKey or RecentKey))
        {
            _host.Ui.LastScriptFolderId = key;
            _host.SaveUi();
        }
        ShowList();
    }

    private void FillColourFilter()
    {
        var keep = (_colourFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        _colourFilter.Items.Clear();
        _colourFilter.Items.Add(new ComboBoxItem { Content = "All colours", Tag = "" });
        var used = ScriptCatalog.All().Select(e => e.Color).OfType<string>().Distinct().ToList();
        foreach (var tag in ColorTags.All.Where(t => used.Contains(t.Id)))
            _colourFilter.Items.Add(new ComboBoxItem { Content = Swatched(tag.Label.Split(' ')[0], ColorTags.Frozen(tag.Color)), Tag = tag.Id });
        foreach (var hex in used.Where(u => ColorTags.All.All(t => t.Id != u) && ColorTags.TryParseHex(u, out _)))
            _colourFilter.Items.Add(new ComboBoxItem { Content = Swatched(hex, ColorTags.Brush(hex)!), Tag = hex });
        _colourFilter.SelectedItem = _colourFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep) ?? _colourFilter.Items[0];
    }

    internal static StackPanel Swatched(string text, Brush brush)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text });
        return p;
    }

    // ================================================================== the list

    /// <summary>The scripts the selected folder (with its subfolders), favourites or recent runs show, searched and filtered.</summary>
    private IReadOnlyList<ScriptEntry> Visible()
    {
        var text = _search.Text.Trim();
        IEnumerable<ScriptEntry> entries;
        if (_folderKey == FavouritesKey) entries = ScriptCatalog.Favourites(_host.Ui.FavoriteScripts);
        else if (_folderKey == RecentKey) entries = ScriptStore.RecentlyRun().Select(ScriptCatalog.Find).OfType<ScriptEntry>();
        else if (_folderKey == LibraryKey) entries = ScriptCatalog.LibraryAllowed ? ScriptLibrary.All.Select(ScriptCatalog.FromLibrary) : [];
        else if (_folderKey == StarterKey) entries = ScriptCatalog.LibraryAllowed ? ScriptLibrary.All.Where(s => ScriptLibrary.StarterSet.Contains(s.Id)).Select(ScriptCatalog.FromLibrary) : [];
        else if (_folderKey.StartsWith("lib:", StringComparison.Ordinal)) entries = ScriptCatalog.LibraryAllowed ? ScriptLibrary.All.Where(s => s.Area == _folderKey[4..]).Select(ScriptCatalog.FromLibrary) : [];
        else
        {
            var folders = ScriptStore.Folders().ToDictionary(f => f.Id);
            var inside = _folderKey == MineKey ? folders.Keys.ToHashSet() : ScriptStore.FolderAndBelow(_folderKey).ToHashSet();
            entries = ScriptStore.Scripts().Where(s => inside.Contains(s.FolderId)).Select(s => ScriptCatalog.Own(s, folders));
        }
        var list = entries.ToList();
        if (text.Length > 0)
        {
            var hits = ScriptCatalog.Search(text).Select(e => e.Key).ToHashSet();
            list = list.Where(e => hits.Contains(e.Key)).ToList();
        }
        if ((_colourFilter.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } colour) list = list.Where(e => e.Color == colour).ToList();
        if (_folderKey is FavouritesKey or RecentKey) return list; // pin order, run order
        return _host.Ui.ScriptsSort switch
        {
            "modified" => list.OrderByDescending(e => e.ModifiedUtc).ToList(),
            "run" => OrderByLastRun(list),
            _ => list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        };
    }

    private static List<ScriptEntry> OrderByLastRun(List<ScriptEntry> list)
    {
        var order = ScriptStore.RecentlyRun(int.MaxValue).Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return list.OrderBy(e => order.GetValueOrDefault(e.Key, int.MaxValue)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ShowList(string? select = null)
    {
        _loading = true;
        try
        {
            var keep = select ?? _current?.Key;
            _list.Items.Clear();
            var entries = Visible();
            foreach (var e in entries) _list.Items.Add(Row(e));
            _listHeader.Text = (_folderKey switch
            {
                FavouritesKey => "Favourites",
                RecentKey => "Recently run",
                MineKey => "My scripts",
                LibraryKey => "Library",
                StarterKey => "Starter set",
                _ when _folderKey.StartsWith("lib:", StringComparison.Ordinal) => ScriptLibrary.Areas.FirstOrDefault(a => a.Key == _folderKey[4..]).Name ?? "Library",
                _ => ScriptStore.Folders().FirstOrDefault(f => f.Id == _folderKey)?.Name ?? "Scripts",
            }) + (entries.Count == 0 ? "" : $" ({entries.Count})");
            if (entries.Count == 0)
            {
                var hint = EmptyHint(_search.Text.Trim().Length > 0 ? "No script matches." : _folderKey == FavouritesKey ? "Pin a script with Add to favourites." : _folderKey == RecentKey ? "Scripts you run show here." : "No scripts here yet.");
                _list.Items.Add(new ListBoxItem { Content = hint, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Center });
            }
            if (keep is not null && _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ScriptEntry e && e.Key == keep) is { } item) _list.SelectedItem = item;
        }
        finally { _loading = false; }
    }

    private ListBoxItem Row(ScriptEntry e)
    {
        var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top, Background = ColorTags.Brush(e.Color) ?? Brushes.Transparent };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        if (_host.Ui.FavoriteScripts.Contains(e.Key))
        {
            var star = new SymbolIcon(SymbolRegular.Star24) { FontSize = 12, Filled = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 3, 0, 0), ToolTip = "In Favourites" };
            star.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            DockPanel.SetDock(star, Dock.Right);
            row.Children.Add(star);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = e.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var caption = new TextBlock { Text = Caption(e), Style = TryFindResource("Caption") as Style, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(caption);
        row.Children.Add(text);
        var item = new ListBoxItem { Content = row, Tag = e, ToolTip = (e.IsLibrary ? e.FolderPath : "My scripts › " + e.FolderPath) };
        AutomationProperties.SetName(item, e.Name);
        return item;
    }

    private static string Caption(ScriptEntry e)
    {
        var parts = new List<string> { e.IsPowerShell ? ScriptLabels.Context(e.RunContext) : ScriptLabels.Language(e.Language) };
        if (e.CatalogId is { } id) parts.Add(id);
        if (!e.IsLibrary)
        {
            if (!e.IsDeployed) parts.Add("Not deployed yet");
            else if (e.HasDraftChanges) parts.Add("Draft");
        }
        return string.Join(" · ", parts);
    }

    private IReadOnlyList<ScriptEntry> SelectedEntries() => _list.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<ScriptEntry>().ToList();

    private ContextMenu SortMenu()
    {
        var m = new ContextMenu();
        void Add(string label, string key)
        {
            var i = new MenuItem { Header = label, IsCheckable = true, IsChecked = _host.Ui.ScriptsSort == key };
            i.Click += (_, _) => { _host.Ui.ScriptsSort = key; _host.SaveUi(); ShowList(); };
            m.Items.Add(i);
        }
        Add("Name", "name");
        Add("Last changed", "modified");
        Add("Last run", "run");
        return m;
    }

    // ================================================================== the script

    private void Open(ScriptEntry e)
    {
        Save();
        _current = e;
        _loading = true;
        try
        {
            var own = e.Own;
            var draft = own is not null ? ScriptStore.Draft(own.Id) ?? ScriptContent.Empty(own.Language) : e.Library!.Content;
            _name.Text = e.Name;
            _name.IsReadOnly = e.IsLibrary;
            _description.Text = draft.Description;
            _description.IsReadOnly = e.IsLibrary;
            _editor.SetText(draft.Body, e.Language);
            _editor.IsReadOnly = e.IsLibrary;
            _language.Visibility = e.IsPowerShell ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)_language.Parent).Visibility = _language.Visibility;
            _language.SelectedItem = _language.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == e.Language);
            _psSettings.Visibility = e.IsPowerShell ? Visibility.Visible : Visibility.Collapsed;
            Pick(_context, draft.RunContext);
            Pick(_risk, draft.Risk);
            Pick(_os, draft.OsScope);
            Pick(_timeout, draft.TimeoutSec ?? ScriptStore.DefaultTimeoutSec);
            foreach (var box in new[] { _context, _risk, _os, _timeout }) box.IsEnabled = !e.IsLibrary;
            _colourButton.IsEnabled = !e.IsLibrary;
            _colourDot.Background = ColorTags.Brush(e.Color) ?? Brushes.Transparent;
            _dirty = false;
        }
        finally { _loading = false; }
        _scriptCard.Visibility = Visibility.Visible;
        _emptyScript.Visibility = Visibility.Collapsed;
        _host.Ui.LastScriptId = e.Key;
        _host.SaveUi();
        UpdateStatus();
    }

    private static void Pick(ComboBox box, object? tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));
        if (box.SelectedItem is null && tag is int seconds)
        {
            // A timeout from an import that isn't one of the choices is kept and shown.
            var item = new ComboBoxItem { Content = ScriptLabels.Timeout(seconds), Tag = seconds };
            box.Items.Add(item);
            box.SelectedItem = item;
        }
    }

    private void CloseScript()
    {
        _current = null;
        _scriptCard.Visibility = Visibility.Collapsed;
        _emptyScript.Visibility = Visibility.Visible;
    }

    private void OnEdited()
    {
        if (_loading || _current?.Own is null) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        ShowStatus("Editing…");
    }

    private void OnSettingChanged()
    {
        if (_loading || _current?.Own is null) return;
        _dirty = true;
        Save();
    }

    private ScriptContent CurrentContent() => new(_editor.Text, _description.Text.Trim(),
        _current?.IsPowerShell == true ? (_context.SelectedItem as ComboBoxItem)?.Tag as string : null,
        _current?.IsPowerShell == true ? (_risk.SelectedItem as ComboBoxItem)?.Tag as string : null,
        _current?.IsPowerShell == true ? (_os.SelectedItem as ComboBoxItem)?.Tag as string : null,
        _current?.IsPowerShell == true ? (_timeout.SelectedItem as ComboBoxItem)?.Tag as int? : null);

    /// <summary>Writes the draft (and the name) when something changed. Never touches a deployed version.</summary>
    public void Save()
    {
        _saveTimer.Stop();
        if (!_dirty || _current?.Own is not { } s) return;
        _dirty = false;
        try
        {
            ScriptStore.SaveDraft(s.Id, CurrentContent());
            if (_name.Text.Trim() != s.Name) ScriptStore.RenameScript(s.Id, _name.Text);
            RefreshCurrent();
        }
        catch (ScriptTooLargeException ex) { ShowError(ex.Message); }
        catch (Exception ex)
        {
            DiagLog.Write("Scripts: saving the draft failed: " + ex.GetType().Name);
            ShowError("The draft couldn't be saved – it will be tried again.");
            _dirty = true;
            _saveTimer.Start();
        }
    }

    /// <summary>Reads the open script's state again (after a save, a deploy or a restore) and updates its row.</summary>
    private void RefreshCurrent()
    {
        if (_current?.Own is not { } s || ScriptCatalog.Find(s.Id) is not { } e) return;
        _current = e;
        var item = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ScriptEntry x && x.Key == e.Key);
        if (item is not null)
        {
            var index = _list.Items.IndexOf(item);
            var selected = item.IsSelected;
            _loading = true;
            try
            {
                _list.Items[index] = Row(e);
                if (selected) ((ListBoxItem)_list.Items[index]).IsSelected = true;
            }
            finally { _loading = false; }
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_current is not { } e) return;
        var errors = _editor.Errors;
        _status.Text = e.IsLibrary
            ? $"{e.CatalogId} · {ScriptLabels.Risk(e.Risk)} · {ScriptLabels.Os(e.OsScope)}"
            : errors.Count > 0 ? $"{(errors.Count == 1 ? "1 syntax error" : $"{errors.Count} syntax errors")} – line {errors[0].Line}: {errors[0].Message}"
            : _dirty ? "Editing…" : "Draft saved";
        _status.SetResourceReference(TextBlock.ForegroundProperty, errors.Count > 0 && !e.IsLibrary ? "ScriptErrorBrush" : "TextFillColorSecondaryBrush");
        _versionBar.Visibility = e.IsLibrary ? Visibility.Collapsed : Visibility.Visible;
        _libraryBar.Visibility = e.IsLibrary ? Visibility.Visible : Visibility.Collapsed;
        _duplicate.Visibility = Features.IsAllowed(Feature.Scripts) ? Visibility.Visible : Visibility.Collapsed;
        var runAllowed = e.IsPowerShell && Features.IsAllowed(Feature.ScriptRun);
        _run.Visibility = runAllowed ? Visibility.Visible : Visibility.Collapsed;
        _run.IsEnabled = e.IsDeployed;
        _run.ToolTip = e.IsDeployed ? "Run the deployed version on a device (F5)" : "Mark the script Ready to deploy first: only deployed versions run on devices.";
        _completeAgainst.Visibility = e.IsPowerShell && !e.IsLibrary && _host.OpenCompletionSession is not null ? Visibility.Visible : Visibility.Collapsed;
        if (e.Own is not { } s) return;
        var versions = ScriptStore.Versions(s.Id);
        _compare.IsEnabled = versions.Count > 1 || (versions.Count == 1 && s.HasDraftChanges);
        _discard.IsEnabled = s.HasDraftChanges;
        _deploy.IsEnabled = !s.IsDeployed || s.HasDraftChanges;
        _versionText.Text = !s.IsDeployed
            ? versions.Count > 0 ? $"Not deployed here · {versions.Count} version{(versions.Count == 1 ? "" : "s")} from the import" : "Not deployed yet"
            : s.HasDraftChanges ? $"Draft saved · changes since version {s.DeployedVersion}"
            : $"Version {s.DeployedVersion} · deployed {versions.FirstOrDefault()?.DeployedUtc.ToLocalTime():d}";
    }

    private void UseSynopsis()
    {
        if (_current?.Own is null) return;
        if (ScriptHeader.Synopsis(_editor.Text) is { } s) _description.Text = s;
        else ShowError("The script has no .SYNOPSIS in its comment-based help.");
    }

    private void ShowColourMenu(FrameworkElement target)
    {
        if (_current?.Own is not { } s) return;
        var m = new ContextMenu();
        var colour = ColorMenu.Build(_host.Owner, s.Color, c => { ScriptStore.SetScriptColor(s.Id, c); RefreshCurrent(); _colourDot.Background = ColorTags.Brush(_current?.Color) ?? Brushes.Transparent; FillColourFilter(); }, "Inherit from the folder");
        foreach (var item in colour.Items.OfType<object>().ToList()) { colour.Items.Remove(item); m.Items.Add(item); }
        m.PlacementTarget = target;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    /// <summary>A failure on the status line, in the error colour as Notes shows its failures.</summary>
    private void ShowError(string text)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "ScriptErrorBrush");
    }

    private void CopyScript()
    {
        NoteEditor.SetClipboardText(_editor.Text);
        ShowStatus("Copied.");
    }

    // ================================================================== versions

    private void Deploy()
    {
        Save();
        if (_current?.Own is not { } s) return;
        var draft = ScriptStore.Draft(s.Id)!;
        var w = new DeployWindow(s.Name, s.Language, ScriptStore.Deployed(s.Id), draft, s.IsPowerShell ? PsCompletion.Errors(draft.Body) : [], ScriptEditor.SecretHint(draft.Body)) { Owner = _host.Owner };
        if (w.ShowDialog() != true) return;
        var version = ScriptStore.Deploy(s.Id, w.Note, Environment.UserDomainName + "\\" + Environment.UserName);
        AuditLog.Write(AuditEvents.ScriptDeployed, null, s.Id, $"Version {version}, SHA-256 {Convert.ToHexString(draft.Sha256())}");
        RefreshCurrent();
        ShowStatus($"Version {version} deployed.");
    }

    private async Task DiscardDraft()
    {
        if (_current?.Own is not { } s || !s.HasDraftChanges) return;
        if (!await Dialogs.Confirm(_host.Owner, "Discard draft", $"Put the draft back to version {s.DeployedVersion}? The changes since then are lost.", "Discard")) return;
        _dirty = false;
        ScriptStore.DiscardDraft(s.Id);
        if (ScriptCatalog.Find(s.Id) is { } e) Open(e);
        RefreshCurrent();
    }

    private void CompareCurrent()
    {
        Save();
        if (_current?.Own is not { } s) return;
        var versions = ScriptStore.Versions(s.Id).OrderBy(v => v.Version).ToList();
        var choices = versions.Select(v => ($"Version {v.Version} · {v.DeployedUtc.ToLocalTime():d}", v.Content)).ToList();
        var draft = ScriptStore.Draft(s.Id)!;
        var hasDraft = s.HasDraftChanges || !s.IsDeployed;
        if (hasDraft) choices.Add(("Draft", draft));
        if (choices.Count < 2) { ShowStatus("There is nothing to compare yet: deploy a version first."); return; }
        // From the editor with a changed draft: the latest version against the draft; otherwise oldest against latest.
        var (left, right) = hasDraft && versions.Count > 0 ? (versions.Count - 1, choices.Count - 1) : (0, choices.Count - 1);
        new CompareWindow(s.Name, s.Language, choices, left, right, _host.Ui, _host.SaveUi) { Owner = _host.Owner }.Show();
    }

    private void ShowVersions()
    {
        Save();
        if (_current?.Own is not { } s) return;
        var w = new VersionsWindow(s, ScriptStore.Draft(s.Id)!, _host.Ui, _host.SaveUi,
            Features.IsAllowed(Feature.ScriptExport) ? v => ExportVersion(s, v) : null) { Owner = _host.Owner };
        w.ShowDialog();
        if (w.Restored && ScriptCatalog.Find(s.Id) is { } e) Open(e);
        RefreshCurrent();
    }

    private void ExportVersion(ScriptInfo s, ScriptVersion v)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = ScriptTransfer.SafeName($"{s.Name} (version {v.Version})"),
            Filter = s.IsPowerShell ? "PowerShell script (*.ps1)|*.ps1" : "SQL script (*.sql)|*.sql",
        };
        if (dlg.ShowDialog(_host.Owner) != true) return;
        File.WriteAllText(dlg.FileName, ScriptHeader.Write(v.Content.Body, v.Content, s.Language, s.LibraryId), new System.Text.UTF8Encoding(true));
        AuditLog.Write(AuditEvents.ScriptsExported, null, null, "1 script as a file");
    }

    // ================================================================== running

    private void RunCurrent(bool draft)
    {
        Save();
        if (_current is not { IsPowerShell: true } e) return;
        if (!draft && !e.IsDeployed) { ShowStatus("Mark the script Ready to deploy first: only deployed versions run on devices."); return; }
        _host.RunOn(e, draft);
    }

    private void CompletionSourceMenu()
    {
        var m = new ContextMenu();
        var local = new MenuItem { Header = "This PC's PowerShell", IsCheckable = true, IsChecked = _remote is null };
        local.Click += (_, _) => { CloseRemote(); UpdateCompletionButton(); };
        m.Items.Add(local);
        var device = new MenuItem { Header = _remoteName is null ? "A device's session…" : $"{_remoteName}'s session", IsCheckable = true, IsChecked = _remote is not null };
        device.Click += async (_, _) =>
        {
            if (_host.OpenCompletionSession is null) return;
            _completeAgainst.IsEnabled = false;
            try
            {
                if (await _host.OpenCompletionSession() is { } opened)
                {
                    CloseRemote();
                    _remote = opened.Runspace;
                    _remoteName = opened.Name;
                }
            }
            finally { _completeAgainst.IsEnabled = true; UpdateCompletionButton(); }
        };
        m.Items.Add(device);
        m.PlacementTarget = _completeAgainst;
        m.Placement = PlacementMode.Top;
        m.IsOpen = true;
    }

    private void UpdateCompletionButton() => _completeAgainst.Content = _remoteName is null ? "Complete from this PC" : $"Complete from {_remoteName}";

    private void CloseRemote()
    {
        var rs = _remote;
        _remote = null;
        _remoteName = null;
        if (rs is not null) _ = Task.Run(() => { try { rs.Dispose(); } catch { /* a broken session */ } });
    }

    // ================================================================== creating, renaming, deleting

    private void NewScript(string language)
    {
        Save();
        var folder = SelectedFolderId ?? (_current?.Own?.FolderId) ?? ScriptStore.Folders().FirstOrDefault()?.Id;
        if (folder is null) { EnsureDefault(); folder = ScriptStore.Folders().First().Id; }
        var name = language == ScriptStore.PowerShell ? "New script" : "New query";
        var id = ScriptStore.CreateScript(folder, name, language);
        AuditLog.Write(AuditEvents.ScriptAdded, null, id, language);
        _search.Text = "";
        if (_folderKey != folder && _folderKey != MineKey) { _folderKey = folder; Reload(folder); }
        ShowList(id);
        if (ScriptCatalog.Find(id) is { } e) Open(e);
        _name.Focus();
        _name.SelectAll();
    }

    private void NewFolder(string? parentId)
    {
        var dlg = new InputDialog("New folder", "Name of the folder", "New folder") { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        var id = ScriptStore.CreateFolder(parentId, dlg.Value, NextColour());
        if (parentId is not null) _host.Ui.ScriptsCollapsedFolders.Remove(parentId);
        Reload(id);
    }

    /// <summary>New folders take the next colour of the palette in turn, as Notes sections do.</summary>
    private static string NextColour()
    {
        var palette = ColorTags.All.Select(t => t.Id).ToList();
        var last = ScriptStore.Folders().LastOrDefault()?.Color;
        var at = last is null ? -1 : palette.IndexOf(last);
        return palette[(at + 1) % palette.Count];
    }

    private void RenameFolder(ScriptFolderInfo f)
    {
        var dlg = new InputDialog("Rename folder", "Name of the folder", f.Name) { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        ScriptStore.RenameFolder(f.Id, dlg.Value);
        Reload();
    }

    private async void DeleteFolder(ScriptFolderInfo f)
    {
        var ids = ScriptStore.FolderAndBelow(f.Id).ToHashSet();
        var scripts = ScriptStore.Scripts().Count(s => ids.Contains(s.FolderId));
        if (!await Dialogs.Confirm(_host.Owner, "Delete folder",
                $"Delete the folder \"{f.Name}\" with {(ids.Count > 1 ? $"{ids.Count - 1} folders and " : "")}{scripts} scripts?\n\nIt stays in Recently deleted for {ScriptStore.KeepDeletedDays} days, with every version.", "Delete")) return;
        Save();
        ScriptStore.Delete(ScriptStore.FolderKind, f.Id);
        if (_current?.Own is { } s && ids.Contains(s.FolderId)) CloseScript();
        Reload(MineKey);
        _undoBar.Show("Folder deleted", () => { ScriptStore.Restore(ScriptStore.FolderKind, f.Id); AuditLog.Write(AuditEvents.ScriptRestored, null, f.Id, ScriptStore.FolderKind); Reload(f.Id); });
    }

    private void DeleteScripts(IReadOnlyList<ScriptEntry> entries)
    {
        var own = entries.Where(e => e.Own is not null).ToList();
        if (own.Count == 0) return;
        Save();
        foreach (var e in own)
        {
            ScriptStore.Delete(ScriptStore.ScriptKind, e.Key);
            AuditLog.Write(AuditEvents.ScriptDeleted, null, e.Key, ScriptStore.ScriptKind);
        }
        if (_current is { } c && own.Any(e => e.Key == c.Key)) CloseScript();
        ShowList();
        _undoBar.Show(own.Count == 1 ? "Script deleted" : $"{own.Count} scripts deleted", () =>
        {
            foreach (var e in own)
            {
                ScriptStore.Restore(ScriptStore.ScriptKind, e.Key);
                AuditLog.Write(AuditEvents.ScriptRestored, null, e.Key, ScriptStore.ScriptKind);
            }
            ShowList(own[0].Key);
            if (ScriptCatalog.Find(own[0].Key) is { } back) Open(back);
        });
    }

    private void ShowRecentlyDeleted()
    {
        Save();
        var w = new RecentlyDeletedWindow(new ScriptsDeletedSource()) { Owner = _host.Owner };
        w.ShowDialog();
        if (w.Changed) Reload();
    }

    private void Duplicate(ScriptEntry e)
    {
        Save();
        string id;
        if (e.Own is { } s)
        {
            var draft = ScriptStore.Draft(s.Id)!;
            id = ScriptStore.CreateScript(s.FolderId, s.Name + " (copy)", s.Language, draft, s.Color, s.LibraryId);
        }
        else
        {
            // A library script becomes the user's own, already deployed as version 1: the library is part of the app (§4.10).
            var folder = SelectedFolderId ?? ScriptStore.Folders().FirstOrDefault()?.Id ?? ScriptStore.CreateFolder(null, "Scripts", "Blue");
            id = ScriptStore.CreateScript(folder, e.Name, ScriptStore.PowerShell, e.Library!.Content, null, e.Library.Id);
            ScriptStore.Deploy(id, $"From the RemoteHub library ({AppVersion.Current})", "RemoteHub");
            _folderKey = folder;
            Reload(folder);
        }
        AuditLog.Write(AuditEvents.ScriptAdded, null, id, e.IsLibrary ? "library " + e.CatalogId : "duplicate");
        ShowList(id);
        if (ScriptCatalog.Find(id) is { } copy) Open(copy);
    }

    private void DuplicateToMine()
    {
        if (_current is { IsLibrary: true } e) Duplicate(e);
    }

    private void MoveTo(IReadOnlyList<ScriptEntry> entries)
    {
        var own = entries.Where(e => e.Own is not null).ToList();
        if (own.Count == 0) return;
        var folders = ScriptStore.Folders().ToDictionary(f => f.Id);
        var targets = folders.Values.Select(f => new NoteLinkTarget(f.Id, f.Name, ScriptCatalog.FolderPath(f.Id, folders), true))
            .OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        var picker = new FolderPickerWindow(targets) { Owner = _host.Owner };
        if (picker.ShowDialog() != true || picker.Selected is not { } folder) return;
        Save();
        foreach (var e in own) ScriptStore.MoveScript(e.Key, folder);
        ShowList();
    }

    private void ToggleFavourite(IReadOnlyList<ScriptEntry> entries)
    {
        var pins = _host.Ui.FavoriteScripts;
        var pin = entries.Any(e => !pins.Contains(e.Key));
        foreach (var e in entries)
        {
            if (pin && !pins.Contains(e.Key)) pins.Add(e.Key);
            else if (!pin) pins.Remove(e.Key);
        }
        _host.SaveUi();
        Reload();
    }

    // ================================================================== menus

    private ContextMenu RootMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("New folder…", SymbolRegular.FolderAdd24, () => NewFolder(null)));
        if (Features.IsAllowed(Feature.ScriptImport)) m.Items.Add(RemoteToolChrome.Item("Import…", SymbolRegular.ArrowImport24, () => _ = Import(null, null)));
        if (Features.IsAllowed(Feature.ScriptExport))
            m.Items.Add(RemoteToolChrome.Item("Export all scripts…", SymbolRegular.ArrowExportLtr24, () => _ = Export(ScriptCatalog.All(includeLibrary: false))));
        return m;
    }

    private ContextMenu FolderMenu(ScriptFolderInfo f)
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("New PowerShell script", SymbolRegular.WindowConsole20, () => { SelectFolder(f.Id); NewScript(ScriptStore.PowerShell); }));
        m.Items.Add(RemoteToolChrome.Item("New SQL script", SymbolRegular.Database24, () => { SelectFolder(f.Id); NewScript("tsql"); }));
        m.Items.Add(RemoteToolChrome.Item("New folder…", SymbolRegular.FolderAdd24, () => NewFolder(f.Id)));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Rename…", SymbolRegular.Rename24, () => RenameFolder(f), gesture: "F2"));
        m.Items.Add(ColorMenu.Build(_host.Owner, f.Color, c => { ScriptStore.SetFolderColor(f.Id, c); Reload(); }));
        if (f.ParentId is not null) m.Items.Add(RemoteToolChrome.Item("Move to the top", SymbolRegular.ArrowUp24, () => { ScriptStore.MoveFolder(f.Id, null); Reload(); }));
        m.Items.Add(new Separator());
        if (Features.IsAllowed(Feature.ScriptImport)) m.Items.Add(RemoteToolChrome.Item("Import…", SymbolRegular.ArrowImport24, () => _ = Import(f.Id, null)));
        if (Features.IsAllowed(Feature.ScriptExport))
        {
            var ids = ScriptStore.FolderAndBelow(f.Id).ToHashSet();
            m.Items.Add(RemoteToolChrome.Item("Export folder…", SymbolRegular.ArrowExportLtr24, () => _ = Export(ScriptCatalog.All(includeLibrary: false).Where(e => ids.Contains(e.Own!.FolderId)).ToList())));
        }
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete…", SymbolRegular.Delete24, () => DeleteFolder(f), gesture: "Del"));
        return m;
    }

    private ContextMenu FavouriteLinkMenu(ScriptEntry e)
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Open", SymbolRegular.Open24, () => Open(e)));
        if (e.IsPowerShell && Features.IsAllowed(Feature.ScriptRun)) m.Items.Add(RemoteToolChrome.Item("Run on…", SymbolRegular.Play24, () => { Open(e); RunCurrent(draft: false); }, e.IsDeployed));
        m.Items.Add(RemoteToolChrome.Item("Remove from favourites", SymbolRegular.StarOff24, () => ToggleFavourite([e]), gesture: "Del"));
        return m;
    }

    private ContextMenu? ListMenu()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0) return null;
        var own = entries.Where(e => e.Own is not null).ToList();
        var m = new ContextMenu();
        if (entries.Count == 1)
        {
            var e = entries[0];
            if (e.IsPowerShell && Features.IsAllowed(Feature.ScriptRun))
                m.Items.Add(RemoteToolChrome.Item("Run on…", SymbolRegular.Play24, () => { Open(e); RunCurrent(draft: false); }, e.IsDeployed, "F5"));
            m.Items.Add(RemoteToolChrome.Item(e.IsLibrary ? "Duplicate to My scripts" : "Duplicate", SymbolRegular.Copy24, () => Duplicate(e)));
            if (e.Own is not null)
                m.Items.Add(RemoteToolChrome.Item("Rename", SymbolRegular.Rename24, () => { Open(e); _name.Focus(); _name.SelectAll(); }, gesture: "F2"));
        }
        var allPinned = entries.All(e => _host.Ui.FavoriteScripts.Contains(e.Key));
        m.Items.Add(RemoteToolChrome.Item(allPinned ? "Remove from favourites" : "Add to favourites", allPinned ? SymbolRegular.StarOff24 : SymbolRegular.Star24, () => ToggleFavourite(entries)));
        if (own.Count > 0)
        {
            var current = own.Select(e => e.Own!.Color).Distinct().Count() == 1 ? own[0].Own!.Color : null;
            m.Items.Add(ColorMenu.Build(_host.Owner, current, c => { foreach (var e in own) ScriptStore.SetScriptColor(e.Key, c); ShowList(); FillColourFilter(); }, "Inherit from the folder"));
            m.Items.Add(RemoteToolChrome.Item("Move to…", SymbolRegular.FolderArrowRight24, () => MoveTo(own)));
        }
        if (Features.IsAllowed(Feature.ScriptExport))
            m.Items.Add(RemoteToolChrome.Item(entries.Count == 1 ? "Export…" : $"Export {entries.Count} scripts…", SymbolRegular.ArrowExportLtr24, () => _ = Export(entries)));
        if (own.Count > 0)
        {
            m.Items.Add(new Separator());
            m.Items.Add(RemoteToolChrome.Item(own.Count == 1 ? "Delete" : $"Delete {own.Count} scripts", SymbolRegular.Delete24, () => DeleteScripts(own), gesture: "Del"));
        }
        return m;
    }

    private ContextMenu ScriptMenu()
    {
        var m = new ContextMenu();
        if (_current is not { } e) return m;
        m.Items.Add(RemoteToolChrome.Item("Copy", SymbolRegular.Copy24, CopyScript));
        if (Features.IsAllowed(Feature.ScriptExport)) m.Items.Add(RemoteToolChrome.Item("Save as file…", SymbolRegular.Save24, () => _ = Export([e])));
        m.Items.Add(RemoteToolChrome.Item(e.IsLibrary ? "Duplicate to My scripts" : "Duplicate", SymbolRegular.DocumentCopy24, () => Duplicate(e)));
        var pinned = _host.Ui.FavoriteScripts.Contains(e.Key);
        m.Items.Add(RemoteToolChrome.Item(pinned ? "Remove from favourites" : "Add to favourites", pinned ? SymbolRegular.StarOff24 : SymbolRegular.Star24, () => ToggleFavourite([e])));
        if (e.Own is not null)
        {
            m.Items.Add(RemoteToolChrome.Item("Move to…", SymbolRegular.FolderArrowRight24, () => MoveTo([e])));
            m.Items.Add(new Separator());
            m.Items.Add(RemoteToolChrome.Item("Versions…", SymbolRegular.BranchFork24, ShowVersions));
            if (e.IsPowerShell && Features.IsAllowed(Feature.ScriptDraftRun))
                m.Items.Add(RemoteToolChrome.Item("Test draft on…", SymbolRegular.BeakerEdit24, () => RunCurrent(draft: true)));
        }
        m.Items.Add(RemoteToolChrome.Item("Run history…", SymbolRegular.History24, ShowRunHistory));
        if (e.Own is not null)
        {
            m.Items.Add(new Separator());
            m.Items.Add(RemoteToolChrome.Item("Delete", SymbolRegular.Delete24, () => DeleteScripts([e]), gesture: "Del"));
        }
        return m;
    }

    private void ShowRunHistory()
    {
        if (_current is not { } e) return;
        new ScriptRunHistoryWindow(e.Key, e.Name, null, _host.ConnectionName, _host.Ui, _host.SaveUi) { Owner = _host.Owner }.ShowDialog();
    }

    // ================================================================== import and export

    /// <summary>Imports files, folders or a package into a folder (null = the top), after the preview (§4.7).</summary>
    private async Task Import(string? targetFolderId, IReadOnlyList<string>? paths)
    {
        if (!Features.IsAllowed(Feature.ScriptImport)) { await Dialogs.Info(_host.Owner, "Scripts", Features.WhyNot(Feature.ScriptImport)!); return; }
        if (paths is null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "Scripts and script packages (*.ps1;*.sql;*.rhscripts)|*.ps1;*.sql;*.rhscripts|All files (*.*)|*.*",
                Title = "Import scripts",
            };
            if (dlg.ShowDialog(_host.Owner) != true) return;
            paths = dlg.FileNames;
        }
        ScriptImportPlan plan;
        try
        {
            var packages = paths.Where(p => p.EndsWith(ScriptTransfer.PackageExtension, StringComparison.OrdinalIgnoreCase)).ToList();
            if (packages.Count > 1 || (packages.Count == 1 && paths.Count > 1))
            {
                await Dialogs.Info(_host.Owner, "Import scripts", "Import one script package at a time, on its own.");
                return;
            }
            if (packages.Count == 1)
            {
                string? password = null;
                if (ScriptTransfer.IsProtected(packages[0]))
                {
                    var pw = new PasswordDialog(confirm: false, title: "Script package", message: "Enter the password the script package was protected with.", okText: "Open") { Owner = _host.Owner };
                    if (pw.ShowDialog() != true) return;
                    password = pw.Password;
                }
                plan = await Task.Run(() => ScriptTransfer.ReadPackage(packages[0], password));
            }
            else plan = await Task.Run(() => ScriptTransfer.ReadFiles(paths));
        }
        catch (ScriptImportException ex) { await Dialogs.Info(_host.Owner, "Import scripts", ex.Message); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Info(_host.Owner, "Import scripts", "The files couldn't be read: " + ex.Message);
            return;
        }
        if (plan.Data.Scripts.Count == 0)
        {
            await Dialogs.Info(_host.Owner, "Import scripts", plan.Skipped > 0 ? "There are no .ps1 or .sql files (of at most 1 MB) in what was chosen." : "There are no scripts to import.");
            return;
        }
        var targetName = targetFolderId is null ? "My scripts" : ScriptCatalog.FolderPath(targetFolderId, ScriptStore.Folders().ToDictionary(f => f.Id));
        var preview = new ImportPreviewWindow(plan, targetFolderId, targetName) { Owner = _host.Owner };
        if (preview.ShowDialog() != true) return;
        var (data, choices) = preview.Result();
        var count = ScriptStore.Import(data, targetFolderId, plan.FromPackage && preview.KeepDeployed, (s, _) => choices.GetValueOrDefault(s.Id, ImportChoice.KeepBoth));
        AuditLog.Write(AuditEvents.ScriptsImported, null, null, $"{count} scripts from {(plan.FromPackage ? "a script package" : "files")}");
        Reload(targetFolderId ?? MineKey);
        ShowStatus($"{count} script{(count == 1 ? "" : "s")} imported as drafts.");
    }

    private void DropFiles(string? folderId, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _ = Import(folderId, files);
        e.Handled = true;
    }

    /// <summary>Exports scripts as files into a folder, or as one script package (§4.7).</summary>
    private async Task Export(IReadOnlyList<ScriptEntry> entries)
    {
        if (!Features.IsAllowed(Feature.ScriptExport)) { await Dialogs.Info(_host.Owner, "Scripts", Features.WhyNot(Feature.ScriptExport)!); return; }
        Save();
        if (entries.Count == 0) { await Dialogs.Info(_host.Owner, "Export scripts", "There are no scripts to export here."); return; }
        var w = new ExportWindow(entries.Count, entries.Any(e => e.HasDraftChanges || (e.Own is not null && !e.IsDeployed))) { Owner = _host.Owner };
        if (w.ShowDialog() != true) return;
        try
        {
            if (w.AsPackage)
            {
                string? password = null;
                if (w.Protect)
                {
                    var pw = new PasswordDialog(confirm: true, title: "Protect script package", message: "The script package is encrypted with this password (AES-256). Use at least 8 characters.", okText: "Protect") { Owner = _host.Owner };
                    if (pw.ShowDialog() != true) return;
                    password = pw.Password;
                }
                var save = new Microsoft.Win32.SaveFileDialog { FileName = "Scripts" + ScriptTransfer.PackageExtension, Filter = "Script package (*.rhscripts)|*.rhscripts" };
                if (save.ShowDialog(_host.Owner) != true) return;
                var data = PackageData(entries);
                await Task.Run(() => ScriptTransfer.WritePackage(data, save.FileName, password));
                AuditLog.Write(AuditEvents.ScriptsExported, null, null, $"{data.Scripts.Count} scripts as a script package{(password is null ? " without a password" : "")}");
                ShowStatus($"{data.Scripts.Count} script{(data.Scripts.Count == 1 ? "" : "s")} exported.");
                return;
            }
            var items = entries.Select(e => FileItem(e, w.UseDrafts)).ToList();
            string target;
            if (items.Count == 1)
            {
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = ScriptTransfer.SafeName(items[0].Name),
                    Filter = items[0].Language == ScriptStore.PowerShell ? "PowerShell script (*.ps1)|*.ps1" : "SQL script (*.sql)|*.sql",
                };
                if (save.ShowDialog(_host.Owner) != true) return;
                var one = items[0];
                File.WriteAllText(save.FileName, ScriptHeader.Write(one.Content.Body, one.Content, one.Language, one.LibraryId), new System.Text.UTF8Encoding(true));
                target = save.FileName;
            }
            else
            {
                var folder = new Microsoft.Win32.OpenFolderDialog { Title = "Export scripts into this folder" };
                if (folder.ShowDialog(_host.Owner) != true) return;
                target = folder.FolderName;
                await Task.Run(() => ScriptTransfer.WriteFiles(items, target));
            }
            AuditLog.Write(AuditEvents.ScriptsExported, null, null, $"{items.Count} scripts as files");
            ShowStatus($"{items.Count} script{(items.Count == 1 ? "" : "s")} exported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Info(_host.Owner, "Export scripts", "The scripts couldn't be written: " + ex.Message);
        }
    }

    /// <summary>A script as a file: below the export folder in its folders' names; the deployed text unless drafts are chosen.</summary>
    private static ScriptTransfer.FileItem FileItem(ScriptEntry e, bool useDraft)
    {
        if (e.Library is { } lib) return new ScriptTransfer.FileItem([e.FolderPath.Split(" › ").Last()], $"{lib.Id} {lib.Name}", ScriptStore.PowerShell, lib.Content, lib.Id);
        var s = e.Own!;
        var content = !useDraft && ScriptStore.Deployed(s.Id) is { } v ? v.Content : ScriptStore.Draft(s.Id)!;
        var path = e.FolderPath.Length == 0 ? [] : e.FolderPath.Split(" › ");
        return new ScriptTransfer.FileItem(path, s.Name, s.Language, content, s.LibraryId);
    }

    /// <summary>The package's data: the user's scripts with their folders and versions; library scripts as plain scripts.</summary>
    private static ScriptsExport PackageData(IReadOnlyList<ScriptEntry> entries)
    {
        var data = ScriptStore.Export(entries.Where(e => e.Own is not null).Select(e => e.Key).ToList());
        var libraryFolder = "library";
        foreach (var e in entries.Where(e => e.Library is not null))
        {
            if (data.Folders.All(f => f.Id != libraryFolder)) data.Folders.Add(new ScriptsExport.Folder(libraryFolder, null, "Library", null, 0));
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            data.Scripts.Add(new ScriptsExport.Script(e.Key, libraryFolder, e.Name, ScriptStore.PowerShell, e.Library!.Content, null, null, e.Library.Id, 0, now, now));
        }
        return data;
    }

    // ================================================================== drag and drop, keys

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var own = SelectedEntries().Where(x => x.Own is not null).ToList();
        if (own.Count == 0 || FindItem(e.OriginalSource as DependencyObject) is not { } item) return;
        StartDrag(e, item, ScriptDragFormat, string.Join("\n", own.Select(x => x.Key)));
    }

    private void StartDrag(MouseEventArgs e, FrameworkElement source, string format, string ids)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        Save();
        DragDrop.DoDragDrop(source, new DataObject(format, ids), DragDropEffects.Move);
        e.Handled = true;
    }

    /// <summary>A drop on a folder (null = My scripts): scripts move into it, a folder moves under it, files are imported.</summary>
    private void DropOn(string? folderId, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(ScriptDragFormat) is string scripts)
        {
            if (folderId is null) return;
            foreach (var id in scripts.Split('\n', StringSplitOptions.RemoveEmptyEntries)) ScriptStore.MoveScript(id, folderId);
            ShowList();
        }
        else if (e.Data.GetData(FolderDragFormat) is string folder)
        {
            if (!ScriptStore.MoveFolder(folder, folderId)) _ = Dialogs.Info(_host.Owner, "Move folder", "A folder can't go into itself or a folder inside it.");
            Reload(folder);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop)) DropFiles(folderId, e);
        e.Handled = true;
    }

    private static ListBoxItem? FindItem(DependencyObject? x)
    {
        for (; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is ListBoxItem i) return i;
        return null;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (_tree.SelectedItem is not TreeViewItem { Tag: string key }) return;
        if (key.StartsWith("link:", StringComparison.Ordinal) && e.Key == Key.Delete)
        {
            if (ScriptCatalog.Find(key[5..]) is { } f) ToggleFavourite([f]);
            e.Handled = true;
            return;
        }
        if (ScriptStore.Folders().FirstOrDefault(f => f.Id == key) is not { } folder) return;
        if (e.Key == Key.F2) { RenameFolder(folder); e.Handled = true; }
        else if (e.Key == Key.Delete) { DeleteFolder(folder); e.Handled = true; }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        var entries = SelectedEntries();
        if (e.Key == Key.Delete && entries.Count > 0) { DeleteScripts(entries); e.Handled = true; }
        else if (e.Key == Key.Enter && entries.Count == 1) { Open(entries[0]); _editor.FocusEditor(); e.Handled = true; }
        else if (e.Key == Key.F2 && entries.Count == 1 && entries[0].Own is not null) { Open(entries[0]); _name.Focus(); _name.SelectAll(); e.Handled = true; }
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.N && !_editor.Editor.IsKeyboardFocusWithin) { NewScript(ScriptStore.PowerShell); e.Handled = true; }
        else if (mods == ModifierKeys.None && e.Key == Key.F5 && _current is { IsPowerShell: true }) { RunCurrent(draft: false); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.D && _deploy.IsEnabled && _current?.Own is not null) { Deploy(); e.Handled = true; }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C && _current?.Own is not null) { CompareCurrent(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.S) { Save(); e.Handled = true; }
    }
}

/// <summary>"Recently deleted" of the Scripts view: folders (with what was in them) and scripts, with their versions.</summary>
public sealed class ScriptsDeletedSource : IRecentlyDeletedSource
{
    public string Info => $"Deleted folders and scripts are kept for {ScriptStore.KeepDeletedDays} days, with their versions.";
    public string Empty => $"Nothing was deleted in the last {ScriptStore.KeepDeletedDays} days.";

    public IReadOnlyList<DeletedEntry> Entries() => ScriptStore.Deleted().Select(d => new DeletedEntry(d.Kind, d.Id, d.Name,
        d.Kind == ScriptStore.FolderKind ? $"Folder, {d.Scripts} script{(d.Scripts == 1 ? "" : "s")}" : "Script", d.DeletedUtc,
        d.Kind == ScriptStore.FolderKind ? SymbolRegular.Folder24 : SymbolRegular.Code24)).ToList();

    public void Restore(DeletedEntry entry)
    {
        ScriptStore.Restore(entry.Kind, entry.Id);
        AuditLog.Write(AuditEvents.ScriptRestored, null, entry.Id, entry.Kind);
    }

    public void DeleteNow(DeletedEntry entry) => ScriptStore.DeleteNow(entry.Kind, entry.Id);
}

/// <summary>Picks the folder scripts move to.</summary>
public sealed class FolderPickerWindow : FluentWindow
{
    private readonly TextBox _search = new() { PlaceholderText = "Search folders", Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _items = new() { Height = 300, BorderThickness = new Thickness(0) };
    private readonly IReadOnlyList<NoteLinkTarget> _targets;

    public string? Selected { get; private set; }

    public FolderPickerWindow(IReadOnlyList<NoteLinkTarget> targets)
    {
        _targets = targets;
        ScriptWindowFrame.Apply(this, "Move to", 460);
        var ok = new Button { Content = "Move", Appearance = ControlAppearance.Primary, IsDefault = true };
        ok.Click += (_, _) => { if (_items.SelectedItem is ListBoxItem { Tag: string id }) { Selected = id; DialogResult = true; } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(_search);
        body.Children.Add(_items);
        body.Children.Add(ScriptWindowFrame.Buttons(ok, cancel));
        ScriptWindowFrame.Fill(this, body, resizable: false);
        _search.TextChanged += (_, _) => Fill();
        _items.MouseDoubleClick += (_, _) => { if (_items.SelectedItem is ListBoxItem { Tag: string id }) { Selected = id; DialogResult = true; } };
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    private void Fill()
    {
        _items.Items.Clear();
        var words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in _targets.Where(t => words.All(w => t.Path.Contains(w, StringComparison.CurrentCultureIgnoreCase))))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SymbolIcon(SymbolRegular.Folder24) { FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = t.Path, TextTrimming = TextTrimming.CharacterEllipsis });
            _items.Items.Add(new ListBoxItem { Content = row, Tag = t.Id });
        }
    }
}
