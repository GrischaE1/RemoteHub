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
    /// <summary>
    /// Service → helper: send Ctrl+Alt+Del in the helper's own session. From the service (session 0) Windows decides
    /// which session gets it – not always the one being viewed.
    /// </summary>
    SecureAttention = 105,
    /// <summary>Helper → service: the result of <see cref="SecureAttention"/> (text: empty when sent, else why not).</summary>
    SecureAttentionResult = 106,
    /// <summary>Helper → service: a line for the agent's log (capture method, resolution change).</summary>
    Log = 107,
    /// <summary>
    /// Helper → service: the session needs the virtual monitor (no usable resolution – no monitor connected) at this size
    /// (text: "width height"), or the viewer's window changed size while it's on.
    /// </summary>
    DisplayRequest = 108,
}

/// <param name="HoldUserDesktop">
/// Nobody in this session allowed the connection (it started on the lock or sign-in screen): only the secure desktop is
/// shown; as soon as a user's desktop appears, the helper stops and reports <see cref="HelperMessage.UserDesktop"/>.
/// </param>
/// <param name="Clipboard">What may go through the clipboard (<see cref="DesktopClipboardLevel"/>; 0 when viewing).</param>
/// <param name="VirtualDisplay">The virtual monitor (<see cref="VirtualDisplayModes"/>) if it's installed, else "".</param>
/// <param name="OfferToViewer">RemoteHub takes offers: what is copied here goes only when pasted there (TransferOffer / TransferRequest).</param>
internal sealed record HelperInit(string Account, string Device, string Consent, string Pin, int TimeoutSeconds, bool ViewOnly,
    bool SecureDesktop, int FramesPerSecond, int Quality, int Monitor, bool HoldUserDesktop = false, int Clipboard = DesktopClipboardLevel.Text,
    bool Gdi = false, string VirtualDisplay = "", bool OfferToViewer = false);

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
    private string _loggedFit = "";
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
                        // Decided for each event on the desktop it would go to – not on what the capture thread last
                        // saw – so nothing reaches the secure desktop without the right, or a user's desktop while held.
                        var init = _init;
                        input.Allowed = secure => !_held && (secure ? init.SecureDesktop : !init.HoldUserDesktop);
                        if (init.Gdi) screen.PreferGdi(); // the GPU picture stood still earlier in this connection
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
                    case (byte)HelperMessage.SecureAttention when _begun && _init?.ViewOnly == false && _init.SecureDesktop:
                        // In this session, as LocalSystem (the service checked the right). Not on this loop's thread.
                        _ = Task.Run(() =>
                        {
                            _ = Send(DesktopWire.Text((DesktopMessage)HelperMessage.SecureAttentionResult, input.SecureAttention() ?? ""));
                        });
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
                    case (byte)DesktopMessage.TransferRequest when _begun:
                        if (int.TryParse(DesktopWire.ReadText(m), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var requested))
                            SendOffered(requested);
                        break;
                    case (byte)DesktopMessage.TransferOffer when _begun:
                        Offer(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer));
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
        if (Level < need) { TransferFailed(t.Id, "Not allowed on this device.", toDevice: true); return; }
        try
        {
            // Files are written as the signed-in user, into their own temp folder – never with the helper's LocalSystem rights.
            if (t.Kind == DesktopTransferKind.Image) _receiving = new TransferReceiver(t, null);
            else
            {
                var root = SessionUser.ClipboardRoot(); // looked up as LocalSystem, then used as the user
                _receiving = SessionUser.Run(() => new TransferReceiver(t, SessionUser.NewClipboardFolder(root)));
            }
            if (_receiving.Complete) Finish(_receiving);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DropReceiving();
            TransferFailed(t.Id, "The device can't take the copied files: " + ex.Message, toDevice: true);
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
            TransferFailed(r.Transfer.Id, "The device can't take the copied files: " + ex.Message, toDevice: true);
        }
    }

    private void Finish(TransferReceiver r)
    {
        _receiving = null;
        r.Dispose(); // the files are closed before they go on the clipboard
        var content = r.Transfer.Kind == DesktopTransferKind.Image ? new HelperClipboard(r.Image, r.IsPng) : new HelperClipboard(Files: r.TopLevel, Items: r.Targets);
        // Asked for by a paste: the waiting paste gets it. Else (an older RemoteHub sends right away): onto the clipboard.
        if (_pasting is { } p && p.Id == r.Transfer.Id) p.Done.TrySetResult(content);
        else if (content.Image is { } image) ui.SetClipboardImage(image, content.Png);
        else ui.SetClipboardFiles(content.Files!);
        // Receipt: RemoteHub says "copied" only now – not when it has sent the last byte.
        _ = Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(r.Transfer.Id, true, "", ToDevice: true), DesktopJson.Default.DesktopTransferEnd));
    }

    private void DropReceiving()
    {
        var r = _receiving;
        _receiving = null;
        if (r is not null) PasteFailed(r.Transfer.Id);
        try { if (r is not null) SessionUser.Run(() => { r.Dispose(); return 0; }); } catch { r?.Dispose(); }
    }

    private void TransferFailed(int id, string reason, bool toDevice = false)
    {
        if (toDevice) PasteFailed(id);
        _ = Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(id, false, reason, toDevice), DesktopJson.Default.DesktopTransferEnd));
    }

    // ---- copied in RemoteHub: offered on the device's clipboard, sent when pasted

    /// <summary>The paste waiting for an offered transfer (it runs on the helper's UI thread).</summary>
    private volatile Pasting? _pasting;

    private sealed record Pasting(int Id, TaskCompletionSource<HelperClipboard?> Done);

    private void Offer(DesktopTransfer t)
    {
        var need = t.Kind == DesktopTransferKind.Image ? DesktopClipboardLevel.Images : DesktopClipboardLevel.Files;
        if (Level < need) { TransferFailed(t.Id, "Not allowed on this device.", toDevice: true); return; }
        if (t.Kind == DesktopTransferKind.Files && VirtualFiles.SecurityResult != 0)
            _ = Send(DesktopWire.Text((DesktopMessage)HelperMessage.Log, $"clipboard: pastes from the user's programs may be refused (COM security 0x{VirtualFiles.SecurityResult:X8})"));
        ui.OfferClipboard(t, paster => Fetch(t, paster));
    }

    /// <summary>Someone pastes the offer: RemoteHub sends it now; the paste waits until it's here (or it failed).</summary>
    private HelperClipboard? Fetch(DesktopTransfer t, string paster)
    {
        // Programs that read every new clipboard entry at once, not a paste: RDP's clipboard sync (it lists the files
        // for an RDP client) and Windows' clipboard history. They get nothing – the next real paste asks again.
        if (BackgroundReader(paster))
        {
            SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, $"clipboard: {paster} reads every clipboard change – not sent (only a paste gets it)"));
            return null;
        }
        var pasting = _pasting = new Pasting(t.Id, new TaskCompletionSource<HelperClipboard?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, $"clipboard: pasted by {paster} – what was copied in RemoteHub ({t.Items.Length} item(s), {t.Total} bytes) comes now"));
            SendSync(DesktopWire.Text(DesktopMessage.TransferRequest, t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // Generous: at least 256 KB/s, at most an hour. Cancelled when the connection ends.
            var limit = TimeSpan.FromSeconds(Math.Min(3600, 60 + t.Total / (256 * 1024.0)));
            return pasting.Done.Task.Wait(limit, _stop.Token) ? pasting.Done.Task.Result : null;
        }
        catch (OperationCanceledException) { return null; }
        finally { if (_pasting == pasting) _pasting = null; }
    }

    /// <summary>"rdpclip (1234)", "svchost (5678)" – background clipboard readers (see <see cref="Fetch"/>).</summary>
    internal static bool BackgroundReader(string paster) =>
        paster.StartsWith("rdpclip ", StringComparison.OrdinalIgnoreCase) || paster.StartsWith("svchost ", StringComparison.OrdinalIgnoreCase);

    private void PasteFailed(int id)
    {
        if (_pasting is { } p && p.Id == id) p.Done.TrySetResult(null);
    }

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
                ClipboardTransfer.Outgoing? o = null;
                string? error = null;
                // Files are read as the signed-in user: only what they may read themselves.
                if (content.Image is { } image) o = ClipboardTransfer.Image(id, image, content.Png);
                else SessionUser.Run(() => o = ClipboardTransfer.Files(id, content.Files!, out error));
                if (o is null) { TransferFailed(id, error ?? "Nothing to copy."); return; }
                if (_init?.OfferToViewer == true)
                {
                    // Names and sizes only: RemoteHub's clipboard offers them; the data goes when someone pastes there.
                    _offered = new OfferedOut(o, content.Files is not null);
                    SendSync(DesktopWire.Json(DesktopMessage.TransferOffer, o.Transfer, DesktopJson.Default.DesktopTransfer));
                    return;
                }
                SendOutgoing(o, content.Files is not null, cancel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TransferFailed(id, "The copied files can't be read on the device: " + ex.Message);
            }
        }) { IsBackground = true, Name = "Clipboard transfer" }.Start();
    }

    /// <summary>What was copied here and offered to RemoteHub, until it's pasted there (or something else is copied).</summary>
    private volatile OfferedOut? _offered;

    private sealed record OfferedOut(ClipboardTransfer.Outgoing Outgoing, bool Files);

    /// <summary>Pasted in RemoteHub: what was offered goes now (in the background, like a direct copy).</summary>
    private void SendOffered(int id)
    {
        if (_offered is not { } offered || offered.Outgoing.Transfer.Id != id) return;
        _sending?.Cancel();
        var cancel = _sending = new CancellationTokenSource();
        new Thread(() =>
        {
            try { SendOutgoing(offered.Outgoing, offered.Files, cancel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TransferFailed(id, "The copied files can't be read on the device: " + ex.Message);
            }
        }) { IsBackground = true, Name = "Clipboard transfer" }.Start();
    }

    /// <summary>The transfer's start, then its chunks; files read as the signed-in user.</summary>
    private void SendOutgoing(ClipboardTransfer.Outgoing o, bool files, CancellationTokenSource cancel)
    {
        void Run()
        {
            SendSync(DesktopWire.Json(DesktopMessage.TransferStart, o.Transfer, DesktopJson.Default.DesktopTransfer));
            foreach (var chunk in ClipboardTransfer.Chunks(o))
            {
                if (cancel.IsCancellationRequested || _stop.IsCancellationRequested)
                {
                    TransferFailed(o.Transfer.Id, "A newer copy replaced it.");
                    return;
                }
                SendSync(chunk);
            }
        }
        if (files) SessionUser.Run(() => { Run(); return 0; });
        else Run();
    }

    // ---- virtual monitor (devices without a monitor)

    private bool _virtualAsked;
    private volatile bool _refit;
    private int _primaryTries;
    private (int W, int H) _virtualSize;
    private readonly Stopwatch _virtualCheck = new();

    /// <summary>
    /// The viewer's window is <paramref name="width"/> × <paramref name="height"/>: with "auto" the virtual monitor is asked
    /// for when the shown display offers far less (a GPU without a monitor: 640×480), with "always" in any case; while
    /// it's on, a new window size goes to the service too (the driver gets that size as a mode).
    /// </summary>
    private void AskForVirtualDisplay(DesktopMonitor shown, int width, int height, bool dark = false)
    {
        var mode = _init!.VirtualDisplay;
        if (mode is not (VirtualDisplayModes.Auto or VirtualDisplayModes.Always)) return;
        if (!_virtualAsked && !dark && mode == VirtualDisplayModes.Auto && DisplayModes.VirtualMonitor() is null &&
            DisplayModes.LargestArea(shown.Name) >= Math.Min((long)width * height, 1280L * 720)) return;
        if (_virtualAsked && _virtualSize == VirtualDisplay.Normalize(width, height)) return;
        _virtualAsked = true;
        _virtualSize = VirtualDisplay.Normalize(width, height);
        SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.DisplayRequest, $"{width} {height}"));
    }

    /// <summary>Once the virtual monitor is there (also after the driver reloaded it): it becomes the primary one and is fitted.</summary>
    private void VirtualDisplayPrimary()
    {
        if (!_virtualAsked || _virtualCheck.IsRunning && _virtualCheck.ElapsedMilliseconds < 1000) return;
        _virtualCheck.Restart();
        if (DisplayModes.VirtualMonitor() is not { } virt) return;
        var monitors = DisplayMonitors.List();
        if (monitors.FirstOrDefault(m => m.Primary)?.Name.Equals(virt, StringComparison.OrdinalIgnoreCase) == true)
        {
            // Primary already; after the driver reloaded with a new window size it still runs at the old one: fit again.
            if (monitors.First(m => m.Primary) is var p && (p.Width, p.Height) != _virtualSize) _refit = true;
            return;
        }
        if (_primaryTries >= 3) return; // logged; the monitor can still be chosen in RemoteHub
        _primaryTries++;
        var failed = DisplayModes.MakePrimary(virt);
        SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, failed is null ? $"virtual monitor {virt} is the primary monitor now" : $"virtual monitor {virt} couldn't be made the primary monitor ({failed})"));
        _refit = true; // fitted to the window at the next check (after the new layout)
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
        // No picture for the viewer (black, held, nothing captured): after 3 s the display is woken (up to 3 tries) and
        // the reason goes to the log once.
        var dark = Stopwatch.StartNew();
        var lastWake = Stopwatch.StartNew();
        int wakes = 0;
        var darkLogged = false;
        var black = false;
        var why = "nothing captured yet";
        screen.KeepDisplayOn(true); // wakes a display that went dark (idle, lock screen) and keeps it on while connected
        try
        {
            screen.SyncDesktop();
            SendLayout(out _);
            while (!ct.IsCancellationRequested)
            {
                var interval = 1000 / Math.Max(1, _fps);
                var loopStart = Stopwatch.GetTimestamp();

                if (dark.ElapsedMilliseconds >= 3000 && wakes < 3 && (wakes == 0 || lastWake.ElapsedMilliseconds >= 2000))
                {
                    wakes++;
                    lastWake.Restart();
                    if (wakes == 2 && !_held && !_virtualAsked && why == "the captured picture is black" && _shown is { } s0 &&
                        s0.Index != DesktopProtocol.AllMonitors && _init!.VirtualDisplay == VirtualDisplayModes.Auto)
                    {
                        // Still black after waking: the monitor's output is dead (seen on Intel GPUs without a monitor that
                        // still list full display modes, while the lock screen is up). The virtual monitor takes over.
                        var (vw, vh) = _fit is { Width: > 0, Height: > 0 } f ? f : (s0.Width, s0.Height);
                        SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, "the picture stays black – switching the virtual monitor on"));
                        AskForVirtualDisplay(s0, vw, vh, dark: true);
                        _refit = true;
                    }
                    // Not while the signed-in user is still asked: their desktop gets no input before they agree.
                    var woke = _held ? "not woken (waiting for consent)" : screen.WakeDisplay();
                    if (!darkLogged)
                    {
                        darkLogged = true;
                        SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log,
                            $"no picture for 3 s ({(_held ? "waiting for the signed-in user's consent" : why)}): {screen.DesktopDescription}, " +
                            $"capture {(screen.CaptureMethod is { Length: > 0 } cm ? cm : "not started")}, lock screen {(screen.LockScreenUp ? "up" : "not up")}, " +
                            $"{_monitors.Count} monitor(s) {string.Join(" ", _monitors.Select(m => $"{m.Width}×{m.Height}"))}; {WindowsInput.Stats}; {woke}"));
                    }
                }

                // Desktop switch (sign-in screen, UAC prompt) and changed monitors. While holding, checked before every
                // frame: the user's desktop must not be sent at all.
                if (_held) { Thread.Sleep(200); continue; }
                if (lastSync.ElapsedMilliseconds >= 200 || _init!.HoldUserDesktop)
                {
                    lastSync.Restart();
                    var switched = screen.SyncDesktop();
                    if ((_settingsChanged || _refit) && _shown is { } m0)
                    {
                        _refit = false;
                        // Resolution to the viewer's window (temporary; Windows' own setting comes back at the end).
                        // Not for the picture of all monitors.
                        var (fw, fh) = _fit;
                        if (fw > 0 && fh > 0 && m0.Index != DesktopProtocol.AllMonitors && _monitor != DesktopProtocol.AllMonitors)
                        {
                            AskForVirtualDisplay(m0, fw, fh);
                            screen.FitResolution(m0, fw, fh);
                            if (screen.FitInfo is { Length: > 0 } fit && fit != _loggedFit) SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, "resolution: " + (_loggedFit = fit)));
                        }
                        else screen.RestoreResolution();
                    }
                    if (_init!.HoldUserDesktop && !screen.SecureDesktop && screen.LockScreenUp)
                    {
                        // Windows 11 / Server 2025: the "Press Ctrl+Alt+Delete" lock screen runs on the Default desktop.
                        // Still locked – nobody unlocked: nothing is shown or sent there, but the session stays
                        // connected, so Ctrl+Alt+Del can bring up the sign-in screen.
                        why = "the lock screen is up – waiting for Ctrl+Alt+Del";
                        Thread.Sleep(200);
                        continue;
                    }
                    if (_init!.HoldUserDesktop && !screen.SecureDesktop)
                    {
                        _held = true;
                        SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.UserDesktop, screen.DesktopDescription));
                        continue;
                    }
                    VirtualDisplayPrimary();
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
                    why = "the secure desktop is up and this connection may not see it";
                    Thread.Sleep(200); // no right to see the secure desktop: nothing is sent until it's gone
                    continue;
                }
                var monitor = _shown;
                if (monitor is null) { why = "no monitor"; Thread.Sleep(200); continue; }

                // Flow control: no more than a few frames ahead of the viewer.
                if (Volatile.Read(ref _inFlight) >= DesktopProtocol.MaxFramesInFlight)
                {
                    blocked++;
                    if (!_acks.Wait(1000, ct)) Interlocked.Exchange(ref _inFlight, 0); // an ack got lost: don't stall forever
                    continue;
                }
                loops++;

                var result = screen.Capture(monitor, haveImage ? Math.Max(1, interval / 2) : 100, frame);
                if (screen.TakeLogNote() is { Length: > 0 } note) SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, note));
                if (result == CaptureResult.Failed) { why = "capturing failed"; Thread.Sleep(50); continue; }
                if (result == CaptureResult.Changed) black = IsBlack(frame);
                if (result == CaptureResult.Changed || haveImage)
                {
                    if (!black) { dark.Restart(); wakes = 0; darkLogged = false; }
                    else why = "the captured picture is black";
                }
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
                        // "Smooth" (above 30 fps): the GPU's encoder when there is one – it falls back to software by itself.
                        encoder = new H264Encoder(w, h, Math.Max(1, _fps), _quality, preferHardware: _fps > 30);
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
                        screen.CaptureMethod + (encoder?.Kind == "hardware" ? ", GPU encoder" : ""), encoder?.Width ?? 0, encoder?.Height ?? 0, screen.FitInfo),
                        DesktopJson.Default.DesktopStats));
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
            screen.KeepDisplayOn(false);
        }
    }


    /// <summary>All of a 16×16 grid of samples is (nearly) black: a display that went dark, a blank screen saver.</summary>
    internal static bool IsBlack(FrameBuffer frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return true;
        for (var gy = 0; gy < 16; gy++)
            for (var gx = 0; gx < 16; gx++)
            {
                var x = (int)((gx + 0.5) * frame.Width / 16);
                var y = (int)((gy + 0.5) * frame.Height / 16);
                var i = (y * frame.Width + x) * 4;
                if (frame.Pixels[i] > 16 || frame.Pixels[i + 1] > 16 || frame.Pixels[i + 2] > 16) return false;
            }
        return true;
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
