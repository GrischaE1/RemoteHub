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

/// <summary>"Recently deleted" of the Notes view: notebooks, sections and pages deleted in the last 30 days.</summary>
public sealed class NotesDeletedSource : IRecentlyDeletedSource
{
    public string Info => $"Deleted notebooks, sections and pages are kept for {NoteStore.KeepDeletedDays} days.";
    public string Empty => $"Nothing was deleted in the last {NoteStore.KeepDeletedDays} days.";

    public IReadOnlyList<DeletedEntry> Entries() => NoteStore.Deleted().Select(d => new DeletedEntry(d.Kind, d.Id,
        d.Name.Length > 0 ? d.Name : "Untitled page",
        d.Kind switch { NoteStore.NotebookKind => $"Notebook, {d.Pages} pages", NoteStore.SectionKind => $"Section, {d.Pages} pages", _ => "Page" },
        d.DeletedUtc,
        d.Kind switch { NoteStore.NotebookKind => SymbolRegular.Notebook24, NoteStore.SectionKind => SymbolRegular.Folder24, _ => SymbolRegular.Document24 })).ToList();

    public void Restore(DeletedEntry entry)
    {
        NoteStore.Restore(entry.Kind, entry.Id);
        AuditLog.Write(AuditEvents.NoteRestored, null, entry.Id, entry.Kind);
    }

    public void DeleteNow(DeletedEntry entry) => NoteStore.DeleteNow(entry.Kind, entry.Id);
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
