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
            return new A.AuthResult(A.AuthOutcome.Ok, "", new A.ShellIdentity("TEST\\" + userName[2..], default, sids, SidOf(userName[2..])));
        }
    }

    /// <summary>The test accounts' SIDs (automatic sign-in checks the account by SID).</summary>
    private static string SidOf(string user) => user switch { "admin" => "S-1-5-21-7-7-7-1001", "admin2" => "S-1-5-21-7-7-7-1002", _ => "S-1-5-21-7-7-7-1003" };

    private sealed class Sessions(params A.DesktopSessionInfo[] list) : A.IDesktopSessions
    {
        /// <summary>Who is signed in to a session, by SID (none: the session's name decides).</summary>
        public readonly ConcurrentDictionary<int, string> Sids = new();
        public readonly ConcurrentQueue<int> LogOffs = new();
        public string? UserSid(int session) => Sids.TryGetValue(session, out var sid) ? sid : null;
        /// <summary>Set to make Windows refuse a sign-out / a lock.</summary>
        public volatile bool RefuseLogOff, RefuseDisconnect;
        // A sign-out ends the session: nobody is signed in to it afterwards.
        public bool LogOff(int session) { if (RefuseLogOff) return false; LogOffs.Enqueue(session); Sids.TryRemove(session, out _); return true; }
        public readonly ConcurrentQueue<int> Disconnects = new();
        public bool Disconnect(int session) { if (RefuseDisconnect) return false; Disconnects.Enqueue(session); return true; }
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
    private sealed class InProcessHelpers(Func<Ui> ui, Func<int, Screen> screen, Input input, Apps apps) : A.IDesktopHelperLauncher
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
            var session = new A.DesktopHelperSession(helperIn, helperOut, u, sc, input, apps);
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
        public void AskConsent(A.HelperInit init, Action<bool> answered)
        {
            Interlocked.Increment(ref Asked);
            // The user picks apps to hide in the question ("Allow and hide apps…"), then allows.
            if (PickOnConsent is { } pick && allow) Chosen?.Invoke(pick);
            Task.Run(() => answered(allow));
        }
        public IReadOnlySet<string>? PickOnConsent;
        public volatile IReadOnlySet<string>? OfferedHidden, OfferedAlways;
        public volatile Action<IReadOnlySet<string>>? Chosen;
        public void OfferHideApps(IReadOnlySet<string> hidden, IReadOnlySet<string> always, Action<IReadOnlySet<string>> chosen) => (OfferedHidden, OfferedAlways, Chosen) = (hidden, always, chosen);
        public void ShowPin(A.HelperInit init, Action denied) { Pin = init.Pin; if (!allow) Task.Run(denied); }
        public void ShowBanner(A.HelperInit init, Action disconnect, Action<string> hide) { Banner = true; Disconnect = disconnect; Hide = hide; }
        /// <summary>The account shown instead of the banner, in a session automatic sign-in created.</summary>
        public volatile string? SignedInRemotely;
        public void ShowSignedInRemotely(A.HelperInit init) => SignedInRemotely = init.Account;
        /// <summary>The banner's "Hide screen" / "Show screen": plays the user at the device.</summary>
        public volatile Action<string>? Hide;
        public volatile A.DesktopPrivacy? Shown;
        public void ShowPrivacy(A.DesktopPrivacy privacy) => Shown = privacy;
        /// <summary>The privacy screen as the helper asked for it; <see cref="ScreenError"/> plays a device that can't show it.</summary>
        public readonly ConcurrentQueue<A.DesktopPrivacyScreen> Screens = new();
        public volatile string? ScreenError;
        public void SetPrivacyScreen(A.DesktopPrivacyScreen screen, Action<string?> done) { Screens.Enqueue(screen); done(ScreenError); }
        public void CloseDialogs() { }
        public void SetClipboard(string text) { lock (Clipboard) Clipboard.Add(text); }
        public void RaiseClipboard(string text) => ClipboardChanged?.Invoke(text);
        public volatile byte[]? Image;
        public volatile IReadOnlyList<string>? Files;
        public event Action<A.HelperClipboard>? ClipboardContent;
        public volatile bool ImageIsPng;
        public void SetClipboardImage(byte[] image, bool png) { ImageIsPng = png; Image = image; }
        public void SetClipboardFiles(IReadOnlyList<string> paths) => Files = paths;
        public void RaiseContent(A.HelperClipboard c) => ClipboardContent?.Invoke(c);
        /// <summary>The last offer of RemoteHub's clipboard; <see cref="Paste"/> plays someone pasting it.</summary>
        public volatile Func<string, A.HelperClipboard?>? Offered;
        public volatile bool OfferedImage;
        public int Offers;
        public void OfferClipboard(A.DesktopTransfer offer, Func<string, A.HelperClipboard?> fetch) { (OfferedImage, Offered) = (offer.Kind == A.DesktopTransferKind.Image, fetch); Interlocked.Increment(ref Offers); }
        public Task<A.HelperClipboard?> Paste() => Task.Run(() => Offered!("test (1)"));
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

    /// <summary>The device's windows as the test sets them (topmost first), the window with the keyboard and the clipboard's owner.</summary>
    private sealed class Apps : A.IAppWindows
    {
        public volatile A.AppWindow[] Windows = [];
        public volatile string Foreground = "", ClipboardOwner = "";
        public IReadOnlyList<A.AppWindow> Visible() => Windows;
        public IReadOnlyList<A.RunningApp> Running() => Windows.Where(w => w.App.Length > 0).GroupBy(w => w.App).Select(g => new A.RunningApp(g.Key, g.Key, g.Count(), IntPtr.Zero)).ToList();
        public A.AppIdentity ForegroundApp() => new(Foreground);
        public A.AppIdentity ClipboardOwnerApp() => new(ClipboardOwner);
    }

    private sealed class Input : A.IInputSink
    {
        public readonly ConcurrentQueue<A.RemoteMouse> Mouse = new();
        public readonly ConcurrentQueue<A.RemoteKey> Keys = new();
        public int Releases, SecureAttentions;
        public volatile string? SecureAttentionError;
        void A.IInputSink.Mouse(A.DesktopMonitor monitor, A.RemoteMouse input) => Mouse.Enqueue(input);
        void A.IInputSink.Key(A.RemoteKey input) => Keys.Enqueue(input);
        public void ReleaseAll() => Interlocked.Increment(ref Releases);
        string? A.IInputSink.SecureAttention() { Interlocked.Increment(ref SecureAttentions); return SecureAttentionError; }
    }

    private sealed class Device : IAsyncDisposable
    {
        public required WebApplication App;
        public required int Port;
        public required string Fingerprint;
        public required InProcessHelpers Helpers;
        public required Sessions Sessions;
        public required Input Input;
        public required Apps Apps;
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
        Func<int, bool>? allowAt = null, Func<string?>? secureAttention = null, IReadOnlySet<string>? pickOnConsent = null,
        A.IAutoSignIn? autoSignIn = null, A.DesktopSessionInfo[]? sessionList = null)
    {
        var cert = SelfSigned();
        var input = new Input();
        var uis = 0;
        var apps = new Apps();
        var helpers = new InProcessHelpers(() => new Ui(allowAt?.Invoke(Interlocked.Increment(ref uis)) ?? allow) { PickOnConsent = pickOnConsent }, screens ?? (_ => new Screen(secureScreen)), input, apps);
        var sessions = new Sessions(sessionList ?? [ActiveUser, LoginScreen]);
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
            new A.DesktopServices(sessions, helpers, secureAttention ?? (() => null), autoSignIn), () => policy));
        await app.StartAsync();
        return new Device { App = app, Port = new Uri(app.Urls.First()).Port, Fingerprint = AgentProtocol.Fingerprint(cert.RawData), Helpers = helpers, Input = input, Apps = apps, Sessions = sessions, Audits = audits };
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
        public readonly ConcurrentQueue<DesktopPrivacy> Privacy = new();
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
                    case DesktopMessage.Privacy: Privacy.Enqueue(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopPrivacy)); break;
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

        public void Start(int session, string consent, bool viewOnly = false, string? resume = null, string[]? features = null, bool autoSignIn = false) =>
            Client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session, consent, viewOnly, 30, 60, 0, resume, features, autoSignIn), DesktopJson.Default.DesktopStart));

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

    // ---- privacy

    private static A.AgentPolicy PrivacyPolicy(int level = A.PrivacyScreenLevel.Allowed) => new()
    {
        PrivacyScreen = level,
        Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>>
        {
            [A.AgentRight.Desktop] = [Admins],
            [A.AgentRight.DesktopViewOnly] = [Users],
            [A.AgentRight.SecureDesktop] = [Admins],
            [A.AgentRight.PrivacyScreen] = [Admins],
        },
    };

    private static void SendPrivacyScreen(Viewer v, string style, bool block = false) =>
        v.Client.Send(DesktopWire.Json(DesktopMessage.PrivacyScreen, new DesktopPrivacyScreen(style, block), DesktopJson.Default.DesktopPrivacyScreen));

    private static DesktopPrivacy? LastPrivacy(Viewer v) => v.Privacy.LastOrDefault();

    [Fact]
    public async Task The_user_can_hide_their_screen_and_show_it_again()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        Assert.Contains(DesktopFeatures.UserPrivacy, v.Client.Hello.Features!);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        WaitFor(() => v.Frames.Count >= 3, "video frames");
        var ui = d.Helpers.Uis.Single();
        WaitFor(() => ui.Hide is not null, "the banner");

        // Black: one black frame, then no picture, no pointer and no input until it's shown again.
        ui.Hide!(DesktopScreenHide.Black);
        WaitFor(() => LastPrivacy(v)?.User == DesktopScreenHide.Black, "the hidden screen reported to RemoteHub");
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopUserHid), "audit entry");
        Assert.Equal(DesktopScreenHide.Black, ui.Shown?.User);
        await Task.Delay(400);
        var (frames, moves) = (v.Frames.Count, v.CursorMoves);
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 5, 6, 0)));
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1E, RemoteKeyFlags.None)));
        v.Client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
        Assert.Contains("hidden their screen", v.Next(DesktopStatusState.Connected).Message);
        await Task.Delay(600);
        Assert.Equal(frames, v.Frames.Count);
        Assert.Equal(moves, v.CursorMoves);
        Assert.Empty(d.Input.Mouse);
        Assert.Empty(d.Input.Keys);

        // Shown again: the picture comes at once, and input goes through.
        ui.Hide(DesktopScreenHide.None);
        WaitFor(() => LastPrivacy(v)?.User == DesktopScreenHide.None, "the shown screen reported to RemoteHub");
        WaitFor(() => v.Frames.Count > frames, "the picture again");
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 7, 8, 0)));
        WaitFor(() => d.Input.Mouse.Count == 1, "input on the device again");
        Assert.Equal(7, d.Input.Mouse.Single().X);
    }

    [Fact]
    public void Blurring_leaves_nothing_of_the_detail_within_a_block()
    {
        // Two pictures that differ in every pixel but not in what a block averages to (a checkerboard and its inverse –
        // like text) come out the same: nothing of the detail is left to undo.
        A.FrameBuffer Checkerboard(int phase)
        {
            var f = new A.FrameBuffer();
            f.Ensure(4 * A.DesktopHelperSession.BlurBlock, 2 * A.DesktopHelperSession.BlurBlock);
            for (var y = 0; y < f.Height; y++)
                for (var x = 0; x < f.Width; x++)
                {
                    var p = (y * f.Width + x) * 4;
                    var v = (byte)((x + y + phase) % 2 == 0 ? 255 : 0);
                    (f.Pixels[p], f.Pixels[p + 1], f.Pixels[p + 2], f.Pixels[p + 3]) = (v, v, v, 255);
                }
            return f;
        }
        var (a, b) = (new A.FrameBuffer(), new A.FrameBuffer());
        A.DesktopHelperSession.Blur(Checkerboard(0), a);
        A.DesktopHelperSession.Blur(Checkerboard(1), b);
        Assert.Equal(a.Pixels, b.Pixels);
        Assert.All(a.Pixels.Where((_, i) => i % 4 != 3), v => Assert.InRange(v, (byte)127, (byte)128)); // mid-grey everywhere
    }

    [Fact]
    public async Task The_privacy_screen_needs_the_right()
    {
        await using var d = await StartDevice(); // nobody has the PrivacyScreen right
        using var v = await Connect(d);
        Assert.False(v.Client.Hello.Rights.PrivacyScreen);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        SendPrivacyScreen(v, DesktopPrivacyStyle.Message);
        WaitFor(() => v.Privacy.Any(p => p.Reason.Contains("may not use the privacy screen")), "the refusal");
        Assert.Empty(d.Helpers.Uis.Single().Screens);
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopPrivacyRefused), "audit entry");
    }

    [Fact]
    public async Task The_privacy_screen_is_shown_on_the_device_and_reported()
    {
        if (!A.DesktopEndpoint.PrivacyScreenSupported) return; // this PC's Windows can't keep a window out of captures
        await using var d = await StartDevice(policy: PrivacyPolicy());
        using var v = await Connect(d);
        Assert.Contains(DesktopFeatures.PrivacyScreen, v.Client.Hello.Features!);
        Assert.True(v.Client.Hello.Rights.PrivacyScreen);
        Assert.False(v.Client.Hello.Rights.PrivacyInputBlock);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        var ui = d.Helpers.Uis.Single();

        SendPrivacyScreen(v, DesktopPrivacyStyle.Message);
        WaitFor(() => LastPrivacy(v)?.Screen == DesktopPrivacyStyle.Message, "the privacy screen reported");
        Assert.Equal((DesktopPrivacyStyle.Message, false), (ui.Screens.Single().Style, ui.Screens.Single().BlockLocalInput));
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopPrivacyScreen), "audit entry");

        // Blocking the device's input needs policy level 2: refused, nothing changes on the device.
        SendPrivacyScreen(v, DesktopPrivacyStyle.Black, block: true);
        WaitFor(() => v.Privacy.Any(p => p.Reason.Contains("isn't allowed")), "the refusal");
        Assert.Single(ui.Screens);

        // A device that can't show it: off, and RemoteHub is told why.
        ui.ScreenError = "can't keep it out of the picture";
        SendPrivacyScreen(v, DesktopPrivacyStyle.Black);
        WaitFor(() => v.Privacy.Any(p => p is { Screen: DesktopPrivacyStyle.Off, Reason: "can't keep it out of the picture" }), "the failure");

        ui.ScreenError = null;
        SendPrivacyScreen(v, DesktopPrivacyStyle.Off);
        WaitFor(() => ui.Screens.LastOrDefault()?.Style == DesktopPrivacyStyle.Off, "turned off on the device");
    }

    [Fact]
    public async Task Policy_level_2_lets_the_privacy_screen_block_the_devices_input()
    {
        if (!A.DesktopEndpoint.PrivacyScreenSupported) return;
        await using var d = await StartDevice(policy: PrivacyPolicy(A.PrivacyScreenLevel.WithInputBlock));
        using var v = await Connect(d);
        Assert.True(v.Client.Hello.Rights.PrivacyInputBlock);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);
        SendPrivacyScreen(v, DesktopPrivacyStyle.Black, block: true);
        WaitFor(() => LastPrivacy(v) is { Screen: DesktopPrivacyStyle.Black, LocalInputBlocked: true }, "the blocked input reported");
        Assert.True(d.Helpers.Uis.Single().Screens.Single().BlockLocalInput);
    }

    [Fact]
    public async Task What_is_hidden_stays_hidden_after_a_dropped_connection()
    {
        if (!A.DesktopEndpoint.PrivacyScreenSupported) return;
        await using var d = await StartDevice(policy: PrivacyPolicy());
        var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        var token = first.Next(DesktopStatusState.Connected).Resume!;
        SendPrivacyScreen(first, DesktopPrivacyStyle.Message);
        WaitFor(() => d.Helpers.Uis[0].Screens.Count == 1, "the privacy screen");
        WaitFor(() => d.Helpers.Uis[0].Hide is not null, "the banner");
        d.Helpers.Uis[0].Hide!(DesktopScreenHide.Blur);
        WaitFor(() => LastPrivacy(first)?.User == DesktopScreenHide.Blur, "the blurred screen");

        using var second = await Connect(d);
        second.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        second.Next(DesktopStatusState.Connected);
        first.Dispose();
        WaitFor(() => LastPrivacy(second) is { User: DesktopScreenHide.Blur, Screen: DesktopPrivacyStyle.Message }, "both again in the new connection");
        Assert.Equal(DesktopPrivacyStyle.Message, d.Helpers.Uis[1].Screens.Single().Style);
    }

    [Fact]
    public void The_privacy_screen_policy_is_read()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins] };
        var d = A.AgentPolicy.From(config, null);
        Assert.Equal(A.PrivacyScreenLevel.WithInputBlock, d.PrivacyScreen);
        Assert.Equal([Admins], d.Groups(A.AgentRight.PrivacyScreen));
        Assert.Equal(A.PrivacyScreenLevel.WithInputBlock, A.AgentPolicy.From(config, new Values(new() { ["PrivacyScreen"] = 2 })).PrivacyScreen);
        Assert.Equal(A.PrivacyScreenLevel.Off, A.AgentPolicy.From(config, new Values(new() { ["PrivacyScreen"] = 0 })).PrivacyScreen);
        Assert.Equal(A.PrivacyScreenLevel.WithInputBlock, A.AgentPolicy.From(config, new Values(new() { ["PrivacyScreen"] = 7 })).PrivacyScreen); // invalid: default
        Assert.Equal(A.PrivacyScreenLevel.Off, A.AgentPolicy.Refused("x").PrivacyScreen);
    }

    // ---- hidden apps

    private const string Secret = @"c:\apps\secret.exe", Notes = @"c:\apps\notes.exe";

    private static A.AppWindow Win(int id, string app, int l, int t, int r, int b, bool opaque = true, bool shell = false, bool unresolved = false) =>
        new(id, app, new A.ScreenRect(l, t, r, b), opaque, shell, unresolved);

    private static HashSet<string> Set(params string[] apps) => [.. apps];

    [Fact]
    public void A_hidden_window_is_covered_except_where_an_opaque_window_lies_over_it()
    {
        var apps = new Apps
        {
            Windows =
            [
                Win(1, Notes, 50, 50, 150, 150),                    // over the secret window: covers part of it
                Win(2, "", 0, 0, 40, 40, opaque: false),             // layered: doesn't cover anything
                Win(3, Secret, 0, 0, 100, 100),
                Win(4, Notes, 200, 200, 300, 300),                    // below, elsewhere
            ],
        };
        var mask = new A.AppMask(apps);
        var rects = mask.Compute(Set(Secret), 0, out _);
        bool Covered(int x, int y) => rects.Any(r => r.Contains(x, y));
        Assert.True(Covered(10, 10));
        Assert.True(Covered(-3, -3));          // the margin around it
        Assert.True(Covered(30, 90));
        Assert.False(Covered(60, 60));         // Notes lies over it there
        Assert.False(Covered(250, 250));
        Assert.Empty(mask.Compute(Set(), 0, out var hold));
        Assert.False(hold);
    }

    [Fact]
    public void Shell_previews_and_unresolved_store_frames_are_covered_while_apps_are_hidden()
    {
        var apps = new Apps { Windows = [Win(1, "", 0, 0, 50, 50, shell: true), Win(2, "", 100, 100, 150, 150, unresolved: true)] };
        var mask = new A.AppMask(apps);
        var rects = mask.Compute(Set(Secret), 0, out _);
        Assert.Contains(rects, r => r.Contains(25, 25));        // Alt+Tab, thumbnails: covered whenever anything is hidden
        Assert.DoesNotContain(rects, r => r.Contains(125, 125)); // a Store frame only matters when a Store app is hidden
        rects = mask.Compute(Set(A.AppIds.Packaged + "contoso.vault_123!app"), 0, out _);
        Assert.Contains(rects, r => r.Contains(125, 125));
    }

    [Fact]
    public void A_moving_hidden_window_holds_the_picture_and_stays_covered_where_it_was()
    {
        var apps = new Apps { Windows = [Win(1, Secret, 0, 0, 100, 100)] };
        var mask = new A.AppMask(apps);
        mask.Compute(Set(Secret), 0, out var hold);
        Assert.True(hold);                                        // just appeared
        mask.Compute(Set(Secret), A.AppMask.HoldMs + 1, out hold);
        Assert.False(hold);                                       // settled

        apps.Windows = [Win(1, Secret, 300, 0, 400, 100)];
        var rects = mask.Compute(Set(Secret), 1000, out hold);
        Assert.True(hold);                                        // moved: no frame for a moment
        Assert.Contains(rects, r => r.Contains(50, 50));          // the old place is still covered in this frame
        Assert.Contains(rects, r => r.Contains(350, 50));
        rects = mask.Compute(Set(Secret), 1000 + A.AppMask.HoldMs + 1, out hold);
        Assert.False(hold);
        Assert.DoesNotContain(rects, r => r.Contains(50, 50));

        apps.Windows = [];                                        // minimized: Windows animates it to the taskbar
        mask.Compute(Set(Secret), 2000, out hold);
        Assert.True(hold);
    }

    [Fact]
    public void Covering_paints_only_the_part_on_the_shown_monitor()
    {
        var frame = new A.FrameBuffer();
        frame.Ensure(100, 50);
        A.AppMask.Apply(frame, new A.DesktopMonitor(1, "", 1000, 0, 100, 50, false), [new A.ScreenRect(1090, 40, 1200, 80), new A.ScreenRect(0, 0, 500, 500)], 0xFFFFFFFF);
        int Pixel(int x, int y) => BitConverter.ToInt32(frame.Pixels, (y * 100 + x) * 4);
        Assert.Equal(-1, Pixel(95, 45));
        Assert.Equal(0, Pixel(89, 45));
        Assert.Equal(0, Pixel(95, 39));
        Assert.Equal(0, Pixel(5, 5)); // the other rectangle is on another monitor
    }

    [Fact]
    public void Rectangles_are_subtracted_in_pieces()
    {
        var left = A.ScreenRect.Subtract(new A.ScreenRect(0, 0, 100, 100), [new A.ScreenRect(25, 25, 75, 75)]);
        Assert.Equal(4, left.Count);
        Assert.Equal(100 * 100 - 50 * 50, left.Sum(r => r.Width * r.Height));
        Assert.Empty(A.ScreenRect.Subtract(new A.ScreenRect(10, 10, 20, 20), [new A.ScreenRect(0, 0, 50, 50)]));
    }

    [Fact]
    public async Task Apps_picked_in_the_question_are_hidden_from_the_first_frame_and_their_input_and_clipboard_too()
    {
        await using var d = await StartDevice(pickOnConsent: Set(Secret));
        d.Apps.Windows = [Win(1, Secret, 10, 10, 110, 110), Win(2, Notes, 150, 10, 300, 200)];
        using var v = await Connect(d);
        Assert.Contains(DesktopFeatures.HideApps, v.Client.Hello.Features!);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);

        // RemoteHub learns how many and where – never which.
        WaitFor(() => LastPrivacy(v) is { HiddenApps: 1, Regions.Length: > 0 }, "the hidden app reported");
        var region = LastPrivacy(v)!.Regions!.Single();
        Assert.True(region.X <= 6 && region.Y <= 6 && region.X + region.Width >= 114 && region.Y + region.Height >= 114);
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopUserHid), "audit entry");
        WaitFor(() => v.Frames.Count >= 1, "video frames");

        // No clicks into it; outside it they go through.
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 50, 50, 0)));
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 200, 50, 0)));
        WaitFor(() => d.Input.Mouse.Count == 1, "the click outside");
        Assert.Equal(200, d.Input.Mouse.Single().X);

        // No typing while it has the keyboard; the release of a key still goes through.
        d.Apps.Foreground = Secret;
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1E, RemoteKeyFlags.None)));
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1E, RemoteKeyFlags.Up)));
        WaitFor(() => d.Input.Keys.Count == 1, "the key's release");
        Assert.Equal((byte)RemoteKeyFlags.Up, (byte)d.Input.Keys.Single().Flags);
        d.Apps.Foreground = Notes;
        v.Client.Send(DesktopWire.Key(new RemoteKey(0x1F, RemoteKeyFlags.None)));
        WaitFor(() => d.Input.Keys.Count == 2, "typing into another app");

        // What is copied there stays on the device.
        var ui = d.Helpers.Uis.Single();
        d.Apps.ClipboardOwner = Secret;
        ui.RaiseClipboard("the password");
        d.Apps.ClipboardOwner = Notes;
        ui.RaiseClipboard("a note");
        WaitFor(() => v.Clipboard.Contains("a note"), "the other app's copy");
        Assert.DoesNotContain("the password", v.Clipboard);

        // Shown again: no region, nothing hidden.
        ui.Chosen!(Set());
        WaitFor(() => LastPrivacy(v) is { HiddenApps: 0, Regions: null }, "nothing hidden any more");
    }

    [Fact]
    public async Task Hidden_apps_stay_hidden_after_a_dropped_connection_and_the_policy_hides_its_own()
    {
        var policy = PrivacyPolicy();
        policy = new A.AgentPolicy { PrivacyScreen = policy.PrivacyScreen, Rights = policy.Rights, HiddenApps = [@"C:\Apps\Vault.exe"] };
        await using var d = await StartDevice(policy: policy);
        using var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        var token = first.Next(DesktopStatusState.Connected).Resume!;
        // The helper's window gets the apps to offer from its own thread – it may be a moment after "Connected".
        var ui = OfferedOn(d, 0);
        Assert.Equal([@"c:\apps\vault.exe"], ui.OfferedAlways!);
        // The policy's app isn't the user's: counted apart, not on the banner and not audited as the user hiding it.
        WaitFor(() => !first.Privacy.IsEmpty, "the first privacy report");
        Assert.Equal((0, 1), (first.Privacy.First().HiddenApps, first.Privacy.First().PolicyHiddenApps));
        Assert.Equal(0, ui.Shown?.HiddenApps ?? 0);
        Assert.DoesNotContain(A.AuditEvent.DesktopUserHid, d.Audits);
        ui.Chosen!(Set(Secret));
        WaitFor(() => LastPrivacy(first) is { HiddenApps: 1, PolicyHiddenApps: 1 }, "the user's app counted as theirs");
        WaitFor(() => d.Audits.Contains(A.AuditEvent.DesktopUserHid), "audit entry for the user's app");
        Assert.Equal(1, ui.Shown!.HiddenApps);

        using var second = await Connect(d);
        second.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        second.Next(DesktopStatusState.Connected);
        Assert.Equal([Secret], OfferedOn(d, 1).OfferedHidden!);
        WaitFor(() => LastPrivacy(second) is { HiddenApps: 1, PolicyHiddenApps: 1 }, "both still hidden in the new connection, still apart");
    }

    [Fact]
    public async Task Apps_only_the_policy_hides_are_masked_but_never_reported_or_audited_as_the_users()
    {
        var policy = PrivacyPolicy();
        policy = new A.AgentPolicy { PrivacyScreen = policy.PrivacyScreen, Rights = policy.Rights, HiddenApps = [Secret] };
        await using var d = await StartDevice(policy: policy);
        d.Apps.Windows = [Win(1, Secret, 10, 10, 110, 110), Win(2, Notes, 150, 10, 300, 200)];
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.Connected);

        // Still covered in the picture, and clicks into it are dropped.
        WaitFor(() => LastPrivacy(v) is { Regions.Length: > 0 }, "the policy's app covered");
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 50, 50, 0)));
        v.Client.Send(DesktopWire.Mouse(new RemoteMouse(RemoteMouseAction.Down, RemoteMouseButton.Left, 200, 50, 0)));
        WaitFor(() => d.Input.Mouse.Count == 1, "the click outside");
        Assert.Equal(200, d.Input.Mouse.Single().X);

        // But none of it is the user's: every report says so, the banner has no "app hidden" and there is no 1105.
        Assert.All(v.Privacy, p => Assert.Equal((0, 1), (p.HiddenApps, p.PolicyHiddenApps)));
        Assert.Equal(0, d.Helpers.Uis.Single().Shown?.HiddenApps ?? 0);
        Assert.DoesNotContain(A.AuditEvent.DesktopUserHid, d.Audits);
    }

    /// <summary>The helper window number <paramref name="index"/>, once it has been offered the apps to hide.</summary>
    private static Ui OfferedOn(Device d, int index)
    {
        Ui? ui = null;
        WaitFor(() =>
        {
            lock (d.Helpers.Uis) ui = d.Helpers.Uis.Count > index ? d.Helpers.Uis[index] : null;
            return ui?.OfferedHidden is not null;
        }, $"the apps to hide offered in helper window {index + 1}");
        return ui!;
    }

    [Fact]
    public void The_hidden_apps_policy_is_read()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins] };
        Assert.Empty(A.AgentPolicy.From(config, null).HiddenApps);
        var p = A.AgentPolicy.From(config, new Values(new() { ["HiddenApps"] = new[] { @"C:\Apps\Vault.exe", " ", "aumid:Contoso.Vault_123!App" } }));
        Assert.Equal([@"c:\apps\vault.exe", "aumid:contoso.vault_123!app"], p.HiddenApps);
    }

    [Fact]
    public async Task Ctrl_alt_del_on_the_console_is_sent_by_the_service()
    {
        var sent = 0;
        await using var d = await StartDevice(allow: true, secureAttention: () => { Interlocked.Increment(ref sent); return null; });
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
        v.Client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
        WaitFor(() => sent == 1, "Ctrl+Alt+Del sent by the service");
        Assert.Equal(0, d.Input.SecureAttentions);
    }

    [Fact]
    public async Task Ctrl_alt_del_in_an_rdp_session_is_sent_by_the_helper_there()
    {
        await using var d = await StartDevice(allow: true);
        d.Sessions.Console = 7; // session 1 isn't the console: an RDP session
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        Assert.Equal(DesktopStatusState.Connected, v.Next(DesktopStatusState.Connected).State);
        WaitFor(() => v.Frames.Count >= 1, "video frames");

        v.Client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
        WaitFor(() => d.Input.SecureAttentions == 1, "Ctrl+Alt+Del sent by the helper");

        // Couldn't be sent there (e.g. a policy): RemoteHub is told why.
        d.Input.SecureAttentionError = "Ctrl+Alt+Del from software is turned off by a policy on this device.";
        v.Client.Send(DesktopWire.Empty(DesktopMessage.SecureAttention));
        Assert.Contains("turned off by a policy", v.Next(DesktopStatusState.Connected).Message);
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
    public async Task Your_own_session_may_be_connected_without_asking()
    {
        await using var d = await StartDevice();
        d.Sessions.Current = [ActiveUser with { User = @"TEST\viewer" }];
        using var v = await Connect(d, "viewer"); // no ConsentBypass right: None isn't allowed for anyone else's session
        v.Start(ActiveUser.Id, DesktopConsent.None, viewOnly: true);
        v.Next(DesktopStatusState.Connected);
        Assert.Equal(0, d.Helpers.Uis[0].Asked);
    }

    [Fact]
    public async Task Your_own_session_asks_when_you_choose_to()
    {
        await using var d = await StartDevice();
        d.Sessions.Current = [ActiveUser with { User = @"TEST\admin" }];
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.Ask);
        v.Next(DesktopStatusState.WaitingForUser);
        v.Next(DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[0].Asked);
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
    public async Task A_token_presented_right_after_the_user_disconnected_asks_again()
    {
        await using var d = await StartDevice();
        using var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        var token = first.Next(DesktopStatusState.Connected).Resume!;
        WaitFor(() => d.Helpers.Uis.Count == 1 && d.Helpers.Uis[0].Disconnect is not null, "banner");
        d.Helpers.Uis[0].Disconnect!();
        first.Next(DesktopStatusState.Ended);

        // At once, while the first connection is still in its close handshake (D-BAN-05): the user's end stands.
        using var second = await Connect(d);
        second.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        Assert.Equal(DesktopStatusState.WaitingForUser, second.Next(DesktopStatusState.WaitingForUser).State);
        second.Next(DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[1].Asked);
        Assert.DoesNotContain(A.AuditEvent.DesktopResumed, d.Audits);
    }

    [Fact]
    public async Task A_token_presented_right_after_an_idle_end_asks_again()
    {
        var policy = new A.AgentPolicy
        {
            IdleTimeout = TimeSpan.FromSeconds(2),
            Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>> { [A.AgentRight.Desktop] = [Admins] },
        };
        await using var d = await StartDevice(policy: policy);
        using var first = await Connect(d);
        first.Start(ActiveUser.Id, DesktopConsent.Ask);
        var token = first.Next(DesktopStatusState.Connected).Resume!;
        first.Next(DesktopStatusState.Ended);

        using var second = await Connect(d);
        second.Start(ActiveUser.Id, DesktopConsent.Ask, resume: token);
        second.Next(DesktopStatusState.WaitingForUser);
        Assert.Equal(1, WaitForAsked(d, 1));
        Assert.DoesNotContain(A.AuditEvent.DesktopResumed, d.Audits);
    }

    private static int WaitForAsked(Device d, int ui)
    {
        WaitFor(() => d.Helpers.Uis.Count > ui && d.Helpers.Uis[ui].Asked > 0, "the user being asked");
        return d.Helpers.Uis[ui].Asked;
    }

    [Fact]
    public async Task A_revoked_token_cannot_be_taken()
    {
        using var owner = new CancellationTokenSource();
        var entry = A.DesktopResume.Issue(@"TEST\admin", @"PC\alice", owner);
        entry.Revoke();
        Assert.Null(await A.DesktopResume.TakeAsync(entry.Token, @"TEST\admin"));
        Assert.False(owner.IsCancellationRequested); // the ended connection isn't touched
    }

    [Fact]
    public async Task An_end_on_purpose_during_a_takeover_wins_over_the_token()
    {
        using var owner = new CancellationTokenSource();
        var entry = A.DesktopResume.Issue(@"TEST\admin", @"PC\alice", owner);
        // The old connection is ending on purpose just as the takeover cancels it: it revokes instead of releasing.
        owner.Token.Register(entry.Revoke);
        Assert.Null(await A.DesktopResume.TakeAsync(entry.Token, @"TEST\admin"));
        Assert.True(entry.Revoked);
        Assert.False(entry.TakenOver); // the old connection writes its end entry and runs the end action
    }

    [Fact]
    public async Task A_dropped_connection_keeps_its_token_for_the_window()
    {
        using var owner = new CancellationTokenSource();
        var entry = A.DesktopResume.Issue(@"TEST\admin", @"PC\alice", owner);
        entry.Release();
        Assert.Null(await A.DesktopResume.TakeAsync(entry.Token, @"TEST\admin2"));
        Assert.Same(entry, await A.DesktopResume.TakeAsync(entry.Token, @"TEST\admin"));
        Assert.Null(await A.DesktopResume.TakeAsync(entry.Token, @"TEST\admin")); // single use
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
        foreach (var chunk in ClipboardTransfer.Chunks(image)) { while (v.Client.Queued > 8) await Task.Delay(10); v.Client.Send(chunk); } // throttled like RemoteHub
        WaitFor(() => d.Helpers.Uis[0].Image is { } i && i.SequenceEqual(png), "image on the device's clipboard");
        WaitFor(() => v.TransferEnds.Any(e => e is { Id: 1, Ok: true, ToDevice: true }), "receipt for the image");
        WaitFor(() => v.Client.Queued == 0, "send queue empty");

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
            WaitFor(() => v.TransferEnds.Any(e => e is { Id: 2, Ok: true, ToDevice: true }), "receipt for the files");
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
    public async Task Files_copied_in_RemoteHub_are_only_sent_when_pasted_on_the_device()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        Assert.Contains(DesktopFeatures.TransferOnPaste, v.Client.Hello.Features!);
        v.Start(ActiveUser.Id, DesktopConsent.None);
        v.Next(DesktopStatusState.Connected);
        var requests = new System.Collections.Concurrent.ConcurrentQueue<int>();
        v.Client.Received += m => { if (DesktopWire.TypeOf(m) == DesktopMessage.TransferRequest) requests.Enqueue(int.Parse(DesktopWire.ReadText(m))); };

        var source = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "b.txt"), "pasted");
        try
        {
            // Copied: only names and sizes go; the device offers them – no data, no request yet.
            var files = ClipboardTransfer.Files(7, [Path.Combine(source, "b.txt")], out _)!;
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferOffer, files.Transfer, DesktopJson.Default.DesktopTransfer));
            WaitFor(() => d.Helpers.Uis[0].Offered is not null, "offer on the device's clipboard");
            Assert.False(d.Helpers.Uis[0].OfferedImage);
            await Task.Delay(300);
            Assert.Empty(requests);
            Assert.Null(d.Helpers.Uis[0].Files);

            // RDP's clipboard sync and Windows' clipboard history read every new entry: they get nothing, nothing is asked for.
            Assert.Null(await Task.Run(() => d.Helpers.Uis[0].Offered!("rdpclip (812)")).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(await Task.Run(() => d.Helpers.Uis[0].Offered!("svchost (1312)")).WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.Delay(300);
            Assert.Empty(requests);

            // Pasted: the device asks, RemoteHub sends, the waiting paste gets the files (not the clipboard directly).
            var paste = d.Helpers.Uis[0].Paste();
            WaitFor(() => requests.Contains(7), "request from the paste");
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferStart, files.Transfer, DesktopJson.Default.DesktopTransfer));
            foreach (var chunk in ClipboardTransfer.Chunks(files)) v.Client.Send(chunk);
            var pasted = await paste.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("pasted", File.ReadAllText(pasted!.Files![0]));
            Assert.Null(d.Helpers.Uis[0].Files);
            WaitFor(() => v.TransferEnds.Any(e => e is { Id: 7, Ok: true, ToDevice: true }), "receipt");
            Directory.Delete(Path.GetDirectoryName(pasted.Files[0])!, true);

            // A paste whose copy is cancelled in RemoteHub gets nothing.
            var again = ClipboardTransfer.Files(8, [Path.Combine(source, "b.txt")], out _)!;
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferOffer, again.Transfer, DesktopJson.Default.DesktopTransfer));
            WaitFor(() => Volatile.Read(ref d.Helpers.Uis[0].Offers) == 2, "second offer");
            var cancelled = d.Helpers.Uis[0].Paste();
            WaitFor(() => requests.Contains(8), "request for the second offer");
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferStart, again.Transfer, DesktopJson.Default.DesktopTransfer));
            v.Client.Send(DesktopWire.Json(DesktopMessage.TransferEnd, new DesktopTransferEnd(8, false, "replaced"), DesktopJson.Default.DesktopTransferEnd));
            Assert.Null(await cancelled.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally { Directory.Delete(source, true); }
    }

    [Fact]
    public async Task Files_copied_on_the_device_are_only_sent_when_pasted_in_RemoteHub()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        var offers = new System.Collections.Concurrent.ConcurrentQueue<DesktopTransfer>();
        v.Client.Received += m => { if (DesktopWire.TypeOf(m) == DesktopMessage.TransferOffer) offers.Enqueue(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopTransfer)); };
        v.Start(ActiveUser.Id, DesktopConsent.None, features: [DesktopFeatures.TransferOnPaste]);
        v.Next(DesktopStatusState.Connected);
        var source = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(source, "from the device");
        try
        {
            // Copied on the device: RemoteHub gets the offer (name, size) – no transfer, no data.
            d.Helpers.Uis[0].RaiseContent(new A.HelperClipboard(Files: [source]));
            WaitFor(() => offers.Count == 1, "offer from the device");
            await Task.Delay(300);
            Assert.Empty(v.Transfers);
            lock (v.TransferBytes) Assert.Equal(0, v.TransferBytes.Length);

            // Pasted in RemoteHub: it asks, the device sends.
            v.Client.Send(DesktopWire.Text(DesktopMessage.TransferRequest, offers.Single().Id.ToString()));
            WaitFor(() => v.Transfers.Any(t => t.Id == offers.Single().Id), "transfer after the request");
            WaitFor(() => { lock (v.TransferBytes) return v.TransferBytes.Length == "from the device".Length; }, "file data");
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task An_older_RemoteHub_still_gets_copies_from_the_device_right_away()
    {
        await using var d = await StartDevice();
        using var v = await Connect(d);
        v.Start(ActiveUser.Id, DesktopConsent.None); // no features: an older RemoteHub
        v.Next(DesktopStatusState.Connected);
        var source = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(source, "abc");
        try
        {
            d.Helpers.Uis[0].RaiseContent(new A.HelperClipboard(Files: [source]));
            WaitFor(() => { lock (v.TransferBytes) return v.TransferBytes.Length == 3; }, "file data without a request");
        }
        finally { File.Delete(source); }
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
    public void A_receiver_checks_the_limits_itself_and_removes_unfinished_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Limits, whatever the sender claims: too many items, sizes that don't add up.
            var many = Enumerable.Range(0, DesktopProtocol.MaxTransferItems + 1).Select(i => new DesktopTransferItem($"f{i}.txt", 0)).ToArray();
            Assert.Throws<InvalidDataException>(() => new TransferReceiver(new DesktopTransfer(1, DesktopTransferKind.Files, many, 0), folder));
            Assert.Throws<InvalidDataException>(() => new TransferReceiver(
                new DesktopTransfer(2, DesktopTransferKind.Files, [new DesktopTransferItem("a.txt", 5)], 1), folder));

            // A transfer that stops halfway leaves nothing behind.
            var r = new TransferReceiver(new DesktopTransfer(3, DesktopTransferKind.Files,
                [new DesktopTransferItem("a.txt", 4), new DesktopTransferItem("b.txt", 4)], 8), folder);
            r.Add(0, [1, 2, 3, 4, 5]);
            Assert.True(File.Exists(Path.Combine(folder, "a.txt")));
            r.Dispose();
            Assert.False(Directory.Exists(folder));

            // A finished one stays.
            using (var done = new TransferReceiver(new DesktopTransfer(4, DesktopTransferKind.Files, [new DesktopTransferItem("c.txt", 2)], 2), folder))
            {
                done.Add(0, [1, 2]);
                Assert.True(done.Complete);
            }
            Assert.True(File.Exists(Path.Combine(folder, "c.txt")));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void Transfer_paths_cannot_leave_their_folder()
    {
        Assert.Null(DesktopWire.SafeRelativePath(".."));
        Assert.Null(DesktopWire.SafeRelativePath("a/../.."));
        Assert.Equal(Path.Combine("C", "Windows", "x.txt"), DesktopWire.SafeRelativePath(@"C:\Windows\x.txt"));
        Assert.Null(DesktopWire.SafeRelativePath(@"logs\CON"));
        Assert.Null(DesktopWire.SafeRelativePath("nul.txt"));
        Assert.Null(DesktopWire.SafeRelativePath("COM1"));
        Assert.Equal("console.txt", DesktopWire.SafeRelativePath("console.txt"));
        // Downloads: every path from the device must stay below the chosen folder.
        var below = Path.Combine(Path.GetTempPath(), "rh-target");
        Assert.Equal(Path.Combine(below, "a", "b.txt"), DesktopWire.PathBelow(below, @"a\b.txt"));
        Assert.Null(DesktopWire.PathBelow(below, @"..\..\Startup\evil.cmd"));
        Assert.Null(DesktopWire.PathBelow(below, @"a\..\..\evil.cmd"));
        Assert.Equal(Path.Combine(below, "Windows", "evil.cmd"), DesktopWire.PathBelow(below, @"\Windows\evil.cmd"));
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

    [Fact]
    public async Task A_resolution_larger_than_the_window_is_applied_within_limits_and_restored_on_disconnect()
    {
        await using var d = await StartDevice();
        var v = await Connect(d);
        int restores;
        try
        {
            v.Start(ActiveUser.Id, DesktopConsent.None);
            v.Next(DesktopStatusState.Connected);
            v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(0, 30, 60, 3840, 2160), DesktopJson.Default.DesktopSettings));
            WaitFor(() => d.Helpers.Screens[0].Fits.Contains((3840, 2160)), "the chosen resolution");
            // From the wire, untrusted: far too large is limited to what the virtual display takes.
            v.Client.Send(DesktopWire.Json(DesktopMessage.Settings, new DesktopSettings(0, 30, 60, int.MaxValue, 99999), DesktopJson.Default.DesktopSettings));
            WaitFor(() => d.Helpers.Screens[0].Fits.Contains((DesktopProtocol.MaxFitWidth, DesktopProtocol.MaxFitHeight)), "the largest resolution");
            Assert.DoesNotContain(d.Helpers.Screens[0].Fits, f => f.Item1 > DesktopProtocol.MaxFitWidth || f.Item2 > DesktopProtocol.MaxFitHeight || f.Item1 <= 0 || f.Item2 <= 0);
            restores = d.Helpers.Screens[0].Restores; // a size is set: nothing restores it until the end
        }
        finally { v.Dispose(); }
        WaitFor(() => d.Helpers.Screens[0].Restores > restores, "the device's own resolution back after disconnecting");
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

    // ---- automatic sign-in (docs/credential-provider-design.md §6)

    /// <summary>Stands in for the credential provider broker; <see cref="Answer"/> plays Windows (and may sign someone in).</summary>
    private sealed class FakeAutoSignIn : A.IAutoSignIn
    {
        public volatile string? Why;
        public volatile string? Password;
        public volatile A.AutoSignInRequest? Request;
        public int Calls;
        public Func<A.AutoSignInRequest, A.AutoSignInResult>? Answer;
        public string? Unavailable() => Why;
        public Task<A.AutoSignInResult> SignInAsync(A.AutoSignInRequest r, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Request = r;
            r.Password.Reveal(p => Password = System.Text.Encoding.Unicode.GetString(p));
            return Task.FromResult(r.Recheck() is { } changed ? A.AutoSignInResult.Failed(A.AutoSignInFailure.SessionChanged, changed)
                : Answer?.Invoke(r) ?? new A.AutoSignInResult(true, "", "Signed in."));
        }
    }

    private static readonly A.DesktopSessionInfo ConsoleSignIn = new(2, "", A.DesktopSessionState.SignIn, true, "Console");

    private static A.AgentPolicy AutoSignInPolicy(bool right = true, string behavior = A.NoUserBehaviors.AutoSignIn) => new()
    {
        NoUserBehavior = behavior,
        Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>>
        {
            [A.AgentRight.PowerShell] = [Admins],
            [A.AgentRight.Desktop] = [Admins],
            [A.AgentRight.DesktopViewOnly] = [Users],
            [A.AgentRight.SecureDesktop] = [Admins],
            [A.AgentRight.AutoSignIn] = right ? [Admins] : [],
        },
    };

    /// <summary>Windows signs the asked account in: the console moves to a new session of that user.</summary>
    private static Func<A.AutoSignInRequest, A.AutoSignInResult> SignsIn(Sessions sessions, string sid, string user = @"TEST\admin") => r =>
    {
        sessions.Sids[3] = sid;
        sessions.Current = [new A.DesktopSessionInfo(3, user, A.DesktopSessionState.Active, true, "Console")];
        sessions.Console = 3;
        return new A.AutoSignInResult(true, "", "Signed in.");
    };

    [Fact]
    public async Task Automatic_sign_in_is_offered_only_with_policy_right_and_a_working_provider()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        async Task<bool> Offered(A.AgentPolicy policy, A.IAutoSignIn? broker)
        {
            await using var d = await StartDevice(policy: policy, autoSignIn: broker, sessionList: [ConsoleSignIn]);
            using var v = await Connect(d);
            Assert.Equal(v.Client.Hello.Rights.AutoSignIn, v.Client.Hello.Features?.Contains(DesktopFeatures.AutoSignIn) == true);
            return v.Client.Hello.Rights.AutoSignIn;
        }
        Assert.True(await Offered(AutoSignInPolicy(), new FakeAutoSignIn()));
        Assert.False(await Offered(AutoSignInPolicy(), null));                                         // no provider on the device
        Assert.False(await Offered(AutoSignInPolicy(), new FakeAutoSignIn { Why = "not signed" }));   // provider not usable
        Assert.False(await Offered(AutoSignInPolicy(right: false), new FakeAutoSignIn()));             // nobody has the right (default)
        Assert.False(await Offered(AutoSignInPolicy(behavior: A.NoUserBehaviors.SignInScreen), new FakeAutoSignIn())); // policy off
    }

    [Fact]
    public async Task Automatic_sign_in_sends_the_password_when_asked_and_continues_in_the_new_session_without_asking()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn();
        await using var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn], secureScreen: true);
        broker.Answer = SignsIn(d.Sessions, SidOf("admin"));
        using var v = await Connect(d);

        v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
        Assert.Equal(DesktopStatusState.SignInReady, v.Next(DesktopStatusState.SignInReady).State);
        var request = DesktopWire.SignInRequest("pw");
        v.Client.Send(request);
        Assert.Equal(DesktopStatusState.SignedIn, v.Next(DesktopStatusState.SignedIn).State);

        Assert.Equal("pw", broker.Password);
        Assert.Equal(("TEST", "admin", SidOf("admin"), ConsoleSignIn.Id), (broker.Request!.Domain, broker.Request.User, broker.Request.Sid, broker.Request.Session));
        Assert.Contains(A.AuditEvent.AutoSignInSucceeded, d.Audits);
        // The picture follows into the new session – the operator's own account: nobody is asked.
        WaitFor(() => d.Helpers.SessionIds.Contains(3), "a helper in the new session");
        Assert.All(d.Helpers.Uis, u => Assert.Equal(0, u.Asked));
        Assert.Empty(d.Sessions.LogOffs);
    }

    [Fact]
    public async Task Automatic_sign_in_is_refused_in_view_only_and_when_the_account_has_a_session()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn();
        var rdp = new A.DesktopSessionInfo(5, @"TEST\admin", A.DesktopSessionState.Disconnected, false, "RDP-Tcp#1");
        await using (var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn]))
        using (var v = await Connect(d))
        {
            v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, viewOnly: true, autoSignIn: true);
            Assert.Contains("view-only", v.Next(DesktopStatusState.SignInFailed).Message);
        }
        await using (var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn, rdp]))
        using (var v = await Connect(d))
        {
            d.Sessions.Sids[5] = SidOf("admin");
            v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
            Assert.Contains("already has a session", v.Next(DesktopStatusState.SignInFailed).Message);
            Assert.Contains(A.AuditEvent.AutoSignInRefused, d.Audits);
        }
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task A_sign_in_request_nobody_asked_for_ends_the_connection()
    {
        await using var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: new FakeAutoSignIn(), sessionList: [ConsoleSignIn]);
        using var v = await Connect(d);
        var closed = new TaskCompletionSource();
        v.Client.Closed += _ => closed.TrySetResult();
        v.Start(ConsoleSignIn.Id, DesktopConsent.Ask); // no automatic sign-in asked for
        v.Next(DesktopStatusState.Connected);
        v.Client.Send(DesktopWire.SignInRequest("pw"));
        Assert.True(await Task.WhenAny(closed.Task, Task.Delay(15000)) == closed.Task, "the connection should end");
    }

    [Fact]
    public async Task Another_account_on_the_console_after_the_sign_in_is_signed_out_at_once()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn();
        await using var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn], secureScreen: true);
        broker.Answer = SignsIn(d.Sessions, "S-1-5-21-9-9-9-500", @"TEST\someone");
        using var v = await Connect(d);
        v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
        v.Next(DesktopStatusState.SignInReady);
        v.Client.Send(DesktopWire.SignInRequest("pw"));
        Assert.Equal(DesktopStatusState.Ended, v.Next(DesktopStatusState.Ended).State);
        WaitFor(() => d.Sessions.LogOffs.Contains(3), "the wrong session signed out");
        Assert.Contains(A.AuditEvent.AutoSignInMismatch, d.Audits);
    }

    [Fact]
    public async Task Windows_refusing_the_sign_in_is_reported_and_the_sign_in_screen_stays()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn { Answer = _ => new A.AutoSignInResult(false, A.AutoSignInFailure.Windows, A.AutoSignInStatus.Describe(unchecked((int)0xC000006D), 0), unchecked((int)0xC000006D)) };
        await using var d = await StartDevice(policy: AutoSignInPolicy(), autoSignIn: broker, sessionList: [ConsoleSignIn]);
        using var v = await Connect(d);
        v.Start(ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
        v.Next(DesktopStatusState.SignInReady);
        v.Client.Send(DesktopWire.SignInRequest("old password"));
        Assert.Contains("password was wrong", v.Next(DesktopStatusState.SignInFailed).Message);
        Assert.Contains(A.AuditEvent.AutoSignInFailed, d.Audits);
        Assert.Equal(1, d.Helpers.Started); // still on the sign-in screen, same helper
    }

    // ---- phase 4: after the sign-in (design §9)

    private static A.AgentPolicy WithEndAction(A.AgentPolicy p, string? endAction) => new()
    {
        NoUserBehavior = p.NoUserBehavior, Rights = p.Rights, AutoSignInEndAction = endAction, PrivacyScreen = A.PrivacyScreenLevel.WithInputBlock,
    };

    private static async Task<(Device D, Viewer V, FakeAutoSignIn Broker)> SignedIn(A.AgentPolicy policy, string? endAction = null)
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var broker = new FakeAutoSignIn();
        var d = await StartDevice(policy: policy, autoSignIn: broker, sessionList: [ConsoleSignIn], secureScreen: true);
        broker.Answer = SignsIn(d.Sessions, SidOf("admin"));
        var v = await Connect(d);
        v.Client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(ConsoleSignIn.Id, DesktopConsent.Ask, false, 30, 60, 0, null, null, true, endAction),
            DesktopJson.Default.DesktopStart));
        v.Next(DesktopStatusState.SignInReady);
        v.Client.Send(DesktopWire.SignInRequest("pw"));
        return (d, v, broker);
    }

    [Fact]
    public async Task The_new_session_starts_behind_the_privacy_screen_with_local_input_blocked()
    {
        if (!A.DesktopEndpoint.PrivacyScreenSupported) return; // Windows 10 2004 and later
        var (d, v, _) = await SignedIn(AutoSignInPolicy());
        await using var _d = d;
        using var _v = v;
        var signed = v.Next(DesktopStatusState.SignedIn);
        Assert.Equal(A.AutoSignInEndActions.Lock, signed.EndAction); // nothing chosen: lock
        WaitFor(() => d.Helpers.SessionIds.Contains(3), "a helper in the new session");
        var helperInNewSession = d.Helpers.SessionIds.IndexOf(3);
        WaitFor(() => d.Helpers.Uis.Count > helperInNewSession && !d.Helpers.Uis[helperInNewSession].Screens.IsEmpty, "the privacy screen in the new session");
        Assert.True(d.Helpers.Uis[helperInNewSession].Screens.TryPeek(out var screen));
        Assert.Equal((A.DesktopPrivacyStyle.Message, true), (screen!.Style, screen.BlockLocalInput));
        Assert.True(d.Helpers.Uis.Take(helperInNewSession).All(u => u.Screens.IsEmpty)); // the sign-in screen itself isn't covered
        // The admin's own session: no banner ("Hide screen", "Disconnect") for whoever stands at the device.
        var ui = d.Helpers.Uis[helperInNewSession];
        Assert.NotNull(ui.SignedInRemotely);
        Assert.False(ui.Banner);
    }

    [Theory]
    [InlineData(0x2C, false, false, false, true)]  // Print Screen
    [InlineData(0x2C, false, true, false, true)]   // with a modifier, too
    [InlineData(0x53, true, true, false, true)]    // Win+Shift+S
    [InlineData(0x53, true, false, false, false)]  // Win+S: search
    [InlineData(0x53, false, true, false, false)]  // Shift+S: a capital S
    [InlineData(0x09, false, false, true, true)]   // Alt+Tab
    [InlineData(0x09, true, false, false, true)]   // Win+Tab: Task view
    [InlineData(0x09, false, true, false, false)]  // Shift+Tab
    [InlineData(0x09, false, false, false, false)] // Tab
    public void The_privacy_screen_drops_the_snip_and_switcher_keys(int vk, bool win, bool shift, bool alt, bool dropped) =>
        Assert.Equal(dropped, A.SnipGuard.IsSnipKey(vk, win, shift, alt));

    [Theory]
    [InlineData(@"C:\Windows\SystemApps\MicrosoftWindows.Client.Core_cw5n1h2txyewy\ScreenClippingHost.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2409.25.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe", true)]
    [InlineData(@"C:\Windows\System32\SnippingTool.exe", true)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    public void The_privacy_screen_knows_the_snipping_tools(string path, bool snip) => Assert.Equal(snip, A.SnipGuard.IsSnipProgram(path));

    [Theory]
    [InlineData(null, null, false)]                                        // nothing chosen: lock
    [InlineData(null, A.AutoSignInEndActions.SignOut, true)]               // RemoteHub's setting
    [InlineData(A.AutoSignInEndActions.Lock, A.AutoSignInEndActions.SignOut, false)] // the device's policy wins
    public async Task Ending_the_connection_on_purpose_locks_or_signs_out_the_session(string? policyAction, string? connectionAction, bool signsOut)
    {
        var (d, v, _) = await SignedIn(WithEndAction(AutoSignInPolicy(), policyAction), connectionAction);
        await using var _d = d;
        v.Next(DesktopStatusState.SignedIn);
        WaitFor(() => d.Helpers.SessionIds.Contains(3), "a helper in the new session");
        v.Dispose(); // RemoteHub closes the tab
        WaitFor(() => (signsOut ? d.Sessions.LogOffs : d.Sessions.Disconnects).Contains(3), signsOut ? "sign-out" : "lock");
        Assert.Empty(signsOut ? d.Sessions.Disconnects : d.Sessions.LogOffs);
        WaitFor(() => d.Audits.Contains(A.AuditEvent.AutoSignInEnded), "audit 1107");
    }

    /// <summary>A helper whose output the test controls (it plays the helper left covering the screen after a drop).</summary>
    private sealed class CoverHelper : A.IDesktopHelper
    {
        private readonly AnonymousPipeServerStream _out = new(PipeDirection.Out);
        public readonly AnonymousPipeClientStream Reader;
        public volatile bool Disposed;
        public CoverHelper() => Reader = new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle);
        public Stream Input => Stream.Null;
        public Stream Output => Reader;
        public int ProcessId => 0;
        public void End(bool byUser) => A.DesktopWire.WriteFramedAsync(_out, A.DesktopWire.Json((A.DesktopMessage)A.HelperMessage.End, new A.HelperEnd("x", byUser), A.HelperJson.Default.HelperEnd), CancellationToken.None).Wait();
        public void Dispose() { Disposed = true; _out.Dispose(); }
    }

    private static (Sessions S, A.AutoSignInSessions Auto, A.AgentLog Log) AutoSignedSession(string state = A.DesktopSessionState.Active)
    {
        var s = new Sessions(new A.DesktopSessionInfo(3, @"TEST\admin", state, true, "Console"));
        s.Sids[3] = SidOf("admin");
        var auto = new A.AutoSignInSessions(s);
        auto.Signed(3, SidOf("admin"), @"TEST\admin", A.AutoSignInEndActions.Lock);
        return (s, auto, new A.AgentLog("", 1));
    }

    [Fact]
    public void A_dropped_connection_keeps_the_screen_covered_then_signs_out_even_with_lock_chosen()
    {
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMilliseconds(400);
        try
        {
            var (s, auto, log) = AutoSignedSession();
            var helper = new CoverHelper();
            auto.Dropped(3, helper, log);
            Thread.Sleep(150);
            Assert.Empty(s.LogOffs);       // still covered, waiting for a resume
            Assert.False(helper.Disposed);
            WaitFor(() => s.LogOffs.Contains(3), "sign-out after the hold");
            WaitFor(() => helper.Disposed, "the cover ends once the session is gone");
            Assert.Empty(s.Disconnects);   // not the end action: a drop always signs out
            Assert.Null(auto.Owned(3));
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); }
    }

    [Fact]
    public void A_refused_sign_out_locks_the_session_before_the_cover_ends()
    {
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMilliseconds(200);
        A.AutoSignInSessions.SignOutWait = TimeSpan.FromSeconds(30);
        try
        {
            var (s, auto, log) = AutoSignedSession();
            var helper = new CoverHelper();
            s.RefuseLogOff = true;
            auto.Dropped(3, helper, log);
            WaitFor(() => s.Disconnects.Contains(3), "locked when the sign-out is refused");
            WaitFor(() => helper.Disposed, "the cover ends once the session is locked");
            Assert.Null(auto.Owned(3));
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); A.AutoSignInSessions.SignOutWait = TimeSpan.FromSeconds(60); }
    }

    [Fact]
    public void Neither_signed_out_nor_locked_the_cover_and_the_record_stay()
    {
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMilliseconds(200);
        try
        {
            var (s, auto, log) = AutoSignedSession();
            var helper = new CoverHelper();
            s.RefuseLogOff = s.RefuseDisconnect = true;
            auto.Dropped(3, helper, log);
            Thread.Sleep(1000);
            Assert.False(helper.Disposed);      // still covering
            Assert.NotNull(auto.Owned(3));      // kept for the next start
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); }
    }

    [Fact]
    public void An_end_action_Windows_refused_stays_recorded_for_the_next_start()
    {
        var file = Path.Combine(Path.GetTempPath(), $"rh-autosignin-{Guid.NewGuid():N}.json");
        try
        {
            var s = new Sessions(new A.DesktopSessionInfo(3, @"TEST\admin", A.DesktopSessionState.Active, true, "Console"));
            s.Sids[3] = SidOf("admin");
            var auto = new A.AutoSignInSessions(s, file);
            auto.Signed(3, SidOf("admin"), @"TEST\admin", A.AutoSignInEndActions.Lock);
            s.RefuseDisconnect = true;
            Assert.Equal("couldn't be locked", auto.EndOnPurpose(3, new A.AgentLog("", 1), "test"));
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".tmp"));

            s.RefuseDisconnect = false;
            new A.AutoSignInSessions(s, file).Recover(new A.AgentLog("", 1)); // the next start locks it
            Assert.Contains(3, s.Disconnects);
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); File.Delete(file + ".tmp"); }
    }

    [Fact]
    public void A_resume_within_the_hold_keeps_the_session()
    {
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMilliseconds(300);
        try
        {
            var (s, auto, log) = AutoSignedSession();
            var helper = new CoverHelper();
            auto.Dropped(3, helper, log);
            Assert.Same(helper, auto.Resumed(3));          // handed back to end once the new helper covers the screen
            Assert.Equal(A.AutoSignInEndActions.Lock, auto.Owned(3)?.EndAction); // the new connection owns the end action
            Thread.Sleep(600);
            Assert.Empty(s.LogOffs);
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); }
    }

    [Theory]
    [InlineData(true, A.DesktopSessionState.Active, true)]    // Disconnect on the device's banner: signed out at once
    [InlineData(false, A.DesktopSessionState.Active, true)]   // the cover crashed on an open desktop: signed out at once
    [InlineData(false, A.DesktopSessionState.Locked, false)]  // locked at the device (Ctrl+Alt+Del → Lock): safe, the hold runs out
    public void When_the_cover_ends_during_the_hold(bool byUser, string state, bool signedOutAtOnce)
    {
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromSeconds(30);
        try
        {
            var (s, auto, log) = AutoSignedSession(state);
            var helper = new CoverHelper();
            auto.Dropped(3, helper, log);
            helper.End(byUser);
            if (signedOutAtOnce) WaitFor(() => s.LogOffs.Contains(3), "sign-out at once");
            else { Thread.Sleep(500); Assert.Empty(s.LogOffs); }
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); }
    }

    [Fact]
    public void After_a_restart_left_sessions_get_their_end_action_and_dropped_ones_are_signed_out()
    {
        var file = Path.Combine(Path.GetTempPath(), $"rh-autosignin-{Guid.NewGuid():N}.json");
        try
        {
            var s = new Sessions(new A.DesktopSessionInfo(3, @"TEST\admin", A.DesktopSessionState.Active, true, "Console"),
                new A.DesktopSessionInfo(4, @"TEST\admin2", A.DesktopSessionState.Active, false, "RDP"),
                new A.DesktopSessionInfo(5, @"TEST\other", A.DesktopSessionState.Active, false, "RDP"));
            s.Sids[3] = SidOf("admin");
            s.Sids[4] = SidOf("admin2");
            s.Sids[5] = "S-1-5-21-9-9-9-500"; // session 5 belongs to someone else now
            var before = new A.AutoSignInSessions(s, file);
            before.Signed(3, SidOf("admin"), @"TEST\admin", A.AutoSignInEndActions.Lock);
            before.Signed(4, SidOf("admin2"), @"TEST\admin2", A.AutoSignInEndActions.Lock);
            before.Signed(5, SidOf("viewer"), @"TEST\viewer", A.AutoSignInEndActions.SignOut);
            before.Dropped(4, new CoverHelper(), new A.AgentLog("", 1)); // the agent "crashes" during the hold
            Assert.True(File.Exists(file));

            new A.AutoSignInSessions(s, file).Recover(new A.AgentLog("", 1)); // the next start
            Assert.Contains(3, s.Disconnects);  // its end action: lock
            Assert.Contains(4, s.LogOffs);      // dropped: signed out
            Assert.DoesNotContain(5, s.LogOffs); // not the account's any more: left alone
            Assert.False(File.Exists(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Automatic_sign_in_attempts_are_limited_per_account()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var gate = new A.AutoSignInGate(clock: () => now);
        Assert.True(gate.TryBegin("S-1"));
        Assert.True(gate.TryBegin("S-1"));
        Assert.True(gate.TryBegin("S-1"));
        Assert.False(gate.TryBegin("S-1"));
        Assert.True(gate.TryBegin("S-2"));   // another account (also behind the same jump host) isn't blocked
        now = now.AddMinutes(15);
        Assert.True(gate.TryBegin("S-1"));
    }

    [Fact]
    public void Automatic_sign_in_policy_values()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins] };
        var d = A.AgentPolicy.From(config, null);
        Assert.Equal(A.NoUserBehaviors.SignInScreen, d.NoUserBehavior);
        Assert.Empty(d.Groups(A.AgentRight.AutoSignIn));  // nobody by default
        Assert.False(d.AutoSignInRequirePrivacyScreen);
        var p = A.AgentPolicy.From(config, new Values(new()
        {
            ["NoUserBehavior"] = new[] { "autosignin" },
            ["AutoSignInRequirePrivacyScreen"] = 1,
            [@"Rights\AutoSignIn"] = new[] { Admins },
        }));
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, p.NoUserBehavior);
        Assert.True(p.AutoSignInRequirePrivacyScreen);
        Assert.Equal([Admins], p.Groups(A.AgentRight.AutoSignIn));
        Assert.Equal(A.NoUserBehaviors.SignInScreen, A.AgentPolicy.From(config, new Values(new() { ["NoUserBehavior"] = new[] { "anything" } })).NoUserBehavior);
        Assert.Empty(A.AgentPolicy.Refused("x").Groups(A.AgentRight.AutoSignIn));
    }

    [Fact]
    public void Pipe_messages_match_the_credential_provider()
    {
        // The same bytes as the provider's Wire.h: version 1, type, payload length, payload.
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var pending = A.AutoSignInWire.WritePending(nonce, 0x01020304);
        Assert.Equal(new byte[] { 1, A.AutoSignInWire.Pending, 20, 0 }, pending[..4]);
        Assert.Equal(nonce, pending[4..20]);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, pending[20..]);

        var ticket = new byte[A.AutoSignInWire.MaxMessageBytes];
        var n = A.AutoSignInWire.WriteTicket(ticket, nonce, "D", "u", "S", System.Text.Encoding.Unicode.GetBytes("pw"));
        Assert.Equal(new byte[] { 1, A.AutoSignInWire.Ticket, (byte)(n - 4), 0 }, ticket[..4]);
        Assert.Equal(new byte[] { 1, 0, (byte)'D', 0, 1, 0, (byte)'u', 0, 1, 0, (byte)'S', 0, 2, 0, (byte)'p', 0, (byte)'w', 0 }, ticket[20..n]);
        Assert.Throws<ArgumentException>(() => A.AutoSignInWire.WriteTicket(ticket, nonce, "D", "", "S", "pw"u8));

        // A Claim and a Result from the provider, as its Wire.cpp writes them.
        var claim = new byte[] { 1, A.AutoSignInWire.Claim, 20, 0 }.Concat(nonce).Concat(new byte[] { 4, 3, 2, 1 }).ToArray();
        Assert.True(A.AutoSignInWire.IsClaim(claim, nonce, 0x01020304));
        Assert.False(A.AutoSignInWire.IsClaim(claim, nonce, 7));
        Assert.False(A.AutoSignInWire.IsClaim(claim[..^1], nonce, 0x01020304));
        var result = new byte[] { 1, A.AutoSignInWire.Result, 24, 0 }.Concat(nonce).Concat(new byte[] { 0x6D, 0, 0, 0xC0, 0x6A, 0, 0, 0xC0 }).ToArray();
        Assert.Equal((unchecked((int)0xC000006D), unchecked((int)0xC000006A)), A.AutoSignInWire.ReadResult(result, nonce));
        Assert.Null(A.AutoSignInWire.ReadResult(result, new byte[16]));
    }

    [Fact]
    public void The_sign_in_request_carries_the_password_as_utf16_and_rejects_malformed_ones()
    {
        var m = DesktopWire.SignInRequest("pä");
        Assert.Equal((byte)DesktopMessage.SignInRequest, m[0]);
        Assert.Equal("pä", System.Text.Encoding.Unicode.GetString(A.DesktopWire.ReadSignInRequest(m)));
        Assert.True(A.DesktopWire.ReadSignInRequest(m[..^1]).IsEmpty);
        Assert.Throws<ArgumentException>(() => DesktopWire.SignInRequest(""));
        Assert.Throws<ArgumentException>(() => DesktopWire.SignInRequest(new string('x', 257)));
    }

    [Fact]
    public void A_protected_secret_gives_its_clear_text_only_inside_the_callback()
    {
        using var s = new A.ProtectedSecret("secret"u8);
        string? seen = null;
        s.Reveal(p => seen = System.Text.Encoding.UTF8.GetString(p));
        Assert.Equal("secret", seen);
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Reveal(_ => { }));
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
        // Consent by default: no "None" and nobody with ConsentBypass until a policy says so.
        Assert.Equal([A.DesktopConsent.Ask, A.DesktopConsent.Pin], d.ConsentModes);
        Assert.Empty(d.Groups(A.AgentRight.ConsentBypass));

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

    [Fact]
    public void An_unreadable_policy_allows_nothing()
    {
        var p = A.AgentPolicy.Refused("access denied");
        Assert.False(p.DesktopEnabled);
        Assert.Empty(p.ConsentModes);
        foreach (var right in Enum.GetValues<A.AgentRight>()) Assert.Empty(p.Groups(right));
        Assert.Equal("access denied", p.LoadError);
    }
}
