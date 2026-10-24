using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace RemoteHub.Remoting.Decoder;

/// <summary>What a message between RemoteHub and its decoder process is (decoder-isolation-design.md §3.3).</summary>
public enum DecoderMessage : byte
{
    /// <summary>Decoder → RemoteHub, once at the start: the section is mapped, requests can come.</summary>
    Ready = 1,
    /// <summary>frameId u32 · width u16 · height u16 · key frame u8 · the H.264 access unit.</summary>
    Video = 2,
    /// <summary>frameId u32 · buffer u8 · width u16 · height u16: the picture is in that buffer of the section.</summary>
    Frame = 3,
    /// <summary>frameId u32: decoded, no picture out yet.</summary>
    NoFrame = 4,
    /// <summary>id u32 · section handle u64 (duplicated into the decoder) · width u16 · height u16 · the image file.</summary>
    Image = 5,
    /// <summary>id u32 · width u16 · height u16: the pixels are in the image's section.</summary>
    ImageDone = 6,
    /// <summary>id u32 · the reason (UTF-8): the input couldn't be decoded; the decoder is still fine.</summary>
    Failed = 7,
    /// <summary>Tries what the sandbox must prevent (decoder-isolation-design.md §6); answered with <see cref="ProbeResult"/>.</summary>
    Probe = 8,
    /// <summary>The probes' results as JSON (UTF-8).</summary>
    ProbeResult = 9,
    /// <summary>The decoder ends.</summary>
    Exit = 10,
}

/// <summary>
/// The messages between RemoteHub and <c>RemoteHubDecoder.exe</c>, over two anonymous pipes: a 4-byte length, then the
/// message. Pixels don't go through the pipes but through a shared section – two video buffers made once, and one
/// section per image. Compiled into both programs. Neither side trusts the other's lengths.
/// </summary>
public static class DecoderProtocol
{
    /// <summary>The largest video picture: the agent desktop protocol's limit (DesktopProtocol.MaxVideoWidth/Height).</summary>
    public const int MaxVideoWidth = 4096, MaxVideoHeight = 2304;

    /// <summary>One video buffer in the section; the section holds two.</summary>
    public const int VideoBufferBytes = MaxVideoWidth * MaxVideoHeight * 4;

    /// <summary>The largest request: a clipboard image may be up to the clipboard's 100 MB.</summary>
    public const int MaxRequestBytes = 100 * 1024 * 1024 + 64;

    /// <summary>The largest answer (probe results are the longest).</summary>
    public const int MaxAnswerBytes = 64 * 1024;

    public static void Write(Stream s, ReadOnlySpan<byte> message)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        s.Write(length);
        s.Write(message);
        s.Flush();
    }

    /// <summary>The next message, or null at the end of the stream. A length above <paramref name="max"/> is an error.</summary>
    public static byte[]? Read(Stream s, int max)
    {
        Span<byte> length = stackalloc byte[4];
        if (s.ReadAtLeast(length, 4, throwOnEndOfStream: false) < 4) return null;
        var n = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (n < 1 || n > max) throw new InvalidDataException($"Invalid decoder message length {n}.");
        var m = new byte[n];
        s.ReadExactly(m);
        return m;
    }

    public static byte[] Video(uint frameId, int width, int height, bool keyFrame, ReadOnlySpan<byte> accessUnit)
    {
        var m = new byte[10 + accessUnit.Length];
        m[0] = (byte)DecoderMessage.Video;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), frameId);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(5), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(7), (ushort)height);
        m[9] = keyFrame ? (byte)1 : (byte)0;
        accessUnit.CopyTo(m.AsSpan(10));
        return m;
    }

    public static byte[] Frame(uint frameId, int buffer, int width, int height)
    {
        var m = new byte[10];
        m[0] = (byte)DecoderMessage.Frame;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), frameId);
        m[5] = (byte)buffer;
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(8), (ushort)height);
        return m;
    }

    public static byte[] Image(uint id, ulong section, int width, int height, ReadOnlySpan<byte> file)
    {
        var m = new byte[17 + file.Length];
        m[0] = (byte)DecoderMessage.Image;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        BinaryPrimitives.WriteUInt64LittleEndian(m.AsSpan(5), section);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(13), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(15), (ushort)height);
        file.CopyTo(m.AsSpan(17));
        return m;
    }

    public static byte[] ImageDone(uint id, int width, int height)
    {
        var m = new byte[9];
        m[0] = (byte)DecoderMessage.ImageDone;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(5), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(7), (ushort)height);
        return m;
    }

    public static byte[] WithId(DecoderMessage kind, uint id, string? text = null)
    {
        var bytes = text is null ? [] : Encoding.UTF8.GetBytes(text.Length > 500 ? text[..500] : text);
        var m = new byte[5 + bytes.Length];
        m[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), id);
        bytes.CopyTo(m, 5);
        return m;
    }

    public static byte[] Text(DecoderMessage kind, string text) => [(byte)kind, .. Encoding.UTF8.GetBytes(text)];

    public static byte[] Bare(DecoderMessage kind) => [(byte)kind];
}
