extern alias agent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>The Windows pieces of the agent's remote desktop on this PC (as the current user, not LocalSystem).</summary>
public class AgentDesktopWindowsTests
{
    [SkippableFact]
    public void The_screen_of_this_PC_can_be_captured()
    {
        Skip.IfNot(TestScreen.Available, TestScreen.Reason);
        using var screen = new A.WindowsScreen();
        screen.SyncDesktop();
        var monitors = screen.Monitors();
        Skip.If(monitors.Count == 0, "No display (service session).");
        Assert.Single(monitors, m => m.Primary);
        Assert.True(monitors[0].Primary);
        var frame = new A.FrameBuffer();
        var result = A.CaptureResult.Unchanged;
        for (var i = 0; i < 10 && result != A.CaptureResult.Changed; i++) result = screen.Capture(monitors[0], 200, frame);
        Assert.Equal(A.CaptureResult.Changed, result);
        // Desktop Duplication delivers real pixels; this test process isn't DPI-aware, so Windows may report the monitor scaled down.
        Assert.True(frame.Width >= monitors[0].Width && frame.Height >= monitors[0].Height, $"{frame.Width}x{frame.Height} for {monitors[0]}");
        Assert.Contains(frame.Pixels.Take(frame.Width * frame.Height * 4), b => b != 0);
    }

    [SkippableFact]
    public void Gdi_capture_reports_unchanged_frames()
    {
        Skip.IfNot(TestScreen.Available, TestScreen.Reason);
        var monitors = A.DisplayMonitors.List();
        Skip.If(monitors.Count == 0, "No display (service session).");
        using var gdi = new A.GdiScreen();
        var frame = new A.FrameBuffer();
        Assert.Equal(A.CaptureResult.Changed, gdi.Capture(monitors[0], frame));
        Assert.Equal((monitors[0].Width, monitors[0].Height), (frame.Width, frame.Height));
    }

    [Fact]
    public void The_arrow_cursor_renders_with_transparency()
    {
        var arrow = LoadCursor(IntPtr.Zero, 32512);
        var image = A.CursorShapes.Render(arrow);
        Assert.NotNull(image);
        Assert.True(image!.Width >= 16 && image.Height >= 16);
        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Bgra[i * 4 + 3] == 0);   // transparent around the arrow
        Assert.Contains(Enumerable.Range(0, image.Width * image.Height), i => image.Bgra[i * 4 + 3] == 255); // the arrow itself
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int name);

    [SkippableFact]
    public void Sessions_of_this_PC_include_the_current_user()
    {
        var list = new A.WtsSessions().List();
        var mine = Process.GetCurrentProcess().SessionId;
        Skip.IfNot(TestScreen.Available, TestScreen.Reason); // a disconnected session is listed as such, not active or locked
        var s = Assert.Single(list, x => x.Id == mine);
        Assert.EndsWith("\\" + Environment.UserName, s.User, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(s.State, new[] { A.DesktopSessionState.Active, A.DesktopSessionState.Locked });
    }

    /// <summary>Starts "RemoteHubAgent.exe desktop" as the current user in this session (the service does it as LocalSystem).</summary>
    private sealed class ProcessHelpers : A.IDesktopHelperLauncher
    {
        public A.IDesktopHelper Start(int sessionId)
        {
            // REMOTEHUB_AGENT_EXE: e.g. the published (trimmed) agent.
            var exe = Environment.GetEnvironmentVariable("REMOTEHUB_AGENT_EXE") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "RemoteHubAgent.exe");
            var p = Process.Start(new ProcessStartInfo(exe, "desktop")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            return new Helper(p);
        }

        private sealed class Helper(Process p) : A.IDesktopHelper
        {
            public Stream Input => p.StandardInput.BaseStream;
            public Stream Output => p.StandardOutput.BaseStream;
            public int ProcessId => p.Id;
            public void Dispose()
            {
                try { p.StandardInput.Close(); } catch { /* gone */ }
                if (!p.WaitForExit(5000)) p.Kill(true);
                p.Dispose();
            }
        }
    }

    private sealed class Auth : A.IShellAuthenticator
    {
        public A.AuthResult Authenticate(string userName, string password) =>
            new(A.AuthOutcome.Ok, "", new A.ShellIdentity("TEST\\admin", default, ["S-1-5-32-544"]));
    }

    /// <summary>
    /// The real helper process: real capture, real encoder, real banner window on this desktop (no input is sent).
    /// Shows a window for a moment, so it runs only with REMOTEHUB_DESKTOP_TESTS=1.
    /// </summary>
    [SkippableFact]
    public async Task The_helper_process_streams_this_screen()
    {
        Skip.If(Environment.GetEnvironmentVariable("REMOTEHUB_DESKTOP_TESTS") != "1", "Shows a window: runs only with REMOTEHUB_DESKTOP_TESTS=1.");
        using var key = RSA.Create(2048);
        using var tmp = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pfx), null);
        var session = new A.DesktopSessionInfo(Process.GetCurrentProcess().SessionId, "PC\\me", A.DesktopSessionState.Active, true, "Console");
        var policy = new A.AgentPolicy { Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>> { [A.AgentRight.Desktop] = ["S-1-5-32-544"], [A.AgentRight.SecureDesktop] = ["S-1-5-32-544"], [A.AgentRight.ConsentBypass] = ["S-1-5-32-544"] } };
        await using var app = A.AgentHost.Build(new A.AgentHostOptions(new A.AgentConfig { DeviceId = "d", ListenPort = 0 }, cert, new Auth(), null!, A.AgentLog.Null, IPAddress.Loopback,
            new A.DesktopServices(new Sessions(session), new ProcessHelpers()), () => policy));
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        var pw = new SecureString();
        pw.AppendChar('x');
        using var client = await AgentDesktopClient.ConnectAsync("127.0.0.1", new AgentEndpoint(port, AgentProtocol.Fingerprint(cert.RawData)), new PSCredential(@".\me", pw), CancellationToken.None);
        var frames = 0;
        var keyFrames = 0;
        var bytes = 0L;
        DesktopLayout? layout = null;
        var statuses = new List<string>();
        var decoded = 0;
        using var decoder = new H264Decoder();
        var done = new TaskCompletionSource();
        client.Received += m =>
        {
            switch (DesktopWire.TypeOf(m))
            {
                case DesktopMessage.Status: lock (statuses) statuses.Add(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopStatus).State); break;
                case DesktopMessage.Layout: layout = DesktopWire.ReadJson(m, DesktopJson.Default.DesktopLayout); break;
                case DesktopMessage.Video:
                    var h = DesktopWire.ReadVideo(m);
                    frames++;
                    if (h.KeyFrame) keyFrames++;
                    bytes += m.Length;
                    unsafe { decoder.Decode(m.AsSpan(DesktopWire.VideoHeaderSize), h.Width, h.Height, (_, _, _) => decoded++); }
                    client.Send(DesktopWire.Ack(h.FrameId));
                    if (frames >= 3) done.TrySetResult();
                    break;
            }
        };
        client.Start();
        client.Send(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session.Id, DesktopConsent.None, true, 30, 60, 0), DesktopJson.Default.DesktopStart));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains(DesktopStatusState.Connected, statuses);
        Assert.NotNull(layout);
        Assert.True(keyFrames >= 1);
        Assert.Equal(frames, decoded);
    }

    private sealed class Sessions(params A.DesktopSessionInfo[] list) : A.IDesktopSessions
    {
        public IReadOnlyList<A.DesktopSessionInfo> List() => list;
    }
}
