using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteHub.Services;

/// <summary>
/// What a recording captures right now: a rectangle of a top-level window (in pixels from the window's top-left corner),
/// a rectangle of the screen (RDP in full screen), or nothing (tab hidden, window minimized).
/// Computed on the UI thread; read by the recorder thread.
/// </summary>
public readonly record struct RecordingSource(IntPtr Window, int X, int Y, int Width, int Height, bool Screen)
{
    public static readonly RecordingSource Hidden = default;
    public bool IsHidden => Width <= 0 || Height <= 0 || (!Screen && Window == IntPtr.Zero);
}

/// <summary>
/// Records one tab to an MP4 file (H.264, no audio) with Windows' own encoder – nothing to install. Frame rate and size
/// come from the settings (<see cref="RecordingOptions"/>); when capturing can't keep up, frames are skipped and the
/// video stays in sync.
/// Captures the window content (also when other windows cover it), draws the mouse pointer and a time stamp, and shows
/// a placeholder while the tab isn't visible. The size is fixed when recording starts; later size changes are scaled
/// to fit. Capture and encoding run on a background thread.
/// </summary>
public sealed class SessionRecorder
{
    internal const int MaxWidth = 3840, MaxHeight = 2160;

    private readonly object _gate = new();
    private RecordingSource _source;
    private volatile bool _stop;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SessionRecorder(string path, RecordingSource initial, RecordingOptions? options = null)
    {
        Path = path;
        _source = initial;
        Options = options ?? RecordingOptions.Default;
        (Width, Height) = OutputSize(initial.IsHidden ? 1280 : initial.Width, initial.IsHidden ? 720 : initial.Height, Options.Resolution);
    }

    public RecordingOptions Options { get; }
    /// <summary>Frames captured and encoded so far (fewer than elapsed × fps when the PC couldn't keep up).</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    private long _framesWritten;
    public int FramesPerSecond => Options.FramesPerSecond;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public DateTime StartedUtc { get; private set; }
    public TimeSpan Elapsed => StartedUtc == default ? TimeSpan.Zero : DateTime.UtcNow - StartedUtc;
    /// <summary>Completes when the file is closed: after <see cref="Stop"/>, or with an exception when recording failed.</summary>
    public Task Completion => _done.Task;

    public void Update(RecordingSource source) { lock (_gate) _source = source; }

    private RecordingSource Current { get { lock (_gate) return _source; } }

    public void Start()
    {
        StartedUtc = DateTime.UtcNow;
        // High frame rates (presentations) get normal priority, so fewer frames are skipped.
        new Thread(Run) { IsBackground = true, Name = "Session recorder", Priority = FramesPerSecond >= 30 ? ThreadPriority.Normal : ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Asks the recorder to finish the file; await <see cref="Completion"/> for the result.</summary>
    public void Stop()
    {
        Interlocked.CompareExchange(ref _stopTimestamp, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        _stop = true;
    }

    /// <summary>When <see cref="Stop"/> was called (Stopwatch timestamp): the video ends there, even if a slow frame delays the stop.</summary>
    private long _stopTimestamp;

    /// <summary>
    /// Video size: Full HD / 4K as chosen (the tab is scaled to fit), or the tab's own size – even dimensions (H.264),
    /// at most <see cref="MaxWidth"/> × <see cref="MaxHeight"/>, same aspect ratio.
    /// </summary>
    internal static (int Width, int Height) OutputSize(int width, int height, RecordingResolution resolution = RecordingResolution.Tab)
    {
        if (resolution == RecordingResolution.FullHd) return (1920, 1080);
        if (resolution == RecordingResolution.Uhd4K) return (3840, 2160);
        width = Math.Max(width, 64);
        height = Math.Max(height, 64);
        var scale = Math.Min(1.0, Math.Min((double)MaxWidth / width, (double)MaxHeight / height));
        return ((int)(width * scale) & ~1, (int)(height * scale) & ~1);
    }

    /// <summary>Target rectangle for a source of the given size: scaled to fit, centred.</summary>
    internal static (int X, int Y, int Width, int Height) Fit(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return (0, 0, dstW, dstH);
        var scale = Math.Min((double)dstW / srcW, (double)dstH / srcH);
        int w = Math.Max(1, (int)Math.Round(srcW * scale)), h = Math.Max(1, (int)Math.Round(srcH * scale));
        return ((dstW - w) / 2, (dstH - h) / 2, w, h);
    }

    /// <summary>Nominal bit rate (the media type needs one; with quality-based encoding it is only a hint).</summary>
    internal static int BitRate(int width, int height, int fps) => (int)Math.Clamp((long)width * height * fps / 10, 1_000_000, 40_000_000);

    private unsafe void Run()
    {
        try
        {
            using var frame = new FrameGrabber(Width, Height, gpu: Options.UseGpu);
            using var writer = Mp4Writer.Create(Path, Width, Height, FramesPerSecond, BitRate(Width, Height, FramesPerSecond));
            var interval = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var clockStart = System.Diagnostics.Stopwatch.GetTimestamp();
            long last = -1;
            while (true)
            {
                var stopping = _stop;
                var index = (long)(clock.Elapsed / interval);
                // The last frame goes where Stop was pressed, not where this loop noticed it.
                if (stopping && Interlocked.Read(ref _stopTimestamp) is var stopAt and > 0)
                    index = Math.Min(index, (long)(System.Diagnostics.Stopwatch.GetElapsedTime(clockStart, Math.Max(clockStart, stopAt)) / interval));
                // Stopping: one last frame only if the stop time isn't covered yet (else the video gets a frame too many);
                // a recording without any frame gets one.
                if (index > last || (stopping && last < 0))
                {
                    if (index <= last) index = last + 1;
                    frame.Capture(Current, DateTime.Now);
                    writer.Write(frame.Bits, index * interval.Ticks, interval.Ticks);
                    Interlocked.Increment(ref _framesWritten);
                    last = index;
                }
                if (stopping) break;
                var next = interval * (last + 1) - clock.Elapsed;
                if (next > TimeSpan.Zero) Thread.Sleep(next);
            }
            writer.Finish();
            _done.TrySetResult();
        }
        catch (Exception ex)
        {
            DiagLog.Write("Recording failed: " + ex.Message);
            _done.TrySetException(ex);
        }
    }
}

/// <summary>
/// Draws one frame into a top-down 32-bit bitmap of the output size: through the GPU (<see cref="GpuCapture"/>) when
/// available, otherwise with GDI (PrintWindow / screen copy – fine for low frame rates).
/// </summary>
internal sealed unsafe class FrameGrabber : IDisposable
{
    private readonly int _w, _h;
    private GpuCapture? _gpu;
    private (int X, int Y, int Width, int Height) _lastGpuRect;
    private readonly IntPtr _dc, _bitmap, _oldBitmap, _font, _oldFont;
    private IntPtr _windowDc, _windowBitmap, _oldWindowBitmap;
    private int _windowW, _windowH;
    private bool _placeholderShown;

    public FrameGrabber(int width, int height, bool gpu = false)
    {
        _w = width;
        _h = height;
        if (gpu) _gpu = GpuCapture.TryCreate();
        _dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        _bitmap = Gdi.CreateTopDownDib(_dc, width, height, out var bits);
        Bits = (byte*)bits;
        _oldBitmap = Gdi.SelectObject(_dc, _bitmap);
        _font = Gdi.CreateFontW(-Math.Max(12, height / 60), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _oldFont = Gdi.SelectObject(_dc, _font);
        Gdi.SetBkMode(_dc, 1);
        Gdi.SetStretchBltMode(_dc, 4); // HALFTONE
        Gdi.SetBrushOrgEx(_dc, 0, 0, IntPtr.Zero);
    }

    public byte* Bits { get; }
    public bool UsesGpu => _gpu is not null;

    public void Capture(RecordingSource source, DateTime now)
    {
        if (source.IsHidden || (!source.Screen && (!Gdi.IsWindow(source.Window) || Gdi.IsIconic(source.Window))))
        {
            if (!_placeholderShown) Placeholder();
            _placeholderShown = true;
            _lastGpuRect = default;
            return;
        }
        if (_gpu is not null)
        {
            try
            {
                CaptureGpu(source);
                _placeholderShown = false;
                Stamp(now.ToString("yyyy-MM-dd HH:mm:ss"));
                Gdi.GdiFlush();
                return;
            }
            catch (Exception ex)
            {
                DiagLog.Write("GPU capture failed, using GDI: " + ex.Message);
                _gpu.Dispose();
                _gpu = null;
            }
        }
        _placeholderShown = false;
        var dst = SessionRecorder.Fit(source.Width, source.Height, _w, _h);
        if (dst.Width != _w || dst.Height != _h) Fill(0, 0, _w, _h, 0x000000);

        int originX, originY;
        if (source.Screen)
        {
            var screen = Gdi.GetDC(IntPtr.Zero);
            try { Copy(screen, source.X, source.Y, source.Width, source.Height, dst); }
            finally { Gdi.ReleaseDC(IntPtr.Zero, screen); }
            (originX, originY) = (source.X, source.Y);
        }
        else
        {
            if (!Gdi.GetWindowRect(source.Window, out var r)) return;
            EnsureWindowBitmap(r.Right - r.Left, r.Bottom - r.Top);
            // PW_RENDERFULLCONTENT: the composed content (also the remote desktop control and when covered by other windows).
            if (!Gdi.PrintWindow(source.Window, _windowDc, 2)) return;
            Copy(_windowDc, source.X, source.Y, source.Width, source.Height, dst);
            (originX, originY) = (r.Left + source.X, r.Top + source.Y);
        }
        DrawCursor(source, originX, originY, dst);
        Stamp(now.ToString("yyyy-MM-dd HH:mm:ss"));
        Gdi.GdiFlush();
    }

    /// <summary>Newest GPU image of the area, scaled to fit (pointer included by Windows). Keeps the last image until one arrives.</summary>
    private void CaptureGpu(RecordingSource source)
    {
        Gdi.GdiFlush(); // pending GDI drawing must be done before the bits are written directly
        _gpu!.TryCopy(source, (pixels, pitch, w, h) =>
        {
            var dst = SessionRecorder.Fit(w, h, _w, _h);
            // Black bars only when the picture doesn't fill the frame and its place changed (the picture overwrites the rest).
            if (dst != _lastGpuRect && (dst.Width != _w || dst.Height != _h)) { Fill(0, 0, _w, _h, 0x000000); Gdi.GdiFlush(); }
            _lastGpuRect = dst;
            FrameScaler.Scale((byte*)pixels, pitch, w, h, Bits + ((long)dst.Y * _w + dst.X) * 4, _w * 4, dst.Width, dst.Height);
        });
    }

    private void Copy(IntPtr from, int x, int y, int w, int h, (int X, int Y, int Width, int Height) dst)
    {
        if (dst.Width == w && dst.Height == h) Gdi.BitBlt(_dc, dst.X, dst.Y, w, h, from, x, y, Gdi.SRCCOPY);
        else Gdi.StretchBlt(_dc, dst.X, dst.Y, dst.Width, dst.Height, from, x, y, w, h, Gdi.SRCCOPY);
    }

    private void EnsureWindowBitmap(int w, int h)
    {
        if (w == _windowW && h == _windowH && _windowDc != IntPtr.Zero) return;
        ReleaseWindowBitmap();
        _windowDc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        _windowBitmap = Gdi.CreateTopDownDib(_windowDc, Math.Max(1, w), Math.Max(1, h), out _);
        _oldWindowBitmap = Gdi.SelectObject(_windowDc, _windowBitmap);
        (_windowW, _windowH) = (w, h);
    }

    private void ReleaseWindowBitmap()
    {
        if (_windowDc == IntPtr.Zero) return;
        Gdi.SelectObject(_windowDc, _oldWindowBitmap);
        Gdi.DeleteObject(_windowBitmap);
        Gdi.DeleteDC(_windowDc);
        _windowDc = IntPtr.Zero;
    }

    /// <summary>The mouse pointer, when it is over the recorded area of that window (it isn't part of the captured image).</summary>
    private void DrawCursor(RecordingSource source, int originX, int originY, (int X, int Y, int Width, int Height) dst)
    {
        var info = new Gdi.CURSORINFO { cbSize = sizeof(Gdi.CURSORINFO) };
        if (!Gdi.GetCursorInfo(ref info) || (info.flags & 1) == 0 || info.hCursor == IntPtr.Zero) return;
        int cx = info.ptScreenPos.X - originX, cy = info.ptScreenPos.Y - originY;
        if (cx < 0 || cy < 0 || cx >= source.Width || cy >= source.Height) return;
        if (!source.Screen && Gdi.GetAncestor(Gdi.WindowFromPoint(info.ptScreenPos), 2) != source.Window) return;
        int hotX = 0, hotY = 0;
        if (Gdi.GetIconInfo(info.hCursor, out var icon))
        {
            (hotX, hotY) = (icon.xHotspot, icon.yHotspot);
            if (icon.hbmMask != IntPtr.Zero) Gdi.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) Gdi.DeleteObject(icon.hbmColor);
        }
        var scale = (double)dst.Width / source.Width;
        var size = (int)Math.Round(32 * Math.Max(scale, 0.5));
        Gdi.DrawIconEx(_dc, dst.X + (int)((cx - hotX) * scale), dst.Y + (int)((cy - hotY) * scale), info.hCursor, size, size, 0, IntPtr.Zero, 3);
    }

    /// <summary>Local date and time in the bottom-right corner.</summary>
    private void Stamp(string text)
    {
        var pad = Math.Max(4, _h / 160);
        var r = new Gdi.RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
        Gdi.DrawTextW(_dc, text, -1, ref r, Gdi.DT_CALCRECT | Gdi.DT_SINGLELINE);
        int tw = r.Right - r.Left, th = r.Bottom - r.Top;
        var box = new Gdi.RECT { Left = _w - tw - pad * 3, Top = _h - th - pad * 2, Right = _w - pad, Bottom = _h - pad };
        Fill(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top, 0x202020);
        Gdi.SetTextColor(_dc, 0xF0F0F0);
        Gdi.DrawTextW(_dc, text, -1, ref box, Gdi.DT_SINGLELINE | Gdi.DT_CENTER | Gdi.DT_VCENTER);
    }

    private void Placeholder()
    {
        Fill(0, 0, _w, _h, 0x1E1E1E);
        var r = new Gdi.RECT { Left = 0, Top = 0, Right = _w, Bottom = _h };
        Gdi.SetTextColor(_dc, 0xB0B0B0);
        Gdi.DrawTextW(_dc, "Tab not visible – recording continues when it is shown again", -1, ref r, Gdi.DT_SINGLELINE | Gdi.DT_CENTER | Gdi.DT_VCENTER);
        Gdi.GdiFlush();
    }

    private void Fill(int x, int y, int w, int h, int bgr)
    {
        var brush = Gdi.CreateSolidBrush(bgr);
        var r = new Gdi.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        Gdi.FillRect(_dc, ref r, brush);
        Gdi.DeleteObject(brush);
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        ReleaseWindowBitmap();
        Gdi.SelectObject(_dc, _oldFont);
        Gdi.DeleteObject(_font);
        Gdi.SelectObject(_dc, _oldBitmap);
        Gdi.DeleteObject(_bitmap);
        Gdi.DeleteDC(_dc);
    }
}

/// <summary>
/// MP4 (H.264) through the Media Foundation sink writer. RGB32 frames go in; the sink writer converts the colours and
/// encodes. Called through the COM vtables directly (slot numbers from mfobjects.h / mfreadwrite.h).
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    private IntPtr _writer;
    private readonly uint _stream;
    private readonly int _length;
    private bool _started, _comInit;
    // Encoding runs on its own thread, so the next frame is captured meanwhile (at most 2 frames wait).
    private readonly System.Collections.Concurrent.BlockingCollection<IntPtr> _queue = new(2);
    private Thread? _encoder;
    private Exception? _encodeError;

    /// <summary>Fragmented MP4 when Windows supports it, otherwise a plain MP4.</summary>
    public static Mp4Writer Create(string path, int width, int height, int fps, int bitRate)
    {
        try { return new Mp4Writer(path, width, height, fps, bitRate, fragmented: true); }
        catch (InvalidOperationException ex)
        {
            DiagLog.Write("Fragmented MP4 not available, using plain MP4: " + ex.Message);
            try { File.Delete(path); } catch { /* recreated below */ }
            return new Mp4Writer(path, width, height, fps, bitRate, fragmented: false);
        }
    }

    public bool Fragmented { get; }

    public Mp4Writer(string path, int width, int height, int fps, int bitRate, bool fragmented, EncoderSettings? settings = null, bool hardware = false)
    {
        settings ??= EncoderSettings.Default;
        Fragmented = fragmented;
        _length = width * height * 4;
        _comInit = Mf.CoInitializeEx(IntPtr.Zero, 0) >= 0; // COINIT_MULTITHREADED
        Mf.Check(Mf.MFStartup(0x20070, 0), "Media Foundation is not available on this PC");
        _started = true;
        IntPtr attrs = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero, encoder = IntPtr.Zero;
        try
        {
            Mf.Check(Mf.MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
            Mf.SetUInt32(attrs, Mf.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            if (hardware) Mf.SetUInt32(attrs, Mf.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            // Fragmented MP4: what was recorded stays playable when RemoteHub or Windows stops before the file is closed.
            Mf.SetGuid(attrs, Mf.MF_TRANSCODE_CONTAINERTYPE, fragmented ? Mf.MFTranscodeContainerType_FMPEG4 : Mf.MFTranscodeContainerType_MPEG4);
            Mf.Check(Mf.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out _writer), "The video file could not be created");

            Mf.Check(Mf.MFCreateMediaType(out output), "MFCreateMediaType");
            Mf.SetGuid(output, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(output, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_H264);
            Mf.SetUInt32(output, Mf.MF_MT_AVG_BITRATE, (uint)bitRate);
            Mf.SetUInt32(output, Mf.MF_MT_INTERLACE_MODE, 2); // progressive
            Mf.SetUInt64(output, Mf.MF_MT_FRAME_SIZE, Mf.Pack(width, height));
            Mf.SetUInt64(output, Mf.MF_MT_FRAME_RATE, Mf.Pack(fps, 1));
            Mf.SetUInt64(output, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            uint stream;
            Mf.Check(Mf.Call(_writer, 3, output, &stream), "The H.264 video encoder is not available on this PC (Windows N editions need the Media Feature Pack)");
            _stream = stream;

            Mf.Check(Mf.MFCreateMediaType(out input), "MFCreateMediaType");
            Mf.SetGuid(input, Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Video);
            Mf.SetGuid(input, Mf.MF_MT_SUBTYPE, Mf.MFVideoFormat_RGB32);
            Mf.SetUInt32(input, Mf.MF_MT_INTERLACE_MODE, 2);
            Mf.SetUInt64(input, Mf.MF_MT_FRAME_SIZE, Mf.Pack(width, height));
            Mf.SetUInt64(input, Mf.MF_MT_FRAME_RATE, Mf.Pack(fps, 1));
            Mf.SetUInt64(input, Mf.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
            Mf.SetUInt32(input, Mf.MF_MT_DEFAULT_STRIDE, (uint)(width * 4)); // positive = top-down rows
            // Encoder settings (ICodecAPI properties as attributes): quality-based VBR spends bits only where the screen changes.
            Mf.Check(Mf.MFCreateAttributes(out encoder, 3), "MFCreateAttributes");
            if (settings.Quality is { } quality)
            {
                Mf.SetUInt32(encoder, Mf.CODECAPI_AVEncCommonRateControlMode, 3); // eAVEncCommonRateControlMode_Quality
                Mf.SetUInt32(encoder, Mf.CODECAPI_AVEncCommonQuality, (uint)quality);
            }
            if (settings.KeyFrameSeconds > 0) Mf.SetUInt32(encoder, Mf.CODECAPI_AVEncMPVGOPSize, (uint)(settings.KeyFrameSeconds * fps));
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Mf.Slot(_writer, 4))(_writer, _stream, input, encoder),
                "The H.264 video encoder doesn't accept this frame size");
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Slot(_writer, 5))(_writer), "BeginWriting");
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            Mf.Release(attrs);
            Mf.Release(output);
            Mf.Release(input);
            Mf.Release(encoder);
        }
    }

    /// <summary>Copies one frame and queues it for encoding; time and duration in 100 ns units.</summary>
    public void Write(byte* bits, long time, long duration)
    {
        if (_encodeError is { } error) throw new InvalidOperationException(error.Message, error);
        if (_encoder is null)
        {
            _encoder = new Thread(EncodeLoop) { IsBackground = true, Name = "Recording encoder" };
            _encoder.Start();
        }
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Mf.Check(Mf.MFCreateMemoryBuffer((uint)_length, out buffer), "MFCreateMemoryBuffer");
            byte* target; uint max, current;
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Mf.Slot(buffer, 3))(buffer, &target, &max, &current), "Lock");
            Buffer.MemoryCopy(bits, target, max, _length);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Slot(buffer, 4))(buffer); // Unlock
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Mf.Slot(buffer, 6))(buffer, (uint)_length), "SetCurrentLength");

            Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Mf.Slot(sample, 42))(sample, buffer), "AddBuffer");
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Mf.Slot(sample, 36))(sample, time), "SetSampleTime");
            Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Mf.Slot(sample, 38))(sample, duration), "SetSampleDuration");
            _queue.Add(sample); // the encoder thread releases it
            sample = IntPtr.Zero;
        }
        finally
        {
            Mf.Release(sample);
            Mf.Release(buffer);
        }
    }

    private void EncodeLoop()
    {
        var com = Mf.CoInitializeEx(IntPtr.Zero, 0) >= 0;
        try
        {
            foreach (var sample in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (_encodeError is null)
                        Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Mf.Slot(_writer, 6))(_writer, _stream, sample), "Writing the video failed");
                }
                catch (Exception ex) { _encodeError = ex; }
                finally { Mf.Release(sample); }
            }
        }
        finally { if (com) Mf.CoUninitialize(); }
    }

    private void StopEncoder()
    {
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        _encoder?.Join();
        _encoder = null;
    }

    /// <summary>Encodes what is queued, flushes the encoder and closes the file (playable only after this).</summary>
    public void Finish()
    {
        StopEncoder();
        if (_encodeError is { } error) throw new InvalidOperationException(error.Message, error);
        Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Mf.Slot(_writer, 11))(_writer), "Closing the video file failed");
    }

    public void Dispose()
    {
        StopEncoder();
        Mf.Release(_writer);
        _writer = IntPtr.Zero;
        if (_started) { Mf.MFShutdown(); _started = false; }
        if (_comInit) { Mf.CoUninitialize(); _comInit = false; }
    }
}

/// <summary>
/// Encoder tuning: Quality = null keeps constant bit rate; otherwise quality-based VBR (0–100). Measured on 10 s of
/// console-like 1920 × 1080 content: constant 3.1 Mbit/s = 2.4 MB, quality 60 = 0.5 MB with sharp text.
/// A key frame every 10 s (a static screen costs almost nothing in between).
/// </summary>
internal sealed record EncoderSettings(int? Quality, int KeyFrameSeconds)
{
    public static readonly EncoderSettings Default = new(Quality: 60, KeyFrameSeconds: 10);
}

internal static unsafe class Mf
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_TRANSCODE_CONTAINERTYPE = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    public static readonly Guid MFTranscodeContainerType_MPEG4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    public static readonly Guid MFTranscodeContainerType_FMPEG4 = new("9ba876f1-419f-4b77-a1e0-35959d9d4004");
    public static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid CODECAPI_AVEncCommonQuality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public static IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

    public static int Call(IntPtr com, int slot, IntPtr arg, uint* result) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(com, slot))(com, arg, result);

    // IMFAttributes: SetUINT32 = 21, SetUINT64 = 22, SetGUID = 24
    public static void SetUInt32(IntPtr attrs, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attrs, 21))(attrs, &key, value), "SetUINT32");

    public static void SetUInt64(IntPtr attrs, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(attrs, 22))(attrs, &key, value), "SetUINT64");

    public static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attrs, 24))(attrs, &key, &value), "SetGUID");

    public static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero) Marshal.Release(com);
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} (0x{hr:X8}).");
    }

    [DllImport("ole32.dll")] public static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll")] public static extern void CoUninitialize();
    [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr mediaType);
    [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr sinkWriter);
}

internal static class Gdi
{
    public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    /// <summary>32-bit DIB section with the first row at the top.</summary>
    public static IntPtr CreateTopDownDib(IntPtr dc, int width, int height, out IntPtr bits)
    {
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) throw new InvalidOperationException($"Not enough memory for a {width} × {height} frame.");
        return bitmap;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, int format);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, int flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
