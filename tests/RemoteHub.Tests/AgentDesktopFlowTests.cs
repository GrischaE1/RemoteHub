extern alias agent;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Management.Automation;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Remote desktop through the agent, end to end on this PC: the agent's endpoint with its real relay and the real
/// desktop helper logic (in-process, with a generated screen and recorded input) against RemoteHub's client.
/// </summary>
public class AgentDesktopFlowTests
{
    private const string Admins = "S-1-5-32-544", Users = "S-1-5-32-545";

    private sealed class Auth : A.IShellAuthenticator
    {
        public A.AuthResult Authenticate(string userName, string password) => Authenticate(userName, password, [Admins]);

        public A.AuthResult Authenticate(string userName, string password, IReadOnlyList<string> groups)
        {
            string[]? sids = (userName, password) switch
            {
                (@".\admin", "pw") => [Admins, Users],
                (@".\viewer", "pw") => [Users],
                (@".\admin2", "pw") => [Admins, Users],
                _ => null,
            };
            if (sids is null) return A.AuthResult.Fail(A.AuthOutcome.BadCredentials, "bad");
            if (!groups.Intersect(sids).Any()) return A.AuthResult.Fail(A.AuthOutcome.NotAllowed, "not allowed");
            return new A.AuthResult(A.AuthOutcome.Ok, "", new A.ShellIdentity("TEST\\" + userName[2..], default, sids));
        }
    }

    private sealed class Sessions(params A.DesktopSessionInfo[] list) : A.IDesktopSessions
    {
        /// <summary>Set to simulate the console moving to another session (someone signs in).</summary>
        public volatile int Console = list.FirstOrDefault(s => s.Console)?.Id ?? 0;
        /// <summary>Replaced to simulate an unlock or sign-in.</summary>
        public volatile A.DesktopSessionInfo[] Current = list;
        public IReadOnlyList<A.DesktopSessionInfo> List() => Current;
        public volatile bool Disconnected;
        public bool IsDisconnected(int session) => Disconnected;
        public int? ConsoleSessionId() => Console;
    }

    /// <summary>Runs the real helper session in-process, connected by pipes like the real process.</summary>
    private sealed class InProcessHelpers(Func<Ui> ui, Func<int, Screen> screen, Input input) : A.IDesktopHelperLauncher
    {
        public readonly List<Ui> Uis = [];
        public readonly List<Screen> Screens = [];
        public readonly List<int> SessionIds = [];
        public int Started;

        public A.IDesktopHelper Start(int sessionId)
        {
            var n = Interlocked.Increment(ref Started);
            lock (SessionIds) SessionIds.Add(sessionId);
            var toHelper = new AnonymousPipeServerStream(PipeDirection.Out);
            var helperIn = new AnonymousPipeClientStream(PipeDirection.In, toHelper.ClientSafePipeHandle);
            var fromHelper = new AnonymousPipeServerStream(PipeDirection.In);
            var helperOut = new AnonymousPipeClientStream(PipeDirection.Out, fromHelper.ClientSafePipeHandle);
            var u = ui();
            lock (Uis) Uis.Add(u);
            var sc = screen(n);
            lock (Screens) Screens.Add(sc);
            var session = new A.DesktopHelperSession(helperIn, helperOut, u, sc, input);
            var run = Task.Run(async () =>
            {
                try { await session.RunAsync(CancellationToken.None); }
                finally { helperOut.Dispose(); }
            });
            return new Helper(toHelper, fromHelper, run);
        }

        private sealed class Helper(Stream input, Stream output, Task run) : A.IDesktopHelper
        {
            public Stream Input => input;
            public Stream Output => output;
            public int ProcessId => 0;
            public void Dispose()
            {
                input.Dispose();
                run.Wait(5000);
                output.Dispose();
            }
        }
    }

    private sealed class Ui(bool allow) : A.IHelperUi
    {
        public string? Pin;
        public int Asked;
        public bool Banner;
        public Action? Disconnect;
        public readonly List<string> Clipboard = [];
        public event Action<string>? ClipboardChanged;
        public void AskConsent(A.HelperInit init, Action<bool> answered) { Interlocked.Increment(ref Asked); Task.Run(() => answered(allow)); }
        public void ShowPin(A.HelperInit init, Action denied) { Pin = init.Pin; if (!allow) Task.Run(denied); }
        public void ShowBanner(A.HelperInit init, Action disconnect) { Banner = true; Disconnect = disconnect; }
        public void CloseDialogs() { }
        public void SetClipboard(string text) { lock (Clipboard) Clipboard.Add(text); }
        public void RaiseClipboard(string text) => ClipboardChanged?.Invoke(text);
        public volatile byte[]? Image;
        public volatile IReadOnlyList<string>? Files;
        public event Action<A.HelperClipboard>? ClipboardContent;
        public void SetClipboardImage(byte[] png) => Image = png;
        public void SetClipboardFiles(IReadOnlyList<string> paths) => Files = paths;
        public void RaiseContent(A.HelperClipboard c) => ClipboardContent?.Invoke(c);
        public void Dispose() { }
    }

    /// <summary>A 320 × 240 monitor whose picture changes on every capture for the first frames.</summary>
    private sealed class Screen(bool secure = false, int failAfter = int.MaxValue) : A.IScreenSource
    {
        private int _n;
        public readonly ConcurrentQueue<(int, int)> Fits = new();
        public int Restores;
        public void FitResolution(A.DesktopMonitor monitor, int width, int height) => Fits.Enqueue((width, height));
        public void RestoreResolution() => Interlocked.Increment(ref Restores);
        /// <summary>Set to false to simulate the user's desktop appearing (unlock or sign-in).</summary>
        public volatile bool Secure = secure;
        public bool SecureDesktop => Secure;
        public IReadOnlyList<A.DesktopMonitor> Monitors() => [new A.DesktopMonitor(0, @"\\.\DISPLAY1", 0, 0, 320, 240, true), new A.DesktopMonitor(1, @"\\.\DISPLAY2", 320, 0, 200, 100, false)];
        public bool SyncDesktop() => false;

        public A.CaptureResult Capture(A.DesktopMonitor monitor, int timeoutMs, A.FrameBuffer target)
        {
            if (_n >= failAfter) throw new InvalidOperationException("desktop gone (sign-in)");
            if (_n++ > 20) { Thread.Sleep(10); return A.CaptureResult.Unchanged; }
            target.Ensure(monitor.Width, monitor.Height);
            for (var i = 0; i < target.Pixels.Length; i += 4) { target.Pixels[i] = (byte)(i + _n); target.Pixels[i + 1] = (byte)_n; target.Pixels[i + 3] = 255; }
            return A.CaptureResult.Changed;
        }

        public A.RemoteCursor Cursor(A.DesktopMonitor monitor, out A.CursorImage? changedShape)
        {
            changedShape = _n == 1 ? new A.CursorImage(2, 2, 0, 0, new byte[16]) : null;
            return new A.RemoteCursor(10, 20, true);
        }

        public void Dispose() { }
    }

    private sealed class Input : A.IInputSink
    {
        public readonly ConcurrentQueue<A.RemoteMouse> Mouse = new();
        public readonly ConcurrentQueue<A.RemoteKey> Keys = new();
        public int Releases;
        void A.IInputSink.Mouse(A.DesktopMonitor monitor, A.RemoteMouse input) => Mouse.Enqueue(input);
        void A.IInputSink.Key(A.RemoteKey input) => Keys.Enqueue(input);
        public void ReleaseAll() => Interlocked.Increment(ref Releases);
    }

    private sealed class Device : IAsyncDisposable
    {
        public required WebApplication App;
        public required int Port;
        public required string Fingerprint;
        public required InProcessHelpers Helpers;
        public required Sessions Sessions;
        public required Input Input;
        public required ConcurrentQueue<A.AuditEvent> Audits;
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + Guid.NewGuid(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private static readonly A.DesktopSessionInfo ActiveUser = new(1, @"PC\alice", A.DesktopSessionState.Active, true, "Console");
    private static readonly A.DesktopSessionInfo LoginScreen = new(2, "", A.DesktopSessionState.SignIn, false, "Console");

    private static async Task<Device> StartDevice(bool allow = true, bool secureScreen = false, A.AgentPolicy? policy = null, Func<int, Screen>? screens = null,
        Func<int, bool>? allowAt = null)
    {
        var cert = SelfSigned();
        var input = new Input();
        var uis = 0;
        var helpers = new InProcessHelpers(() => new Ui(allowAt?.Invoke(Interlocked.Increment(ref uis)) ?? allow), screens ?? (_ => new Screen(secureScreen)), input);
        var sessions = new Sessions(ActiveUser, LoginScreen);
        var config = new A.AgentConfig { DeviceId = "dev", DeviceLabel = "Device 1", ListenPort = 0 };
        policy ??= new A.AgentPolicy
        {
            Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>>
            {
                [A.AgentRight.PowerShell] = [Admins],
                [A.AgentRight.Desktop] = [Admins],
                [A.AgentRight.DesktopViewOnly] = [Users],
                [A.AgentRight.SecureDesktop] = [Admins],
                [A.AgentRight.ConsentBypass] = [Admins],
            },
        };
        var log = Environment.GetEnvironmentVariable("RH_TEST_LOG") is { } folder ? new A.AgentLog(folder, 1) : new A.AgentLog("", 1);
        var audits = new ConcurrentQueue<A.AuditEvent>();
        log.Audited += (id, _) => audits.Enqueue(id);
        var app = A.AgentHost.Build(new A.AgentHostOptions(config, cert, new Auth(), null!, log, IPAddress.Loopback,
            new A.DesktopServices(sessions, helpers, () => null), () => policy));
        await app.StartAsync();
        return new Device { App = app, Port = new Uri(app.Urls.First()).Port, Fingerprint = AgentProtocol.Fingerprint(cert.RawData), Helpers = helpers, Input = input, Sessions = sessions, Audits = audits };
    }

    private static PSCredential Cred(string user)
    {
        var pw = new SecureString();
        foreach (var c in "pw") pw.AppendChar(c);
        return new PSCredential(@".\" + user, pw);
    }

    /// <summary>The client with every message collected; video frames are acknowledged like the real viewer does.</summary>
    private sealed class Viewer : IDisposable
    {
        public readonly AgentDesktopClient Client;
        public readonly BlockingCollection<DesktopStatus> Statuses = new();
        public readonly ConcurrentQueue<VideoHeader> Frames = new();
        public readonly ConcurrentQueue<DesktopLayout> Layouts = new();
        public readonly ConcurrentQueue<string> Clipboard = new();
        public readonly ConcurrentQueue<DesktopTransfer> Transfers = new();
        public readonly ConcurrentQueue<DesktopTransferEnd> TransferEnds = new();
        public readonly MemoryStream TransferBytes = new();
        public int CursorShapes, CursorMoves, Pongs, Stats;

        public Viewer(AgentDesktopClient client)
        {
            Client = client;
            client.Received += m =>
            {
                switch (DesktopWire.TypeOf(m))
                {
                    case DesktopMessage.Status: Statuses.Add(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopStatus)); break;
                    case DesktopMessage.Layout: Layouts.Enqueue(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopLayout)); break;
                    case DesktopMessage.Video:
                        var h = DesktopWire.ReadVideo(m);
                        Frames.Enqueue(h);
                        client.Send(DesktopWire.Ack(h.FrameId));
                        break;
                    case DesktopMessage.CursorShape: Interlocked.Increment(ref CursorShapes); break;
                    case DesktopMessage.CursorPosition: Interlocked.Increment(ref CursorMoves); break;
                    case DesktopMessage.Clipboard: Clipboard.Enqueue(DesktopWire.ReadText(m)); break;
                    case DesktopMessage.Pong: Interlocked.Increment(ref Pongs); break;
                    case DesktopMessage.Stats: Interlocked.Increment(ref Stats); break;
                    case DesktopMessage.TransferStart: Transfers.Enqueue(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer)); break;
                    case DesktopMessage.TransferData: lock (TransferBytes) TransferBytes.Write(m, DesktopWire.TransferHeaderSize, m.Length - DesktopWire.TransferHeaderSize); break;
                    case DesktopMessage.TransferEnd: TransferEnds.Enqueue(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransferEnd)); break;
                }
            };
            client.Closed += why => _closed = why;
            client.Start();
        }

        private volatile string? _closed;

        public DesktopStatus Next(string? until = null)
        {
            while (true)
            {
                Assert.True(Statuses.TryTake(out var s, TimeSpan.FromSeconds(15)), "no status from the agent" + (_closed is { } c ? $" (connection closed: {c})" : ""));
                if (until is null || s!.State == until || s.State is DesktopStatusState.Failed or DesktopStatusState.Denied) return s!;
            }
        }

        public void Start(int session, string consent, bool viewOnly = false, string? resume = null) =>
            Client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session, consent, viewOnly, 30, 60, 0, resume), DesktopJson.Default.DesktopStart));

        public void Dispose() => Client.Dispose();
    }

    private static async Task<Viewer> Connect(Device d, string user = "admin") =>
        new(await AgentDesktopClient.ConnectAsync("127.0.0.1", new AgentEndpoint(d.Port, d.Fingerprint), Cred(user), CancellationToken.None));

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for " + what);
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task Asked_user_allows_then_picture_input_and_clipboard_flow()
    {
        await using var d = await StartDevice(allow: true);
        using var v = await Connect(d);
        Assert.Equal("TEST\\admin", v.Client.Hello.Account);
        Assert.True(v.Client.Hello.Rights.Control);
        Assert.Contains(DesktopConsent.None, v.Client.Hello.ConsentModes);
        Assert.Equal(2, v.Client.Hello.Sessions.Length);

        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        Assert.Equal(DesktopStatusState.WaitingForUser, v.Next(DesktopStatusState.WaitingForUser).State);
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);

        WaitFor(() => v.Frames.Count >= 5, "video frames");
        var frames = v.Frames.ToArray();
        Assert.True(frames[0].KeyFrame);
        Assert.Equal((320, 240), (frames[0].Width, frames[0].Height));
        Assert.True(v.Layouts.TryPeek(out var layout) && layout.Monitors.Length == 2 && !layout.ViewOnly);
        WaitFor(() => v.CursorShapes > 0 && v.CursorMoves > 0, "pointer");
        Assert.True(d.Helpers.Uis.Single().Banner);

        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 5, 6, 0)));
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1E, RemoteKeyFlags.None)));
        v.Client.Send(DesktopWire.Text(DesktopMessage.Clipboard, "from RemoteHub"));
        WaitFor(() => d.Input.Mouse.Count == 1 && d.Input.Keys.Count == 1, "input on the device");
        Assert.Equal(5, d.Input.Mouse.Single().X);
        Assert.Equal((ushort)0x1E, d.Input.Keys.Single().ScanCode);
        WaitFor(() => d.Helpers.Uis.Single().Clipboard.Contains("from RemoteHub"), "clipboard on the device");
        d.Helpers.Uis.Single().RaiseClipboard("from the device");
        WaitFor(() => v.Clipboard.Contains("from the device"), "clipboard from the device");

        // Another monitor: a new layout and a new key frame in its size.
        v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(1, 30, 60), DesktopJson.Default.DesktopSettings));
        WaitFor(() => v.Frames.Any(f => f.Width == 200 && f.Height == 100 && f.KeyFrame), "frames of the second monitor");
    }

    [Fact]
    public async Task View_only_drops_input_in_the_agent()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d, "viewer");
        Assert.False(v.Client.Hello.Rights.Control);
        Assert.DoesNotContain(DesktopConsent.None, v.Client.Hello.ConsentModes); // no ConsentBypass right
        v.Start(ActiveUser.Id, DesktopConsent.Ask, viewOnly: false); // asks for control anyway
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
        WaitFor(() => v.Frames.Count >= 2, "video frames");
        Assert.True(v.Layouts.TryPeek(out var layout) && layout.ViewOnly);
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 1, 1, 0)));
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1E, RemoteKeyFlags.None)));
        v.Client.Send(DesktopWire.Text(DesktopMessage.Clipboard, "nope"));
        await Task.Delay(500);
        Assert.Empty(d.Input.Mouse);
        Assert.Empty(d.Input.Keys);
        Assert.Empty(d.Helpers.Uis.Single().Clipboard);
    }

    [Fact]
    public async Task The_code_must_match()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Pin);
        var s = v.Next(DesktopStatusState.EnterPin);
        Assert.Equal(3, s.PinAttemptsLeft);
        WaitFor(() => d.Helpers.Uis.Count == 1 && d.Helpers.Uis[0].Pin is { Length: 6 }, "code on the device");
        var pin = d.Helpers.Uis[0].Pin!;
        v.Client.Send(DesktopWire.Json(DesktopMessage.Pin, new DesktopPin(pin == "000000" ? "111111" : "000000"), DesktopJson.Default.DesktopPin));
        s = v.Next(DesktopStatusState.EnterPin);
        Assert.Equal(2, s.PinAttemptsLeft);
        Assert.Empty(v.Frames);
        v.Client.Send(DesktopWire.Json(DesktopMessage.Pin, new DesktopPin(pin[..3] + " " + pin[3..]), DesktopJson.Default.DesktopPin));
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
        WaitFor(() => v.Frames.Count >= 1, "video frames");
    }

    [Fact]
    public async Task Wrong_codes_end_the_attempt()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Pin);
        v.Next(DesktopStatusState.EnterPin);
        for (var i = 0; i < 3; i++)
            v.Client.Send(DesktopWire.Json(DesktopMessage.Pin, new DesktopPin("12345" + i), DesktopJson.Default.DesktopPin));
        var s = v.Next(DesktopStatusState.Denied);
        Assert.Equal(DesktopStatusState.Denied, s.State);
        Assert.Empty(v.Frames);
    }

    [Fact]
    public async Task A_refusing_user_gets_no_picture()
    {
        await using var d = await StartDevice(allow: false);
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        Assert.Equal(DesktopStatusState.Denied, v.Next(DesktopStatusState.Connected).State);
        await Task.Delay(300);
        Assert.Empty(v.Frames);
        Assert.False(d.Helpers.Uis.Single().Banner);
    }

    [Fact]
    public async Task Connecting_without_asking_needs_the_right()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d, "viewer");
        v.Start(ActiveUser.Id, DesktopConsent.None);
        var s = v.Next();
        Assert.Equal(DesktopStatusState.Failed, s.State);
        Assert.Equal(0, d.Helpers.Started);
    }

    [Fact]
    public async Task The_sign_in_screen_needs_no_consent_but_the_secure_desktop_right()
    {
        await using (var d = await StartDevice(allow: false, secureScreen: true))
        {
            using var v = await Connect(d);
            v.Start(LoginScreen.Id, DesktopConsent.Ask); // ignored: nobody to ask
            Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
            WaitFor(() => v.Frames.Count >= 1, "sign-in screen frames");
            Assert.True(v.Layouts.TryPeek(out var layout) && layout.SecureDesktop);
        }
        await using (var d = await StartDevice(secureScreen: true))
        {
            using var v = await Connect(d, "viewer");
            v.Start(LoginScreen.Id, DesktopConsent.Ask);
            Assert.Equal(DesktopStatusState.Failed, v.Next().State);
        }
    }

    [Fact]
    public async Task Whoever_unlocks_is_asked_before_anything_of_their_desktop_is_sent()
    {
        var locked = new A.DesktopSessionInfo(1, @"PC\alice", A.DesktopSessionState.Locked, true, "Console");
        Screen? first = null;
        await using var d = await StartDevice(screens: n => n == 1 ? first = new Screen(secure: true) : new Screen());
        d.Sessions.Current = [locked];
        using var v = await Connect(d);
        v.Start(locked.Id, DesktopConsent.Ask); // the lock screen is shown right away; Ask applies on unlock
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => v.Frames.Count >= 1, "lock screen frames");
        Assert.Equal(0, d.Helpers.Uis[0].Asked);

        d.Sessions.Current = [ActiveUser];
        first!.Secure = false; // alice unlocks
        var waiting = v.Next(DesktopStatusState.WaitingForUser);
        Assert.Contains("alice", waiting.Message);
        var framesBefore = v.Frames.Count;
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
        Assert.Equal(1, d.Helpers.Uis[1].Asked);
        WaitFor(() => v.Frames.Count > framesBefore, "frames after she allowed it");
        Assert.Equal(2, d.Helpers.Started);
    }

    [Fact]
    public async Task A_disconnected_session_is_reported_and_its_return_too()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        d.Sessions.Disconnected = true; // the RDP window of that session was closed
        Assert.Contains("disconnected", v.Next(DesktopStatusState.Paused).Message);
        d.Sessions.Disconnected = false;
        Assert.Contains("screen again", v.Next(DesktopStatusState.Connected).Message);
    }

    [Fact]
    public async Task Signing_in_with_your_own_account_asks_nobody()
    {
        Screen? first = null;
        await using var d = await StartDevice(screens: n => n == 1 ? first = new Screen(secure: true) : new Screen());
        using var v = await Connect(d);
        v.Start(LoginScreen.Id, DesktopConsent.Pin);
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => v.Frames.Count >= 1, "sign-in screen frames");
        d.Sessions.Current = [ActiveUser, LoginScreen with { User = @"TEST\admin", State = A.DesktopSessionState.Active }];
        first!.Secure = false; // the admin signs in on the device with the account they connected with
        WaitFor(() => d.Helpers.Started == 2, "a new helper");
        var before = v.Frames.Count;
        WaitFor(() => v.Frames.Count > before, "frames of the admin's own desktop");
        Assert.Null(d.Helpers.Uis[1].Pin);
        Assert.Equal(0, d.Helpers.Uis[1].Asked);
        Assert.DoesNotContain(v.Statuses, s => s.State is DesktopStatusState.EnterPin or DesktopStatusState.WaitingForUser);
    }

    [Fact]
    public async Task Connecting_to_your_own_session_asks_nobody()
    {
        await using var d = await StartDevice();
        d.Sessions.Current = [ActiveUser with { User = @"TEST\admin" }];
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        Assert.Equal(0, d.Helpers.Uis[0].Asked);
    }

    [Fact]
    public async Task A_refusal_after_sign_in_ends_the_session()
    {
        Screen? first = null;
        // The sign-in screen needs no answer; the user who signs in then refuses.
        await using var d = await StartDevice(screens: n => n == 1 ? first = new Screen(secure: true) : new Screen(), allowAt: n => n == 1);
        using var v = await Connect(d);
        v.Start(LoginScreen.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => v.Frames.Count >= 1, "sign-in screen frames");
        d.Sessions.Current = [ActiveUser, LoginScreen with { User = @"PC\carol", State = A.DesktopSessionState.Active }];
        first!.Secure = false; // carol signs in
        v.Next(DesktopStatusState.WaitingForUser);
        var framesAtSignIn = v.Frames.Count; // everything before is the sign-in screen
        Assert.Equal(DesktopStatusState.Denied, v.Next(DesktopStatusState.Denied).State);
        Assert.Equal(framesAtSignIn, v.Frames.Count); // nothing of carol's desktop was sent
    }

    [Fact]
    public async Task Ping_is_answered_and_stats_arrive()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        Assert.Contains(DesktopFeatures.Ping, v.Client.Hello.Features!);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        v.Client.Send(DesktopWire.Ping(42));
        WaitFor(() => v.Pongs == 1, "pong");
        WaitFor(() => v.Stats >= 1, "stats from the device");
    }

    [Fact]
    public async Task All_monitors_are_shown_in_one_picture()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(DesktopProtocol.AllMonitors, 30, 60, 1600, 900), DesktopJson.Default.DesktopSettings));
        WaitFor(() => v.Layouts.Any(l => l.Monitor == DesktopProtocol.AllMonitors), "layout of all monitors");
        var all = v.Layouts.Last(l => l.Monitor == DesktopProtocol.AllMonitors);
        Assert.Equal((520, 240), (all.Width, all.Height)); // 320 + 200 side by side, the taller height
        await Task.Delay(500);
        Assert.Empty(d.Helpers.Screens[0].Fits); // no resolution change for all monitors
    }

    [Fact]
    public void A_large_picture_is_scaled_down_evenly()
    {
        var source = new A.FrameBuffer();
        source.Ensure(4, 2);
        for (var i = 0; i < source.Pixels.Length; i += 4) { source.Pixels[i] = (byte)(i / 4 % 2 == 0 ? 0 : 200); source.Pixels[i + 3] = 255; }
        var target = new A.FrameBuffer();
        A.DesktopHelperSession.Downscale(source, target, 2);
        Assert.Equal((2, 1), (target.Width, target.Height));
        Assert.Equal(100, target.Pixels[0]); // average of 0 and 200
    }

    [Fact]
    public async Task Idle_sessions_are_warned_and_ended()
    {
        var policy = new A.AgentPolicy
        {
            IdleTimeout = TimeSpan.FromSeconds(4),
            Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>> { [A.AgentRight.Desktop] = [Admins], [A.AgentRight.ConsentBypass] = [Admins] },
        };
        await using var d = await StartDevice(policy: policy);
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        var warning = v.Next(DesktopStatusState.Connected);
        Assert.Contains("ends soon", warning.Message); // a notice while still connected
        var end = v.Next(DesktopStatusState.Ended);
        Assert.Contains("without mouse or keyboard input", end.Message);
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopIdleEnded), "audit entry");
    }

    [Fact]
    public async Task A_dropped_connection_continues_without_asking_again()
    {
        await using var d = await StartDevice();
        string token;
        var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        token = first.Next(DesktopStatusState.Connected).Resume!;
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal(1, d.Helpers.Uis[0].Asked);

        // The old connection still looks alive (a dead network isn't noticed at once): the new one takes over.
        using var second = await Connect(d);
        second.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        var connected = second.Next(DesktopStatusState.Connected);
        Assert.False(string.IsNullOrEmpty(connected.Resume));
        Assert.Equal(0, d.Helpers.Uis[1].Asked); // alice allowed it already
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopResumed), "audit entry");
        first.Dispose();

        // The token is used up: a third connection with it is asked again.
        using var third = await Connect(d);
        third.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        third.Next(DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[2].Asked);
    }

    [Fact]
    public async Task A_token_is_only_for_the_same_account()
    {
        await using var d = await StartDevice();
        using var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        var token = first.Next(DesktopStatusState.Connected).Resume!;
        using var other = await Connect(d, "admin2");
        other.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        other.Next(DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[1].Asked);
    }

    [Fact]
    public async Task Images_and_files_are_copied_both_ways()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        Assert.Equal(DesktopClipboardLevel.Files, v.Client.Hello.Clipboard);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);

        // RemoteHub → device: an image in two chunks.
        var png = Enumerable.Range(0, DesktopProtocol.TransferChunkBytes + 10).Select(i => (byte)i).ToArray();
        var image = ClipboardTransfer.Image(1, png, png: true);
        v.Client.Send(DesktopWire.Json(DesktopMessage.TransferStart, image.Transfer, DesktopJson.Default.DesktopTransfer));
        foreach (var chunk in ClipboardTransfer.Chunks(image)) v.Client.Send(chunk);
        WaitFor(() => d.Helpers.Uis[0].Image is { } i && i.SequenceEqual(png), "image on the device's clipboard");

        // RemoteHub → device: a folder with a file.
        var source = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "a.txt"), "hello");
        try
        {
            var files = ClipboardTransfer.Files(2, [source], out var error)!;
            Assert.Null(error);
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferStart, files.Transfer, DesktopJson.Default.DesktopTransfer));
            foreach (var chunk in ClipboardTransfer.Chunks(files)) v.Client.Send(chunk);
            WaitFor(() => d.Helpers.Uis[0].Files is { Count: 1 }, "files on the device's clipboard");
            var copied = d.Helpers.Uis[0].Files![0];
            Assert.Equal("hello", File.ReadAllText(Path.Combine(copied, "sub", "a.txt")));
            Directory.Delete(Path.GetDirectoryName(copied)!, true);

            // Device → RemoteHub: files copied there.
            d.Helpers.Uis[0].RaiseContent(new A.HelperClipboard(Files: [Path.Combine(source, "sub", "a.txt")]));
            WaitFor(() => v.Transfers.Any(t => t.Kind == DesktopTransferKind.Files), "transfer from the device");
            WaitFor(() => { lock (v.TransferBytes) return v.TransferBytes.Length == 5; }, "file data");
        }
        finally { Directory.Delete(source, true); }
    }

    [Fact]
    public async Task The_clipboard_policy_limits_what_is_copied()
    {
        var policy = A.AgentPolicy.From(new A.AgentConfig { AllowedGroups = [Admins] }, new Values(new() { ["DesktopClipboard"] = DesktopClipboardLevel.Text }));
        await using var d = await StartDevice(policy: policy);
        using var v = await Connect(d);
        Assert.Equal(DesktopClipboardLevel.Text, v.Client.Hello.Clipboard);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        var image = ClipboardTransfer.Image(1, [1, 2, 3], png: true);
        v.Client.Send(DesktopWire.Json(DesktopMessage.TransferStart, image.Transfer, DesktopJson.Default.DesktopTransfer));
        foreach (var chunk in ClipboardTransfer.Chunks(image)) v.Client.Send(chunk);
        WaitFor(() => v.TransferEnds.Any(e => !e.Ok), "refusal");
        Assert.Null(d.Helpers.Uis[0].Image);
        d.Helpers.Uis[0].RaiseContent(new A.HelperClipboard([1, 2, 3], Png: true));
        await Task.Delay(500);
        Assert.Empty(v.Transfers);
    }

    [Fact]
    public async Task Sessions_and_refusals_are_audited()
    {
        await using var d = await StartDevice(allow: false);
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Denied);
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopRefused), "refusal audited");
    }

    [Fact]
    public void Transfer_paths_cannot_leave_their_folder()
    {
        Assert.Null(DesktopWire.SafeRelativePath(".."));
        Assert.Null(DesktopWire.SafeRelativePath("a/../.."));
        Assert.Equal(Path.Combine("C", "Windows", "x.txt"), DesktopWire.SafeRelativePath(@"C:\Windows\x.txt"));
        var folder = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidDataException>(() => new TransferReceiver(
                new DesktopTransfer(1, DesktopTransferKind.Files, [new DesktopTransferItem("../evil.txt", 1)], 1), folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_bitmap_from_the_clipboard_gets_its_file_header()
    {
        var dib = new byte[40 + 4];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(1).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        var bmp = A.Win32HelperUi.DibToBmp(dib)!;
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
    }

    [Fact]
    public async Task A_disabled_desktop_is_explained()
    {
        var policy = A.AgentPolicy.From(new A.AgentConfig(), new Values(new() { ["DesktopEnabled"] = 0 }));
        await using var d = await StartDevice(policy: policy);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Connect(d));
        Assert.Contains("turned off by a policy", ex.GetBaseException().Message + ex.Message);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var json = await http.GetStringAsync($"https://127.0.0.1:{d.Port}{AgentProtocol.HealthPath}");
        Assert.DoesNotContain("desktop", json);
    }

    [Fact]
    public async Task Health_lists_the_desktop()
    {
        await using var d = await StartDevice();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var json = await http.GetStringAsync($"https://127.0.0.1:{d.Port}{AgentProtocol.HealthPath}");
        Assert.Contains("\"desktop\"", json);
    }

    [Fact]
    public async Task A_failing_helper_is_started_again_without_asking()
    {
        // The first helper loses its desktop after a few frames (like at sign-in); the next one keeps going.
        await using var d = await StartDevice(screens: n => new Screen(failAfter: n == 1 ? 3 : int.MaxValue));
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        var notice = v.Next(DesktopStatusState.Connected);
        Assert.Contains("reconnecting", notice.Message);
        WaitFor(() => d.Helpers.Started == 2, "a second helper");
        // The new helper starts its own stream: frame 1 (a key frame) arrives a second time.
        WaitFor(() => v.Frames.Count(f => f.FrameId == 1u) == 2, "frames from the new helper");
        Assert.All(v.Frames.Where(f => f.FrameId == 1u), f => Assert.True(f.KeyFrame));
        Assert.True(d.Helpers.Uis[1].Banner);
    }

    [Fact]
    public async Task Disconnect_on_the_device_ends_the_session()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => d.Helpers.Uis.Count == 1 && d.Helpers.Uis[0].Disconnect is not null, "banner");
        d.Helpers.Uis[0].Disconnect!();
        Assert.Equal(DesktopStatusState.Ended, v.Next(DesktopStatusState.Ended).State);
        await Task.Delay(500);
        Assert.Equal(1, d.Helpers.Started);
    }

    [Fact]
    public async Task The_console_is_followed_into_a_new_session()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => v.Frames.Count >= 1, "frames");
        d.Sessions.Current = [ActiveUser, new A.DesktopSessionInfo(7, @"PC\bob", A.DesktopSessionState.Active, true, "Console")];
        d.Sessions.Console = 7; // someone signed in: Windows moved the console to session 7
        WaitFor(() => d.Helpers.Started == 2, "a helper in the new session");
        Assert.Equal([ActiveUser.Id, 7], d.Helpers.SessionIds);
        var before = v.Frames.Count;
        WaitFor(() => v.Frames.Count > before, "frames from session 7");
    }

    [Fact]
    public async Task The_resolution_follows_the_window_but_not_for_view_only()
    {
        await using (var d = await StartDevice())
        {
            using var v = await Connect(d);
            v.Start(ActiveUser.Id, DesktopConsent.None);
            v.Next(DesktopStatusState.Connected);
            v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(0, 30, 60, 1600, 900), DesktopJson.Default.DesktopSettings));
            WaitFor(() => d.Helpers.Screens[0].Fits.Contains((1600, 900)), "resolution change");
            v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(0, 30, 60), DesktopJson.Default.DesktopSettings));
            WaitFor(() => d.Helpers.Screens[0].Restores > 0, "resolution restored");
        }
        await using (var d = await StartDevice())
        {
            using var v = await Connect(d, "viewer");
            v.Start(ActiveUser.Id, DesktopConsent.Ask);
            v.Next(DesktopStatusState.Connected);
            v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(0, 30, 60, 1600, 900), DesktopJson.Default.DesktopSettings));
            await Task.Delay(800);
            Assert.Empty(d.Helpers.Screens[0].Fits);
        }
    }

    [Theory]
    [InlineData(1600, 900, 1600, 900)]   // listed: exact
    [InlineData(1700, 950, 1600, 900)]   // largest that fits
    [InlineData(1000, 1000, 800, 600)]   // 1024 is wider than the window
    [InlineData(500, 400, 800, 600)]     // window smaller than every mode: the smallest
    public void The_closest_display_mode_is_chosen(int w, int h, int ew, int eh)
    {
        (int, int)[] modes = [(800, 600), (1024, 768), (1280, 720), (1600, 900), (1920, 1080)];
        Assert.Equal((ew, eh), A.DisplayModes.Choose(modes, w, h));
    }

    private sealed class Values(Dictionary<string, object> values) : A.AgentPolicy.IPolicyValues
    {
        public int? Number(string? subKey, string name) => values.TryGetValue(Key(subKey, name), out var v) && v is int i ? i : null;
        public IReadOnlyList<string>? Strings(string? subKey, string name) => values.TryGetValue(Key(subKey, name), out var v) && v is string[] s ? s : null;
        private static string Key(string? sub, string name) => sub is null ? name : sub + "\\" + name;
    }

    [Fact]
    public void Policy_values_override_the_defaults()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins] };
        var d = A.AgentPolicy.From(config, null);
        Assert.True(d.DesktopEnabled);
        Assert.Equal([Admins], d.Groups(A.AgentRight.Desktop));
        Assert.Empty(d.Groups(A.AgentRight.DesktopViewOnly));
        Assert.Equal(3, d.ConsentModes.Count);

        var p = A.AgentPolicy.From(config, new Values(new()
        {
            ["AllowedConsentModes"] = new[] { "pin", "Ask", "bogus" },
            ["ConsentTimeoutSeconds"] = 45,
            ["MaxFramesPerSecond"] = 500, // invalid: default kept
            ["MaxQuality"] = 70,
            ["Rights\\Desktop"] = new[] { "S-1-5-32-555", " " },
            ["Rights\\ConsentBypass"] = Array.Empty<string>(),
        }));
        Assert.Equal([A.DesktopConsent.Pin, A.DesktopConsent.Ask], p.ConsentModes);
        Assert.Equal(45, p.ConsentTimeoutSeconds);
        Assert.Equal(60, p.MaxFramesPerSecond);
        Assert.Equal(70, p.MaxQuality);
        Assert.Equal(["S-1-5-32-555"], p.Groups(A.AgentRight.Desktop));
        Assert.Empty(p.Groups(A.AgentRight.ConsentBypass));
        Assert.Equal([Admins], p.Groups(A.AgentRight.PowerShell));
    }
}
