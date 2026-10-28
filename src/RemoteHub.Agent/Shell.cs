namespace RemoteHub.Agent;

/// <summary>An account that passed the sign-in check. Owns its Windows token (if any).</summary>
public sealed class ShellIdentity(string account, IntPtr token = default, IReadOnlyCollection<string>? groupSids = null, string? userSid = null) : IDisposable
{
    /// <summary>The account as Windows resolved it: DOMAIN\user, or COMPUTERNAME\user for a local account.</summary>
    public string Account { get; } = account;

    /// <summary>The account's own SID (null where the authenticator doesn't know it). Automatic sign-in uses exactly this account.</summary>
    public string? UserSid { get; } = userSid;
    internal IntPtr Token { get; private set; } = token;

    /// <summary>SIDs of the account and all its groups (from the sign-in token).</summary>
    public IReadOnlyCollection<string> GroupSids { get; } = groupSids ?? [];

    /// <summary>Member of at least one of these groups (SIDs or names)?</summary>
    public bool InAny(IEnumerable<string> groups) =>
        groups.Any(g => GroupNames.Resolve(g) is { } sid && GroupSids.Contains(sid.Value, StringComparer.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (Token != IntPtr.Zero) Native.CloseHandle(Token);
        Token = IntPtr.Zero;
    }
}

public enum AuthOutcome { Ok, BadCredentials, NotAllowed, Error }

public sealed record AuthResult(AuthOutcome Outcome, string Reason, ShellIdentity? Identity = null)
{
    public static AuthResult Fail(AuthOutcome outcome, string reason) => new(outcome, reason);
}

/// <summary>Checks a user name and password on this device and decides whether the account may connect.</summary>
public interface IShellAuthenticator
{
    AuthResult Authenticate(string userName, string password);

    /// <summary>Same, but the account must be in one of <paramref name="allowedGroups"/> (a right from the policy).</summary>
    AuthResult Authenticate(string userName, string password, IReadOnlyList<string> allowedGroups) => Authenticate(userName, password);

    /// <summary>
    /// Same, with the password in a buffer the caller wipes after the call – the agent's sign-in uses this one, so an
    /// implementation that hands the password to Windows never needs to make a string of it.
    /// </summary>
    AuthResult Authenticate(string userName, char[] password, IReadOnlyList<string> allowedGroups) => Authenticate(userName, new string(password), allowedGroups);
}

/// <summary>A running <c>powershell.exe -s</c>: its standard input and output carry the remoting protocol.</summary>
public interface IShellProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    int ProcessId { get; }
}

public interface IShellLauncher
{
    IShellProcess Start(ShellIdentity identity);
}

public static class ShellCommand
{
    /// <summary>
    /// Windows PowerShell 5.1 in server mode – present on every supported Windows. "-Version 5.1" avoids the legacy
    /// "PowerShell 2.0" notice that a bare "-s" prints.
    /// </summary>
    public static string PowerShellExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    public const string Arguments = "-Version 5.1 -s -NoLogo -NoProfile";
}
