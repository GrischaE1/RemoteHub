using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteHub.Agent;

/// <summary>
/// H.264 encoder for the remote desktop (Windows' own Media Foundation encoder, low-latency mode, no B-frames). Takes
/// BGRA frames, returns Annex B access units. Software encoding, so it works the same on servers and VMs without a GPU.
/// Quality-based rate control spends bits only where the screen changes; key frames only on request.
/// Compiled into RemoteHub and the agent (tests encode and decode in one process).
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private IntPtr _mft, _codec;
    private readonly int _width, _height;
    private readonly long _frameDuration;
    private long _time;
    private int _outputSize;
    private bool _providesSamples;
    private readonly bool _started;

    public int Width => _width;
    public int Height => _height;
    public int Quality { get; private set; }

    /// <param name="width">Even number of pixels (the caller crops odd sizes).</param>
    public H264Encoder(int width, int height, int fps, int quality)
    {
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0 || width > 8192 || height > 8192)
            throw new ArgumentException($"Unsupported frame size {width} × {height}.");
        (_width, _height) = (width, height);
        fps = Math.Clamp(fps, 1, 60);
        _frameDuration = 10_000_000 / fps;
        Quality = Math.Clamp(quality, 1, 100);
        MediaFoundation.Startup();
        _started = true;
        IntPtr output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            _mft = MediaFoundation.Create(MediaFoundation.CLSID_MSH264EncoderMFT, "The H.264 video encoder is not available on this device (Windows N editions need the Media Feature Pack)");
            if (Marshal.QueryInterface(_mft, in MediaFoundation.IID_ICodecAPI, out _codec) < 0) _codec = IntPtr.Zero;
            // Before the media types: some values are fixed once the types are set.
            SetCodec(MediaFoundation.CODECAPI_AVLowLatencyMode, MediaFoundation.Variant.Bool(true));
            SetCodec(MediaFoundation.CODECAPI_AVEncCommonRateControlMode, MediaFoundation.Variant.UInt(3)); // quality-based VBR
            SetCodec(MediaFoundation.CODECAPI_AVEncCommonQuality, MediaFoundation.Variant.UInt((uint)Quality));
            SetCodec(MediaFoundation.CODECAPI_AVEncMPVDefaultBPictureCount, MediaFoundation.Variant.UInt(0));
            SetCodec(MediaFoundation.CODECAPI_AVEncMPVGOPSize, MediaFoundation.Variant.UInt((uint)(fps * 120))); // key frames on request

            output = MediaFoundation.VideoType(MediaFoundation.MFVideoFormat_H264, width, height, fps);
            MediaFoundation.SetUInt32(output, MediaFoundation.MF_MT_AVG_BITRATE, (uint)Math.Clamp((long)width * height * fps / 8, 500_000, 40_000_000));
            MediaFoundation.SetUInt32(output, MediaFoundation.MF_MT_MPEG2_PROFILE, 100); // High: 8×8 transform keeps text sharper
            MediaFoundation.Check(MediaFoundation.SetOutputType(_mft, output), "The H.264 encoder doesn't accept this frame size");
            input = MediaFoundation.VideoType(MediaFoundation.MFVideoFormat_NV12, width, height, fps);
            MediaFoundation.Check(MediaFoundation.SetInputType(_mft, input), "The H.264 encoder doesn't accept NV12 frames");

            MediaFoundation.OutputStreamInfo(_mft, out var flags, out _outputSize);
            _providesSamples = (flags & 0x300) != 0;
            if (_outputSize <= 0) _outputSize = width * height * 3 / 2;
            MediaFoundation.ProcessMessage(_mft, 0x10000000); // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
            MediaFoundation.ProcessMessage(_mft, 0x10000003); // MFT_MESSAGE_NOTIFY_START_OF_STREAM
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            MediaFoundation.Release(output);
            MediaFoundation.Release(input);
        }
    }

    /// <summary>Changes the quality without a new encoder, if the encoder allows it.</summary>
    public bool TrySetQuality(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        if (quality == Quality) return true;
        if (!SetCodec(MediaFoundation.CODECAPI_AVEncCommonQuality, MediaFoundation.Variant.UInt((uint)quality))) return false;
        Quality = quality;
        return true;
    }

    /// <summary>Encodes one BGRA frame (rows of <paramref name="pitch"/> bytes). Returns the access unit, or null if the encoder holds it back.</summary>
    public byte[]? Encode(byte* bgra, int pitch, bool keyFrame, out bool isKeyFrame)
    {
        isKeyFrame = false;
        if (keyFrame) SetCodec(MediaFoundation.CODECAPI_AVEncVideoForceKeyFrame, MediaFoundation.Variant.UInt(1));
        var length = _width * _height * 3 / 2;
        var sample = MediaFoundation.CreateSample(length, out var buffer);
        try
        {
            var target = MediaFoundation.Lock(buffer, out _);
            try { ColorConversion.BgraToNv12(bgra, pitch, _width, _height, target); }
            finally { MediaFoundation.Unlock(buffer); }
            MediaFoundation.SetLength(buffer, length);
            MediaFoundation.SetTimes(sample, _time, _frameDuration);
            _time += _frameDuration;
            MediaFoundation.Check(MediaFoundation.ProcessInput(_mft, sample), "Encoding failed");
        }
        finally
        {
            MediaFoundation.Release(buffer);
            MediaFoundation.Release(sample);
        }

        byte[]? result = null;
        while (true)
        {
            var hr = MediaFoundation.ProcessOutput(_mft, _providesSamples, _outputSize, out var output);
            if (hr == MediaFoundation.MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            MediaFoundation.Check(hr, "Encoding failed");
            try
            {
                var bytes = MediaFoundation.ReadSample(output);
                result = result is null ? bytes : [.. result, .. bytes];
            }
            finally { MediaFoundation.Release(output); }
        }
        if (result is not null) isKeyFrame = H264.ContainsIdr(result);
        return result;
    }

    private bool SetCodec(Guid key, MediaFoundation.Variant value) =>
        _codec != IntPtr.Zero && MediaFoundation.SetCodecValue(_codec, key, value) >= 0;

    public void Dispose()
    {
        MediaFoundation.Release(_codec);
        MediaFoundation.Release(_mft);
        _codec = _mft = IntPtr.Zero;
        if (_started) MediaFoundation.Shutdown();
    }
}

/// <summary>H.264 decoder (Media Foundation, low-latency): Annex B access units in, BGRA frames out.</summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private IntPtr _mft;
    private readonly bool _started;
    private bool _typeSet, _providesSamples;
    private int _outputSize, _planeWidth, _planeHeight, _stride;
    private byte[] _bgra = [];
    private long _time;

    public H264Decoder()
    {
        MediaFoundation.Startup();
        _started = true;
        IntPtr input = IntPtr.Zero;
        try
        {
            _mft = MediaFoundation.Create(MediaFoundation.CLSID_MSH264DecoderMFT, "The H.264 video decoder is not available on this PC (Windows N editions need the Media Feature Pack)");
            // Low latency (the decoder's attribute, not ICodecAPI): every access unit comes out as a picture at once.
            if (MediaFoundation.GetAttributes(_mft, out var attrs) >= 0)
            {
                try { MediaFoundation.SetUInt32(attrs, MediaFoundation.CODECAPI_AVLowLatencyMode, 1); }
                finally { MediaFoundation.Release(attrs); }
            }
            MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out input), "MFCreateMediaType");
            MediaFoundation.SetGuid(input, MediaFoundation.MF_MT_MAJOR_TYPE, MediaFoundation.MFMediaType_Video);
            MediaFoundation.SetGuid(input, MediaFoundation.MF_MT_SUBTYPE, MediaFoundation.MFVideoFormat_H264);
            MediaFoundation.Check(MediaFoundation.SetInputType(_mft, input), "The H.264 decoder doesn't accept the stream");
            SelectOutput();
            MediaFoundation.ProcessMessage(_mft, 0x10000000);
            MediaFoundation.ProcessMessage(_mft, 0x10000003);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally { MediaFoundation.Release(input); }
    }

    /// <summary>
    /// Decodes one access unit; for every finished picture <paramref name="frame"/> gets BGRA pixels (top-down, pitch =
    /// width × 4) cropped to <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> data, int width, int height, FrameCallback frame)
    {
        var sample = MediaFoundation.CreateSample(data.Length, out var buffer);
        try
        {
            var target = MediaFoundation.Lock(buffer, out _);
            try { data.CopyTo(new Span<byte>(target, data.Length)); }
            finally { MediaFoundation.Unlock(buffer); }
            MediaFoundation.SetLength(buffer, data.Length);
            MediaFoundation.SetTimes(sample, _time, 333_333);
            _time += 333_333;
            var hr = MediaFoundation.ProcessInput(_mft, sample);
            if (hr == MediaFoundation.MF_E_NOTACCEPTING)
            {
                Drain(width, height, frame);
                hr = MediaFoundation.ProcessInput(_mft, sample);
            }
            MediaFoundation.Check(hr, "Decoding failed");
        }
        finally
        {
            MediaFoundation.Release(buffer);
            MediaFoundation.Release(sample);
        }
        Drain(width, height, frame);
    }

    public delegate void FrameCallback(byte* bgra, int width, int height);

    /// <summary>Forgets buffered data (after a lost frame; the next key frame starts clean).</summary>
    public void Flush() => MediaFoundation.ProcessMessage(_mft, 0); // MFT_MESSAGE_COMMAND_FLUSH

    private void Drain(int width, int height, FrameCallback frame)
    {
        while (true)
        {
            var hr = MediaFoundation.ProcessOutput(_mft, _providesSamples, _outputSize, out var output);
            if (hr == MediaFoundation.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr == MediaFoundation.MF_E_TRANSFORM_STREAM_CHANGE || hr == MediaFoundation.MF_E_TRANSFORM_TYPE_NOT_SET)
            {
                MediaFoundation.Release(output);
                SelectOutput();
                continue;
            }
            MediaFoundation.Check(hr, "Decoding failed");
            try
            {
                MediaFoundation.Check(MediaFoundation.ConvertToContiguous(output, out var contiguous), "ConvertToContiguousBuffer");
                try
                {
                    var nv12 = MediaFoundation.Lock(contiguous, out var length);
                    try
                    {
                        int w = Math.Min(width, _planeWidth), h = Math.Min(height, _planeHeight);
                        var stride = _stride > 0 ? _stride : _planeWidth;
                        if (w <= 0 || h <= 0 || (long)stride * _planeHeight * 3 / 2 > length) continue;
                        if (_bgra.Length < w * h * 4) _bgra = new byte[w * h * 4];
                        fixed (byte* bgra = _bgra)
                        {
                            ColorConversion.Nv12ToBgra(nv12, stride, nv12 + (long)stride * _planeHeight, w, h, bgra);
                            frame(bgra, w, h);
                        }
                    }
                    finally { MediaFoundation.Unlock(contiguous); }
                }
                finally { MediaFoundation.Release(contiguous); }
            }
            finally { MediaFoundation.Release(output); }
        }
    }

    private void SelectOutput()
    {
        for (var i = 0; ; i++)
        {
            if (MediaFoundation.GetOutputAvailableType(_mft, i, out var type) < 0) break;
            try
            {
                if (MediaFoundation.GetGuid(type, MediaFoundation.MF_MT_SUBTYPE) != MediaFoundation.MFVideoFormat_NV12) continue;
                MediaFoundation.Check(MediaFoundation.SetOutputType(_mft, type), "SetOutputType");
                _typeSet = true;
                (_planeWidth, _planeHeight) = MediaFoundation.GetSize(type);
                _stride = MediaFoundation.GetUInt32(type, MediaFoundation.MF_MT_DEFAULT_STRIDE) is { } s and > 0 and < 65536 ? (int)s : 0;
                MediaFoundation.OutputStreamInfo(_mft, out var flags, out _outputSize);
                _providesSamples = (flags & 0x300) != 0;
                if (_outputSize <= 0) _outputSize = Math.Max(1, _planeWidth * _planeHeight * 3 / 2);
                return;
            }
            finally { MediaFoundation.Release(type); }
        }
        if (!_typeSet) throw new InvalidOperationException("The H.264 decoder offers no NV12 output.");
    }

    public void Dispose()
    {
        MediaFoundation.Release(_mft);
        _mft = IntPtr.Zero;
        if (_started) MediaFoundation.Shutdown();
    }
}

/// <summary>BGRA ↔ NV12 (BT.709, full range – both ends are ours, so no studio-range squeeze that blurs text).</summary>
public static unsafe class ColorConversion
{
    public static void BgraToNv12(byte* bgra, int pitch, int width, int height, byte* nv12)
    {
        long src = (long)bgra, dst = (long)nv12;
        Parallel.For(0, height / 2, pair =>
        {
            var y0 = pair * 2;
            var row0 = (byte*)(src + (long)y0 * pitch);
            var row1 = row0 + pitch;
            var luma0 = (byte*)(dst + (long)y0 * width);
            var luma1 = luma0 + width;
            var chroma = (byte*)(dst + (long)width * height + (long)pair * width);
            for (var x = 0; x < width; x += 2)
            {
                int b0 = row0[x * 4], g0 = row0[x * 4 + 1], r0 = row0[x * 4 + 2];
                int b1 = row0[x * 4 + 4], g1 = row0[x * 4 + 5], r1 = row0[x * 4 + 6];
                int b2 = row1[x * 4], g2 = row1[x * 4 + 1], r2 = row1[x * 4 + 2];
                int b3 = row1[x * 4 + 4], g3 = row1[x * 4 + 5], r3 = row1[x * 4 + 6];
                luma0[x] = (byte)((54 * r0 + 183 * g0 + 19 * b0 + 128) >> 8);
                luma0[x + 1] = (byte)((54 * r1 + 183 * g1 + 19 * b1 + 128) >> 8);
                luma1[x] = (byte)((54 * r2 + 183 * g2 + 19 * b2 + 128) >> 8);
                luma1[x + 1] = (byte)((54 * r3 + 183 * g3 + 19 * b3 + 128) >> 8);
                int r = r0 + r1 + r2 + r3, g = g0 + g1 + g2 + g3, b = b0 + b1 + b2 + b3;
                chroma[x] = Clamp(((-29 * r - 99 * g + 128 * b + 512) >> 10) + 128);
                chroma[x + 1] = Clamp(((128 * r - 116 * g - 12 * b + 512) >> 10) + 128);
            }
        });
    }

    public static void Nv12ToBgra(byte* luma, int stride, byte* chroma, int width, int height, byte* bgra)
    {
        long l = (long)luma, c = (long)chroma, d = (long)bgra;
        Parallel.For(0, height, y =>
        {
            var yRow = (byte*)(l + (long)y * stride);
            var cRow = (byte*)(c + (long)(y / 2) * stride);
            var o = (byte*)(d + (long)y * width * 4);
            for (var x = 0; x < width; x++)
            {
                int yy = yRow[x] << 8, u = cRow[x & ~1] - 128, v = cRow[(x & ~1) + 1] - 128;
                o[0] = Clamp((yy + 475 * u + 128) >> 8);
                o[1] = Clamp((yy - 48 * u - 120 * v + 128) >> 8);
                o[2] = Clamp((yy + 403 * v + 128) >> 8);
                o[3] = 255;
                o += 4;
            }
        });
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}

public static class H264
{
    /// <summary>True if the access unit contains an IDR picture (NAL type 5) – the viewer can start from it.</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int start;
            if (data[i + 2] == 1) start = i + 3;
            else if (data[i + 2] == 0 && i + 4 < data.Length && data[i + 3] == 1) start = i + 4;
            else continue;
            if (start < data.Length && (data[start] & 0x1F) == 5) return true;
            i = start - 1;
        }
        return false;
    }
}

/// <summary>Media Foundation through its COM vtables (no interop assemblies; the agent is published trimmed).</summary>
internal static unsafe class MediaFoundation
{
    public static readonly Guid CLSID_MSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    public static readonly Guid CLSID_MSH264DecoderMFT = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    public static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60);
    public const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public short boolVal;

        public static Variant UInt(uint v) => new() { vt = 19, uintVal = v };      // VT_UI4
        public static Variant Bool(bool v) => new() { vt = 11, boolVal = (short)(v ? -1 : 0) }; // VT_BOOL
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }

    private static int _startups;
    private static readonly Lock StartupLock = new();

    public static void Startup()
    {
        lock (StartupLock)
        {
            _ = CoInitializeEx(IntPtr.Zero, 0); // MTA where possible; S_FALSE / RPC_E_CHANGED_MODE are fine
            Check(MFStartup(0x20070, 0), "Media Foundation is not available on this device");
            _startups++;
        }
    }

    public static void Shutdown()
    {
        lock (StartupLock)
        {
            if (_startups == 0) return;
            _startups--;
            MFShutdown();
        }
    }

    public static IntPtr Create(Guid clsid, string error)
    {
        var iid = IID_IMFTransform;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out var mft), error);
        return mft;
    }

    public static IntPtr VideoType(Guid subtype, int width, int height, int fps)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
        SetGuid(type, MF_MT_SUBTYPE, subtype);
        SetUInt32(type, MF_MT_INTERLACE_MODE, 2); // progressive
        SetUInt64(type, MF_MT_FRAME_SIZE, Pack(width, height));
        SetUInt64(type, MF_MT_FRAME_RATE, Pack(fps, 1));
        SetUInt64(type, MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        return type;
    }

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    // IMFAttributes
    public static uint? GetUInt32(IntPtr attrs, Guid key)
    {
        uint v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(attrs, 7))(attrs, &key, &v) >= 0 ? v : null;
    }

    public static (int Width, int Height) GetSize(IntPtr attrs)
    {
        var key = MF_MT_FRAME_SIZE;
        ulong v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(attrs, 8))(attrs, &key, &v) >= 0 ? ((int)(v >> 32), (int)(v & 0xFFFFFFFF)) : (0, 0);
    }

    public static Guid GetGuid(IntPtr attrs, Guid key)
    {
        Guid v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 10))(attrs, &key, &v) >= 0 ? v : Guid.Empty;
    }

    public static void SetUInt32(IntPtr attrs, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, value), "SetUINT32");

    public static void SetUInt64(IntPtr attrs, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(attrs, 22))(attrs, &key, value), "SetUINT64");

    public static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value), "SetGUID");

    // ICodecAPI::SetValue = 9
    public static int SetCodecValue(IntPtr codec, Guid key, Variant value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Slot(codec, 9))(codec, &key, &value);

    // IMFTransform
    public static int GetAttributes(IntPtr mft, out IntPtr attributes)
    {
        IntPtr a;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(mft, 8))(mft, &a);
        attributes = hr >= 0 ? a : IntPtr.Zero;
        return hr;
    }

    public static void OutputStreamInfo(IntPtr mft, out uint flags, out int size)
    {
        var info = stackalloc uint[3];
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)Slot(mft, 7))(mft, 0, info), "GetOutputStreamInfo");
        flags = info[0];
        size = (int)info[1];
    }

    public static int GetOutputAvailableType(IntPtr mft, int index, out IntPtr type)
    {
        IntPtr t;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Slot(mft, 14))(mft, 0, (uint)index, &t);
        type = hr >= 0 ? t : IntPtr.Zero;
        return hr;
    }

    public static int SetInputType(IntPtr mft, IntPtr type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(mft, 15))(mft, 0, type, 0);
    public static int SetOutputType(IntPtr mft, IntPtr type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(mft, 16))(mft, 0, type, 0);

    public static void ProcessMessage(IntPtr mft, uint message) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)Slot(mft, 23))(mft, message, 0);

    public static int ProcessInput(IntPtr mft, IntPtr sample) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Slot(mft, 24))(mft, 0, sample, 0);

    /// <summary>One output sample (the caller releases it), or an error / NEED_MORE_INPUT.</summary>
    public static int ProcessOutput(IntPtr mft, bool providesSamples, int size, out IntPtr sample)
    {
        IntPtr own = IntPtr.Zero, buffer = IntPtr.Zero;
        if (!providesSamples) own = CreateSample(size, out buffer);
        MediaFoundation.Release(buffer);
        var data = new OutputDataBuffer { StreamId = 0, Sample = own };
        uint status;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Slot(mft, 25))(mft, 0, 1, &data, &status);
        Release(data.Events);
        if (hr < 0)
        {
            if (data.Sample != own) Release(data.Sample);
            Release(own);
            sample = IntPtr.Zero;
            return hr;
        }
        sample = data.Sample;
        if (own != IntPtr.Zero && own != data.Sample) Release(own);
        return hr;
    }

    // Samples and buffers
    public static IntPtr CreateSample(int length, out IntPtr buffer)
    {
        Check(MFCreateMemoryBuffer((uint)Math.Max(1, length), out buffer), "MFCreateMemoryBuffer");
        Check(MFCreateSample(out var sample), "MFCreateSample");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, buffer), "AddBuffer");
        return sample;
    }

    public static void SetTimes(IntPtr sample, long time, long duration)
    {
        Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 36))(sample, time), "SetSampleTime");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 38))(sample, duration), "SetSampleDuration");
    }

    public static int ConvertToContiguous(IntPtr sample, out IntPtr buffer)
    {
        IntPtr b;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &b);
        buffer = hr >= 0 ? b : IntPtr.Zero;
        return hr;
    }

    public static byte[] ReadSample(IntPtr sample)
    {
        Check(ConvertToContiguous(sample, out var buffer), "ConvertToContiguousBuffer");
        try
        {
            var p = Lock(buffer, out var length);
            try { return new ReadOnlySpan<byte>(p, length).ToArray(); }
            finally { Unlock(buffer); }
        }
        finally { Release(buffer); }
    }

    /// <summary>Locks a media buffer; <paramref name="length"/> is its current length.</summary>
    public static byte* Lock(IntPtr buffer, out int length)
    {
        byte* p; uint max, current;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &p, &max, &current), "Lock");
        length = (int)current;
        return p;
    }

    public static void Unlock(IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);

    public static void SetLength(IntPtr buffer, int length) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 6))(buffer, (uint)length), "SetCurrentLength");

    public static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero) Marshal.Release(com);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} (0x{hr:X8}).");
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, out IntPtr obj);
    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
}
