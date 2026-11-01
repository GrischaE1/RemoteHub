using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Services;

namespace RemoteHub.ViewModels;

public sealed record Choice(string Label, object? Value)
{
    public override string ToString() => Label;
}

/// <summary>Colour swatch in the editor. Value: null = inherit, "None", palette id, or "custom".</summary>
public sealed record ColourChoice(string Label, string? Value, System.Windows.Media.Brush? Brush)
{
    public const string Custom = "custom";
    public bool IsInherit => Value is null;
    public bool IsNone => Value == Views.ColorTags.None;
    public bool IsCustom => Value == Custom;
    /// <summary>Large swatch (palette colours only); the inherit choice shows a small dot next to its label.</summary>
    public System.Windows.Media.Brush? SwatchBrush => IsInherit ? null : Brush;
}

public sealed partial class OptionRow : ObservableObject
{
    public required string Label { get; init; }
    public string? Hint { get; init; }
    public required IReadOnlyList<Choice> Choices { get; init; }
    public required Action<RdpOptions, object?> Setter { get; init; }
    /// <summary>Set when a policy decides this row: it stays visible but greyed out, with this text (docs/admx-design.md P2).</summary>
    public string? PolicyNote { get; init; }
    public bool IsEditable => PolicyNote is null;
    /// <summary>The row shows the value IT fixes, not the stored one: saving leaves the stored value as it is (§4.3).</summary>
    public bool FixedByPolicy { get; init; }
    [ObservableProperty] private Choice _selected = null!;
}

/// <summary>Edits a connection, a folder, or (node == null) the global defaults.</summary>
public sealed partial class EditorViewModel : ObservableObject
{
    private readonly NodeBase? _node;
    private readonly AppData _data;

    public EditorViewModel(NodeBase? node, AppData data, bool isNew)
    {
        _node = node;
        _data = data;
        IsNew = isNew;

        _name = node?.Name ?? "";
        _notes = node?.Notes;
        if (node is Connection c) { _host = c.Host; _port = c.Port.ToString(); }

        var cred = node?.Credentials ?? data.DefaultCredentials;
        var savedId = node is null ? data.DefaultCredentialId : node.CredentialId;
        SavedOptions = data.SavedCredentials.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _selectedSaved = savedId is { } sid ? SavedOptions.FirstOrDefault(c => c.Id == sid) : null;
        _mode = node is { InheritCredentials: true } ? CredMode.Inherit
              : _selectedSaved is not null ? CredMode.Saved
              : CredMode.Custom;
        _username = cred.Username;
        _domain = cred.Domain;
        _password = cred.Password;

        var (inh, src) = Inheritance.ResolveCredentials(node, data, includeSelf: false);
        InheritedCredentialsText = inh.IsEmpty
            ? $"Inherited: {src}"
            : $"Inherited from {src}: {(string.IsNullOrEmpty(inh.Domain) ? "" : inh.Domain + "\\")}{inh.Username}";

        BuildRows();
    }

    public bool IsNew { get; }
    public bool IsDefaults => _node is null;
    /// <summary>The item edited; null for the defaults.</summary>
    public NodeBase? Node => _node;
    public bool IsConnection => _node is Connection;
    /// <summary>Below a jump host: how it is reached (shown on the General page), else null.</summary>
    public string? RouteText => _node?.JumpHost is { } j
        ? $"Reached through {j.Name} – its RemoteHub Agent is the jump host{(_node is Folder ? " for everything in this folder" : "")}. Move it out of {j.Name} in the list to connect directly."
        : null;
    public bool IsFolder => _node is Folder;
    public bool CanInherit => !IsDefaults;

    public string WindowTitle => IsDefaults ? "Settings"
        : IsConnection ? (IsNew ? "New connection" : "Edit connection")
        : (IsNew ? "New folder" : "Edit folder");

    public string Subtitle => IsDefaults
        ? "Used by every folder and connection unless they override a value."
        : IsFolder
            ? "Settings and credentials set here are inherited by all connections and sub-folders in this folder."
            : "Choose \"Inherit\" to take the value from the parent folder (or global defaults).";

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _host;
    [ObservableProperty] private string? _port = "3389";
    [ObservableProperty] private string? _notes;
    public enum CredMode { Inherit, Saved, Custom }

    [ObservableProperty] private CredMode _mode;
    [ObservableProperty] private SavedCredential? _selectedSaved;
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _domain;
    [ObservableProperty] private string? _error;

    /// <summary>
    /// The stored password. The editor never shows it: the box starts empty, a typed password replaces it and
    /// <see cref="RemovePassword"/> clears it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStoredPassword), nameof(PasswordPlaceholder))]
    private Secret? _password;

    public bool HasStoredPassword => Password is not null;
    public string PasswordPlaceholder => HasStoredPassword ? "A password is stored – type a new one to replace it" : "Leave empty to be asked when connecting";

    /// <summary>A password typed into the editor replaces the stored one; an empty box keeps it.</summary>
    public void ReplacePassword(string? typed)
    {
        if (Secret.From(typed) is { } s) Password = s;
    }

    public void RemovePassword() => Password = null;

    public string InheritedCredentialsText { get; }
    /// <summary>"Local account on the target computer": stored as domain "." (sent as .\\user).</summary>
    public bool LocalAccount
    {
        get => Domain == CredentialSet.LocalComputerDomain;
        set
        {
            if (value == LocalAccount) return;
            Domain = value ? CredentialSet.LocalComputerDomain : null;
        }
    }
    public bool DomainEditable => !LocalAccount;
    partial void OnDomainChanged(string? value)
    {
        OnPropertyChanged(nameof(LocalAccount));
        OnPropertyChanged(nameof(DomainEditable));
    }

    public List<SavedCredential> SavedOptions { get; }
    public bool HasSavedOptions => SavedOptions.Count > 0;

    public bool IsInherit { get => Mode == CredMode.Inherit; set { if (value) Mode = CredMode.Inherit; } }
    public bool IsSaved { get => Mode == CredMode.Saved; set { if (value) Mode = CredMode.Saved; } }
    public bool IsCustom { get => Mode == CredMode.Custom; set { if (value) Mode = CredMode.Custom; } }

    partial void OnModeChanged(CredMode value)
    {
        OnPropertyChanged(nameof(IsInherit));
        OnPropertyChanged(nameof(IsSaved));
        OnPropertyChanged(nameof(IsCustom));
        if (value == CredMode.Saved && SelectedSaved is null) SelectedSaved = SavedOptions.FirstOrDefault();
    }

    public List<OptionRow> DisplayRows { get; } = new();

    // ---------- appearance: icon + colour ----------

    public IReadOnlyList<Views.IconCatalog.Item> IconChoices => Views.IconCatalog.All;
    [ObservableProperty] private Views.IconCatalog.Item? _selectedIcon;
    [ObservableProperty] private string? _iconImage;
    public bool HasIconImage => !string.IsNullOrEmpty(IconImage);
    public bool ShowIconPicker => !IsDefaults;
    partial void OnIconImageChanged(string? value) => OnPropertyChanged(nameof(HasIconImage));

    public List<ColourChoice> ColourChoices { get; } = new();
    [ObservableProperty] private ColourChoice? _selectedColour;
    [ObservableProperty] private string? _customColour;
    public System.Windows.Media.Brush? CustomColourBrush => Views.ColorTags.TryParseHex(CustomColour, out var c) ? Views.ColorTags.Frozen(c) : null;

    partial void OnCustomColourChanged(string? value)
    {
        OnPropertyChanged(nameof(CustomColourBrush));
        if (CustomColourBrush is not null) SelectedColour = ColourChoices.First(c => c.IsCustom);
    }

    private void BuildAppearance(string? ownColour, string? inheritedColour)
    {
        if (CanInherit)
        {
            var inhLabel = Views.ColorTags.All.FirstOrDefault(t => t.Id == inheritedColour)?.Id
                           ?? (Views.ColorTags.TryParseHex(inheritedColour, out _) ? inheritedColour : "none");
            ColourChoices.Add(new ColourChoice($"Inherit · {inhLabel}", null, Views.ColorTags.Brush(inheritedColour)));
        }
        ColourChoices.Add(new ColourChoice("No colour", Views.ColorTags.None, null));
        ColourChoices.AddRange(Views.ColorTags.All.Select(t => new ColourChoice(t.Label, t.Id, Views.ColorTags.Frozen(t.Color))));
        ColourChoices.Add(new ColourChoice("Custom colour (#RRGGBB)", ColourChoice.Custom, null));

        if (Views.ColorTags.All.Any(t => t.Id == ownColour) || ownColour == Views.ColorTags.None || ownColour is null)
            SelectedColour = ColourChoices.FirstOrDefault(c => c.Value == ownColour) ?? ColourChoices[0];
        else if (Views.ColorTags.TryParseHex(ownColour, out _))
        {
            CustomColour = ownColour;
            SelectedColour = ColourChoices.First(c => c.IsCustom);
        }
        else SelectedColour = ColourChoices[0];

        SelectedIcon = Views.IconCatalog.All.FirstOrDefault(i => i.Symbol == _node?.Icon) ?? Views.IconCatalog.All[0];
        IconImage = _node?.IconImage;
    }

    /// <summary>The colour value to store, or an error message.</summary>
    private (string? Value, string? Error) ColourToStore() => SelectedColour switch
    {
        null => (null, null),
        { IsCustom: true } => CustomColourBrush is null ? (null, "Enter a custom colour as #RRGGBB, e.g. #1E90FF.") : (CustomColour!.Trim(), null),
        var c => (c.Value, null),
    };
    public List<OptionRow> ResourceRows { get; } = new();
    public List<OptionRow> SecurityRows { get; } = new();
    public List<OptionRow> PsRows { get; } = new();
    /// <summary>Refresh intervals of the processes and services tabs (global defaults only).</summary>
    public List<OptionRow> ToolRows { get; } = new();

    // ---- Apps (docs/app-tabs-design.md §8.1)

    /// <summary>The apps on the Apps page: this item's own list, or – while it inherits – its folder's, read-only.</summary>
    public ObservableCollection<ConnectionApp> Apps { get; } = new();
    /// <summary>What this item would inherit (its folders', else the defaults').</summary>
    public IReadOnlyList<ConnectionApp> InheritedApps { get; private set; } = [];
    /// <summary>Inherit the apps, or a list of its own; the defaults always have their own.</summary>
    public OptionRow AppsRow { get; private set; } = null!;
    /// <summary>The PowerShell remoting page's "Sign in automatically when nobody is signed in": app tabs need it.</summary>
    public OptionRow? AutoSignInRow { get; private set; }
    private bool _inheritedAutoSignIn;

    /// <summary>The list on the page is this item's own (else it shows the inherited one, which can't be changed here).</summary>
    public bool AppsOwn => !CanInherit || AppsRow.Selected.Value is not null;

    /// <summary>Automatic sign-in as the PowerShell remoting page has it now (also before saving).</summary>
    public bool AutoSignInOn => AutoSignInRow?.Selected.Value is bool own ? own : _inheritedAutoSignIn;

    /// <summary>"Turn on automatic sign-in": the row on the PowerShell remoting page set to On, saved with the rest.</summary>
    public void TurnOnAutoSignIn()
    {
        if (AutoSignInRow is { IsEditable: true } row && row.Choices.FirstOrDefault(c => Equals(c.Value, true)) is { } on) row.Selected = on;
    }

    private void BuildApps(RdpOptions own, RdpOptions parent)
    {
        InheritedApps = CanInherit ? parent.Apps ?? [] : [];
        _inheritedAutoSignIn = parent.AgentAutoSignIn == true;
        AutoSignInRow = PsRows.FirstOrDefault(r => r.Label == "Sign in automatically when nobody is signed in");
        var count = InheritedApps.Count switch { 0 => "none", 1 => "1 app", var n => $"{n} apps" };
        List<Choice> choices = CanInherit
            ? [new Choice($"Inherit · {count}", null), new Choice(IsConnection ? "This connection's own apps" : "This folder's own apps", true)]
            : [new Choice("These apps", true)];
        AppsRow = new OptionRow
        {
            Label = "Apps", Hint = CanInherit ? "Like the other agent options: the folder's apps, or a list of its own that replaces them." : "Every connection without apps of its own gets these.",
            Choices = choices, Selected = own.Apps is not null || !CanInherit ? choices[^1] : choices[0], Setter = (_, _) => { },
        };
        foreach (var a in own.Apps ?? InheritedApps) Apps.Add(a);
        // Switching to a list of its own starts from what it inherited; back to Inherit shows the folder's again.
        AppsRow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OptionRow.Selected) || AppsOwn) return;
            Apps.Clear();
            foreach (var a in InheritedApps) Apps.Add(a);
        };
    }

    /// <summary>Result of the last PowerShell remoting check, and the RemoteHub Agent of the device (connections only).</summary>
    public string? PsStatusText => _node is not Connection c ? null : string.Join("\n", new[]
    {
        c.PsRemoting is { } st
            ? $"Last check ({(st.ViaAgent ? "RemoteHub Agent" : "WinRM")}) {st.CheckedUtc.ToLocalTime():g}: {(st.State == PsRemotingState.Available ? "available" : "not available")} – {st.Message}"
            : "Not checked yet. It is checked after the first RDP login, or right-click → Test PowerShell remoting.",
        c.Agent is { Fingerprint.Length: 0 } p
            ? $"RemoteHub Agent: prepared, waiting for the device – port {p.Port}. RemoteHub pins the device's certificate when the agent first answers."
            : c.Agent is { } a
            ? $"RemoteHub Agent: {(a.Status == AgentStatus.Installed ? "installed" : "prepared, not confirmed yet")} – port {a.Port}, " +
              $"certificate SHA-256 {a.Fingerprint}{(a.Version is { } v ? $", version {v}" : "")}"
            : "RemoteHub Agent: not installed (right-click the connection → Install RemoteHub Agent).",
    });

    private static readonly string[] Resolutions =
        ["1280x720", "1366x768", "1440x900", "1600x900", "1680x1050", "1920x1080", "1920x1200", "2560x1440", "2560x1600", "3440x1440", "3840x2160"];

    private static readonly (object, string)[] YesNo = [(true, "On"), (false, "Off")];

    private void BuildRows()
    {
        var own = IsDefaults ? _data.Defaults : _node!.Options;
        var parent = IsDefaults ? RdpOptions.BuiltInDefaults() : Inheritance.ResolveOptions(_node, _data, includeSelf: false);

        // When IT fixes the property a row edits (subkey Connections), the row shows that value at every level, greyed out,
        // and the value stored here is kept but not used (docs/admx-design.md §7, §4.2). Which property: the one the row's
        // setter writes on an empty RdpOptions.
        OptionRow Row(string label, object? current, object? inherited, (object Value, string Label)[] values,
                      Action<RdpOptions, object?> set, string? hint = null, string? policyNote = null)
        {
            var probe = new RdpOptions();
            set(probe, values[0].Value);
            var policy = typeof(RdpOptions).GetProperties().FirstOrDefault(p => p.GetValue(probe) is not null)?.Name;
            var fixedValue = policy is null ? null : AppPolicy.Connection(policy);
            if (fixedValue is not null && !values.Any(v => Equals(v.Value, fixedValue))) values = [.. values, (fixedValue, fixedValue.ToString()!)];
            var choices = new List<Choice>();
            if (CanInherit)
            {
                var inhLabel = values.FirstOrDefault(v => Equals(v.Value, inherited)).Label ?? inherited?.ToString() ?? "–";
                choices.Add(new Choice($"Inherit · {inhLabel}", null));
            }
            choices.AddRange(values.Select(v => new Choice(v.Label, v.Value)));
            var sel = choices.FirstOrDefault(c => fixedValue is not null ? c.Value is not null && Equals(c.Value, fixedValue) : Equals(c.Value, current)) ?? choices[0];
            return new OptionRow
            {
                Label = label, Hint = hint, Choices = choices, Selected = sel, Setter = set,
                PolicyNote = policyNote ?? (fixedValue is null ? null : PolicyReport.ManagedNote), FixedByPolicy = fixedValue is not null,
            };
        }

        BuildAppearance(own.ColorTag, parent.ColorTag);
        DisplayRows.Add(Row("Resize behaviour", own.Resize, parent.Resize,
            [(DisplayResize.DynamicResolution, "Dynamic resolution"), (DisplayResize.SmartSizing, "Smart sizing (scale)"), (DisplayResize.Fixed, "Fixed resolution")],
            (o, v) => o.Resize = (DisplayResize?)v, "Dynamic resolution needs Windows 8.1 / Server 2012 R2 or later on the remote side."));
        DisplayRows.Add(Row("Fixed resolution", own.FixedResolution, parent.FixedResolution,
            Resolutions.Select(r => ((object)r, r.Replace("x", " × "))).ToArray(),
            (o, v) => o.FixedResolution = (string?)v, "Only used with \"Fixed resolution\"."));
        DisplayRows.Add(Row("Color depth", own.ColorDepth, parent.ColorDepth,
            [(32, "32-bit"), (24, "24-bit"), (16, "16-bit"), (15, "15-bit")], (o, v) => o.ColorDepth = (int?)v));
        DisplayRows.Add(Row("Visual quality", own.Quality, parent.Quality,
            [(VisualQuality.Best, "Best (all effects)"), (VisualQuality.Balanced, "Balanced"), (VisualQuality.Fast, "Fast (low bandwidth)")],
            (o, v) => o.Quality = (VisualQuality?)v));
        DisplayRows.Add(Row("Use all monitors in full screen", own.UseMultimon, parent.UseMultimon, YesNo, (o, v) => o.UseMultimon = (bool?)v));

        ResourceRows.Add(Row("Clipboard (copy & paste)", own.RedirectClipboard, parent.RedirectClipboard, YesNo,
            (o, v) => o.RedirectClipboard = (bool?)v, "Shares the clipboard with this PC – and therefore between all open sessions."));
        ResourceRows.Add(Row("Send Windows key combinations", own.ForwardWindowsKeys, parent.ForwardWindowsKeys, YesNo,
            (o, v) => o.ForwardWindowsKeys = (bool?)v, "Win, Alt+Tab, etc. go to the remote session while it has focus."));
        ResourceRows.Add(Row("Local drives", own.RedirectDrives, parent.RedirectDrives, YesNo, (o, v) => o.RedirectDrives = (bool?)v));
        ResourceRows.Add(Row("Printers", own.RedirectPrinters, parent.RedirectPrinters, YesNo, (o, v) => o.RedirectPrinters = (bool?)v));
        ResourceRows.Add(Row("Smart cards", own.RedirectSmartCards, parent.RedirectSmartCards, YesNo, (o, v) => o.RedirectSmartCards = (bool?)v));
        ResourceRows.Add(Row("Audio", own.Audio, parent.Audio,
            [(AudioMode.PlayLocal, "Play on this computer"), (AudioMode.PlayRemote, "Play on remote computer"), (AudioMode.None, "Do not play")],
            (o, v) => o.Audio = (AudioMode?)v));

        SecurityRows.Add(Row("Network Level Authentication", own.EnableNla, parent.EnableNla, YesNo, (o, v) => o.EnableNla = (bool?)v));
        SecurityRows.Add(Row("Server authentication", own.ServerAuthentication, parent.ServerAuthentication,
            [(ServerAuth.Warn, "Warn me"), (ServerAuth.DoNotConnect, "Do not connect"), (ServerAuth.ConnectWithoutWarning, "Connect without warning")],
            (o, v) => o.ServerAuthentication = (ServerAuth?)v, "What happens if the server identity cannot be verified."));
        SecurityRows.Add(Row("Administrative (console) session", own.AdminSession, parent.AdminSession, YesNo, (o, v) => o.AdminSession = (bool?)v));
        SecurityRows.Add(Row("Reconnect automatically", own.AutoReconnect, parent.AutoReconnect, YesNo, (o, v) => o.AutoReconnect = (bool?)v));
        SecurityRows.Add(Row("When Windows asks for the password", own.PasswordPrompt, parent.PasswordPrompt,
            [(PasswordPromptAction.Clipboard, "Copy to clipboard (cleared after 30 s)"),
             (PasswordPromptAction.Off, "Do nothing")],
            (o, v) => o.PasswordPrompt = (PasswordPromptAction?)v,
            "For servers that refuse saved credentials (\"Your credentials did not work\"): the stored password is copied so you can paste it with Ctrl+V.",
            Features.IsAllowed(Feature.PasswordCopy) ? null : Features.TurnedOff)); // policy AllowPasswordCopy = 0 (§5.6)

        // Settings of a feature this PC's policy blocks stay visible, greyed out (settings are never hidden, only actions are).
        var remotingOff = Features.IsAllowed(Feature.PowerShellRemoting) ? null : Features.TurnedOff;
        var agentOff = Features.IsAllowed(Feature.Agent) ? null : Features.TurnedOff;
        // In a synced broker folder there is no choice: the broker reaches nothing but the device's agent (Inheritance.UsesAgent).
        var connectThroughFixed = agentOff ?? (_node?.BrokerRoot is not null ? "Devices of the RemoteHub Broker are always reached through their RemoteHub Agent." : null);
        var agentDesktopOff = Features.IsAllowed(Feature.AgentDesktop) ? null : Features.TurnedOff;
        // Below a jump host an installed agent is used unless the connection itself says WinRM (Inheritance.UsesAgent).
        var jumpNote = _node?.JumpHost is { } jump
            ? $" Reached through {jump.Name}: with an agent installed, it is used unless this connection itself is set to PowerShell remoting. " +
              $"WinRM through {jump.Name} needs HTTPS (or 127.0.0.1 in this PC's TrustedHosts)."
            : "";
        PsRows.Add(Row("Check automatically after RDP login", own.PsRemoting, parent.PsRemoting, YesNo,
            (o, v) => o.PsRemoting = (bool?)v,
            "Tests WinRM after the first RDP login. You can always test by hand: right-click → Test PowerShell remoting. If it works, right-click offers a PowerShell tab and \"File transfer\" with the same credentials.", remotingOff));
        PsRows.Add(Row("Connect through", own.UseAgent, parent.UseAgent,
            [(false, "PowerShell remoting (WinRM)"), (true, "RemoteHub Agent")],
            (o, v) => o.UseAgent = (bool?)v,
            "RemoteHub Agent: for devices where WinRM isn't allowed. PowerShell, files, registry, services and processes then go through the agent " +
            "(port 58732, TLS with the device's own certificate). Install it with right-click → Install RemoteHub Agent." + jumpNote, connectThroughFixed));
        PsRows.Add(Row("Remote desktop through", own.RemoteDesktopVia, parent.RemoteDesktopVia,
            [(DesktopVia.Automatic, "Automatic – the agent if installed, otherwise RDP"), (DesktopVia.Rdp, "RDP"), (DesktopVia.Agent, "RemoteHub Agent")],
            (o, v) => o.RemoteDesktopVia = (DesktopVia?)v,
            "RemoteHub Agent: the device's screen without RDP – for desktops where RDP is turned off. The device decides who may see or control " +
            "the screen and whether the signed-in user is asked first. RDP stays available: right-click → Remote desktop via RDP.", agentDesktopOff));
        var autoSignInOff = AppPolicy.AgentAutoSignInDisabled || agentDesktopOff is not null ? Features.TurnedOff : null;
        PsRows.Add(Row("Sign in automatically when nobody is signed in", own.AgentAutoSignIn, parent.AgentAutoSignIn, YesNo,
            (o, v) => o.AgentAutoSignIn = (bool?)v,
            "Remote desktop through the RemoteHub Agent: when the device shows its sign-in screen, RemoteHub signs in with this connection's " +
            "credentials. Your own session on the device is unlocked the same way when it is locked, and locked again when you disconnect. " +
            "The device must allow it (\"NoUserBehavior\" and the rights \"AutoSignIn\", \"Desktop\" and \"SecureDesktop\" in the device's " +
            "policy) and have the credential provider installed (right-click → Install credential provider). Anyone at the device can see the " +
            "session while you work.", autoSignInOff));
        PsRows.Add(Row("When an automatic sign-in's connection ends", own.AgentAutoSignInEndAction, parent.AgentAutoSignInEndAction,
            [(Agent.AutoSignInEndActions.Lock, "Lock the session"), (Agent.AutoSignInEndActions.SignOut, "Sign out – unsaved work in the session is lost")],
            (o, v) => o.AgentAutoSignInEndAction = (string?)v,
            "For a session RemoteHub signed in automatically: when you disconnect, it is locked (you can come back to open work) or signed " +
            "out. A connection that drops is always signed out after 2 minutes if it isn't continued. The device's policy " +
            "(\"AutoSignInEndAction\") may decide instead.", autoSignInOff));
        PsRows.Add(Row("Reach", own.Reach, parent.Reach,
            [(BrokerReach.Automatic, "Automatic – directly, otherwise through the RemoteHub Broker"), (BrokerReach.Direct, "Directly only"), (BrokerReach.Broker, "Through the RemoteHub Broker only")],
            (o, v) => o.Reach = (BrokerReach?)v,
            "For devices in a RemoteHub Broker's synced folder – the agent and RDP alike. Automatic tries the device's own address for 1.5 seconds " +
            "(in the same network), then goes through the broker. Without a connected broker it is reached directly.",
            Features.IsAllowed(Feature.Broker) ? null : Features.TurnedOff));
        PsRows.Add(Row("Use HTTPS", own.PsUseSsl, parent.PsUseSsl,
            [(false, "Off (HTTP, port 5985 – encrypted by Kerberos/NTLM)"), (true, "On (HTTPS, port 5986)")],
            (o, v) => o.PsUseSsl = (bool?)v,
            "Use HTTPS for computers outside the domain or addressed by IP, unless they are in this PC's TrustedHosts list." + jumpNote, remotingOff));

        if (IsDefaults)
        {
            var tools = _data.Tools;
            ToolRows.Add(RefreshRow("Processes tab – refresh every", tools.ProcessRefreshSeconds, RemoteToolSettings.DefaultProcessRefresh,
                v => tools.ProcessRefreshSeconds = v, "Only while the tab is visible. CPU % is the average since the previous refresh.", AppPolicy.ProcessRefreshSeconds));
            ToolRows.Add(RefreshRow("Services tab – refresh every", tools.ServiceRefreshSeconds, RemoteToolSettings.DefaultServiceRefresh,
                v => tools.ServiceRefreshSeconds = v, "Only while the tab is visible. F5 or the refresh button always reads the list again.", AppPolicy.ServiceRefreshSeconds));
        }
        BuildApps(own, parent); // after PsRows: the Apps page follows their automatic sign-in row
    }

    private static readonly int[] RefreshChoices = [0, 5, 10, 15, 30, 60, 120, 300, 600];

    /// <param name="fixedByPolicy">The interval IT fixes (§6.7): shown, greyed out, and not stored.</param>
    private static OptionRow RefreshRow(string label, int current, int builtIn, Action<int> apply, string hint, int? fixedByPolicy)
    {
        var shown = fixedByPolicy ?? current;
        var values = RefreshChoices.Append(shown).Where(v => v >= 0).Distinct().Order();
        var choices = values.Select(v => new Choice(
            (v == 0 ? "Off (refresh by hand)" : Views.RemoteToolChrome.Every(v)) + (v == builtIn ? "  (default)" : ""), v)).ToList();
        return new OptionRow
        {
            Label = label, Hint = hint, Choices = choices,
            Selected = choices.First(c => Equals(c.Value, shown)),
            Setter = (_, v) => apply((int)v!),
            PolicyNote = fixedByPolicy is null ? null : PolicyReport.ManagedNote,
            FixedByPolicy = fixedByPolicy is not null,
        };
    }

    /// <summary>Validates and writes the values back. Returns false (and sets <see cref="Error"/>) on invalid input.</summary>
    public bool Apply()
    {
        Error = null;
        if (AppsOwn && Apps.Any(a => a.Name.Trim().Length == 0 || a.Program.Trim().Length == 0))
        {
            Error = "Give every app a name and a program (Apps page).";
            return false;
        }
        string? host = null;
        int port = 3389;
        if (IsConnection)
        {
            var raw = (Host ?? "").Trim();
            if (raw.Length == 0) { Error = "Enter a computer name or IP address."; return false; }
            (host, var parsedPort) = RdpFileParser.SplitAddress(raw);
            if (host.Any(char.IsWhiteSpace)) { Error = "The computer name must not contain spaces."; return false; }
            if (!int.TryParse(Port, out port) || port is < 1 or > 65535) { Error = "Port must be a number between 1 and 65535."; return false; }
            if (parsedPort != 3389) port = parsedPort;
        }
        if (!IsDefaults && string.IsNullOrWhiteSpace(Name))
        {
            if (IsConnection) Name = host!;
            else { Error = "Enter a folder name."; return false; }
        }
        if (!IsDefaults && Mode == CredMode.Custom && string.IsNullOrWhiteSpace(Username))
        {
            Error = "Enter a username, or switch credentials back to \"Inherit\".";
            return false;
        }
        if (Mode == CredMode.Saved && SelectedSaved is null)
        {
            Error = "Choose a saved credential (Menu → Saved credentials to create one).";
            return false;
        }

        var (colour, colourError) = ColourToStore();
        if (colourError is not null) { Error = colourError; return false; }

        var options = IsDefaults ? _data.Defaults : _node!.Options;
        foreach (var row in DisplayRows.Concat(ResourceRows).Concat(SecurityRows).Concat(PsRows).Concat(ToolRows).Where(r => !r.FixedByPolicy))
            row.Setter(options, row.Selected.Value);
        options.ColorTag = colour;
        options.Apps = AppsOwn ? [.. Apps.Select(a => a with { Name = a.Name.Trim(), Program = a.Program.Trim(), Arguments = a.Arguments.Trim(), WorkingFolder = a.WorkingFolder.Trim() })] : null;
        if (_node is not null)
        {
            _node.Icon = SelectedIcon?.Symbol;
            _node.IconImage = string.IsNullOrEmpty(IconImage) ? null : IconImage;
        }
        if (IsDefaults) options.FillFrom(RdpOptions.BuiltInDefaults());

        var cred = new CredentialSet
        {
            Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
            Domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim(),
            Password = Password,
        };
        // Allow "DOMAIN\user" in the username field.
        if (cred.Username is { } u && u.IndexOf('\\') is var i and > 0 && cred.Domain is null)
        {
            cred.Domain = u[..i];
            cred.Username = u[(i + 1)..];
        }

        Guid? savedId = Mode == CredMode.Saved ? SelectedSaved!.Id : null;
        if (Mode != CredMode.Custom) cred = new CredentialSet();

        if (IsDefaults)
        {
            _data.DefaultCredentials = cred;
            _data.DefaultCredentialId = savedId;
            return true;
        }

        _node!.Name = Name.Trim();
        _node.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes;
        _node.InheritCredentials = Mode == CredMode.Inherit;
        _node.CredentialId = savedId;
        _node.Credentials = cred;
        if (_node is Connection c)
        {
            c.Host = host!;
            c.Port = port;
        }
        return true;
    }
}
