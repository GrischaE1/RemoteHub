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
    private volatile int _monitorCount; // in the helper's last layout; 0 before one arrived
    private const int MaxMonitors = 64;

    /// <summary>
    /// A monitor index from the wire: all monitors, or one the device has as far as the helper's last layout says (at
    /// most <see cref="MaxMonitors"/> before one arrived) – the helper never sees an index outside that.
    /// </summary>
    internal static int ClampMonitor(int requested, int known) =>
        requested == DesktopProtocol.AllMonitors ? requested : Math.Clamp(requested, 0, (known > 0 ? known : MaxMonitors) - 1);
    private volatile bool _idleWarned;
    private bool _paused; // the session has no screen (disconnected RDP session): RemoteHub shows why the picture stands still
    private DesktopResume.Entry? _resume;
    private bool _lost; // the connection broke (not ended on purpose): it may be continued for a minute

    // Clipboard transfers on their way, by id: bytes still allowed. Both directions are checked here, not only in the helper.
    private readonly ConcurrentDictionary<int, long> _toDevice = new(), _fromDevice = new();
    // Files dropped on the tab and on their way: audited once the helper reports them saved.
    private readonly ConcurrentDictionary<int, DesktopTransfer> _drops = new();

    private static readonly string[] Features =
        [DesktopFeatures.AllMonitors, DesktopFeatures.Stats, DesktopFeatures.Ping, DesktopFeatures.Resume, DesktopFeatures.Transfer, DesktopFeatures.TransferReceipt, DesktopFeatures.TransferOnPaste,
         DesktopFeatures.UserPrivacy, DesktopFeatures.HideApps, DesktopFeatures.DropFiles];

    // App tab (docs/app-tabs-design.md): the app to start, its monitor, its process and what the helper is told – kept
    // here, so they outlive a helper restart (an automatic sign-in moves the console to a new session).
    private DesktopAppStart? _app;
    private VirtualDisplay.AppLease? _appLease;
    private AppProcess? _appProcess;
    private HelperApp? _helperApp;
    private int? _appSession; // the session the app runs in, once started
    private volatile bool _appClosed;  // the app ended by itself: the tab offers to start it again
    private volatile bool _appClosing; // the admin closed the tab: the app's end ends the connection

    /// <summary>
    /// App tabs whose app runs, per session. Several tabs share the session the first one's automatic sign-in made: its
    /// end action (sign out, by default) waits for the last of them (§4.3), whichever connection signed in.
    /// </summary>
    internal static readonly ConcurrentDictionary<int, int> AppTabsOpen = new();

    /// <summary>
    /// App tabs of an account (by SID) whose automatic sign-in is under way: the account's further app tabs wait for it
    /// and then use the session it made, rather than starting a second sign-in (§4.1).
    /// </summary>
    internal static readonly ConcurrentDictionary<string, TaskCompletionSource> AppSignIns = new(StringComparer.OrdinalIgnoreCase);
    private (string Sid, TaskCompletionSource Done)? _appSignIn;

    /// <summary>How long an app tab waits for another tab's sign-in.</summary>
    internal static TimeSpan AppSignInWait = TimeSpan.FromMinutes(2);

    /// <summary>This tab's sign-in is over (signed in and the app started, failed, or the connection ended): the waiting tabs go on.</summary>
    private void AppSignInDone()
    {
        if (_appSignIn is not { } mine) return;
        _appSignIn = null;
        AppSignIns.TryRemove(new KeyValuePair<string, TaskCompletionSource>(mine.Sid, mine.Done));
        mine.Done.TrySetResult();
    }

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
        var autoSignInWhyNot = AutoSignInRefusal(policy, control);
        var autoSignInRight = autoSignInWhyNot is null;
        // App tabs where the virtual display is installed and the policy allows them; for this account only where automatic
        // sign-in is available to it too (app-tabs-design §4.1, §9).
        var appsRight = AppTabs.Refusal(policy, control, autoSignInWhyNot, VirtualDisplay.Installed) is null;
        if (VirtualDisplay.Installed && policy.DesktopApps) features = [.. features, DesktopFeatures.Apps];
        if (autoSignInRight) features = [.. features, DesktopFeatures.AutoSignIn];
        // The device allows it by policy, yet it isn't offered: say why, so the log answers "where is the option?"
        else if (policy.NoUserBehavior == NoUserBehaviors.AutoSignIn && sessions.Any(s => s.Console && s.State == DesktopSessionState.SignIn))
            o.Log.Write($"Desktop: automatic sign-in not offered to {identity.Account}: {autoSignInWhyNot}");

        try
        {
            await SendWs(DesktopWire.Json(DesktopMessage.Hello, new DesktopHello(DesktopProtocol.Version, o.Config.DeviceLabel.Length > 0 ? o.Config.DeviceLabel : Environment.MachineName,
                identity.Account, new DesktopRights(control, _secureRight, bypass, _privacyRight, _privacyInputRight, autoSignInRight,
                    Apps: appsRight, AppsElevated: appsRight && policy.DesktopAppsElevated), modes, [.. sessions], policy.MaxFramesPerSecond, policy.MaxQuality,
                policy.ConsentTimeoutSeconds, features, _clipboard, AgentHost.AgentVersion), DesktopJson.Default.DesktopHello), ct);

            // ---- Start
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startTimeout.CancelAfter(TimeSpan.FromMinutes(5));
            var first = await ReceiveWs(startTimeout.Token);
            if (first is not null && DesktopWire.TypeOf(first) == DesktopMessage.AppList)
            {
                // The Start menu list (app-tabs-design §6.5): instead of a start, once; no session needed, nothing runs.
                if (AppTabs.Refusal(policy, control, autoSignInWhyNot, VirtualDisplay.Installed) is { } listWhyNot)
                {
                    await Status(DesktopStatusState.Failed, listWhyNot, ct);
                    return;
                }
                var list = await Task.Run(() => AppStartMenu.List(policy, identity.UserSid), ct);
                o.Log.Write($"Desktop: the Start menu list for {identity.Account} from {source}: {list.Apps.Length} app(s){(list.Truncated ? ", more left out" : "")}");
                await SendWs(DesktopWire.Json(DesktopMessage.AppList, list, DesktopJson.Default.DesktopAppList), ct);
                return;
            }
            if (first is null || DesktopWire.TypeOf(first) != DesktopMessage.Start) return;
            var start = DesktopWire.ReadJson(first, DesktopJson.Default.DesktopStart);

            // Refused before anything is shown: audited like a refusal by the user.
            async Task Refuse(string reason)
            {
                o.Log.Audit(AuditEvent.DesktopRefused, $"Desktop refused: {identity.Account} from {source} – session {start.SessionId}: {reason}");
                await Status(DesktopStatusState.Failed, reason, ct);
            }

            // App tab: the account's own console session, or automatic sign-in when nobody is signed in (§4.1) – never
            // someone else's. Its monitor is taken before anything starts, so a full device refuses at once.
            if (start.App is { } app)
            {
                if (AppTabs.Refusal(policy, control, autoSignInWhyNot, VirtualDisplay.Installed) is { } notHere)
                {
                    await Refuse(notHere);
                    return;
                }
                var console = sessions.FirstOrDefault(s => s.Console);
                // One of the account's app tabs signs in, the others wait for it and then use its session – also while the
                // new session still shows as locked (Windows 11 shows its lock screen for a few seconds after a sign-in).
                var waitedTooLong = false;
                while (identity.UserSid is { } signer)
                {
                    if (!waitedTooLong && AppSignIns.TryGetValue(signer, out var other))
                    {
                        await Status(DesktopStatusState.StartingApp, $"Waiting for the sign-in of your other app tab on {DeviceName}…", ct);
                        try { await other.Task.WaitAsync(AppSignInWait, ct); }
                        catch (TimeoutException) { waitedTooLong = true; } // that tab is stuck: this one looks for itself
                        sessions = desktop.Sessions.List();
                        console = sessions.FirstOrDefault(s => s.Console);
                        continue;
                    }
                    if (console is { State: DesktopSessionState.SignIn })
                    {
                        var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (AppSignIns.TryAdd(signer, mine)) _appSignIn = (signer, mine);
                        else if (!waitedTooLong) continue; // another tab began signing in just now: wait for it
                    }
                    break;
                }
                if (AppSessionRefusal(console) is { } notThere)
                {
                    await Refuse(notThere);
                    return;
                }
                _app = app;
                start = start with
                {
                    SessionId = console!.Id, Consent = DesktopConsent.None, ViewOnly = false, Monitor = 0,
                    AutoSignIn = console.State == DesktopSessionState.SignIn,
                    // The apps were started for these tabs: signed out at the end unless the policy or RemoteHub says otherwise (§12, Q1).
                    AutoSignInEndAction = start.AutoSignInEndAction ?? AutoSignInEndActions.SignOut,
                };
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
            if (_app is { } tabApp)
            {
                // Continued after a drop: the same monitor and the same running app – never a second copy of either.
                if (resumed?.App is { } carried && carried.TryClaim())
                    (_appLease, _appProcess, _helperApp, _appSession) = (carried.Lease, carried.Process, carried.Helper, carried.Session);
                else
                {
                    var lease = _appLease = VirtualDisplay.NewAppLease();
                    var (why, tabMonitor, note) = await Task.Run(() => (VirtualDisplay.Open(lease, 1280, 720, policy.MaxAppTabs, out var m, out var n), m, n), ct);
                    if (note is not null) o.Log.Write($"Desktop: app tab: {note}");
                    if (why is not null || tabMonitor is null)
                    {
                        await Refuse($"{tabApp.Name} can't get a monitor of its own: {why}");
                        return;
                    }
                    _helperApp = new HelperApp(tabMonitor.Place, "", tabApp.Name);
                }
            }
            if (resumed is not null)
            {
                _allowedBy = resumed.AllowedBy;
                _screen = _privacyRight && (resumed.Screen?.BlockLocalInput != true || _privacyInputRight) || resumed.ScreenByService ? resumed.Screen : null;
                _screenByService = resumed.ScreenByService && _screen is not null;
                // A session automatic sign-in created: this connection takes over its end action and the cover kept since the drop.
                if (o.Desktop!.AutoSigned.Owned(session.Id) is { } owned && string.Equals(owned.Sid, identity.UserSid, StringComparison.OrdinalIgnoreCase))
                {
                    _autoSigned = (session.Id, owned.EndAction);
                    _parkedHelper = o.Desktop.AutoSigned.Resumed(session.Id);
                }
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
            var ownSession = active && IsSelfSession(session);
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
            // Automatic sign-in: decided now (§6.2 checks 1–5); the sign-in itself runs once the sign-in screen is shown.
            var autoSignInRefused = start.AutoSignIn && resumed is null ? AutoSignInStartRefusal(policy, control, session) : null;
            var autoSignIn = start.AutoSignIn && resumed is null && autoSignInRefused is null;
            if (_app is not null && autoSignInRefused is { } noSignIn)
            {
                AppSignInDone();
                await Refuse(noSignIn.Message);
                return;
            }
            what = $"{(_app is { } whatApp ? $"app tab {whatApp.Name}, " : "")}{(_viewOnly ? "view-only" : "control")}, session {session.Id} ({(session.User.Length > 0 ? session.User : "sign-in screen")}), " +
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
            var monitor = ClampMonitor(start.Monitor, 0);
            var init = new HelperInit(identity.Account, o.Config.DeviceLabel, consent, _pin, policy.ConsentTimeoutSeconds, _viewOnly, _secureRight,
                Math.Clamp(start.FramesPerSecond, 1, policy.MaxFramesPerSecond), Math.Clamp(start.Quality, 1, policy.MaxQuality), monitor,
                HoldUserDesktop: !active && !(session.State == DesktopSessionState.Locked && SameUser(session.User, _allowedBy)), Clipboard: _clipboard,
                VirtualDisplay: policy.VirtualDisplay != VirtualDisplayModes.Off && VirtualDisplay.Installed ? policy.VirtualDisplay : "", // policy, else agent.json
                OfferToViewer: start.Features?.Contains(DesktopFeatures.TransferOnPaste) == true,
                // Continued after a dropped connection, nobody new asked: what the user hid stays hidden.
                UserHidden: resumed is not null && consent == DesktopConsent.None ? resumed.UserHidden : DesktopScreenHide.None,
                HiddenApps: resumed is not null && consent == DesktopConsent.None ? resumed.HiddenApps : null, AlwaysHidden: [.. policy.HiddenApps],
                SignedInRemotely: _autoSigned?.Session == session.Id, // continued in the session automatic sign-in created
                PrivacyHeading: policy.PrivacyScreenMessage, App: _helperApp);
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
            _resume.ScreenByService = _screenByService;
            if (_parkedHelper is { } parked) { _parkedHelper = null; parked.Dispose(); } // the new helper covers the screen now
            await Status(DesktopStatusState.Connected, _viewOnly ? "Connected (view only)." : "Connected.", ct, resume: _resume.Token);
            o.Log.Audit(resumed is not null ? AuditEvent.DesktopResumed : AuditEvent.DesktopStarted,
                $"Desktop session {(resumed is not null ? "continued" : "started")}: {identity.Account} from {source} – {what}");
            if (autoSignInRefused is { } refusedWhy) await RefuseAutoSignIn(refusedWhy.Code, refusedWhy.Message, ct);
            else if (autoSignIn)
            {
                var endAction = policy.AutoSignInEndAction ?? AutoSignInEndActions.Valid(start.AutoSignInEndAction) ?? AutoSignInEndActions.Lock;
                _ = Task.Run(() => AutoSignInAsync(session.Id, endAction, ct), ct);
            }
            // App tab in the admin's own session: the app starts now (after an automatic sign-in: once it is there, below).
            if (_app is not null && active && !await StartApp(session.Id, ct)) return;

            // The helper can end without anyone wanting it: signing in switches desktops and often moves the console to a
            // new session. Then a new helper is started (in the console's current session). The connection was allowed –
            // but only by the user who allowed it: anyone else signed in there now is asked first. The same happens after
            // an unlock or sign-in within the session, which the helper reports (it holds the picture until then).
            var restarts = new Queue<DateTime>();
            while (true)
            {
                var watch = Task.Delay(TimeSpan.FromSeconds(2), ct);
                var ended = await Task.WhenAny(fromClient, fromHelper, watch, _endRequested.Task);
                if (ended == _endRequested.Task) break;
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
                    else if (fromClient.IsCanceled)
                    {
                        // The agent noticed the drop first: the keep-alive timeout or the aborted request ends the pending
                        // receive with an OperationCanceledException, so the loop ends cancelled, not faulted. Nothing here
                        // cancels it on purpose – only a takeover by a new connection, which continues the session too.
                        _lost = true;
                        _endReason = _resume?.TakenOver == true ? "continued in a new connection" : "connection lost";
                    }
                    // Closed by RemoteHub or an invalid message: ended on purpose, so the token goes now rather than after the
                    // close handshake, which waits up to 2 s. A receive that ended because this connection was cancelled (a
                    // takeover by a new connection) is no such end.
                    if (!_lost && !ct.IsCancellationRequested) _resume?.Revoke();
                    break;
                }
                // The aborted request (a drop) or a takeover cancels the helper's loop too, often before the receive: that
                // is no helper ending, and starting a new helper for a connection that is gone only delays the end.
                ct.ThrowIfCancellationRequested();
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
                _drops.Clear();
                _fromDevice.Clear();
                try { await fromHelper.WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { /* ended with the old pipe */ }
                if (!userDesktop) await Task.Delay(TimeSpan.FromSeconds(1.5), ct); // let the new session or desktop settle
                target = session.Console ? desktop.Sessions.ConsoleSessionId() ?? target : target;

                // Who is there now? Anyone but the user who allowed it is asked; a lock or sign-in screen is held (only it is shown).
                var now = await SessionNow(target, userDesktop, ct);
                var signedIn = now is { State: DesktopSessionState.Active };
                // Whoever signed in is asked – unless it is the user who allowed it, or the admin signing in with their own account.
                var self = signedIn && !SameUser(now!.User, _allowedBy) && IsSelfSession(now);
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
                    UserHidden = ask ? DesktopScreenHide.None : _privacy.User, HiddenApps = ask ? null : _hiddenApps,
                    // The session automatic sign-in created (or is creating) for this admin: no banner for anyone at the device.
                    SignedInRemotely = signedIn && !ask && (_autoSignInPending || _autoSigned?.Session == target) && IsSelfSession(now!), App = _helperApp },
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
                // App tab: the automatic sign-in made the admin's session – the app starts in it now; the account's other
                // app tabs, waiting for this sign-in, go on into the same session.
                if (_app is not null && signedIn && IsSelfSession(now!))
                {
                    var appStarted = await StartApp(target, ct);
                    AppSignInDone();
                    if (!appStarted) break;
                }
            }
        }
        // Already ended on purpose (the token is revoked) when the connection broke, e.g. while the end was being reported:
        // it stays ended on purpose – it may not be continued, and a session automatic sign-in created gets its end action.
        catch (Exception ex) when ((ex is OperationCanceledException or WebSocketException or IOException or InvalidDataException) && _resume?.Revoked == true) { }
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
            // A session automatic sign-in created (§9.2): ended on purpose → its end action; dropped → its screen stays covered
            // by this helper for a while (a continued connection takes over), then it is signed out.
            // A sign-in still under way confirms its session after this point and applies the end action itself.
            lock (_autoSignedLock) _autoSignClosed = true;
            AppSignInDone(); // ended before its app ran: the waiting app tabs don't wait for it any longer
            // App tabs: other app tabs still running in this session keep it as it is; the last one applies the end action.
            // A dropped one is parked for its resume (it still counts) – its app and monitor wait for the connection to continue.
            var parkApp = _lost && _resume is { Revoked: false } && _appLease is not null && _appProcess is not null && _helperApp is not null && _appSession is not null;
            var appTabsLeft = _appSession is not { } appSession ? 0
                : parkApp ? AppTabsOpen.GetValueOrDefault(appSession)
                : AppTabsOpen.AddOrUpdate(appSession, 0, (_, n) => Math.Max(0, n - 1));
            if (_autoSigned is { } shared && appTabsLeft > 0 && shared.Session == _appSession)
                o.Log.Write($"Desktop: app tab: session {shared.Session} stays for {appTabsLeft} other app tab(s); the last one ends it ({shared.EndAction})");
            else if (_autoSigned is null && _app is not null && appTabsLeft == 0 && _appSession is { } lastIn && o.Desktop!.AutoSigned.Owned(lastIn) is not null)
                o.Desktop.AutoSigned.EndOnPurpose(lastIn, o.Log, "the last app tab ended");
            if (_autoSigned is { } mine && _resume?.TakenOver != true && !(appTabsLeft > 0 && mine.Session == _appSession))
            {
                if (_lost && _helper is { } cover && _helperSession == mine.Session)
                {
                    _helper = null;
                    o.Desktop!.AutoSigned.Dropped(mine.Session, cover, o.Log);
                }
                else o.Desktop!.AutoSigned.EndOnPurpose(mine.Session, o.Log, _lost ? "connection lost, nothing to cover the screen" : _endReason,
                    _lost ? AutoSignInEndActions.SignOut : null);
            }
            _parkedHelper?.Dispose();
            _helper?.Dispose();
            if (parkApp && _resume is { } parking)
            {
                var carried = parking.App = new DesktopResume.ParkedApp(_appLease!, _appProcess!, _helperApp!, _appSession!.Value);
                var log = o.Log;
                var autoSigned = o.Desktop!.AutoSigned;
                _ = Task.Run(async () =>
                {
                    // Not continued in time: the monitor becomes a spare, and as the last app tab it ends the session.
                    await Task.Delay(AutoSignInSessions.HoldAfterDrop + TimeSpan.FromSeconds(5));
                    if (!carried.TryClaim()) return;
                    carried.Process.Dispose();
                    try { if (VirtualDisplay.Close(carried.Lease) is { } closed) log.Write($"Desktop: app tab (not continued): {closed}"); }
                    catch (Exception ex) { log.Write($"Desktop: app tab: its monitor wasn't given back: {ex.Message}"); }
                    if (AppTabsOpen.AddOrUpdate(carried.Session, 0, (_, n) => Math.Max(0, n - 1)) == 0 && autoSigned.Owned(carried.Session) is not null)
                        autoSigned.EndOnPurpose(carried.Session, log, "the last app tab wasn't continued", AutoSignInEndActions.SignOut);
                });
            }
            else if (_appLease is { } appLease)
            {
                _appProcess?.Dispose(); // the app keeps running: closing it is the tab's business (§4.3)
                var log = o.Log;
                _ = Task.Run(() =>
                {
                    try { if (VirtualDisplay.Close(appLease, line => log.Write($"Desktop: app tab: {line}")) is { } closed) log.Write($"Desktop: app tab: {closed}"); }
                    catch (Exception ex) { log.Write($"Desktop: app tab: its monitor wasn't given back: {ex.Message}"); }
                });
            }
            {
                // Switched off with the last connection that had it on – in the background, since it may be tried again for
                // a few seconds while the ended helper still holds the display. Always released: a switch-on still under
                // way for this connection then switches it off again itself. Every outcome goes to the log.
                var log = o.Log;
                var session = _helperSession;
                var lease = _virtualLease;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (VirtualDisplay.Hide(lease, line => log.Write($"Desktop: session {session}: {line}")) is { } off)
                            log.Write($"Desktop: session {session}: {off}");
                    }
                    catch (Exception ex) { log.Write($"Desktop: session {session}: Virtual display not switched off: {ex.Message}"); }
                });
            }
            // Only a broken connection may be continued; ended on purpose (RemoteHub, the user, idle, refused) it may not – that
            // was revoked when the end was reported. A takeover still under way (nothing revoked it) is let through.
            if (_resume is { } r)
            {
                if (_lost || r.TakenOver) r.Release(_autoSigned is not null ? AutoSignInSessions.HoldAfterDrop : null);
                else r.Revoke();
            }
            if (_signInRequest.Task.IsCompletedSuccessfully) _signInRequest.Task.Result.Dispose(); // sent but never used
        }
    }

    // ---- automatic sign-in (docs/credential-provider-design.md §6)

    // Ends the connection from outside the main loop (the sign-in runs beside it) the same clean way as the other ends: the
    // main loop stops and the close in its finally lets the last status reach RemoteHub. Cancelling the connection instead
    // aborted the WebSocket, which could drop that status (seen as "connection lost" in RemoteHub).
    private readonly TaskCompletionSource _endRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (int Session, string EndAction)? _autoSigned; // the session automatic sign-in created, while this connection owns it
    private readonly object _autoSignedLock = new();     // _autoSigned against _autoSignClosed: exactly one side applies the end action
    private bool _autoSignClosed;                          // the connection's end has run; a sign-in confirmed later ends its session itself
    private bool _screenByService;                         // the privacy screen is the service's (automatic sign-in), not the admin's
    private volatile bool _autoSignInPending;              // the sign-in went to Windows; its session isn't confirmed yet
    private IDesktopHelper? _parkedHelper;                 // the dropped connection's helper, ended once this one covers the screen
    private volatile bool _signInReady; // a SignInRequest is expected now – at any other time it ends the connection
    private readonly TaskCompletionSource<ProtectedSecret> _signInRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Attempts per account on the whole device (§6.2 check 7); tests replace it.</summary>
    internal static AutoSignInGate AutoSignInAttempts = new();

    /// <summary>Whether this account may sign in automatically on this device at all (Hello): null = yes, else why not.</summary>
    private string? AutoSignInRefusal(AgentPolicy p, bool control) =>
        p.NoUserBehavior != NoUserBehaviors.AutoSignIn ? "Automatic sign-in is turned off on this device (policy NoUserBehavior)."
        : !identity.InAny(p.Groups(AgentRight.AutoSignIn)) ? "Your account may not sign in automatically on this device (right AutoSignIn)."
        : !control || !identity.InAny(p.Groups(AgentRight.SecureDesktop)) ? "Automatic sign-in needs the rights Desktop and SecureDesktop on this device."
        : identity.UserSid is null ? "The account's SID is unknown."
        : p.AutoSignInRequirePrivacyScreen && !(PrivacyScreenSupported && p.PrivacyScreen != PrivacyScreenLevel.Off)
            ? "Automatic sign-in needs the privacy screen on this device, which isn't available (policy AutoSignInRequirePrivacyScreen)."
        : o.Desktop?.AutoSignIn is not { } broker ? "Automatic sign-in isn't available on this device."
        : broker.Unavailable() is { } why ? $"Automatic sign-in isn't available on this device: {why}."
        : null;

    /// <summary>§6.2 checks 1–5 for this start: null = go ahead, else the reason code and text.</summary>
    private (string Code, string Message)? AutoSignInStartRefusal(AgentPolicy p, bool control, DesktopSessionInfo session)
    {
        if (AutoSignInRefusal(p, control) is { } why)
            return (o.Desktop?.AutoSignIn?.Unavailable() is not null ? AutoSignInFailure.Unavailable : AutoSignInFailure.NotAllowed, why);
        if (_viewOnly) return (AutoSignInFailure.NotAllowed, "Automatic sign-in isn't available in a view-only connection.");
        return SessionRefusal(session.Id) is { } state ? (state.Code, state.Message) : null;
    }

    /// <summary>§6.2 checks 4–5: the console in the SignIn state, and no other session of this account.</summary>
    private (string Code, string Message)? SessionRefusal(int sessionId)
    {
        var sessions = o.Desktop!.Sessions;
        if (sessions.ConsoleSessionId() != sessionId) return (AutoSignInFailure.SessionState, "Automatic sign-in works only on the device's console.");
        var list = sessions.List();
        if (list.FirstOrDefault(s => s.Id == sessionId) is not { State: DesktopSessionState.SignIn })
            return (AutoSignInFailure.SessionState, "Someone is signed in on the console now – automatic sign-in only works when nobody is.");
        // On client Windows, signing in on the console would take over an RDP session of the same account.
        if (list.Any(s => s.User.Length > 0 && string.Equals(sessions.UserSid(s.Id), identity.UserSid, StringComparison.OrdinalIgnoreCase)))
            return (AutoSignInFailure.ExistingSession, "This account already has a session on the device – connect to that session instead.");
        return null;
    }

    private async Task RefuseAutoSignIn(string code, string message, CancellationToken ct)
    {
        o.Log.Audit(AuditEvent.AutoSignInRefused, $"Automatic sign-in refused: {identity.Account} from {source} – {message} ({code})");
        await SignInFailed(message, ct);
    }

    /// <summary>
    /// The automatic sign-in didn't happen. A desktop tab stays on the sign-in screen; an app tab has no session to start
    /// its app in, so it ends, saying why – and the account's other app tabs waiting for this sign-in go on.
    /// </summary>
    private async Task SignInFailed(string message, CancellationToken ct)
    {
        if (_app is null)
        {
            await Status(DesktopStatusState.SignInFailed, message, ct);
            return;
        }
        AppSignInDone();
        _endReason = "the automatic sign-in failed";
        await Status(DesktopStatusState.Failed, message, ct);
        _endRequested.TrySetResult();
    }

    /// <summary>
    /// SignInReady → the password from RemoteHub (30 s) → the provider signs the account in → the new session's user is
    /// checked against the account's SID. The picture follows the console into the new session (the main loop), where
    /// the operator's own account is connected to without asking.
    /// </summary>
    private async Task AutoSignInAsync(int sessionId, string endAction, CancellationToken ct)
    {
        try
        {
            _signInReady = true;
            await Status(DesktopStatusState.SignInReady, $"Ready to sign in as {identity.Account}.", ct);
            ProtectedSecret secret;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                try { secret = await _signInRequest.Task.WaitAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await RefuseAutoSignIn(AutoSignInFailure.Timeout, "RemoteHub didn't send the sign-in in time.", ct);
                    return;
                }
                finally { _signInReady = false; }
            }

            AutoSignInResult result;
            using (secret)
            {
                if (!AutoSignInAttempts.TryBegin(identity.UserSid!))
                {
                    await RefuseAutoSignIn(AutoSignInFailure.RateLimit, "Too many automatic sign-ins for this account – try again in 15 minutes, or sign in at the sign-in screen.", ct);
                    return;
                }
                await Status(DesktopStatusState.SigningIn, $"Signing in as {identity.Account}…", ct);
                // §9.1: the new session starts covered – every helper started in it gets the privacy screen after Begin.
                var p = o.CurrentPolicy();
                if (PrivacyScreenSupported && p.PrivacyScreen != PrivacyScreenLevel.Off)
                {
                    // Always with the device's keyboard and mouse blocked: the session is the admin's, nobody at the device may type into it.
                    _screen = new DesktopPrivacyScreen(DesktopPrivacyStyle.Message, BlockLocalInput: true);
                    _screenByService = true;
                    _resume?.Screen = _screen;
                    _resume?.ScreenByService = true;
                }
                var slash = identity.Account.IndexOf('\\');
                var (domain, user) = slash < 0 ? (Environment.MachineName, identity.Account) : (identity.Account[..slash], identity.Account[(slash + 1)..]);
                _autoSignInPending = true;
                result = await o.Desktop!.AutoSignIn!.SignInAsync(new AutoSignInRequest(sessionId, domain, user, identity.UserSid!, secret,
                    () => RecheckAutoSignIn(sessionId), o.Desktop.SecureAttention), ct);
            }
            // The ticket went out but Windows' answer was lost (lab C-END-10: LogonUI signed the account in and went away
            // before the provider sent the result). Fail safe: if the console now has this account's session, it is the
            // one this sign-in created and gets the cover and the end action like any other; otherwise it failed.
            var lost = !result.Ok && result.Unconfirmed;
            // Not cancelled with the connection: once the ticket has gone out, the outcome must be known even when nobody is connected.
            var (console, sid) = result.Ok || lost ? await ConsoleUser() : (null, null);
            if (lost && string.Equals(sid, identity.UserSid, StringComparison.OrdinalIgnoreCase))
            {
                o.Log.Write($"Automatic sign-in: Windows' answer didn't arrive ({result.Code}), but console session {console} has the account – treated as signed in");
                result = new AutoSignInResult(true, "", "Signed in.");
            }
            if (!result.Ok)
            {
                if (result.Code == AutoSignInFailure.Windows)
                    o.Log.Audit(AuditEvent.AutoSignInFailed, $"Automatic sign-in failed: {identity.Account} from {source} – Windows reported 0x{result.Status:X8} (0x{result.Substatus:X8}): {result.Message}");
                else if (result.Code == AutoSignInFailure.NotOffered)
                    // Not LogonUI's answer: the sign-in screen doesn't list the account, and the service checked with Windows itself.
                    o.Log.Audit(AuditEvent.AutoSignInFailed, $"Automatic sign-in failed: {identity.Account} from {source} – the sign-in screen didn't offer the account" +
                        (result.CheckedError is { } error ? $"; checked with Windows: error {error}{(result.Status != 0 ? $" (0x{result.Status:X8})" : "")}" : "; Windows couldn't be asked") +
                        $": {result.Message}");
                else o.Log.Audit(AuditEvent.AutoSignInRefused, $"Automatic sign-in refused: {identity.Account} from {source} – {result.Message} ({result.Code})");
                if (_screenByService) { _screen = null; _screenByService = false; _resume?.Screen = null; _resume?.ScreenByService = false; }
                await SignInFailed(result.Message, ct);
                return;
            }

            // §6.3: the console's new session must belong to exactly this account – otherwise it is signed out at once.
            if (!string.Equals(sid, identity.UserSid, StringComparison.OrdinalIgnoreCase))
            {
                if (console is { } wrong && sid is not null) o.Desktop.Sessions.LogOff(wrong);
                o.Log.Audit(AuditEvent.AutoSignInMismatch, $"Automatic sign-in: the console session {console?.ToString() ?? "?"} has " +
                    $"{(sid is null ? "no readable user" : $"user {sid}")}, not {identity.Account} ({identity.UserSid}){(sid is null ? "" : " – signed out")}");
                _endReason = "automatic sign-in ended up with another account";
                await Status(DesktopStatusState.Ended, "The automatic sign-in couldn't be confirmed, so it was undone. Details are in the agent's log.", ct);
                _endRequested.TrySetResult();
                return;
            }
            o.Desktop.AutoSigned.Signed(console!.Value, sid!, identity.Account, endAction);
            o.Log.Audit(AuditEvent.AutoSignInSucceeded, $"Automatic sign-in: {identity.Account} from {source} signed in to console session {console} " +
                $"(when the connection ends: {(endAction == AutoSignInEndActions.SignOut ? "sign out" : "lock")}{(_screenByService ? ", privacy screen on" : ", no privacy screen on this device")}" +
                $"{(lost ? "; Windows' answer was lost, the console session confirmed it" : "")})");
            // Hand the session to the connection's end – unless that has already run (the connection ended while the
            // sign-in was under way). Then nothing else will give the session its end action, so it gets it here, as the
            // end would have: a drop with nothing covering the screen signs out, an end on purpose takes the end action.
            bool closed;
            lock (_autoSignedLock)
            {
                closed = _autoSignClosed;
                if (!closed) _autoSigned = (console.Value, endAction);
            }
            if (closed)
            {
                o.Desktop.AutoSigned.EndOnPurpose(console.Value, o.Log, _lost ? "connection lost, nothing to cover the screen" : _endReason,
                    _lost ? AutoSignInEndActions.SignOut : null);
                return;
            }
            await SendWs(DesktopWire.Json(DesktopMessage.Status, new DesktopStatus(DesktopStatusState.SignedIn, $"Signed in as {identity.Account}.", EndAction: endAction),
                DesktopJson.Default.DesktopStatus), ct);
        }
        catch (OperationCanceledException) { /* the connection ended */ }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { /* gone */ }
        catch (Exception ex)
        {
            o.Log.Audit(AuditEvent.DesktopError, $"Automatic sign-in: unexpected error ({identity.Account} from {source}): {ex}");
            try { await SignInFailed("The automatic sign-in failed on the device. Details are in the agent's log.", ct); } catch { /* gone */ }
        }
        finally { _autoSignInPending = false; } // confirmed (_autoSigned from now on), failed or undone
    }

    /// <summary>How long the console's new session gets to show its user after a sign-in (§6.3), in polls of 500 ms; tests shorten it.</summary>
    internal static int ConsoleUserPolls = 60;

    /// <summary>The console session and its user's SID, once Windows reports one (up to 30 s); the SID is null if it never does.</summary>
    private async Task<(int? Console, string? Sid)> ConsoleUser()
    {
        int? console = null;
        string? sid = null;
        for (var i = 0; i < ConsoleUserPolls && sid is null; i++)
        {
            console = o.Desktop!.Sessions.ConsoleSessionId();
            sid = console is { } c ? o.Desktop.Sessions.UserSid(c) : null;
            if (sid is null) await Task.Delay(500);
        }
        return (console, sid);
    }

    /// <summary>Right before the ticket goes out (§6.2): the policy and right still allow it, and the console is still waiting.</summary>
    private string? RecheckAutoSignIn(int sessionId)
    {
        var now = o.CurrentPolicy();
        if (now.NoUserBehavior != NoUserBehaviors.AutoSignIn || !identity.InAny(now.Groups(AgentRight.AutoSignIn)))
            return "Automatic sign-in was turned off on this device meanwhile.";
        return SessionRefusal(sessionId)?.Message;
    }

    /// <summary>
    /// The sign-in request is read straight into its own pinned buffer and protected at once – never into the
    /// message buffers every other message goes through, never into a string (§10.1). Answers a one-byte marker.
    /// </summary>
    private async Task<byte[]> ReceiveSignInRequest(int firstCount, bool endOfMessage, CancellationToken ct)
    {
        var buffer = GC.AllocateArray<byte>(DesktopWire.SignInRequestHeaderSize + DesktopWire.MaxSignInPasswordChars * 2, pinned: true);
        try
        {
            var length = 0;
            var count = firstCount;
            while (true)
            {
                if (length + count > buffer.Length)
                {
                    ProtectedSecret.CryptographicWipe(_receive.AsSpan(0, count));
                    throw new InvalidDataException("Sign-in request too large.");
                }
                _receive.AsSpan(0, count).CopyTo(buffer.AsSpan(length));
                ProtectedSecret.CryptographicWipe(_receive.AsSpan(0, count));
                length += count;
                if (endOfMessage) break;
                var r = await _ws.ReceiveAsync(_receive, ct);
                if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Closed during a sign-in request.");
                count = r.Count;
                endOfMessage = r.EndOfMessage;
            }
            if (!_signInReady || _signInRequest.Task.IsCompleted) throw new InvalidDataException("A sign-in request the agent didn't ask for.");
            var password = DesktopWire.ReadSignInRequest(buffer.AsSpan(0, length));
            if (password.IsEmpty) throw new InvalidDataException("Malformed sign-in request.");
            _signInRequest.TrySetResult(new ProtectedSecret(password));
            return [(byte)DesktopMessage.SignInRequest];
        }
        finally { ProtectedSecret.CryptographicWipe(buffer); }
    }

    /// <summary>The session's user is this account – by SID where Windows tells it (§6.3), else by name.</summary>
    private string DeviceName => o.Config.DeviceLabel.Length > 0 ? o.Config.DeviceLabel : Environment.MachineName;

    /// <summary>
    /// App tab (§4.1): why the console session can't take it, or null – the account's own unlocked session, or nobody
    /// signed in (then automatic sign-in makes the session). Someone else's session is never used.
    /// </summary>
    private string? AppSessionRefusal(DesktopSessionInfo? console) => console switch
    {
        null => "This device has no console session at the moment.",
        { State: DesktopSessionState.SignIn } => null,
        { State: DesktopSessionState.Active } when IsSelfSession(console) => null,
        { State: DesktopSessionState.Locked } when IsSelfSession(console) => $"Your session on {DeviceName} is locked – unlock it in a desktop tab first.",
        _ when IsSelfSession(console) => $"Your session on {DeviceName} has no screen at the moment – connect to it in a desktop tab first.",
        _ => $"{console.User} is signed in on {DeviceName} – app tabs need your own session.",
    };

    /// <summary>
    /// App tab: starts the app in the account's own session (§6.1) – checked again here, as the account and with its
    /// environment – and tells the helper its job. Audited with the program's path, never its arguments. False when it
    /// couldn't be started (RemoteHub was told why).
    /// </summary>
    private async Task<bool> StartApp(int sessionId, CancellationToken ct)
    {
        if (_app is not { } app || _appProcess is not null || _helperApp is not { } tab) return true;
        await Status(DesktopStatusState.StartingApp, $"Starting {app.Name}…", ct);
        string? why;
        AppTabs.Launch? launch = null;
        var job = AppProcess.NewJobName();
        using (var token = identity.UserSid is { } sid ? AppProcess.Token(sessionId, sid) : null)
        {
            if (token is null) why = $"Your session on {DeviceName} isn't there any more.";
            else
            {
                (launch, why) = AppTabs.Check(app, policy, text => AppProcess.Expand(token, text), File.Exists);
                if (launch is not null)
                    try { _appProcess = AppProcess.Start(token, launch, job); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { why = $"{app.Name} couldn't be started: {ex.Message}"; }
            }
        }
        if (_appProcess is null || launch is null)
        {
            o.Log.Audit(AuditEvent.DesktopRefused, $"Desktop refused: {identity.Account} from {source} – app tab in session {sessionId}: {why}");
            await Status(DesktopStatusState.Failed, why ?? $"{app.Name} couldn't be started.", ct);
            return false;
        }
        o.Log.Audit(AuditEvent.DesktopAppStarted,
            $"Desktop: {identity.Account} from {source} started {launch.Program}{(launch.Elevated ? " (elevated)" : "")} in session {sessionId} for an app tab");
        _helperApp = tab with { Job = job };
        _appSession = sessionId;
        AppTabsOpen.AddOrUpdate(sessionId, 1, (_, n) => n + 1);
        await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.App, _helperApp, HelperJson.Default.HelperApp), ct);
        // §4.2: the device's own screens are covered and its mouse and keyboard blocked while the admin's apps run there –
        // also in a session that existed before. Put up by the service, so it comes back after a drop whatever the right.
        if (PrivacyScreenSupported && _screen is null)
        {
            _screen = new DesktopPrivacyScreen(DesktopPrivacyStyle.Message, BlockLocalInput: true);
            _screenByService = true;
            if (_resume is { } r) (r.Screen, r.ScreenByService) = (_screen, true);
            await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.PrivacyScreen, _screen, DesktopJson.Default.DesktopPrivacyScreen), ct);
        }
        await Status(DesktopStatusState.Connected, $"{app.Name} started.", ct, resume: _resume?.Token);
        return true;
    }

    private bool IsSelfSession(DesktopSessionInfo s) =>
        identity.UserSid is { } mine && o.Desktop?.Sessions.UserSid(s.Id) is { } theirs
            ? string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase)
            : IsSelf(s.User);

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
                case DesktopMessage.AppClose when _live && _app is { } closing && _helperApp is { Job.Length: > 0 }:
                    // §4.3: the app is asked to close; whatever it asks first ("Save changes?") shows in the tab. Force: it is
                    // left running (moved to the primary monitor) and the tab ends.
                    var close = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopAppClose);
                    _appClosing = true;
                    _endReason = close.Force ? $"the tab was closed, {closing.Name} left running" : "the tab was closed";
                    await SendPipe(DesktopWire.Json((DesktopMessage)HelperMessage.AppClose, close, DesktopJson.Default.DesktopAppClose), ct);
                    if (close.Force || _appClosed)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(close.Force ? 1 : 0), ct); // the helper moves the windows first
                        await Status(DesktopStatusState.Ended, close.Force ? $"{closing.Name} keeps running on the device." : $"{closing.Name} was closed.", ct);
                        _endRequested.TrySetResult();
                    }
                    else _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        if (!_endRequested.Task.IsCompleted)
                            await Status(DesktopStatusState.AppClosing, $"{closing.Name} asks something before it closes.", ct);
                    }, ct);
                    break;
                case DesktopMessage.AppRestart when _live && _app is not null && _appClosed && !_appClosing && _appSession is { } restartIn:
                    _appClosed = false;
                    _appProcess?.Dispose();
                    _appProcess = null;
                    AppTabsOpen.AddOrUpdate(restartIn, 0, (_, n) => Math.Max(0, n - 1));
                    if (!await StartApp(restartIn, ct)) _endRequested.TrySetResult();
                    break;
                case DesktopMessage.Settings when _live:
                    var s = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopSettings);
                    var all = s.Monitor == DesktopProtocol.AllMonitors;
                    // Changing the device's resolution is control: not for view-only, and not for the picture of all monitors.
                    var (fitWidth, fitHeight) = _viewOnly || all ? (0, 0) : DesktopProtocol.ValidFitSize(s.FitWidth, s.FitHeight);
                    if (_appLease is { } tabLease && fitWidth > 0 && fitHeight > 0)
                    {
                        // App tab: its monitor at the largest size the driver lists that fits the tab (no reload, §5).
                        var (w, h) = (fitWidth, fitHeight);
                        var refit = await Task.Run(() => VirtualDisplay.Open(tabLease, w, h, policy.MaxAppTabs, out var m, out _) is null ? m : null, ct);
                        if (refit is not null) (fitWidth, fitHeight) = (refit.Width, refit.Height);
                    }
                    _settings = new DesktopSettings(all ? DesktopProtocol.AllMonitors : ClampMonitor(s.Monitor, _monitorCount), Math.Clamp(s.FramesPerSecond, 1, policy.MaxFramesPerSecond),
                        Math.Clamp(s.Quality, 1, policy.MaxQuality), fitWidth, fitHeight);
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
                    if (!ClipboardClosed()) await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferOffer when _live:
                    // Names and sizes only; the device's clipboard offers them until someone pastes (then: TransferRequest).
                    var offer = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                    if (TransferAllowed(offer) is { } notOffered)
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(offer.Id, false, notOffered, ToDevice: true), DesktopJson.Default.DesktopTransferEnd), ct);
                    else await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferRequest when _live:
                    // Pasted on the PC: the device sends what it offered – not while its lock screen is up (the paste
                    // there gets nothing at once instead of waiting).
                    Touch();
                    if (!ClipboardClosed()) await SendPipe(m, ct);
                    else if (TransferId(m) is { } asked)
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(asked, false, ClipboardClosedReason), DesktopJson.Default.DesktopTransferEnd), ct);
                    break;
                case DesktopMessage.TransferStart when _live:
                    var t = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer);
                    if (TransferAllowed(t, drop: true) is { } refused)
                    {
                        await SendWs(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(t.Id, false, refused, ToDevice: true), DesktopJson.Default.DesktopTransferEnd), ct);
                        break;
                    }
                    Touch();
                    _toDevice[t.Id] = t.Total;
                    if (t.Kind == DesktopTransferKind.Drop) _drops[t.Id] = t;
                    await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferData when _live:
                    if (Within(_toDevice, m)) await SendPipe(m, ct);
                    break;
                case DesktopMessage.TransferEnd:
                    var end = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                    if (_toDevice.TryRemove(end.Id, out _)) await SendPipe(m, ct);
                    _drops.TryRemove(end.Id, out _); // RemoteHub ended it (cancelled): nothing to audit
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
        // The cover over a session automatic sign-in created is the service's (§9.1): the admin didn't turn it on (C-END-14).
        var who = _screenByService ? $"the service (automatic sign-in of {identity.Account} from {source})" : $"{identity.Account} from {source}";
        if (now.Reason.Length == 0 && (now.Screen != before.Screen || now.LocalInputBlocked != before.LocalInputBlocked))
            o.Log.Audit(AuditEvent.DesktopPrivacyScreen, now.Screen == DesktopPrivacyStyle.Off
                ? $"Desktop: {who} turned the privacy screen off in session {_helperSession}"
                : $"Desktop: {who} turned the privacy screen on in session {_helperSession} ({now.Screen.ToLowerInvariant()}{(now.LocalInputBlocked ? ", mouse and keyboard at the device blocked" : "")})");
        if (now.Reason.Length > 0)
        {
            _screen = null; // it couldn't be shown (or had to come down): a new helper doesn't try again on its own
            _resume?.Screen = null;
            o.Log.Audit(AuditEvent.DesktopPrivacyRefused, $"Desktop: privacy screen for {identity.Account} from {source} in session {_helperSession} not shown: {now.Reason}");
        }
        await SendWs(DesktopWire.Json(DesktopMessage.Privacy, now, DesktopJson.Default.DesktopPrivacy), ct);
    }

    private volatile bool _gdi;
    /// <summary>This connection's hold on the virtual monitor (counted once it is on for it, whichever way).</summary>
    private readonly VirtualDisplay.Lease _virtualLease = VirtualDisplay.NewLease();
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

    internal const string ClipboardClosedReason = "Nothing is copied while the device shows its lock or sign-in screen – copy again once it is unlocked.";
    private bool _clipboardClosedNoted;

    /// <summary>
    /// Windows' lock, sign-in or Ctrl+Alt+Del screen is up in the session: the clipboard stays closed both ways until it is
    /// gone – nothing typed or copied at the admin's PC lands on the locked user's clipboard (and in a password field), and
    /// nothing copied in the locked session goes out (UI-CP-08). Checked here, not only in RemoteHub; once per copy.
    /// </summary>
    private bool ClipboardClosed()
    {
        var closed = o.Desktop!.Sessions.IsLocked(_helperSession);
        if (closed && !_clipboardClosedNoted) o.Log.Write($"Desktop: session {_helperSession}: clipboard closed while the lock or sign-in screen is up");
        _clipboardClosedNoted = closed;
        return closed;
    }

    /// <summary>The transfer id a TransferRequest names, or null when it isn't one.</summary>
    private static int? TransferId(byte[] m) =>
        int.TryParse(DesktopWire.ReadText(m), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>
    /// Null if this clipboard transfer may go through, else why not. <paramref name="drop"/>: a TransferStart from
    /// RemoteHub, the only place files dropped on the tab may come in – they are never offered for pasting, and the device
    /// never sends any.
    /// </summary>
    internal string? TransferAllowed(DesktopTransfer t, bool drop = false)
    {
        if (t.Kind == DesktopTransferKind.Drop && !drop) return "The copied items are incomplete.";
        // Dropped files need the same right as copied ones: they leave the admin's PC for the device's disk.
        var need = t.Kind switch
        {
            DesktopTransferKind.Image => DesktopClipboardLevel.Images,
            DesktopTransferKind.Files or DesktopTransferKind.Drop => DesktopClipboardLevel.Files,
            _ => int.MaxValue,
        };
        if (_clipboard < need)
            return t.Kind == DesktopTransferKind.Image ? "Copying images isn't allowed on this device." : "Copying files isn't allowed on this device.";
        if (ClipboardClosed()) return ClipboardClosedReason;
        if (t.Items is null || t.Items.Any(i => i is null || t.Kind != DesktopTransferKind.Image && string.IsNullOrEmpty(i.Path)))
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
            while (await HelperOutput.ReadAsync(helper, ct) is { } m)
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
                            var lease = _virtualLease;
                            var (shown, recovered) = await Task.Run(() => (VirtualDisplay.Show(lease, vw, vh, out var r), r), ct);
                            if (recovered is not null) o.Log.Write($"Desktop: session {_helperSession}: {recovered}");
                            o.Log.Write($"Desktop: session {_helperSession}: virtual monitor {(shown is null ? $"on at {VirtualDisplay.Normalize(vw, vh).W}×{VirtualDisplay.Normalize(vw, vh).H}" : "not switched on: " + shown)}");
                        }
                        break;
                    case (byte)HelperMessage.AppClosed when helper == _helper && _app is { } closedApp:
                        o.Log.Write($"Desktop: app tab: {closedApp.Name} was closed in session {_helperSession}");
                        _appClosed = true;
                        if (_appClosing)
                        {
                            // The admin closed the tab and the app has gone: the tab ends.
                            await Status(DesktopStatusState.Ended, $"{closedApp.Name} was closed.", ct);
                            _endRequested.TrySetResult();
                        }
                        else await Status(DesktopStatusState.AppClosed, $"{closedApp.Name} was closed.", ct);
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
                        else if (m[0] == (byte)DesktopMessage.Layout)
                        {
                            var layout = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopLayout);
                            _monitorCount = layout.Monitors.Length;
                            LogDesktopSwitch(layout.SecureDesktop);
                        }
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
                        if (!ClipboardClosed()) await SendWs(m, ct);
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
                    case (byte)DesktopMessage.TransferRequest when helper == _helper:
                        // A paste on the device of what was copied in RemoteHub. Written down when it goes nowhere: in lab
                        // run 20261019-193627 (UI-CP-15) the device logged the paste and RemoteHub never started the copy.
                        if (!_live) { o.Log.Write($"Desktop: session {_helperSession}: clipboard: a paste on the device asked for copy {TransferId(m)?.ToString() ?? "?"} while the connection isn't live – not passed on"); break; }
                        Touch();
                        if (ClipboardClosed())
                        {
                            if (TransferId(m) is { } asked)
                                await SendPipe(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(asked, false, ClipboardClosedReason, ToDevice: true), DesktopJson.Default.DesktopTransferEnd), ct);
                            break;
                        }
                        await SendWs(m, ct);
                        break;
                    case (byte)DesktopMessage.TransferEnd when helper == _helper:
                        var te = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd);
                        // The device's own transfer ended – or one to the device arrived there (receipt) or failed there.
                        if ((te.ToDevice ? _toDevice.TryRemove(te.Id, out _) : _fromDevice.TryRemove(te.Id, out _)) || !te.Ok) await SendWs(m, ct);
                        if (te.ToDevice && _drops.TryRemove(te.Id, out var dropped) && te.Ok)
                        {
                            var files = dropped.Items.Count(i => !i.Folder);
                            o.Log.Audit(AuditEvent.DesktopFilesSaved,
                                $"Desktop: {identity.Account} from {source} saved {files} file(s), {dropped.Total:N0} bytes, on the desktop of the user in session {_helperSession} (folder \"{te.Folder}\")");
                        }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { return new HelperExit(false, ex.Message); }
        return new HelperExit(false, "the desktop helper ended");
    }

    private Task Status(string state, string message, CancellationToken ct, int attempts = 0, string? resume = null)
    {
        // An end is final: the token is void before RemoteHub – or anyone else holding the token – can learn of the end.
        if (state is DesktopStatusState.Ended or DesktopStatusState.Denied or DesktopStatusState.Failed) _resume?.Revoke();
        return SendWs(DesktopWire.Json(DesktopMessage.Status, new DesktopStatus(state, message, attempts, resume), DesktopJson.Default.DesktopStatus), ct);
    }

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

    /// <summary>One receive buffer for the connection, pinned so a sign-in request never leaves copies behind when the GC moves it.</summary>
    private readonly byte[] _receive = GC.AllocateArray<byte>(64 * 1024, pinned: true);

    private async Task<byte[]?> ReceiveWs(CancellationToken ct)
    {
        var buffer = _receive;
        using var message = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            // A sign-in request goes its own way from its first byte on – never into the MemoryStream (§10.1).
            if (message.Length == 0 && r.Count > 0 && buffer[0] == (byte)DesktopMessage.SignInRequest)
                return await ReceiveSignInRequest(r.Count, r.EndOfMessage, ct);
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
        private readonly Lock _gate = new();
        private bool _revoked, _takenOver;

        /// <summary>A new connection took the session over while this one still looked alive.</summary>
        public bool TakenOver { get { lock (_gate) return _takenOver; } }

        /// <summary>Ended on purpose: the token can't continue anything, not even a takeover that had already begun.</summary>
        public bool Revoked { get { lock (_gate) return _revoked; } }

        internal DateTime? ExpiresUtc;
        internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Owner => owner;

        public void Update(string allowedBy) => AllowedBy = allowedBy;

        /// <summary>What the user at the device hid: still hidden in the continued connection.</summary>
        public string UserHidden { get; set; } = DesktopScreenHide.None;

        /// <summary>The admin's privacy screen, if it was on: on again in the continued connection.</summary>
        public DesktopPrivacyScreen? Screen { get; set; }

        /// <summary>The service put the privacy screen up (automatic sign-in): it comes back whatever the account's right.</summary>
        public bool ScreenByService { get; set; }

        /// <summary>The apps the user hid: still hidden in the continued connection.</summary>
        public string[]? HiddenApps { get; set; }

        /// <summary>An app tab's monitor and running app, parked while the connection may be continued.</summary>
        public ParkedApp? App { get; set; }

        /// <summary>The connection broke: usable for <see cref="Window"/> (or longer: a session automatic sign-in created).</summary>
        public void Release(TimeSpan? window = null)
        {
            ExpiresUtc = DateTime.UtcNow + (window ?? Window);
            Ended.TrySetResult();
        }

        /// <summary>
        /// Ended on purpose: not usable. The endpoint calls it before the end is reported, so the token doesn't outlive the
        /// user's (or RemoteHub's) decision during the close handshake. It also wins over a takeover that has begun but not
        /// finished: as far as the user is concerned the end came first, so the new connection is asked again.
        /// </summary>
        public void Revoke()
        {
            lock (_gate) { _revoked = true; _takenOver = false; }
            Entries.TryRemove(new KeyValuePair<string, Entry>(token, this));
            Ended.TrySetResult();
        }

        /// <summary>Marks the takeover – unless the connection was already ended on purpose.</summary>
        internal bool TryTakeOver()
        {
            lock (_gate)
            {
                if (_revoked) return false;
                _takenOver = true;
                return true;
            }
        }
    }

    /// <summary>
    /// A dropped app tab's monitor lease, process and helper settings, waiting for the connection to be continued. Taken
    /// exactly once: by the continued connection, or by the clean-up when nobody continued it.
    /// </summary>
    internal sealed class ParkedApp(VirtualDisplay.AppLease lease, AppProcess process, HelperApp helper, int session)
    {
        private int _claimed;
        public VirtualDisplay.AppLease Lease => lease;
        public AppProcess Process => process;
        public HelperApp Helper => helper;
        public int Session => session;
        public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
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
            // The old connection hasn't noticed yet: end it, then continue here – unless it is being ended on purpose (the
            // user's Disconnect, a refusal, idle, RemoteHub's close), which no token may skip.
            if (!e.TryTakeOver()) return null;
            try { e.Owner.Cancel(); } catch (ObjectDisposedException) { }
            await Task.WhenAny(e.Ended.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        }
        return e.Revoked ? null : e; // ended on purpose while it was being taken over
    }
}
