using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

/// <summary>What the Notes view needs from the main window.</summary>
/// <param name="LinkTargets">The folders and connections a page can be linked to.</param>
/// <param name="SharedFolderMode">Connections live in a shared settings file: notes stay on this PC (§9.2).</param>
public sealed record NotesHost(Window Owner, UiState Ui, Action SaveUi, Func<IReadOnlyList<NoteLinkTarget>> LinkTargets, bool SharedFolderMode);

/// <summary>
/// The Notes view (docs/notes-view-design.md §4): notebooks and sections, the pages of a section, and the page in the
/// WYSIWYG editor. Pages save themselves a second after typing stops and whenever the page, section or view changes.
/// Titles and text never go to the diagnostics log; the audit log gets ids only.
/// </summary>
public sealed class NotesView : Grid
{
    private const string PageDragFormat = "RemoteHub.NotePage";
    private const string SectionDragFormat = "RemoteHub.NoteSection";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly NotesHost _host;
    private readonly System.Windows.Controls.TreeView _tree = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ListBox _pages = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _search = new() { PlaceholderText = "Search notes (Ctrl+F)", ClearButtonEnabled = true, Icon = new SymbolIcon(SymbolRegular.Search24) };
    private readonly TextBlock _listHeader = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _clearFilter = new();
    private readonly System.Windows.Controls.TextBox _title = new() { FontSize = NoteMarkdown.H1Size, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Padding = new Thickness(0, 2, 0, 4) };
    private readonly TextBlock _date = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly WrapPanel _links = new() { Margin = new Thickness(0, 6, 0, 8) };
    private readonly NoteEditor _editor = new();
    private readonly TextBlock _status = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly UndoBar _undoBar = new();
    private readonly Grid _pageCard = new();
    private readonly FrameworkElement _emptyPage;
    private readonly ColumnDefinition _booksColumn;
    private readonly ColumnDefinition _pagesColumn;
    private readonly DispatcherTimer _saveTimer = new() { Interval = SaveDelay };
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, Dictionary<string, (string Title, string Body)>> _unsaved = [];
    private readonly HashSet<string> _changeAudited = [];

    private IReadOnlyList<NotebookInfo> _books = [];
    private IReadOnlyList<SectionInfo> _sections = [];
    private string? _sectionId;
    private string? _pageId;
    private string? _savedTitle;
    private string? _savedBody;
    private bool _dirty;
    private bool _loading;
    private bool _locked;
    private (IReadOnlyList<string> NodeIds, string Label)? _linkFilter;

    public NotesView(NotesHost host)
    {
        _host = host;
        Focusable = false;
        ScriptBlock.RecentLanguages.AddRange(host.Ui.NotesRecentLanguages.Take(5));
        ScriptBlock.RecentLanguagesChanged += () => { host.Ui.NotesRecentLanguages = [.. ScriptBlock.RecentLanguages]; host.SaveUi(); };

        _booksColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(host.Ui.NotesNotebooksWidth, 160, 500)), MinWidth = 160 };
        _pagesColumn = new ColumnDefinition { Width = new GridLength(Math.Clamp(host.Ui.NotesPagesWidth, 180, 520)), MinWidth = 180 };
        ColumnDefinitions.Add(_booksColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(_pagesColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });

        Children.Add(Card(BuildNotebooksPane(), 0, new Thickness(0, 0, 2, 10)));
        Children.Add(Splitter(1, () => { host.Ui.NotesNotebooksWidth = _booksColumn.ActualWidth; host.SaveUi(); }));
        Children.Add(Card(BuildPagesPane(), 2, new Thickness(2, 0, 2, 10)));
        Children.Add(Splitter(3, () => { host.Ui.NotesPagesWidth = _pagesColumn.ActualWidth; host.SaveUi(); }));
        _emptyPage = EmptyHint("Pick a page, or add one with Add page (Ctrl+N).");
        Children.Add(Card(BuildPagePane(), 4, new Thickness(2, 0, 10, 10)));
        BuildUndoBar();

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _ = Save(sync: false); };
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); RetryUnsaved(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };
        _editor.Changed += OnEdited;
        _editor.Message += ShowError;
        _editor.NoteLinkRequested += id => OpenPage(id, null);
        _editor.StoreImage = StoreImage;
        if (host.Ui.NotesMarkdownView) _editor.SetMarkdownView(true);
        _editor.MarkdownViewChanged += on => { host.Ui.NotesMarkdownView = on; host.SaveUi(); };
        _title.TextChanged += (_, _) => OnEdited();
        _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _editor.FocusStart(); e.Handled = true; } };
        AutomationProperties.SetName(_title, "Page title");
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) _ = Save(sync: true); };
        PreviewKeyDown += View_PreviewKeyDown;

        Reload(selectSection: host.Ui.LastNoteSectionId);
        if (host.Ui.LastNotePageId is { } last && NoteStore.Page(last) is { } p && p.SectionId == _sectionId) OpenPage(last, null);
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

    private UIElement BuildNotebooksPane()
    {
        var dock = new DockPanel();
        var header = new TextBlock { Text = "Notebooks", Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(4, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(LinkButton(SymbolRegular.Add24, "New notebook", NewNotebook));
        bottom.Children.Add(LinkButton(SymbolRegular.Delete24, "Recently deleted", ShowRecentlyDeleted));
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        if (_host.SharedFolderMode && !_host.Ui.NotesSharedHintDismissed)
        {
            var hint = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
            hint.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
            var row = new DockPanel();
            var close = RemoteToolChrome.Tool(SymbolRegular.Dismiss24, "Hide this hint", () => { hint.Visibility = Visibility.Collapsed; _host.Ui.NotesSharedHintDismissed = true; _host.SaveUi(); });
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
            row.Children.Add(new TextBlock { Text = "Notes are stored on this PC only. They aren't in the shared settings folder.", TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            hint.Child = row;
            DockPanel.SetDock(hint, Dock.Top);
            dock.Children.Add(hint);
        }
        _tree.SelectedItemChanged += (_, _) => { if (!_loading && _tree.SelectedItem is TreeViewItem { Tag: SectionInfo s }) SelectSection(s.Id); };
        AutomationProperties.SetName(_tree, "Notebooks and sections");
        dock.Children.Add(_tree);
        return dock;
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

    private UIElement BuildPagesPane()
    {
        var dock = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
        var add = new Button { Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.Add24), Content = "Add page", ToolTip = "Add page (Ctrl+N)" };
        add.Click += (_, _) => NewPage();
        DockPanel.SetDock(add, Dock.Left);
        bar.Children.Add(add);
        var sort = RemoteToolChrome.Tool(SymbolRegular.ArrowSort24, "Sort pages", () => { });
        sort.Click += (_, _) => { var m = SortMenu(); m.PlacementTarget = sort; m.Placement = PlacementMode.Bottom; m.IsOpen = true; };
        DockPanel.SetDock(sort, Dock.Right);
        bar.Children.Add(sort);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);

        _search.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(_search, "Search notes");
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _search.Text = ""; e.Handled = true; } else if (e.Key == Key.Down && _pages.Items.Count > 0) { _pages.SelectedIndex = 0; _pages.Focus(); e.Handled = true; } };
        DockPanel.SetDock(_search, Dock.Top);
        dock.Children.Add(_search);

        var head = new DockPanel { Margin = new Thickness(4, 0, 0, 6) };
        _clearFilter.Icon = new SymbolIcon(SymbolRegular.Dismiss24);
        _clearFilter.ToolTip = "Show the section's pages again";
        _clearFilter.Padding = new Thickness(8, 5, 8, 5);
        _clearFilter.MinWidth = 0;
        AutomationProperties.SetName(_clearFilter, "Show the section's pages again");
        _clearFilter.Click += (_, _) => { _linkFilter = null; ShowPages(); };
        _clearFilter.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_clearFilter, Dock.Right);
        head.Children.Add(_clearFilter);
        head.Children.Add(_listHeader);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        _pages.SelectionChanged += (_, _) => { if (!_loading && _pages.SelectedItem is ListBoxItem { Tag: PageInfo p }) OpenPage(p.Id, _search.Text); };
        _pages.KeyDown += (_, e) => { if (e.Key == Key.Delete && _pages.SelectedItem is ListBoxItem { Tag: PageInfo p }) { DeletePage(p); e.Handled = true; } };
        _pages.PreviewMouseMove += Pages_PreviewMouseMove;
        AutomationProperties.SetName(_pages, "Pages");
        ScrollViewer.SetHorizontalScrollBarVisibility(_pages, ScrollBarVisibility.Disabled);
        dock.Children.Add(_pages);
        return dock;
    }

    private UIElement BuildPagePane()
    {
        _pageCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _pageCard.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _pageCard.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _title.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        // No border until focused (§4.4).
        _title.GotKeyboardFocus += (_, _) => _title.SetResourceReference(Control.BorderBrushProperty, "ChromeStrokeBrush");
        _title.LostKeyboardFocus += (_, _) => _title.BorderBrush = Brushes.Transparent;
        _title.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextFillColorPrimaryBrush");
        _date.Style = (Style)FindResource("Caption");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var head = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        head.Children.Add(_title);
        head.Children.Add(_date);
        head.Children.Add(_links);
        _pageCard.Children.Add(head);
        _editor.Margin = new Thickness(4, 0, 0, 0);
        SetRow(_editor, 1);
        _pageCard.Children.Add(_editor);
        SetRow(_status, 2);
        _pageCard.Children.Add(_status);
        _editor.AddBarItems(
            PageAction(SymbolRegular.Link24, "Link this page to folders or connections", EditLinks),
            PageAction(SymbolRegular.DocumentCopy24, "Copy page as Markdown", CopyPageAsMarkdown),
            PageAction(SymbolRegular.ArrowExportLtr24, "Export page as Markdown…", () => { if (CurrentPageInfo() is { } p) ExportPage(p); }));

        var host = new Grid();
        host.Children.Add(_pageCard);
        host.Children.Add(_emptyPage);
        return host;
    }

    private static Button PageAction(SymbolRegular icon, string tip, Action a)
    {
        var b = NoteEditor.BarButton(icon, tip);
        b.Click += (_, _) => a();
        return b;
    }

    private void BuildUndoBar()
    {
        SetColumnSpan(_undoBar, 5);
        Children.Add(_undoBar);
    }

    // ================================================================== loading

    /// <summary>Reloads notebooks and sections (after a change elsewhere) and keeps the selection when it still exists.</summary>
    public void Reload(string? selectSection = null)
    {
        NoteStore.EnsureDefault();
        _books = NoteStore.Notebooks();
        _sections = NoteStore.Sections();
        var section = selectSection ?? _sectionId;
        if (section is null || _sections.All(s => s.Id != section)) section = _sections.FirstOrDefault()?.Id;
        BuildTree(section);
        SelectSection(section);
    }

    private void BuildTree(string? select)
    {
        _loading = true;
        _tree.Items.Clear();
        foreach (var book in _books)
        {
            var bookItem = new TreeViewItem { Header = BookHeader(book), Tag = book, IsExpanded = !_host.Ui.NotesCollapsedNotebooks.Contains(book.Id) };
            AutomationProperties.SetName(bookItem, "Notebook " + book.Name);
            bookItem.Expanded += (_, e) => { if (e.OriginalSource == bookItem) { _host.Ui.NotesCollapsedNotebooks.Remove(book.Id); _host.SaveUi(); } };
            bookItem.Collapsed += (_, e) => { if (e.OriginalSource == bookItem && !_host.Ui.NotesCollapsedNotebooks.Contains(book.Id)) { _host.Ui.NotesCollapsedNotebooks.Add(book.Id); _host.SaveUi(); } };
            bookItem.ContextMenu = BookMenu(book);
            bookItem.AllowDrop = true;
            bookItem.Drop += (_, e) => DropOnNotebook(book, e);
            foreach (var s in _sections.Where(s => s.NotebookId == book.Id))
            {
                var item = new TreeViewItem { Header = SectionHeader(s), Tag = s, ContextMenu = SectionMenu(s), AllowDrop = true };
                AutomationProperties.SetName(item, s.Name);
                item.Drop += (_, e) => DropOnSection(s, e);
                item.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(PageDragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
                item.PreviewMouseMove += (_, e) => StartDrag(e, item, SectionDragFormat, s.Id);
                bookItem.Items.Add(item);
                if (s.Id == select) { item.IsSelected = true; bookItem.IsExpanded = true; }
            }
            var add = new TreeViewItem { Header = AddSectionHeader(), Focusable = true };
            AutomationProperties.SetName(add, "New section in " + book.Name);
            // Opens on a click or Enter only: arrowing through the tree must not pop up a dialog.
            add.Selected += (_, e) => e.Handled = true;
            add.MouseLeftButtonUp += (_, e) => { e.Handled = true; NewSection(book); };
            add.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; NewSection(book); } };
            bookItem.Items.Add(add);
            _tree.Items.Add(bookItem);
        }
        _loading = false;
    }

    private static UIElement BookHeader(NotebookInfo b)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new SymbolIcon(SymbolRegular.Notebook24) { FontSize = 16, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = b.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    private static UIElement SectionHeader(SectionInfo s)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 8, 0), Background = ColorTags.Brush(s.Color) ?? Brushes.Transparent };
        row.Children.Add(bar);
        row.Children.Add(new TextBlock { Text = s.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        AutomationProperties.SetName(row, "Section " + s.Name);
        return row;
    }

    private static UIElement AddSectionHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new SymbolIcon(SymbolRegular.Add24) { FontSize = 12, Margin = new Thickness(2, 0, 9, 0) });
        var t = new TextBlock { Text = "New section" };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        row.Children.Add(t);
        return row;
    }

    private void SelectSection(string? id)
    {
        if (id != _sectionId) _ = Save(sync: true);
        _sectionId = id;
        _linkFilter = null;
        if (_search.Text.Length > 0) { _loading = true; _search.Text = ""; _loading = false; }
        if (id is not null && _host.Ui.LastNoteSectionId != id) { _host.Ui.LastNoteSectionId = id; _host.SaveUi(); }
        ShowPages();
    }

    private void ShowPages()
    {
        _loading = true;
        _pages.Items.Clear();
        _clearFilter.Visibility = _linkFilter is null ? Visibility.Collapsed : Visibility.Visible;
        IReadOnlyList<PageInfo> pages;
        if (_linkFilter is { } f)
        {
            _listHeader.Text = f.Label;
            pages = NoteStore.PagesLinkedTo(f.NodeIds);
        }
        else
        {
            _listHeader.Text = _sections.FirstOrDefault(s => s.Id == _sectionId)?.Name ?? "";
            pages = _sectionId is null ? [] : Sorted(NoteStore.Pages(_sectionId));
        }
        foreach (var p in pages) _pages.Items.Add(PageItem(p, _linkFilter is not null));
        if (pages.Count == 0)
            _pages.Items.Add(new ListBoxItem { Content = EmptyHint(_linkFilter is null ? "No pages yet." : "No pages are linked here."), IsEnabled = false });
        foreach (ListBoxItem i in _pages.Items) if (i.Tag is PageInfo p && p.Id == _pageId) i.IsSelected = true;
        _loading = false;
        ShowPageCard();
    }

    private IReadOnlyList<PageInfo> Sorted(IReadOnlyList<PageInfo> pages) => _host.Ui.NotesSort switch
    {
        "title" => [.. pages.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)],
        "modified" => [.. pages.OrderByDescending(p => p.ModifiedUtc)],
        "created" => [.. pages.OrderByDescending(p => p.CreatedUtc)],
        _ => pages,
    };

    private ListBoxItem PageItem(PageInfo p, bool showSection)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
        var title = new TextBlock { Text = p.Title.Length > 0 ? p.Title : "Untitled page", TextTrimming = TextTrimming.CharacterEllipsis };
        if (p.Title.Length == 0) title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        stack.Children.Add(title);
        var caption = showSection ? _sections.FirstOrDefault(s => s.Id == p.SectionId)?.Name
            : _host.Ui.NotesSort == "modified" ? p.ModifiedUtc.ToLocalTime().ToString("g")
            : _host.Ui.NotesSort == "created" ? p.CreatedUtc.ToLocalTime().ToString("g") : null;
        if (caption is not null) stack.Children.Add(new TextBlock { Text = caption, Style = (Style)FindResource("Caption") });
        var item = new ListBoxItem { Content = stack, Tag = p, ContextMenu = PageMenu(p) };
        AutomationProperties.SetName(item, p.Title.Length > 0 ? p.Title : "Untitled page");
        return item;
    }

    private ContextMenu SortMenu()
    {
        var m = new ContextMenu();
        foreach (var (id, label) in new[] { ("manual", "Manual order"), ("title", "Title"), ("modified", "Date modified"), ("created", "Date created") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = _host.Ui.NotesSort == id };
            item.Click += (_, _) => { _host.Ui.NotesSort = id; _host.SaveUi(); ShowPages(); };
            m.Items.Add(item);
        }
        return m;
    }

    // ================================================================== the page

    private PageInfo? CurrentPageInfo() => _pageId is null ? null : NoteStore.Page(_pageId);

    /// <summary>Opens a page (from the list, a search result, a link). <paramref name="highlight"/>: search words to show.</summary>
    public void OpenPage(string id, string? highlight)
    {
        if (_locked) return;
        if (id != _pageId) _ = Save(sync: true);
        if (NoteStore.Page(id) is not { } page) { ShowError("That page no longer exists."); return; }
        if (page.SectionId != _sectionId && string.IsNullOrEmpty(highlight) && _linkFilter is null)
        {
            _sectionId = page.SectionId;
            BuildTree(page.SectionId);
            ShowPages();
        }
        LeavePage();
        _pageId = id;
        _loading = true;
        var body = Unsaved(id)?.Body ?? NoteStore.Body(id) ?? "";
        var title = Unsaved(id)?.Title ?? page.Title;
        _title.Text = title;
        var created = page.CreatedUtc.ToLocalTime();
        _date.Text = created.ToString("D") + "  " + created.ToString("t"); // the user's regional formats
        _date.ToolTip = "Modified " + page.ModifiedUtc.ToLocalTime().ToString("g");
        _editor.Load(body, NoteStore.Images(id));
        _savedTitle = page.Title;
        _savedBody = NoteStore.Body(id) ?? "";
        _dirty = Unsaved(id) is not null;
        _loading = false;
        ShowLinks();
        foreach (var item in _pages.Items.OfType<ListBoxItem>()) item.IsSelected = item.Tag is PageInfo pi && pi.Id == id;
        if (_host.Ui.LastNotePageId != id) { _host.Ui.LastNotePageId = id; _host.SaveUi(); }
        ShowPageCard();
        _status.Text = "";
        if (!string.IsNullOrWhiteSpace(highlight)) Dispatcher.BeginInvoke(() => _editor.Highlight(highlight), DispatcherPriority.Loaded);
    }

    private (string Title, string Body)? Unsaved(string id) =>
        _unsaved.TryGetValue("p", out var d) && d.TryGetValue(id, out var v) ? v : null;

    /// <summary>Leaving a page: images it no longer refers to are removed (its undo history goes with it).</summary>
    private void LeavePage()
    {
        if (_pageId is not { } id || _dirty) return;
        try { NoteStore.RemoveUnusedImages(id, NoteMarkdown.ImageIds(_savedBody ?? "")); }
        catch (Exception ex) { DiagLog.Write("Notes: removing unused images failed: " + ex.GetType().Name); }
    }

    private void ClosePage()
    {
        LeavePage();
        _pageId = null;
        _editor.Clear();
        ShowPageCard();
    }

    private void ShowPageCard()
    {
        var open = _pageId is not null && !_locked;
        _pageCard.Visibility = open ? Visibility.Visible : Visibility.Hidden;
        _emptyPage.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowLinks()
    {
        _links.Children.Clear();
        if (_pageId is null) return;
        var ids = NoteStore.Links(_pageId);
        if (ids.Count == 0) { _links.Visibility = Visibility.Collapsed; return; }
        _links.Visibility = Visibility.Visible;
        var targets = _host.LinkTargets().ToDictionary(t => t.Id);
        var label = new TextBlock { Text = "Linked to:", Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _links.Children.Add(label);
        foreach (var id in ids)
        {
            var name = targets.TryGetValue(id, out var t) ? t.Name : "(removed)";
            var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1, 2, 1), Margin = new Thickness(0, 0, 6, 2), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BorderBrushProperty, "ChromeStrokeBrush");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var linkId = id;
            var remove = RemoteToolChrome.Tool(SymbolRegular.Dismiss24, $"Remove the link to {name}", () =>
            {
                NoteStore.SetLinks(_pageId!, NoteStore.Links(_pageId!).Where(x => x != linkId));
                ShowLinks();
            });
            remove.Padding = new Thickness(4, 0, 4, 0);
            remove.FontSize = 10;
            row.Children.Add(remove);
            chip.Child = row;
            _links.Children.Add(chip);
        }
    }

    private void EditLinks()
    {
        if (_pageId is null) return;
        var dlg = new LinkPickerWindow(_host.LinkTargets(), NoteStore.Links(_pageId)) { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        NoteStore.SetLinks(_pageId, dlg.Selected);
        ShowLinks();
    }

    /// <summary>Shows the pages linked to some folders or connections (the tree's "Notes" command).</summary>
    public void ShowLinkedTo(IReadOnlyList<string> nodeIds, string label)
    {
        _ = Save(sync: true);
        _linkFilter = (nodeIds, label);
        if (_search.Text.Length > 0) { _loading = true; _search.Text = ""; _loading = false; }
        ShowPages();
        if (_pages.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is PageInfo) is { Tag: PageInfo first }) OpenPage(first.Id, null);
    }

    // ================================================================== saving (§6.1)

    private void OnEdited()
    {
        if (_loading || _pageId is null) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Saves the open page now if it changed. <paramref name="sync"/>: before closing, locking or switching.</summary>
    public async Task Save(bool sync)
    {
        _saveTimer.Stop();
        if (!_dirty || _pageId is not { } id || _locked) return;
        var title = _title.Text.Trim();
        var body = _editor.Markdown();
        if (body.Length > NoteStore.MaxBodyLength)
        {
            _editor.UndoLast();
            ShowError("This page is too large – split it into several pages.");
            return;
        }
        _dirty = false;
        if (title == _savedTitle && body == _savedBody) return;
        var first = _changeAudited.Add(id);
        try
        {
            if (sync) SaveNow(id, title, body);
            else await Task.Run(() => SaveNow(id, title, body));
            _savedTitle = title;
            _savedBody = body;
            Forget(id);
            if (first) AuditLog.Write(AuditEvents.NoteChanged, null, id, null);
            if (_status.Text.StartsWith("Not saved", StringComparison.Ordinal)) _status.Text = "";
            UpdateListTitle(id, title);
        }
        catch (Exception ex) when (ex is not NotePageTooLargeException)
        {
            // Kept in memory and tried again; the text stays in the editor.
            if (first) _changeAudited.Remove(id);
            Remember(id, title, body);
            ShowError("Not saved – retrying");
            DiagLog.Write("Notes: saving a page failed: " + ex.GetType().Name);
            _retryTimer.Start();
        }
    }

    private static void SaveNow(string id, string title, string body) => NoteStore.SavePage(id, title, body, NoteMarkdown.PlainText(body));

    private void Remember(string id, string title, string body)
    {
        if (!_unsaved.TryGetValue("p", out var d)) _unsaved["p"] = d = [];
        d[id] = (title, body);
    }

    private void Forget(string id) { if (_unsaved.TryGetValue("p", out var d)) d.Remove(id); }

    private void RetryUnsaved()
    {
        if (!_unsaved.TryGetValue("p", out var d) || d.Count == 0) return;
        foreach (var (id, (title, body)) in d.ToList())
        {
            try
            {
                SaveNow(id, title, body);
                d.Remove(id);
                if (id == _pageId) { _savedTitle = title; _savedBody = body; }
            }
            catch (Exception ex) { DiagLog.Write("Notes: saving a page failed again: " + ex.GetType().Name); }
        }
        if (d.Count > 0) _retryTimer.Start();
        else if (_status.Text.StartsWith("Not saved", StringComparison.Ordinal)) _status.Text = "";
    }

    private void UpdateListTitle(string id, string title)
    {
        foreach (var item in _pages.Items.OfType<ListBoxItem>())
            if (item.Tag is PageInfo p && p.Id == id && p.Title != title)
            {
                var updated = p with { Title = title };
                var replacement = PageItem(updated, _linkFilter is not null);
                item.Content = replacement.Content;
                item.Tag = updated;
                item.ContextMenu = PageMenu(updated);
            }
    }

    /// <summary>Before RemoteHub locks: saves, then takes the page's text out of the window (§6.3).</summary>
    public void Lock()
    {
        _ = Save(sync: true);
        _locked = true;
        _editor.Clear();
        _title.Text = "";
        ShowPageCard();
    }

    public void Unlock()
    {
        if (!_locked) return;
        _locked = false;
        var id = _pageId;
        _pageId = null;
        if (id is not null) OpenPage(id, null);
        else ShowPageCard();
    }

    private string? StoreImage(PreparedImage image)
    {
        if (_pageId is null) return null;
        try { return NoteStore.AddImage(_pageId, image.Mime, image.Width, image.Height, image.Data); }
        catch (Exception ex)
        {
            DiagLog.Write("Notes: storing an image failed: " + ex.GetType().Name);
            ShowError("The image could not be stored.");
            return null;
        }
    }

    private void ShowMessage(string text)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    /// <summary>A failure on the status line, coloured like the other views' errors.</summary>
    private void ShowError(string text)
    {
        _status.Text = text;
        _status.Foreground = Brushes.IndianRed;
    }

    // ================================================================== commands

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control && e.Key == Key.F) { FocusSearch(); e.Handled = true; }
        else if (mods == ModifierKeys.Control && e.Key == Key.N) { NewPage(); e.Handled = true; }
        else if (mods == ModifierKeys.None && _tree.IsKeyboardFocusWithin && _tree.SelectedItem is TreeViewItem { Tag: var tag } && e.Key is Key.F2 or Key.Delete)
        {
            e.Handled = true;
            switch (tag, e.Key)
            {
                case (SectionInfo s, Key.F2): RenameSection(s); break;
                case (NotebookInfo b, Key.F2): RenameNotebook(b); break;
                case (SectionInfo s, Key.Delete): DeleteSection(s); break;
                case (NotebookInfo b, Key.Delete): DeleteNotebook(b); break;
                default: e.Handled = false; break;
            }
        }
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    public void NewPage()
    {
        if (_sectionId is null) return;
        _ = Save(sync: true);
        var id = NoteStore.CreatePage(_sectionId, "");
        AuditLog.Write(AuditEvents.NoteAdded, null, id, null);
        _linkFilter = null;
        ShowPages();
        OpenPage(id, null);
        _title.Focus();
    }

    private async void DeletePage(PageInfo p)
    {
        await Save(sync: true);
        NoteStore.Delete(NoteStore.PageKind, p.Id);
        AuditLog.Write(AuditEvents.NoteDeleted, null, p.Id, NoteStore.PageKind);
        if (p.Id == _pageId) { _pageId = null; _editor.Clear(); }
        ShowPages();
        ShowUndo("Page deleted", () =>
        {
            NoteStore.Restore(NoteStore.PageKind, p.Id);
            AuditLog.Write(AuditEvents.NoteRestored, null, p.Id, NoteStore.PageKind);
            ShowPages();
            OpenPage(p.Id, null);
        });
    }

    private void ShowUndo(string text, Action undo) => _undoBar.Show(text, undo);

    private void NewNotebook()
    {
        var dlg = new InputDialog("New notebook", "Name of the notebook", "New notebook") { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        var book = NoteStore.CreateNotebook(dlg.Value);
        var section = NoteStore.CreateSection(book, "New section", NextColour());
        Reload(section);
    }

    private void NewSection(NotebookInfo book)
    {
        var dlg = new InputDialog("New section", $"Name of the new section in {book.Name}", "New section") { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) { BuildTree(_sectionId); return; }
        var id = NoteStore.CreateSection(book.Id, dlg.Value, NextColour());
        _host.Ui.NotesCollapsedNotebooks.Remove(book.Id);
        Reload(id);
    }

    /// <summary>New sections take the next colour of the palette in turn.</summary>
    private string NextColour()
    {
        var palette = ColorTags.All.Select(t => t.Id).ToList();
        var last = _sections.LastOrDefault()?.Color;
        var at = last is null ? -1 : palette.IndexOf(last);
        return palette[(at + 1) % palette.Count];
    }

    private void RenameNotebook(NotebookInfo b)
    {
        var dlg = new InputDialog("Rename notebook", "Name of the notebook", b.Name) { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        NoteStore.RenameNotebook(b.Id, dlg.Value);
        Reload();
    }

    private void RenameSection(SectionInfo s)
    {
        var dlg = new InputDialog("Rename section", "Name of the section", s.Name) { Owner = _host.Owner };
        if (dlg.ShowDialog() != true) return;
        NoteStore.RenameSection(s.Id, dlg.Value);
        Reload();
    }

    private async void DeleteNotebook(NotebookInfo b)
    {
        var sections = _sections.Count(s => s.NotebookId == b.Id);
        var pages = _sections.Where(s => s.NotebookId == b.Id).Sum(s => NoteStore.Pages(s.Id).Count);
        if (!await Dialogs.Confirm(_host.Owner, "Delete notebook", $"Delete the notebook \"{b.Name}\" with {sections} sections and {pages} pages?\n\nIt stays in Recently deleted for {NoteStore.KeepDeletedDays} days.", "Delete")) return;
        await Save(sync: true);
        NoteStore.Delete(NoteStore.NotebookKind, b.Id);
        AuditLog.Write(AuditEvents.NoteDeleted, null, b.Id, NoteStore.NotebookKind);
        ClosePage();
        Reload();
        ShowUndo("Notebook deleted", () => { NoteStore.Restore(NoteStore.NotebookKind, b.Id); AuditLog.Write(AuditEvents.NoteRestored, null, b.Id, NoteStore.NotebookKind); Reload(); });
    }

    private async void DeleteSection(SectionInfo s)
    {
        var pages = NoteStore.Pages(s.Id).Count;
        if (!await Dialogs.Confirm(_host.Owner, "Delete section", $"Delete the section \"{s.Name}\" and its {pages} pages?\n\nIt stays in Recently deleted for {NoteStore.KeepDeletedDays} days.", "Delete")) return;
        await Save(sync: true);
        NoteStore.Delete(NoteStore.SectionKind, s.Id);
        AuditLog.Write(AuditEvents.NoteDeleted, null, s.Id, NoteStore.SectionKind);
        if (s.Id == _sectionId) ClosePage();
        Reload();
        ShowUndo("Section deleted", () => { NoteStore.Restore(NoteStore.SectionKind, s.Id); AuditLog.Write(AuditEvents.NoteRestored, null, s.Id, NoteStore.SectionKind); Reload(s.Id); });
    }

    private void Move(string kind, IReadOnlyList<string> order, string id, int by)
    {
        var list = order.ToList();
        var i = list.IndexOf(id);
        var j = i + by;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        NoteStore.Reorder(kind, list);
        if (kind == NoteStore.PageKind) ShowPages(); else Reload();
    }

    private void ShowRecentlyDeleted()
    {
        _ = Save(sync: true);
        var w = new RecentlyDeletedWindow(new NotesDeletedSource()) { Owner = _host.Owner };
        w.ShowDialog();
        if (w.Changed) Reload();
    }

    private void CopyPageAsMarkdown()
    {
        if (_pageId is null) return;
        var title = _title.Text.Trim();
        NoteEditor.SetClipboardText((title.Length > 0 ? "# " + title + "\n\n" : "") + _editor.Markdown());
        ShowMessage("Copied as Markdown.");
    }

    // ================================================================== menus

    private ContextMenu BookMenu(NotebookInfo b)
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Rename…", SymbolRegular.Rename24, () => RenameNotebook(b), gesture: "F2"));
        m.Items.Add(RemoteToolChrome.Item("New section…", SymbolRegular.Add24, () => NewSection(b)));
        m.Items.Add(RemoteToolChrome.Item("Move up", SymbolRegular.ArrowUp24, () => Move(NoteStore.NotebookKind, _books.Select(x => x.Id).ToList(), b.Id, -1), _books.FirstOrDefault()?.Id != b.Id));
        m.Items.Add(RemoteToolChrome.Item("Move down", SymbolRegular.ArrowDown24, () => Move(NoteStore.NotebookKind, _books.Select(x => x.Id).ToList(), b.Id, 1), _books.LastOrDefault()?.Id != b.Id));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Export as Markdown…", SymbolRegular.ArrowExportLtr24, () => ExportTree(b.Name, _sections.Where(s => s.NotebookId == b.Id).ToList())));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete…", SymbolRegular.Delete24, () => DeleteNotebook(b), gesture: "Del"));
        return m;
    }

    private ContextMenu SectionMenu(SectionInfo s)
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Rename…", SymbolRegular.Rename24, () => RenameSection(s), gesture: "F2"));
        m.Items.Add(ColorMenu.Build(_host.Owner, s.Color, c => { NoteStore.SetSectionColor(s.Id, c); Reload(); }));
        var siblings = _sections.Where(x => x.NotebookId == s.NotebookId).Select(x => x.Id).ToList();
        m.Items.Add(RemoteToolChrome.Item("Move up", SymbolRegular.ArrowUp24, () => Move(NoteStore.SectionKind, siblings, s.Id, -1), siblings.FirstOrDefault() != s.Id));
        m.Items.Add(RemoteToolChrome.Item("Move down", SymbolRegular.ArrowDown24, () => Move(NoteStore.SectionKind, siblings, s.Id, 1), siblings.LastOrDefault() != s.Id));
        if (_books.Count > 1)
        {
            var to = new MenuItem { Header = "Move to notebook", Icon = new SymbolIcon(SymbolRegular.Notebook24) };
            foreach (var b in _books.Where(b => b.Id != s.NotebookId))
            {
                var bookId = b.Id;
                to.Items.Add(RemoteToolChrome.Item(b.Name, SymbolRegular.Notebook24, () => { NoteStore.MoveSection(s.Id, bookId); Reload(s.Id); }));
            }
            m.Items.Add(to);
        }
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Export as Markdown…", SymbolRegular.ArrowExportLtr24, () => ExportTree(s.Name, [s])));
        m.Items.Add(RemoteToolChrome.Item("Import Markdown files…", SymbolRegular.ArrowImport24, () => ImportFiles(s.Id)));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete…", SymbolRegular.Delete24, () => DeleteSection(s), gesture: "Del"));
        return m;
    }

    private ContextMenu PageMenu(PageInfo p)
    {
        var m = new ContextMenu();
        m.Items.Add(RemoteToolChrome.Item("Rename", SymbolRegular.Rename24, () => { OpenPage(p.Id, null); _title.Focus(); _title.SelectAll(); }));
        var to = new MenuItem { Header = "Move to section", Icon = new SymbolIcon(SymbolRegular.ArrowForward24) };
        foreach (var b in _books)
        {
            var bm = new MenuItem { Header = b.Name, Icon = new SymbolIcon(SymbolRegular.Notebook24) };
            foreach (var s in _sections.Where(s => s.NotebookId == b.Id && s.Id != p.SectionId))
            {
                var sid = s.Id;
                bm.Items.Add(RemoteToolChrome.Item(s.Name, SymbolRegular.Folder24, () => { _ = Save(sync: true); NoteStore.MovePage(p.Id, sid); ShowPages(); }));
            }
            if (bm.Items.Count > 0) to.Items.Add(bm);
        }
        m.Items.Add(to);
        if (_host.Ui.NotesSort == "manual" && _linkFilter is null && _sectionId is not null)
        {
            var order = _pages.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<PageInfo>().Select(x => x.Id).ToList();
            m.Items.Add(RemoteToolChrome.Item("Move up", SymbolRegular.ArrowUp24, () => Move(NoteStore.PageKind, order, p.Id, -1), order.FirstOrDefault() != p.Id));
            m.Items.Add(RemoteToolChrome.Item("Move down", SymbolRegular.ArrowDown24, () => Move(NoteStore.PageKind, order, p.Id, 1), order.LastOrDefault() != p.Id));
        }
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Copy link", SymbolRegular.Link24, () => NoteEditor.SetClipboardText("remotehub://note/" + p.Id)));
        m.Items.Add(RemoteToolChrome.Item("Export as Markdown…", SymbolRegular.ArrowExportLtr24, () => ExportPage(p)));
        m.Items.Add(new Separator());
        m.Items.Add(RemoteToolChrome.Item("Delete", SymbolRegular.Delete24, () => DeletePage(p), gesture: "Del"));
        return m;
    }

    // ================================================================== search (§7)

    private void RunSearch()
    {
        if (_loading) return;
        var text = _search.Text.Trim();
        if (text.Length == 0) { ShowPages(); return; }
        IReadOnlyList<NoteSearchHit> hits;
        try { hits = NoteStore.Search(text); }
        catch (Exception ex) { DiagLog.Write("Notes: search failed: " + ex.GetType().Name); hits = []; }
        _loading = true;
        _pages.Items.Clear();
        _clearFilter.Visibility = Visibility.Collapsed;
        _listHeader.Text = hits.Count == 0 ? "No pages found" : hits.Count == 1 ? "1 page found" : $"{hits.Count} pages found";
        foreach (var h in hits) _pages.Items.Add(HitItem(h));
        _loading = false;
    }

    private ListBoxItem HitItem(NoteSearchHit hit)
    {
        var section = _sections.FirstOrDefault(s => s.Id == hit.Page.SectionId);
        var stack = new StackPanel { Margin = new Thickness(2, 3, 2, 4) };
        stack.Children.Add(new TextBlock { Text = hit.Page.Title.Length > 0 ? hit.Page.Title : "Untitled page", TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold });
        var where = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        where.Children.Add(new Border { Width = 3, Height = 12, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 6, 0), Background = ColorTags.Brush(section?.Color) ?? Brushes.Transparent });
        where.Children.Add(new TextBlock { Text = section?.Name ?? "", Style = (Style)FindResource("Caption") });
        stack.Children.Add(where);
        var snippet = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxHeight = 50, TextTrimming = TextTrimming.CharacterEllipsis };
        snippet.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        // Matches are marked by the store with control characters; shown in bold, never parsed as markup.
        foreach (var (part, i) in hit.Snippet.Split(NoteStore.MatchStart).SelectMany((s, i) => i == 0 ? new[] { (s, false) } : SplitEnd(s)).Select((x, i) => (x, i)))
            snippet.Inlines.Add(new Run(part.Item1) { FontWeight = part.Item2 ? FontWeights.SemiBold : FontWeights.Normal });
        stack.Children.Add(snippet);
        var item = new ListBoxItem { Content = stack, Tag = hit.Page, ContextMenu = PageMenu(hit.Page) };
        AutomationProperties.SetName(item, hit.Page.Title);
        return item;

        static IEnumerable<(string, bool)> SplitEnd(string s)
        {
            var end = s.IndexOf(NoteStore.MatchEnd);
            if (end < 0) { yield return (s, true); yield break; }
            yield return (s[..end], true);
            yield return (s[(end + 1)..], false);
        }
    }

    // ================================================================== drag and drop

    private Point _dragStart;

    private void Pages_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(this); return; }
        if (FindItem(e.OriginalSource as DependencyObject) is { Tag: PageInfo p } item) StartDrag(e, item, PageDragFormat, p.Id);
    }

    private void StartDrag(MouseEventArgs e, FrameworkElement source, string format, string id)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(this); return; }
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _ = Save(sync: true);
        DragDrop.DoDragDrop(source, new DataObject(format, id), DragDropEffects.Move);
        e.Handled = true;
    }

    private static ListBoxItem? FindItem(DependencyObject? x)
    {
        for (; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is ListBoxItem i) return i;
        return null;
    }

    private void DropOnSection(SectionInfo s, DragEventArgs e)
    {
        if (e.Data.GetData(PageDragFormat) is not string page) return;
        e.Handled = true;
        NoteStore.MovePage(page, s.Id);
        ShowPages();
    }

    private void DropOnNotebook(NotebookInfo b, DragEventArgs e)
    {
        if (e.Data.GetData(SectionDragFormat) is not string section) return;
        e.Handled = true;
        if (_sections.FirstOrDefault(s => s.Id == section)?.NotebookId == b.Id) return;
        NoteStore.MoveSection(section, b.Id);
        Reload(section);
    }

    // ================================================================== export and import (§4.7)

    private async Task<string?> PickExportFolder()
    {
        if (!await Dialogs.Confirm(_host.Owner, "Export as Markdown", "Exported pages are not encrypted. Keep them somewhere safe.", "Choose folder…")) return null;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Export to this folder" };
        return dlg.ShowDialog(_host.Owner) == true ? dlg.FolderName : null;
    }

    private async void ExportPage(PageInfo p)
    {
        await Save(sync: true);
        if (await PickExportFolder() is not { } folder) return;
        try
        {
            var file = NoteFiles.ExportPageTo(folder, new ExportPage(p.Title, NoteStore.Body(p.Id) ?? "", NoteStore.Images(p.Id)));
            AuditLog.Write(AuditEvents.NotesExported, null, p.Id, "1 page");
            await Dialogs.Info(_host.Owner, "Export as Markdown", $"Page saved to\n{file}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.Info(_host.Owner, "Export failed", ex.Message); }
    }

    private async void ExportTree(string name, IReadOnlyList<SectionInfo> sections)
    {
        await Save(sync: true);
        if (await PickExportFolder() is not { } folder) return;
        try
        {
            var data = sections.Select(s => (s.Name, (IReadOnlyList<ExportPage>)NoteStore.Pages(s.Id)
                .Select(p => new ExportPage(p.Title, NoteStore.Body(p.Id) ?? "", NoteStore.Images(p.Id))).ToList()));
            var (dir, pages) = NoteFiles.ExportTree(folder, name, data);
            AuditLog.Write(AuditEvents.NotesExported, null, sections.FirstOrDefault()?.NotebookId, $"{pages} pages");
            await Dialogs.Info(_host.Owner, "Export as Markdown", $"{pages} pages saved to\n{dir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Dialogs.Info(_host.Owner, "Export failed", ex.Message); }
    }

    private async void ImportFiles(string sectionId)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt", Multiselect = true, Title = "Import Markdown files" };
        if (dlg.ShowDialog(_host.Owner) != true) return;
        await Save(sync: true);
        var done = 0;
        var failed = new List<string>();
        string? lastId = null;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var page = await Task.Run(() => NoteFiles.Import(file));
                var id = NoteStore.CreatePage(sectionId, page.Title);
                var body = page.Body;
                for (var i = 0; i < page.Images.Count; i++)
                {
                    var img = page.Images[i];
                    var imageId = NoteStore.AddImage(id, img.Mime, img.Width, img.Height, img.Data);
                    body = body.Replace($"({NoteStore.NoteImageScheme}{NoteFiles.Pending}{i})", $"({NoteStore.NoteImageScheme}{imageId})", StringComparison.Ordinal);
                }
                SaveNow(id, page.Title, body);
                lastId = id;
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotePageTooLargeException)
            {
                failed.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        if (done > 0) AuditLog.Write(AuditEvents.NotesImported, null, sectionId, $"{done} pages");
        SelectSection(sectionId);
        BuildTree(sectionId);
        if (lastId is not null) OpenPage(lastId, null);
        if (failed.Count > 0) await Dialogs.Info(_host.Owner, "Import Markdown files", $"{done} pages imported. Not imported:\n" + string.Join("\n", failed.Take(10)));
    }
}
