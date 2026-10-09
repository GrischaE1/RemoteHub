using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace RemoteHub.Agent;

/// <summary>Messages only between the agent service and its desktop helper (never accepted from RemoteHub).</summary>
internal enum HelperMessage : byte
{
    /// <summary>Service → helper: who connects, how to ask, what to send.</summary>
    Init = 100,
    /// <summary>Helper → service: the user's answer (Ask) or refusal (Pin).</summary>
    Consent = 101,
    /// <summary>Service → helper: consent is settled – show the banner and start sending.</summary>
    Begin = 102,
    /// <summary>Helper → service: the user clicked "Disconnect", or the helper can't go on.</summary>
    End = 103,
    /// <summary>
    /// Helper → service (only with <see cref="HelperInit.HoldUserDesktop"/>): a user's desktop appeared – someone unlocked
    /// or signed in. Nothing is sent or typed from then on; the service asks that user first.
    /// </summary>
    UserDesktop = 104,
}

/// <param name="HoldUserDesktop">
/// Nobody in this session allowed the connection (it started on the lock or sign-in screen): only the secure desktop is
/// shown; as soon as a user's desktop appears, the helper stops and reports <see cref="HelperMessage.UserDesktop"/>.
/// </param>
/// <param name="Clipboard">What may go through the clipboard (<see cref="DesktopClipboardLevel"/>; 0 when viewing).</param>
internal sealed record HelperInit(string Account, string Device, string Consent, string Pin, int TimeoutSeconds, bool ViewOnly,
    bool SecureDesktop, int FramesPerSecond, int Quality, int Monitor, bool HoldUserDesktop = false, int Clipboard = DesktopClipboardLevel.Text);

internal sealed record HelperConsent(bool Granted, string Reason);

/// <param name="ByUser">The user at the device ended it ("Disconnect") – otherwise the service starts a new helper.</param>
internal sealed record HelperEnd(string Reason, bool ByUser = false);

[JsonSerializable(typeof(HelperInit))]
[JsonSerializable(typeof(HelperConsent))]
[JsonSerializable(typeof(HelperEnd))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class HelperJson : JsonSerializerContext;

/// <summary>
/// <c>RemoteHubAgent.exe desktop</c> – started by the service in the chosen Windows session (as LocalSystem, so the
/// sign-in screen and UAC prompts are reachable). Talks to the service over its standard input/output only; the
/// service checks who connects and enforces view-only, this process does the screen work.
/// </summary>
internal static class DesktopHelperProgram
{
    public static int Run()
    {
        SetProcessDpiAwarenessContext(-4); // per-monitor v2: real pixels for capture and input
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var ui = new Win32HelperUi();
        using var screen = new WindowsScreen();
        using var keyboard = new WindowsInput();
        var session = new DesktopHelperSession(input, output, ui, screen, keyboard);
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
}

/// <summary>The work of the desktop helper, independent of Windows' screen and input (tests use doubles).</summary>
internal sealed class DesktopHelperSession(Stream fromService, Stream toService, IHelperUi ui, IScreenSource screen, IInputSink input)
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly BlockingCollection<RemoteKeyOrMouse> _input = new(256);
    private readonly SemaphoreSlim _acks = new(0, int.MaxValue);
    private HelperInit? _init;
    private volatile bool _begun;
    private volatile int _monitor, _fps = DesktopProtocol.DefaultFramesPerSecond, _quality = DesktopProtocol.DefaultQuality;
    private volatile bool _keyFrameRequested, _settingsChanged;
    private int _inFlight;
    private uint _frameId;
    private IReadOnlyList<DesktopMonitor> _monitors = [];
    private (int Width, int Height) _fit; // written with _settingsChanged (volatile) after it
    private volatile DesktopMonitor? _shown;
    private volatile bool _held; // a user's desktop appeared while HoldUserDesktop: no picture, no input
    private volatile int _scale = 1; // the picture of all monitors is scaled down by this when it is too large for H.264
    private int _sent;               // frames sent since the last stats

    // Clipboard images and files: one transfer each way at a time (a newer copy replaces an unfinished one).
    private TransferReceiver? _receiving;
    private CancellationTokenSource? _sending;
    private int _transferIds;

    private readonly record struct RemoteKeyOrMouse(RemoteMouse? Mouse, RemoteKey? Key);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        ui.ClipboardChanged += text => { if (_begun && Level >= DesktopClipboardLevel.Text) _ = Send(DesktopWire.Text(DesktopMessage.Clipboard, text)); };
        ui.ClipboardContent += content => { if (_begun) SendClipboard(content); };
        var capture = new Thread(() => CaptureLoop(token)) { IsBackground = true, Name = "Desktop capture" };
        var inject = new Thread(() => InputLoop(token)) { IsBackground = true, Name = "Desktop input" };
        try
        {
            while (!token.IsCancellationRequested)
            {
                var m = await DesktopWire.ReadFramedAsync(fromService, token).ConfigureAwait(false);
                if (m is null) break; // the service ended the session
                switch ((byte)DesktopWire.TypeOf(m))
                {
                    case (byte)HelperMessage.Init:
                        _init = DesktopWire.ReadJson(m, HelperJson.Default.HelperInit);
                        (_fps, _quality, _monitor) = (_init.FramesPerSecond, _init.Quality, _init.Monitor);
                        StartConsent(_init);
                        break;
                    case (byte)HelperMessage.Begin when _init is not null && !_begun:
                        _begun = true;
                        ui.CloseDialogs();
                        ui.ShowBanner(_init, () => _ = End("The user at the device ended the session.", byUser: true));
                        capture.Start();
                        inject.Start();
                        break;
                    case (byte)DesktopMessage.Settings when _begun:
                        var s = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopSettings);
                        (_monitor, _fps, _quality) = (s.Monitor, Math.Clamp(s.FramesPerSecond, 1, 60), Math.Clamp(s.Quality, 1, 100));
                        _fit = _init?.ViewOnly != false ? (0, 0) : (s.FitWidth, s.FitHeight);
                        _settingsChanged = true;
                        break;
                    case (byte)DesktopMessage.Ack when _begun:
                        DesktopWire.ReadAck(m);
                        if (Interlocked.Decrement(ref _inFlight) < 0) Interlocked.Exchange(ref _inFlight, 0);
                        _acks.Release();
                        break;
                    case (byte)DesktopMessage.KeyFrameRequest when _begun:
                        _keyFrameRequested = true;
                        break;
                    case (byte)DesktopMessage.Mouse when _begun && _init?.ViewOnly == false:
                        _input.TryAdd(new RemoteKeyOrMouse(DesktopWire.ReadMouse(m), null));
                        break;
                    case (byte)DesktopMessage.Key when _begun && _init?.ViewOnly == false:
                        _input.TryAdd(new RemoteKeyOrMouse(null, DesktopWire.ReadKey(m)), 1000);
                        break;
                    case (byte)DesktopMessage.Clipboard when _begun && Level >= DesktopClipboardLevel.Text:
                        ui.SetClipboard(DesktopWire.ReadText(m));
                        break;
                    case (byte)DesktopMessage.TransferStart when _begun:
                        Receive(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer));
                        break;
                    case (byte)DesktopMessage.TransferData when _begun && _receiving is { } r:
                        var (id, offset) = DesktopWire.ReadTransferData(m);
                        if (id == r.Transfer.Id) ReceiveData(r, offset, m);
                        break;
                    case (byte)DesktopMessage.TransferEnd when _receiving is { } r2 && DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd).Id == r2.Transfer.Id:
                        DropReceiving();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException) { }
        finally
        {
            _stop.Cancel();
            _sending?.Cancel();
            DropReceiving();
            _input.CompleteAdding();
            if (capture.IsAlive) capture.Join(TimeSpan.FromSeconds(3));
            if (inject.IsAlive) inject.Join(TimeSpan.FromSeconds(3));
            input.ReleaseAll();
        }
    }

    private void StartConsent(HelperInit init)
    {
        switch (init.Consent)
        {
            case DesktopConsent.Ask:
                ui.AskConsent(init, allowed => _ = Send(DesktopWire.Json((DesktopMessage)HelperMessage.Consent,
                    new HelperConsent(allowed, allowed ? "" : "The user at the device didn't allow it."), HelperJson.Default.HelperConsent)));
                break;
            case DesktopConsent.Pin:
                ui.ShowPin(init, () => _ = Send(DesktopWire.Json((DesktopMessage)HelperMessage.Consent,
                    new HelperConsent(false, "The user at the device refused."), HelperJson.Default.HelperConsent)));
                break;
        }
    }

    private async Task End(string reason, bool byUser = false)
    {
        await Send(DesktopWire.Json((DesktopMessage)HelperMessage.End, new HelperEnd(reason, byUser), HelperJson.Default.HelperEnd));
        _stop.Cancel();
    }

    private async Task Send(byte[] message)
    {
        try
        {
            await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { await DesktopWire.WriteFramedAsync(toService, message, _stop.Token).ConfigureAwait(false); }
            finally { _write.Release(); }
        }
        catch (Exception) { _stop.Cancel(); }
    }

    private void SendSync(byte[] message) => Send(message).GetAwaiter().GetResult();

    private int Level => _init is null || _init.ViewOnly ? DesktopClipboardLevel.Off : _init.Clipboard;

    // ---- clipboard images and files

    /// <summary>RemoteHub copied an image or files: collected here, then put on the device's clipboard.</summary>
    private void Receive(DesktopTransfer t)
    {
        DropReceiving();
        var need = t.Kind == DesktopTransferKind.Image ? DesktopClipboardLevel.Images : DesktopClipboardLevel.Files;
        if (Level < need) { TransferFailed(t.Id, "Not allowed on this device."); return; }
        try
        {
            // Files are written as the signed-in user, into their own temp folder – never with the helper's LocalSystem rights.
            _receiving = t.Kind == DesktopTransferKind.Image
                ? new TransferReceiver(t, null)
                : SessionUser.Run(() => new TransferReceiver(t, SessionUser.NewClipboardFolder()));
            if (_receiving.Complete) Finish(_receiving);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DropReceiving();
            TransferFailed(t.Id, "The device can't take the copied files: " + ex.Message);
        }
    }

    private void ReceiveData(TransferReceiver r, long offset, byte[] m)
    {
        try
        {
            var data = m.AsMemory(DesktopWire.TransferHeaderSize);
            if (r.Transfer.Kind == DesktopTransferKind.Image) r.Add(offset, data.Span);
            else SessionUser.Run(() => { r.Add(offset, data.Span); return 0; });
            if (r.Complete) Finish(r);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DropReceiving();
            TransferFailed(r.Transfer.Id, "The device can't take the copied files: " + ex.Message);
        }
    }

    private void Finish(TransferReceiver r)
    {
        _receiving = null;
        if (r.Transfer.Kind == DesktopTransferKind.Image) ui.SetClipboardImage(r.Image);
        else ui.SetClipboardFiles(r.TopLevel);
        r.Dispose();
    }

    private void DropReceiving()
    {
        var r = _receiving;
        _receiving = null;
        try { if (r is not null) SessionUser.Run(() => { r.Dispose(); return 0; }); } catch { r?.Dispose(); }
    }

    private void TransferFailed(int id, string reason) =>
        _ = Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(id, false, reason), DesktopJson.Default.DesktopTransferEnd));

    /// <summary>The device's clipboard got an image or files: sent to RemoteHub in chunks, in the background.</summary>
    private void SendClipboard(HelperClipboard content)
    {
        var need = content.Files is not null ? DesktopClipboardLevel.Files : DesktopClipboardLevel.Images;
        if (Level < need) return;
        _sending?.Cancel();
        var cancel = _sending = new CancellationTokenSource();
        var id = Interlocked.Increment(ref _transferIds);
        new Thread(() =>
        {
            try
            {
                void Run()
                {
                    ClipboardTransfer.Outgoing? o;
                    if (content.Image is { } image) o = ClipboardTransfer.Image(id, image, content.Png);
                    else if ((o = ClipboardTransfer.Files(id, content.Files!, out var error)) is null) { TransferFailed(id, error ?? "Nothing to copy."); return; }
                    SendSync(DesktopWire.Json(DesktopMessage.TransferStart, o.Transfer, DesktopJson.Default.DesktopTransfer));
                    foreach (var chunk in ClipboardTransfer.Chunks(o))
                    {
                        if (cancel.IsCancellationRequested || _stop.IsCancellationRequested)
                        {
                            TransferFailed(id, "A newer copy replaced it.");
                            return;
                        }
                        SendSync(chunk);
                    }
                }
                // Files are read as the signed-in user: only what they may read themselves.
                if (content.Files is not null) SessionUser.Run(() => { Run(); return 0; });
                else Run();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TransferFailed(id, "The copied files can't be read on the device: " + ex.Message);
            }
        }) { IsBackground = true, Name = "Clipboard transfer" }.Start();
    }

    // ---- input thread (follows the input desktop itself)

    private void InputLoop(CancellationToken ct)
    {
        try
        {
            foreach (var i in _input.GetConsumingEnumerable(ct))
            {
                if (_held || _init!.HoldUserDesktop && !screen.SecureDesktop) continue; // not allowed by the user now there
                if (i.Key is { } k) input.Key(k);
                else if (i.Mouse is { } m && _shown is { } monitor)
                {
                    var f = _scale;
                    input.Mouse(monitor, f == 1 ? m : m with { X = m.X * f + f / 2, Y = m.Y * f + f / 2 });
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { input.ReleaseAll(); }
    }

    // ---- capture thread

    private unsafe void CaptureLoop(CancellationToken ct)
    {
        var frame = new FrameBuffer();
        var scaled = new FrameBuffer();
        var lastStats = Stopwatch.StartNew();
        H264Encoder? encoder = null;
        var quality = _quality;
        var lastSync = Stopwatch.StartNew();
        var lastChange = Stopwatch.StartNew();
        var lastAdapt = Stopwatch.StartNew();
        var refinements = 0;
        int blocked = 0, loops = 0;
        RemoteCursor lastCursor = default;
        var secure = false;
        var haveImage = false;
        try
        {
            screen.SyncDesktop();
            SendLayout(out _);
            while (!ct.IsCancellationRequested)
            {
                var interval = 1000 / Math.Max(1, _fps);
                var loopStart = Stopwatch.GetTimestamp();

                // Desktop switch (sign-in screen, UAC prompt) and changed monitors. While holding, checked before every
                // frame: the user's desktop must not be sent at all.
                if (_held) { Thread.Sleep(200); continue; }
                if (lastSync.ElapsedMilliseconds >= 200 || _init!.HoldUserDesktop)
                {
                    lastSync.Restart();
                    var switched = screen.SyncDesktop();
                    if (_settingsChanged && _shown is { } m0)
                    {
                        // Resolution to the viewer's window (temporary; Windows' own setting comes back at the end).
                        // Not for the picture of all monitors.
                        var (fw, fh) = _fit;
                        if (fw > 0 && fh > 0 && m0.Index != DesktopProtocol.AllMonitors && _monitor != DesktopProtocol.AllMonitors) screen.FitResolution(m0, fw, fh);
                        else screen.RestoreResolution();
                    }
                    if (_init!.HoldUserDesktop && !screen.SecureDesktop)
                    {
                        _held = true;
                        SendSync([(byte)HelperMessage.UserDesktop]);
                        continue;
                    }
                    if (switched || screen.SecureDesktop != secure || _settingsChanged || MonitorsChanged())
                    {
                        _settingsChanged = false;
                        secure = screen.SecureDesktop;
                        SendLayout(out _);
                        encoder?.Dispose();
                        encoder = null;
                        haveImage = false;
                    }
                }
                if (secure && _init!.SecureDesktop == false)
                {
                    Thread.Sleep(200); // no right to see the secure desktop: nothing is sent until it's gone
                    continue;
                }
                var monitor = _shown;
                if (monitor is null) { Thread.Sleep(200); continue; }

                // Flow control: no more than a few frames ahead of the viewer.
                if (Volatile.Read(ref _inFlight) >= DesktopProtocol.MaxFramesInFlight)
                {
                    blocked++;
                    if (!_acks.Wait(1000, ct)) Interlocked.Exchange(ref _inFlight, 0); // an ack got lost: don't stall forever
                    continue;
                }
                loops++;

                var result = screen.Capture(monitor, haveImage ? Math.Max(1, interval / 2) : 100, frame);
                if (result == CaptureResult.Failed) { Thread.Sleep(50); continue; }
                var cursor = screen.Cursor(monitor, out var shape);
                if (_scale > 1) cursor = cursor with { X = cursor.X / _scale, Y = cursor.Y / _scale };
                if (shape is not null) SendSync(DesktopWire.CursorShape(shape.Width, shape.Height, shape.HotX, shape.HotY, shape.Bgra));
                if (cursor != lastCursor)
                {
                    lastCursor = cursor;
                    SendSync(DesktopWire.Cursor(cursor));
                }

                var send = false;
                if (result == CaptureResult.Changed)
                {
                    haveImage = true;
                    send = true;
                    refinements = 0;
                    lastChange.Restart();
                }
                else if (haveImage && refinements < 3 && lastChange.ElapsedMilliseconds > 150 + refinements * 400)
                {
                    send = true; // the screen stopped changing: a few more frames sharpen it
                    refinements++;
                }
                else if (haveImage && _keyFrameRequested) send = true;

                if (send)
                {
                    var picture = frame;
                    if (_scale > 1)
                    {
                        Downscale(frame, scaled, _scale);
                        picture = scaled;
                    }
                    int w = picture.Width & ~1, h = picture.Height & ~1;
                    if (encoder is null || encoder.Width != w || encoder.Height != h || quality != _quality && !encoder.TrySetQuality(_quality))
                    {
                        encoder?.Dispose();
                        encoder = new H264Encoder(w, h, Math.Max(1, _fps), _quality);
                        _keyFrameRequested = true;
                        if ((w != (_layoutWidth & ~1) || h != (_layoutHeight & ~1)) && _scale == 1) SendLayout(out _);
                    }
                    quality = encoder.Quality;
                    var key = _keyFrameRequested;
                    _keyFrameRequested = false;
                    byte[]? au;
                    bool isKey;
                    fixed (byte* p = picture.Pixels) au = encoder.Encode(p, picture.Width * 4, key, out isKey);
                    if (au is not null)
                    {
                        _sent++;
                        Interlocked.Increment(ref _inFlight);
                        SendSync(DesktopWire.Video(new VideoHeader(++_frameId, w, h, isKey), au));
                    }
                }

                if (lastStats.ElapsedMilliseconds >= 2000)
                {
                    var seconds = lastStats.Elapsed.TotalSeconds;
                    lastStats.Restart();
                    SendSync(DesktopWire.Json(DesktopMessage.Stats, new DesktopStats((int)Math.Round(_sent / seconds), encoder?.Quality ?? _quality,
                        screen.CaptureMethod, encoder?.Width ?? 0, encoder?.Height ?? 0, screen.FitInfo), DesktopJson.Default.DesktopStats));
                    _sent = 0;
                }

                // Slow network: lower the quality step by step; raise it again when the acks keep up.
                if (lastAdapt.ElapsedMilliseconds >= 2000)
                {
                    lastAdapt.Restart();
                    var target = _quality;
                    if (blocked > loops && encoder is not null && encoder.Quality > 20) encoder.TrySetQuality(encoder.Quality - 10);
                    else if (blocked == 0 && encoder is not null && encoder.Quality < target) encoder.TrySetQuality(Math.Min(target, encoder.Quality + 5));
                    if (encoder is not null) quality = encoder.Quality;
                    blocked = loops = 0;
                }

                var spent = (int)Stopwatch.GetElapsedTime(loopStart).TotalMilliseconds;
                if (spent < interval) Thread.Sleep(interval - spent);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _ = End("The screen can't be captured: " + ex.Message);
        }
        finally
        {
            encoder?.Dispose();
            try { screen.RestoreResolution(); } catch { /* the process ends anyway – Windows restores it then */ }
        }
    }

    private int _layoutWidth, _layoutHeight;

    private bool MonitorsChanged()
    {
        var now = screen.Monitors();
        return !now.SequenceEqual(_monitors);
    }

    private void SendLayout(out DesktopMonitor? shown)
    {
        _monitors = screen.Monitors();
        var all = _monitor == DesktopProtocol.AllMonitors && _monitors.Count > 1;
        shown = _monitors.Count == 0 ? null : all ? AllMonitors(_monitors) : _monitors[Math.Clamp(_monitor, 0, _monitors.Count - 1)];
        // All monitors in one picture: larger than H.264 allows is scaled down (mouse and pointer are scaled back).
        _scale = shown is null || !all ? 1
            : Math.Max(1, Math.Max((shown.Width + DesktopProtocol.MaxVideoWidth - 1) / DesktopProtocol.MaxVideoWidth, (shown.Height + DesktopProtocol.MaxVideoHeight - 1) / DesktopProtocol.MaxVideoHeight));
        _shown = shown;
        (_layoutWidth, _layoutHeight) = shown is null ? (0, 0) : (shown.Width / _scale, shown.Height / _scale);
        SendSync(DesktopWire.Json(DesktopMessage.Layout,
            new DesktopLayout([.. _monitors], all ? DesktopProtocol.AllMonitors : shown?.Index ?? 0, _layoutWidth & ~1, _layoutHeight & ~1, screen.SecureDesktop, _init?.ViewOnly ?? true),
            DesktopJson.Default.DesktopLayout));
    }

    /// <summary>The bounding box of all monitors, as one "monitor" (gaps between monitors stay black).</summary>
    internal static DesktopMonitor AllMonitors(IReadOnlyList<DesktopMonitor> monitors)
    {
        int left = monitors.Min(m => m.X), top = monitors.Min(m => m.Y);
        int right = monitors.Max(m => m.X + m.Width), bottom = monitors.Max(m => m.Y + m.Height);
        return new DesktopMonitor(DesktopProtocol.AllMonitors, WindowsScreen.AllMonitorsName, left, top, right - left, bottom - top, false);
    }

    /// <summary>Box filter by an integer factor (BGRA).</summary>
    internal static unsafe void Downscale(FrameBuffer source, FrameBuffer target, int factor)
    {
        int w = source.Width / factor, h = source.Height / factor, n = factor * factor;
        target.Ensure(w, h);
        fixed (byte* s = source.Pixels, d = target.Pixels)
        {
            var stride = source.Width * 4;
            for (var y = 0; y < h; y++)
            {
                var o = d + (long)y * w * 4;
                for (var x = 0; x < w; x++, o += 4)
                {
                    int b = 0, g = 0, r = 0;
                    for (var dy = 0; dy < factor; dy++)
                    {
                        var p = s + (long)(y * factor + dy) * stride + x * factor * 4;
                        for (var dx = 0; dx < factor; dx++, p += 4) { b += p[0]; g += p[1]; r += p[2]; }
                    }
                    o[0] = (byte)(b / n);
                    o[1] = (byte)(g / n);
                    o[2] = (byte)(r / n);
                    o[3] = 255;
                }
            }
        }
    }
}
