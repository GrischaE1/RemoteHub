using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteHub.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Folder), "folder")]
[JsonDerivedType(typeof(Connection), "connection")]
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
    [JsonIgnore] public string DisplaySymbol => Icon ?? (this is Folder ? "Folder24" : "Desktop24");
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

    [JsonIgnore] public string Address => Port == 3389 ? Host : $"{Host}:{Port}";

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(Address));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(Address));
}

public enum PsRemotingState { Unknown, Available, Unavailable }

/// <summary>Whether PowerShell remoting worked the last time it was tested for a connection.</summary>
public sealed class PsRemotingStatus
{
    public PsRemotingState State { get; set; }
    public DateTime CheckedUtc { get; set; }
    /// <summary>E.g. "PowerShell 5.1 on SRV01", or why it failed.</summary>
    public string? Message { get; set; }
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
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 860;
    public double SidebarWidth { get; set; } = 360;
    public bool SidebarCollapsed { get; set; }
    /// <summary>Width of the file transfers pane on the right.</summary>
    public double TransfersWidth { get; set; } = 360;
    public bool Maximized { get; set; }
    public string Theme { get; set; } = "Dark";
    /// <summary>Id of the <see cref="Services.SwitchHotkeys"/> preset used to switch sessions.</summary>
    public string SwitchKeys { get; set; } = "Less";
    /// <summary>Folder shown on the "this PC" side of the file transfer tab last time.</summary>
    public string? LastLocalFolder { get; set; }
    /// <summary>Anonymous start counter (see <see cref="Services.Telemetry"/>). IT policy can turn it off.</summary>
    public bool TelemetryEnabled { get; set; } = true;
    /// <summary>Look for a newer version when RemoteHub starts.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;
    /// <summary>"stable" or "beta"; null = the default for the running version (see <see cref="Services.UpdateService.DefaultChannel"/>).</summary>
    public string? UpdateChannel { get; set; }
    /// <summary>Version the user chose to skip; it isn't offered at start-up again (a newer one is).</summary>
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>Obsolete (v1.5.0 menu toggle). Read once and migrated to <see cref="RdpOptions.CopyPasswordOnPrompt"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool? CopyPasswordOnPrompt { get; set; }
}

