using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>Actions the settings overview can start (they open the existing editors).</summary>
public sealed record SettingsActions(Action EditDefaults, Action ChangeLocation, Action ManageCredentials, Action OpenFolder, Action OpenLog, Action StartupProtection, Action Policies);

/// <summary>
/// Read-only overview of all settings in one place – where they are stored, global defaults, remote tools, credentials,
/// appearance and the library – with a button to change each part. Never shows passwords or user names.
/// </summary>
public sealed class SettingsOverviewWindow : FluentWindow
{
    private readonly Func<AppData> _data;
    private readonly Func<IEnumerable<NodeBase>> _allNodes;
    private readonly SettingsActions _actions;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 0, 24, 16) };

    public SettingsOverviewWindow(Func<AppData> data, Func<IEnumerable<NodeBase>> allNodes, SettingsActions actions)
    {
        _data = data;
        _allNodes = allNodes;
        _actions = actions;
        Title = "Settings overview";
        Width = 760; Height = 760; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var root = new DockPanel();
        var bar = new TitleBar { Title = "Settings overview", ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Copy as text", Icon = new SymbolIcon(SymbolRegular.Copy24), Margin = new Thickness(0, 0, 8, 0),
                                ToolTip = "For support: the overview as plain text – without passwords, user names or host names." };
        copy.Click += (_, _) => { try { Clipboard.SetText(AsText()); } catch { /* clipboard busy */ } };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 100 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Build();
    }

    // ------------------------------------------------------------------ content

    private sealed record Section(string Title, SymbolRegular Icon, List<(string Label, string Value)> Rows, (string Text, Action Run)[] Buttons);

    private List<Section> Sections()
    {
        var d = _data();
        var o = d.Defaults.Clone().OverrideWith(AppPolicy.Connections()); // what connections get: IT's connection settings win
        var nodes = _allNodes().ToList();
        var connections = nodes.OfType<Connection>().ToList();
        string OnOff(bool? b) => b == true ? "On" : "Off";

        var storage = new List<(string, string)>
        {
            ("Stored", SettingsStore.IsShared ? (SettingsStore.IsPolicyManaged ? "Shared folder (set by your IT policy)" : "Shared folder (e.g. OneDrive)") : "This PC only (encrypted database)"),
            ("Settings", SettingsStore.FilePath),
            ("Database", $"{Data.LocalDb.FilePath} – encrypted (ChaCha20-Poly1305), key protected for your Windows account"),
            ("Database backups", Data.LocalDb.Backups() is [var newest, ..] ? $"{Data.LocalDb.Backups().Count}, newest {newest.TimeUtc.ToLocalTime():g}" : "None yet (made daily)"),
            ("Audit log", $"Kept {Data.AuditLog.RetentionDays} days{(AppPolicy.AuditRetentionDays is null ? "" : " (set by your IT policy)")}"),
            ("Local data folder", SettingsStore.Directory),
            ("Startup protection", AppLock.Describe()),
        };
        if (SettingsStore.UsingLocalFallback) storage.Add(("Note", "The shared file wasn't available at start-up – the settings of this PC are used."));
        if (SettingsStore.MigratedFrom is { } from) storage.Add(("Moved", $"Settings of RDP Manager were copied from {from} (the old folder is kept as a backup)."));

        var (_, credSource) = Inheritance.ResolveCredentials((NodeBase?)null, d);
        var defaults = new List<(string, string)>
        {
            ("Credentials", credSource == "global defaults" ? (d.DefaultCredentials.Password is { Length: > 0 } ? "User name and password set" : "User name set, password asked when connecting") : credSource),
            ("Display", $"{o.Resize switch { DisplayResize.DynamicResolution => "Dynamic resolution", DisplayResize.SmartSizing => "Smart sizing", _ => "Fixed " + o.FixedResolution }}, {o.ColorDepth}-bit, quality {o.Quality}, all monitors {OnOff(o.UseMultimon)}"),
            ("Local resources", $"Clipboard {OnOff(o.RedirectClipboard)}, drives {OnOff(o.RedirectDrives)}, printers {OnOff(o.RedirectPrinters)}, smart cards {OnOff(o.RedirectSmartCards)}, Windows keys {OnOff(o.ForwardWindowsKeys)}"),
            ("Audio", o.Audio switch { AudioMode.PlayLocal => "Play on this computer", AudioMode.PlayRemote => "Play on remote computer", _ => "Do not play" }),
            ("Security", $"NLA {OnOff(o.EnableNla)}, server authentication: {o.ServerAuthentication switch { ServerAuth.Warn => "warn", ServerAuth.DoNotConnect => "do not connect", _ => "connect without warning" }}, admin session {OnOff(o.AdminSession)}, reconnect automatically {OnOff(o.AutoReconnect)}"),
            ("Password prompt", o.PasswordPrompt == PasswordPromptAction.Off ? "Do nothing" : "Copy password to clipboard (cleared after 30 s)"),
            ("PowerShell remoting", $"Check after RDP login {OnOff(o.PsRemoting)}, HTTPS {OnOff(o.PsUseSsl)}"),
            ("Colour", o.ColorTag is null or "None" ? "None" : o.ColorTag),
        };

        var tools = new List<(string, string)>
        {
            ("Processes tab", Interval(AppPolicy.ProcessRefreshSeconds ?? d.Tools.ProcessRefreshSeconds)),
            ("Services tab", Interval(AppPolicy.ServiceRefreshSeconds ?? d.Tools.ServiceRefreshSeconds)),
            ("Scheduled tasks tab", Interval(AppPolicy.TaskRefreshSeconds ?? d.Tools.TaskRefreshSeconds)),
            ("Recordings", AppPolicy.RecordingDisabled ? "Turned off by your IT policy"
                : Recordings.Describe(Recordings.Options(d.Ui)) + (Recordings.IncludeDetails(d.Ui) ? " · with connection details" : "") + " · " +
                  Recordings.Folder(d.Ui) + (AppPolicy.RecordingFolder is not null ? " (set by your IT policy)" : "")),
        };

        var creds = new List<(string, string)>
        {
            ("Saved credentials", d.SavedCredentials.Count == 0 ? "None" : $"{d.SavedCredentials.Count} ({d.SavedCredentials.Count(c => c.Password is null)} without stored password)"),
            ("Folders / connections with own credentials", nodes.Count(n => !n.InheritCredentials).ToString()),
            ("Connections without any credentials", connections.Count(c => Inheritance.ResolveCredentials(c, d).Credentials.IsEmpty) is var none and > 0 ? $"{none} – Windows asks when connecting; PowerShell remoting uses your Windows account" : "0"),
        };

        var appearance = new List<(string, string)>
        {
            ("Theme", AppPolicy.Theme ?? d.Ui.Theme),
            ("Session switch keys", SwitchHotkeys.Get(AppPolicy.SwitchKeys ?? d.Ui.SwitchKeys).Hint),
            ("Navigation", d.Ui.SidebarCollapsed ? "Collapsed" : $"Expanded, {d.Ui.SidebarWidth:0} px"),
        };

        var library = new List<(string, string)>
        {
            ("Folders", nodes.OfType<Folder>().Count().ToString()),
            ("Connections", $"{connections.Count} ({connections.Count(c => c.Parent is null)} at the top level, outside folders)"),
            ("PowerShell remoting", $"{connections.Count(c => c.PsRemoting?.State == PsRemotingState.Available)} available, {connections.Count(c => c.PsRemoting?.State == PsRemotingState.Unavailable)} not available, {connections.Count(c => c.PsRemoting is null)} not checked"),
            ("Diagnostic log", DiagLog.FilePath),
        };

        return
        [
            new("Storage", SymbolRegular.Storage24, storage, [("Startup protection…", _actions.StartupProtection), ("Change location…", _actions.ChangeLocation), ("Open folder", _actions.OpenFolder)]),
            new("Global defaults", SymbolRegular.Settings24, defaults, [("Edit global defaults…", _actions.EditDefaults)]),
            new("Remote tools", SymbolRegular.Pulse24, tools, [("Change…", _actions.EditDefaults)]),
            new("Credentials", SymbolRegular.Key24, creds, [("Saved credentials…", _actions.ManageCredentials)]),
            new("Appearance & keys", SymbolRegular.WeatherMoon24, appearance, []),
            new("Library", SymbolRegular.Folder24, library, [("Open diagnostic log", _actions.OpenLog)]),
            // Every value in detail is on Settings → Policies; here only how many.
            new("Policies", SymbolRegular.LockClosed24, [("Set by your organisation", PolicyReport.Build(false).Summary)], [("Show policies…", _actions.Policies)]),
        ];
    }

    private static string Interval(int s) => s <= 0 ? "Off (refresh by hand)" : "every " + RemoteToolChrome.Every(s);

    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Muted("Everything RemoteHub is set to, in one place. Passwords are never shown.", 0, 0, 0, 4));
        foreach (var s in Sections())
        {
            var head = new DockPanel { Margin = new Thickness(0, 18, 0, 6) };
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new SymbolIcon(s.Icon) { FontSize = 16, Margin = new Thickness(0, 0, 8, 0) });
            title.Children.Add(new TextBlock { Text = s.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (text, run) in s.Buttons)
            {
                var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
                b.Click += (_, _) => { run(); Build(); };
                actions.Children.Add(b);
            }
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
            head.Children.Add(title);
            _body.Children.Add(head);

            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < s.Rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Muted(s.Rows[i].Label, 0, 3, 12, 3);
                var value = new TextBlock { Text = s.Rows[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            card.Child = grid;
            _body.Children.Add(card);
        }
    }

    private static TextBlock Muted(string text, double l, double t, double r, double b)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(l, t, r, b) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    /// <summary>Plain text for support. Paths are kept (they show where settings are) but the user profile is shortened.</summary>
    private string AsText()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sb = new System.Text.StringBuilder($"{AboutWindow.ProductLine}{Environment.NewLine}");
        foreach (var s in Sections())
        {
            sb.AppendLine().AppendLine($"[{s.Title}]");
            foreach (var (label, value) in s.Rows) sb.AppendLine($"{label}: {value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)}");
        }
        return sb.ToString();
    }
}
