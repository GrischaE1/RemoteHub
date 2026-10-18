using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>
/// Automatic sign-in on the console through the agent's credential provider (docs/credential-provider-design.md). The
/// service hands a single-use ticket to the provider in LogonUI over a local pipe; the Windows implementation is
/// <see cref="AutoSignInBroker"/>, tests pass their own.
/// </summary>
public interface IAutoSignIn
{
    /// <summary>Null when the device can sign in automatically now; otherwise why not (for the log and the audit).</summary>
    string? Unavailable();

    /// <summary>
    /// Hands the ticket to the provider in the request's session and waits for Windows' answer. Never throws for an
    /// expected failure – the result says why. Only one at a time per device; a second one fails at once.
    /// </summary>
    Task<AutoSignInResult> SignInAsync(AutoSignInRequest request, CancellationToken ct);
}

/// <param name="Session">The console session in the sign-in state.</param>
/// <param name="Domain">The account as Windows resolved it at the agent sign-in (local accounts: the computer name).</param>
/// <param name="Sid">The account's SID (for the provider's tile).</param>
/// <param name="Password">The password RemoteHub sent for this (UTF-16LE, protected). The broker doesn't dispose it.</param>
/// <param name="Recheck">Run again right before the ticket goes out (§6.2): null = still fine, else why not.</param>
/// <param name="SecureAttention">Sends Ctrl+Alt+Del when the sign-in screen needs it to load the providers: null = sent.</param>
public sealed record AutoSignInRequest(int Session, string Domain, string User, string Sid, ProtectedSecret Password,
    Func<string?> Recheck, Func<string?> SecureAttention);

/// <param name="Code">See <see cref="AutoSignInFailure"/>; "" on success.</param>
/// <param name="Status">Windows' NTSTATUS from LogonUI (0 = signed in); 0 when it never got that far.</param>
public sealed record AutoSignInResult(bool Ok, string Code, string Message, int Status = 0, int Substatus = 0)
{
    /// <summary>
    /// The ticket went to the provider but Windows' answer never came back (the pipe ended, broke or timed out), so the
    /// account may be signed in all the same. The caller looks at the console before calling it a failure (§6.3).
    /// </summary>
    public bool Unconfirmed { get; init; }

    /// <summary>NotOffered only: the Win32 error of the service's own check with Windows (0 = accepted); null when it couldn't check.</summary>
    public int? CheckedError { get; init; }

    public static AutoSignInResult Failed(string code, string message) => new(false, code, message);

    /// <summary>A failure after the ticket went out: see <see cref="Unconfirmed"/>.</summary>
    public static AutoSignInResult Lost(string code, string message) => new(false, code, message) { Unconfirmed = true };
}

/// <summary>Why an automatic sign-in didn't happen – the reason codes RemoteHub gets with SignInFailed.</summary>
public static class AutoSignInFailure
{
    public const string NotAllowed = "notAllowed", Unavailable = "unavailable", SessionState = "sessionState", ExistingSession = "existingSession",
        Busy = "busy", RateLimit = "rateLimit", Timeout = "timeout", NoProvider = "noProvider", SessionChanged = "sessionChanged",
        Windows = "windows", WrongAccount = "wrongAccount", NotOffered = "notOffered";
}

/// <summary>
/// The provider's NotOffered: the sign-in screen doesn't list the account and didn't take the ticket (Windows 11 leaves
/// locked-out, disabled and – for a while – newly created accounts out, lab C-AUTO-20; docs/credential-provider-design.md
/// §6.4, §8.2). Windows' answer never comes then, so the service asks Windows itself: an interactive LogonUserW with the
/// ticket's password, whose error is worded like the NTSTATUS LogonUI would have reported.
/// </summary>
public static class AutoSignInNotOffered
{
    public const string Accepted = "The sign-in screen doesn't offer this account yet, although Windows accepts its sign-in – " +
        "a new account may only appear there later. Try again in a while, or sign in at the device once.";

    // winerror.h -> the ntstatus.h value LogonUI would have reported, so the texts are AutoSignInStatus's.
    private static readonly Dictionary<int, uint> Statuses = new()
    {
        [1326] = AutoSignInStatus.LogonFailure,              // ERROR_LOGON_FAILURE
        [1327] = AutoSignInStatus.AccountRestriction,        // ERROR_ACCOUNT_RESTRICTION
        [1328] = AutoSignInStatus.InvalidLogonHours,         // ERROR_INVALID_LOGON_HOURS
        [1329] = AutoSignInStatus.InvalidWorkstation,        // ERROR_INVALID_WORKSTATION
        [1330] = AutoSignInStatus.PasswordExpired,           // ERROR_PASSWORD_EXPIRED
        [1331] = AutoSignInStatus.AccountDisabled,           // ERROR_ACCOUNT_DISABLED
        [1317] = AutoSignInStatus.NoSuchUser,                // ERROR_NO_SUCH_USER
        [1311] = AutoSignInStatus.NoLogonServers,            // ERROR_NO_LOGON_SERVERS
        [1385] = AutoSignInStatus.LogonTypeNotGranted,       // ERROR_LOGON_TYPE_NOT_GRANTED
        [1398] = AutoSignInStatus.TimeDifferenceAtDc,        // ERROR_TIME_SKEW
        [1789] = AutoSignInStatus.TrustedRelationshipFailure, // ERROR_TRUSTED_RELATIONSHIP_FAILURE
        [1793] = AutoSignInStatus.AccountExpired,            // ERROR_ACCOUNT_EXPIRED
        [1907] = AutoSignInStatus.PasswordMustChange,        // ERROR_PASSWORD_MUST_CHANGE
        [1909] = AutoSignInStatus.AccountLockedOut,          // ERROR_ACCOUNT_LOCKED_OUT
        [1922] = AutoSignInStatus.SmartCardLogonRequired,    // ERROR_SMARTCARD_LOGON_REQUIRED
    };

    /// <param name="checkLogon">Signs the account in interactively and closes the token at once: 0 when Windows accepts
    /// it, else the Win32 error. Tests pass their own; the service passes <see cref="CheckLogon"/>.</param>
    public static AutoSignInResult Result(string domain, string user, ProtectedSecret password, Func<string, string, ProtectedSecret, int> checkLogon)
    {
        int error;
        try { error = checkLogon(domain, user, password); }
        catch (Exception) { error = -1; }
        if (error == 0) return new AutoSignInResult(false, AutoSignInFailure.NotOffered, Accepted) { CheckedError = 0 };
        if (error > 0 && Statuses.TryGetValue(error, out var status))
            return new AutoSignInResult(false, AutoSignInFailure.NotOffered, AutoSignInStatus.Describe((int)status, 0), (int)status) { CheckedError = error };
        return new AutoSignInResult(false, AutoSignInFailure.NotOffered, error > 0
            ? $"The sign-in screen doesn't offer this account, and Windows refused its sign-in (error {error})."
            : "The sign-in screen doesn't offer this account – it may be disabled or locked.") { CheckedError = error > 0 ? error : null };
    }

    /// <summary>
    /// LogonUserW(LOGON32_LOGON_INTERACTIVE) as the service: 0 or the Win32 error. The password is in a pinned buffer for
    /// the call only and zeroed right after; the token, if any, is closed at once. This is one more interactive sign-in
    /// attempt for the account (with a wrong password it counts towards the lockout like any attempt).
    /// </summary>
    public static int CheckLogon(string domain, string user, ProtectedSecret password)
    {
        var error = -1;
        password.Reveal(utf16 =>
        {
            var buffer = GC.AllocateArray<byte>(utf16.Length + 2, pinned: true); // UTF-16LE and its terminator
            try
            {
                utf16.CopyTo(buffer);
                if (LogonUserW(user, domain, Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0), 2 /* LOGON32_LOGON_INTERACTIVE */,
                        0 /* LOGON32_PROVIDER_DEFAULT */, out var token))
                {
                    Native.CloseHandle(token);
                    error = 0;
                }
                else error = Marshal.GetLastWin32Error();
            }
            finally { ProtectedSecret.CryptographicWipe(buffer); }
        });
        return error;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUserW(string user, string domain, IntPtr password, int logonType, int logonProvider, out IntPtr token);
}
/// <summary>
/// A secret in its own pinned buffer, protected with CryptProtectMemory (same process) while it waits. Never a .NET
/// string; zeroed on dispose. <see cref="Reveal"/> gives the clear text only for the duration of a callback.
/// </summary>
public sealed class ProtectedSecret : IDisposable
{
    private const int BlockSize = 16; // CRYPTPROTECTMEMORY_BLOCK_SIZE
    private byte[]? _buffer;          // pinned, length a multiple of BlockSize
    private readonly int _length;
    private readonly object _lock = new();

    /// <summary>Copies <paramref name="clear"/> and protects the copy at once. The caller zeroes its own buffer.</summary>
    public ProtectedSecret(ReadOnlySpan<byte> clear)
    {
        _length = clear.Length;
        _buffer = GC.AllocateArray<byte>(Math.Max(BlockSize, (clear.Length + BlockSize - 1) / BlockSize * BlockSize), pinned: true);
        clear.CopyTo(_buffer);
        if (!CryptProtectMemory(_buffer, (uint)_buffer.Length, 0)) // CRYPTPROTECTMEMORY_SAME_PROCESS
        {
            CryptographicWipe(_buffer);
            _buffer = null;
            throw new System.ComponentModel.Win32Exception();
        }
    }

    public int Length => _length;

    /// <summary>The clear text for the callback only; protected again (or zeroed if that fails) when it returns.</summary>
    public void Reveal(Action<ReadOnlySpan<byte>> use)
    {
        lock (_lock)
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(ProtectedSecret));
            if (!CryptUnprotectMemory(buffer, (uint)buffer.Length, 0)) throw new System.ComponentModel.Win32Exception();
            try { use(buffer.AsSpan(0, _length)); }
            finally
            {
                if (!CryptProtectMemory(buffer, (uint)buffer.Length, 0))
                {
                    CryptographicWipe(buffer);
                    _buffer = null;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_buffer is { } b) CryptographicWipe(b);
            _buffer = null;
        }
    }

    internal static void CryptographicWipe(Span<byte> data) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(data);

    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectMemory(byte[] data, uint size, uint flags);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectMemory(byte[] data, uint size, uint flags);
}

/// <summary>
/// The pipe messages between the service and the credential provider – the same bytes as the provider's Wire.h:
/// <c>[u8 version = 1][u8 type][u16 payload length][payload]</c>, little-endian, strings as a u16 length in UTF-16 units
/// followed by the units.
/// </summary>
public static class AutoSignInWire
{
    public const byte Version = 1;
    public const int MaxMessageBytes = 4096, NonceBytes = 16, MaxNameChars = 256, MaxPasswordChars = 256;
    public const byte Pending = 1, Claim = 2, Ticket = 3, Result = 4, NotOffered = 5;
    private const int HeaderBytes = 4;

    public static byte[] WritePending(ReadOnlySpan<byte> nonce, uint session) => NonceSession(Pending, nonce, session);

    /// <summary>Into <paramref name="destination"/> (the caller's pinned buffer, which it zeroes after sending). Returns the length.</summary>
    public static int WriteTicket(Span<byte> destination, ReadOnlySpan<byte> nonce, string domain, string user, string sid, ReadOnlySpan<byte> passwordUtf16)
    {
        if (nonce.Length != NonceBytes || domain.Length > MaxNameChars || user.Length is 0 or > MaxNameChars || sid.Length > MaxNameChars
            || passwordUtf16.Length % 2 != 0 || passwordUtf16.Length is 0 || passwordUtf16.Length / 2 > MaxPasswordChars)
            throw new ArgumentException("A ticket field is empty or too long.");
        var pos = HeaderBytes;
        nonce.CopyTo(destination[pos..]);
        pos += NonceBytes;
        foreach (var s in new[] { domain, user, sid })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], (ushort)s.Length);
            pos += 2;
            foreach (var ch in s) { BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], ch); pos += 2; }
        }
        BinaryPrimitives.WriteUInt16LittleEndian(destination[pos..], (ushort)(passwordUtf16.Length / 2));
        pos += 2;
        passwordUtf16.CopyTo(destination[pos..]);
        pos += passwordUtf16.Length;
        return Header(destination, Ticket, pos);
    }

    /// <summary>A Claim with this nonce and session, nothing else.</summary>
    public static bool IsClaim(ReadOnlySpan<byte> m, ReadOnlySpan<byte> nonce, uint session) =>
        TryPayload(m, Claim, NonceBytes + 4, out var p) && p[..NonceBytes].SequenceEqual(nonce) && BinaryPrimitives.ReadUInt32LittleEndian(p[NonceBytes..]) == session;

    /// <summary>A Result for this nonce: NTSTATUS and substatus; null if it isn't one.</summary>
    public static (int Status, int Substatus)? ReadResult(ReadOnlySpan<byte> m, ReadOnlySpan<byte> nonce)
    {
        if (!TryPayload(m, Result, NonceBytes + 8, out var p) || !p[..NonceBytes].SequenceEqual(nonce)) return null;
        return (BinaryPrimitives.ReadInt32LittleEndian(p[NonceBytes..]), BinaryPrimitives.ReadInt32LittleEndian(p[(NonceBytes + 4)..]));
    }

    /// <summary>A NotOffered for this nonce: the sign-in screen won't take the ticket (the provider has dropped it).</summary>
    public static bool IsNotOffered(ReadOnlySpan<byte> m, ReadOnlySpan<byte> nonce) =>
        TryPayload(m, NotOffered, NonceBytes, out var p) && p.SequenceEqual(nonce);

    private static byte[] NonceSession(byte type, ReadOnlySpan<byte> nonce, uint session)
    {
        if (nonce.Length != NonceBytes) throw new ArgumentException("The nonce must be 16 bytes.");
        var m = new byte[HeaderBytes + NonceBytes + 4];
        nonce.CopyTo(m.AsSpan(HeaderBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(HeaderBytes + NonceBytes), session);
        Header(m, type, m.Length);
        return m;
    }

    private static int Header(Span<byte> m, byte type, int total)
    {
        m[0] = Version;
        m[1] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(m[2..], (ushort)(total - HeaderBytes));
        return total;
    }

    /// <summary>The payload of a message of exactly this type and payload length.</summary>
    private static bool TryPayload(ReadOnlySpan<byte> m, byte type, int payloadBytes, out ReadOnlySpan<byte> payload)
    {
        var ok = m.Length == HeaderBytes + payloadBytes && m[0] == Version && m[1] == type && BinaryPrimitives.ReadUInt16LittleEndian(m[2..]) == payloadBytes;
        payload = ok ? m[HeaderBytes..] : default;
        return ok;
    }
}

/// <summary>
/// At most 3 automatic sign-ins per account (SID) in 15 minutes, on the whole device (§6.2 check 7) – an outdated stored
/// password then can't lock the account out. Keyed by account, not by address: behind a jump host everyone has the
/// jump host's address.
/// </summary>
public sealed class AutoSignInGate(int maxAttempts = 3, TimeSpan? window = null, Func<DateTime>? clock = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(15);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, List<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts an attempt for the account; false (and nothing counted) when it has had its share.</summary>
    public bool TryBegin(string sid)
    {
        lock (_attempts)
        {
            var now = _now();
            if (!_attempts.TryGetValue(sid, out var list)) _attempts[sid] = list = [];
            list.RemoveAll(t => now - t >= _window);
            if (list.Count >= maxAttempts) return false;
            list.Add(now);
            return true;
        }
    }
}

/// <summary>
/// What Windows' answer from LogonUI means for the operator (never the input, never more than the reason). Windows often
/// reports a refusal as the general STATUS_LOGON_FAILURE or STATUS_ACCOUNT_RESTRICTION with the actual reason in the
/// substatus – a disabled account, for example, as 0xC000006E / 0xC0000072 – so the substatus is read first.
/// </summary>
public static class AutoSignInStatus
{
    // ntstatus.h
    public const uint LogonFailure = 0xC000006D, AccountRestriction = 0xC000006E, NoSuchUser = 0xC0000064, WrongPassword = 0xC000006A,
        InvalidLogonHours = 0xC000006F, InvalidWorkstation = 0xC0000070, PasswordExpired = 0xC0000071, AccountDisabled = 0xC0000072,
        AccountExpired = 0xC0000193, PasswordMustChange = 0xC0000224, AccountLockedOut = 0xC0000234, LogonTypeNotGranted = 0xC000015B,
        NoLogonServers = 0xC000005E, TrustedRelationshipFailure = 0xC000018D, TimeDifferenceAtDc = 0xC0000133,
        SmartCardLogonRequired = 0xC00002FA, AuthenticationFirewallFailed = 0xC0000413;

    public static string Describe(int status, int substatus)
    {
        var s = (uint)status;
        if (s is LogonFailure or AccountRestriction && (uint)substatus != s && Specific((uint)substatus) is { } reason) return reason;
        return Specific(s) ?? s switch
        {
            LogonFailure => "The password was wrong (the stored password may be out of date).",
            AccountRestriction => "Account restrictions prevent this sign-in (for example a blank password, or the account needs a smart card or Windows Hello).",
            _ => $"Windows refused the sign-in (0x{status:X8}).",
        };
    }

    /// <summary>A status that names the reason itself; null for the general ones and anything unknown.</summary>
    private static string? Specific(uint status) => status switch
    {
        NoSuchUser => "The account doesn't exist on the device.",
        WrongPassword => "The password was wrong (the stored password may be out of date).",
        AccountLockedOut => "The account is locked.",
        PasswordExpired or PasswordMustChange => "The password has expired and must be changed at the device.",
        AccountDisabled => "The account is disabled.",
        AccountExpired => "The account has expired.",
        LogonTypeNotGranted => "The account may not sign in on this device's screen (\"Allow log on locally\").",
        InvalidLogonHours => "The account may not sign in at this time of day.",
        InvalidWorkstation => "The account may not sign in on this device.",
        SmartCardLogonRequired => "The account must sign in with a smart card.",
        AuthenticationFirewallFailed => "The sign-in was blocked by the domain's authentication firewall.",
        NoLogonServers => "No domain controller could be reached to check the account.",
        TrustedRelationshipFailure => "The device's trust relationship with its domain failed.",
        TimeDifferenceAtDc => "The device's clock differs too much from the domain controller's.",
        _ => null,
    };
}
