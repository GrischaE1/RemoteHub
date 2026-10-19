using System.Windows;
using System.Windows.Controls;
using RemoteHub.Data;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>"Recently deleted" of the Notes view: what was deleted in the last 30 days, to restore or delete for good.</summary>
public sealed class RecentlyDeletedWindow : FluentWindow
{
    private readonly ListView _list = new() { Height = 300, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0) };
    private readonly Button _restore = new() { Content = "Restore", Appearance = ControlAppearance.Primary, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _delete = new() { Content = "Delete now", Appearance = ControlAppearance.Danger, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };

    /// <summary>Something was restored or deleted (the view reloads).</summary>
    public bool Changed { get; private set; }

    public RecentlyDeletedWindow()
    {
        Title = "Recently deleted";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var info = new TextBlock { Text = $"Deleted notebooks, sections and pages are kept for {NoteStore.KeepDeletedDays} days.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _list.SelectionChanged += (_, _) => _restore.IsEnabled = _delete.IsEnabled = _list.SelectedItem is not null;
        _restore.Click += (_, _) => Act(restore: true);
        _delete.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not ListViewItem { Tag: DeletedNote d }) return;
            if (!await Dialogs.Confirm(this, "Delete now", $"Delete \"{d.Name}\" for good? This can't be undone.", "Delete")) return;
            Act(restore: false);
        };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_restore);
        buttons.Children.Add(_delete);
        buttons.Children.Add(close);

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(info);
        body.Children.Add(_list);
        body.Children.Add(buttons);
        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        var entries = NoteStore.Deleted();
        if (entries.Count == 0)
        {
            var empty = new TextBlock { Text = $"Nothing was deleted in the last {NoteStore.KeepDeletedDays} days.", Margin = new Thickness(4, 8, 4, 8) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _list.Items.Add(new ListViewItem { Content = empty, IsEnabled = false });
            return;
        }
        foreach (var d in entries)
        {
            var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };
            var icon = new SymbolIcon(d.Kind switch { NoteStore.NotebookKind => SymbolRegular.Notebook24, NoteStore.SectionKind => SymbolRegular.Folder24, _ => SymbolRegular.Document24 }) { Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var when = new TextBlock { Text = d.DeletedUtc.ToLocalTime().ToString("g"), Style = (Style)FindResource("Caption"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            var kind = d.Kind switch { NoteStore.NotebookKind => $"Notebook, {d.Pages} pages", NoteStore.SectionKind => $"Section, {d.Pages} pages", _ => "Page" };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name.Length > 0 ? d.Name : "Untitled page", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = kind, Style = (Style)FindResource("Caption") });
            row.Children.Add(text);
            _list.Items.Add(new ListViewItem { Content = row, Tag = d });
        }
    }

    private void Act(bool restore)
    {
        if (_list.SelectedItem is not ListViewItem { Tag: DeletedNote d }) return;
        if (restore)
        {
            NoteStore.Restore(d.Kind, d.Id);
            AuditLog.Write(AuditEvents.NoteRestored, null, d.Id, d.Kind);
        }
        else NoteStore.DeleteNow(d.Kind, d.Id);
        Changed = true;
        Fill();
    }
}

/// <summary>A folder or connection a page can be linked to (§4.6).</summary>
public sealed record NoteLinkTarget(string Id, string Name, string Path, bool IsFolder);

/// <summary>Picks the folders and connections a page is linked to.</summary>
public sealed class LinkPickerWindow : FluentWindow
{
    private readonly TextBox _search = new() { PlaceholderText = "Search folders and connections", Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _items = new();
    private readonly HashSet<string> _selected;
    private readonly IReadOnlyList<NoteLinkTarget> _targets;

    public IReadOnlyCollection<string> Selected => _selected;

    public LinkPickerWindow(IReadOnlyList<NoteLinkTarget> targets, IEnumerable<string> selected)
    {
        _targets = targets;
        _selected = [.. selected];
        Title = "Link page";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var info = new TextBlock { Text = "The page shows up under Notes in the context menu of the folders and connections picked here.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var scroll = new ScrollViewer { Height = 320, Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var ok = new Button { Content = "OK", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(info);
        body.Children.Add(_search);
        body.Children.Add(scroll);
        body.Children.Add(buttons);
        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        _search.TextChanged += (_, _) => Fill();
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    private void Fill()
    {
        _items.Children.Clear();
        var words = _search.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in _targets.Where(t => words.All(w => t.Path.Contains(w, StringComparison.CurrentCultureIgnoreCase))).Take(300))
        {
            var cb = new CheckBox { IsChecked = _selected.Contains(t.Id), Margin = new Thickness(0, 2, 0, 2) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SymbolIcon(t.IsFolder ? SymbolRegular.Folder24 : SymbolRegular.Desktop24) { Margin = new Thickness(0, 0, 6, 0), FontSize = 14 });
            row.Children.Add(new TextBlock { Text = t.Path, TextTrimming = TextTrimming.CharacterEllipsis });
            cb.Content = row;
            var id = t.Id;
            cb.Checked += (_, _) => _selected.Add(id);
            cb.Unchecked += (_, _) => _selected.Remove(id);
            _items.Children.Add(cb);
        }
    }
}
