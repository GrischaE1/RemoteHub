using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteHub.Broker;
using RemoteHub.Remoting;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// Settings → RemoteHub Broker: every organisation this PC is connected to – its state, the
/// agent's install command line and the registered consoles – then Connect for another one.
/// </summary>
public partial class EditorWindow
{
    public const string PageBrokerSettings = "PageBroker";

    /// <summary>How long a copied install command (it holds the device secret) stays on the clipboard.</summary>
    private const int BrokerClipboardSeconds = 120;

    /// <summary>The state line of each listed organisation, updated live while the window is open.</summary>
    private readonly Dictionary<BrokerClient, TextBlock> _brokerStates = new();
    private IDisposable? _brokerWatch;

    private void InitBrokerPage()
    {
        if (!Features.IsAllowed(Feature.Broker))
        {
            // Blocked by this PC's policy: its actions aren't offered (as Security & storage hides the enrollment section
            // with the agent off), and the page says why.
            BrokerPolicyText.Text = "Turned off by your organisation (Settings → Policies). RemoteHub doesn't connect to a RemoteHub Broker, " +
                                    "and devices in synced folders are reached only directly.";
            BrokerPolicyText.Visibility = Visibility.Visible;
            BrokerList.Visibility = BrokerConnectSection.Visibility = Visibility.Collapsed;
            return;
        }
        BrokerNameBox.Text = $"{Environment.UserName} – {Environment.MachineName}";
        BrokerConnectButton.Click += async (_, _) => await ConnectBroker();
        BrokerImportButton.Click += (_, _) => ImportBrokerFile();
        if (_host?.WatchBrokers is { } watch)
        {
            _brokerWatch = watch(c => { if (_brokerStates.TryGetValue(c, out var line)) ShowBrokerState(c, line); });
            Closed += (_, _) => _brokerWatch?.Dispose();
        }
        RefreshBrokerPage();
    }

    private void RefreshBrokerPage()
    {
        BrokerList.Children.Clear();
        _brokerStates.Clear();
        var clients = _host?.Brokers?.Invoke().OrderBy(c => c.Info.OrgName, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
        foreach (var c in clients) BrokerList.Children.Add(BrokerSection(c, first: BrokerList.Children.Count == 0));
        BrokerConnectHeader.Text = clients.Count == 0 ? "Connect to a RemoteHub Broker" : "Connect to another organisation";
        BrokerConnectHeader.Margin = new Thickness(0, clients.Count == 0 ? 0 : 18, 0, 4);
    }

    /// <summary>One organisation: header, state, the buttons, and the install command and consoles below them when asked for.</summary>
    private StackPanel BrokerSection(BrokerClient client, bool first)
    {
        var b = client.Info;
        var section = new StackPanel();
        section.Children.Add(new TextBlock { Text = b.OrgName.Length > 0 ? b.OrgName : b.Org, Style = (Style)FindResource("SectionHeader"), Margin = new Thickness(0, first ? 0 : 18, 0, 4) });
        section.Children.Add(Caption($"{new Uri(b.Url).Authority} · organisation {b.Org} · this PC is \"{b.ConsoleName}\""));
        var state = Caption("");
        _brokerStates[client] = state;
        ShowBrokerState(client, state);
        section.Children.Add(state);

        var install = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var consoles = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(ActionButton("Install on devices", SymbolRegular.Shield24, "The agent's MSI command line for Intune, Group Policy or software distribution", () =>
        {
            install.Visibility = install.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (install.Visibility == Visibility.Visible) FillInstall(install, client, reveal: false);
        }));
        row.Children.Add(ActionButton("Consoles", SymbolRegular.People24, "The RemoteHub installations connected to this organisation", async () =>
        {
            consoles.Visibility = consoles.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (consoles.Visibility == Visibility.Visible) await FillConsoles(consoles, client);
        }));
        var disconnect = ActionButton("Disconnect…", SymbolRegular.PlugDisconnected24, "Removes this PC from the organisation and its synced folder from the navigation", async () => await DisconnectBroker(client));
        disconnect.Margin = new Thickness(0);
        row.Children.Add(disconnect);
        section.Children.Add(row);
        section.Children.Add(install);
        section.Children.Add(consoles);
        return section;
    }

    private void ShowBrokerState(BrokerClient client, TextBlock line)
    {
        line.Text = client.State;
        if (client.IsConnected) line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        else line.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
    }

    /// <summary>
    /// The MSI command line with the organisation's newest device secret – masked until "Show secret", copied whole (and
    /// cleared from the clipboard again) through <see cref="SecureClipboard"/>.
    /// </summary>
    private void FillInstall(StackPanel panel, BrokerClient client, bool reveal)
    {
        panel.Children.Clear();
        if (client.DeviceSecret is not { } secret)
        {
            panel.Children.Add(Caption(client.IsConnected
                ? "This console's keyring holds no device secret of the organisation – ask your administrator for a current console secret."
                : "Shown once RemoteHub is connected to the broker: the device secret comes from the organisation's keyring."));
            return;
        }
        panel.Children.Add(Caption("Install the agent with these properties (Intune, Group Policy, software distribution). The device joins the organisation " +
                                   "and shows in New devices. The device secret lets any device join – handle it like a password."));
        var shown = new TextBox
        {
            Text = InstallCommand(client.Info.Url, reveal ? secret : new string('•', 12)),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 6, 0, 6),
        };
        panel.Children.Add(shown);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var status = Caption("");
        status.Margin = new Thickness(0, 6, 0, 0);
        row.Children.Add(ActionButton("Copy command", SymbolRegular.Copy24, $"Copies the whole command, with the secret; the clipboard is cleared after {BrokerClipboardSeconds / 60} minutes", () =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            status.Text = SecureClipboard.CopySecret(hwnd, InstallCommand(client.Info.Url, secret), TimeSpan.FromSeconds(BrokerClipboardSeconds))
                ? $"Copied – the clipboard is cleared in {BrokerClipboardSeconds / 60} minutes."
                : "The clipboard is in use by another program – try again.";
        }));
        if (!reveal)
        {
            var show = ActionButton("Show secret", SymbolRegular.Eye24, "Shows the device secret in the command", () => FillInstall(panel, client, reveal: true));
            show.Margin = new Thickness(0);
            row.Children.Add(show);
        }
        panel.Children.Add(row);
        panel.Children.Add(status);
    }

    internal static string InstallCommand(string url, string secret) =>
        $"msiexec /i {AgentInstaller.PackageName} BROKER_URL={url} BROKER_SECRET={secret} /qn";

    private async Task FillConsoles(StackPanel panel, BrokerClient client)
    {
        panel.Children.Clear();
        panel.Children.Add(Caption("Loading…"));
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> items;
        try { items = await client.ConsolesAsync(); }
        catch (Exception ex)
        {
            panel.Children.Clear();
            panel.Children.Add(Caption("The list of consoles couldn't be loaded: " + ex.Message));
            return;
        }
        panel.Children.Clear();
        foreach (var item in items.Where(i => i["removed"]?.GetValue<bool>() != true))
        {
            var id = item["id"]?.GetValue<string>() ?? "";
            var name = item["name"]?.GetValue<string>() ?? "";
            var self = id == client.Info.ConsoleId;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            left.Children.Add(new TextBlock { Text = self ? $"{name} (this PC)" : name, TextWrapping = TextWrapping.Wrap });
            left.Children.Add(Caption($"Connected since {Date(item["registered"])} · last contact {Date(item["lastSeen"])}"));
            row.Children.Add(left);
            if (!self)
            {
                var remove = ActionButton("Remove…", SymbolRegular.Delete24, "It can't connect any more until it connects again with the console secret", async () =>
                {
                    if (!await Dialogs.Confirm(this, "Remove console", $"{name} can no longer reach the organisation's devices through the broker. " +
                                                                       "It can connect again with the console secret – retire that secret if it shouldn't.", "Remove"))
                        return;
                    try { await client.RemoveConsoleAsync(id); }
                    catch (Exception ex) { await Dialogs.Info(this, "Remove console", $"{name} couldn't be removed: {ex.Message}"); }
                    await FillConsoles(panel, client);
                });
                remove.Margin = new Thickness(0);
                remove.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
            }
            panel.Children.Add(row);
        }
        if (panel.Children.Count == 0) panel.Children.Add(Caption("No consoles are registered."));

        static string Date(System.Text.Json.Nodes.JsonNode? seconds) =>
            seconds is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<long>(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime.ToString("g") : "–";
    }

    private async Task DisconnectBroker(BrokerClient client)
    {
        var org = client.Info.OrgName.Length > 0 ? client.Info.OrgName : client.Info.Org;
        if (!await Dialogs.Confirm(this, "Disconnect from the RemoteHub Broker",
                $"RemoteHub on this PC leaves {org}: this PC is removed from the organisation, its key is deleted and the synced folder disappears " +
                "from the navigation. The devices stay registered for the other consoles. To come back, connect again with the console secret.", "Disconnect"))
            return;
        if (_host?.DisconnectBroker is not { } disconnect) return;
        try { await disconnect(client); }
        catch (Exception ex) { await Dialogs.Info(this, "Disconnect from the RemoteHub Broker", $"{org} couldn't be disconnected completely: {ex.Message}"); }
        RefreshBrokerPage();
    }

    private async Task ConnectBroker()
    {
        var url = BrokerUrlBox.Text.Trim();
        var secret = BrokerSecretBox.Password.Trim();
        var name = BrokerNameBox.Text.Trim();
        if (url.Length == 0 || secret.Length == 0 || name.Length == 0)
        {
            BrokerStatus("Enter the broker's address, the console secret and a name for this PC – or import a .rhbroker file.");
            return;
        }
        if (_host?.ConnectBroker is not { } connect) return;
        BrokerConnectButton.IsEnabled = BrokerImportButton.IsEnabled = false;
        BrokerStatus("Connecting…");
        try
        {
            var client = await connect(url, secret, name);
            BrokerSecretBox.Password = "";
            BrokerUrlBox.Text = "";
            BrokerStatus($"Connected to {(client.Info.OrgName.Length > 0 ? client.Info.OrgName : client.Info.Org)}. Its devices show in the navigation, in the folder \"{BrokerSync.RootName(client.Info)}\".");
            RefreshBrokerPage();
        }
        catch (Exception ex) { BrokerStatus(ex is BrokerException ? ex.Message : "RemoteHub couldn't connect to the broker: " + ex.Message); }
        finally { BrokerConnectButton.IsEnabled = BrokerImportButton.IsEnabled = true; }
    }

    private void ImportBrokerFile()
    {
        var pick = new Microsoft.Win32.OpenFileDialog { Title = "Import a RemoteHub Broker file", Filter = $"RemoteHub Broker (*{BrokerFile.Extension})|*{BrokerFile.Extension}" };
        if (pick.ShowDialog(this) != true) return;
        try
        {
            var file = BrokerFile.Read(pick.FileName);
            BrokerUrlBox.Text = file.Url;
            BrokerSecretBox.Password = file.ConsoleSecret;
            BrokerStatus($"{Path.GetFileName(pick.FileName)}: {file.Name} ({file.Org}). Check this PC's name, then Connect.");
        }
        catch (Exception ex) { BrokerStatus(ex is BrokerException ? ex.Message : $"{Path.GetFileName(pick.FileName)} couldn't be read: {ex.Message}"); }
    }

    private void BrokerStatus(string text)
    {
        BrokerStatusText.Text = text;
        BrokerStatusText.Visibility = Visibility.Visible;
    }

    private TextBlock Caption(string text) =>
        new() { Text = text, Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };

    private static Button ActionButton(string text, SymbolRegular icon, string tip, Action click)
    {
        var b = new Button { Content = text, Icon = new SymbolIcon(icon), ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button ActionButton(string text, SymbolRegular icon, string tip, Func<Task> click)
    {
        var b = new Button { Content = text, Icon = new SymbolIcon(icon), ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await click(); }
            finally { b.IsEnabled = true; }
        };
        return b;
    }
}
