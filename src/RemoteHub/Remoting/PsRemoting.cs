using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>
/// Where and as whom to open a PowerShell session: over WinRM, or through the RemoteHub Agent when <see cref="Agent"/> is
/// set. Holds the password in memory only. <paramref name="Via"/>: WinRM goes through this jump host (the agent's own way
/// through is <see cref="AgentEndpoint.Via"/>). <paramref name="TrustedCertificate"/>: WinRM over HTTPS through a jump
/// host – the certificate the user trusted for this connection (<see cref="Connection.WinRmCertificate"/>);
/// <paramref name="ConnectionId"/> names that connection for <see cref="PsRemoting.ConfirmCertificate"/>.
/// </summary>
public sealed record PsTarget(string Host, bool UseSsl, CredentialSet Credentials, AgentEndpoint? Agent = null, JumpRoute? Via = null,
    string? TrustedCertificate = null, Guid ConnectionId = default)
{
    public int Port => Agent?.Port ?? (UseSsl ? 5986 : 5985);
    public string Display => (Agent is null ? $"{Host} ({(UseSsl ? "HTTPS" : "HTTP")} {Port})" : $"{Host} (RemoteHub Agent {Port})") +
                             ((Agent?.Via ?? Via) is { } j ? $" via {j.Name}" : "");
    /// <summary>The way the session goes, for the tab header.</summary>
    public string Transport => Agent is null ? $"PowerShell remoting ({(UseSsl ? "HTTPS" : "HTTP")})" : "RemoteHub Agent";
}

/// <summary>
/// The certificate a WinRM target presented through a jump host when it isn't one this PC trusts for the name.
/// <c>Changed</c>: another one was trusted for the connection before.
/// </summary>
public sealed record PresentedCertificate(string Host, string IssuedTo, string IssuedBy, DateTime ValidUntil, string Thumbprint, string Pin, bool Changed);

public static class PsRemoting
{
    private const string ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";

    /// <summary>
    /// Asked on the opening thread when <see cref="CheckCertificate"/> finds a certificate it can't accept by itself: true
    /// when the user trusts it (the handler remembers <see cref="PresentedCertificate.Pin"/> for the connection). Null –
    /// no window to ask in – refuses.
    /// </summary>
    public static Func<PsTarget, PresentedCertificate, bool>? ConfirmCertificate { get; set; }

    /// <summary>
    /// User name as WinRM expects it. A local account (".\user") becomes "COMPUTER\user", using the first label of
    /// the host name; for an IP address it stays ".\user".
    /// </summary>
    public static string? UserName(CredentialSet c, string host)
    {
        if (c.IsEmpty) return null;
        var user = c.Username!.Trim();
        if (user.Contains('\\') || user.Contains('@')) return user;
        if (c.IsLocalAccount)
        {
            var computer = IPAddress.TryParse(host.Trim('[', ']'), out _) ? "." : host.Split('.')[0];
            return $"{computer}\\{user}";
        }
        return string.IsNullOrEmpty(c.Domain) ? user : $"{c.Domain}\\{user}";
    }

    /// <summary>True when a username is set but no password: the caller has to ask for it.</summary>
    public static bool NeedsPassword(CredentialSet c) => !c.IsEmpty && c.Password is null;

    /// <summary>Where a runspace connects to; <paramref name="forward"/>: WinRM through a jump host's local port.</summary>
    public static RunspaceConnectionInfo ConnectionInfo(PsTarget t, LocalForward? forward = null) =>
        t.Agent is null ? WsManConnectionInfo(t, forward) : AgentConnectionInfo(t);

    /// <summary>The agent signs the account in on the device itself: a local account is ".\user" there.</summary>
    public static string? AgentUserName(CredentialSet c)
    {
        if (c.IsEmpty) return null;
        var user = c.Username!.Trim();
        if (user.Contains('\\') || user.Contains('@')) return user;
        return c.IsLocalAccount || string.IsNullOrEmpty(c.Domain) ? $".\\{user}" : $"{c.Domain}\\{user}";
    }

    /// <summary>The account for the RemoteHub Agent (see <see cref="AgentUserName"/>), or null without credentials.</summary>
    public static PSCredential? AgentCredential(CredentialSet c) => Credential(AgentUserName(c), c);

    private static PSCredential? Credential(string? user, CredentialSet c)
    {
        if (user is null) return null;
        var pw = c.Password?.ToSecureString() ?? new SecureString(); // decrypted straight into the SecureString
        pw.MakeReadOnly();
        return new PSCredential(user, pw);
    }

    // The agent has no "current Windows user" sign-in: without credentials it refuses the session.
    private static AgentConnectionInfo AgentConnectionInfo(PsTarget t) =>
        new(t.Host.Trim(), t.Agent!, Credential(AgentUserName(t.Credentials), t.Credentials));

    private static WSManConnectionInfo WsManConnectionInfo(PsTarget t, LocalForward? forward)
    {
        var host = forward?.Host ?? t.Host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]"; // IPv6
        var uri = new Uri($"{(t.UseSsl ? "https" : "http")}://{host}:{forward?.Port ?? t.Port}/wsman");
        var cred = Credential(UserName(t.Credentials, t.Host), t.Credentials);
        // No credential: the current Windows user is used (Kerberos/Negotiate).
        return new WSManConnectionInfo(uri, ShellUri, cred)
        {
            AuthenticationMechanism = AuthenticationMechanism.Default,
            OpenTimeout = 20_000,
            OperationTimeout = 0, // no limit: long-running commands and big uploads
            IdleTimeout = -1,
            // Through a jump host the name is 127.0.0.1, so WinRM can't check it: Open has looked at the certificate the
            // target presents through the jump host first (CheckCertificate), and the sign-in is bound to the TLS channel.
            SkipCNCheck = forward is not null,
        };
    }

    /// <summary>Opens a remote runspace (blocking – call from a background thread).</summary>
    public static Runspace Open(PsTarget t, PSHost? host = null)
    {
        var via = t is { Agent: null, Via: { } v } ? v : null;
        if (via is not null && t.UseSsl) CheckCertificate(t, ct => OpenThrough(via, t, ct), ConfirmCertificate);
        // WinRM behind a jump host: to a local port that leads there, held while the runspace is open.
        var forward = via is null ? null : JumpForwarders.Open(via, t.Host, t.Port);
        Runspace? rs = null;
        try
        {
            var info = ConnectionInfo(t, forward);
            rs = host is null ? RunspaceFactory.CreateRunspace(info) : RunspaceFactory.CreateRunspace(host, info);
            if (forward is not null)
                rs.StateChanged += (_, e) => { if (e.RunspaceStateInfo.State is RunspaceState.Closed or RunspaceState.Broken) forward.Dispose(); };
            rs.Open();
            return rs;
        }
        catch (Exception ex)
        {
            rs?.Dispose();
            forward?.Dispose();
            // WinRM only saw the connection closed: the jump host's answer says why.
            if (forward?.TakeError() is { } why) throw new JumpHostException(why, inner: ex);
            throw;
        }
    }

    private static async Task<Stream> OpenThrough(JumpRoute via, PsTarget t, CancellationToken ct) =>
        await JumpTunnels.OpenAsync(via, t.Host.Trim(), t.Port, ct).ConfigureAwait(false);

    /// <summary>
    /// WinRM over HTTPS through a jump host only sees 127.0.0.1 and skips the name check, so any certificate this PC trusts
    /// would pass. As for RDP (MainWindow.RdpRouteAsync), the target's certificate is read through the jump host first: one
    /// this PC trusts for the name is fine; otherwise only the one trusted for this connection, and a new or changed one is
    /// asked through <paramref name="confirm"/> (<see cref="ConfirmCertificate"/>). Nothing is sent after the TLS handshake. The check uses its own
    /// connection through the jump host, as RDP's does: it stops a jump host that answers with another certificate, not
    /// one that answers the check and WinRM's own connection differently.
    /// </summary>
    /// <exception cref="AuthenticationException">The certificate wasn't trusted.</exception>
    internal static void CheckCertificate(PsTarget t, Func<CancellationToken, Task<Stream>> open, Func<PsTarget, PresentedCertificate, bool>? confirm)
    {
        var host = t.Host.Trim();
        (X509Certificate2 Certificate, bool Valid) presented;
        try
        {
            presented = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var stream = await open(timeout.Token).ConfigureAwait(false);
                return await ServerCertificates.TlsAsync(stream, host, timeout.Token).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (JumpHostException) { throw; }
        catch (Exception ex)
        {
            throw new IOException($"{host} didn't complete a TLS handshake through the jump host on port {t.Port}: {ex.GetBaseException().Message}", ex);
        }
        using var cert = presented.Certificate;
        var pin = ServerCertificates.Pin(cert);
        if (presented.Valid || pin == t.TrustedCertificate) return;
        var seen = new PresentedCertificate(host, cert.GetNameInfo(X509NameType.SimpleName, false), cert.GetNameInfo(X509NameType.SimpleName, true),
            cert.NotAfter, cert.Thumbprint, pin, Changed: t.TrustedCertificate is not null);
        if (confirm?.Invoke(t, seen) != true)
            throw new AuthenticationException($"The WinRM certificate {host} presents through the jump host isn't trusted, so RemoteHub didn't sign in.");
    }

    /// <summary>Opens a session, reads version and computer name, closes it again.</summary>
    public static Task<PsRemotingStatus> TestAsync(PsTarget t) => Task.Run(() =>
    {
        try
        {
            using var rs = Open(t);
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript("'PowerShell ' + $PSVersionTable.PSVersion.ToString(2) + ' on ' + $env:COMPUTERNAME");
            var info = ps.Invoke().FirstOrDefault()?.ToString() ?? "PowerShell remoting";
            return new PsRemotingStatus { State = PsRemotingState.Available, CheckedUtc = DateTime.UtcNow, Message = info };
        }
        catch (Exception ex)
        {
            return new PsRemotingStatus { State = PsRemotingState.Unavailable, CheckedUtc = DateTime.UtcNow, Message = Explain(ex, t) };
        }
    });

    /// <summary>The HTTPS test failed on the certificate (the connection test then offers to check and trust it).</summary>
    public const string UntrustedCertificate = "The HTTPS certificate of the computer is not trusted by this PC, or its name does not match the host name.";

    /// <summary>The account was refused (the connection test then points to the credentials, not to the setup script).</summary>
    public const string AccessDenied =
        "Access denied. Either the user name or password is wrong, or the account signs in but isn't allowed: it must be an " +
        "administrator (or in \"Remote Management Users\") – and a local administrator only gets its admin rights over the network " +
        "if \"UAC restrictions for local accounts on network logons\" are off there (LocalAccountTokenFilterPolicy = 1; security " +
        "baselines turn them on). A domain account, or the RemoteHub Agent, works without that.";

    /// <summary>Short, actionable text for the usual WinRM failures. Never contains the password.</summary>
    public static string Explain(Exception ex, PsTarget t)
    {
        if (t.Agent is not null) return AgentExplain(ex);
        if (ex is JumpHostException) return Trim(ex.Message);
        var inner = ex;
        while (inner.InnerException is not null && inner is not PSRemotingTransportException) inner = inner.InnerException;
        var msg = inner.Message;
        if (msg.Contains("TrustedHosts", StringComparison.OrdinalIgnoreCase) && t.Via is { } via)
            return $"Through the jump host {via.Name}, WinRM connects to a local port (127.0.0.1): Kerberos isn't possible there, and this PC " +
                   "only sends a password over HTTP to computers it trusts. Use HTTPS for this connection (PowerShell remoting settings, or " +
                   "\"Set up PowerShell remoting on the target\"), install the RemoteHub Agent on it, or an administrator adds 127.0.0.1 to " +
                   "this PC's WinRM TrustedHosts list.";
        if (msg.Contains("TrustedHosts", StringComparison.OrdinalIgnoreCase))
            return "This PC only sends a password over HTTP to computers it trusts (Kerberos is not possible here – e.g. an IP address or a " +
                   "computer outside the domain). Use HTTPS for this connection (PowerShell remoting settings), or an administrator adds the " +
                   $"computer to this PC's WinRM TrustedHosts list.";
        if (inner is PSRemotingTransportException te)
        {
            if ((te.ErrorCode == 5 || msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) && t.Credentials.IsEmpty)
                return "Access denied – no credentials are set for this connection (or its folder), so your own Windows account was used, " +
                       "and the remote computer doesn't accept it. Set credentials on the connection or its folder.";
            if (te.ErrorCode == 5 || msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                return AccessDenied;
            if (msg.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) || msg.Contains("cannot complete the operation", StringComparison.OrdinalIgnoreCase))
                return $"No WinRM service answered on {t.Display}. PowerShell remoting is probably not enabled there, or a firewall blocks the port.";
            if (msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return UntrustedCertificate;
        }
        return Trim(msg);
    }

    /// <summary>Agent errors already carry a readable message from the transport.</summary>
    private static string AgentExplain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is AgentTransportException || e is PSRemotingTransportException te && te.Message.Contains("RemoteHub Agent", StringComparison.Ordinal))
                return Trim(e.Message);
        return Trim(ex.GetBaseException().Message);
    }

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}
