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
        Password = cred.Password;

        var (inh, src) = Inheritance.ResolveCredentials(node, data, includeSelf: false);
        InheritedCredentialsText = inh.IsEmpty
            ? $"Inherited: {src}"
            : $"Inherited from {src}: {(string.IsNullOrEmpty(inh.Domain) ? "" : inh.Domain + "\\")}{inh.Username}";

        BuildRows();
    }

    public bool IsNew { get; }
    public bool IsDefaults => _node is null;
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

    public string? Password { get; set; }
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

    /// <summary>Result of the last PowerShell remoting check, and the RemoteHub Agent of the device (connections only).</summary>
    public string? PsStatusText => _node is not Connection c ? null : string.Join("\n", new[]
    {
        c.PsRemoting is { } st
            ? $"Last check ({(st.ViaAgent ? "RemoteHub Agent" : "WinRM")}) {st.CheckedUtc.ToLocalTime():g}: {(st.State == PsRemotingState.Available ? "available" : "not available")} – {st.Message}"
            : "Not checked yet. It is checked after the first RDP login, or right-click → Test PowerShell remoting.",
        c.Agent is { } a
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

        OptionRow Row(string label, object? current, object? inherited, (object Value, string Label)[] values,
                      Action<RdpOptions, object?> set, string? hint = null)
        {
            var choices = new List<Choice>();
            if (CanInherit)
            {
                var inhLabel = values.FirstOrDefault(v => Equals(v.Value, inherited)).Label ?? inherited?.ToString() ?? "–";
                choices.Add(new Choice($"Inherit · {inhLabel}", null));
            }
            choices.AddRange(values.Select(v => new Choice(v.Label, v.Value)));
            var sel = choices.FirstOrDefault(c => Equals(c.Value, current)) ?? choices[0];
            return new OptionRow { Label = label, Hint = hint, Choices = choices, Selected = sel, Setter = set };
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
            "For servers that refuse saved credentials (\"Your credentials did not work\"): the stored password is copied so you can paste it with Ctrl+V."));

        // Below a jump host an installed agent is used unless the connection itself says WinRM (Inheritance.UsesAgent).
        var jumpNote = _node?.JumpHost is { } jump
            ? $" Reached through {jump.Name}: with an agent installed, it is used unless this connection itself is set to PowerShell remoting. " +
              $"WinRM through {jump.Name} needs HTTPS (or 127.0.0.1 in this PC's TrustedHosts)."
            : "";
        PsRows.Add(Row("Check automatically after RDP login", own.PsRemoting, parent.PsRemoting, YesNo,
            (o, v) => o.PsRemoting = (bool?)v,
            "Tests WinRM after the first RDP login. You can always test by hand: right-click → Test PowerShell remoting. If it works, right-click offers a PowerShell tab and \"File transfer\" with the same credentials."));
        PsRows.Add(Row("Connect through", own.UseAgent, parent.UseAgent,
            [(false, "PowerShell remoting (WinRM)"), (true, "RemoteHub Agent")],
            (o, v) => o.UseAgent = (bool?)v,
            "RemoteHub Agent: for devices where WinRM isn't allowed. PowerShell, files, registry, services and processes then go through the agent " +
            "(port 58732, TLS with the device's own certificate). Install it with right-click → Install RemoteHub Agent." + jumpNote));
        PsRows.Add(Row("Remote desktop through", own.RemoteDesktopVia, parent.RemoteDesktopVia,
            [(DesktopVia.Automatic, "Automatic – the agent if installed, otherwise RDP"), (DesktopVia.Rdp, "RDP"), (DesktopVia.Agent, "RemoteHub Agent")],
            (o, v) => o.RemoteDesktopVia = (DesktopVia?)v,
            "RemoteHub Agent: the device's screen without RDP – for desktops where RDP is turned off. The device decides who may see or control " +
            "the screen and whether the signed-in user is asked first. RDP stays available: right-click → Remote desktop via RDP."));
        PsRows.Add(Row("Use HTTPS", own.PsUseSsl, parent.PsUseSsl,
            [(false, "Off (HTTP, port 5985 – encrypted by Kerberos/NTLM)"), (true, "On (HTTPS, port 5986)")],
            (o, v) => o.PsUseSsl = (bool?)v,
            "Use HTTPS for computers outside the domain or addressed by IP, unless they are in this PC's TrustedHosts list." + jumpNote));

        if (IsDefaults)
        {
            var tools = _data.Tools;
            ToolRows.Add(RefreshRow("Processes tab – refresh every", tools.ProcessRefreshSeconds, RemoteToolSettings.DefaultProcessRefresh,
                v => tools.ProcessRefreshSeconds = v, "Only while the tab is visible. CPU % is the average since the previous refresh."));
            ToolRows.Add(RefreshRow("Services tab – refresh every", tools.ServiceRefreshSeconds, RemoteToolSettings.DefaultServiceRefresh,
                v => tools.ServiceRefreshSeconds = v, "Only while the tab is visible. F5 or the refresh button always reads the list again."));
        }
    }

    private static readonly int[] RefreshChoices = [0, 5, 10, 15, 30, 60, 120, 300, 600];

    private static OptionRow RefreshRow(string label, int current, int builtIn, Action<int> apply, string hint)
    {
        var values = RefreshChoices.Append(current).Where(v => v >= 0).Distinct().Order();
        var choices = values.Select(v => new Choice(
            (v == 0 ? "Off (refresh by hand)" : Views.RemoteToolChrome.Every(v)) + (v == builtIn ? "  (default)" : ""), v)).ToList();
        return new OptionRow
        {
            Label = label, Hint = hint, Choices = choices,
            Selected = choices.First(c => Equals(c.Value, current)),
            Setter = (_, v) => apply((int)v!),
        };
    }

    /// <summary>Validates and writes the values back. Returns false (and sets <see cref="Error"/>) on invalid input.</summary>
    public bool Apply()
    {
        Error = null;
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
        foreach (var row in DisplayRows.Concat(ResourceRows).Concat(SecurityRows).Concat(PsRows).Concat(ToolRows)) row.Setter(options, row.Selected.Value);
        options.ColorTag = colour;
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
            Password = string.IsNullOrEmpty(Password) ? null : Password,
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
