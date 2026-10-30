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
    /// <summary>Service → helper: the admin's privacy screen (<see cref="DesktopPrivacyScreen"/>; the service checked the right and policy).</summary>
    PrivacyScreen = 109,
    /// <summary>
    /// Helper → service: the apps the user at the device hides (<see cref="HelperHiddenApps"/>) – kept for a helper started
    /// later and a continued connection. Never passed on to RemoteHub: the admin learns how many, not which.
    /// </summary>
    HiddenApps = 110,
    /// <summary>Helper → service: the result of <see cref="SecureAttention"/> (text: empty when sent, else why not).</summary>
    SecureAttentionResult = 106,
    /// <summary>Helper → service: a line for the agent's log (capture method, resolution change).</summary>
    Log = 107,
    /// <summary>
    /// Helper → service: the session needs the virtual monitor (no usable resolution – no monitor connected) at this size
    /// (text: "width height"), or the viewer's window changed size while it's on.
    /// </summary>
    DisplayRequest = 108,
    /// <summary>
    /// Service → helper, in an app tab: the app was started (<see cref="HelperApp"/> with its job) – from now on only its
    /// windows are shown on the tab's monitor and input goes to them.
    /// </summary>
    App = 111,
    /// <summary>Helper → service, in an app tab: the app's last window closed and its job has no process left (no payload).</summary>
    AppClosed = 112,
    /// <summary>Service → helper, in an app tab: the tab was closed (<see cref="DesktopAppClose"/>) – ask the app to close, or move it to the primary monitor.</summary>
    AppClose = 113,
}

/// <param name="HoldUserDesktop">
/// Nobody in this session allowed the connection (it started on the lock or sign-in screen): only the secure desktop is
/// shown; as soon as a user's desktop appears, the helper stops and reports <see cref="HelperMessage.UserDesktop"/>.
/// </param>
/// <param name="Clipboard">What may go through the clipboard (<see cref="DesktopClipboardLevel"/>; 0 when viewing).</param>
/// <param name="VirtualDisplay">The virtual monitor (<see cref="VirtualDisplayModes"/>) if it's installed, else "".</param>
/// <param name="OfferToViewer">RemoteHub takes offers: what is copied here goes only when pasted there (TransferOffer / TransferRequest).</param>
/// <param name="UserHidden">
/// The user at the device hid their screen with the helper before this one (<see cref="DesktopScreenHide"/>): it stays
/// hidden from the first frame on.
/// </param>
/// <param name="HiddenApps">The apps the user hid with the helper before this one (<see cref="AppIds"/>): hidden from the first frame on.</param>
/// <param name="AlwaysHidden">Apps the device's policy always hides (policy HiddenApps); the user can't show them.</param>
/// <param name="SignedInRemotely">
/// The session automatic sign-in created for the admin: no banner (nobody at the device owns the session, there is nothing
/// for them to hide or end); the privacy screen says who is working.
/// </param>
/// <param name="PrivacyHeading">The privacy screen's heading from the device's policy (PrivacyScreenMessage); null: the built-in one.</param>
internal sealed record HelperInit(string Account, string Device, string Consent, string Pin, int TimeoutSeconds, bool ViewOnly,
    bool SecureDesktop, int FramesPerSecond, int Quality, int Monitor, bool HoldUserDesktop = false, int Clipboard = DesktopClipboardLevel.Text,
    bool Gdi = false, string VirtualDisplay = "", bool OfferToViewer = false, string UserHidden = DesktopScreenHide.None,
    string[]? HiddenApps = null, string[]? AlwaysHidden = null, bool SignedInRemotely = false, string? PrivacyHeading = null,
    HelperApp? App = null);

/// <summary>
/// An app tab (docs/app-tabs-design.md): the place of its virtual monitor, and – once the service started the app – the
/// name of the job its processes are in. Before that, the tab's monitor shows nothing and no input is let through.
/// </summary>
/// <param name="Job">The job's name (<c>Global\RemoteHub-AppTab-…</c>), "" until the app was started.</param>
/// <param name="Name">The app's name, for the log.</param>
/// <param name="AppId">A Store app's AppUserModelID: its windows are also those that carry it (a UWP app's frame is drawn
/// by ApplicationFrameHost, outside the job – app-tabs-design §6.6).</param>
internal sealed record HelperApp(int Place, string Job, string Name, string? AppId = null);

internal sealed record HelperHiddenApps(string[] Apps);

internal sealed record HelperConsent(bool Granted, string Reason);

/// <param name="ByUser">The user at the device ended it ("Disconnect") – otherwise the service starts a new helper.</param>
internal sealed record HelperEnd(string Reason, bool ByUser = false);

[JsonSerializable(typeof(HelperInit))]
[JsonSerializable(typeof(HelperConsent))]
[JsonSerializable(typeof(HelperEnd))]
[JsonSerializable(typeof(HelperHiddenApps))]
[JsonSerializable(typeof(HelperApp))]
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
        var session = new DesktopHelperSession(input, output, ui, screen, keyboard, new WindowsAppWindows());
        ui.Noted = session.Note; // a snipping tool the privacy screen closed
        // RDP windows in this session keep drawing when minimized while the admin is here (put back when this ends);
        // tried again until someone is signed in. Disposed in reverse order: the timer first, then the setting is put back.
        using var keepRdp = new KeepRdpDrawing();
        // Ctrl+Alt+Del screen actions an earlier helper in this session hid and couldn't put back (it was killed): put
        // back once the user's hive is loaded, also when this connection never blocks input (SasActions).
        var leftovers = new SasActions();
        var reconciled = false;
        using var tryRdp = new Timer(_ =>
        {
            try { if (keepRdp.Apply() is { } done) session.Note(done); }
            catch (Exception ex) { session.Note("RDP minimize setting not changed: " + ex.Message); keepRdp.Dispose(); }
            if (reconciled) return;
            try { reconciled = leftovers.TryReconcile(out var back); if (back is not null) session.Note(back); }
            catch (Exception ex) { reconciled = true; session.Note("Ctrl+Alt+Del screen: actions an earlier helper hid could not be put back: " + ex.Message); }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        session.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
}

/// <summary>The work of the desktop helper, independent of Windows' screen and input (tests use doubles).</summary>
internal sealed class DesktopHelperSession(Stream fromService, Stream toService, IHelperUi ui, IScreenSource screen, IInputSink input, IAppWindows apps)
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

    // Privacy: what the user at the device hid (DesktopScreenHide), and what RemoteHub is told about it and the privacy screen.
    private volatile string _userHide = DesktopScreenHide.None;
    private volatile DesktopPrivacy _privacy = DesktopPrivacy.Nothing;
    private volatile bool _privacyChanged; // the capture thread sends the right picture at once
    private readonly object _privacyLock = new();

    // Hidden apps: the user's choice (and the policy's), where they are on the screen now (screen pixels, for input).
    private volatile IReadOnlySet<string> _chosenApps = new HashSet<string>(), _alwaysHidden = new HashSet<string>(), _hiddenApps = new HashSet<string>();
    private volatile ScreenRect[] _mask = [];
    private readonly AppMask _appMask = new(apps);

    // App tab (HelperInit.App, docs/app-tabs-design.md §6.2, §6.3): the app once it runs, its windows (capture thread),
    // the window that last had the keyboard (for the input thread), and the shell keys that are dropped.
    private volatile HelperApp? _app;
    private AppTabWindows? _appWindows;
    private string _appJob = "";
    private IntPtr _appMain;
    private volatile IntPtr _appFront;
    private long _appGoneSince = -1;
    private bool _appClosedSent, _appJobFailed, _appLeft;
    private readonly Stopwatch _appPlaced = Stopwatch.StartNew();
    private readonly ShellKeyFilter _shellKeys = new();
    private volatile DesktopAppClose? _appClose; // the tab was closed: done by the capture thread, which knows the app's windows
    private readonly AppTaskbar _taskbar = new();  // no taskbar on the tabs' monitors while the app runs (§5)
    private bool _taskbarFailed;

    // Clipboard images and files: one transfer each way at a time (a newer copy replaces an unfinished one).
    private TransferReceiver? _receiving;
    private CancellationTokenSource? _sending;
    private int _transferIds;

    private readonly record struct RemoteKeyOrMouse(RemoteMouse? Mouse, RemoteKey? Key);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;
        ui.ClipboardChanged += text => { if (_begun && Level >= DesktopClipboardLevel.Text && !CopiedInHiddenApp()) _ = Send(DesktopWire.Text(DesktopMessage.Clipboard, text)); };
        ui.ClipboardContent += content =>
        {
            if (!_begun) return;
            if (!CopiedInHiddenApp()) SendClipboard(content);
            else if (content.Files is not null) Note("clipboard: files copied at the device – not sent: copied in an app the user hid");
        };
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
                        _app = _init.App;
                        // Decided for each event on the desktop it would go to – not on what the capture thread last
                        // saw – so nothing reaches the secure desktop without the right, or a user's desktop while held.
                        var init = _init;
                        _userHide = DesktopScreenHide.Valid(init.UserHidden);
                        _alwaysHidden = (init.AlwaysHidden ?? []).Select(AppIds.Normalize).ToHashSet();
                        ChooseApps((init.HiddenApps ?? []).Select(AppIds.Normalize).ToHashSet(), report: false);
                        _privacy = _privacy with { User = _userHide }; // the counts ChooseApps just set
                        // Before the question: the user can pick apps to hide before anything is sent.
                        ui.OfferHideApps(_chosenApps, _alwaysHidden, chosen => ChooseApps(chosen, report: _begun));
                        // Nothing from the admin while the user has hidden their screen – also not on the secure desktop.
                        input.Allowed = secure => !_held && _userHide == DesktopScreenHide.None && (secure ? init.SecureDesktop : !init.HoldUserDesktop);
                        if (init.Gdi) screen.PreferGdi(); // the GPU picture stood still earlier in this connection
                        ui.SetPrivacyHeading(init.PrivacyHeading);
                        StartConsent(_init);
                        break;
                    case (byte)HelperMessage.Begin when _init is not null && !_begun:
                        _begun = true;
                        ui.CloseDialogs();
                        if (_init.SignedInRemotely) ui.ShowSignedInRemotely(_init); // the admin's own session: nobody at the device to end it
                        else ui.ShowBanner(_init, () => _ = End("The user at the device ended the session.", byUser: true), HideScreen);
                        if (_userHide != DesktopScreenHide.None || _hiddenApps.Count > 0) ui.ShowPrivacy(_privacy);
                        _ = Send(DesktopWire.Json(DesktopMessage.Privacy, _privacy, DesktopJson.Default.DesktopPrivacy));
                        if (_chosenApps.Count > 0) _ = Send(DesktopWire.Json((DesktopMessage)HelperMessage.HiddenApps, new HelperHiddenApps([.. _chosenApps]), HelperJson.Default.HelperHiddenApps));
                        capture.Start();
                        inject.Start();
                        break;
                    case (byte)DesktopMessage.Settings when _begun:
                        var s = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopSettings);
                        (_monitor, _fps, _quality) = (s.Monitor, Math.Clamp(s.FramesPerSecond, 1, 60), Math.Clamp(s.Quality, 1, 100));
                        _fit = _init?.ViewOnly != false ? (0, 0) : DesktopProtocol.ValidFitSize(s.FitWidth, s.FitHeight); // the service checked it too
                        _settingsChanged = true;
                        break;
                    case (byte)HelperMessage.App when _init?.App is not null:
                        _app = DesktopWire.ReadJson(m, HelperJson.Default.HelperApp);
                        break;
                    case (byte)HelperMessage.AppClose when _init?.App is not null:
                        _appClose = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopAppClose);
                        break;
                    case (byte)DesktopMessage.Ack when _begun:
                        DesktopWire.ReadAck(m);
                        if (Interlocked.Decrement(ref _inFlight) < 0) Interlocked.Exchange(ref _inFlight, 0);
                        _acks.Release();
                        break;
                    case (byte)DesktopMessage.KeyFrameRequest when _begun:
                        _keyFrameRequested = true;
                        break;
                    case (byte)HelperMessage.PrivacyScreen when _begun && _init?.ViewOnly == false:
                        var screenAsked = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopPrivacyScreen);
                        var style = DesktopPrivacyStyle.Valid(screenAsked.Style);
                        // Called again (with why) if the overlays have to come down later, e.g. when the monitors change.
                        ui.SetPrivacyScreen(screenAsked with { Style = style }, error => Report(error is null
                            ? _privacy with { Screen = style, LocalInputBlocked = style != DesktopPrivacyStyle.Off && screenAsked.BlockLocalInput, Reason = "" }
                            : _privacy with { Screen = DesktopPrivacyStyle.Off, LocalInputBlocked = false, Reason = error }));
                        break;
                    case (byte)HelperMessage.SecureAttention when _begun && _init?.ViewOnly == false && _init.SecureDesktop && _userHide == DesktopScreenHide.None:
                        // In this session, as LocalSystem (the service checked the right). Not on this loop's thread.
                        _ = Task.Run(() =>
                        {
                            _ = Send(DesktopWire.Text((DesktopMessage)HelperMessage.SecureAttentionResult, input.SecureAttention() ?? ""));
                        });
                        break;
                    case (byte)DesktopMessage.Mouse when _begun && _init?.ViewOnly == false && _userHide == DesktopScreenHide.None:
                        _input.TryAdd(new RemoteKeyOrMouse(DesktopWire.ReadMouse(m), null));
                        break;
                    case (byte)DesktopMessage.Key when _begun && _init?.ViewOnly == false && _userHide == DesktopScreenHide.None:
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
                    case (byte)DesktopMessage.TransferEnd:
                        var ended = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd).Id;
                        if (_receiving is { } r2 && ended == r2.Transfer.Id) DropReceiving();
                        else PasteFailed(ended); // refused before anything came (the service: the lock screen is up)
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
            if (_taskbar.Off)
                try { if (_taskbar.PutBack()) AppTaskbar.Announce(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) { /* the next helper or the service puts it back */ }
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

    /// <summary>The user at the device hid their screen (Black, Blur) or shows it again (None) – from the banner.</summary>
    private void HideScreen(string mode)
    {
        mode = DesktopScreenHide.Valid(mode);
        if (mode == _userHide) return;
        _userHide = mode;
        if (mode != DesktopScreenHide.None) input.ReleaseAll(); // nothing the admin holds stays pressed
        _keyFrameRequested = true;
        _privacyChanged = true;
        Report(_privacy with { User = mode, Reason = "" });
    }

    /// <summary>What is hidden now: to the banner and to RemoteHub (through the service, which audits it).</summary>
    private void Report(DesktopPrivacy privacy) => Report(_ => privacy);

    /// <summary>Changes what is reported (from the UI thread and the capture thread – one at a time).</summary>
    private void Report(Func<DesktopPrivacy, DesktopPrivacy> change)
    {
        lock (_privacyLock)
        {
            var before = _privacy;
            var now = _privacy = change(before);
            // The banner only cares about these; a moved hidden area alone doesn't redraw it.
            if (now.User != before.User || now.Screen != before.Screen || now.LocalInputBlocked != before.LocalInputBlocked || now.HiddenApps != before.HiddenApps)
                ui.ShowPrivacy(now);
            _ = Send(DesktopWire.Json(DesktopMessage.Privacy, now, DesktopJson.Default.DesktopPrivacy));
        }
    }

    /// <summary>The user picked the apps to hide (in the consent question, the code dialog or from the banner).</summary>
    private void ChooseApps(IReadOnlySet<string> chosen, bool report)
    {
        _chosenApps = chosen;
        var all = chosen.Union(_alwaysHidden).ToHashSet();
        _appMask.Reset(Environment.TickCount64); // nothing goes out until the new choice is in the picture
        _hiddenApps = all;
        _privacyChanged = true;
        _keyFrameRequested = true;
        var (byUser, byPolicy) = HiddenCounts(chosen, _alwaysHidden);
        if (!report)
        {
            // Before the session began (picked in the question): it goes out with the first report.
            lock (_privacyLock) _privacy = _privacy with { HiddenApps = byUser, PolicyHiddenApps = byPolicy };
            return;
        }
        _ = Send(DesktopWire.Json((DesktopMessage)HelperMessage.HiddenApps, new HelperHiddenApps([.. chosen]), HelperJson.Default.HelperHiddenApps));
        Report(p => p with { HiddenApps = byUser, PolicyHiddenApps = byPolicy, Regions = all.Count == 0 ? null : p.Regions, Reason = "" });
    }

    /// <summary>
    /// How many apps the user hides and how many the policy does. Both are masked alike, but only the user's choice is
    /// reported as theirs – on the banner, to RemoteHub and in the audit (DesktopUserHid) – so an app the policy hides
    /// doesn't read as one the user hid. An app in both lists counts as the policy's: the user can't show it anyway.
    /// </summary>
    internal static (int ByUser, int ByPolicy) HiddenCounts(IReadOnlySet<string> chosen, IReadOnlySet<string> always) =>
        (chosen.Count(a => !always.Contains(a)), always.Count);

    /// <summary>What was just copied comes from a hidden app: it isn't sent to RemoteHub.</summary>
    private bool CopiedInHiddenApp()
    {
        var hidden = _hiddenApps;
        if (hidden.Count == 0) return false;
        try { return AppIds.IsHidden(hidden, apps.ClipboardOwnerApp()); }
        catch { return true; } // unknown: not sent
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

    /// <summary>A line for the agent's log (from any thread).</summary>
    internal void Note(string text) => _ = Send(DesktopWire.Text((DesktopMessage)HelperMessage.Log, text));

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
            else if (t.Kind == DesktopTransferKind.Drop)
            {
                // Dropped on the tab: onto the user's Desktop, as the user – a link they planted there can only lead where
                // they may write anyway.
                var desktop = SessionUser.DesktopFolder();
                var names = t.Items.Select(i => DesktopWire.SafeRelativePath(i.Path)?.Split('\\', '/')[0]).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _receiving = SessionUser.Run(() =>
                {
                    var folder = SessionUser.NewDropFolder(desktop, names, DateTime.Now);
                    _dropFolder = Path.GetRelativePath(desktop, folder);
                    return new TransferReceiver(t, folder, ownsFolder: false);
                });
            }
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

    /// <summary>Where the files dropped now go, relative to the user's Desktop (for the receipt).</summary>
    private string _dropFolder = "";

    private void Finish(TransferReceiver r)
    {
        _receiving = null;
        r.Dispose(); // the files are closed before they go on the clipboard
        if (r.Transfer.Kind == DesktopTransferKind.Drop)
        {
            // Saved on the Desktop, nothing for the clipboard; the receipt says where (RemoteHub shows it, the service audits it).
            _ = Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(r.Transfer.Id, true, "", ToDevice: true, Folder: _dropFolder), DesktopJson.Default.DesktopTransferEnd));
            return;
        }
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
        // Dropped files are saved, never offered for pasting (the service refuses that already).
        if (t.Kind == DesktopTransferKind.Drop) { TransferFailed(t.Id, "The copied items are incomplete.", toDevice: true); return; }
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
        if (Level < need)
        {
            if (content.Files is not null) Note($"clipboard: files copied at the device – not sent: the clipboard level is {Level}");
            return;
        }
        // Before the files are looked at (as the user): a copy that never gets further still leaves this line.
        if (content.Files is not null) Note($"clipboard: {content.Files.Count} path(s) copied at the device – reading them as the signed-in user");
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
                if (o is null)
                {
                    if (content.Files is not null) Note($"clipboard: {content.Files.Count} path(s) copied at the device – not sent: {error ?? "nothing to copy"}");
                    TransferFailed(id, error ?? "Nothing to copy.");
                    return;
                }
                if (content.Files is not null)
                    Note($"clipboard: files copied at the device ({o.Transfer.Items.Length} item(s), {o.Transfer.Total} bytes) – {(_init?.OfferToViewer == true ? "offered to RemoteHub" : "sent to RemoteHub")}");
                if (_init?.OfferToViewer == true)
                {
                    // Names and sizes only: RemoteHub's clipboard offers them; the data goes when someone pastes there.
                    // One copy often brings two clipboard changes (announced, then rendered), so two of these threads
                    // race: only the newest copy is offered, and it is the last offer RemoteHub sees – else RemoteHub
                    // asks for an id that is no longer the one kept here.
                    lock (_offerGate)
                    {
                        if (cancel.IsCancellationRequested) return; // a newer copy came meanwhile
                        _offered = new OfferedOut(o, content.Files is not null);
                        SendSync(DesktopWire.Json(DesktopMessage.TransferOffer, o.Transfer, DesktopJson.Default.DesktopTransfer));
                    }
                    return;
                }
                SendOutgoing(o, content.Files is not null, cancel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TransferFailed(id, "The copied files can't be read on the device: " + ex.Message);
            }
            catch (Exception ex)
            {
                // Impersonating the user can fail in ways the file APIs don't (SecurityException): refused, not a helper crash.
                Note($"clipboard: a copy at the device could not be read ({ex.GetType().Name})");
                TransferFailed(id, "The copied files can't be read on the device.");
            }
        }) { IsBackground = true, Name = "Clipboard transfer" }.Start();
    }

    /// <summary>What was copied here and offered to RemoteHub, until it's pasted there (or something else is copied).</summary>
    private volatile OfferedOut? _offered;
    private readonly object _offerGate = new();

    private sealed record OfferedOut(ClipboardTransfer.Outgoing Outgoing, bool Files);

    /// <summary>Pasted in RemoteHub: what was offered goes now (in the background, like a direct copy).</summary>
    private void SendOffered(int id)
    {
        if (_offered is not { } offered || offered.Outgoing.Transfer.Id != id)
        {
            // An older offer (a newer copy replaced it): answered, so RemoteHub's paste doesn't wait for nothing.
            TransferFailed(id, "A newer copy replaced it.");
            return;
        }
        _sending?.Cancel();
        var cancel = _sending = new CancellationTokenSource();
        new Thread(() =>
        {
            try { SendOutgoing(offered.Outgoing, offered.Files, cancel); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TransferFailed(id, "The copied files can't be read on the device: " + ex.Message);
            }
            catch (Exception ex)
            {
                Note($"clipboard: a copy at the device could not be read ({ex.GetType().Name})");
                TransferFailed(id, "The copied files can't be read on the device.");
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
        if (_init!.App is not null) return; // the service gave the app tab its monitor
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
        if (_init?.App is not null) return; // app tabs never make their monitor the primary one (app-tabs-design §5)
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
                if (_userHide != DesktopScreenHide.None) continue;                      // the user hid their screen
                if (_init.App is not null && !AppTabInput(i)) continue;
                if (i.Key is { } k)
                {
                    // Not typed blind into a hidden app (a password manager…): only a key's release goes through there.
                    if ((k.Flags & RemoteKeyFlags.Up) == 0 && IntoHiddenApp()) continue;
                    input.Key(k);
                }
                else if (i.Mouse is { } m && _shown is { } monitor)
                {
                    var f = _scale;
                    var at = f == 1 ? m : m with { X = m.X * f + f / 2, Y = m.Y * f + f / 2 };
                    // No clicks or scrolling on what the admin can't see; moving the pointer and letting go still work.
                    if (m.Action is RemoteMouseAction.Down or RemoteMouseAction.Wheel or RemoteMouseAction.HorizontalWheel &&
                        _mask.Any(r => r.Contains(monitor.X + at.X, monitor.Y + at.Y))) continue;
                    ui.AdminPointer(monitor.X + at.X, monitor.Y + at.Y); // the user's banner lets the admin's clicks through
                    input.Mouse(monitor, at);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { input.ReleaseAll(); }
    }

    /// <summary>
    /// App tab: nothing goes through before the app runs; the shell's keys never (<see cref="ShellKeyFilter"/>); a key
    /// that would go to another program's window first brings the app's window that last had the keyboard to the front.
    /// </summary>
    private bool AppTabInput(RemoteKeyOrMouse i)
    {
        if (i.Key is { } k && !_shellKeys.Pass(k)) return false;
        if (_app is not { Job.Length: > 0 }) return false;
        if (i.Key is { } key && (key.Flags & RemoteKeyFlags.Up) == 0 && _appFront is var front && front != IntPtr.Zero && AppTabWindows.Foreground() != front
            && AppTabWindows.BringToFront(front))
        {
            // The switch reaches the window's thread a moment later: the key waits for it (at most 200 ms), so it isn't
            // typed into the window that had the keyboard before.
            var until = Environment.TickCount64 + 200;
            while (AppTabWindows.Foreground() != front && Environment.TickCount64 < until) Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>
    /// App tab, after each capture: what to fill on the tab's monitor (all of it before the app runs), the app's windows
    /// put where they belong (every 250 ms), the window to give the keyboard to, and the end of the app reported once its
    /// windows are gone for 2 s and its job is empty.
    /// </summary>
    private ScreenRect[] AppTabFrame(HelperApp tab, DesktopMonitor monitor)
    {
        var area = new ScreenRect(monitor.X, monitor.Y, monitor.X + monitor.Width, monitor.Y + monitor.Height);
        if (tab.Job.Length == 0) return [area];
        if (_appJob != tab.Job)
        {
            _appWindows?.Dispose();
            _appWindows = AppTabWindows.Open(tab.Job);
            (_appJob, _appMain, _appFront, _appGoneSince, _appClosedSent) = (tab.Job, IntPtr.Zero, IntPtr.Zero, -1, false);
            if (_appWindows is null && !_appJobFailed)
            {
                _appJobFailed = true;
                SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, $"app tab: the job of {tab.Name} couldn't be opened – nothing is shown"));
            }
        }
        if (_appWindows is not { } job) return [area];
        if (!_taskbar.Off && !_taskbarFailed && !_appLeft) TaskbarOff();
        var windows = apps.Visible();
        // A Store app's windows may be drawn by another process (§6.6): also those that carry its AppUserModelID.
        bool Owned(AppWindow w) => job.Owns(w.Handle) || (tab.AppId is { } appId && job.Carries(w.Handle, appId));
        if (_appMain == IntPtr.Zero || !AppTabWindows.Exists(_appMain))
            _appMain = windows.FirstOrDefault(w => Owned(w) && AppTabWindows.MainCandidate(w.Handle))?.Handle ?? IntPtr.Zero;

        if (_appClose is { } close)
        {
            // The tab was closed (§4.3): the app's top-level windows are asked to close, as their close button would –
            // once; or, to leave it running, moved to the primary monitor, where it stays usable without the tab.
            _appClose = null;
            var primaryArea = _monitors.FirstOrDefault(m => m.Primary) is { } pm ? new ScreenRect(pm.X, pm.Y, pm.X + pm.Width, pm.Y + pm.Height) : area;
            foreach (var w in windows.Where(Owned))
            {
                if (close.Force) AppTabWindows.Apply(new AppTabView.Move(w.Handle, AppTabView.CentredIn(w.Bounds, primaryArea, primaryArea), false));
                else if (AppTabWindows.MainCandidate(w.Handle)) AppTabWindows.Close(w.Handle);
            }
            if (close.Force) _appLeft = true; // no longer the tab's: nothing is placed or watched any more
            else _appPlaced.Restart();
        }
        if (_appLeft) return [area];

        if (_appPlaced.ElapsedMilliseconds >= 250)
        {
            _appPlaced.Restart();
            var primary = _monitors.FirstOrDefault(m => m.Primary) is { } p ? new ScreenRect(p.X, p.Y, p.X + p.Width, p.Y + p.Height) : area;
            foreach (var move in AppTabView.Placement(area, primary, windows, Owned, _appMain, AppTabWindows.IsMaximized, AppTabWindows.Resizable))
                AppTabWindows.Apply(move);
        }

        var foreground = AppTabWindows.Foreground();
        if (foreground != IntPtr.Zero && windows.Any(w => w.Handle == foreground && Owned(w))) _appFront = foreground;
        else if (_appFront == IntPtr.Zero || !AppTabWindows.Exists(_appFront)) _appFront = _appMain;

        var now = Environment.TickCount64;
        if (windows.Any(Owned)) _appGoneSince = -1;
        else if (_appGoneSince < 0) _appGoneSince = now;
        else if (!_appClosedSent && now - _appGoneSince >= 2000 && job.ActiveProcesses() == 0)
        {
            _appClosedSent = true;
            SendSync(DesktopWire.Empty((DesktopMessage)HelperMessage.AppClosed));
        }
        return [.. AppTabView.Mask(area, windows, Owned)];
    }

    /// <summary>The app runs in this session: its user's taskbar on other monitors goes (put back when the helper ends).</summary>
    private void TaskbarOff()
    {
        try
        {
            if (SessionUser.Sid() is not { } sid) return;
            _taskbar.Reconcile(sid); // a helper that ended abruptly may have left it off – the record is the original
            if (_taskbar.TurnOff(sid)) AppTaskbar.Announce();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.Security.SecurityException)
        {
            _taskbarFailed = true; // the tabs' monitors keep their taskbar; it is filled like anything that isn't the app
            SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, "app tab: the taskbar on other monitors stays: " + ex.Message));
        }
    }

    private DesktopRect[]? _regionsSent;

    /// <summary>
    /// Where hidden apps cover the picture, for the viewer's "Hidden by the user" labels: sent when it changed, at most
    /// 5 times a second.
    /// </summary>
    private void ReportRegions(ScreenRect[] mask, DesktopMonitor monitor, Stopwatch clock)
    {
        DesktopRect[]? regions = null;
        if (mask.Length > 0)
        {
            var f = Math.Max(1, _scale);
            var picture = new ScreenRect(0, 0, monitor.Width, monitor.Height);
            regions = mask.Select(r => r.Offset(-monitor.X, -monitor.Y).Intersect(picture)).Where(r => !r.IsEmpty)
                .Select(r => new DesktopRect(r.Left / f, r.Top / f, (r.Width + f - 1) / f, (r.Height + f - 1) / f)).Distinct().Take(64).ToArray();
            if (regions.Length == 0) regions = null;
        }
        if (regions is null && _regionsSent is null || regions is not null && _regionsSent is not null && regions.SequenceEqual(_regionsSent)) return;
        if (clock.ElapsedMilliseconds < 200 && regions is not null) return; // a moving window: the next one is sent shortly
        clock.Restart();
        _regionsSent = regions;
        Report(p => p with { Regions = regions });
    }

    /// <summary>The window with the keyboard belongs to a hidden app (unknown counts as hidden).</summary>
    private bool IntoHiddenApp()
    {
        var hidden = _hiddenApps;
        if (hidden.Count == 0) return false;
        try { return AppIds.IsHidden(hidden, apps.ForegroundApp()); }
        catch { return true; }
    }

    // ---- capture thread

    private unsafe void CaptureLoop(CancellationToken ct)
    {
        var frame = new FrameBuffer();
        var scaled = new FrameBuffer();
        var hidden = new FrameBuffer(); // what is sent instead while the user hides their screen (the capture stays untouched)
        var blackSent = false;
        var blurPending = false;
        var blurClock = Stopwatch.StartNew();
        var covered = new FrameBuffer(); // the picture with the hidden apps covered
        var appsPending = false;
        var maskFailed = false;
        var regionClock = Stopwatch.StartNew();
        var lastStats = Stopwatch.StartNew();
        H264Encoder? encoder = null;
        // The quality the viewer asked for last; the encoder's own may be lower while the network is slow.
        var requested = _quality;
        // The frame rate the viewer asked for last, and the one the loop runs at: lower while the network is slow. The
        // encoder keeps the asked rate – fed fewer frames, it sends fewer bits, and no new key frame is needed per step.
        var askedRate = _fps;
        var frameRate = askedRate;
        var lastSync = Stopwatch.StartNew();
        var lastChange = Stopwatch.StartNew();
        var lastAdapt = Stopwatch.StartNew();
        var refinements = 0;
        long waitedTicks = 0;
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
                if (_fps != askedRate) frameRate = askedRate = _fps; // the viewer asked for another rate: start from it
                var interval = 1000 / Math.Max(1, frameRate);
                var loopStart = Stopwatch.GetTimestamp();

                if (dark.ElapsedMilliseconds >= 3000 && wakes < 3 && (wakes == 0 || lastWake.ElapsedMilliseconds >= 2000))
                {
                    wakes++;
                    lastWake.Restart();
                    if (wakes == 2 && !_held && !_virtualAsked && why == "the captured picture is black" && _shown is { } s0 &&
                        s0.Index != DesktopProtocol.AllMonitors && _init!.VirtualDisplay == VirtualDisplayModes.Auto && _init.App is null)
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
                    var waitStart = Stopwatch.GetTimestamp();
                    if (!_acks.Wait(1000, ct)) Interlocked.Exchange(ref _inFlight, 0); // an ack got lost: don't stall forever
                    waitedTicks += Stopwatch.GetTimestamp() - waitStart;
                    continue;
                }

                var result = screen.Capture(monitor, haveImage ? Math.Max(1, interval / 2) : 100, frame);
                if (screen.TakeLogNote() is { Length: > 0 } note) SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, note));
                if (result == CaptureResult.Failed) { why = "capturing failed"; Thread.Sleep(50); continue; }
                if (result == CaptureResult.Changed) black = IsBlack(frame);
                if (result == CaptureResult.Changed || haveImage)
                {
                    if (!black) { dark.Restart(); wakes = 0; darkLogged = false; }
                    else why = "the captured picture is black";
                }
                // The user hid their screen: black – one black frame and no pointer; blurred – few, blurred frames.
                var hide = _userHide;
                var privacyChanged = _privacyChanged;
                if (privacyChanged) { _privacyChanged = false; blackSent = false; }

                // Hidden apps: where their windows are now – and whether a frame may go out at all (one of them is
                // opening, moving or being restored). Worked out after the capture: the positions are never older than
                // the picture. Anything going wrong here sends no picture rather than an uncovered one.
                ScreenRect[] mask = [];
                var appsHold = false;
                if (hide != DesktopScreenHide.Black && _hiddenApps is { Count: > 0 } hiddenApps)
                {
                    try { mask = _appMask.Compute(hiddenApps, Environment.TickCount64, out appsHold); }
                    catch (Exception ex)
                    {
                        appsHold = true;
                        if (!maskFailed) SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, "hidden apps: their windows couldn't be found – no picture until they can: " + ex.Message));
                        maskFailed = true;
                    }
                }
                ReportRegions(mask, monitor, regionClock);
                // App tab: everything on its monitor that isn't the app is filled like a hidden app – and isn't clicked.
                if (_app is { } tab)
                {
                    try { mask = [.. mask, .. AppTabFrame(tab, monitor)]; }
                    catch (Exception ex)
                    {
                        mask = [.. mask, new ScreenRect(monitor.X, monitor.Y, monitor.X + monitor.Width, monitor.Y + monitor.Height)];
                        if (!maskFailed) SendSync(DesktopWire.Text((DesktopMessage)HelperMessage.Log, "app tab: its windows couldn't be found – nothing shown until they can: " + ex.Message));
                        maskFailed = true;
                    }
                }
                _mask = mask;

                var cursor = screen.Cursor(monitor, out var shape);
                if (_scale > 1) cursor = cursor with { X = cursor.X / _scale, Y = cursor.Y / _scale };
                if (hide == DesktopScreenHide.Black) lastCursor = default; // sent again once it is shown
                else
                {
                    if (shape is not null) SendSync(DesktopWire.CursorShape(shape.Width, shape.Height, shape.HotX, shape.HotY, shape.Bgra));
                    if (cursor != lastCursor)
                    {
                        lastCursor = cursor;
                        SendSync(DesktopWire.Cursor(cursor));
                    }
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

                switch (hide)
                {
                    case DesktopScreenHide.Black:
                        send = haveImage && (!blackSent || _keyFrameRequested);
                        break;
                    case DesktopScreenHide.Blur:
                        // At most 2 frames a second, so movement doesn't help to read what is blurred.
                        if (send || privacyChanged) blurPending = true;
                        send = haveImage && blurPending && (privacyChanged || blurClock.ElapsedMilliseconds >= 500);
                        break;
                    default:
                        if (privacyChanged) send = haveImage; // shown again: the picture as it is now, at once
                        break;
                }
                // While a hidden app's window moves or animates the viewer keeps the last covered picture; the current one follows.
                if (appsHold)
                {
                    if (send) appsPending = true;
                    send = false;
                }
                else if (appsPending)
                {
                    send = haveImage;
                    appsPending = false;
                }

                if (send)
                {
                    var picture = frame;
                    if (mask.Length > 0)
                    {
                        // Covered in a copy before scaling (nothing of a hidden window blends into an edge pixel);
                        // the capture itself stays as it is for the next frames.
                        covered.Ensure(frame.Width, frame.Height);
                        Buffer.BlockCopy(frame.Pixels, 0, covered.Pixels, 0, frame.Width * frame.Height * 4);
                        AppMask.Apply(covered, monitor, mask);
                        picture = covered;
                    }
                    if (_scale > 1)
                    {
                        Downscale(picture, scaled, _scale);
                        picture = scaled;
                    }
                    if (hide == DesktopScreenHide.Black)
                    {
                        hidden.Ensure(picture.Width, picture.Height);
                        Array.Clear(hidden.Pixels);
                        picture = hidden;
                        blackSent = true;
                    }
                    else if (hide == DesktopScreenHide.Blur)
                    {
                        Blur(picture, hidden);
                        picture = hidden;
                        blurPending = false;
                        blurClock.Restart();
                    }
                    int w = picture.Width & ~1, h = picture.Height & ~1;
                    // Only a new quality from the viewer is applied here – compared with what it asked for before, not
                    // with the encoder's, which the slow-network step below may have lowered on purpose.
                    var asked = _quality;
                    if (encoder is null || encoder.Width != w || encoder.Height != h || requested != asked && !encoder.TrySetQuality(asked))
                    {
                        encoder?.Dispose();
                        // "Smooth" (above 30 fps): the GPU's encoder when there is one – it falls back to software by itself.
                        encoder = new H264Encoder(w, h, Math.Max(1, _fps), asked, preferHardware: _fps > 30);
                        _keyFrameRequested = true;
                        if ((w != (_layoutWidth & ~1) || h != (_layoutHeight & ~1)) && _scale == 1) SendLayout(out _);
                    }
                    requested = asked;
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
                        screen.CaptureMethod + (encoder?.Kind == "hardware" ? ", GPU encoder" : ""), encoder?.Width ?? 0, encoder?.Height ?? 0, screen.FitInfo,
                        FrameRateLimit: frameRate < askedRate ? frameRate : 0),
                        DesktopJson.Default.DesktopStats));
                    _sent = 0;
                }

                // Slow network: lower the quality, then the frame rate, step by step; raise them again when the acks keep up.
                if (lastAdapt.ElapsedMilliseconds >= 2000)
                {
                    var window = lastAdapt.ElapsedMilliseconds;
                    lastAdapt.Restart();
                    var waited = (long)Stopwatch.GetElapsedTime(0, waitedTicks).TotalMilliseconds;
                    if (encoder is not null)
                    {
                        var (quality, rate) = Adapted(encoder.Quality, frameRate, requested, askedRate, waited, window);
                        if (quality != encoder.Quality) encoder.TrySetQuality(quality);
                        frameRate = rate;
                    }
                    waitedTicks = 0;
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


    /// <summary>The pixels of one blurred block (<see cref="Blur"/>).</summary>
    internal const int BlurBlock = 32;

    /// <summary>
    /// The picture the admin gets while the user blurred their screen: averaged over blocks of
    /// <see cref="BlurBlock"/> × <see cref="BlurBlock"/> pixels (≈ 60 × 34 values for 1920 × 1080 – no text survives that),
    /// then smoothly scaled back up. Nothing of the detail within a block is left to undo.
    /// </summary>
    internal static void Blur(FrameBuffer source, FrameBuffer target)
    {
        int w = source.Width, h = source.Height;
        target.Ensure(w, h);
        if (w <= 0 || h <= 0) return;
        int gw = (w + BlurBlock - 1) / BlurBlock, gh = (h + BlurBlock - 1) / BlurBlock;
        var sum = new long[gw * gh * 3];
        var count = new int[gw * gh];
        var src = source.Pixels;
        for (var y = 0; y < h; y++)
        {
            var row = y / BlurBlock * gw;
            for (var x = 0; x < w; x++)
            {
                var cell = row + x / BlurBlock;
                var p = (y * w + x) * 4;
                sum[cell * 3] += src[p];
                sum[cell * 3 + 1] += src[p + 1];
                sum[cell * 3 + 2] += src[p + 2];
                count[cell]++;
            }
        }
        var avg = new float[gw * gh * 3];
        for (var c = 0; c < count.Length; c++)
            for (var k = 0; k < 3; k++) avg[c * 3 + k] = sum[c * 3 + k] / (float)count[c];

        // Bilinear between the block centres.
        var dst = target.Pixels;
        for (var y = 0; y < h; y++)
        {
            var fy = Math.Clamp((y + 0.5f) / BlurBlock - 0.5f, 0, gh - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, gh - 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / BlurBlock - 0.5f, 0, gw - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, gw - 1);
                var tx = fx - x0;
                var p = (y * w + x) * 4;
                for (var k = 0; k < 3; k++)
                {
                    var top = avg[(y0 * gw + x0) * 3 + k] * (1 - tx) + avg[(y0 * gw + x1) * 3 + k] * tx;
                    var bottom = avg[(y1 * gw + x0) * 3 + k] * (1 - tx) + avg[(y1 * gw + x1) * 3 + k] * tx;
                    dst[p + k] = (byte)Math.Clamp(top * (1 - ty) + bottom * ty + 0.5f, 0, 255);
                }
                dst[p + 3] = 255;
            }
        }
    }

    /// <summary>Quality the slow-network steps keep before they lower the frame rate: text on the device stays readable.</summary>
    internal const int ReadableQuality = 50;
    /// <summary>The lowest quality and frame rate the slow-network steps go to.</summary>
    internal const int LowestQuality = 20, LowestFrameRate = 5;

    /// <summary>
    /// Quality and frame rate after <paramref name="windowMs"/> in which the capture loop spent <paramref name="waitedMs"/>
    /// waiting for the viewer's acks. A quarter of the time or more means the network can't keep up, and one step goes
    /// down: the quality by 10 to <see cref="ReadableQuality"/>, then the frame rate by a third to
    /// <see cref="LowestFrameRate"/>, then the quality to <see cref="LowestQuality"/> – an admin reads text, so a sharp
    /// picture that moves less often comes before a blurred one. No wait at all: one step back up, in the same order
    /// reversed, up to what the viewer asked for. Counting waits rather than blocked loops matters: on a saturated link
    /// every wait is followed by exactly one capture, so the counts stay equal while the time is nearly all spent waiting.
    /// </summary>
    internal static (int Quality, int FrameRate) Adapted(int quality, int frameRate, int askedQuality, int askedFrameRate, long waitedMs, long windowMs)
    {
        askedFrameRate = Math.Max(1, askedFrameRate);
        quality = Math.Min(quality, askedQuality);
        frameRate = Math.Clamp(frameRate, 1, askedFrameRate);
        var readable = Math.Min(ReadableQuality, askedQuality);
        var lowestRate = Math.Min(LowestFrameRate, askedFrameRate);
        if (windowMs > 0 && waitedMs * 4 >= windowMs)
        {
            if (quality > readable) return (Math.Max(readable, quality - 10), frameRate);
            if (frameRate > lowestRate) return (quality, Math.Max(lowestRate, frameRate * 2 / 3));
            return (quality > LowestQuality ? Math.Max(LowestQuality, quality - 10) : quality, frameRate);
        }
        if (waitedMs <= 0)
        {
            if (quality < readable) return (Math.Min(readable, quality + 5), frameRate);
            if (frameRate < askedFrameRate) return (quality, Math.Min(askedFrameRate, Math.Max(frameRate + 1, frameRate * 3 / 2)));
            if (quality < askedQuality) return (Math.Min(askedQuality, quality + 5), frameRate);
        }
        return (quality, frameRate);
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
        if (_init?.App is { } tab)
        {
            // App tab: its own monitor only, found by its place (the driver names them anew on every change) – there is
            // nothing else to switch to. Not there (the driver is reloading): no picture until it is.
            shown = AppTabView.Monitor(DisplayModes.VirtualMonitors(), tab.Place, _monitors);
            _scale = 1;
            _shown = shown;
            (_layoutWidth, _layoutHeight) = shown is null ? (0, 0) : (shown.Width, shown.Height);
            SendSync(DesktopWire.Json(DesktopMessage.Layout,
                new DesktopLayout(shown is null ? [] : [shown], shown?.Index ?? 0, _layoutWidth & ~1, _layoutHeight & ~1, screen.SecureDesktop, _init.ViewOnly),
                DesktopJson.Default.DesktopLayout));
            return;
        }
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
