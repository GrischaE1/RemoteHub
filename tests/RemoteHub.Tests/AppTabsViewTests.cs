using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using RemoteHub.Agent;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.ViewModels;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;

namespace RemoteHub.Tests;

/// <summary>
/// App tabs as the user sees them (docs/app-tabs-design.md §8): the device's Start menu picker, the Apps page of a
/// connection's settings, and an app tab's own panels.
/// </summary>
[Collection("Policies")] // the settings window takes RemoteHub's application resources (BrokerUiTests.WithAppResources)
public class AppTabsViewTests
{
    private static readonly DesktopListedApp Events = new("Event Viewer", "Administrative Tools", @"%SystemRoot%\system32\mmc.exe", @"""%SystemRoot%\system32\eventvwr.msc""", "", false, null);
    private static readonly DesktopListedApp Services = new("Services", "Administrative Tools", @"%SystemRoot%\system32\mmc.exe", @"""%SystemRoot%\system32\services.msc""", "", true, null);
    private static readonly DesktopListedApp Notepad = new("Notepad", "", @"%SystemRoot%\system32\notepad.exe", "", "", false, [1, 2, 3]); // an icon that doesn't decode

    private static readonly DesktopListedApp Terminal = new("Windows Terminal", "Store apps", @"shell:AppsFolder\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "", "", false, null);

    // A device that answers with one part: the Start menu only.
    private static AppPickerWindow Picker(Window owner, Func<CancellationToken, Task<DesktopAppList>> load, IReadOnlyList<ConnectionApp> added, bool single) =>
        Picker(owner, async (part, ct) => part(await load(ct)), added, single);

    private static AppPickerWindow Picker(Window owner, Func<Action<DesktopAppList>, CancellationToken, Task> load, IReadOnlyList<ConnectionApp> added, bool single)
    {
        var w = Sta.Theme(new AppPickerWindow("FS01", load, added, single) { Owner = owner, Left = -20000, Top = -20000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual });
        w.Show();
        for (var i = 0; i < 20 && !Sta.All<ListBoxItem>(w).Any(); i++) Sta.Pump();
        return w;
    }

    /// <summary>A note under the list (no name of its own) that says <paramref name="text"/> and is shown.</summary>
    private static bool NoteShown(Window w, string text) =>
        Sta.All<ListBoxItem>(w).Any(i => i.Visibility == Visibility.Visible && Sta.All<TextBlock>(i).Any(t => t.Text == text));

    private static List<string> Rows(Window w) =>
        Sta.All<ListBoxItem>(w).Where(i => i.Visibility == Visibility.Visible).Select(i => AutomationProperties.GetName(i)).Where(n => n.Length > 0).ToList();
    private static Button ButtonNamed(DependencyObject root, string text) => Sta.All<Button>(root).First(b => Equals(b.Content, text) || AutomationProperties.GetName(b) == text);
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    [Fact]
    public void The_picker_lists_the_start_menu_by_folder_and_adds_what_is_ticked() => Sta.Run(owner =>
    {
        var w = Picker(owner, _ => Task.FromResult(new DesktopAppList([Events, Services, Notepad], false)), [ConnectionApp.From(Events)], single: false);
        Assert.Equal(["Notepad", "Administrative Tools, 2 apps, open", "Event Viewer", "Services"], Rows(w));
        var add = Sta.All<Button>(w).First(b => b.IsDefault);
        Assert.False(add.IsEnabled);

        // Already on the connection: ticked, "Added", not ticked again
        var events = Sta.All<ListBoxItem>(w).First(i => AutomationProperties.GetName(i) == "Event Viewer");
        var eventsTick = Sta.All<CheckBox>(events).First();
        Assert.True(eventsTick.IsChecked);
        Assert.False(eventsTick.IsEnabled);
        Assert.Contains(Sta.All<TextBlock>(events), t => t.Text == "Added");
        Assert.Contains(Sta.All<TextBlock>(Sta.All<ListBoxItem>(w).First(i => AutomationProperties.GetName(i) == "Services")), t => t.Text == "Runs as administrator");

        Sta.All<CheckBox>(Sta.All<ListBoxItem>(w).First(i => AutomationProperties.GetName(i) == "Services")).First().IsChecked = true;
        Assert.True(add.IsEnabled);
        Assert.Equal("Add 1 app", add.Content);

        // a folder closes with a click; searching shows what it finds in it anyway
        Sta.Key(Sta.All<ListBoxItem>(w).First(i => AutomationProperties.GetName(i).StartsWith("Administrative Tools")), System.Windows.Input.Key.Space);
        Sta.Pump();
        Assert.Equal(["Notepad", "Administrative Tools, 2 apps, closed"], Rows(w));
        Sta.All<Wpf.Ui.Controls.TextBox>(w).First().Text = "serv";
        Sta.Pump();
        Assert.Contains("Services", Rows(w));
        Assert.DoesNotContain("Notepad", Rows(w));

        Click(add);
        Assert.Equal(["Services"], w.Chosen.Select(a => a.Name));
        Assert.True(w.Chosen[0].Elevated);
    });

    [Fact]
    public void A_device_that_refuses_says_why_and_can_be_asked_again() => Sta.Run(owner =>
    {
        var asked = 0;
        var w = Picker(owner, _ =>
        {
            asked++;
            return asked == 1 ? throw new InvalidOperationException("FS01 doesn't offer app tabs to your account – they need automatic sign-in there.")
                : Task.FromResult(new DesktopAppList([], false));
        }, [], single: false);
        for (var i = 0; i < 20 && !Sta.All<TextBlock>(w).Any(t => t.Text.Contains("doesn't offer app tabs")); i++) Sta.Pump();
        Assert.Contains(Sta.All<TextBlock>(w), t => t.Text.Contains("doesn't offer app tabs"));
        Click(ButtonNamed(w, "Try again"));
        for (var i = 0; i < 20 && !Sta.All<TextBlock>(w).Any(t => t.Text.Contains("No apps found")); i++) Sta.Pump();
        Assert.Contains(Sta.All<TextBlock>(w), t => t.Text == "No apps found on FS01.");
        Assert.Equal(2, asked);
    });

    [Fact]
    public void The_start_menu_shows_at_once_and_the_store_apps_are_added_when_they_arrive() => Sta.Run(owner =>
    {
        Action<DesktopAppList>? send = null;
        var last = new TaskCompletionSource();
        var w = Picker(owner, async (part, ct) =>
        {
            part(new DesktopAppList([Notepad], false, More: true));
            send = part;
            await last.Task;
        }, [], single: false);
        Assert.Equal(["Notepad"], Rows(w));
        Assert.True(NoteShown(w, "Reading the Store apps on FS01…"));

        send!(new DesktopAppList([Terminal], false));
        last.SetResult();
        for (var i = 0; i < 20 && Rows(w).Count < 3; i++) Sta.Pump();
        Assert.Equal(["Notepad", "Store apps, 1 app, open", "Windows Terminal"], Rows(w));
        Assert.False(NoteShown(w, "Reading the Store apps on FS01…"));

        // Searching finds both parts
        Sta.All<Wpf.Ui.Controls.TextBox>(w).First().Text = "term";
        Sta.Pump();
        Assert.Equal(["Store apps, 1 app, open", "Windows Terminal"], Rows(w));
    });

    [Fact]
    public void Without_store_apps_the_picker_says_why_under_the_start_menu() => Sta.Run(owner =>
    {
        // Not signed in on the device: one part, with the device's note
        var w = Picker(owner, _ => Task.FromResult(new DesktopAppList([Events], false, Note: "Store apps are listed once you're signed in on FS01.")), [], single: false);
        Assert.Equal(["Administrative Tools, 1 app, open", "Event Viewer"], Rows(w));
        Assert.True(NoteShown(w, "Store apps are listed once you're signed in on FS01."));

        // The Store apps didn't come: the Start menu stays, the failure is said under it
        var storeApps = new TaskCompletionSource();
        var failed = Picker(owner, async (part, ct) =>
        {
            part(new DesktopAppList([Notepad], false, More: true));
            await storeApps.Task;
        }, [], single: false);
        storeApps.SetException(new InvalidOperationException("FS01 didn't send its Store apps within 150 s."));
        for (var i = 0; i < 20 && !NoteShown(failed, "FS01 didn't send its Store apps within 150 s."); i++) Sta.Pump();
        Assert.Equal(["Notepad"], Rows(failed));
        Assert.True(NoteShown(failed, "FS01 didn't send its Store apps within 150 s."));
        Assert.False(NoteShown(failed, "Reading the Store apps on FS01…"));
    });

    [Fact]
    public void Choosing_one_to_open_keeps_it_in_the_connection_s_apps_unless_told_not_to() => Sta.Run(owner =>
    {
        var w = Picker(owner, _ => Task.FromResult(new DesktopAppList([Events, Notepad], false)), [], single: true);
        Assert.DoesNotContain(Sta.All<CheckBox>(w), c => c.Content is not string); // no ticks in single choice
        var keep = Sta.All<CheckBox>(w).Single(c => Equals(c.Content, "Keep it in the connection's apps"));
        Assert.True(keep.IsChecked);
        var open = Sta.All<Button>(w).First(b => b.IsDefault);
        Assert.Equal("Open", open.Content);
        Assert.False(open.IsEnabled);
        Sta.All<ListBox>(w).First().SelectedItem = Sta.All<ListBoxItem>(w).First(i => AutomationProperties.GetName(i) == "Notepad");
        Assert.True(open.IsEnabled);
        keep.IsChecked = false;
        Click(open);
        Assert.Equal("Notepad", Assert.Single(w.Chosen).Name);
        Assert.False(w.Keep);
    });

    private static (Connection Connection, Folder Folder, AppData Data) Tree()
    {
        var data = new AppData();
        var folder = new Folder { Name = "Servers", Options = { Apps = [new ConnectionApp("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc")] } };
        var c = new Connection { Name = "FS01", Host = "fs01.contoso.local", Parent = folder, Agent = new AgentDevice { Status = AgentStatus.Installed } };
        folder.Children.Add(c);
        data.Items.Add(folder);
        return (c, folder, data);
    }

    private static EditorWindow Editor(Window owner, NodeBase node, AppData data)
    {
        var w = Sta.Theme(new EditorWindow(node, data, isNew: false, page: "PageApps") { Owner = owner, Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false });
        w.Show();
        Sta.Pump();
        return w;
    }

    [Fact]
    public void The_apps_page_says_app_tabs_need_automatic_sign_in_and_turns_it_on() => Sta.Run(owner => BrokerUiTests.WithAppResources(() =>
    {
        var (c, _, data) = Tree();
        var w = Editor(owner, c, data);
        var vm = (EditorViewModel)w.DataContext;
        Assert.Contains(Sta.All<TextBlock>(w), t => t.IsVisible && t.Text.StartsWith("App tabs need automatic sign-in"));
        var turnOn = ButtonNamed(w, "Turn on automatic sign-in");
        Assert.True(turnOn.IsVisible);
        Click(turnOn);
        Sta.Pump();
        Assert.True(vm.AutoSignInOn);
        Assert.False(turnOn.IsVisible);
        Assert.True(vm.Apply());
        Assert.True(c.Options.AgentAutoSignIn); // saved like the PowerShell remoting page's row
        w.Close();
    }));

    [Fact]
    public void A_connection_shows_its_folder_s_apps_until_it_takes_a_list_of_its_own() => Sta.Run(owner => BrokerUiTests.WithAppResources(() =>
    {
        var (c, folder, data) = Tree();
        var w = Editor(owner, c, data);
        var vm = (EditorViewModel)w.DataContext;
        Assert.False(vm.AppsOwn);
        Assert.Equal("Inherit · 1 app", vm.AppsRow.Selected.Label);
        Assert.Contains("Event Viewer", Rows(w));
        Assert.False(ButtonNamed(w, "Add from the device…").IsEnabled); // the folder's list isn't changed here

        vm.AppsRow.Selected = vm.AppsRow.Choices[1];
        Sta.Pump();
        Assert.True(vm.AppsOwn);
        Assert.True(ButtonNamed(w, "Add from the device…").IsEnabled);
        vm.Apps.Add(new ConnectionApp("Tool", @"C:\Tools\tool.exe"));
        Assert.True(vm.Apply());
        Assert.Equal(["Event Viewer", "Tool"], c.Options.Apps!.Select(a => a.Name)); // started from the folder's, then its own
        Assert.Single(folder.Options.Apps!);
        w.Close();
    }));

    [Fact]
    public void An_app_without_a_program_isnt_saved_and_the_apps_page_is_shown() => Sta.Run(owner => BrokerUiTests.WithAppResources(() =>
    {
        var (c, _, data) = Tree();
        var w = Editor(owner, c, data);
        var vm = (EditorViewModel)w.DataContext;
        vm.AppsRow.Selected = vm.AppsRow.Choices[1];
        vm.Apps.Add(new ConnectionApp("New app", ""));
        Assert.False(vm.Apply());
        Assert.Equal("Give every app a name and a program (Apps page).", vm.Error);
        Assert.Null(c.Options.Apps);
        w.Close();
    }));

    [Fact]
    public void A_store_app_has_no_folder_to_start_in_and_never_runs_as_administrator() => Sta.Run(owner => BrokerUiTests.WithAppResources(() =>
    {
        var (c, folder, data) = Tree();
        folder.Options.Apps = [new ConnectionApp("Windows Terminal", ConnectionApp.StorePrefix + "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"), new ConnectionApp("Tool", @"C:\Tools	ool.exe")];
        var w = Editor(owner, c, data);
        var vm = (EditorViewModel)w.DataContext;
        vm.AppsRow.Selected = vm.AppsRow.Choices[1];
        Sta.Pump();
        var list = Sta.All<ListBox>(w).First(l => AutomationProperties.GetName(l) == "Apps");
        var elevated = Sta.All<CheckBox>(w).First(b => Equals(b.Content, "Run as administrator"));
        var startIn = Sta.All<System.Windows.Controls.TextBox>(w).First(t => t is Wpf.Ui.Controls.TextBox { PlaceholderText: "The program's folder" });
        list.SelectedIndex = 0;
        Sta.Pump();
        Assert.True(vm.Apps[0].IsStoreApp);
        Assert.False(elevated.IsEnabled);
        Assert.False(startIn.IsEnabled);
        list.SelectedIndex = 1;
        Sta.Pump();
        Assert.True(elevated.IsEnabled);
        Assert.True(startIn.IsEnabled);
        w.Close();
    }));

    [Fact]
    public void An_app_tab_shows_only_what_an_app_needs_and_offers_to_start_a_closed_app_again() => Sta.Run(owner =>
    {
        var session = new AgentDesktopSession(new Connection { Name = "FS01", Host = "fs01.contoso.local" }, new ConnectionApp("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc"));
        var closed = 0;
        var view = new AgentDesktopView(session, () => { }, () => closed++, () => { }, () => DesktopQualityPreset.Find(null), (_, _) => { },
            () => new DesktopResolution(DesktopResolutionKind.Fixed, 1024, 768), _ => { });
        owner.Content = view;
        Sta.Pump();
        Assert.Contains(Sta.All<TextBlock>(view), t => t.Text.StartsWith("  ·  App  ·  "));
        Assert.Equal(DesktopResolutionKind.Fit, session.Resolution.Kind); // the tab's size, whatever this PC remembers

        session.StatusText = "Event Viewer was closed.";
        session.Phase = DesktopPhase.AppClosed;
        Sta.Pump();
        var again = ButtonNamed(view, "Start again");
        Assert.True(again.IsVisible);
        Assert.Contains(Sta.All<TextBlock>(view), t => t.IsVisible && t.Text == "Event Viewer was closed.");
        Click(ButtonNamed(view, "Close tab"));
        Assert.Equal(1, closed);

        session.Phase = DesktopPhase.Live;
        session.ClosingApp = true;
        Sta.Pump();
        Assert.False(again.IsVisible);
        Assert.True(ButtonNamed(view, "Leave it running").IsVisible);
        // nothing a desktop has that an app doesn't need
        Assert.DoesNotContain(Sta.All<Button>(view), b => b.IsVisible && (Equals(b.ToolTip, "Send Ctrl+Alt+Del") || Equals(b.ToolTip, "Press the Windows key (Start menu)")));
    });
}
