using RemoteHub.Agent;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>What RemoteHub can do that IT may block (docs/admx-design.md §5).</summary>
public enum Feature
{
    Rdp,
    Agent, AgentDesktop, AgentPowerShell, AgentFileTransfer, AgentRegistry, AgentServices, AgentProcesses, AgentEventLog, AgentScheduledTasks, AgentDeviceActions, AgentInstall,
    CredentialProviderInstall, AgentAutoSignIn,
    PowerShellRemoting, RemotingPowerShell, RemotingFileTransfer, RemotingRegistry, RemotingServices, RemotingProcesses, RemotingEventLog, RemotingScheduledTasks, RemotingDeviceActions,
    RemotingSetup,
    QuickConnect, ConnectionEditing, Ssh,
    JumpHosts, NetworkShares, Smb, Sftp, Ftp, WebDav,
    Broker,
    Recording, SavedPasswords, PasswordCopy,
    RdpFileImport, RdpFileExport, BackupExport, BackupImport, SharedSettings,
    Notes,
    Scripts, ScriptRun, ScriptRunSystem, ScriptRunUser, ScriptRunAsMe, ScriptDraftRun, ScriptLibrary, ScriptImport, ScriptExport,
}

/// <summary>
/// The tabs and the device actions (lock, restart …) that work through the agent or WinRM; which switch applies depends on
/// the connection's transport.
/// </summary>
public enum RemoteTool { PowerShell, FileTransfer, Registry, Services, Processes, EventLog, ScheduledTasks, DeviceActions }

/// <summary>
/// Whether a feature is allowed on this PC. A feature's own value at 0 blocks it, and so does its main switch
/// (AllowAgent, AllowPowerShellRemoting, AllowNetworkShares) at 0, whatever the feature's own value says. Not set or
/// invalid means allowed. Read when asked, so a gpupdate takes effect without a restart. Blocked actions are hidden
/// where they are offered and refused where they start (P2, P3); this PC's policy doesn't stop a device – the agent's
/// policy on the device does (P6).
/// </summary>
public static class Features
{
    /// <summary>Shown where a blocked feature is refused, and on greyed-out settings that belong to one.</summary>
    public const string TurnedOff = "Turned off by your organisation – see Settings → Policies.";

    private sealed record Switch(PolicyEntry Entry, string Name, Feature? Main = null, bool Inverted = false, bool Plural = false);

    private static readonly Dictionary<Feature, Switch> Switches = new()
    {
        [Feature.Rdp] = new(PolicyCatalog.AllowRdp, "Remote desktop over RDP"),
        [Feature.Agent] = new(PolicyCatalog.AllowAgent, "The RemoteHub Agent"),
        [Feature.AgentDesktop] = new(PolicyCatalog.AllowAgentDesktop, "Remote desktop through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentPowerShell] = new(PolicyCatalog.AllowAgentPowerShell, "PowerShell through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentFileTransfer] = new(PolicyCatalog.AllowAgentFileTransfer, "File transfer through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentRegistry] = new(PolicyCatalog.AllowAgentRegistry, "The registry through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentServices] = new(PolicyCatalog.AllowAgentServices, "Services through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentProcesses] = new(PolicyCatalog.AllowAgentProcesses, "Processes through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentEventLog] = new(PolicyCatalog.AllowAgentEventLog, "The event viewer through the RemoteHub Agent", Feature.Agent),
        [Feature.AgentScheduledTasks] = new(PolicyCatalog.AllowAgentScheduledTasks, "Scheduled tasks through the RemoteHub Agent", Feature.Agent, Plural: true),
        [Feature.AgentDeviceActions] = new(PolicyCatalog.AllowAgentDeviceActions, "Device actions through the RemoteHub Agent", Feature.Agent, Plural: true),
        [Feature.AgentInstall] = new(PolicyCatalog.AllowAgentInstall, "Installing and updating the RemoteHub Agent", Feature.Agent),
        [Feature.CredentialProviderInstall] = new(PolicyCatalog.AllowCredentialProviderInstall, "Installing and removing the credential provider", Feature.Agent),
        [Feature.AgentAutoSignIn] = new(PolicyCatalog.DisableAgentAutoSignIn, "Automatic sign-in through the RemoteHub Agent", Feature.Agent, Inverted: true),
        [Feature.PowerShellRemoting] = new(PolicyCatalog.AllowPowerShellRemoting, "PowerShell remoting"),
        [Feature.RemotingPowerShell] = new(PolicyCatalog.AllowRemotingPowerShell, "PowerShell over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingFileTransfer] = new(PolicyCatalog.AllowRemotingFileTransfer, "File transfer over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingRegistry] = new(PolicyCatalog.AllowRemotingRegistry, "The registry over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingServices] = new(PolicyCatalog.AllowRemotingServices, "Services over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingProcesses] = new(PolicyCatalog.AllowRemotingProcesses, "Processes over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingEventLog] = new(PolicyCatalog.AllowRemotingEventLog, "The event viewer over WinRM", Feature.PowerShellRemoting),
        [Feature.RemotingScheduledTasks] = new(PolicyCatalog.AllowRemotingScheduledTasks, "Scheduled tasks over WinRM", Feature.PowerShellRemoting, Plural: true),
        [Feature.RemotingDeviceActions] = new(PolicyCatalog.AllowRemotingDeviceActions, "Device actions over WinRM", Feature.PowerShellRemoting, Plural: true),
        [Feature.RemotingSetup] = new(PolicyCatalog.AllowRemotingSetup, "Setting up PowerShell remoting on a target", Feature.PowerShellRemoting),
        [Feature.QuickConnect] = new(PolicyCatalog.AllowQuickConnect, "Quick connect"),
        [Feature.ConnectionEditing] = new(PolicyCatalog.AllowConnectionEditing, "Changing folders, connections and shares"),
        [Feature.Ssh] = new(PolicyCatalog.AllowSsh, "SSH connections", Plural: true),
        // A jump host is an agent: no agent, no jump hosts.
        [Feature.JumpHosts] = new(PolicyCatalog.AllowJumpHosts, "Jump hosts", Feature.Agent, Plural: true),
        [Feature.Broker] = new(PolicyCatalog.AllowBroker, "The RemoteHub Broker", Feature.Agent),
        [Feature.NetworkShares] = new(PolicyCatalog.AllowNetworkShares, "Network shares", Plural: true),
        [Feature.Smb] = new(PolicyCatalog.AllowSmb, "SMB shares", Feature.NetworkShares, Plural: true),
        [Feature.Sftp] = new(PolicyCatalog.AllowSftp, "SFTP shares", Feature.NetworkShares, Plural: true),
        [Feature.Ftp] = new(PolicyCatalog.AllowFtp, "FTP shares", Feature.NetworkShares, Plural: true),
        [Feature.WebDav] = new(PolicyCatalog.AllowWebDav, "WebDAV shares", Feature.NetworkShares, Plural: true),
        [Feature.Recording] = new(PolicyCatalog.DisableRecording, "Recording", Inverted: true),
        [Feature.SavedPasswords] = new(PolicyCatalog.AllowSavedPasswords, "Saving passwords"),
        [Feature.PasswordCopy] = new(PolicyCatalog.AllowPasswordCopy, "Copying a stored password"),
        [Feature.RdpFileImport] = new(PolicyCatalog.AllowRdpFileImport, "Importing .rdp files"),
        [Feature.RdpFileExport] = new(PolicyCatalog.AllowRdpFileExport, "Saving .rdp files"),
        [Feature.BackupExport] = new(PolicyCatalog.AllowBackupExport, "Exporting a backup"),
        [Feature.BackupImport] = new(PolicyCatalog.AllowBackupImport, "Importing a backup"),
        [Feature.SharedSettings] = new(PolicyCatalog.AllowSharedSettings, "A shared settings folder"),
        [Feature.Notes] = new(PolicyCatalog.AllowNotes, "Notes", Plural: true),
        [Feature.Scripts] = new(PolicyCatalog.AllowScripts, "Scripts", Plural: true),
        [Feature.ScriptRun] = new(PolicyCatalog.AllowScriptRun, "Running scripts on devices", Feature.Scripts),
        [Feature.ScriptRunSystem] = new(PolicyCatalog.AllowScriptRunSystem, "Running scripts as System", Feature.ScriptRun),
        [Feature.ScriptRunUser] = new(PolicyCatalog.AllowScriptRunUser, "Running scripts as the signed-in user", Feature.ScriptRun),
        [Feature.ScriptRunAsMe] = new(PolicyCatalog.AllowScriptRunAsMe, "Running scripts as the connection's account", Feature.ScriptRun),
        [Feature.ScriptDraftRun] = new(PolicyCatalog.AllowScriptDraftRun, "Testing a draft on a device", Feature.ScriptRun),
        [Feature.ScriptLibrary] = new(PolicyCatalog.AllowScriptLibrary, "The built-in script library", Feature.Scripts),
        [Feature.ScriptImport] = new(PolicyCatalog.AllowScriptImport, "Importing scripts", Feature.Scripts),
        [Feature.ScriptExport] = new(PolicyCatalog.AllowScriptExport, "Exporting scripts", Feature.Scripts),
    };

    /// <summary>The title of a refusal dialog: the topic, as the neighbouring dialogs name it ("Jump host", "Network share").</summary>
    public static string Topic(Feature feature) => feature switch
    {
        Feature.Rdp => "Remote desktop",
        Feature.JumpHosts => "Jump host",
        Feature.NetworkShares or Feature.Smb or Feature.Sftp or Feature.Ftp or Feature.WebDav => "Network share",
        Feature.QuickConnect => "Quick connect",
        Feature.Ssh => "SSH",
        Feature.ConnectionEditing => "Connections",
        Feature.SavedPasswords or Feature.PasswordCopy => "Passwords",
        Feature.RdpFileImport or Feature.RdpFileExport or Feature.BackupExport or Feature.BackupImport => "Import and export",
        Feature.SharedSettings => "Settings location",
        Feature.Scripts or Feature.ScriptRun or Feature.ScriptRunSystem or Feature.ScriptRunUser or Feature.ScriptRunAsMe
            or Feature.ScriptDraftRun or Feature.ScriptLibrary or Feature.ScriptImport or Feature.ScriptExport => "Scripts",
        _ => Switches[feature].Entry.Group, // "RemoteHub Agent", "PowerShell remoting", "Import and export", "Recordings"
    };

    /// <summary>Every feature has a switch (a test checks it against the catalogue).</summary>
    internal static IEnumerable<(Feature Feature, PolicyEntry Entry, Feature? Main)> All => Switches.Select(s => (s.Key, s.Value.Entry, s.Value.Main));

    public static bool IsAllowed(Feature feature, IPolicySource? source = null) => Blocking(feature, source) is null;

    /// <summary>A remote tool, through the transport the connection uses: the agent's switch or WinRM's.</summary>
    public static bool IsAllowed(RemoteTool tool, bool viaAgent, IPolicySource? source = null) => IsAllowed(For(tool, viaAgent), source);

    /// <summary>A network share: AllowNetworkShares and its protocol's switch. SSH: AllowSsh alone.</summary>
    public static bool IsAllowed(ShareProtocol protocol, IPolicySource? source = null) => IsAllowed(For(protocol), source);

    public static Feature For(RemoteTool tool, bool viaAgent) => (tool, viaAgent) switch
    {
        (RemoteTool.PowerShell, true) => Feature.AgentPowerShell,
        (RemoteTool.FileTransfer, true) => Feature.AgentFileTransfer,
        (RemoteTool.Registry, true) => Feature.AgentRegistry,
        (RemoteTool.Services, true) => Feature.AgentServices,
        (RemoteTool.Processes, true) => Feature.AgentProcesses,
        (RemoteTool.EventLog, true) => Feature.AgentEventLog,
        (RemoteTool.ScheduledTasks, true) => Feature.AgentScheduledTasks,
        (RemoteTool.DeviceActions, true) => Feature.AgentDeviceActions,
        (RemoteTool.PowerShell, false) => Feature.RemotingPowerShell,
        (RemoteTool.FileTransfer, false) => Feature.RemotingFileTransfer,
        (RemoteTool.Registry, false) => Feature.RemotingRegistry,
        (RemoteTool.Services, false) => Feature.RemotingServices,
        (RemoteTool.EventLog, false) => Feature.RemotingEventLog,
        (RemoteTool.ScheduledTasks, false) => Feature.RemotingScheduledTasks,
        (RemoteTool.DeviceActions, false) => Feature.RemotingDeviceActions,
        _ => Feature.RemotingProcesses,
    };

    public static Feature For(ShareProtocol protocol) => protocol switch
    {
        ShareProtocol.Smb => Feature.Smb,
        ShareProtocol.Sftp => Feature.Sftp,
        ShareProtocol.Ftp => Feature.Ftp,
        ShareProtocol.Ssh => Feature.Ssh,
        _ => Feature.WebDav,
    };

    /// <summary>Any of the remote tools allowed over this transport – otherwise its test and Tools entries aren't offered.</summary>
    public static bool AnyTool(bool viaAgent, IPolicySource? source = null) =>
        Enum.GetValues<RemoteTool>().Any(t => IsAllowed(t, viaAgent, source));

    /// <summary>Why a feature can't be used ("PowerShell remoting is turned off by …"), or null when it can.</summary>
    public static string? WhyNot(Feature feature, IPolicySource? source = null) =>
        Blocking(feature, source) is { } off
            ? $"{Switches[off].Name} {(Switches[off].Plural ? "are" : "is")} turned off by your organisation – see Settings → Policies." : null;

    /// <summary>The main switch that blocks this feature's own value, for Settings → Policies (null: none, or it's allowed).</summary>
    public static PolicyEntry? OffBecause(PolicyEntry entry, IPolicySource? source = null)
    {
        var own = Switches.Values.FirstOrDefault(s => ReferenceEquals(s.Entry, entry));
        return own?.Main is { } main && Blocking(main, source) is { } off ? Switches[off].Entry : null;
    }

    /// <summary>The switch that blocks the feature – its main switch first, then its own value – or null.</summary>
    private static Feature? Blocking(Feature feature, IPolicySource? source)
    {
        var s = Switches[feature];
        if (s.Main is { } main && Blocking(main, source) is { } off) return off;
        var value = PolicyCatalog.Number(s.Entry, source ?? AppPolicy.Source);
        return value == (s.Inverted ? 1 : 0) ? feature : null;
    }
}
