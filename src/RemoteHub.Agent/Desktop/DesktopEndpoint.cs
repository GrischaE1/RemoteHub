using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace RemoteHub.Agent;

/// <summary>
/// <c>/v1/desktop</c> in the service: who may connect and how (rights, consent, view-only) is decided and enforced here;
/// the desktop helper in the session only does the screen work. Everything between RemoteHub and the helper passes
/// through this relay, which drops what the admin isn't allowed to send.
/// </summary>
internal sealed class DesktopEndpoint(AgentHostOptions o, ShellIdentity identity, AgentPolicy policy, string source)
{
    private readonly SemaphoreSlim _wsWrite = new(1, 1), _pipeWrite = new(1, 1);
    private volatile TaskCompletionSource<HelperConsent> _consent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebSocket _ws = null!;
    private volatile IDesktopHelper? _helper;
    private int _helperSession;
    private volatile bool _connected; // allowed once (the session counts as started)
    private volatile bool _live;      // the picture and input flow now (not while a user is being asked)
    private volatile bool _asking;    // waiting for the user's answer (Ask) or the code (Pin)
    private string _allowedBy = "";   // the signed-in user who allowed it (or was connected to without asking); "" = nobody
    private bool _viewOnly, _secureRight;
    private int _clipboard;           // what may go through the clipboard (DesktopClipboardLevel), 0 when viewing
    private string _pin = "";
    private int _pinAttempts = 3;
    private string _endReason = "closed by RemoteHub";
    private long _lastInput = Environment.TickCount64;
    private volatile bool _idleWarned;
    private bool _paused; // the session has no screen (disconnected RDP session): RemoteHub shows why the picture stands still
    private DesktopResume.Entry? _resume;
    private bool _lost; // the connection broke (not ended on purpose): it may be continued for a minute

    // Clipboard transfers on their way, by id: bytes still allowed. Both directions are checked here, not only in the helper.
    private readonly ConcurrentDictionary<int, long> _toDevice = new(), _fromDevice = new();

    private static readonly string[] Features =
        [DesktopFeatures.AllMonitors, DesktopFeatures.Stats, DesktopFeatures.Ping, DesktopFeatures.Resume, DesktopFeatures.Transfer, DesktopFeatures.TransferReceipt, DesktopFeatures.TransferOnPaste,
         DesktopFeatures.UserPrivacy, DesktopFeatures.HideApps];

    // Privacy: what the admin may do, and what is hidden now (as the helper last reported it).
    private bool _privacyRight, _privacyInputRight;
    private volatile DesktopPrivacy _privacy = DesktopPrivacy.Nothing;
    private DesktopPrivacyScreen? _screen; // the admin's privacy screen while on: given to a helper started later, too
    private string[]? _hiddenApps;         // the apps the user hid – kept here for the next helper, never sent to RemoteHub

    /// <summary>Windows hides a window from captures only from Windows 10 2004 on – before, the privacy screen would black out the picture too.</summary>
    internal static bool PrivacyScreenSupported => Environment.OSVersion.Version.Build >= 19041;

    public async Task RunAsync(WebSocket ws, CancellationToken aborted)
    {
        _ws = ws;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var ct = cts.Token;
        var desktop = o.Desktop!;
        var control = identity.InAny(policy.Groups(AgentRight.Desktop));
        _secureRight = identity.InAny(policy.Groups(AgentRight.SecureDesktop));
        var bypass = identity.InAny(policy.Groups(AgentRight.ConsentBypass));
        var modes = policy.ConsentModes.Where(m => m != DesktopConsent.None || bypass).ToArray();
        var sessions = desktop.Sessions.List();
        var started = DateTime.UtcNow;
        var what = "";
        Task? fromClient = null;
        _clipboard = control ? policy.Clipboard : DesktopClipboardLevel.Off;
        _privacyRight = control && PrivacyScreenSupported && policy.PrivacyScreen != PrivacyScreenLevel.Off && identity.InAny(policy.Groups(AgentRight.PrivacyScreen));
        _privacyInputRight = _privacyRight && policy.PrivacyScreen == PrivacyScreenLevel.WithInputBlock;
        string[] features = PrivacyScreenSupported && policy.PrivacyScreen != PrivacyScreenLevel.Off ? [.. Features, DesktopFeatures.PrivacyScreen] : Features;

        try
        {
            await SendWs(DesktopWire.Json(DesktopMessage.Hello, new DesktopHello(DesktopProtocol.Version, o.Config.DeviceLabel.Length > 0 ? o.Config.DeviceLabel : Environment.MachineName,
                identity.Account, new DesktopRights(control, _secureRight, bypass, _privacyRight, _privacyInputRight), modes, [.. sessions], policy.MaxFramesPerSecond, policy.MaxQuality,
                policy.ConsentTimeoutSeconds, features, _clipboard, AgentHost.AgentVersion), DesktopJson.Default.DesktopHello), ct);

            // ---- Start
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            var first = await ReceiveWs(startTimeout.Token);
            if (first is null || DesktopWire.TypeOf(first) != DesktopMessage.Start) return;
            var start = DesktopWire.ReadJson(first, DesktopJson.Default.DesktopStart);

            // Refused before anything is shown: audited like a refusal by the user.
            async Task Refuse(string reason)
            {
                o.Log.Audit(AuditEvent.DesktopRefused, $"Desktop refused: {identity.Account} from {source} – session {start.SessionId}: {reason}");
                await Status(DesktopStatusState.Failed, reason, ct);
            }

            var session = sessions.FirstOrDefault(s => s.Id == start.SessionId);
            if (session is null || session.State == DesktopSessionState.Disconnected)
            {
                await Refuse("This Windows session can't be shown (it ended or has no screen).");
                return;
            }
            _viewOnly = start.ViewOnly || !control;
            if (_viewOnly) _clipboard = DesktopClipboardLevel.Off;

            // Continuing after a dropped connection: who allowed it carries over (single-use token, same account, a minute).
            var resumed = start.Resume is { Length: > 0 } token ? await DesktopResume.TakeAsync(token, identity.Account) : null;
            if (resumed is not null)
            {
                _allowedBy = resumed.AllowedBy;
                _screen = _privacyRight && (resumed.Screen?.BlockLocalInput != true || _privacyInputRight) ? resumed.Screen : null;
            }

            // The user is asked only when someone is signed in and using the session; the sign-in and lock screen show nothing
            // private. Started there, the chosen mode is used later: when someone unlocks or signs in, they are asked first.
            var active = session.State == DesktopSessionState.Active;
            // Nobody to ask: the user who allowed it before (a resumed session).
            var alreadyAllowed = active && SameUser(session.User, _allowedBy);
            if (modes.Length == 0)
            {
                await Refuse("No way to connect is allowed on this device (policy).");
                return;
            }
            var followUp = modes.Contains(start.Consent) ? start.Consent
                : modes.Contains(DesktopConsent.Ask) ? DesktopConsent.Ask : modes[0];
            var consent = active && !alreadyAllowed ? start.Consent : DesktopConsent.None;
            // The admin's own signed-in session: they choose – connect right away, ask, or show a code.
            var ownSession = active && IsSelf(session.User);
            if (active && !alreadyAllowed && !modes.Contains(consent) && !(ownSession && consent == DesktopConsent.None))
            {
                await Refuse($"Connecting with \"{start.Consent}\" isn't allowed on this device.");
                return;
            }
            if (session.State is DesktopSessionState.SignIn or DesktopSessionState.Locked && !_secureRight)
            {
                await Refuse("Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device.");
                return;
            }
            if (consent == DesktopConsent.Pin) _pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            what = $"{(_viewOnly ? "view-only" : "control")}, session {session.Id} ({(session.User.Length > 0 ? session.User : "sign-in screen")}), " +
                (resumed is not null ? $"continued after a dropped connection (allowed by {(_allowedBy.Length > 0 ? _allowedBy : "nobody yet")})"
                : active ? $"consent {consent}" : $"consent {consent} (on unlock or sign-in: {followUp})");

            // ---- Helper in the session
            await Status(DesktopStatusState.Starting, resumed is not null ? "Continuing on the device…" : "Starting on the device…", ct);
            try { _helper = desktop.Helpers.Start(session.Id); }
            catch (Exception ex)
            {
                o.Log.Audit(AuditEvent.DesktopError, $"Desktop: could not start the helper in session {session.Id} for {identity.Account} from {source}: {ex.Message}");
                await Status(DesktopStatusState.Failed, "The remote desktop could not be started on the device. Details are in the agent's log.", ct);
                return;
            }
            _helperSession = session.Id;
            var monitor = start.Monitor == DesktopProtocol.AllMonitors ? DesktopProtocol.AllMonitors : Math.Max(0, start.Monitor);
            var init = new HelperInit(identity.Account, o.Config.DeviceLabel, consent, _pin, policy.ConsentTimeoutSeconds, _viewOnly, _secureRight,
                Math.Clamp(start.FramesPerSecond, 1, policy.MaxFramesPerSecond), Math.Clamp(start.Quality, 1, policy.MaxQuality), monitor,
                HoldUserDesktop: !active && !(session.State == DesktopSessionState.Locked && SameUser(session.User, _allowedBy)), Clipboard: _clipboard,
                VirtualDisplay: o.Config.VirtualDisplay != VirtualDisplayModes.Off && VirtualDisplay.Installed ? o.Config.VirtualDisplay : "",
                OfferToViewer: start.Features?.Contains(DesktopFeatures.TransferOnPaste) == true,
                // Continued after a dropped connection, nobody new asked: what the user hid stays hidden.
                UserHidden: resumed is not null && consent == DesktopConsent.None ? resumed.UserHidden : DesktopScreenHide.None,
                HiddenApps: resumed is not null && consent == DesktopConsent.None ? resumed.HiddenApps : null, AlwaysHidden: [.. policy.HiddenApps]);
            _hiddenApps = init.HiddenApps;
            _privacy = _privacy with { User = init.UserHidden };
            _settings = new DesktopSettings(init.Monitor, init.FramesPerSecond, init.Quality);
            await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.Init, init, HelperJson.Default.HelperInit), ct);

            fromClient = Task.Run(() => ClientLoop(ct), ct);
            var fromHelper = Task.Run(() => HelperLoop(_helper, ct), ct);

            // ---- Consent
            var answer = await AwaitConsent(consent, session.User, fromClient, fromHelper, ct);
            if (!answer.Granted)
            {
                o.Log.Audit(AuditEvent.DesktopRefused, $"Desktop refused: {identity.Account} from {source} – {what}: {answer.Reason}");
                await Status(DesktopStatusState.Denied, answer.Reason, ct);
                return;
            }
            if (active) _allowedBy = session.User;

            await SendPipe([(byte)HelperMessage.Begin], ct);
            _connected = _live = true;
            Touch();
            _resume = DesktopResume.Issue(identity.Account, _allowedBy, cts);
            _resume.UserHidden = _privacy.User;
            _resume.HiddenApps = _hiddenApps;
            _resume.Screen = _screen;
            if (_screen is { } resumedScreen) await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.PrivacyScreen, resumedScreen, DesktopJson.Default.DesktopPrivacyScreen), ct);
            await Status(DesktopStatusState.Connected, _viewOnly ? "Connected (view only)." : "Connected.", ct, resume: _resume.Token);
            o.Log.Audit(resumed is not null ? AuditEvent.DesktopResumed : AuditEvent.DesktopStarted,
                $"Desktop session {(resumed is not null ? "continued" : "started")}: {identity.Account} from {source} – {what}");

            // The helper can end without anyone wanting it: signing in switches desktops and often moves the console to a
            // new session. Then a new helper is started (in the console's current session). The connection was allowed –
            // but only by the user who allowed it: anyone else signed in there now is asked first. The same happens after
            // an unlock or sign-in within the session, which the helper reports (it holds the picture until then).
            var restarts = new Queue<DateTime>();
            while (true)
            {
                var watch = Task.Delay(TimeSpan.FromSeconds(2), ct);
                var ended = await Task.WhenAny(fromClient, fromHelper, watch);
                if (ended == fromClient)
                {
                    if (fromClient.IsFaulted)
                    {
                        // A broken connection may be continued with the resume token; a message that makes no sense may
                        // not – that's no network problem, and the token mustn't outlive it.
                        var error = fromClient.Exception!.GetBaseException();
                        _lost = !IsProtocolError(error);
                        _endReason = (_lost ? "connection lost: " : "invalid message from RemoteHub: ") + error.Message;
                    }
                    break;
                }
                if (ended == watch && await IdleCheck(ct)) break;
                if (ended == watch && _live) await PausedCheck(desktop, ct);
                var target = session.Console ? desktop.Sessions.ConsoleSessionId() ?? _helperSession : _helperSession;
                var userDesktop = false;
                if (ended == watch)
                {
                    if (target == _helperSession) continue;
                    o.Log.Write($"Desktop: the console moved from session {_helperSession} to {target} – following it");
                }
                else
                {
                    var exit = fromHelper.IsCompletedSuccessfully ? fromHelper.Result : new HelperExit(false, fromHelper.Exception?.GetBaseException().Message ?? "the desktop helper ended");
                    if (exit.ByUser)
                    {
                        _endReason = exit.Reason;
                        await Status(DesktopStatusState.Ended, exit.Reason, ct);
                        break;
                    }
                    userDesktop = exit.UserDesktop;
                    if (!userDesktop) o.Log.Write($"Desktop: helper in session {_helperSession} ended ({exit.Reason}) – starting it again");
                }
                if (!userDesktop) // an unlock is no failure: it doesn't count towards giving up
                {
                    while (restarts.Count > 0 && DateTime.UtcNow - restarts.Peek() > TimeSpan.FromMinutes(1)) restarts.Dequeue();
                    if (restarts.Count >= 5)
                    {
                        _endReason = "the device's screen kept failing";
                        await Status(DesktopStatusState.Ended, "The device's screen can't be shown at the moment (it kept failing). Details are in the agent's log.", ct);
                        break;
                    }
                    restarts.Enqueue(DateTime.UtcNow);
                    await Status(DesktopStatusState.Connected, "The device's screen changed (sign-in or another session) – reconnecting…", ct);
                }
                _live = false;
                var old = _helper;
                _helper = null;
                old?.Dispose();
                _toDevice.Clear();
                _fromDevice.Clear();
                try { await fromHelper.WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { /* ended with the old pipe */ }
                if (!userDesktop) await Task.Delay(TimeSpan.FromSeconds(1.5), ct); // let the new session or desktop settle
                target = session.Console ? desktop.Sessions.ConsoleSessionId() ?? target : target;

                // Who is there now? Anyone but the user who allowed it is asked; a lock or sign-in screen is held (only it is shown).
                var now = await SessionNow(target, userDesktop, ct);
                var signedIn = now is { State: DesktopSessionState.Active };
                // Whoever signed in is asked – unless it is the user who allowed it, or the admin signing in with their own account.
                var self = signedIn && !SameUser(now!.User, _allowedBy) && IsSelf(now.User);
                if (self)
                {
                    _allowedBy = now!.User;
                    _resume?.Update(_allowedBy);
                    o.Log.Audit(AuditEvent.DesktopUserAllowed, $"Desktop: {identity.Account} from {source} signed in to session {target} with their own account – continued without asking");
                }
                var ask = signedIn && !SameUser(now!.User, _allowedBy);
                var hold = !signedIn && !(now is { State: DesktopSessionState.Locked } && SameUser(now.User, _allowedBy));
                var mode = ask ? followUp : DesktopConsent.None;
                if (mode == DesktopConsent.Pin) (_pin, _pinAttempts) = (RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"), 3);
                if (ask) o.Log.Write($"Desktop: {now!.User} is signed in to session {target} now – " +
                    (mode == DesktopConsent.None ? "connected without asking (policy and right allow it)" : $"asking them ({mode})"));

                IDesktopHelper next;
                try { next = desktop.Helpers.Start(target); }
                catch (Exception ex)
                {
                    o.Log.Write($"Desktop: could not start the helper in session {target}: {ex.Message}");
                    fromHelper = Task.FromResult(new HelperExit(false, ex.Message));
                    continue;
                }
                _helperSession = target;
                NewConsent();
                _helper = next;
                fromHelper = Task.Run(() => HelperLoop(next, ct), ct);
                // The user who hid their screen keeps it hidden in the new helper; someone else signing in decides for themselves.
                await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.Init, init with { Consent = mode, Pin = mode == DesktopConsent.Pin ? _pin : "",
                    Monitor = _settings.Monitor, FramesPerSecond = _settings.FramesPerSecond, Quality = _settings.Quality, HoldUserDesktop = hold, Gdi = _gdi,
                    UserHidden = ask ? DesktopScreenHide.None : _privacy.User, HiddenApps = ask ? null : _hiddenApps },
                    HelperJson.Default.HelperInit), ct);
                if (ask)
                {
                    var reply = await AwaitConsent(mode, now!.User, fromClient, fromHelper, ct);
                    if (!reply.Granted)
                    {
                        _endReason = $"{now.User} didn't allow it ({reply.Reason})";
                        o.Log.Audit(AuditEvent.DesktopUserRefused, $"Desktop: {now.User} in session {target} didn't allow the remote desktop of {identity.Account} from {source} – ended ({reply.Reason})");
                        await Status(DesktopStatusState.Denied, reply.Reason, ct);
                        break;
                    }
                    _allowedBy = now.User;
                    _resume?.Update(_allowedBy);
                    o.Log.Audit(AuditEvent.DesktopUserAllowed, mode == DesktopConsent.None
                        ? $"Desktop: {now.User} signed in to session {target} – the remote desktop of {identity.Account} from {source} continued without asking (policy and right allow it)"
                        : $"Desktop: {now.User} in session {target} allowed the remote desktop of {identity.Account} from {source} ({mode})");
                }
                _privacy = _privacy with { Screen = DesktopPrivacyStyle.Off, LocalInputBlocked = false }; // the new helper starts without it
                await SendPipe([(byte)HelperMessage.Begin], ct);
                _live = true;
                if (_screen is { } screen) await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.PrivacyScreen, screen, DesktopJson.Default.DesktopPrivacyScreen), ct);
                if (ask) await Status(DesktopStatusState.Connected, mode == DesktopConsent.None ? $"{now!.User} signed in – connected." : $"{now!.User} allowed access.", ct);
                if (_settings.FitWidth > 0 || _settings.Monitor != init.Monitor)
                    await SendPipe(DesktopWire.Json(DesktopMessage.Settings, _settings, DesktopJson.Default.DesktopSettings), ct);
            }
        }
        catch (OperationCanceledException)
        {
            _lost = true;
            _endReason = _resume?.TakenOver == true ? "continued in a new connection" : "connection lost";
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
        {
            _lost = true;
            _endReason = "connection lost: " + ex.Message;
        }
        catch (Exception ex)
        {
            _endReason = "error: " + ex.Message;
            o.Log.Audit(AuditEvent.DesktopError, $"Desktop: unexpected error ({identity.Account} from {source}): {ex}");
            try { await Status(DesktopStatusState.Failed, "The remote desktop failed on the device. Details are in the agent's log.", CancellationToken.None); } catch { /* gone */ }
        }
        finally
        {
            // A clean close, so the last status (why it ended) reaches RemoteHub: send the close, then keep reading until
            // RemoteHub's close arrives. Cancelling the receive aborts the WebSocket, and closing the socket with unread
            // data (RemoteHub's acks) resets the connection – either can drop that status on the way.
            try
            {
                if (await _wsWrite.WaitAsync(TimeSpan.FromSeconds(2)))
                {
                    if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token);
                    _wsWrite.Release();
                }
                if (fromClient is not null) await Task.WhenAny(fromClient, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            catch { /* already gone */ }
            cts.Cancel();
            if (_connected && _resume?.TakenOver != true)
                o.Log.Audit(AuditEvent.DesktopEnded, $"Desktop session ended: {identity.Account} from {source} after {(DateTime.UtcNow - started):hh\\:mm\\:ss} – {what} ({_endReason})");
            _helper?.Dispose();
            if (_virtualShown) VirtualDisplay.Hide(); // switched off with the last connection that needed it
            // Only a broken connection may be continued; ended on purpose (RemoteHub, the user, idle, refused) it may not.
            if (_resume is { } r) { if (_lost) r.Release(); else r.Revoke(); }
        }
    }

    /// <summary>A disconnected session has no screen: the picture stands still – RemoteHub says why, and when it is back.</summary>
    private async Task PausedCheck(DesktopServices desktop, CancellationToken ct)
    {
        var disconnected = desktop.Sessions.IsDisconnected(_helperSession);
        if (disconnected == _paused) return;
        _paused = disconnected;
        if (disconnected)
        {
            o.Log.Write($"Desktop: session {_helperSession} is disconnected – no screen until someone connects to it");
            await Status(DesktopStatusState.Paused, "This Windows session is disconnected (its remote desktop connection was closed), so it has no screen right now. " +
                "The picture continues when someone connects to the session again.", ct);
        }
        else await Status(DesktopStatusState.Connected, "The session has a screen again.", ct);
    }

    private void Touch()
    {
        Interlocked.Exchange(ref _lastInput, Environment.TickCount64);
        _idleWarned = false;
    }

    /// <summary>Policy DesktopIdleTimeoutMinutes: a warning a minute before, then the end. True = ended.</summary>
    private async Task<bool> IdleCheck(CancellationToken ct)
    {
        var limit = policy.IdleTimeout;
        if (limit <= TimeSpan.Zero || !_live) return false;
        var idle = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastInput));
        var minutes = limit.TotalMinutes >= 1 ? $"{limit.TotalMinutes:0} minute(s)" : $"{limit.TotalSeconds:0} seconds";
        if (idle >= limit)
        {
            _endReason = $"no mouse or keyboard input for {minutes}";
            o.Log.Audit(AuditEvent.DesktopIdleEnded, $"Desktop: the session of {identity.Account} from {source} ended after {minutes} without input (policy)");
            await Status(DesktopStatusState.Ended, $"The session ended after {minutes} without mouse or keyboard input (a policy of the device).", ct);
            return true;
        }
        if (!_idleWarned && idle >= limit - TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, limit.Ticks / 2)))
        {
            _idleWarned = true;
            await Status(DesktopStatusState.Connected, "No mouse or keyboard input for a while – the session ends soon unless you use them.", ct);
        }
        return false;
    }

    /// <summary>RemoteHub → helper: only what the admin may send in this session.</summary>
    private async Task ClientLoop(CancellationToken ct)
    {
        while (await ReceiveWs(ct) is { } m)
        {
            switch (DesktopWire.TypeOf(m))
            {
                case DesktopMessage.Pin when _asking && _pin.Length > 0:
                    var entered = new string(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopPin).Pin.Where(char.IsDigit).ToArray());
                    if (CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(entered), System.Text.Encoding.ASCII.GetBytes(_pin)))
                        _consent.TrySetResult(new HelperConsent(true, ""));
                    else if (--_pinAttempts <= 0)
                        _consent.TrySetResult(new HelperConsent(false, "The code was entered wrong too often."));
                    else
                        await Status(DesktopStatusState.EnterPin, "That code isn't right. Try again.", ct, _pinAttempts);
                    break;
                case DesktopMessage.Ping:
                    await SendWs(DesktopWire.Ping(DesktopWire.ReadPing(m), DesktopMessage.Pong), ct);
                    break;
                case DesktopMessage.Settings when _live:
                    var s = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopSettings);
                    var all = s.Monitor == DesktopProtocol.AllMonitors;
                    // Changing the device's resolution is control: not for view-only, and not for the picture of all monitors.
                    var fit = !_viewOnly && !all && s.FitWidth >= 320 && s.FitHeight >= 200;
                    _settings = new DesktopSettings(all ? DesktopProtocol.AllMonitors : Math.Max(0, s.Monitor), Math.Clamp(s.FramesPerSecond, 1, policy.MaxFramesPerSecond),
                        Math.Clamp(s.Quality, 1, policy.MaxQuality), fit ? Math.Min(s.FitWidth, 7680) : 0, fit ? Math.Min(s.FitHeight, 4320) : 0);
                    await SendPipe(DesktopWire.Json(DesktopMessage.Settings, _settings, DesktopJson.Default.DesktopSettings), ct);
                    break;
                case DesktopMessage.Ack or DesktopMessage.KeyFrameRequest when _live:
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.Mouse or DesktopMessage.Key when _live && !_viewOnly:
                    Touch();
                    if (_privacy.User == DesktopScreenHide.None) await SendPipe(m, ct); // the user hid their screen: no input (the helper checks too)
                    break;
                case DesktopMessage.PrivacyScreen when _live && !_viewOnly:
                    await PrivacyScreen(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopPrivacyScreen), ct);
                    break;
                case DesktopMessage.Clipboard when _live && _clipboard >= DesktopClipboardLevel.Text:
                    Touch();
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferOffer when _live:
                    // Names and sizes only; the device's clipboard offers them until someone pastes (then: TransferRequest).
                    var offer = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                    if (TransferAllowed(offer) is { } notOffered)
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(offer.Id, false, notOffered, ToDevice: true), DesktopJson.Default.DesktopTransferEnd), ct);
                    else await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferRequest when _live:
                    // Pasted on the PC: the device sends what it offered.
                    Touch();
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferStart when _live:
                    var t = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                    if (TransferAllowed(t) is { } refused)
                    {
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(t.Id, false, refused, ToDevice: true), DesktopJson.Default.DesktopTransferEnd), ct);
                        break;
                    }
                    Touch();
                    _toDevice[t.Id] = t.Total;
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferData when _live:
                    if (Within(_toDevice, m)) await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferEnd:
                    var end = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                    if (_toDevice.TryRemove(end.Id, out _)) await SendPipe(m, ct);
                    break;
                case DesktopMessage.SecureAttention when _live && !_viewOnly:
                    Touch();
                    if (!_secureRight)
                    {
                        await Status(DesktopStatusState.Connected, "Your account may not use the sign-in screen on this device.", ct);
                        break;
                    }
                    if (_privacy.User != DesktopScreenHide.None)
                    {
                        await Status(DesktopStatusState.Connected, "The user at the device has hidden their screen – Ctrl+Alt+Del isn't sent until they show it again.", ct);
                        break;
                    }
                    // The console: sent by the service itself – Windows accepts it from services, and from session 0 it
                    // goes to the console. Another session (RDP): by the helper in that session.
                    if (o.Desktop!.Sessions.ConsoleSessionId() == _helperSession)
                    {
                        var sas = o.Desktop.SecureAttention();
                        LogSecureAttention("service", sas ?? "");
                        if (sas is not null) await Status(DesktopStatusState.Connected, sas, ct);
                    }
                    else await SendPipe([(byte)HelperMessage.SecureAttention], ct);
                    break;
            }
        }
    }

    /// <summary>The admin turns the privacy screen on, changes it or turns it off – checked here, shown by the helper.</summary>
    private async Task PrivacyScreen(DesktopPrivacyScreen asked, CancellationToken ct)
    {
        var style = DesktopPrivacyStyle.Valid(asked.Style);
        var refused = style == DesktopPrivacyStyle.Off ? null
            : !PrivacyScreenSupported ? "Windows on this device is too old for the privacy screen (it needs Windows 10 version 2004 or later)."
            : policy.PrivacyScreen == PrivacyScreenLevel.Off ? "The privacy screen is turned off on this device (policy)."
            : !_privacyRight ? "Your account may not use the privacy screen on this device."
            : asked.BlockLocalInput && !_privacyInputRight ? "Blocking the mouse and keyboard at the device isn't allowed on this device (policy)."
            : null;
        if (refused is not null)
        {
            o.Log.Audit(AuditEvent.DesktopPrivacyRefused, $"Desktop: privacy screen refused for {identity.Account} from {source} in session {_helperSession}: {refused}");
            await SendWs(DesktopWire.Json(DesktopMessage.Privacy, _privacy with { Reason = refused }, DesktopJson.Default.DesktopPrivacy), ct);
            return;
        }
        var screen = new DesktopPrivacyScreen(style, style != DesktopPrivacyStyle.Off && asked.BlockLocalInput);
        _screen = style == DesktopPrivacyStyle.Off ? null : screen;
        _resume?.Screen = _screen;
        await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.PrivacyScreen, screen, DesktopJson.Default.DesktopPrivacyScreen), ct);
    }

    /// <summary>The helper reports what is hidden now: audited when it changed, then passed on to RemoteHub.</summary>
    private async Task PrivacyReported(DesktopPrivacy now, CancellationToken ct)
    {
        var before = _privacy;
        _privacy = now;
        _resume?.UserHidden = now.User;
        if (now.HiddenApps != before.HiddenApps)
            o.Log.Audit(AuditEvent.DesktopUserHid, $"Desktop: the user in session {_helperSession} hides {now.HiddenApps} app(s) from {identity.Account} from {source}");
        if (now.User != before.User)
            o.Log.Audit(AuditEvent.DesktopUserHid, now.User == DesktopScreenHide.None
                ? $"Desktop: the user in session {_helperSession} shows their screen to {identity.Account} from {source} again"
                : $"Desktop: the user in session {_helperSession} {(now.User == DesktopScreenHide.Blur ? "blurred" : "hid")} their screen for {identity.Account} from {source}");
        if (now.Reason.Length == 0 && (now.Screen != before.Screen || now.LocalInputBlocked != before.LocalInputBlocked))
            o.Log.Audit(AuditEvent.DesktopPrivacyScreen, now.Screen == DesktopPrivacyStyle.Off
                ? $"Desktop: {identity.Account} from {source} turned the privacy screen off in session {_helperSession}"
                : $"Desktop: {identity.Account} from {source} turned the privacy screen on in session {_helperSession} ({now.Screen.ToLowerInvariant()}{(now.LocalInputBlocked ? ", mouse and keyboard at the device blocked" : "")})");
        if (now.Reason.Length > 0)
        {
            _screen = null; // it couldn't be shown (or had to come down): a new helper doesn't try again on its own
            _resume?.Screen = null;
            o.Log.Audit(AuditEvent.DesktopPrivacyRefused, $"Desktop: privacy screen for {identity.Account} from {source} in session {_helperSession} not shown: {now.Reason}");
        }
        await SendWs(DesktopWire.Json(DesktopMessage.Privacy, now, DesktopJson.Default.DesktopPrivacy), ct);
    }

    private volatile bool _gdi;
    private bool _virtualShown;
    private bool? _secureShown;
    private int _framesSinceSwitch;

    /// <summary>
    /// For the log: the picture switched between the user's desktop and the secure one (sign-in, lock, UAC) – and a few
    /// seconds later, whether frames of the new screen arrived (a picture that stands still shows up here).
    /// </summary>
    private void LogDesktopSwitch(bool secure)
    {
        if (_secureShown == secure) return;
        var first = _secureShown is null;
        _secureShown = secure;
        Interlocked.Exchange(ref _framesSinceSwitch, 0);
        if (first) return;
        var what = secure ? "the secure desktop (sign-in, lock or UAC screen)" : "the user's desktop";
        o.Log.Write($"Desktop: session {_helperSession} switched to {what}");
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            if (_secureShown == secure) o.Log.Write($"Desktop: {Volatile.Read(ref _framesSinceSwitch)} frame(s) of {what} sent in 3 s");
        });
    }

    private void LogSecureAttention(string by, string error) =>
        o.Log.Write($"Desktop: Ctrl+Alt+Del for {identity.Account} in session {_helperSession} by the {by}: {(error.Length == 0 ? "sent" : error)} " +
                    $"(SoftwareSASGeneration = {SecureAttention.Current()?.ToString() ?? "not set"}, console session {o.Desktop?.Sessions.ConsoleSessionId()?.ToString() ?? "none"}" +
                    (by == "service" ? $", {SecureAttention.LastDetail})" : ")"));

    /// <summary>Malformed or incomplete messages (bad JSON, missing fields, wrong sizes) – as opposed to a broken connection.</summary>
    private static bool IsProtocolError(Exception e) =>
        e is System.Text.Json.JsonException or InvalidDataException or NullReferenceException or ArgumentException or FormatException or IndexOutOfRangeException;

    /// <summary>Null if this clipboard transfer may go through, else why not.</summary>
    private string? TransferAllowed(DesktopTransfer t)
    {
        var need = t.Kind switch { DesktopTransferKind.Image => DesktopClipboardLevel.Images, DesktopTransferKind.Files => DesktopClipboardLevel.Files, _ => int.MaxValue };
        if (_clipboard < need)
            return t.Kind == DesktopTransferKind.Files ? "Copying files isn't allowed on this device." : "Copying images isn't allowed on this device.";
        if (t.Items is null || t.Items.Any(i => i is null || t.Kind == DesktopTransferKind.Files && string.IsNullOrEmpty(i.Path)))
            return "The copied items are incomplete.";
        if (t.Items.Length == 0 || t.Items.Length > DesktopProtocol.MaxTransferItems || t.Total < 0 || t.Total > DesktopProtocol.MaxTransferBytes ||
            t.Items.Any(i => i.Size < 0 || i.Folder && i.Size != 0) || t.Items.Sum(i => i.Size) != t.Total ||
            t.Kind == DesktopTransferKind.Image && (t.Items.Length != 1 || t.Items[0].Folder))
            return $"Too much at once (at most {DesktopProtocol.MaxTransferBytes / (1024 * 1024)} MB and {DesktopProtocol.MaxTransferItems} files).";
        return null;
    }

    /// <summary>Transfer data for a known transfer, not beyond what its start announced.</summary>
    private static bool Within(ConcurrentDictionary<int, long> open, byte[] m)
    {
        var (id, _) = DesktopWire.ReadTransferData(m);
        if (!open.TryGetValue(id, out var left)) return false;
        var bytes = m.Length - DesktopWire.TransferHeaderSize;
        if (bytes > left) { open.TryRemove(id, out _); return false; }
        open[id] = left - bytes;
        return true;
    }

    private DesktopSettings _settings = new(0, DesktopProtocol.DefaultFramesPerSecond, DesktopProtocol.DefaultQuality);

    private sealed record HelperExit(bool ByUser, string Reason, bool UserDesktop = false);

    private static bool SameUser(string a, string b) => a.Length > 0 && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The session's user is the account the admin signed in to the agent with.</summary>
    private bool IsSelf(string user) => SameUser(user, identity.Account);

    /// <summary>".\user" → "MACHINE\user" (local accounts are written both ways).</summary>
    private static string Normalize(string account) => account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account[1..] : account;

    /// <summary>A fresh slot for the next answer – made before the helper that asks gets its Init.</summary>
    private void NewConsent() => _consent = new TaskCompletionSource<HelperConsent>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Asks the user at the device (Ask / Pin) and waits for the answer (after <see cref="NewConsent"/>); None is granted right away.</summary>
    private async Task<HelperConsent> AwaitConsent(string mode, string user, Task fromClient, Task fromHelper, CancellationToken ct)
    {
        if (mode == DesktopConsent.None) return new HelperConsent(true, "");
        var consent = _consent; // set up before the helper was told to ask (NewConsent): a quick answer isn't lost
        _asking = true;
        try
        {
            if (mode == DesktopConsent.Ask) await Status(DesktopStatusState.WaitingForUser, $"Waiting for {user} to allow access…", ct);
            else await Status(DesktopStatusState.EnterPin, $"Ask {user} for the code shown on their screen.", ct, _pinAttempts);
            // Ask: the helper counts down itself; picking apps to hide stops its countdown, so this is only the last resort.
            var timeout = mode == DesktopConsent.Pin ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(policy.ConsentTimeoutSeconds + 10 + 180);
            var decided = await Task.WhenAny(consent.Task, Task.Delay(timeout, ct), fromClient, fromHelper);
            return decided == consent.Task ? consent.Task.Result
                : decided == fromClient || decided == fromHelper ? new HelperConsent(false, "The connection ended before access was granted.")
                : new HelperConsent(false, "No answer in time.");
        }
        finally { _asking = false; }
    }

    /// <summary>
    /// The session as it is now. After an unlock or sign-in (<paramref name="userDesktop"/>) it is someone's desktop even if
    /// Windows doesn't say so yet (the lock screen lingers, the user name comes late): waits a few seconds for the name.
    /// </summary>
    private async Task<DesktopSessionInfo?> SessionNow(int id, bool userDesktop, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            var s = o.Desktop!.Sessions.List().FirstOrDefault(x => x.Id == id);
            if (!userDesktop) return s;
            if (s is { User.Length: > 0 }) return s with { State = DesktopSessionState.Active };
            // Still no name (or the session is gone from the list): someone is there all the same – they are asked.
            if (i >= 20) return (s ?? new DesktopSessionInfo(id, "", DesktopSessionState.Active, false, "")) with { State = DesktopSessionState.Active, User = "the signed-in user" };
            await Task.Delay(250, ct);
        }
    }

    /// <summary>Helper → RemoteHub: screen, pointer, clipboard, stats; consent answers and the end are handled here.</summary>
    private async Task<HelperExit> HelperLoop(IDesktopHelper? helper, CancellationToken ct)
    {
        if (helper is null) return new HelperExit(false, "no desktop helper");
        try
        {
            while (await DesktopWire.ReadFramedAsync(helper.Output, ct) is { } m)
            {
                switch ((byte)DesktopWire.TypeOf(m))
                {
                    case (byte)HelperMessage.Consent when helper == _helper:
                        var c = DesktopWire.ReadJson(m, HelperJson.Default.HelperConsent);
                        _consent.TrySetResult(c);
                        break;
                    case (byte)HelperMessage.DisplayRequest when helper == _helper:
                        var size = DesktopWire.ReadText(m).Split(' ');
                        if (size.Length == 2 && int.TryParse(size[0], out var vw) && int.TryParse(size[1], out var vh))
                        {
                            var shown = await Task.Run(() => VirtualDisplay.Show(vw, vh, resizeOnly: _virtualShown), ct);
                            if (shown is null && !_virtualShown) _virtualShown = true;
                            o.Log.Write($"Desktop: session {_helperSession}: virtual monitor {(shown is null ? $"on at {VirtualDisplay.Normalize(vw, vh).W}×{VirtualDisplay.Normalize(vw, vh).H}" : "not switched on: " + shown)}");
                        }
                        break;
                    case (byte)HelperMessage.Log when helper == _helper:
                        var note = DesktopWire.ReadText(m);
                        if (note.Contains("stood still")) _gdi = true; // the next helpers (after a sign-in) start with GDI
                        o.Log.Write($"Desktop: session {_helperSession}: {note}");
                        break;
                    case (byte)HelperMessage.UserDesktop:
                        o.Log.Write($"Desktop: the helper in session {_helperSession} saw a user's desktop ({DesktopWire.ReadText(m)})");
                        return new HelperExit(false, "a user's desktop appeared", UserDesktop: true);
                    case (byte)HelperMessage.SecureAttentionResult when helper == _helper:
                        var sas = DesktopWire.ReadText(m);
                        LogSecureAttention("helper", sas);
                        if (sas.Length > 0) await Status(DesktopStatusState.Connected, sas, ct);
                        break;
                    case (byte)HelperMessage.End:
                        var end = DesktopWire.ReadJson(m, HelperJson.Default.HelperEnd);
                        if (!_connected)
                        {
                            _endReason = end.Reason;
                            await Status(DesktopStatusState.Ended, end.Reason, ct);
                        }
                        return new HelperExit(end.ByUser, end.Reason);
                    case (byte)DesktopMessage.Layout or (byte)DesktopMessage.Video or (byte)DesktopMessage.CursorShape
                        or (byte)DesktopMessage.CursorPosition or (byte)DesktopMessage.Stats when _live && helper == _helper:
                        if (m[0] == (byte)DesktopMessage.Video) Interlocked.Increment(ref _framesSinceSwitch);
                        else if (m[0] == (byte)DesktopMessage.Layout) LogDesktopSwitch(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopLayout).SecureDesktop);
                        await SendWs(m, ct);
                        break;
                    case (byte)HelperMessage.HiddenApps when helper == _helper:
                        _hiddenApps = DesktopWire.ReadJson(m, HelperJson.Default.HelperHiddenApps).Apps;
                        _resume?.HiddenApps = _hiddenApps;
                        break;
                    case (byte)DesktopMessage.Privacy when helper == _helper:
                        await PrivacyReported(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopPrivacy), ct);
                        break;
                    case (byte)DesktopMessage.Clipboard when _live && helper == _helper && _clipboard >= DesktopClipboardLevel.Text:
                        await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferStart when _live && helper == _helper:
                        var t = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                        if (TransferAllowed(t) is not null) break; // the helper checks too; nothing is announced
                        _fromDevice[t.Id] = t.Total;
                        await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferData when _live && helper == _helper:
                        if (Within(_fromDevice, m)) await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferOffer when _live && helper == _helper:
                        // Copied on the device: names and sizes for RemoteHub's clipboard – the data follows when pasted there.
                        if (TransferAllowed(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer)) is null) await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferRequest when _live && helper == _helper:
                        Touch();
                        await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferEnd when helper == _helper:
                        var te = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                        // The device's own transfer ended – or one to the device arrived there (receipt) or failed there.
                        if ((te.ToDevice ? _toDevice.TryRemove(te.Id, out _) : _fromDevice.TryRemove(te.Id, out _)) || !te.Ok) await SendWs(m, ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { return new HelperExit(false, ex.Message); }
        return new HelperExit(false, "the desktop helper ended");
    }

    private Task Status(string state, string message, CancellationToken ct, int attempts = 0, string? resume = null) =>
        SendWs(DesktopWire.Json(DesktopMessage.Status, new DesktopStatus(state, message, attempts, resume), DesktopJson.Default.DesktopStatus), ct);

    private async Task SendWs(byte[] m, CancellationToken ct)
    {
        await _wsWrite.WaitAsync(ct);
        try { await _ws.SendAsync(m, WebSocketMessageType.Binary, true, ct); }
        finally { _wsWrite.Release(); }
    }

    /// <summary>To the current helper; dropped while it is being replaced (its loss is handled by the restart).</summary>
    private async Task SendPipe(byte[] m, CancellationToken ct)
    {
        await _pipeWrite.WaitAsync(ct);
        try
        {
            if (_helper is { } h) await DesktopWire.WriteFramedAsync(h.Input, m, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* helper gone */ }
        finally { _pipeWrite.Release(); }
    }

    private async Task<byte[]?> ReceiveWs(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (message.Length > DesktopProtocol.MaxMessageBytes) throw new InvalidDataException("Message too large.");
            if (!r.EndOfMessage) continue;
            if (message.Length == 0) continue;
            return message.ToArray();
        }
    }
}

/// <summary>
/// Tokens to continue a remote desktop after its connection dropped (Wi-Fi, VPN), in the service's memory only: random,
/// single-use, for the same Windows account, usable for a minute after the old connection ended. If the old connection
/// still looks alive (a dead network isn't noticed at once), the new one takes over and the old one is ended.
/// </summary>
internal static class DesktopResume
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal sealed class Entry(string token, string account, string allowedBy, CancellationTokenSource owner)
    {
        public string Token => token;
        public string Account => account;
        public string AllowedBy { get; private set; } = allowedBy;
        public bool TakenOver { get; set; }
        internal DateTime? ExpiresUtc;
        internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Owner => owner;

        public void Update(string allowedBy) => AllowedBy = allowedBy;

        /// <summary>What the user at the device hid: still hidden in the continued connection.</summary>
        public string UserHidden { get; set; } = DesktopScreenHide.None;

        /// <summary>The admin's privacy screen, if it was on: on again in the continued connection.</summary>
        public DesktopPrivacyScreen? Screen { get; set; }

        /// <summary>The apps the user hid: still hidden in the continued connection.</summary>
        public string[]? HiddenApps { get; set; }

        /// <summary>The connection broke: usable for <see cref="Window"/>.</summary>
        public void Release()
        {
            ExpiresUtc = DateTime.UtcNow + Window;
            Ended.TrySetResult();
        }

        /// <summary>Ended on purpose: not usable.</summary>
        public void Revoke()
        {
            Entries.TryRemove(token, out _);
            Ended.TrySetResult();
        }
    }

    public static Entry Issue(string account, string allowedBy, CancellationTokenSource owner)
    {
        foreach (var (k, e) in Entries)
            if (e.ExpiresUtc is { } x && x < DateTime.UtcNow) Entries.TryRemove(k, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var entry = new Entry(token, account, allowedBy, owner);
        Entries[token] = entry;
        return entry;
    }

    /// <summary>
    /// The entry if the token is valid for this account – then it is used up. Presented by another account it stays
    /// valid: that account can't use it, and mustn't be able to spoil the reconnect of the one it belongs to.
    /// </summary>
    public static async Task<Entry?> TakeAsync(string token, string account)
    {
        if (!Entries.TryGetValue(token, out var e) || !string.Equals(e.Account, account, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Entries.TryRemove(new KeyValuePair<string, Entry>(token, e))) return null; // taken at the same moment by another connection
        if (e.ExpiresUtc is { } x && x < DateTime.UtcNow) return null;
        if (e.ExpiresUtc is null)
        {
            // The old connection hasn't noticed yet: end it, then continue here.
            e.TakenOver = true;
            try { e.Owner.Cancel(); } catch (ObjectDisposedException) { }
            await Task.WhenAny(e.Ended.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        return e;
    }
}
