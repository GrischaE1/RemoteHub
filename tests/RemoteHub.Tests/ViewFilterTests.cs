using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Views;
using GridView = System.Windows.Controls.GridView;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteHub.Tests;

/// <summary>
/// The filter box and sortable headers of the file panes, the registry's value list and the services list: what the
/// user types, clicks and presses.
/// </summary>
public class ViewFilterTests
{
    private static FileEntry F(string name, long size = 0) => new(name, @"C:\x\" + name, EntryKind.File, size, null, false);
    private static FileEntry D(string name) => new(name, @"C:\x\" + name, EntryKind.Folder, 0, null, false);

    private sealed class Harness
    {
        public readonly ListView List = new();
        public readonly TextBox Filter = new();
        public readonly List<FileEntry> Activated = [];
        public readonly ListFilter<FileEntry, FileSortColumn> Rows;
        public readonly GridViewColumn Name = new(), Size = new();
        public int Changes;

        public Harness(Window window)
        {
            Rows = new ListFilter<FileEntry, FileSortColumn>(List, Filter, FileEntry.Matches, FileEntry.Compare, FileSortColumn.Name, Activated.Add);
            Rows.Changed += () => Changes++;
            var gv = new GridView();
            gv.Columns.Add(Name);
            gv.Columns.Add(Size);
            Name.DisplayMemberBinding = new System.Windows.Data.Binding(nameof(FileEntry.Name));
            Rows.Column(Name, "Name", FileSortColumn.Name);
            Rows.Column(Size, "Size", FileSortColumn.Size);
            List.View = gv;
            var panel = new DockPanel();
            DockPanel.SetDock(Filter, Dock.Top);
            panel.Children.Add(Filter);
            panel.Children.Add(List);
            window.Content = panel;
            Sta.Pump();
        }

        public List<string> Shown() => List.Items.Cast<FileEntry>().Select(e => e.Name).ToList();

        public void ClickHeader(GridViewColumn column)
        {
            var header = Sta.Find<GridViewColumnHeader>(List, h => h.Column == column);
            header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, header));
        }
    }

    [Fact]
    public void Typing_in_the_filter_narrows_the_list_and_selects_the_first_match() => Sta.Run(w =>
    {
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("setup.log", 5), D("Logs"), F("readme.txt"), F("app.log", 9) }, samePlace: false);
        Assert.Equal(["Logs", "app.log", "readme.txt", "setup.log"], h.Shown());

        h.Filter.Text = "log";
        Assert.Equal(["Logs", "app.log", "setup.log"], h.Shown());
        Assert.Equal("Logs", ((FileEntry)h.List.SelectedItem).Name);
        Assert.Single(h.List.SelectedItems);
        Assert.True(h.Rows.Active);
        Assert.Equal(3, h.Rows.Shown);
        Assert.True(h.Changes >= 2);

        h.Filter.Text = "nothing like this";
        Assert.Empty(h.Shown());
        Assert.Null(h.List.SelectedItem);
    });

    [Fact]
    public void Enter_activates_the_match_Down_moves_into_the_list_and_Escape_clears() => Sta.Run(w =>
    {
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("a.txt"), F("b.txt") }, samePlace: false);
        h.Filter.Text = "b.";
        Sta.Key(h.Filter, Key.Enter);
        Assert.Equal(["b.txt"], h.Activated.Select(e => e.Name));

        h.Filter.Text = ".txt"; // two matches: the first is selected, and Enter opens just that one
        Sta.Key(h.Filter, Key.Enter);
        Assert.Equal(["b.txt", "a.txt"], h.Activated.Select(e => e.Name));

        Sta.Key(h.Filter, Key.Escape);
        Assert.Equal("", h.Filter.Text);
        Assert.Equal(2, h.Shown().Count);
        Assert.False(h.Rows.Active);
    });

    [Fact]
    public void Enter_does_nothing_without_a_match() => Sta.Run(w =>
    {
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("a.txt") }, samePlace: false);
        h.Filter.Text = "zzz";
        Sta.Key(h.Filter, Key.Enter);
        Assert.Empty(h.Activated);
    });

    [Fact]
    public void A_header_click_sorts_and_a_second_click_reverses_with_an_arrow() => Sta.Run(w =>
    {
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("small", 1), D("Folder"), F("big", 100), F("medium", 50) }, samePlace: false);
        Assert.Equal("Name  ▲", h.Name.Header);
        Assert.Equal("Size", h.Size.Header);

        h.ClickHeader(h.Size);
        Assert.Equal(["Folder", "small", "medium", "big"], h.Shown());
        Assert.Equal("Size  ▲", h.Size.Header);
        Assert.Equal("Name", h.Name.Header);

        h.ClickHeader(h.Size);
        Assert.Equal(["Folder", "big", "medium", "small"], h.Shown()); // folders stay on top
        Assert.Equal("Size  ▼", h.Size.Header);
        Assert.True(h.Rows.Descending);

        h.ClickHeader(h.Name); // another column starts ascending
        Assert.Equal(["Folder", "big", "medium", "small"], h.Shown());
        Assert.False(h.Rows.Descending);
        Assert.Equal(FileSortColumn.Name, h.Rows.SortBy);
    });

    [Fact]
    public void Another_place_starts_unfiltered_and_a_refresh_keeps_the_filter() => Sta.Run(w =>
    {
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("a.log"), F("b.txt") }, samePlace: false);
        h.Filter.Text = "log";
        h.Rows.SetRows(new List<FileEntry> { F("a.log"), F("b.txt"), F("c.log") }, samePlace: true);
        Assert.Equal("log", h.Filter.Text);
        Assert.Equal(["a.log", "c.log"], h.Shown());

        h.Rows.SetRows(new List<FileEntry> { F("x.log"), F("y.txt") }, samePlace: false);
        Assert.Equal("", h.Filter.Text);
        Assert.Equal(2, h.Shown().Count);
    });

    [Fact]
    public void Revealing_a_hidden_row_clears_the_filter() => Sta.Run(w =>
    {
        var h = new Harness(w);
        var created = F("new.txt");
        h.Rows.SetRows(new List<FileEntry> { F("a.log"), created }, samePlace: false);
        h.Filter.Text = "log";
        h.Rows.Reveal(created);
        Assert.Equal("", h.Filter.Text);
        Assert.Same(created, h.List.SelectedItem);
    });

    // ------------------------------------------------------------------ the file pane itself

    [Fact]
    public void A_file_pane_filters_its_folder_opens_a_matching_folder_with_Enter_and_resets_in_the_next_folder() => Sta.Run(w =>
    {
        var listed = new List<string>();
        Task<Listing> List(string path)
        {
            listed.Add(path);
            return Task.FromResult(path == @"C:\x\Logs"
                ? new Listing(path, [F("today.log"), F("old.log")])
                : new Listing(@"C:\x", [D("Logs"), D("Data"), F("log-viewer.exe"), F("readme.txt")]));
        }
        var pane = new FilePane("This PC", "Desktop24", List, _ => Task.FromResult(""));
        w.Content = pane;
        Sta.Pump();
        Assert.True(pane.Navigate(@"C:\x").GetAwaiter().GetResult());
        var filter = Sta.Find<TextBox>(pane, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter (Ctrl+F)" });
        var status = Sta.All<TextBlock>(pane).Last(t => t.Text.Contains("folders,"));

        filter.Text = "log";
        Assert.Equal(["Logs"], pane.SelectedEntries.Select(e => e.Name));
        Assert.Contains("2 shown", status.Text);

        Sta.Key(filter, Key.Enter);
        Sta.Pump();
        Assert.Equal(@"C:\x\Logs", pane.CurrentPath);
        Assert.Equal("", filter.Text);
        Assert.DoesNotContain("shown", status.Text);

        filter.Text = "today";
        pane.Refresh().GetAwaiter().GetResult();
        Assert.Equal("today", filter.Text); // a refresh of the same folder keeps it
    });

    [Fact]
    public void Ctrl_F_finds_the_filter_of_the_view_the_focus_is_in() => Sta.Run(w =>
    {
        var pane = new FilePane("This PC", "Desktop24", p => Task.FromResult(new Listing(p, [])), _ => Task.FromResult(""));
        var outside = new Button();
        var panel = new StackPanel();
        panel.Children.Add(outside);
        panel.Children.Add(pane);
        w.Content = panel;
        Sta.Pump();
        var inside = Sta.Find<ListView>(pane);
        Assert.Same(pane, MainWindow.FilterableAncestor(inside));
        Assert.Null(MainWindow.FilterableAncestor(outside));
        Assert.Null(MainWindow.FilterableAncestor(null));

        pane.FocusFilter();
        var filter = Sta.Find<TextBox>(pane, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter (Ctrl+F)" });
        Assert.True(filter.IsKeyboardFocusWithin || filter.IsFocused || Keyboard.FocusedElement == filter);
    });

    // ------------------------------------------------------------------ services (the shared remote list)

    [Fact]
    public void The_services_filter_selects_the_first_match() => Sta.Run(w =>
    {
        var view = new ServicesView(new ServicesSession(new Connection { Name = "SRV01", Host = "srv01" }), () => 0, () => { }, () => { });
        w.Content = view;
        Sta.Pump();
        var rows = (ObservableCollection<ServiceRow>)typeof(RemoteListView<ServiceRow>)
            .GetField("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        foreach (var (name, display) in new[] { ("Spooler", "Print Spooler"), ("WinRM", "Windows Remote Management"), ("wuauserv", "Windows Update") })
            rows.Add(new ServiceRow(new ServiceInfo(name, display, "Running", ServiceStartup.Automatic, "LocalSystem", 1, "", "", true)));
        var list = Sta.Find<ListView>(view);
        var filter = Sta.Find<TextBox>(view, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter" });

        filter.Text = "windows";
        Assert.Equal(2, list.Items.Count);
        Assert.Single(list.SelectedItems);
        Assert.Same(list.Items[0], list.SelectedItem);

        view.FocusFilter();
        Assert.True(filter.IsKeyboardFocusWithin || Keyboard.FocusedElement == filter);
    });

    [Fact]
    public void Right_clicking_another_service_selects_it_without_an_error() => Sta.Run(w =>
    {
        var view = new ServicesView(new ServicesSession(new Connection { Name = "SRV01", Host = "srv01" }), () => 0, () => { }, () => { });
        w.Content = view;
        var rows = (ObservableCollection<ServiceRow>)typeof(RemoteListView<ServiceRow>)
            .GetField("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        rows.Add(new ServiceRow(new ServiceInfo("A", "Alpha", "Running", ServiceStartup.Automatic, "LocalSystem", 1, "", "", true)));
        rows.Add(new ServiceRow(new ServiceInfo("B", "Beta", "Running", ServiceStartup.Automatic, "LocalSystem", 2, "", "", true)));
        Sta.Pump();
        var list = Sta.Find<ListView>(view);
        list.SelectedItem = rows[0];
        Sta.Pump();
        var second = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(rows[1]);
        // WPF turns a mouse-down into the per-element right-button events, as a real click does.
        second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Assert.Same(rows[1], list.SelectedItem);
        Assert.Single(list.SelectedItems);
    });

    [Fact]
    public void Filter_and_path_boxes_and_favourites_have_names_screen_readers_and_the_lab_use() => Sta.Run(w =>
    {
        var pane = new FilePane("This PC", "Desktop24", p => Task.FromResult(new Listing(p, [])), _ => Task.FromResult(""));
        w.Content = pane;
        Sta.Pump();
        var names = Sta.All<TextBox>(pane).Select(System.Windows.Automation.AutomationProperties.GetName).ToList();
        Assert.Contains("Filter This PC", names);
        Assert.Contains("Path This PC", names);
        Assert.Equal("Favourites", new FavoritesGroup().ToString());
        Assert.Equal("DC01 (favourite)", new FavoriteLink(new Connection { Name = "DC01" }).ToString());
    });

    [Fact]
    public void Rows_that_arrive_after_the_filter_was_typed_get_the_first_match_selected() => Sta.Run(w =>
    {
        // the services list loads in the background: typing before the first load must still select a match
        var view = new ServicesView(new ServicesSession(new Connection { Name = "SRV01", Host = "srv01" }), () => 0, () => { }, () => { });
        w.Content = view;
        Sta.Pump();
        var rows = (ObservableCollection<ServiceRow>)typeof(RemoteListView<ServiceRow>)
            .GetField("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        var list = Sta.Find<ListView>(view);
        Sta.Find<TextBox>(view, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter" }).Text = "spool";
        Assert.Null(list.SelectedItem);
        rows.Add(new ServiceRow(new ServiceInfo("WinRM", "Windows Remote Management", "Running", ServiceStartup.Automatic, "NetworkService", 2, "", "", true)));
        rows.Add(new ServiceRow(new ServiceInfo("Spooler", "Print Spooler", "Running", ServiceStartup.Automatic, "LocalSystem", 1, "", "", true)));
        Assert.Equal("Spooler", ((ServiceRow)list.SelectedItem!).Name);

        // and in a file pane: a refresh of the same folder that brings the first match
        var h = new Harness(w);
        h.Rows.SetRows(new List<FileEntry> { F("a.txt") }, samePlace: false);
        h.Filter.Text = "log";
        Assert.Null(h.List.SelectedItem);
        h.Rows.SetRows(new List<FileEntry> { F("a.txt"), F("new.log") }, samePlace: true);
        Assert.Equal("new.log", ((FileEntry)h.List.SelectedItem!).Name);
    });

    [Fact]
    public void A_refresh_with_a_filter_keeps_the_selection_the_user_made() => Sta.Run(w =>
    {
        var pane = new FilePane("This PC", "Desktop24", p => Task.FromResult(new Listing(@"C:\x", [F("a.log"), F("b.log"), F("c.txt")])), _ => Task.FromResult(""));
        w.Content = pane;
        Sta.Pump();
        pane.Navigate(@"C:\x").GetAwaiter().GetResult();
        Sta.Find<TextBox>(pane, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter (Ctrl+F)" }).Text = "log";
        var list = Sta.Find<ListView>(pane);
        list.SelectedItem = list.Items.Cast<FileEntry>().Single(e => e.Name == "b.log");
        pane.Refresh().GetAwaiter().GetResult();
        Assert.Equal(["b.log"], pane.SelectedEntries.Select(e => e.Name));
    });

    [Fact]
    public void A_registry_key_typed_while_the_start_key_still_loads_is_the_one_shown() => Sta.Run(w =>
    {
        var slow = new TaskCompletionSource<RegListing>();
        var hklmCalls = 0;
        RegListing Key(string path, params string[] subKeys) =>
            new(path, subKeys.Select(k => new RegSubKey(k, false)).ToList(), [new RegValue("", RegKind.String, "", IsSet: false)]);
        var session = new RegistrySession(new Connection { Name = "SRV01", Host = "srv01" }, null)
        {
            // the first listing of HKLM (the start key's walk) is slow; the one for the typed key answers at once
            Lister = path => path switch
            {
                "HKEY_LOCAL_MACHINE" when ++hklmCalls == 1 => slow.Task,
                "HKEY_LOCAL_MACHINE" => Task.FromResult(Key(path, "SOFTWARE")),
                @"HKEY_LOCAL_MACHINE\SOFTWARE" => Task.FromResult(Key(path, "RemoteHubLab")),
                _ => Task.FromResult(Key(path)),
            },
        };
        session.State = RemoteHub.Rdp.SessionState.Connected;
        var view = new RegistryView(session, () => { }, () => { });
        w.Content = view;
        Sta.Pump();

        var start = view.Navigate(@"HKEY_LOCAL_MACHINE\SOFTWARE");          // what the tab opens first
        var typed = view.Navigate(@"HKLM\SOFTWARE\RemoteHubLab");            // what the user types meanwhile
        for (var i = 0; i < 20 && !typed.IsCompleted; i++) Sta.Pump();
        Assert.True(typed.IsCompleted && typed.Result);
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\RemoteHubLab", view.CurrentPath);

        slow.SetResult(Key("HKEY_LOCAL_MACHINE", "SOFTWARE"));               // the start key's walk ends later
        for (var i = 0; i < 20 && !start.IsCompleted; i++) Sta.Pump();
        Assert.False(start.Result);                                          // it gave way
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\RemoteHubLab", view.CurrentPath);
    });
}
