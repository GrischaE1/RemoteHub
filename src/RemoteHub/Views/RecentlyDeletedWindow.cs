using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>An entry of "Recently deleted": what it was, when it went, and a line about it ("Folder, 3 scripts").</summary>
public sealed record DeletedEntry(string Kind, string Id, string Name, string Detail, DateTime DeletedUtc, SymbolRegular Icon);

/// <summary>What a "Recently deleted" window lists and does: Notes, Scripts and the connection tree each have one.</summary>
public interface IRecentlyDeletedSource
{
    /// <summary>The line above the list ("Deleted scripts and folders are kept for 30 days.").</summary>
    string Info { get; }
    /// <summary>Shown when nothing was deleted.</summary>
    string Empty { get; }
    IReadOnlyList<DeletedEntry> Entries();
    void Restore(DeletedEntry entry);
    void DeleteNow(DeletedEntry entry);
}

/// <summary>
/// "Recently deleted": what was deleted in the last 30 days, to restore or delete for good. Shared by Notes, Scripts and
/// the connection tree (docs/scripts-view-design.md §4.9); the source says what it lists.
/// </summary>
public sealed class RecentlyDeletedWindow : FluentWindow
{
    private readonly IRecentlyDeletedSource _source;
    private readonly ListView _list = new() { Height = 300, SelectionMode = SelectionMode.Single, BorderThickness = new Thickness(0) };
    private readonly Button _restore = new() { Content = "Restore", Appearance = ControlAppearance.Primary, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
    private readonly Button _delete = new() { Content = "Delete now", Appearance = ControlAppearance.Danger, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };

    /// <summary>Something was restored or deleted (the view reloads).</summary>
    public bool Changed { get; private set; }

    public RecentlyDeletedWindow(IRecentlyDeletedSource source)
    {
        _source = source;
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

        var info = new TextBlock { Text = source.Info, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _list.SelectionChanged += (_, _) => _restore.IsEnabled = _delete.IsEnabled = _list.SelectedItem is ListViewItem { Tag: DeletedEntry };
        _restore.Click += (_, _) => Act(restore: true);
        _delete.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not ListViewItem { Tag: DeletedEntry d }) return;
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
        IReadOnlyList<DeletedEntry> entries;
        try { entries = _source.Entries(); }
        catch (Exception ex)
        {
            Services.DiagLog.Write("Recently deleted: reading failed: " + ex.GetType().Name);
            entries = [];
        }
        if (entries.Count == 0)
        {
            var empty = new TextBlock { Text = _source.Empty, Margin = new Thickness(4, 8, 4, 8) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            _list.Items.Add(new ListViewItem { Content = empty, IsEnabled = false });
            return;
        }
        foreach (var d in entries)
        {
            var row = new DockPanel { Margin = new Thickness(2, 3, 2, 3) };
            var icon = new SymbolIcon(d.Icon) { Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var when = new TextBlock { Text = d.DeletedUtc.ToLocalTime().ToString("g"), Style = (Style)FindResource("Caption"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = d.Detail, Style = (Style)FindResource("Caption") });
            row.Children.Add(text);
            _list.Items.Add(new ListViewItem { Content = row, Tag = d });
        }
    }

    private void Act(bool restore)
    {
        if (_list.SelectedItem is not ListViewItem { Tag: DeletedEntry d }) return;
        if (restore) _source.Restore(d); else _source.DeleteNow(d);
        Changed = true;
        Fill();
    }
}
