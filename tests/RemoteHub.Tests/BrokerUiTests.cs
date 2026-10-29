using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Agent;
using RemoteHub.Broker;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.ViewModels;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Tests;

/// <summary>
/// The RemoteHub Broker in RemoteHub's UI: the .rhbroker file, the agent's install command line, the synced folder's rules
/// and the devices' state in the navigation, Reach by policy, and Settings → RemoteHub Broker.
/// </summary>
[Collection("Policies")] // the settings page tests set this PC's policy and RemoteHub's application resources
public class BrokerUiTests
{
    private const string Org = "o_aaaaaaaaaaaaaaaa";

    private static string SecretText(BrokerProtocol.SecretKind kind, string org = Org) => new BrokerProtocol.Secret(kind, org, "s_aaaaaaaa", new byte[32]).ToString();

    private static string RhBroker(string broker, string secret, string org = Org, int v = 1) =>
        $$"""{"v":{{v}},"broker":"{{broker}}","org":"{{org}}","name":"Contoso Ltd","consoleSecret":"{{secret}}"}""";

    [Fact]
    public void A_rhbroker_file_gives_the_broker_the_organisation_and_the_console_secret()
    {
        var secret = SecretText(BrokerProtocol.SecretKind.Console);
        var f = BrokerFile.Parse(RhBroker("https://rh.contoso.com", secret));
        Assert.Equal(("https://rh.contoso.com", Org, "Contoso Ltd", secret), (f.Url, f.Org, f.Name, f.ConsoleSecret));
    }

    [Fact]
    public void A_file_that_isnt_a_usable_rhbroker_file_is_refused_with_a_reason()
    {
        static string Refused(string text) => Assert.Throws<BrokerException>(() => BrokerFile.Parse(text, "contoso.rhbroker")).Message;
        var console = SecretText(BrokerProtocol.SecretKind.Console);
        Assert.Contains("isn't a RemoteHub Broker file", Refused("not json"));
        Assert.Contains("newer version", Refused(RhBroker("https://rh.contoso.com", console, v: 2)));
        Assert.Contains("no valid broker address", Refused(RhBroker("ftp://rh.contoso.com", console)));
        Assert.Contains("no console secret", Refused(RhBroker("https://rh.contoso.com", SecretText(BrokerProtocol.SecretKind.Device))));
        Assert.Contains("another organisation", Refused(RhBroker("https://rh.contoso.com", console, org: "o_bbbbbbbbbbbbbbbb")));
    }

    [Fact]
    public void The_install_command_line_passes_the_broker_and_the_device_secret_as_MSI_properties() =>
        Assert.Equal($"msiexec /i {AgentInstaller.PackageName} BROKER_URL=https://rh.contoso.com BROKER_SECRET=rhd1.secret /qn",
            EditorWindow.InstallCommand("https://rh.contoso.com", "rhd1.secret"));

    // ---- The synced folder in the navigation ---------------------------------------------------------------------------

    private sealed record SyncedTree(Folder Root, Folder NewDevices, Folder Servers, Connection Device, Connection Placed);

    private static SyncedTree Synced()
    {
        BrokerRef Ref(BrokerItemKind kind, string item = "") => new() { Connection = "b1", Kind = kind, Item = item, Connected = true };
        var root = new Folder { Name = "Contoso Ltd – RemoteHub Broker", Broker = Ref(BrokerItemKind.Root) };
        var fresh = new Folder { Name = BrokerSync.NewDevicesName, Broker = Ref(BrokerItemKind.NewDevices), Parent = root };
        var servers = new Folder { Name = "Servers", Broker = Ref(BrokerItemKind.Folder, "f1"), Parent = root };
        var device = new Connection { Name = "PC-0815", Broker = Ref(BrokerItemKind.Device, "d1"), Parent = fresh };
        var placed = new Connection { Name = "SRV-SQL01", Broker = Ref(BrokerItemKind.Device, "d2"), Parent = servers };
        device.Broker!.Online = true;
        root.Children.Add(fresh);
        root.Children.Add(servers);
        fresh.Children.Add(device);
        servers.Children.Add(placed);
        return new(root, fresh, servers, device, placed);
    }

    [Fact]
    public void The_synced_folder_keeps_its_devices_and_folders_and_takes_nothing_of_the_users_own()
    {
        var t = Synced();
        var mine = new Folder { Name = "Mine" };
        var own = new Connection { Name = "SRV01" };
        Assert.Null(TreeLayout.WhyNot(t.Device, t.Servers)); // placed into one of its folders
        Assert.Null(TreeLayout.WhyNot(t.Servers, t.Root));
        Assert.Null(TreeLayout.WhyNot(t.Root, null));
        Assert.Contains("stay in its synced folder", TreeLayout.WhyNot(t.Device, mine));
        Assert.Contains("stay in its synced folder", TreeLayout.WhyNot(t.Servers, null));
        Assert.Contains("your own items can't go there", TreeLayout.WhyNot(own, t.Servers));
        Assert.Contains("only devices that registered", TreeLayout.WhyNot(t.Placed, t.NewDevices));
        Assert.Contains("through another device", TreeLayout.WhyNot(t.Device, t.Placed));
        Assert.Contains("stays at the top", TreeLayout.WhyNot(t.Root, mine));
        Assert.Contains("stays where it is", TreeLayout.WhyNot(t.NewDevices, t.Servers));
    }

    [Fact]
    public void A_synced_device_shows_its_state_at_the_broker_as_a_dot_and_only_not_sealed_as_text()
    {
        var d = Synced().Device;
        Assert.Equal(BrokerPresence.Online, d.BrokerPresence);
        Assert.Null(d.RouteHint);
        Assert.True(d.RouteUsable);
        d.Broker!.Online = false;
        Assert.Equal(BrokerPresence.Offline, d.BrokerPresence);
        Assert.True(d.RouteUsable); // no warning: in the same network it may still answer directly
        d.Broker.Online = null;
        Assert.Equal(BrokerPresence.Unknown, d.BrokerPresence);
        Assert.Contains("hasn't reported it yet", d.RouteToolTip);
        d.Broker.Online = true;
        d.Broker.Connected = false;
        Assert.Equal(BrokerPresence.Unknown, d.BrokerPresence); // a PC that shares the settings but isn't connected doesn't know
        Assert.Contains("isn't connected", d.RouteToolTip);
        Assert.Equal(BrokerPresence.None, new Connection { Name = "SRV01" }.BrokerPresence);
        d.Broker.Sealed = false;
        Assert.Equal(BrokerPresence.Unknown, d.BrokerPresence);
        Assert.Equal("not sealed", d.RouteHint);
        Assert.True(d.BrokerUnsealed); // greyed
        Assert.False(d.RouteUsable); // in the warning colour
        Assert.Contains("Not sealed by your organisation", d.RouteToolTip);
    }

    [Fact]
    public void A_synced_device_always_uses_its_agent_and_the_editor_says_why_it_cant_be_changed()
    {
        var t = Synced();
        var data = new AppData();
        t.Root.Options.UseAgent = false; // an inherited "PowerShell remoting" doesn't reach through the broker
        t.Device.Options.UseAgent = false;
        Assert.True(Inheritance.UsesAgent(t.Device, data));
        Assert.True(Inheritance.UsesAgent(t.Placed, data));
        Assert.False(Inheritance.UsesAgent(new Connection { Name = "SRV01", Host = "srv01" }, data)); // ordinary items as before

        foreach (NodeBase n in new NodeBase[] { t.Device, t.Servers })
        {
            var row = new EditorViewModel(n, data, isNew: false).PsRows.Single(r => r.Label == "Connect through");
            Assert.False(row.IsEditable);
            Assert.Contains("always reached through their RemoteHub Agent", row.PolicyNote);
        }
        Assert.True(new EditorViewModel(new Connection { Name = "SRV01", Host = "srv01" }, data, isNew: false).PsRows.Single(r => r.Label == "Connect through").IsEditable);
    }

    [Fact]
    public void Presence_and_the_connection_state_update_the_devices_line_live()
    {
        var top = new ObservableCollection<NodeBase>();
        var sync = new BrokerSync(top, () => { });
        var b = new BrokerConnection("b1", "https://rh.contoso.com", Org, "Contoso Ltd", Guid.NewGuid().ToString(), "Test", DateTime.UtcNow, 0);
        var root = sync.Root(b);
        Assert.Equal(("Contoso Ltd – RemoteHub Broker", "CloudLink24"), (root.Name, root.Icon));
        var fresh = (Folder)root.Children.Single();
        var device = new Connection { Name = "PC-0815", Broker = new BrokerRef { Connection = "b1", Kind = BrokerItemKind.Device, Item = "d1" }, Parent = fresh };
        fresh.Children.Add(device);
        var changed = new List<string?>();
        device.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        sync.SetConnected(b, true);
        sync.Presence(b, "d1", online: true);
        Assert.Equal(BrokerPresence.Online, device.BrokerPresence);
        Assert.Contains(nameof(NodeBase.BrokerPresence), changed);
        sync.Presence(b, "d1", online: false);
        Assert.Equal(BrokerPresence.Offline, device.BrokerPresence);
    }

    [Fact]
    public void A_sealed_broker_device_offers_its_agent_tools_without_a_test_first()
    {
        var t = Synced();
        // Registered at the broker with its agent: tools, Run script and device actions without Test RemoteHub Agent.
        Assert.True(MainWindow.ToolsReady(t.Device, viaAgent: true, PsRemotingState.Unknown));
        Assert.True(MainWindow.ToolsReady(t.Device, viaAgent: true, PsRemotingState.Unavailable));
        // Not sealed: RemoteHub doesn't connect to it, so nothing is offered.
        t.Device.Broker!.Sealed = false;
        Assert.False(MainWindow.ToolsReady(t.Device, viaAgent: true, PsRemotingState.Unknown));
        t.Device.Broker.Sealed = true;
        // UseAgent = 0 by policy (WinRM): as for any connection, once its test passed.
        Assert.False(MainWindow.ToolsReady(t.Device, viaAgent: false, PsRemotingState.Unknown));
        // Ordinary connections as before: after a passed test.
        var own = new Connection { Name = "SRV01", Host = "srv01" };
        Assert.False(MainWindow.ToolsReady(own, viaAgent: true, PsRemotingState.Unknown));
        Assert.True(MainWindow.ToolsReady(own, viaAgent: true, PsRemotingState.Available));
    }

    [Fact]
    public void A_presence_snapshot_sets_every_device_at_once_and_tells_only_the_ones_that_changed()
    {
        var top = new ObservableCollection<NodeBase>();
        var sync = new BrokerSync(top, () => { });
        var b = new BrokerConnection("b1", "https://rh.contoso.com", Org, "Contoso Ltd", Guid.NewGuid().ToString(), "Test", DateTime.UtcNow, 0);
        var fresh = (Folder)sync.Root(b).Children.Single();
        Connection Device(string id, string name)
        {
            var c = new Connection { Name = name, Broker = new BrokerRef { Connection = "b1", Kind = BrokerItemKind.Device, Item = id }, Parent = fresh };
            fresh.Children.Add(c);
            return c;
        }
        var hv01 = Device("d1", "HV01");
        var hv02 = Device("d2", "HV02");
        var hv03 = Device("d3", "HV03");
        sync.SetConnected(b, true);
        // Restarted: nothing known yet, every dot yellow until the snapshot arrives.
        Assert.All(new[] { hv01, hv02, hv03 }, d => Assert.Equal(BrokerPresence.Unknown, d.BrokerPresence));

        var told = new List<string>();
        foreach (var d in new[] { hv01, hv02, hv03 }) d.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NodeBase.BrokerPresence)) told.Add(d.Name); };
        sync.PresenceAll(b, [("d1", true), ("d2", false), ("d9", true)]); // d9: not in this tree (yet) – ignored
        Assert.Equal((BrokerPresence.Online, BrokerPresence.Offline, BrokerPresence.Unknown), (hv01.BrokerPresence, hv02.BrokerPresence, hv03.BrokerPresence));
        Assert.Equal(["HV01", "HV02"], told);

        // The next snapshot a minute later: only HV02 changed.
        told.Clear();
        sync.PresenceAll(b, [("d1", true), ("d2", true), ("d3", true)]);
        Assert.Equal(["HV02", "HV03"], told);
        Assert.All(new[] { hv01, hv02, hv03 }, d => Assert.Equal(BrokerPresence.Online, d.BrokerPresence));
    }

    [Fact]
    public void A_presence_page_is_checked_and_asked_for_only_when_the_broker_names_it()
    {
        static JsonObject Page(bool more, params (string Id, bool Online)[] items) => new()
        {
            ["t"] = "presenceList",
            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["deviceId"] = i.Id, ["online"] = i.Online, ["lastSeen"] = 1793023426 }).ToArray()),
            ["more"] = more,
        };
        const string a = "0d3f5c1e-1111-4a2b-9c3d-000000000001", c = "0d3f5c1e-1111-4a2b-9c3d-000000000003";
        var (items, more) = BrokerClient.ReadPresencePage(Page(true, (a, true), (c, false)), null);
        Assert.Equal([(a, true), (c, false)], items);
        Assert.True(more);
        // Out of order, repeating the last page, or not a device id: refused (a broker can't keep the console reading).
        Assert.Throws<InvalidDataException>(() => BrokerClient.ReadPresencePage(Page(false, (c, true), (a, true)), null));
        Assert.Throws<InvalidDataException>(() => BrokerClient.ReadPresencePage(Page(false, (c, true)), c));
        Assert.Throws<InvalidDataException>(() => BrokerClient.ReadPresencePage(Page(false, ("../../etc", true)), null));
        Assert.Throws<InvalidDataException>(() => BrokerClient.ReadPresencePage(new JsonObject { ["t"] = "presenceList" }, null));

        Assert.True(BrokerClient.HasFeature(new JsonObject { ["t"] = "welcome", ["features"] = new JsonArray("presenceList") }, "presenceList"));
        // A broker before it sends no features: the request isn't sent (it would close the connection).
        Assert.False(BrokerClient.HasFeature(new JsonObject { ["t"] = "welcome" }, "presenceList"));
        Assert.False(BrokerClient.HasFeature(new JsonObject { ["t"] = "welcome", ["features"] = "presenceList" }, "presenceList"));
    }

    [Fact]
    public void New_items_go_into_the_synced_folder_only_as_folders_and_never_into_New_devices()
    {
        var t = Synced();
        var vm = new MainViewModel(new AppData());
        vm.Items.Add(t.Root);
        vm.SelectedNode = t.Device;
        Assert.Null(vm.TargetFolderForNew()); // a connection of one's own goes to the top level
        Assert.Same(t.Root, vm.TargetFolderForNew(folder: true));
        vm.SelectedNode = t.Servers;
        Assert.Same(t.Servers, vm.TargetFolderForNew(folder: true));
    }

    // ---- Reach ---------------------------------------------------------------------------------------------------------

    private sealed class ConnectionsSource(string name, object value) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string n) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root + "\\" + PolicyCatalog.ConnectionsKey, StringComparison.OrdinalIgnoreCase) && n == name ? value : null;
    }

    [Fact]
    public void Reach_set_by_policy_applies_to_every_connection()
    {
        Assert.Equal(BrokerReach.Broker, AppPolicy.ConnectionsFrom(new ConnectionsSource("Reach", "Broker")).Reach);
        Assert.Equal(BrokerReach.Direct, AppPolicy.ConnectionsFrom(new ConnectionsSource("Reach", "direct")).Reach);
        Assert.Null(AppPolicy.ConnectionsFrom(new ConnectionsSource("Reach", "Sometimes")).Reach); // invalid: not set
        Assert.Equal(BrokerReach.Automatic, RdpOptions.BuiltInDefaults().Reach);
    }

    // ---- Settings → RemoteHub Broker -------------------------------------------------------------------------------------

    private sealed class FeatureSource(string name, int value) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string n) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && n == name ? value : null;
    }

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, b));

    /// <summary>
    /// The settings window takes its styles from App.xaml: an Application with RemoteHub's resources while the test runs,
    /// removed again afterwards so other tests see none, as before.
    /// </summary>
    internal static void WithAppResources(Action test)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/RemoteHub;component/Themes/Styles.xaml") });
        try { test(); }
        finally
        {
            foreach (var name in new[] { "_appInstance", "_appCreatedInThisAppDomain" })
            {
                var field = typeof(Application).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
                field.SetValue(null, field.FieldType == typeof(bool) ? false : null);
            }
        }
    }

    private static EditorWindow Settings(Window owner, AppSettingsHost host)
    {
        var w = Sta.Theme(new EditorWindow(null, new AppData(), isNew: false, host, EditorWindow.PageBrokerSettings)
            { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        w.Show();
        Sta.Pump();
        return w;
    }

    private static AppSettingsHost Host(Func<string, string, string, Task<BrokerClient>>? connect = null) =>
        new(_ => { }, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { }, () => { }, () => { },
            () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
            Brokers: () => [], ConnectBroker: connect, DisconnectBroker: _ => Task.CompletedTask, WatchBrokers: _ => new Nothing());

    private sealed class Nothing : IDisposable { public void Dispose() { } }

    [Fact]
    public void The_broker_page_asks_for_the_address_the_console_secret_and_a_name_and_shows_why_connecting_failed() => Sta.Run(owner => WithAppResources(() =>
    {
        string? asked = null;
        var w = Settings(owner, Host(async (url, secret, name) =>
        {
            asked = $"{url}|{secret}|{name}";
            await Task.Yield();
            throw new BrokerException("The RemoteHub Broker refused the console secret – check it with your administrator.", "auth");
        }));
        var page = (StackPanel)w.FindName("PageBroker");
        Assert.Equal(Visibility.Visible, page.Visibility);
        var connect = Sta.Find<Button>(page, b => Equals(b.Content, "Connect"));
        var status = (TextBlock)w.FindName("BrokerStatusText");

        Click(connect);
        Sta.Pump();
        Assert.Contains("Enter the broker's address", status.Text);
        Assert.Null(asked);

        ((Wpf.Ui.Controls.TextBox)w.FindName("BrokerUrlBox")).Text = "https://rh.contoso.com";
        ((Wpf.Ui.Controls.PasswordBox)w.FindName("BrokerSecretBox")).Password = SecretText(BrokerProtocol.SecretKind.Console);
        Click(connect);
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); return status.Text.Contains("refused", StringComparison.Ordinal); }, TimeSpan.FromSeconds(10)), status.Text);
        Assert.StartsWith("https://rh.contoso.com|rhc1.", asked);
        Assert.EndsWith($"– {Environment.MachineName}", asked); // this PC's name, as the broker lists it
        Assert.True(connect.IsEnabled); // ready for another try
        w.Close();
    }));

    [Fact]
    public void Test_shows_its_lines_and_marks_relayed_only_as_a_caution() => Sta.Run(owner => WithAppResources(() =>
    {
        var w = Settings(owner, Host());
        var panel = new StackPanel();
        ((StackPanel)w.FindName("PageBroker")).Children.Add(panel); // in the window, so the theme's brushes resolve
        var done = w.FillTest(panel, async _ =>
        {
            await Task.Delay(50);
            return new BrokerTestResult("Control channel: connected.", "STUN (stun.cloudflare.com:3478): no answer.", false, "Only relayed connections: UDP is blocked from this network.");
        });
        Sta.Pump();
        Assert.Equal("Testing…", Assert.IsType<TextBlock>(Assert.Single(panel.Children)).Text);
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); return done.IsCompleted; }, TimeSpan.FromSeconds(5)));
        Sta.Pump();
        var lines = panel.Children.OfType<TextBlock>().ToList();
        Assert.Equal(["Control channel: connected.", "STUN (stun.cloudflare.com:3478): no answer.", "Only relayed connections: UDP is blocked from this network."], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Same(w.FindResource("Caption"), l.Style));
        Assert.Equal(w.FindResource("SystemFillColorCautionBrush"), lines[2].Foreground);

        done = w.FillTest(panel, _ => Task.FromResult(new BrokerTestResult("Control channel: connected.", "STUN (…): answered.", true, "Direct connections possible.")));
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); return done.IsCompleted; }, TimeSpan.FromSeconds(5)));
        Assert.Equal(w.FindResource("TextFillColorSecondaryBrush"), ((TextBlock)panel.Children[2]).Foreground);

        done = w.FillTest(panel, _ => throw new InvalidOperationException("No socket."));
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); return done.IsCompleted; }, TimeSpan.FromSeconds(5)));
        Assert.Equal("The test couldn't run: No socket.", Assert.IsType<TextBlock>(Assert.Single(panel.Children)).Text);
        w.Close();
    }));

    [Fact]
    public void The_broker_page_says_why_when_this_PCs_policy_turns_the_broker_off() => Sta.Run(owner => WithAppResources(() =>
    {
        AppPolicy.Source = new FeatureSource("AllowBroker", 0);
        try
        {
            var w = Settings(owner, Host());
            var policy = (TextBlock)w.FindName("BrokerPolicyText");
            Assert.Equal(Visibility.Visible, policy.Visibility);
            Assert.Contains("RemoteHub doesn't connect to a RemoteHub Broker", policy.Text);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)w.FindName("BrokerConnectSection")).Visibility); // nothing offered
            w.Close();
        }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }));
}
