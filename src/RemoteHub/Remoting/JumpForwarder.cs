using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using RemoteHub.Services;

namespace RemoteHub.Remoting;

/// <summary>
/// A local port (127.0.0.1) that leads through a jump host to one port of a target – for what only takes a host and a port:
/// the RDP control and WinRM. Held while in use; disposing it lets go (the port stays a minute for a reconnect).
/// </summary>
public sealed class LocalForward : IDisposable
{
    private JumpForwarder? _forwarder;

    internal LocalForward(JumpForwarder forwarder)
    {
        _forwarder = forwarder;
        Port = forwarder.Port;
        Forwarder = forwarder;
    }

    internal JumpForwarder Forwarder { get; }
    public string Host => "127.0.0.1";
    public int Port { get; }

    /// <summary>Why the last connection through it couldn't be made (the jump host's answer), once – then null.</summary>
    public string? TakeError() => Forwarder.TakeError();

    public void Dispose() => Interlocked.Exchange(ref _forwarder, null)?.Release();
}

/// <summary>The local forwarders: one per jump host route and target port, shared by every session to that target.</summary>
public static class JumpForwarders
{
    internal static TimeSpan IdleClose = TimeSpan.FromMinutes(1);
    private static readonly Dictionary<string, JumpForwarder> All = [];

    /// <summary>127.0.0.1:<see cref="LocalForward.Port"/> leading to <paramref name="host"/>:<paramref name="port"/> through <paramref name="route"/>.</summary>
    public static LocalForward Open(JumpRoute route, string host, int port)
    {
        host = host.Trim();
        return Open($"{route.Key}|{host.ToLowerInvariant()}|{port}", $"{host}:{port} via {route.Name}",
            async ct => await JumpTunnels.OpenAsync(route, host, port, ct).ConfigureAwait(false));
    }

    /// <summary>With any way to the target (tests).</summary>
    internal static LocalForward Open(string key, string name, Func<CancellationToken, Task<Stream>> connect)
    {
        lock (All)
        {
            if (!All.TryGetValue(key, out var f) || !f.TryAcquire())
            {
                All[key] = f = new JumpForwarder(key, name, connect);
                f.TryAcquire();
            }
            return new LocalForward(f);
        }
    }

    internal static void Removed(string key, JumpForwarder f)
    {
        lock (All)
            if (All.TryGetValue(key, out var current) && current == f) All.Remove(key);
    }

    internal static int Count { get { lock (All) return All.Count; } }

    /// <summary>Closes every forwarder (RemoteHub closing; tests).</summary>
    public static void CloseAll()
    {
        List<JumpForwarder> all;
        lock (All) { all = [.. All.Values]; All.Clear(); }
        foreach (var f in all) f.Close();
    }
}

/// <summary>
/// Listens on 127.0.0.1 (a port Windows picks) and turns each accepted connection into a stream through the jump host.
/// Only RemoteHub's own process may connect: on a shared PC (terminal server) another user could otherwise use the jump
/// host with this user's sign-in.
/// </summary>
internal sealed class JumpForwarder
{
    private readonly string _key, _name;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private int _users; // leases and open connections
    private bool _closed;
    private Timer? _idle;
    private string? _error;

    internal JumpForwarder(string key, string name, Func<CancellationToken, Task<Stream>> connect)
    {
        _key = key;
        _name = name;
        _connect = connect;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        DiagLog.Write($"Jump host forwarder for {_name} on 127.0.0.1:{Port}");
    }

    public int Port { get; }
    /// <summary>Connections refused because another process opened them (tests).</summary>
    internal int Refused;

    internal string? TakeError() => Interlocked.Exchange(ref _error, null);

    internal bool TryAcquire()
    {
        lock (_lock)
        {
            if (_closed) return false;
            _users++;
            _idle?.Dispose();
            _idle = null;
            return true;
        }
    }

    internal void Release()
    {
        lock (_lock)
        {
            if (--_users > 0 || _closed) return;
            _idle?.Dispose();
            _idle = new Timer(_ => CloseIfIdle(), null, JumpForwarders.IdleClose, Timeout.InfiniteTimeSpan);
        }
    }

    private void CloseIfIdle()
    {
        lock (_lock)
            if (_users > 0 || _closed) return;
        Close();
    }

    internal void Close()
    {
        lock (_lock)
        {
            if (_closed && _cts.IsCancellationRequested) return;
            _closed = true;
            _idle?.Dispose();
        }
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* closed */ }
        JumpForwarders.Removed(_key, this);
        DiagLog.Write($"Jump host forwarder for {_name} closed");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; } // closed
            var pid = LoopbackOwner.ProcessOf((IPEndPoint)socket.RemoteEndPoint!, (IPEndPoint)socket.LocalEndPoint!);
            if (pid != Environment.ProcessId)
            {
                Interlocked.Increment(ref Refused);
                DiagLog.Write($"Jump host forwarder for {_name}: a connection from another process ({(pid is { } p ? $"PID {p}" : "unknown")}) was refused");
                socket.Dispose();
                continue;
            }
            if (!TryAcquire()) { socket.Dispose(); return; }
            _ = Task.Run(() => Serve(socket));
        }
    }

    private async Task Serve(Socket socket)
    {
        try
        {
            socket.NoDelay = true;
            using var local = new NetworkStream(socket, ownsSocket: true);
            Stream remote;
            try { remote = await _connect(_cts.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // The caller only sees the connection closed: the jump host's answer is kept for its error message.
                _error = ex is JumpHostException ? ex.Message : $"Through the jump host: {ex.GetBaseException().Message}";
                DiagLog.Write($"Jump host forwarder for {_name}: not connected – {_error}");
                return;
            }
            await using (remote.ConfigureAwait(false))
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                var up = Pump(local, remote, () => HalfClose(remote), both.Token);
                var down = Pump(remote, local, () => socket.Shutdown(SocketShutdown.Send), both.Token);
                // Each direction ends on its own (half-close); a failure on either ends both.
                if (await Task.WhenAny(up, down).ConfigureAwait(false) is { IsFaulted: true } or { IsCanceled: true }) both.Cancel();
                try { await Task.WhenAll(up, down).ConfigureAwait(false); }
                catch { both.Cancel(); }
            }
        }
        catch { /* the connection ended */ }
        finally { Release(); }
    }

    private static void HalfClose(Stream s)
    {
        if (s is TunnelStream t) t.CloseOutput();
        else if (s is NetworkStream n) n.Socket.Shutdown(SocketShutdown.Send);
    }

    private static async Task Pump(Stream from, Stream to, Action ended, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            ended();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

/// <summary>Which process opened a loopback connection: its row in Windows' TCP table.</summary>
internal static class LoopbackOwner
{
    /// <summary>The process at <paramref name="client"/>'s end of a connection to <paramref name="server"/> (IPv4), or null.</summary>
    public static int? ProcessOf(IPEndPoint client, IPEndPoint server)
    {
        var size = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidConnections, 0);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return null;
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRow>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(buffer + 4 + i * rowSize);
                    if (Port(row.LocalPort) == client.Port && Port(row.RemotePort) == server.Port &&
                        new IPAddress(row.LocalAddr).Equals(client.Address.MapToIPv4()) && new IPAddress(row.RemoteAddr).Equals(server.Address.MapToIPv4()))
                        return (int)row.OwningPid;
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

    private const int AfInet = 2, TcpTableOwnerPidConnections = 4, ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

/// <summary>
/// The certificate a server presents, read through any stream (a jump host's): what the RDP control and WinRM can't check
/// themselves when they only see 127.0.0.1.
/// </summary>
public static class ServerCertificates
{
    /// <summary>
    /// A TLS handshake on <paramref name="stream"/>; nothing is sent after it. <c>Valid</c>: trusted by this PC and issued to
    /// <paramref name="host"/> – what a direct connection would accept without asking.
    /// </summary>
    public static async Task<(X509Certificate2 Certificate, bool Valid)> TlsAsync(Stream stream, string host, CancellationToken ct)
    {
        X509Certificate2? seen = null;
        var valid = false;
        await using var tls = new SslStream(stream, leaveInnerStreamOpen: true, (_, cert, _, errors) =>
        {
            if (cert is not null) seen = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
            valid = errors == SslPolicyErrors.None;
            return true; // only looked at
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host.Trim().Trim('[', ']') }, ct).ConfigureAwait(false);
        return (seen ?? throw new InvalidDataException("The computer didn't present a certificate."), valid);
    }

    /// <summary>
    /// RDP: the security negotiation (X.224), then TLS. Null when the server uses the old RDP security, which has no
    /// certificate to check.
    /// </summary>
    public static async Task<(X509Certificate2 Certificate, bool Valid)?> RdpAsync(Stream stream, string host, CancellationToken ct)
    {
        // TPKT + X.224 Connection Request + RDP_NEG_REQ asking for TLS or CredSSP (both start with TLS).
        byte[] request = [0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0, 0, 0, 0, 0, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = header[2] << 8 | header[3];
        if (header[0] != 3 || length < 11 || length > 1024) throw new InvalidDataException("No RDP server answered on this port.");
        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        if ((body[1] & 0xF0) != 0xD0) throw new InvalidDataException("No RDP server answered on this port.");
        if (body.Length < 15) return null; // no negotiation: old RDP security
        var selected = BitConverter.ToUInt32(body, 11);
        if (body[7] == 0x03)
        {
            if (selected == 2) return null; // SSL_NOT_ALLOWED_BY_SERVER: old RDP security only
            throw new InvalidDataException($"The RDP server refused the security negotiation (code {selected}).");
        }
        if (body[7] != 0x02 || selected == 0) return null;
        return await TlsAsync(stream, host, ct).ConfigureAwait(false);
    }

    /// <summary>What a trusted certificate is remembered by.</summary>
    public static string Pin(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));
}
