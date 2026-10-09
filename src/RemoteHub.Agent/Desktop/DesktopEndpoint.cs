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
        [DesktopFeatures.AllMonitors, DesktopFeatures.Stats, DesktopFeatures.Ping, DesktopFeatures.Resume, DesktopFeatures.Transfer];

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

        try
        {
            await SendWs(DesktopWire.Json(DesktopMessage.Hello, new DesktopHello(DesktopProtocol.Version, o.Config.DeviceLabel.Length > 0 ? o.Config.DeviceLabel : Environment.MachineName,
                identity.Account, new DesktopRights(control, _secureRight, bypass), modes, [.. sessions], policy.MaxFramesPerSecond, policy.MaxQuality,
                policy.ConsentTimeoutSeconds, Features, _clipboard, AgentHost.AgentVersion), DesktopJson.Default.DesktopHello), ct);

            // ---- Start
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            var first = await ReceiveWs(startTimeout.Token);
            if (first is null || DesktopWire.TypeOf(first) != DesktopMessage.Start) return;
            var start = DesktopWire.ReadJson(first, DesktopJson.Default.DesktopStart);
            var session = sessions.FirstOrDefault(s => s.Id == start.SessionId);
            if (session is null || session.State == DesktopSessionState.Disconnected)
            {
                await Status(DesktopStatusState.Failed, "This Windows session can't be shown (it ended or has no screen).", ct);
                return;
            }
            _viewOnly = start.ViewOnly || !control;
            if (_viewOnly) _clipboard = DesktopClipboardLevel.Off;

            // Continuing after a dropped connection: who allowed it carries over (single-use token, same account, a minute).
            var resumed = start.Resume is { Length: > 0 } token ? await DesktopResume.TakeAsync(token, identity.Account) : null;
            if (resumed is not null) _allowedBy = resumed.AllowedBy;

            // The user is asked only when someone is signed in and using the session; the sign-in and lock screen show nothing
            // private. Started there, the chosen mode is used later: when someone unlocks or signs in, they are asked first.
            var active = session.State == DesktopSessionState.Active;
            // Nobody to ask: the user who allowed it before (a resumed session), or the admin's own account.
            var alreadyAllowed = active && (SameUser(session.User, _allowedBy) || IsSelf(session.User));
            if (modes.Length == 0)
            {
                await Status(DesktopStatusState.Failed, "No way to connect is allowed on this device (policy).", ct);
                return;
            }
            var followUp = modes.Contains(start.Consent) ? start.Consent
                : modes.Contains(DesktopConsent.Ask) ? DesktopConsent.Ask : modes[0];
            var consent = active && !alreadyAllowed ? start.Consent : DesktopConsent.None;
            if (active && !alreadyAllowed && !modes.Contains(consent))
            {
                await Status(DesktopStatusState.Failed, $"Connecting with \"{start.Consent}\" isn't allowed on this device.", ct);
                return;
            }
            if (session.State is DesktopSessionState.SignIn or DesktopSessionState.Locked && !_secureRight)
            {
                await Status(DesktopStatusState.Failed, "Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device.", ct);
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
                HoldUserDesktop: !active && !(session.State == DesktopSessionState.Locked && SameUser(session.User, _allowedBy)), Clipboard: _clipboard);
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
                        _lost = true;
                        _endReason = "connection lost: " + fromClient.Exception!.GetBaseException().Message;
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
                await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.Init, init with { Consent = mode, Pin = mode == DesktopConsent.Pin ? _pin : "",
                    Monitor = _settings.Monitor, FramesPerSecond = _settings.FramesPerSecond, Quality = _settings.Quality, HoldUserDesktop = hold },
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
                await SendPipe([(byte)HelperMessage.Begin], ct);
                _live = true;
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
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.Clipboard when _live && _clipboard >= DesktopClipboardLevel.Text:
                    Touch();
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferStart when _live:
                    var t = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                    if (TransferAllowed(t) is { } refused)
                    {
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(t.Id, false, refused), DesktopJson.Default.DesktopTransferEnd), ct);
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
                    var error = _secureRight ? o.Desktop!.SecureAttention() : "Your account may not use the sign-in screen on this device.";
                    if (error is not null) await Status(DesktopStatusState.Connected, error, ct);
                    break;
            }
        }
    }

    /// <summary>Null if this clipboard transfer may go through, else why not.</summary>
    private string? TransferAllowed(DesktopTransfer t)
    {
        var need = t.Kind switch { DesktopTransferKind.Image => DesktopClipboardLevel.Images, DesktopTransferKind.Files => DesktopClipboardLevel.Files, _ => int.MaxValue };
        if (_clipboard < need)
            return t.Kind == DesktopTransferKind.Files ? "Copying files isn't allowed on this device." : "Copying images isn't allowed on this device.";
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
            var timeout = mode == DesktopConsent.Pin ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(policy.ConsentTimeoutSeconds + 10);
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
                    case (byte)HelperMessage.UserDesktop:
                        return new HelperExit(false, "a user's desktop appeared", UserDesktop: true);
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
                        await SendWs(m, ct);
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
                    case (byte)DesktopMessage.TransferEnd when helper == _helper:
                        var te = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                        if (_fromDevice.TryRemove(te.Id, out _) || !te.Ok) await SendWs(m, ct); // also: a transfer to the device failed there
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

    /// <summary>The entry if the token is valid for this account (it is used up either way).</summary>
    public static async Task<Entry?> TakeAsync(string token, string account)
    {
        if (!Entries.TryRemove(token, out var e)) return null;
        if (!string.Equals(e.Account, account, StringComparison.OrdinalIgnoreCase)) return null;
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
