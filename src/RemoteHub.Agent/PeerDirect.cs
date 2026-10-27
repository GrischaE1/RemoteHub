using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace RemoteHub.Agent;

/// <summary>How a candidate was found: an address of the side's own adapter, or its public address as STUN saw it.</summary>
public enum CandidateKind { Host, Srflx }

/// <summary>
/// One address and port a side of a broker session can be reached at, in its wire form
/// <c>host:192.0.2.10:51820</c> or <c>srflx:[2001:db8::7]:51820</c>.
/// </summary>
public sealed record PeerCandidate(CandidateKind Kind, IPEndPoint EndPoint)
{
    public const int MaxLength = 64;

    public override string ToString() =>
        $"{(Kind == CandidateKind.Host ? "host" : "srflx")}:{(EndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{EndPoint.Address}]" : EndPoint.Address.ToString())}:{EndPoint.Port}";

    /// <summary>The wire form, or null for anything else (another kind, a name instead of an address, port 0, too long).</summary>
    public static PeerCandidate? Parse(string? text)
    {
        if (text is null || text.Length > MaxLength) return null;
        var colon = text.IndexOf(':');
        if (colon < 0) return null;
        CandidateKind kind;
        switch (text[..colon])
        {
            case "host": kind = CandidateKind.Host; break;
            case "srflx": kind = CandidateKind.Srflx; break;
            default: return null; // "relay:…" is the UDP relay's, not used by this version
        }
        var rest = text[(colon + 1)..];
        var portAt = rest.LastIndexOf(':');
        if (portAt <= 0) return null;
        var host = rest[..portAt];
        if (host.StartsWith('[') != host.EndsWith(']')) return null;
        var v6 = host.StartsWith('[');
        if (v6) host = host[1..^1];
        if (!IPAddress.TryParse(host, out var address) || (address.AddressFamily == AddressFamily.InterNetworkV6) != v6 || (v6 && address.ScopeId != 0)) return null;
        if (!int.TryParse(rest[(portAt + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return null;
        return new PeerCandidate(kind, new IPEndPoint(address, port));
    }
}

/// <summary>
/// The direct path of a RemoteHub Broker session (this file is compiled into both RemoteHub and the agent): candidates,
/// hole punching with packets only the session's two sides can make, then QUIC on the punched ports with both ends
/// pinned – the agent's device certificate, and the console key named in the sealed offer. The relay runs at the same
/// time; whatever comes up here only replaces it as the way new streams go.
/// </summary>
public static class PeerDirect
{
    public const string Alpn = "remotehub-peer/1";
    public const string DefaultStun = "stun.cloudflare.com:3478";
    public const int PunchPacketSize = 44;
    public const int MaxPackets = 200;
    public static readonly TimeSpan PunchInterval = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan PunchTime = TimeSpan.FromSeconds(3);
    /// <summary>A LAN path answering this soon after an internet one is taken instead (it beats a hairpin through the NAT).</summary>
    public static readonly TimeSpan HostPreference = TimeSpan.FromMilliseconds(100);
    /// <summary>How long the agent keeps answering after the console's first punch, so the console gets its echo.</summary>
    public static readonly TimeSpan AgentLinger = TimeSpan.FromMilliseconds(300);
    /// <summary>From the handover until QUIC must be up: the console retries while the agent's listener isn't there yet.</summary>
    public static readonly TimeSpan ConnectTime = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);
    private static readonly byte[] Magic = "RHP1"u8.ToArray();

    /// <summary>msquic needs TLS 1.3 in Schannel: Windows 11 and Server 2022 or later. Elsewhere sessions stay relayed.</summary>
    public static bool Supported => QuicListener.IsSupported && QuicConnection.IsSupported;

    // ---- Addresses ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Addresses nothing is ever sent to, whatever a broker passes on: unspecified, loopback, link-local, multicast,
    /// broadcast and reserved – so a session can't be turned against the device's own services or its network.
    /// </summary>
    public static bool IsSpecial(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4];
            a.TryWriteBytes(b, out _);
            return b[0] is 0 or 127 or >= 224 || (b[0] == 169 && b[1] == 254);
        }
        return a.Equals(IPAddress.IPv6None) || a.Equals(IPAddress.IPv6Loopback) || a.IsIPv6LinkLocal || a.IsIPv6Multicast || a.IsIPv6SiteLocal
            || a.GetAddressBytes() is [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ..]; // IPv4-compatible and the rest of ::/96
    }

    /// <summary>
    /// The other side's candidates this side punches: valid and not special, at most
    /// <see cref="BrokerProtocol.MaxCandidates"/>; <paramref name="level"/> 1 (BrokerDirect) skips host candidates.
    /// </summary>
    public static List<PeerCandidate> Usable(IEnumerable<string?>? cands, int level)
    {
        var usable = new List<PeerCandidate>();
        if (level <= 0 || cands is null) return usable;
        foreach (var text in cands.Take(BrokerProtocol.MaxCandidates))
            if (PeerCandidate.Parse(text) is { } c && !IsSpecial(c.EndPoint.Address) && (level >= 2 || c.Kind == CandidateKind.Srflx)
                && !usable.Any(u => u.EndPoint.Equals(c.EndPoint)))
                usable.Add(c);
        return usable;
    }

    /// <summary>
    /// This PC's own addresses for host candidates: adapters that are up, IPv4 and global IPv6, without loopback and
    /// link-local. VPN adapters are kept – a VPN both sides are on is a direct path – and so are Hyper-V's switches (a
    /// lab host reaches its VMs over one), but adapters with a gateway come first and Hyper-V's internal ones last, so
    /// the cap of eight keeps the likely ones.
    /// </summary>
    public static List<IPAddress> HostAddresses()
    {
        var found = new List<(IPAddress Address, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var gateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                var rank = gateway ? 0 : nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                foreach (var u in props.UnicastAddresses)
                    if (u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !IsSpecial(u.Address))
                        found.Add((u.Address, rank));
            }
        }
        catch (NetworkInformationException) { /* no adapters to offer */ }
        return [.. found.OrderBy(f => f.Rank).Select(f => f.Address).Distinct()];
    }

    /// <summary>This side's candidates on <paramref name="port"/>: host ones (level 2 only) and the STUN answer, at most <see cref="BrokerProtocol.MaxCandidates"/>.</summary>
    public static List<string> OwnCandidates(int port, IPEndPoint? reflexive, int level)
    {
        var list = new List<string>();
        if (level <= 0) return list;
        if (reflexive is not null && !IsSpecial(reflexive.Address)) list.Add(new PeerCandidate(CandidateKind.Srflx, Normal(reflexive)).ToString());
        if (level >= 2)
            foreach (var a in HostAddresses())
            {
                var c = new PeerCandidate(CandidateKind.Host, new IPEndPoint(a, port)).ToString();
                if (c.Length <= PeerCandidate.MaxLength && !list.Contains(c)) list.Add(c);
            }
        return [.. list.Take(BrokerProtocol.MaxCandidates)];
    }

    /// <summary>
    /// The session socket: UDP on both IPv4 and IPv6, one port for STUN, punching and – after the handover – QUIC. ICMP
    /// "port unreachable" from a candidate that isn't there doesn't end its receives.
    /// </summary>
    public static Socket NewSocket()
    {
        var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        try
        {
            s.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null);
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    public static IPEndPoint Normal(IPEndPoint ep) => ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    private static IPEndPoint ToSocket(IPEndPoint ep) => ep.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;

    // ---- Punching -------------------------------------------------------------------------------------------------

    /// <summary><c>"RHP1" | session (16) | nonce (8) | HMAC-SHA256(key, the first 28 bytes) truncated to 16</c>.</summary>
    public static byte[] PunchPacket(ReadOnlySpan<byte> session, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> key)
    {
        if (session.Length != 16 || nonce.Length != 8) throw new ArgumentException("session 16 bytes, nonce 8 bytes");
        var p = new byte[PunchPacketSize];
        Magic.CopyTo(p, 0);
        session.CopyTo(p.AsSpan(4));
        nonce.CopyTo(p.AsSpan(20));
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, p.AsSpan(0, 28), mac);
        mac[..16].CopyTo(p.AsSpan(28));
        return p;
    }

    /// <summary>A punch of this session, made with its key – anything else is dropped without an answer.</summary>
    public static bool IsPunch(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> session, ReadOnlySpan<byte> key)
    {
        if (packet.Length != PunchPacketSize || !packet[..4].SequenceEqual(Magic) || !packet.Slice(4, 16).SequenceEqual(session)) return false;
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, packet[..28], mac);
        return CryptographicOperations.FixedTimeEquals(mac[..16], packet.Slice(28, 16));
    }

    /// <summary>What a punch round found: the other side's address that answered (console), or only that it got through (agent).</summary>
    public sealed record PunchResult(IPEndPoint Remote, bool Local, int Packets);

    /// <summary>
    /// One punch round on <paramref name="socket"/>: every <see cref="PunchInterval"/> a fresh punch to every candidate,
    /// for at most <see cref="PunchTime"/> and <see cref="MaxPackets"/> packets (echoes included); a valid punch from the
    /// other side is echoed to where it came from.
    /// <para>
    /// The console (<paramref name="agent"/> false) wins when one of its own punches comes back: that address works both
    /// ways. A host candidate answering within <see cref="HostPreference"/> beats an earlier internet one. The agent
    /// wins when a punch of the console's arrives, then goes on answering for <see cref="AgentLinger"/> so the console
    /// gets its echo. Null when nothing answered.
    /// </para>
    /// </summary>
    public static async Task<PunchResult?> PunchAsync(Socket socket, IReadOnlyList<PeerCandidate> remote, byte[] session, byte[] key, bool agent, CancellationToken ct)
    {
        if (remote.Count == 0) return null;
        using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
        round.CancelAfter(PunchTime);
        var mine = new HashSet<ulong>();
        var packets = 0;
        var sendLock = new SemaphoreSlim(1, 1);

        async Task<bool> SendTo(byte[] p, IPEndPoint to, CancellationToken token)
        {
            await sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (packets >= MaxPackets) return false;
                packets++;
                try { await socket.SendToAsync(p, SocketFlags.None, ToSocket(to), token).ConfigureAwait(false); }
                catch (SocketException) { /* no route to that one: the others go on */ }
                return true;
            }
            finally { sendLock.Release(); }
        }

        using var stopSending = CancellationTokenSource.CreateLinkedTokenSource(round.Token);
        var sender = Task.Run(async () =>
        {
            try
            {
                while (!stopSending.IsCancellationRequested)
                {
                    foreach (var c in remote)
                    {
                        var nonce = RandomNumberGenerator.GetBytes(8);
                        lock (mine) mine.Add(BinaryPrimitives.ReadUInt64LittleEndian(nonce));
                        if (!await SendTo(PunchPacket(session, nonce, key), c.EndPoint, stopSending.Token).ConfigureAwait(false)) return;
                    }
                    await Task.Delay(PunchInterval, stopSending.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        PunchResult? won = null;
        PunchResult? first = null;
        var buffer = new byte[2048];
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(round.Token);
        try
        {
            while (true)
            {
                SocketReceiveFromResult r;
                try { r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                var from = Normal((IPEndPoint)r.RemoteEndPoint);
                var packet = buffer.AsSpan(0, r.ReceivedBytes);
                if (!IsPunch(packet, session, key)) continue;
                bool ours;
                lock (mine) ours = mine.Contains(BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(20, 8)));
                var local = remote.Any(c => c.Kind == CandidateKind.Host && c.EndPoint.Address.Equals(from.Address));
                if (!ours)
                {
                    await SendTo(packet.ToArray(), from, round.Token).ConfigureAwait(false); // the echo
                    if (agent && first is null)
                    {
                        first = new PunchResult(from, local, 0);
                        deadline.CancelAfter(AgentLinger);
                    }
                    continue;
                }
                if (agent) continue; // the agent doesn't choose: it listens on its port for whatever the console picks
                if (local) { won = new PunchResult(from, true, 0); break; }
                if (first is null)
                {
                    first = new PunchResult(from, false, 0);
                    deadline.CancelAfter(HostPreference);
                }
            }
        }
        finally
        {
            stopSending.Cancel();
            await sender.ConfigureAwait(false);
            deadline.Dispose();
        }
        var result = won ?? first;
        return result is null ? null : result with { Packets = packets };
    }

    // ---- QUIC -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The agent's side: its device certificate, and only a client certificate on the console key the sealed offer
    /// named (<paramref name="consoleKeyHash"/>, the SHA-256 of its public key) – a stranger who finds the punched port
    /// gets no further than the handshake.
    /// </summary>
    public static QuicServerConnectionOptions ServerOptions(X509Certificate2 certificate, string consoleKeyHash, int maxStreams) => new()
    {
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 1,
        IdleTimeout = IdleTimeout,
        KeepAliveInterval = KeepAlive,
        MaxInboundBidirectionalStreams = maxStreams,
        MaxInboundUnidirectionalStreams = 0,
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(Alpn)],
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is X509Certificate2 x && IsConsoleKey(x, consoleKeyHash),
        },
    };

    /// <summary>RemoteHub's side: from its punched port, with its console certificate, pinned to the device's certificate.</summary>
    public static QuicClientConnectionOptions ClientOptions(IPEndPoint remote, int localPort, X509Certificate2 consoleCertificate, string fingerprint) => new()
    {
        RemoteEndPoint = remote,
        LocalEndPoint = new IPEndPoint(remote.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, localPort),
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 1,
        IdleTimeout = IdleTimeout,
        KeepAliveInterval = KeepAlive,
        MaxInboundBidirectionalStreams = 0,
        MaxInboundUnidirectionalStreams = 0,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(Alpn)],
            TargetHost = "remotehub-agent",
            ClientCertificates = [consoleCertificate],
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is X509Certificate2 x
                && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(AgentProtocol.Fingerprint(x.RawData)), System.Text.Encoding.ASCII.GetBytes(fingerprint)),
        },
    };

    public static bool IsConsoleKey(X509Certificate2 certificate, string consoleKeyHash) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(BrokerProtocol.KeyHash(certificate.PublicKey.ExportSubjectPublicKeyInfo())),
            System.Text.Encoding.ASCII.GetBytes(consoleKeyHash));

    /// <summary>
    /// The console's QUIC connection after the handover. msquic ends an attempt at once on "port unreachable" – the
    /// agent's listener isn't up yet – so it tries again from the same port every 100 ms until <see cref="ConnectTime"/>.
    /// A refused handshake (wrong certificate) isn't tried again.
    /// </summary>
    public static async Task<QuicConnection> ConnectAsync(QuicClientConnectionOptions options, CancellationToken ct)
    {
        var until = DateTime.UtcNow + ConnectTime;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(until - DateTime.UtcNow + TimeSpan.FromSeconds(1));
            try { return await QuicConnection.ConnectAsync(options, attempt.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or QuicException { QuicError: QuicError.ConnectionRefused }
                                       && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// STUN binding (RFC 8489, binding only): asks a STUN service which public address and port a packet from the session
/// socket came from. Nothing else of STUN is used.
/// </summary>
public static class Stun
{
    private const uint Cookie = 0x2112A442;
    private const ushort BindingRequestType = 0x0001, BindingSuccess = 0x0101, MappedAddress = 0x0001, XorMappedAddress = 0x0020;

    public static byte[] BindingRequest(ReadOnlySpan<byte> transaction)
    {
        if (transaction.Length != 12) throw new ArgumentException("the transaction id is 12 bytes");
        var p = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(p, BindingRequestType);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), Cookie);
        transaction.CopyTo(p.AsSpan(8));
        return p;
    }

    /// <summary>The address in a binding success for <paramref name="transaction"/>; null for anything else or anything malformed.</summary>
    public static IPEndPoint? ParseBindingResponse(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> transaction)
    {
        if (packet.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(packet) != BindingSuccess) return null;
        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (length % 4 != 0 || 20 + length > packet.Length || BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != Cookie || !packet.Slice(8, 12).SequenceEqual(transaction))
            return null;
        IPEndPoint? plain = null;
        var at = 20;
        var end = 20 + length;
        while (at + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
            if (at + 4 + size > end) return null;
            var value = packet.Slice(at + 4, size);
            if (type == XorMappedAddress) return Address(value, packet[4..20], xor: true);
            if (type == MappedAddress) plain ??= Address(value, default, xor: false);
            at += 4 + ((size + 3) & ~3);
        }
        return plain;
    }

    private static IPEndPoint? Address(ReadOnlySpan<byte> v, ReadOnlySpan<byte> cookieAndTransaction, bool xor)
    {
        if (v.Length < 4) return null;
        var family = v[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(v[2..]);
        var size = family switch { 1 => 4, 2 => 16, _ => 0 };
        if (size == 0 || v.Length != 4 + size) return null;
        Span<byte> address = stackalloc byte[size];
        v.Slice(4, size).CopyTo(address);
        if (xor)
        {
            port ^= (ushort)(Cookie >> 16);
            for (var i = 0; i < size; i++) address[i] ^= cookieAndTransaction[i];
        }
        return port == 0 ? null : new IPEndPoint(new IPAddress(address), port);
    }

    /// <summary>"host:port" (or "host", port 3478; "[v6]:port"); null when it isn't one.</summary>
    public static (string Host, int Port)? ParseServer(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 200) return null;
        string host;
        var port = 3478;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) return null;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !int.TryParse(rest[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port))) return null;
        }
        else
        {
            var colon = text.LastIndexOf(':');
            host = colon < 0 ? text : text[..colon];
            if (colon >= 0 && (host.Contains(':') || !int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port))) return null;
        }
        return host.Length == 0 || port is < 1 or > 65535 || Uri.CheckHostName(host) == UriHostNameType.Unknown ? null : (host, port);
    }

    /// <summary>
    /// The session socket's public address: up to three binding requests within <paramref name="timeout"/>, to the
    /// service's first IPv4 address (the one a NAT maps). Null when nothing answered – UDP may be blocked.
    /// </summary>
    public static async Task<IPEndPoint?> QueryAsync(Socket socket, string server, TimeSpan timeout, CancellationToken ct)
    {
        if (ParseServer(server) is not var (host, port)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            if ((addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()) is not { } address) return null;
            var to = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address, port);
            var transaction = RandomNumberGenerator.GetBytes(12);
            var request = BindingRequest(transaction);
            var buffer = new byte[1024];
            var resend = Task.Run(async () =>
            {
                for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
                {
                    await socket.SendToAsync(request, SocketFlags.None, to, cts.Token).ConfigureAwait(false);
                    await Task.Delay(timeout / 3, cts.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);
            try
            {
                while (true)
                {
                    var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.IPv6Any, 0), cts.Token).ConfigureAwait(false);
                    if (ParseBindingResponse(buffer.AsSpan(0, r.ReceivedBytes), transaction) is { } mapped) return PeerDirect.Normal(mapped);
                }
            }
            finally
            {
                cts.Cancel();
                try { await resend.ConfigureAwait(false); } catch { /* cancelled */ }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { return null; }
    }
}
