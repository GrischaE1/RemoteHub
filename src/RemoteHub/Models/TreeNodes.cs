using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Folder), "folder")]
[JsonDerivedType(typeof(Connection), "connection")]
[JsonDerivedType(typeof(NetworkShare), "share")]
public abstract partial class NodeBase : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _notes;
    /// <summary>Built-in icon (SymbolRegular name, e.g. "Server24"); null = default icon.</summary>
    [ObservableProperty] private string? _icon;
    /// <summary>Custom icon image (PNG, base64, max 64×64); wins over <see cref="Icon"/>.</summary>
    [ObservableProperty] private string? _iconImage;

    /// <summary>When true, credentials come from the parent folder chain.</summary>
    [ObservableProperty] private bool _inheritCredentials = true;
    /// <summary>When not inheriting: a reference to a saved credential (wins over <see cref="Credentials"/>).</summary>
    public Guid? CredentialId { get; set; }
    public CredentialSet Credentials { get; set; } = new();
    public RdpOptions Options { get; set; } = new();

    [JsonIgnore] public Folder? Parent { get; set; }

    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
    /// <summary>Effective (inherited) colour tag brush for the tree; null = no tag. Computed by the view model.</summary>
    [ObservableProperty] [property: JsonIgnore] private System.Windows.Media.Brush? _tagBrush;
    /// <summary>Icon to show (computed): the item's icon or the default for its type.</summary>
    [JsonIgnore] public virtual string DisplaySymbol => Icon ?? (this is Folder ? "Folder24" : "Desktop24");
    /// <summary>For the tree: folders get a frame around themselves and their contents.</summary>
    [JsonIgnore] public bool IsFolder => this is Folder;
    partial void OnIconChanged(string? value) => OnPropertyChanged(nameof(DisplaySymbol));

    public IEnumerable<Folder> Ancestors()
    {
        for (var f = Parent; f is not null; f = f.Parent) yield return f;
    }
}

public sealed partial class Folder : NodeBase
{
    public ObservableCollection<NodeBase> Children { get; set; } = new();
    [ObservableProperty] private bool _isExpanded = true;

    public IEnumerable<NodeBase> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            if (c is Folder f)
                foreach (var d in f.Descendants()) yield return d;
        }
    }
}

public sealed partial class Connection : NodeBase
{
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 3389;

    /// <summary>Number of open sessions for this connection (UI indicator only).</summary>
    [ObservableProperty] [property: JsonIgnore] private int _openSessions;
    /// <summary>Best state of this connection's open sessions (same colours as the tabs); null if none is open.</summary>
    [ObservableProperty] [property: JsonIgnore] private Rdp.SessionState? _liveState;

    /// <summary>Result of the last PowerShell remoting check (persisted with the connection).</summary>
    [ObservableProperty] private PsRemotingStatus? _psRemoting;
    /// <summary>A PowerShell remoting check is running (UI indicator only).</summary>
    [ObservableProperty] [property: JsonIgnore] private bool _psChecking;

    /// <summary>The RemoteHub Agent enrolled for this connection's device (its pinned certificate), if any.</summary>
    [ObservableProperty] private AgentDevice? _agent;
    /// <summary>Agent icon in the navigation (computed from <see cref="Agent"/> and the inherited "Connect through" setting).</summary>
    [ObservableProperty] [property: JsonIgnore] private AgentMode _agentMode;

    /// <summary>When a tab of this connection last connected (any kind: RDP, agent desktop, PowerShell, tools).</summary>
    [ObservableProperty] private DateTime? _lastConnectedUtc;

    /// <summary>Network shares of this server (SMB, SFTP, FTP, WebDAV), shown below it in the navigation.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NetworkShare> Shares { get; set; } = new();
    /// <summary>The shares below it are shown in the navigation.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    [JsonIgnore] public string Address => Port == 3389 ? Host : $"{Host}:{Port}";

    /// <summary>Beside the name in the navigation: when it was last used ("today 15:42"), or "never connected".</summary>
    [JsonIgnore] public string LastUsedText => LastConnectedUtc is { } last ? When(last, DateTime.Now) : "never connected";
    [JsonIgnore] public bool NeverConnected => LastConnectedUtc is null;

    /// <summary>The agent shield's tooltip: how the agent is used, and its version.</summary>
    [JsonIgnore] public string? AgentToolTip
    {
        get
        {
            var version = Agent is { Version: { Length: > 0 } v } ? $"RemoteHub Agent {ShortVersion(v)}" : "RemoteHub Agent";
            return AgentMode switch
            {
                AgentMode.Active => $"{version}: active – PowerShell, files, registry, services and processes go through the agent",
                AgentMode.Available => $"{version}: installed but not used – the connection is set to PowerShell remoting (WinRM)",
                _ => "No RemoteHub Agent – remote desktop over RDP, tools over PowerShell remoting (WinRM). Right-click → Install RemoteHub Agent… to add it.",
            };
        }
    }

    /// <summary>For the tree after something changed inside <see cref="Agent"/>, and as time passes ("today" → "yesterday").</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(NeverConnected));
        OnPropertyChanged(nameof(AgentToolTip));
    }

    /// <summary>"just now", "today 15:42", "yesterday 09:10", "Mon 15:42" (this week), "12 Oct", "12 Oct 2025".</summary>
    public static string When(DateTime utc, DateTime now)
    {
        var local = utc.ToLocalTime();
        if (now - local < TimeSpan.FromMinutes(1) && now >= local) return "just now";
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var time = local.ToString("t", culture);
        if (local.Date == now.Date) return "today " + time;
        if (local.Date == now.Date.AddDays(-1)) return "yesterday " + time;
        if (local.Date > now.Date.AddDays(-7) && local.Date < now.Date) return local.ToString("ddd", culture) + " " + time;
        return local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", culture);
    }

    /// <summary>"0.13.0+4dd1aa1…" → "0.13.0".</summary>
    public static string ShortVersion(string version) => version.Split('+')[0];

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(Address));
        foreach (var s in Shares) s.RefreshLabels();
    }
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(Address));
    partial void OnLastConnectedUtcChanged(DateTime? value) => RefreshLabels();
    partial void OnAgentChanged(AgentDevice? value) => RefreshLabels();
    partial void OnAgentModeChanged(AgentMode value) => RefreshLabels();
}

public enum PsRemotingState { Unknown, Available, Unavailable }

public enum AgentStatus { NotInstalled, Installed }

/// <summary>Active: agent installed and the connection uses it. Available: installed, but the connection uses WinRM.</summary>
public enum AgentMode { None, Available, Active }

/// <summary>
/// A device's RemoteHub Agent identity, stored with the connection: where it listens and the SHA-256 fingerprint of
/// its certificate – the only certificate RemoteHub accepts for it. No secret is stored here.
/// </summary>
public sealed class AgentDevice
{
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public int Port { get; set; } = RemoteHub.Agent.AgentProtocol.DefaultPort;
    public string Fingerprint { get; set; } = "";
    public AgentStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? InstalledUtc { get; set; }
    /// <summary>Agent version seen at the last successful connection or install.</summary>
    public string? Version { get; set; }
    /// <summary>The virtual display option chosen at the last install or update (null: never chosen – on).</summary>
    public bool? VirtualDisplay { get; set; }

    public AgentDevice Clone() => (AgentDevice)MemberwiseClone();
}

/// <summary>Whether PowerShell remoting worked the last time it was tested for a connection.</summary>
public sealed class PsRemotingStatus
{
    public PsRemotingState State { get; set; }
    public DateTime CheckedUtc { get; set; }
    /// <summary>E.g. "PowerShell 5.1 on SRV01", or why it failed.</summary>
    public string? Message { get; set; }
    /// <summary>The result is for the RemoteHub Agent (false: WinRM). A result for the other way counts as "not tested".</summary>
    public bool ViaAgent { get; set; }
    /// <summary>Remote folder used for the last file transfer (suggested next time).</summary>
    public string? UploadFolder { get; set; }
    /// <summary>Registry key that was open last in the registry tab.</summary>
    public string? RegistryPath { get; set; }
}

/// <summary>A named credential that folders and connections can reference.</summary>
public sealed partial class SavedCredential : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _domain;
    public string? Password { get; set; }

    [JsonIgnore] public string Display => string.IsNullOrEmpty(Domain) ? (Username ?? "") : $"{Domain}\\{Username}";
    partial void OnUsernameChanged(string? value) => OnPropertyChanged(nameof(Display));
    partial void OnDomainChanged(string? value) => OnPropertyChanged(nameof(Display));

    public CredentialSet ToSet() => new() { Username = Username, Domain = Domain, Password = Password };
}

/// <summary>Root document persisted to disk.</summary>
public sealed class AppData
{
    public int Version { get; set; } = 1;
    public RdpOptions Defaults { get; set; } = RdpOptions.BuiltInDefaults();
    public CredentialSet DefaultCredentials { get; set; } = new();
    public Guid? DefaultCredentialId { get; set; }
    public ObservableCollection<SavedCredential> SavedCredentials { get; set; } = new();
    public ObservableCollection<NodeBase> Items { get; set; } = new();
    public UiState Ui { get; set; } = new();
    public RemoteToolSettings Tools { get; set; } = new();
}

/// <summary>Global settings of the remote tool tabs (shared with the other settings).</summary>
public sealed class RemoteToolSettings
{
    public const int DefaultProcessRefresh = 15, DefaultServiceRefresh = 60;
    /// <summary>Seconds between refreshes of the processes tab while it is visible; 0 = only by hand.</summary>
    public int ProcessRefreshSeconds { get; set; } = DefaultProcessRefresh;
    /// <summary>Seconds between refreshes of the services tab while it is visible; 0 = only by hand.</summary>
    public int ServiceRefreshSeconds { get; set; } = DefaultServiceRefresh;
}

public sealed class UiState
{
    /// <summary>Since when connections' last use is recorded (set once; the "not used for" filter needs it).</summary>
    public DateTime? UsageTrackedSinceUtc { get; set; }
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 860;
    public double SidebarWidth { get; set; } = 360;
    public bool SidebarCollapsed { get; set; }
    /// <summary>Width of the file transfers pane on the right.</summary>
    public double TransfersWidth { get; set; } = 360;
    public bool Maximized { get; set; }
    /// <summary>With startup protection on: lock RemoteHub after this many minutes without any input on this PC (0 = never).</summary>
    public int AutoLockMinutes { get; set; }
    public string Theme { get; set; } = "Dark";
    /// <summary>Id of the <see cref="Services.SwitchHotkeys"/> preset used to switch sessions.</summary>
    public string SwitchKeys { get; set; } = "Less";
    /// <summary>Folder shown on the "this PC" side of the file transfer tab last time.</summary>
    public string? LastLocalFolder { get; set; }
    /// <summary>Anonymous usage data and crash reports (see <see cref="Services.Telemetry"/>). IT policy can turn it off.</summary>
    public bool TelemetryEnabled { get; set; } = true;
    /// <summary>Look for a newer version when RemoteHub starts.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    /// <summary>"stable" or "beta"; null = the default for the running version (see <see cref="Services.UpdateService.DefaultChannel"/>).</summary>
    public string? UpdateChannel { get; set; }
    /// <summary>Folder for session recordings; null = Videos\RemoteHub (see <see cref="Services.Recordings.Folder"/>).</summary>
    public string? RecordingFolder { get; set; }
    /// <summary>Frames per second of recordings (5, 10, 15, 30, 60).</summary>
    public int RecordingFps { get; set; } = 10;
    /// <summary>"Tab", "FullHd" or "Uhd4K" (see <see cref="Services.RecordingResolution"/>).</summary>
    public string RecordingResolution { get; set; } = "Tab";
    /// <summary>Capture recordings through the graphics card (fast; off unless chosen).</summary>
    public bool RecordingUseGpu { get; set; }
    /// <summary>Recordings include the bar above the tab (connection name, address, how it connects); off = only the content.</summary>
    public bool RecordingShowDetails { get; set; }
    /// <summary>Remote desktop through the agent: picture preset (see <see cref="Remoting.DesktopQualityPreset"/>) and the last consent mode.</summary>
    public string AgentDesktopQuality { get; set; } = "Admin";
    public string? AgentDesktopConsent { get; set; }
    /// <summary>The device's resolution follows the tab (restored when the session ends).</summary>
    public bool AgentDesktopFitToWindow { get; set; } = true;
    /// <summary>The connection info over the agent desktop's picture.</summary>
    public bool AgentDesktopShowInfo { get; set; }
    /// <summary>Alt+Tab, the Windows key and its combinations go to the device while its tab has the focus.</summary>
    public bool AgentDesktopKeyCombinations { get; set; } = true;
    /// <summary>The note shown before the first recording was confirmed.</summary>
    public bool RecordingNoticeAccepted { get; set; }
    /// <summary>Version the user chose to skip; it isn't offered at start-up again (a newer one is).</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>Obsolete (v1.5.0 menu toggle). Read once and migrated to <see cref="RdpOptions.CopyPasswordOnPrompt"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool? CopyPasswordOnPrompt { get; set; }
}

