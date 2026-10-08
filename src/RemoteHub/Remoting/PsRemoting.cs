using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using RemoteHub.Models;

namespace RemoteHub.Remoting;

/// <summary>Where and as whom to open a PowerShell remoting (WinRM) session. Holds the password in memory only.</summary>
public sealed record PsTarget(string Host, bool UseSsl, CredentialSet Credentials)
{
    public int Port => UseSsl ? 5986 : 5985;
    public string Display => $"{Host} ({(UseSsl ? "HTTPS" : "HTTP")} {Port})";
}

public static class PsRemoting
{
    private const string ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";

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
    public static bool NeedsPassword(CredentialSet c) => !c.IsEmpty && string.IsNullOrEmpty(c.Password);

    public static WSManConnectionInfo ConnectionInfo(PsTarget t)
    {
        var host = t.Host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]"; // IPv6
        var uri = new Uri($"{(t.UseSsl ? "https" : "http")}://{host}:{t.Port}/wsman");
        PSCredential? cred = null;
        if (UserName(t.Credentials, t.Host) is { } user)
        {
            var pw = new SecureString();
            foreach (var ch in t.Credentials.Password ?? "") pw.AppendChar(ch);
            pw.MakeReadOnly();
            cred = new PSCredential(user, pw);
        }
        // No credential: the current Windows user is used (Kerberos/Negotiate).
        return new WSManConnectionInfo(uri, ShellUri, cred)
        {
            AuthenticationMechanism = AuthenticationMechanism.Default,
            OpenTimeout = 20_000,
            OperationTimeout = 0, // no limit: long-running commands and big uploads
            IdleTimeout = -1,
        };
    }

    /// <summary>Opens a remote runspace (blocking – call from a background thread).</summary>
    public static Runspace Open(PsTarget t, PSHost? host = null)
    {
        var rs = host is null ? RunspaceFactory.CreateRunspace(ConnectionInfo(t)) : RunspaceFactory.CreateRunspace(host, ConnectionInfo(t));
        try { rs.Open(); }
        catch { rs.Dispose(); throw; }
        return rs;
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

    /// <summary>Short, actionable text for the usual WinRM failures. Never contains the password.</summary>
    public static string Explain(Exception ex, PsTarget t)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is not PSRemotingTransportException) inner = inner.InnerException;
        var msg = inner.Message;
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
                return "Access denied – the account is not allowed to use PowerShell remoting on this computer (it needs to be an administrator " +
                       "or a member of \"Remote Management Users\"), or the user name or password is wrong.";
            if (msg.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) || msg.Contains("cannot complete the operation", StringComparison.OrdinalIgnoreCase))
                return $"No WinRM service answered on {t.Display}. PowerShell remoting is probably not enabled there, or a firewall blocks the port.";
            if (msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return "The HTTPS certificate of the computer is not trusted by this PC, or its name does not match the host name.";
        }
        return Trim(msg);
    }

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}
