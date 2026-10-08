namespace RemoteHub.Models;

public enum DisplayResize { DynamicResolution, SmartSizing, Fixed }
public enum AudioMode { PlayLocal = 0, PlayRemote = 1, None = 2 }
public enum ServerAuth { ConnectWithoutWarning = 0, DoNotConnect = 1, Warn = 2 }
public enum VisualQuality { Best, Balanced, Fast }
/// <summary>What to do when Windows asks for the password during connect (e.g. the server refuses saved credentials).</summary>
public enum PasswordPromptAction
{
    /// <summary>Removed in 1.9.1 (did not work); only kept so older settings files load. Migrated to <see cref="Clipboard"/>.</summary>
    AutoType,
    Clipboard,
    Off,
}

/// <summary>
/// Connection settings. Every property is nullable: <c>null</c> means "inherit from the parent folder"
/// (and ultimately from the global defaults).
/// </summary>
public sealed class RdpOptions
{
    /// <summary>Colour tag id (see Views.ColorTags) or "None". Colours the session frame, tab and tree entry.</summary>
    public string? ColorTag { get; set; }
    public DisplayResize? Resize { get; set; }
    public string? FixedResolution { get; set; }
    public int? ColorDepth { get; set; }
    public bool? UseMultimon { get; set; }
    public VisualQuality? Quality { get; set; }

    public bool? RedirectClipboard { get; set; }
    public bool? RedirectDrives { get; set; }
    public bool? RedirectPrinters { get; set; }
    public bool? RedirectSmartCards { get; set; }
    public AudioMode? Audio { get; set; }
    public bool? ForwardWindowsKeys { get; set; }

    public bool? EnableNla { get; set; }
    public ServerAuth? ServerAuthentication { get; set; }
    public bool? AdminSession { get; set; }
    public bool? AutoReconnect { get; set; }
    /// <summary>When Windows asks for the password: copy the stored password to the clipboard, or do nothing.</summary>
    public PasswordPromptAction? PasswordPrompt { get; set; }
    /// <summary>Obsolete (v1.5.x on/off switch); read once and migrated to <see cref="PasswordPrompt"/>.</summary>
    public bool? CopyPasswordOnPrompt { get; set; }

    /// <summary>Check connections for PowerShell remoting (WinRM) and offer a PowerShell tab and file transfer.</summary>
    public bool? PsRemoting { get; set; }
    /// <summary>PowerShell remoting over HTTPS (port 5986) instead of HTTP (5985).</summary>
    public bool? PsUseSsl { get; set; }

    public static RdpOptions BuiltInDefaults() => new()
    {
        ColorTag = "None",
        Resize = DisplayResize.DynamicResolution,
        FixedResolution = "1920x1080",
        ColorDepth = 32,
        UseMultimon = false,
        Quality = VisualQuality.Balanced,
        RedirectClipboard = true,
        RedirectDrives = false,
        RedirectPrinters = false,
        RedirectSmartCards = false,
        Audio = AudioMode.PlayLocal,
        ForwardWindowsKeys = true,
        EnableNla = true,
        ServerAuthentication = ServerAuth.Warn,
        AdminSession = false,
        AutoReconnect = true,
        PasswordPrompt = PasswordPromptAction.Clipboard,
        PsRemoting = false,
        PsUseSsl = false,
    };

    public RdpOptions Clone() => (RdpOptions)MemberwiseClone();

    /// <summary>Fills every unset value of this instance from <paramref name="fallback"/>.</summary>
    public RdpOptions FillFrom(RdpOptions fallback)
    {
        foreach (var p in typeof(RdpOptions).GetProperties())
            if (p.GetValue(this) is null)
                p.SetValue(this, p.GetValue(fallback));
        return this;
    }
}

public sealed class CredentialSet
{
    public string? Username { get; set; }
    public string? Domain { get; set; }
    public string? Password { get; set; }

    /// <summary>Domain value meaning "local account on the computer being connected to" (".\user").</summary>
    public const string LocalComputerDomain = ".";

    public bool IsEmpty => string.IsNullOrWhiteSpace(Username);
    public bool IsLocalAccount => Domain == LocalComputerDomain;
    public CredentialSet Clone() => (CredentialSet)MemberwiseClone();
}
