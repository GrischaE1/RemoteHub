using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>
/// The service's side of the pipe to the credential provider (docs/credential-provider-design.md §7). Created once when
/// the service starts, before it listens on the network: the first instance with FIRST_PIPE_INSTANCE (nobody can have
/// created it before), SYSTEM-only, message mode, local only. Only LogonUI.exe from System32, running as SYSTEM in the
/// console session, is kept as a client; anything else is disconnected at once.
/// </summary>
internal sealed class AutoSignInBroker : IAutoSignIn, IDisposable
{
    public const string PipeName = "RemoteHubAgent.AutoSignIn";
    public const string Clsid = "{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}";
    public const string DllName = "RemoteHubCredentialProvider.dll";
    private const int MaxInstances = 4;

    private readonly AgentLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly object _lock = new();
    private Client? _client; // the console's LogonUI, while connected
    private string? _pipeError;

    private AutoSignInBroker(AgentLog log) => _log = log;

    /// <summary>Creates the pipe and starts accepting. Never throws: a pipe that can't be created turns the feature off (logged).</summary>
    public static AutoSignInBroker Start(AgentLog log)
    {
        var broker = new AutoSignInBroker(log);
        try
        {
            var first = Create(firstInstance: true);
            _ = Task.Run(() => broker.AcceptLoop(first));
        }
        catch (Exception ex)
        {
            // Someone else may have created it first (squatting) – never use a pipe we didn't create.
            broker._pipeError = $"the pipe {PipeName} couldn't be created ({ex.Message})";
            log.Audit(AuditEvent.AutoSignInUnavailable, $"Automatic sign-in unavailable: {broker._pipeError}");
        }
        return broker;
    }

    private static NamedPipeServerStream Create(bool firstInstance)
    {
        var security = new PipeSecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(system);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // D:P(A;;GA;;;SY)
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        // .NET always adds PIPE_REJECT_REMOTE_CLIENTS on Windows.
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            AutoSignInWire.MaxMessageBytes, AutoSignInWire.MaxMessageBytes, security);
    }

    private async Task AcceptLoop(NamedPipeServerStream server)
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await server.WaitForConnectionAsync(_stop.Token); }
            catch (OperationCanceledException) { server.Dispose(); return; }
            catch (Exception ex)
            {
                _log.Write($"Automatic sign-in: pipe error ({ex.Message})");
                server.Dispose();
                server = await NextInstance();
                continue;
            }
            var connected = server;
            server = await NextInstance(); // the next one waits at once: one stuck client never blocks the console's LogonUI
            Accept(connected);
        }
    }

    /// <summary>A new waiting instance; retried while all instances are taken.</summary>
    private async Task<NamedPipeServerStream> NextInstance()
    {
        while (true)
        {
            try { return Create(firstInstance: false); }
            catch (Exception) when (!_stop.IsCancellationRequested) { await Task.Delay(1000, _stop.Token); }
        }
    }

    /// <summary>§7.2: LogonUI.exe from System32, as SYSTEM, in the console session. The newest such client replaces an older one.</summary>
    private void Accept(NamedPipeServerStream pipe)
    {
        var (ok, why, session) = CheckClient(pipe.SafePipeHandle);
        if (!ok)
        {
            _log.Write($"Automatic sign-in: pipe client refused ({why})");
            pipe.Dispose();
            return;
        }
        Client? old;
        lock (_lock)
        {
            old = _client;
            _client = new Client(pipe, session);
        }
        old?.Pipe.Dispose();
    }

    private static (bool Ok, string Why, uint Session) CheckClient(SafePipeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out var pid) || !GetNamedPipeClientSessionId(pipe, out var session)) return (false, "client unknown", 0);
        if (session != WtsNative.WTSGetActiveConsoleSessionId()) return (false, $"session {session} isn't the console", session);
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == IntPtr.Zero) return (false, $"process {pid} can't be opened", session);
        try
        {
            var image = new char[1024];
            var length = image.Length;
            if (!QueryFullProcessImageNameW(process, 0, image, ref length)) return (false, $"process {pid}'s image is unknown", session);
            var path = new string(image, 0, length);
            var expected = Path.Combine(Environment.SystemDirectory, "LogonUI.exe");
            if (!string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)) return (false, $"{path} isn't LogonUI", session);
            if (!IsSystem(process)) return (false, "not SYSTEM", session);
            return (true, "", session);
        }
        finally { Native.CloseHandle(process); }
    }

    private static bool IsSystem(IntPtr process)
    {
        if (!WtsNative.OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        catch { return false; }
        finally { Native.CloseHandle(token); }
    }

    // ------------------------------------------------------------------ availability

    private (DateTime Checked, string? Why)? _availability;

    /// <summary>
    /// The pipe is up, the provider is registered by the MSI (its key, and InprocServer32 pointing to the DLL next to the
    /// agent), Windows' policy doesn't exclude it, and the DLL is Authenticode-signed by the same certificate as the agent
    /// (§8.4). Checked at most once a minute.
    /// </summary>
    public string? Unavailable()
    {
        if (_pipeError is not null) return _pipeError;
        lock (_lock)
        {
            if (_availability is { } a && DateTime.UtcNow - a.Checked < TimeSpan.FromMinutes(1)) return a.Why;
        }
        var why = CheckInstallation();
        lock (_lock) _availability = (DateTime.UtcNow, why);
        return why;
    }

    private static string? CheckInstallation()
    {
        try
        {
            var dll = Path.Combine(AppContext.BaseDirectory, DllName);
            using (var provider = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{Clsid}"))
                if (provider is null) return "the credential provider isn't installed";
            using (var inproc = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32"))
                if (inproc?.GetValue(null) is not string registered || !string.Equals(Path.GetFullPath(registered), dll, StringComparison.OrdinalIgnoreCase))
                    return "the credential provider's registration doesn't point to the agent's folder";
            using (var system = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System"))
                if (system?.GetValue("ExcludedCredentialProviders") is string excluded && excluded.Contains(Clsid, StringComparison.OrdinalIgnoreCase))
                    return "the credential provider is excluded by policy (ExcludedCredentialProviders)";
            if (!File.Exists(dll)) return $"{DllName} is missing";
            if (!Authenticode.Trusted(dll)) return $"{DllName} isn't signed by a trusted publisher";
            var agentExe = Environment.ProcessPath ?? "";
            if (Authenticode.Signer(dll) is not { } dllSigner || Authenticode.Signer(agentExe) is not { } agentSigner || dllSigner != agentSigner)
                return $"{DllName} isn't signed by the same certificate as the agent";
            return null;
        }
        catch (Exception ex) { return "the credential provider couldn't be checked: " + ex.Message; }
    }

    // ------------------------------------------------------------------ sign-in

    public async Task<AutoSignInResult> SignInAsync(AutoSignInRequest request, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct)) return AutoSignInResult.Failed(AutoSignInFailure.Busy, "Another automatic sign-in is running on this device.");
        Client? client = null;
        var ticketSent = false; // from here on Windows may have signed the account in, whatever happens to the pipe
        try
        {
            // The ticket's life (§7.3). Once the ticket has gone out, only that life and the service stopping end the wait for
            // Windows' answer – not the RemoteHub connection: the account may be signed in, and the caller must learn it.
            using var life = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            life.CancelAfter(TimeSpan.FromSeconds(60));
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct, life.Token);

            // The provider in the console's LogonUI. Where the sign-in screen waits for Ctrl+Alt+Del (or behind the
            // lock-screen picture), LogonUI loads the providers only after it.
            client = await WaitForClient((uint)request.Session, TimeSpan.FromSeconds(3), expiry.Token);
            if (client is null)
            {
                var sas = request.SecureAttention();
                if (sas is not null) _log.Write($"Automatic sign-in: Ctrl+Alt+Del not sent ({sas})");
                client = await WaitForClient((uint)request.Session, TimeSpan.FromSeconds(20), expiry.Token);
            }
            if (client is null)
                return AutoSignInResult.Failed(AutoSignInFailure.NoProvider, "The device's sign-in screen didn't load the RemoteHub credential provider.");

            // §6.2: someone may have signed in at the device while RemoteHub sent the password. The client is then the
            // LogonUI that has gone, and writing to it fails with a broken pipe instead of saying why (lab C-AUTO-15).
            if (request.Recheck() is { } early) return AutoSignInResult.Failed(AutoSignInFailure.SessionChanged, early);

            var nonce = RandomNumberGenerator.GetBytes(AutoSignInWire.NonceBytes);
            await Write(client, AutoSignInWire.WritePending(nonce, client.Session), expiry.Token);
            var claim = await Read(client, TimeSpan.FromSeconds(10), expiry.Token);
            if (claim is null || !AutoSignInWire.IsClaim(claim, nonce, client.Session))
            {
                Drop(client);
                return AutoSignInResult.Failed(AutoSignInFailure.NoProvider, "The credential provider didn't take the sign-in.");
            }

            // §6.2: checked again right before the ticket goes out – someone may have signed in at the device meanwhile.
            if (request.Recheck() is { } changed)
            {
                Drop(client);
                return AutoSignInResult.Failed(AutoSignInFailure.SessionChanged, changed);
            }

            var message = GC.AllocateArray<byte>(AutoSignInWire.MaxMessageBytes, pinned: true);
            try
            {
                var length = 0;
                request.Password.Reveal(password =>
                    length = AutoSignInWire.WriteTicket(message, nonce, request.Domain, request.User, request.Sid, password));
                expiry.Token.ThrowIfCancellationRequested(); // the connection ended meanwhile: the ticket never goes out
                ticketSent = true; // set before the write: a write that fails half way may still have delivered it
                await Write(client, message.AsMemory(0, length), life.Token);
            }
            finally { ProtectedSecret.CryptographicWipe(message); } // the only clear copy: gone once written

            var result = await Read(client, TimeSpan.FromSeconds(50), life.Token);
            // The sign-in screen doesn't list the account and won't take the ticket (the provider has dropped it): no
            // answer from Windows will come, so the service asks Windows itself with the same password (lab C-AUTO-20).
            if (result is not null && AutoSignInWire.IsNotOffered(result, nonce))
                return AutoSignInNotOffered.Result(request.Domain, request.User, request.Password, AutoSignInNotOffered.CheckLogon);
            if (result is null || AutoSignInWire.ReadResult(result, nonce) is not { } r)
            {
                Drop(client);
                // Typically the sign-in screen still shows the message of a failed attempt: it takes nothing until it is
                // closed. But the pipe also ends when LogonUI signed the account in and went away before the provider
                // sent its result (lab C-END-10), so the caller checks the console before calling it a failure.
                return AutoSignInResult.Lost(AutoSignInFailure.Timeout,
                    "The sign-in screen didn't take the sign-in. If it shows a message (for example after a failed attempt), click OK and try again.");
            }
            return r.Status == 0
                ? new AutoSignInResult(true, "", "Signed in.")
                : new AutoSignInResult(false, AutoSignInFailure.Windows, AutoSignInStatus.Describe(r.Status, r.Substatus), r.Status, r.Substatus);
        }
        catch (OperationCanceledException) when (ticketSent || !ct.IsCancellationRequested)
        {
            if (client is not null) Drop(client);
            const string message = "The automatic sign-in took too long.";
            return ticketSent ? AutoSignInResult.Lost(AutoSignInFailure.Timeout, message) : AutoSignInResult.Failed(AutoSignInFailure.Timeout, message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (client is not null) Drop(client);
            if (ticketSent) return AutoSignInResult.Lost(AutoSignInFailure.NoProvider, "The connection to the credential provider broke: " + ex.Message);
            // Before the ticket: a LogonUI that went away because someone signed in at the device meanwhile (C-AUTO-15).
            return request.Recheck() is { } changed
                ? AutoSignInResult.Failed(AutoSignInFailure.SessionChanged, changed)
                : AutoSignInResult.Failed(AutoSignInFailure.NoProvider, "The connection to the credential provider broke: " + ex.Message);
        }
        finally { _busy.Release(); }
    }

    private async Task<Client?> WaitForClient(uint session, TimeSpan within, CancellationToken ct)
    {
        var until = DateTime.UtcNow + within;
        while (true)
        {
            lock (_lock)
                if (_client is { } c && c.Session == session && c.Pipe.IsConnected) return c;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(200, ct);
        }
    }

    private void Drop(Client client)
    {
        lock (_lock) if (_client == client) _client = null;
        client.Pipe.Dispose();
    }

    private static async Task Write(Client client, ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await client.Pipe.WriteAsync(message, timeout.Token);
        await client.Pipe.FlushAsync(timeout.Token);
    }

    /// <summary>One whole message (4 KB at most); null on timeout, end or an oversized message.</summary>
    private static async Task<byte[]?> Read(Client client, TimeSpan within, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(within);
        var buffer = new byte[AutoSignInWire.MaxMessageBytes];
        try
        {
            var n = await client.Pipe.ReadAsync(buffer, timeout.Token);
            if (n == 0 || !client.Pipe.IsMessageComplete) return null;
            return buffer[..n];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_lock)
        {
            _client?.Pipe.Dispose();
            _client = null;
        }
    }

    private sealed record Client(NamedPipeServerStream Pipe, uint Session);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [Out] char[] name, ref int size);
}

/// <summary>Authenticode checks through WinVerifyTrust (the whole chain, no revocation check – the device may be offline).</summary>
internal static class Authenticode
{
    /// <summary>Signed, and the signature chains to a root this device trusts.</summary>
    public static bool Trusted(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, info, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = info,
                dwStateAction = 0,
                dwProvFlags = 0x00000080,  // WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    /// <summary>The thumbprint of the certificate that signed the file, or null if it isn't signed.</summary>
    public static string? Signer(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the signer of a signed file; X509CertificateLoader has no equivalent
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Thumbprint;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
