namespace RemoteHub.Agent;

/// <summary>
/// The wire contract between RemoteHub and the agent (this file is compiled into both).
/// <para>
/// One HTTPS listener. <c>GET /v1/powershell</c> is a WebSocket upgrade with HTTP Basic credentials (a Windows account of
/// the device); after the upgrade every text message is one line of PowerShell's out-of-process remoting protocol, in
/// both directions. The agent only checks who is connecting and starts <c>powershell.exe -s</c> as that user – it never
/// looks at the commands.
/// </para>
/// </summary>
public static class AgentProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 58732;
    public const string HealthPath = "/v1/health";
    public const string PowerShellPath = "/v1/powershell";

    /// <summary>Largest single protocol line accepted (PowerShell fragments its messages far below this).</summary>
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>SHA-256 of a certificate as upper-case hex without separators – the form fingerprints are stored in.</summary>
    public static string Fingerprint(byte[] rawCertificate) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawCertificate));

    /// <summary>Accepts "AB:CD …", "ab cd …" or "ABCD…" and returns the stored form (or "" if it isn't a SHA-256 fingerprint).</summary>
    public static string NormalizeFingerprint(string? value)
    {
        var hex = new string((value ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 64 ? hex : "";
    }
}

/// <summary>
/// GET /v1/health – no credentials needed, nothing sensitive. Features: what this agent offers (older agents list none,
/// which means PowerShell only; since 0.18.12 PowerShell is missing when the policy PowerShellEnabled turns it off).
/// Managed: which of RemoteHub's device actions the device's policy decides (<see cref="AgentManaged"/>) – names only,
/// never values or other policies.
/// </summary>
public sealed record AgentHealth(string Product, string Version, int Protocol, string DeviceId, string[]? Features = null, string[]? Managed = null);

/// <summary>What IT sets on the device that RemoteHub's install and update options would otherwise change.</summary>
public static class AgentManaged
{
    /// <summary>Automatic sign-in (policy NoUserBehavior or Rights\AutoSignIn).</summary>
    public const string AutoSignIn = "autoSignIn";
    /// <summary>The virtual display (policy VirtualDisplay).</summary>
    public const string VirtualDisplay = "virtualDisplay";
    /// <summary>The port (policy ListenPort).</summary>
    public const string Port = "port";
}

public static class AgentFeatures
{
    public const string PowerShell = "powershell";
    public const string Desktop = "desktop";
    /// <summary>The device can be used as a jump host (<see cref="TunnelProtocol"/>).</summary>
    public const string Tunnel = "tunnel";
}
