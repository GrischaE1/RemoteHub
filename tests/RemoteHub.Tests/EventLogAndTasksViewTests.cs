using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteHub.Tests;

/// <summary>
/// The Event Viewer and Scheduled tasks tabs on screen: their filters and panes, what the list shows and which actions
/// are offered for the selected row.
/// </summary>
public class EventLogAndTasksViewTests
{
    private static Connection Srv => new() { Name = "SRV01", Host = "srv01" };

    private static ObservableCollection<T> Rows<T>(RemoteListView<T> view) where T : class =>
        (ObservableCollection<T>)typeof(RemoteListView<T>)
            .GetField("Rows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;

    private static Button Tool(DependencyObject view, string text) => Sta.Find<Button>(view, b => Equals(b.Content, text));

    private static EventRow Ev(long id, int minutesAgo, EventLevel level, string message) =>
        new(new EventEntry(id, DateTime.UtcNow.AddMinutes(-minutesAgo), level, "Service Control Manager", 7000 + (int)id, message));

    [Fact]
    public void The_event_viewer_has_named_filters_a_message_pane_and_is_refreshed_by_hand() => Sta.Run(w =>
    {
        var view = new EventLogView(new EventLogSession(Srv), () => { }, () => { });
        w.Content = view;
        Sta.Pump();
        var names = Sta.All<FrameworkElement>(view).Select(System.Windows.Automation.AutomationProperties.GetName).ToList();
        foreach (var n in new[] { "Log", "Level", "Time", "Source", "Event message", "Filter" }) Assert.Contains(n, names);
        Assert.Equal("Last 24 hours", (Sta.Find<ComboBox>(view, c => System.Windows.Automation.AutomationProperties.GetName(c) == "Time").SelectedItem as ComboBoxItem)?.Content);
        Assert.NotNull(Tool(view, "Load more"));
        Assert.NotNull(Tool(view, "Export…"));
        // No pause button: nothing refreshes on its own (the services tab has one, for comparison).
        Assert.DoesNotContain(Sta.All<Button>(view), b => b.IsVisible && b.Icon is Wpf.Ui.Controls.SymbolIcon { Symbol: Wpf.Ui.Controls.SymbolRegular.Pause24 or Wpf.Ui.Controls.SymbolRegular.Play24 } && b.Content is null);
        var services = new ServicesView(new ServicesSession(Srv), () => 60, () => { }, () => { });
        w.Content = services;
        Sta.Pump();
        Assert.Contains(Sta.All<Button>(services), b => b.IsVisible && b.Icon is Wpf.Ui.Controls.SymbolIcon { Symbol: Wpf.Ui.Controls.SymbolRegular.Pause24 });
    });

    [Fact]
    public void Events_are_newest_first_and_the_selected_ones_whole_message_shows_below() => Sta.Run(w =>
    {
        var view = new EventLogView(new EventLogSession(Srv), () => { }, () => { });
        w.Content = view;
        var rows = Rows(view);
        rows.Add(Ev(1, 30, EventLevel.Information, "Old"));
        rows.Add(Ev(3, 1, EventLevel.Error, "The service terminated unexpectedly.\r\nIt has done this 2 time(s)."));
        rows.Add(Ev(2, 10, EventLevel.Warning, "Middle"));
        Sta.Pump();
        var list = Sta.Find<ListView>(view);
        Assert.Equal([3L, 2L, 1L], list.Items.Cast<EventRow>().Select(r => r.RecordId));
        var message = Sta.Find<TextBox>(view, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Event message");
        Assert.True(message.IsReadOnly);
        Assert.Equal("", message.Text);

        list.SelectedItem = rows[1];
        Sta.Pump();
        Assert.Equal("The service terminated unexpectedly.\r\nIt has done this 2 time(s).", message.Text);
        Assert.Equal("The service terminated unexpectedly.", rows[1].FirstLine);
        list.SelectedItem = null;
        Sta.Pump();
        Assert.Equal("", message.Text);

        // The filter box narrows what was read: by level, source, event ID or words of the message.
        var filter = Sta.Find<TextBox>(view, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter" });
        filter.Text = "error";
        Assert.Single(list.Items);
        filter.Text = "7002";
        Assert.Equal(2L, ((EventRow)list.Items[0]!).RecordId);
        // Not connected: neither more events nor an export can be asked for.
        Assert.False(Tool(view, "Load more").IsEnabled);
        Assert.False(Tool(view, "Export…").IsEnabled);
    });

    private static TaskRow Task(string folder, string name, TaskState state) =>
        new(new TaskInfo(folder, name, state, null, null, 0x41303, "", "SYSTEM", ""));

    private static void ShowFolders(ScheduledTasksView view, params string[] folders) =>
        typeof(ScheduledTasksView).GetMethod("ShowFolders", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, [folders.ToList()]);

    [Fact]
    public void The_folder_tree_shows_the_chosen_folders_tasks_and_the_filter_searches_every_folder() => Sta.Run(w =>
    {
        var view = new ScheduledTasksView(new ScheduledTasksSession(Srv), () => 60, () => { }, () => { });
        w.Content = view;
        var rows = Rows(view);
        rows.Add(Task(@"\", "Backup", TaskState.Ready));
        rows.Add(Task(@"\Microsoft\Windows\Defrag\", "ScheduledDefrag", TaskState.Ready));
        rows.Add(Task(@"\Microsoft\Windows\Defrag\", "Report", TaskState.Disabled));
        ShowFolders(view, @"\", @"\Microsoft\", @"\Microsoft\Windows\", @"\Microsoft\Windows\Defrag\");
        Sta.Pump();
        var tree = Sta.Find<TreeView>(view);
        var library = (TaskFolderNode)tree.Items[0]!;
        Assert.Equal("Task Scheduler Library", library.Name);
        Assert.True(library.IsSelected);
        var list = Sta.Find<ListView>(view);
        Assert.Equal(["Backup"], list.Items.Cast<TaskRow>().Select(r => r.Name));

        // Opened as the user opens it: Microsoft, Windows, then a click on Defrag.
        var microsoft = library.Children.Single();
        microsoft.IsExpanded = true;
        Sta.Pump();
        microsoft.Children.Single().IsExpanded = true;
        Sta.Pump();
        var defrag = microsoft.Children.Single().Children.Single();
        Assert.Equal("Defrag", defrag.Name);
        defrag.IsSelected = true;
        Sta.Pump();
        Assert.Equal(["Report", "ScheduledDefrag"], list.Items.Cast<TaskRow>().Select(r => r.Name));

        var filter = Sta.Find<TextBox>(view, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter" });
        filter.Text = "backup";
        Assert.Equal(["Backup"], list.Items.Cast<TaskRow>().Select(r => r.Name));
        filter.Text = "";
        Assert.Equal(2, list.Items.Count);

        // A refresh with the same folders keeps the open folder and its expansion.
        ShowFolders(view, @"\", @"\Microsoft\", @"\Microsoft\Windows\", @"\Microsoft\Windows\Defrag\", @"\Microsoft\Windows\New\");
        Sta.Pump();
        var again = (TaskFolderNode)tree.Items[0]!;
        Assert.True(again.Children.Single().Children.Single().Children.Single(n => n.Name == "Defrag").IsSelected);
        Assert.Equal(2, list.Items.Count);
    });

    [Fact]
    public void RemoteHubs_own_tasks_cant_be_ended_or_disabled_and_the_buttons_follow_the_state() => Sta.Run(w =>
    {
        var session = new ScheduledTasksSession(Srv) { State = RemoteHub.Rdp.SessionState.Connected };
        var view = new ScheduledTasksView(session, () => 60, () => { }, () => { });
        w.Content = view;
        var rows = Rows(view);
        var own = Task(@"\RemoteHub\", "RemoteHub-Run-1", TaskState.Running);
        var windows = Task(@"\", "Cleanup", TaskState.Running);
        var off = Task(@"\", "Old", TaskState.Disabled);
        rows.Add(own); rows.Add(windows); rows.Add(off);
        ShowFolders(view, @"\", @"\RemoteHub\");
        var filter = Sta.Find<TextBox>(view, t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "Filter" });
        filter.Text = "e"; // every folder
        Sta.Pump();
        var list = Sta.Find<ListView>(view);
        Button run = Tool(view, "Run"), end = Tool(view, "End"), enable = Tool(view, "Enable"), disable = Tool(view, "Disable");

        list.SelectedItem = own;
        Sta.Pump();
        Assert.False(end.IsEnabled);
        Assert.False(disable.IsEnabled);
        Assert.Contains("RemoteHub's own task", end.ToolTip as string);
        Assert.False(run.IsEnabled); // already running

        list.SelectedItem = windows;
        Sta.Pump();
        Assert.True(end.IsEnabled);
        Assert.True(disable.IsEnabled);
        Assert.False(enable.IsEnabled);
        Assert.Equal("End the running task", end.ToolTip);

        list.SelectedItem = off;
        Sta.Pump();
        Assert.True(enable.IsEnabled);
        Assert.False(run.IsEnabled);
        Assert.False(disable.IsEnabled);
    });
}
