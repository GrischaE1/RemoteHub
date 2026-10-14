using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RemoteHub.Agent;

/// <summary>
/// The wire contract of <c>GET /v1/tunnel</c> (this file is compiled into both RemoteHub and the agent): the agent as a
/// jump host. After the WebSocket upgrade (HTTP Basic sign-in, right <c>Tunnel</c>) every binary message
/// is one frame – type (1), stream id (4, big-endian), payload – and one connection carries any number of TCP streams.
/// <para>
/// RemoteHub opens a stream with <see cref="TunnelMessage.Open"/>; the agent resolves the name, checks its policy, connects
/// and answers <see cref="TunnelMessage.Opened"/> or <see cref="TunnelMessage.Refused"/>. Then both sides send
/// <see cref="TunnelMessage.Data"/>, at most <see cref="Window"/> bytes ahead of the <see cref="TunnelMessage.Credit"/>
/// the other side returned for what it passed on – so one slow stream never stalls another.
/// <see cref="TunnelMessage.Close"/> ends the sender's direction (like a TCP half-close); <see cref="TunnelMessage.Reset"/>
/// ends the stream at once in both directions.
/// </para>
/// </summary>
public static class TunnelProtocol
{
    public const string Path = "/v1/tunnel";
    public const int HeaderSize = 5;
    /// <summary>Largest payload of a <see cref="TunnelMessage.Data"/> frame.</summary>
    public const int MaxData = 64 * 1024;
    /// <summary>Bytes a sender may have on the way per stream before the receiver returns credit.</summary>
    public const int Window = 256 * 1024;
    /// <summary>Largest frame accepted (a data frame; JSON frames are far smaller).</summary>
    public const int MaxFrame = HeaderSize + MaxData;

    public static byte[] Frame(TunnelMessage type, uint stream, ReadOnlySpan<byte> payload = default)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), stream);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    public static byte[] Json<T>(TunnelMessage type, uint stream, T value, JsonTypeInfo<T> info) =>
        Frame(type, stream, JsonSerializer.SerializeToUtf8Bytes(value, info));

    public static byte[] Credit(uint stream, int bytes)
    {
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(n, bytes);
        return Frame(TunnelMessage.Credit, stream, n);
    }

    /// <summary>Splits a received frame; false if it is too short to be one.</summary>
    public static bool TryRead(ReadOnlySpan<byte> frame, out TunnelMessage type, out uint stream)
    {
        type = 0;
        stream = 0;
        if (frame.Length < HeaderSize) return false;
        type = (TunnelMessage)frame[0];
        stream = BinaryPrimitives.ReadUInt32BigEndian(frame[1..]);
        return true;
    }

    /// <summary>The bytes a <see cref="TunnelMessage.Credit"/> payload returns (0 if it isn't one).</summary>
    public static int ReadCredit(ReadOnlySpan<byte> payload) => payload.Length == 4 ? BinaryPrimitives.ReadInt32BigEndian(payload) : 0;
}

public enum TunnelMessage : byte
{
    /// <summary>RemoteHub → agent: <see cref="TunnelOpen"/>.</summary>
    Open = 1,
    /// <summary>Agent → RemoteHub: <see cref="TunnelOpened"/>.</summary>
    Opened = 2,
    /// <summary>Agent → RemoteHub: <see cref="TunnelRefused"/>; the stream id is free again.</summary>
    Refused = 3,
    /// <summary>Both ways: bytes of the stream (at most <see cref="TunnelProtocol.MaxData"/>).</summary>
    Data = 4,
    /// <summary>Both ways: 4 bytes (big-endian) – the receiver passed this many bytes on; the sender may send that many more.</summary>
    Credit = 5,
    /// <summary>Both ways: the sender sends nothing more on this stream (it still receives).</summary>
    Close = 6,
    /// <summary>Both ways: the stream ends now in both directions (an error, or the other side gave up).</summary>
    Reset = 7,
}

/// <summary>
/// Open a TCP connection from the jump host to <paramref name="Host"/> (name or address) and port. <paramref name="Probe"/>:
/// only resolve and check it, and answer <see cref="TunnelMessage.Refused"/> with <see cref="TunnelRefusal.Probe"/> and the
/// jump host's own address towards it – nothing is connected (older agents ignore it and connect).
/// </summary>
public sealed record TunnelOpen(string Host, int Port, bool Probe = false);

/// <summary>Connected to <paramref name="Address"/>; <paramref name="Local"/> is the jump host's own address towards it.</summary>
public sealed record TunnelOpened(string Address, string? Local = null);

/// <summary>
/// Not connected. <paramref name="Reason"/>: see <see cref="TunnelRefusal"/>; <paramref name="Message"/> says it in words.
/// <paramref name="Local"/>: the jump host's own address towards the target, when its address was resolved and allowed.
/// </summary>
public sealed record TunnelRefused(string Reason, string Message, string? Local = null);

public static class TunnelRefusal
{
    /// <summary>The jump host's policy doesn't allow this target or port.</summary>
    public const string Policy = "policy";
    /// <summary>The name doesn't resolve on the jump host.</summary>
    public const string Dns = "dns";
    /// <summary>Nothing listens there, or no route from the jump host.</summary>
    public const string Unreachable = "unreachable";
    public const string Timeout = "timeout";
    /// <summary>Too many open streams on this tunnel.</summary>
    public const string Limit = "limit";
    /// <summary>The answer to a probe (<see cref="TunnelOpen.Probe"/>): allowed, not connected; <see cref="TunnelRefused.Local"/> says from where.</summary>
    public const string Probe = "probe";
}

[JsonSerializable(typeof(TunnelOpen))]
[JsonSerializable(typeof(TunnelOpened))]
[JsonSerializable(typeof(TunnelRefused))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class TunnelJson : JsonSerializerContext;
