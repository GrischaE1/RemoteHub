using System.Collections.Concurrent;
using System.IO;
using System.Management.Automation;
using System.Net.WebSockets;
using System.Security;
using System.Threading;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Services;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Remoting;

/// <summary>Picture settings for the remote desktop through the agent (frames per second and encoder quality).</summary>
public sealed record DesktopQualityPreset(string Id, string Label, int FramesPerSecond, int Quality)
{
    public static readonly DesktopQualityPreset[] All =
    [
        new("Low", "Slow connection – 15 fps", 15, 40),
        new("Admin", "Admin work – 30 fps (default)", 30, 60),
        new("High", "Sharp – 30 fps, best picture", 30, 85),
        new("Smooth", "Smooth – 60 fps, best picture", 60, 85),
    ];

    public static DesktopQualityPreset Default => All[1];

    public static DesktopQualityPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;
}

/// <summary>Where the tab is in the connect flow (what the view shows).</summary>
/// <summary>Where the tab is in the connect flow (what the view shows). Reconnecting: the connection dropped, the picture stays.</summary>
public enum DesktopPhase { Idle, Connecting, Choose, Waiting, Pin, Live, Reconnecting, Ended }

/// <summary>
/// The screen of a device through the RemoteHub Agent – for devices where RDP is off. Same trust as the agent's
/// PowerShell: pinned device certificate and a Windows sign-in; the device decides who may see or control the screen
/// and whether the user there is asked first.
/// </summary>
public sealed partial class AgentDesktopSession : SessionBase
{
    private AgentDesktopClient? _client;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private int _generation;
    private bool _closed;
    private string _lastClipboardSent = "";

    public AgentDesktopSession(Connection connection) : base(connection) => Title = connection.Name;

    public override string KindSymbol => "DesktopCursor24";
    public override string KindLabel => "Desktop (agent)";

    public PsTarget? Target { get; private set; }

    [ObservableProperty] private DesktopPhase _phase = DesktopPhase.Idle;
    [ObservableProperty] private DesktopHello? _hello;
    [ObservableProperty] private DesktopLayout? _layout;
    [ObservableProperty] private int _pinAttemptsLeft;
    /// <summary>While reconnecting: why the connection dropped and which attempt this is (shown under the headline).</summary>
    [ObservableProperty] private string _reconnectDetail = "";
    [ObservableProperty] private bool _viewOnly;
    /// <summary>The device shows the sign-in screen or a UAC prompt and this account may not see it.</summary>
    [ObservableProperty] private bool _secureDesktopHidden;
    [ObservableProperty] private WriteableBitmap? _screen;
    [ObservableProperty] private BitmapSource? _cursorImage;
    [ObservableProperty] private System.Windows.Input.Cursor? _cursor;
    [ObservableProperty] private Point _cursorHotspot;
    [ObservableProperty] private Point _cursorPosition;
    [ObservableProperty] private bool _cursorVisible;
    [ObservableProperty] private DesktopQualityPreset _quality = DesktopQualityPreset.Default;
    /// <summary>A short note over the picture (e.g. "Ctrl+Alt+Del isn't allowed"), empty when none.</summary>
    [ObservableProperty] private string _notice = "";
    /// <summary>Why the picture stands still (the Windows session is disconnected); empty when it isn't.</summary>
    [ObservableProperty] private string _paused = "";
    /// <summary>Connection info (frames, data rate, round trip, quality, capture), refreshed every 2 s while live.</summary>
    [ObservableProperty] private string _connectionInfo = "";

    private DesktopSessionInfo? _chosen;
    private string _chosenConsent = DesktopConsent.Ask;
    private string? _resumeToken;

    private bool Supports(string feature) => Hello?.Features?.Contains(feature) == true;
    private int ClipboardLevel => ViewOnly ? DesktopClipboardLevel.Off : Hello?.Clipboard ?? DesktopClipboardLevel.Off;

    public event Action? FocusRequested;

    public override void FocusSession() => FocusRequested?.Invoke();

    /// <summary>Connects and signs in; then the view asks what to show (<see cref="Choose"/>).</summary>
    public void Connect(PsTarget target, string credentialSource, Brush accent)
    {
        if (_closed || target.Agent is null) return;
        Target = target;
        Transport = "RemoteHub Agent";
        AccentBrush = accent;
        CredentialInfo = $"Credentials from {credentialSource} · {target.Display}";
        Drop();
        _resumeToken = null;
        _reconnect?.Cancel();
        State = SessionState.Connecting;
        Phase = DesktopPhase.Connecting;
        StatusText = $"Connecting to the RemoteHub Agent on {target.Host}…";
        var gen = ++_generation;
        Open(target).ContinueWith(t =>
        {
            if (_closed || gen != _generation) { if (t.IsCompletedSuccessfully) t.Result.Dispose(); return; }
            if (!t.IsCompletedSuccessfully)
            {
                Fail(t.Exception!.GetBaseException().Message);
                return;
            }
            Attach(t.Result, gen);
            Phase = DesktopPhase.Choose;
            StatusText = $"Signed in as {Hello!.Account}. Choose what to show.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static Task<AgentDesktopClient> Open(PsTarget target) => Task.Run(async () =>
    {
        var user = PsRemoting.AgentUserName(target.Credentials) ?? throw new InvalidOperationException("The RemoteHub Agent needs credentials (a Windows account of the device).");
        var cred = new PSCredential(user, Secure(target.Credentials.Password ?? ""));
        return await AgentDesktopClient.ConnectAsync(target.Host, target.Agent!, cred, CancellationToken.None);
    });

    private void Attach(AgentDesktopClient client, int gen)
    {
        _client = client;
        Hello = client.Hello;
        if (client.Hello.Version is { Length: > 0 } v && Connection.Agent is { } agent && agent.Version != v)
        {
            agent.Version = v; // saved with the next change (e.g. the last-connection time when it connects)
            Connection.RefreshLabels();
        }
        client.Received += m => OnMessage(gen, m);
        client.Closed += why => _ui.BeginInvoke(() => { if (gen == _generation) Lost(why); });
        client.Start();
    }

    // ---- automatic reconnect: the network dropped for a moment (Wi-Fi, VPN). The agent's token continues the session
    // without asking the user again, as long as the user who allowed it is still the one signed in.

    private CancellationTokenSource? _reconnect;
    public static readonly TimeSpan ReconnectFor = TimeSpan.FromSeconds(30);

    private async void Reconnect(string why)
    {
        if (Target is not { } target || _chosen is not { } chosen) return;
        _reconnect?.Cancel();
        var cancel = _reconnect = new CancellationTokenSource();
        Phase = DesktopPhase.Reconnecting;
        State = SessionState.Connecting;
        StatusText = $"The connection dropped ({why}). Reconnecting…";
        ReconnectDetail = Sentence(why);
        Services.DiagLog.Write("Agent desktop: connection lost – reconnecting");
        var until = DateTime.UtcNow + ReconnectFor;
        for (var attempt = 1; !cancel.IsCancellationRequested && !_closed; attempt++)
        {
            var gen = ++_generation;
            try
            {
                var client = await Open(target);
                if (cancel.IsCancellationRequested || _closed || gen != _generation) { client.Dispose(); return; }
                Attach(client, gen);
                // The same session – for the console, the console's current one (it may have moved).
                var session = chosen.Console ? Hello!.Sessions.FirstOrDefault(s => s.Console) ?? Hello.Sessions.FirstOrDefault(s => s.Id == chosen.Id)
                    : Hello!.Sessions.FirstOrDefault(s => s.Id == chosen.Id);
                if (session is null) { End(SessionState.Disconnected, "The connection dropped and the Windows session is gone."); return; }
                _chosen = session;
                StatusText = "Reconnected – continuing on the device…";
                client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session.Id, _chosenConsent, ViewOnly, Quality.FramesPerSecond, Quality.Quality,
                    Layout?.Monitor ?? 0, _resumeToken, ViewerFeatures), DesktopJson.Default.DesktopStart));
                _resumeToken = null; // single-use; the next Connected brings a new one
                return;
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested || _closed) return;
                if (DateTime.UtcNow >= until)
                {
                    End(SessionState.Disconnected, $"The connection dropped and couldn't be restored within {ReconnectFor.TotalSeconds:0} s: {ex.GetBaseException().Message}");
                    return;
                }
                StatusText = $"The connection dropped ({why}). Reconnecting… (attempt {attempt + 1})";
                ReconnectDetail = $"{Sentence(why)}\nAttempt {attempt + 1} – trying for up to {ReconnectFor.TotalSeconds:0} s.";
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>"Connection lost: the remote party …" → "The remote party …."</summary>
    private static string Sentence(string why)
    {
        var s = why.StartsWith("Connection lost: ", StringComparison.OrdinalIgnoreCase) ? why["Connection lost: ".Length..] : why;
        s = s.Trim().TrimEnd('.');
        return s.Length == 0 ? "" : char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }

    private static SecureString Secure(string s)
    {
        var ss = new SecureString();
        foreach (var ch in s) ss.AppendChar(ch);
        ss.MakeReadOnly();
        return ss;
    }

    /// <summary>Starts the chosen session (after <see cref="Phase"/> = Choose).</summary>
    public void Choose(DesktopSessionInfo session, string consent, bool viewOnly, DesktopQualityPreset quality)
    {
        if (_client is null || Phase != DesktopPhase.Choose) return;
        ViewOnly = viewOnly || Hello?.Rights.Control == false;
        Quality = quality;
        _chosen = session;
        _chosenConsent = consent;
        Phase = DesktopPhase.Waiting;
        StatusText = "Starting on the device…";
        _client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session.Id, consent, ViewOnly, quality.FramesPerSecond, quality.Quality, 0, null, ViewerFeatures),
            DesktopJson.Default.DesktopStart));
    }

    public void SendPin(string pin) =>
        _client?.Send(DesktopWire.Json(DesktopMessage.Pin, new DesktopPin(pin), DesktopJson.Default.DesktopPin));

    public void SetQuality(DesktopQualityPreset quality)
    {
        Quality = quality;
        SendSettings();
    }

    /// <summary>A monitor of the device, or <see cref="DesktopProtocol.AllMonitors"/>.</summary>
    public void ShowMonitor(int index)
    {
        if (Layout is null || index == Layout.Monitor) return;
        if (index == DesktopProtocol.AllMonitors && !Supports(DesktopFeatures.AllMonitors)) return;
        Layout = Layout with { Monitor = index };
        SendSettings();
    }

    private (int Width, int Height) _window;

    /// <summary>The device's resolution follows the tab (like RDP's dynamic resolution); restored when the session ends.</summary>
    [ObservableProperty] private bool _fitToWindow = true;

    partial void OnFitToWindowChanged(bool value) => SendSettings();

    /// <summary>Size of the picture area in device pixels (the view reports it, debounced).</summary>
    public void WindowSizeChanged(int width, int height)
    {
        if (_window == (width, height)) return;
        _window = (width, height);
        if (FitToWindow) SendSettings();
    }

    private void SendSettings()
    {
        if (Phase != DesktopPhase.Live) return;
        var (w, h) = FitToWindow && !ViewOnly && Layout?.Monitor != DesktopProtocol.AllMonitors ? _window : (0, 0);
        _client?.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(Layout?.Monitor ?? 0, Quality.FramesPerSecond, Quality.Quality, w & ~1, h & ~1),
            DesktopJson.Default.DesktopSettings));
    }

    // ---- this PC's clipboard: sent as soon as it changes (text), while the tab controls the device

    private System.Windows.Interop.HwndSource? _clipboardWatch;

    /// <summary>Whatever is copied on this PC goes to the device now.</summary>
    public bool SendsClipboard => _clipboardWatch is not null;

    private void WatchClipboard(bool on)
    {
        if (!on)
        {
            _clipboardWatch?.Dispose();
            _clipboardWatch = null;
            return;
        }
        if (_clipboardWatch is not null) return;
        var p = new System.Windows.Interop.HwndSourceParameters("RemoteHub clipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 }; // message-only
        _clipboardWatch = new System.Windows.Interop.HwndSource(p);
        _clipboardWatch.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x031D) SyncClipboardToRemote(); // WM_CLIPBOARDUPDATE
            return IntPtr.Zero;
        });
        AddClipboardFormatListener(_clipboardWatch.Handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>The device's monitor layout supports showing all monitors (newer agents).</summary>
    public bool CanShowAllMonitors => Supports(DesktopFeatures.AllMonitors);

    public void SendMouse(RemoteMouse m) { if (Phase == DesktopPhase.Live && !ViewOnly) _client?.Send(DesktopWire.Mouse(m)); }
    public void SendKey(RemoteKey k) { if (Phase == DesktopPhase.Live && !ViewOnly) _client?.Send(DesktopWire.Key(k)); }
    public void SendCtrlAltDel() { if (Phase == DesktopPhase.Live && !ViewOnly) _client?.Send(DesktopWire.Empty(DesktopMessage.SecureAttention)); }

    /// <summary>
    /// Sends this PC's clipboard when it changed (called when it changes and when the tab gets the focus): text, and with
    /// newer agents images and files – as far as the device's policy allows.
    /// </summary>
    public void SyncClipboardToRemote()
    {
        if (Phase != DesktopPhase.Live || ClipboardLevel < DesktopClipboardLevel.Text) return;
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _clipboardHandled) return; // unchanged, or what came from the device
        _clipboardHandled = sequence;
        if (ClipboardOffer.Instance.OwnsClipboard) return; // what the device offered (or a paste of it fetched): not sent back
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (text == _lastClipboardSent || text.Length > 1_000_000) return;
                _lastClipboardSent = text;
                _client?.Send(DesktopWire.Text(DesktopMessage.Clipboard, text));
                return;
            }
            if (!Supports(DesktopFeatures.Transfer)) return;
            if (Clipboard.ContainsImage() && ClipboardLevel >= DesktopClipboardLevel.Images && Clipboard.GetImage() is { } image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var png = new MemoryStream();
                encoder.Save(png);
                if (png.Length > DesktopProtocol.MaxTransferBytes) { ShowNotice("The copied image is too large for the remote desktop."); return; }
                OfferOrSend(ClipboardTransfer.Image(++_transferIds, png.ToArray(), png: true));
            }
            else if (Clipboard.ContainsFileDropList() && ClipboardLevel >= DesktopClipboardLevel.Files)
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                var id = ++_transferIds;
                Task.Run(() => ClipboardTransfer.Files(id, files, out var error) is { } o ? (o, (string?)null) : (null, error))
                    .ContinueWith(t =>
                    {
                        if (!t.IsCompletedSuccessfully) return;
                        var (o, error) = t.Result;
                        if (o is null) ShowNotice(error ?? "The copied files can't be sent.");
                        else OfferOrSend(o);
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            else if (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList())
                ShowNotice("Copying images and files isn't allowed on this device – only text.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException) { /* clipboard busy */ }
    }

    private uint _clipboardHandled;
    private int _transferIds;
    private CancellationTokenSource? _sending;

    // ---- progress of a copy (either way), shown on the picture

    /// <summary>A copy is shown with progress (from 1 MB; smaller ones only get the "copied" notice).</summary>
    [ObservableProperty] private bool _transferActive;
    [ObservableProperty] private double _transferProgress;
    [ObservableProperty] private string _transferTitle = "";
    /// <summary>"12.3 MB of 40 MB · 5.2 MB/s · about 6 s left", or what happens now.</summary>
    [ObservableProperty] private string _transferDetail = "";
    /// <summary>Only a copy to the device can be cancelled from here.</summary>
    [ObservableProperty] private bool _transferCancellable;

    private const long ProgressMinimum = 1024 * 1024;
    private int _outgoingId;
    private string _outgoingWhat = "";
    private DispatcherTimer? _receiptTimer;

    public void CancelTransfer() => _sending?.Cancel();

    private void ShowTransfer(string title, double progress, string detail, bool cancellable)
    {
        (TransferTitle, TransferProgress, TransferDetail, TransferCancellable) = (title, progress, detail, cancellable);
        TransferActive = true;
    }

    private void EndTransfer(string? notice)
    {
        _receiptTimer?.Stop();
        TransferActive = false;
        if (notice is not null) ShowNotice(notice);
    }

    /// <summary>Speed (smoothed like the file transfer list) and time left for one copy.</summary>
    internal sealed class TransferMeter(long total)
    {
        private readonly DateTime _started = DateTime.UtcNow;
        private DateTime _sampleAt;
        private long _sampleBytes;
        private double _rate;

        public string Detail(long done, DateTime? now = null)
        {
            var at = now ?? DateTime.UtcNow;
            if (_sampleAt == default) (_sampleAt, _sampleBytes) = (at, done);
            else if ((at - _sampleAt).TotalSeconds >= 0.25)
            {
                var current = (done - _sampleBytes) / (at - _sampleAt).TotalSeconds;
                _rate = _rate == 0 ? current : _rate * 0.6 + current * 0.4;
                (_sampleAt, _sampleBytes) = (at, done);
            }
            var parts = new List<string> { $"{TransferItem.Size(done)} of {TransferItem.Size(total)}" };
            if (_rate > 0)
            {
                parts.Add($"{TransferItem.Size((long)_rate)}/s");
                parts.Add(Left((total - done) / _rate));
            }
            return string.Join(" · ", parts);
        }

        public string Done(DateTime? now = null) => $"Done · {TransferItem.Size(total)} in {((now ?? DateTime.UtcNow) - _started).TotalSeconds:0.#} s";

        internal static string Left(double seconds) => seconds switch
        {
            < 1 => "almost done",
            < 60 => $"about {Math.Ceiling(seconds):0} s left",
            _ => $"about {Math.Ceiling(seconds / 60):0} min left",
        };
    }

    /// <summary>
    /// To the device in the background, without crowding out mouse and keys (at most a few chunks queued). "Copied" is
    /// said when the device confirms it has the files on its clipboard (newer agents), not when the last byte left here.
    /// </summary>
    private void SendTransfer(ClipboardTransfer.Outgoing o)
    {
        if (_client is not { } client) return;
        _sending?.Cancel();
        var cancel = _sending = new CancellationTokenSource();
        var what = o.Transfer.Kind == DesktopTransferKind.Image ? "the image" : o.Transfer.Items.Count(i => !i.Folder) is var n && n == 1 ? "1 file" : $"{n} files";
        var total = o.Transfer.Total;
        var meter = new TransferMeter(total);
        var receipt = Supports(DesktopFeatures.TransferReceipt);
        (_outgoingId, _outgoingWhat) = (o.Transfer.Id, what);
        if (total >= ProgressMinimum) ShowTransfer($"Copying {what} to the device", 0, "Starting…", cancellable: true);
        else EndTransfer(null);
        Task.Run(async () =>
        {
            try
            {
                client.Send(DesktopWire.Json(DesktopMessage.TransferStart, o.Transfer, DesktopJson.Default.DesktopTransfer));
                long sent = 0;
                var shown = DateTime.UtcNow;
                foreach (var chunk in ClipboardTransfer.Chunks(o))
                {
                    while (client.Queued > 8 && !cancel.IsCancellationRequested) await Task.Delay(10);
                    if (cancel.IsCancellationRequested)
                    {
                        client.Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(o.Transfer.Id, false, "replaced"), DesktopJson.Default.DesktopTransferEnd));
                        _ = _ui.BeginInvoke(() => { if (_outgoingId == o.Transfer.Id) EndTransfer("Cancelled – nothing was copied to the device."); });
                        return;
                    }
                    client.Send(chunk);
                    sent += chunk.Length - DesktopWire.TransferHeaderSize;
                    if (total >= ProgressMinimum && (DateTime.UtcNow - shown).TotalMilliseconds >= 250)
                    {
                        shown = DateTime.UtcNow;
                        var (progress, detail) = (sent / (double)total, meter.Detail(sent));
                        _ = _ui.BeginInvoke(() => { if (_outgoingId == o.Transfer.Id && TransferActive) (TransferProgress, TransferDetail) = (progress, detail); });
                    }
                }
                while (client.Queued > 0 && !cancel.IsCancellationRequested) await Task.Delay(20);
                var done = meter.Done();
                _ = _ui.BeginInvoke(() =>
                {
                    if (_outgoingId != o.Transfer.Id) return;
                    if (!receipt) { EndTransfer($"Copied {what} to the device – paste it there."); return; }
                    if (TransferActive) (TransferProgress, TransferDetail, TransferCancellable) = (1, done + " · waiting for the device…", false);
                    // The device answers when the files are on its clipboard; no answer means they didn't get there.
                    _receiptTimer?.Stop();
                    _receiptTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    _receiptTimer.Tick += (_, _) => { if (_outgoingId == o.Transfer.Id) EndTransfer($"Failed – the device didn't confirm the copy of {what}; it may not have arrived."); };
                    _receiptTimer.Start();
                });
            }
            catch (Exception ex)
            {
                client.Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(o.Transfer.Id, false, ex.Message), DesktopJson.Default.DesktopTransferEnd));
                var why = ex.Message;
                _ = _ui.BeginInvoke(() => { if (_outgoingId == o.Transfer.Id) EndTransfer("Failed – the copied files can't be sent: " + why); });
            }
        });
    }

    private ClipboardTransfer.Outgoing? _offered;

    /// <summary>
    /// Copied on this PC: newer agents get names and sizes only and offer them on the device's clipboard – the data is
    /// sent when someone pastes there (<see cref="DesktopMessage.TransferRequest"/>). Older agents get it right away.
    /// </summary>
    private void OfferOrSend(ClipboardTransfer.Outgoing o)
    {
        if (!Supports(DesktopFeatures.TransferOnPaste)) { SendTransfer(o); return; }
        if (_client is not { } client) return;
        _offered = o;
        client.Send(DesktopWire.Json(DesktopMessage.TransferOffer, o.Transfer, DesktopJson.Default.DesktopTransfer));
    }

    /// <summary>Pasted on the device: the offered copy goes now.</summary>
    private void OnTransferRequest(int id)
    {
        if (_offered is { } o && o.Transfer.Id == id) SendTransfer(o);
    }

    /// <summary>The device's answer about a copy to it: on its clipboard, or why not.</summary>
    private void OnTransferReceipt(DesktopTransferEnd end)
    {
        if (end.Id != _outgoingId)
        {
            if (!end.Ok && _offered?.Transfer.Id == end.Id) ShowNotice("Failed – " + end.Reason); // the offer was refused
            return;
        }
        EndTransfer(end.Ok ? $"Copied {_outgoingWhat} to the device – paste it there." : "Failed – " + end.Reason);
    }

    // ---- from the device: an image or files, put together here, then on this PC's clipboard

    /// <summary>Files copied on a device land here (one folder per copy) until they're pasted.</summary>
    public static string ReceivedRoot => Path.Combine(Path.GetTempPath(), "RemoteHub", "Clipboard");

    /// <summary>
    /// Removes copies older than <paramref name="age"/> – long enough to have been pasted; newer ones may still be on the
    /// clipboard. Called when RemoteHub starts and closes.
    /// </summary>
    public static void CleanReceived(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(ReceivedRoot)) return;
            foreach (var dir in new DirectoryInfo(ReceivedRoot).GetDirectories())
                if (DateTime.UtcNow - dir.CreationTimeUtc >= age)
                    try { dir.Delete(true); } catch { /* in use */ }
        }
        catch { /* best effort */ }
    }

    private TransferReceiver? _receiving;
    private TransferMeter? _receiveMeter;
    private DateTime _receiveShown;
    /// <summary>The receive thread fills <see cref="_receiving"/>; closing the tab (UI thread) disposes it.</summary>
    private readonly object _receiveGate = new();

    private void OnTransferStart(DesktopTransfer t)
    {
        lock (_receiveGate) StartReceiving(t);
    }

    private void StartReceiving(DesktopTransfer t)
    {
        _receiving?.Dispose();
        _receiving = null;
        if (ClipboardLevel < (t.Kind == DesktopTransferKind.Image ? DesktopClipboardLevel.Images : DesktopClipboardLevel.Files)) return;
        try
        {
            var folder = t.Kind == DesktopTransferKind.Files ? Path.Combine(ReceivedRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")) : null;
            _receiving = new TransferReceiver(t, folder);
            _receiveMeter = new TransferMeter(t.Total);
            _receiveShown = DateTime.UtcNow;
            if (t.Total >= ProgressMinimum)
            {
                var what = t.Kind == DesktopTransferKind.Image ? "the image" : t.Items.Count(i => !i.Folder) is var n && n == 1 ? "1 file" : $"{n} files";
                _ui.BeginInvoke(() => ShowTransfer($"Copying {what} from the device", 0, "Starting…", cancellable: false));
            }
            if (_receiving.Complete) FinishReceiving();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _receiving = null;
            var why = ex.Message;
            PasteFromDeviceFailed();
            _ui.BeginInvoke(() => { if (!TransferCancellable) TransferActive = false; ShowNotice("Failed – what was copied on the device can't be taken: " + why); });
        }
    }

    private void OnTransferData(byte[] m)
    {
        lock (_receiveGate) ReceiveData(m);
    }

    private void ReceiveData(byte[] m)
    {
        if (_receiving is not { } r) return;
        var (id, offset) = DesktopWire.ReadTransferData(m);
        if (id != r.Transfer.Id) return;
        try
        {
            r.Add(offset, m.AsSpan(DesktopWire.TransferHeaderSize));
            if (r.Complete) FinishReceiving();
            else if (r.Transfer.Total >= ProgressMinimum && _receiveMeter is { } meter && (DateTime.UtcNow - _receiveShown).TotalMilliseconds >= 250)
            {
                _receiveShown = DateTime.UtcNow;
                var received = (long)(r.Progress * r.Transfer.Total);
                var (progress, detail) = (r.Progress, meter.Detail(received));
                _ui.BeginInvoke(() => { if (TransferActive && !TransferCancellable) (TransferProgress, TransferDetail) = (progress, detail); });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            r.Dispose();
            _receiving = null;
            var why = ex.Message;
            PasteFromDeviceFailed();
            _ui.BeginInvoke(() => { if (!TransferCancellable) TransferActive = false; ShowNotice("Failed – what was copied on the device can't be taken: " + why); });
        }
    }

    private void FinishReceiving()
    {
        var r = _receiving!;
        _receiving = null;
        var image = r.Transfer.Kind == DesktopTransferKind.Image ? r.Image : null;
        var files = r.Transfer.Kind == DesktopTransferKind.Files ? r.TopLevel : null;
        var count = r.Transfer.Items.Count(i => !i.Folder);
        var png = r.IsPng;
        var items = r.Transfer.Kind == DesktopTransferKind.Files ? r.Targets : null;
        r.Dispose();
        // Asked for by a paste on this PC: the waiting paste gets it (it's on the clipboard through that paste).
        if (_pastingFromDevice is { } p && p.Id == r.Transfer.Id)
        {
            p.Done.TrySetResult(new OfferedContent(files, image, png, items));
            _ui.BeginInvoke(() =>
            {
                if (!TransferCancellable) TransferActive = false;
                ShowNotice(image is not null ? "Pasted the image from the device." : count == 1 ? "Pasted 1 file from the device." : $"Pasted {count} files from the device.");
            });
            return;
        }
        _ui.BeginInvoke(() =>
        {
            if (!TransferCancellable) TransferActive = false; // the progress of this copy, not of one to the device
            try
            {
                if (image is not null)
                {
                    var decoded = BitmapDecoder.Create(new MemoryStream(image), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    Clipboard.SetImage(decoded);
                    ShowNotice("Copied the image from the device – paste it here.");
                }
                else if (files is not null)
                {
                    var list = new System.Collections.Specialized.StringCollection();
                    list.AddRange([.. files]);
                    Clipboard.SetFileDropList(list);
                    ShowNotice(count == 1 ? "Copied 1 file from the device – paste it here." : $"Copied {count} files from the device – paste them here.");
                }
                _clipboardHandled = GetClipboardSequenceNumber(); // not sent back
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or NotSupportedException or IOException or InvalidOperationException)
            {
                ShowNotice("What was copied on the device can't be put on this PC's clipboard: " + ex.Message);
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    // ---- copied on the device: offered on this PC's clipboard, fetched when pasted here

    /// <summary>What this RemoteHub supports, told to the agent at the start (it then offers instead of sending at once).</summary>
    private static readonly string[] ViewerFeatures = [DesktopFeatures.TransferOnPaste];

    private sealed record PastingFromDevice(int Id, TaskCompletionSource<OfferedContent?> Done);
    private volatile PastingFromDevice? _pastingFromDevice;

    /// <summary>Copied on the device: names and sizes only. This PC's clipboard offers it; a paste here fetches it.</summary>
    private void OnDeviceOffer(DesktopTransfer t)
    {
        if (ClipboardLevel < (t.Kind == DesktopTransferKind.Image ? DesktopClipboardLevel.Images : DesktopClipboardLevel.Files)) return;
        ClipboardOffer.Instance.Offer(t, paster => FetchFromDevice(t, paster));
        _ui.BeginInvoke(() => _clipboardHandled = GetClipboardSequenceNumber()); // our own offer: not sent back
    }

    /// <summary>A paste on this PC (on the offer's own thread): the device sends now; the paste waits until it's here.</summary>
    private OfferedContent? FetchFromDevice(DesktopTransfer t, string paster)
    {
        if (ClipboardOffer.BackgroundReader(paster) || _client is not { } client || _closed) return null;
        var pasting = _pastingFromDevice = new PastingFromDevice(t.Id, new TaskCompletionSource<OfferedContent?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            Services.DiagLog.Write($"Agent desktop: {paster} pastes what was copied on the device – fetching it");
            client.Send(DesktopWire.Text(DesktopMessage.TransferRequest, t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var limit = TimeSpan.FromSeconds(Math.Min(3600, 60 + t.Total / (256 * 1024.0)));
            return pasting.Done.Task.Wait(limit) ? pasting.Done.Task.Result : null;
        }
        finally { if (_pastingFromDevice == pasting) _pastingFromDevice = null; }
    }

    /// <summary>The fetch for a paste failed (or the connection ended): the paste gets nothing.</summary>
    private void PasteFromDeviceFailed(int? id = null)
    {
        if (_pastingFromDevice is { } p && (id is null || p.Id == id)) p.Done.TrySetResult(null);
    }

    // ---- connection info: own count of frames and data, the round trip through the agent, the device's stats

    private DispatcherTimer? _infoTimer;
    private int _framesShown;
    private long _videoBytes;
    private double _roundTripMs = -1;
    private DesktopStats? _stats;
    private readonly System.Diagnostics.Stopwatch _infoClock = new();

    private void InfoTimer(bool on)
    {
        if (!on) { _infoTimer?.Stop(); _infoTimer = null; return; }
        if (_infoTimer is not null) return;
        _infoClock.Restart();
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _infoTimer.Tick += (_, _) =>
        {
            if (Supports(DesktopFeatures.Ping)) _client?.Send(DesktopWire.Ping(System.Diagnostics.Stopwatch.GetTimestamp()));
            var seconds = Math.Max(0.5, _infoClock.Elapsed.TotalSeconds);
            _infoClock.Restart();
            var fps = Interlocked.Exchange(ref _framesShown, 0) / seconds;
            var mbit = Interlocked.Exchange(ref _videoBytes, 0) * 8 / seconds / 1_000_000;
            var parts = new List<string> { $"{fps:0} fps", $"{mbit:0.0} Mbit/s", _roundTripMs >= 0 ? $"{_roundTripMs:0} ms round trip" : "round trip: Data unavailable" };
            if (_stats is { } st)
            {
                parts.Add($"quality {st.Quality}");
                if (st.Width > 0) parts.Add($"{st.Width}×{st.Height}");
                if (st.Capture.Length > 0) parts.Add(st.Capture);
            }
            ConnectionInfo = string.Join("  ·  ", parts) + (_stats is { Fit.Length: > 0 } s2 ? $"\nResolution: {s2.Fit}" : "");
        };
        _infoTimer.Start();
    }

    // ---- messages from the agent (receive thread)

    private void OnMessage(int gen, byte[] m)
    {
        switch (DesktopWire.TypeOf(m))
        {
            case DesktopMessage.Video:
                Interlocked.Add(ref _videoBytes, m.Length);
                _decoder?.Enqueue(m);
                break;
            case DesktopMessage.Pong:
                var rtt = (System.Diagnostics.Stopwatch.GetTimestamp() - DesktopWire.ReadPing(m)) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _roundTripMs = rtt;
                break;
            case DesktopMessage.Stats:
                _stats = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopStats);
                break;
            case DesktopMessage.TransferStart:
                OnTransferStart(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer));
                break;
            case DesktopMessage.TransferData:
                OnTransferData(m);
                break;
            case DesktopMessage.TransferOffer:
                OnDeviceOffer(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer));
                break;
            case DesktopMessage.TransferRequest:
                if (int.TryParse(DesktopWire.ReadText(m), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var requested))
                    _ui.BeginInvoke(() => { if (gen == _generation) OnTransferRequest(requested); });
                break;
            case DesktopMessage.TransferEnd:
                var end = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                if (end.ToDevice)
                {
                    _ui.BeginInvoke(() => { if (gen == _generation) OnTransferReceipt(end); });
                    break;
                }
                lock (_receiveGate)
                    if (_receiving?.Transfer.Id == end.Id) { _receiving.Dispose(); _receiving = null; }
                if (!end.Ok) PasteFromDeviceFailed(end.Id);
                if (!end.Ok && end.Reason.Length > 0 && end.Reason != "replaced")
                    _ui.BeginInvoke(() => { if (gen == _generation) ShowNotice(end.Reason); });
                break;
            case DesktopMessage.Status:
                var status = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopStatus);
                _ui.BeginInvoke(() => { if (gen == _generation) OnStatus(status); });
                break;
            case DesktopMessage.Layout:
                var layout = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopLayout);
                _ui.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    Layout = layout;
                    SecureDesktopHidden = layout.SecureDesktop && Hello?.Rights.SecureDesktop == false;
                });
                break;
            case DesktopMessage.CursorShape:
                if (m.Length < DesktopWire.CursorShapeHeaderSize) break;
                var (w, h, hx, hy) = DesktopWire.ReadCursorShape(m);
                // A shape that doesn't fit the message is ignored – it must not end the session.
                if (w is <= 0 or > 1024 || h is <= 0 or > 1024 || m.Length < DesktopWire.CursorShapeHeaderSize + w * h * 4) break;
                var bgra = m.AsSpan(DesktopWire.CursorShapeHeaderSize, w * h * 4).ToArray();
                _ui.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                    bmp.Freeze();
                    CursorImage = bmp;
                    CursorHotspot = new Point(hx, hy);
                    Cursor = CursorFiles.Create(w, h, hx, hy, bgra);
                });
                break;
            case DesktopMessage.CursorPosition:
                var c = DesktopWire.ReadCursor(m);
                _ui.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    CursorPosition = new Point(c.X, c.Y);
                    CursorVisible = c.Visible;
                });
                break;
            case DesktopMessage.Clipboard:
                var text = DesktopWire.ReadText(m);
                _ui.BeginInvoke(() =>
                {
                    if (gen != _generation) return;
                    try
                    {
                        _lastClipboardSent = text;
                        Clipboard.SetText(text);
                        _clipboardHandled = GetClipboardSequenceNumber();
                    }
                    catch (Exception) { /* clipboard busy */ }
                });
                break;
        }
    }

    private void OnStatus(DesktopStatus s)
    {
        switch (s.State)
        {
            case DesktopStatusState.Starting when Phase == DesktopPhase.Reconnecting:
                StatusText = s.Message; // the dimmed picture stays until the device continues
                break;
            case DesktopStatusState.Starting:
            case DesktopStatusState.WaitingForUser:
                Phase = DesktopPhase.Waiting;
                StatusText = s.Message;
                break;
            case DesktopStatusState.EnterPin:
                Phase = DesktopPhase.Pin;
                PinAttemptsLeft = s.PinAttemptsLeft;
                StatusText = s.Message;
                break;
            case DesktopStatusState.Paused when Phase == DesktopPhase.Live:
                Paused = s.Message;
                break;
            case DesktopStatusState.Connected when Phase == DesktopPhase.Live:
                Paused = "";
                ShowNotice(s.Message); // e.g. Ctrl+Alt+Del refused
                break;
            case DesktopStatusState.Connected:
                if (s.Resume is { Length: > 0 }) _resumeToken = s.Resume;
                var wasReconnecting = Phase == DesktopPhase.Reconnecting;
                _decoder ??= new FrameDecoder(this);
                Phase = DesktopPhase.Live;
                State = SessionState.Connected;
                StatusText = s.Message;
                SendSettings(); // resolution to the window right away
                if (!ViewOnly) WatchClipboard(true);
                InfoTimer(true);
                _clipboardHandled = 0;
                SyncClipboardToRemote();
                if (wasReconnecting) ShowNotice("Reconnected.");
                break;
            case DesktopStatusState.Denied:
            case DesktopStatusState.Failed:
            case DesktopStatusState.Ended:
                End(s.State == DesktopStatusState.Ended ? SessionState.Disconnected : SessionState.Failed, s.Message);
                break;
        }
    }

    private DispatcherTimer? _noticeTimer;

    private void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _noticeTimer.Tick += (_, _) => { Notice = ""; _noticeTimer?.Stop(); };
        _noticeTimer.Start();
    }

    private void Lost(string why)
    {
        if (Phase == DesktopPhase.Ended) return;
        // Live (or already reconnecting) with a token from the agent: try to continue for a while.
        if (Phase is DesktopPhase.Live or DesktopPhase.Reconnecting && _resumeToken is not null && Supports(DesktopFeatures.Resume) && !_closed)
        {
            DropClient();
            Reconnect(why);
            return;
        }
        End(Phase == DesktopPhase.Live ? SessionState.Disconnected : SessionState.Failed, why);
    }

    private void Fail(string why) => End(SessionState.Failed, why);

    private void End(SessionState state, string why)
    {
        Paused = "";
        Drop();
        Phase = DesktopPhase.Ended;
        State = state;
        StatusText = why;
    }

    public override void Disconnect()
    {
        _generation++;
        _reconnect?.Cancel();
        _resumeToken = null;
        if (Drop() || State == SessionState.Connecting)
        {
            Phase = DesktopPhase.Ended;
            State = SessionState.Disconnected;
            StatusText = "Disconnected.";
        }
    }

    private bool Drop()
    {
        _decoder?.Dispose();
        _decoder = null;
        InfoTimer(false);
        ConnectionInfo = "";
        _stats = null;
        _roundTripMs = -1;
        PasteFromDeviceFailed(); // a paste waiting for the device gets nothing
        return DropClient();
    }

    /// <summary>Only the connection (a reconnect keeps the picture and the decoder).</summary>
    private bool DropClient()
    {
        WatchClipboard(false);
        _sending?.Cancel();
        lock (_receiveGate)
        {
            _receiving?.Dispose();
            _receiving = null;
        }
        var c = _client;
        _client = null;
        if (c is null) return false;
        c.Dispose();
        return true;
    }

    public override void Close()
    {
        if (_closed) return;
        Dispose();
        RaiseClosed();
    }

    public override void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _generation++;
        _reconnect?.Cancel();
        Drop();
    }

    // ---- video: decoded on its own thread, shown on the UI thread, acknowledged right after decoding

    private FrameDecoder? _decoder;

    private sealed class FrameDecoder : IDisposable
    {
        private readonly AgentDesktopSession _session;
        private readonly BlockingCollection<byte[]> _queue = new(16);
        private readonly Thread _thread;
        private readonly object _lock = new();
        private byte[] _back = [];
        private int _w, _h;
        private bool _pending, _waitForKeyFrame;

        public FrameDecoder(AgentDesktopSession session)
        {
            _session = session;
            _thread = new Thread(Run) { IsBackground = true, Name = "Desktop decoder" };
            _thread.Start();
        }

        public void Enqueue(byte[] m)
        {
            try { _queue.Add(m); } catch (InvalidOperationException) { /* closing */ }
        }

        private unsafe void Run()
        {
            H264Decoder? decoder = null;
            try
            {
                decoder = new H264Decoder();
                foreach (var m in _queue.GetConsumingEnumerable())
                {
                    var h = DesktopWire.ReadVideo(m);
                    try
                    {
                        if (_waitForKeyFrame && !h.KeyFrame) continue;
                        _waitForKeyFrame = false;
                        decoder.Decode(m.AsSpan(DesktopWire.VideoHeaderSize), h.Width, h.Height, (bgra, w, hh) => Publish(bgra, w, hh));
                    }
                    catch (Exception ex)
                    {
                        Services.DiagLog.Write("Agent desktop: frame could not be decoded – " + ex.Message);
                        try { decoder.Flush(); } catch { /* new decoder below */ }
                        _waitForKeyFrame = true;
                        _session._client?.Send(DesktopWire.Empty(DesktopMessage.KeyFrameRequest));
                    }
                    finally { _session._client?.Send(DesktopWire.Ack(h.FrameId)); }
                }
            }
            catch (Exception ex)
            {
                var why = ex.Message;
                _session._ui.BeginInvoke(() => _session.Fail("The picture can't be shown on this PC: " + why));
            }
            finally { decoder?.Dispose(); }
        }

        private unsafe void Publish(byte* bgra, int w, int h)
        {
            lock (_lock)
            {
                if (_back.Length < w * h * 4) _back = new byte[w * h * 4];
                new ReadOnlySpan<byte>(bgra, w * h * 4).CopyTo(_back);
                (_w, _h) = (w, h);
                Interlocked.Increment(ref _session._framesShown);
                if (_pending) return;
                _pending = true;
            }
            _session._ui.BeginInvoke(Show, DispatcherPriority.Render);
        }

        private void Show()
        {
            lock (_lock)
            {
                _pending = false;
                if (_w == 0) return;
                var bmp = _session.Screen;
                if (bmp is null || bmp.PixelWidth != _w || bmp.PixelHeight != _h)
                    _session.Screen = bmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);
                bmp.WritePixels(new Int32Rect(0, 0, _w, _h), _back, _w * 4, 0);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}

/// <summary>
/// The WebSocket to the agent's <c>/v1/desktop</c>: pinned TLS and sign-in like the PowerShell transport. Messages go
/// out in order through one queue, so mouse and keys never block the UI.
/// </summary>
public sealed class AgentDesktopClient : IDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private int _closed;

    private AgentDesktopClient(ClientWebSocket ws, DesktopHello hello)
    {
        _ws = ws;
        Hello = hello;
    }

    public DesktopHello Hello { get; }

    /// <summary>Every message from the agent (receive thread).</summary>
    public event Action<byte[]>? Received;
    /// <summary>The connection ended (receive thread); the text says why.</summary>
    public event Action<string>? Closed;

    public static async Task<AgentDesktopClient> ConnectAsync(string host, AgentEndpoint endpoint, PSCredential credential, CancellationToken ct)
    {
        var info = new AgentConnectionInfo(host, endpoint, credential);
        ClientWebSocket ws;
        try { ws = await Task.Run(() => AgentClientTransport.Connect(info, ct, DesktopProtocol.Path), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex.InnerException is WebSocketException && ex.Message.Contains("isn't available"))
        {
            // 404: an agent without the remote desktop (older version), or turned off by policy – the health answer tells which.
            AgentHealth? health = null;
            try { health = await AgentInstaller.HealthAsync(host, endpoint.Port, endpoint.Fingerprint, ct).ConfigureAwait(false); } catch { /* keep the general message */ }
            if (health is { Features: null })
                throw new InvalidOperationException($"The RemoteHub Agent on {host} (version {health.Version}) doesn't have the remote desktop yet. " +
                                                    "Update it: right-click the connection → Update RemoteHub Agent.", ex);
            if (health?.Features is { } f && !f.Contains(AgentFeatures.Desktop))
                throw new InvalidOperationException($"The remote desktop is turned off by a policy on {host} (DesktopEnabled in HKLM\\SOFTWARE\\Policies\\RemoteHub\\Agent).", ex);
            throw;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var first = await ReceiveAsync(ws, timeout.Token).ConfigureAwait(false);
            if (first is null || DesktopWire.TypeOf(first) != DesktopMessage.Hello)
                throw new InvalidDataException("The RemoteHub Agent didn't answer as expected.");
            var hello = DesktopWire.ReadJson(first, DesktopJson.Default.DesktopHello);
            if (hello.Protocol != DesktopProtocol.Version)
                throw new InvalidDataException($"The RemoteHub Agent speaks remote desktop version {hello.Protocol}, this RemoteHub version {DesktopProtocol.Version}. Update both.");
            return new AgentDesktopClient(ws, hello);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary>Starts receiving and sending (after the events are attached).</summary>
    public void Start()
    {
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(SendLoop);
    }

    public void Send(byte[] message)
    {
        if (_out.Writer.TryWrite(message)) Interlocked.Increment(ref _queued);
    }

    private int _queued;

    /// <summary>
    /// Messages waiting to go out (clipboard transfers wait for it to be small). Counted here: the channel (single reader)
    /// can't count – asking it threw, which silently stopped every copy to the device.
    /// </summary>
    public int Queued => Volatile.Read(ref _queued);

    private async Task SendLoop()
    {
        try
        {
            await foreach (var m in _out.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _ws.SendAsync(m, WebSocketMessageType.Binary, true, _cts.Token).ConfigureAwait(false);
                Interlocked.Decrement(ref _queued);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Close("Connection lost: " + ex.GetBaseException().Message); }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (await ReceiveAsync(_ws, _cts.Token).ConfigureAwait(false) is { } m)
                Received?.Invoke(m);
            // Answer the agent's close (so it can end the connection cleanly instead of resetting it); nothing is sent after it.
            _out.Writer.TryComplete();
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token).ConfigureAwait(false); }
            catch { /* already gone */ }
            Close("The RemoteHub Agent ended the session.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Close("Connection lost: " + ex.GetBaseException().Message); }
    }

    private static async Task<byte[]?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (message.Length > DesktopProtocol.MaxMessageBytes) throw new InvalidDataException("Message from the agent too large.");
            if (r.EndOfMessage && message.Length > 0) return message.ToArray();
        }
    }

    private void Close(string why)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(why);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        _out.Writer.TryComplete();
        try
        {
            if (_ws.State == WebSocketState.Open)
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(1000).Token).Wait(1000);
        }
        catch { /* already gone */ }
        _cts.Cancel();
        _ws.Dispose();
    }
}

/// <summary>Builds a Windows cursor (.cur in memory) from BGRA pixels, so this PC shows the remote pointer shape.</summary>
internal static class CursorFiles
{
    public static System.Windows.Input.Cursor? Create(int w, int h, int hotX, int hotY, byte[] bgra)
    {
        if (w is < 1 or > 256 || h is < 1 or > 256) return null;
        try
        {
            var maskStride = (w + 31) / 32 * 4;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((short)0); bw.Write((short)2); bw.Write((short)1);                // ICONDIR: cursor, 1 image
            bw.Write((byte)(w == 256 ? 0 : w)); bw.Write((byte)(h == 256 ? 0 : h));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)hotX); bw.Write((short)hotY);
            bw.Write(40 + w * h * 4 + maskStride * h); bw.Write(22);                     // size, offset
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32); // BITMAPINFOHEADER
            bw.Write(0); bw.Write(w * h * 4 + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (var y = h - 1; y >= 0; y--) bw.Write(bgra, y * w * 4, w * 4);          // bottom-up colour
            bw.Write(new byte[maskStride * h]);                                          // AND mask unused (alpha)
            bw.Flush();
            ms.Position = 0;
            return new System.Windows.Input.Cursor(ms);
        }
        catch (Exception) { return null; }
    }
}
