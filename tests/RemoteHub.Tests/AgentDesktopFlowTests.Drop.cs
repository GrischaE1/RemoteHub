extern alias agent;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Agent;
using RemoteHub.Remoting;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

// A drop the agent notices first (lab D-RES-10, C-END-06, C-END-07): the endpoint driven directly on a scripted socket,
// since only that can end the agent's receive the way .NET's keep-alive timeout does.
public partial class AgentDesktopFlowTests
{
    /// <summary>The desktop endpoint on a <see cref="ScriptedSocket"/>, with the in-process helpers of the other tests.</summary>
    private sealed class DirectDevice
    {
        public required InProcessHelpers Helpers;
        public required Sessions Sessions;
        public required A.DesktopServices Desktop;
        public required A.AgentHostOptions Options;
        public required A.AgentPolicy Policy;
        public readonly ConcurrentQueue<(A.AuditEvent Id, string Message)> Audits = new();

        /// <param name="aborted">The request's RequestAborted: cancelled when the server sees the connection reset.</param>
        public (ScriptedSocket Socket, Task Run) Connect(string user = "admin", CancellationToken aborted = default)
        {
            var ws = new ScriptedSocket();
            var identity = new A.ShellIdentity(@"TEST\" + user, default, [Admins, Users], SidOf(user));
            return (ws, new A.DesktopEndpoint(Options, identity, Policy, "127.0.0.1").RunAsync(ws, aborted));
        }

        public string Ended() => Audits.Single(a => a.Id == A.AuditEvent.DesktopEnded).Message;
    }

    private static DirectDevice StartDirect(A.AgentPolicy? policy = null, A.IAutoSignIn? autoSignIn = null, A.DesktopSessionInfo[]? sessionList = null,
        string? stateFile = null, bool secureScreen = false, int changing = 20)
    {
        var helpers = new InProcessHelpers(() => new Ui(true), _ => new Screen(secureScreen, changing: changing), new Input(), new Apps());
        var sessions = new Sessions(sessionList ?? [ActiveUser, LoginScreen]);
        policy ??= new A.AgentPolicy
        {
            Rights = new Dictionary<A.AgentRight, IReadOnlyList<string>>
            {
                [A.AgentRight.Desktop] = [Admins],
                [A.AgentRight.SecureDesktop] = [Admins],
            },
        };
        var desktop = new A.DesktopServices(sessions, helpers, () => null, autoSignIn) { AutoSigned = new A.AutoSignInSessions(sessions, stateFile) };
        var log = new A.AgentLog("", 1);
        var options = new A.AgentHostOptions(new A.AgentConfig { DeviceId = "dev", DeviceLabel = "Device 1" }, null!, new Auth(), null!, log, Desktop: desktop, Policy: () => policy!);
        var d = new DirectDevice { Helpers = helpers, Sessions = sessions, Desktop = desktop, Options = options, Policy = policy };
        log.Audited += (id, message) => d.Audits.Enqueue((id, message));
        return d;
    }

    /// <summary>The agent's statuses on this socket until <paramref name="state"/> (or a failure) arrives.</summary>
    private static DesktopStatus NextStatus(ScriptedSocket ws, string state)
    {
        while (true)
        {
            Assert.True(ws.Sent.TryTake(out var m, TimeSpan.FromSeconds(15)), $"no {state} status from the agent");
            if (DesktopWire.TypeOf(m!) != DesktopMessage.Status) continue;
            var s = DesktopWire.ReadJson(m!, DesktopJson.Default.DesktopStatus);
            if (s.State == state || s.State is DesktopStatusState.Failed or DesktopStatusState.Denied) return s;
        }
    }

    private static void SendStart(ScriptedSocket ws, int session, string consent, string? resume = null, bool autoSignIn = false) =>
        ws.Receive(DesktopWire.Json(DesktopMessage.Start, new DesktopStart(session, consent, false, 30, 60, 0, resume, null, autoSignIn), DesktopJson.Default.DesktopStart));

    [Fact]
    public async Task A_drop_the_agent_notices_first_is_lost_and_continues_without_asking_again()
    {
        var d = StartDirect();
        var (first, run) = d.Connect();
        SendStart(first, ActiveUser.Id, DesktopConsent.Ask);
        var token = NextStatus(first, DesktopStatusState.Connected).Resume!;
        Assert.Equal(1, d.Helpers.Uis[0].Asked);

        first.Drop(); // the keep-alive timeout: the receive ends cancelled, not with an error
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.EndsWith("(connection lost)", d.Ended());

        var (second, _) = d.Connect();
        SendStart(second, ActiveUser.Id, DesktopConsent.Ask, resume: token);
        Assert.Equal(DesktopStatusState.Connected, NextStatus(second, DesktopStatusState.Connected).State);
        Assert.Equal(0, d.Helpers.Uis[1].Asked); // no new question (no WaitingForUser)
        WaitFor(() => d.Audits.Any(a => a.Id == A.AuditEvent.DesktopResumed), "the resumed audit entry"); // written after the Connected status
        second.Close();
    }

    [Fact]
    public async Task An_aborted_request_is_lost_without_restarting_the_helper_and_a_later_close_still_revokes()
    {
        var d = StartDirect();
        using var aborted = new CancellationTokenSource();
        var (first, run) = d.Connect(aborted: aborted.Token);
        SendStart(first, ActiveUser.Id, DesktopConsent.Ask);
        var token = NextStatus(first, DesktopStatusState.Connected).Resume!;

        // The server saw the connection reset: the receive and the helper's loop are both cancelled (lab D-RES-10, where
        // the helper's loop came first and was logged as "the desktop helper ended – starting it again").
        aborted.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.EndsWith("(connection lost)", d.Ended());
        Assert.Equal(1, d.Helpers.Started);

        // Continued with the token: nobody is asked (both fixes together: the drop releases it, nothing revokes it).
        var (second, secondRun) = d.Connect();
        SendStart(second, ActiveUser.Id, DesktopConsent.Ask, resume: token);
        var next = NextStatus(second, DesktopStatusState.Connected).Resume!;
        Assert.Equal(0, d.Helpers.Uis[1].Asked);
        WaitFor(() => d.Audits.Any(a => a.Id == A.AuditEvent.DesktopResumed), "the resumed audit entry"); // written after the Connected status

        // Ended on purpose this time: the new token goes with it.
        second.Close();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(15));
        var (third, _) = d.Connect();
        SendStart(third, ActiveUser.Id, DesktopConsent.Ask, resume: next);
        NextStatus(third, DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[2].Asked);
        third.Close();
    }

    [Fact]
    public async Task A_close_from_RemoteHub_still_ends_on_purpose_and_the_token_goes()
    {
        var d = StartDirect();
        var (first, run) = d.Connect();
        SendStart(first, ActiveUser.Id, DesktopConsent.Ask);
        var token = NextStatus(first, DesktopStatusState.Connected).Resume!;

        first.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.EndsWith("(closed by RemoteHub)", d.Ended());

        var (second, _) = d.Connect();
        SendStart(second, ActiveUser.Id, DesktopConsent.Ask, resume: token);
        NextStatus(second, DesktopStatusState.Connected);
        Assert.Equal(1, d.Helpers.Uis[1].Asked); // asked again
        second.Close();
    }

    [Fact]
    public async Task A_drop_the_agent_notices_first_keeps_the_automatic_sign_in_session_covered_then_signs_it_out()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var file = Path.Combine(Path.GetTempPath(), $"rh-autosignin-{Guid.NewGuid():N}.json");
        A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromSeconds(3);
        try
        {
            var broker = new FakeAutoSignIn();
            var d = StartDirect(AutoSignInPolicy(), broker, [ConsoleSignIn], file, secureScreen: true);
            broker.Answer = SignsIn(d.Sessions, SidOf("admin"));
            var (ws, run) = d.Connect();
            SendStart(ws, ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
            NextStatus(ws, DesktopStatusState.SignInReady);
            ws.Receive(DesktopWire.SignInRequest("pw"));
            NextStatus(ws, DesktopStatusState.SignedIn);
            WaitFor(() => d.Desktop.AutoSigned.Owned(3) is not null && d.Helpers.SessionIds.Contains(3), "the picture in the new session");
            var frames = ws.SentOf((byte)DesktopMessage.Video); // only the helper that is the connection's now gets its frames through
            WaitFor(() => ws.SentOf((byte)DesktopMessage.Video) > frames, "frames from the helper in session 3");

            ws.Drop();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.EndsWith("(connection lost)", d.Ended());
            // Covered for the hold, not locked at once (C-END-06) – and recorded as dropped, so a restart signs it out (C-END-07).
            Assert.Empty(d.Sessions.Disconnects);
            Assert.Empty(d.Sessions.LogOffs);
            var recorded = File.ReadAllText(file);
            Assert.Matches("\"Dropped\"\\s*:\\s*true", recorded);

            WaitFor(() => d.Sessions.LogOffs.Contains(3), "sign-out after the hold");
            Assert.Contains(d.Audits, a => a.Id == A.AuditEvent.AutoSignInEnded && a.Message.EndsWith("signed out (connection lost and not continued in time)"));
            Assert.Empty(d.Sessions.Disconnects);

            // The service killed during the hold instead: the next start signs the session out.
            var restarted = new Sessions(new A.DesktopSessionInfo(3, @"TEST\admin", A.DesktopSessionState.Active, true, "Console"));
            restarted.Sids[3] = SidOf("admin");
            File.WriteAllText(file, recorded);
            new A.AutoSignInSessions(restarted, file).Recover(new A.AgentLog("", 1));
            Assert.Equal([3], restarted.LogOffs);
            Assert.Empty(restarted.Disconnects);
        }
        finally
        {
            A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2);
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_aborted_request_keeps_the_automatic_sign_in_cover_for_the_hold_and_a_resume_continues_in_the_same_session()
    {
        A.DesktopEndpoint.AutoSignInAttempts = new A.AutoSignInGate();
        var hold = TimeSpan.FromSeconds(3);
        A.AutoSignInSessions.HoldAfterDrop = hold;
        try
        {
            var broker = new FakeAutoSignIn();
            var d = StartDirect(AutoSignInPolicy(), broker, [ConsoleSignIn], secureScreen: true, changing: int.MaxValue); // frames keep coming, as on a busy screen
            broker.Answer = SignsIn(d.Sessions, SidOf("admin"));
            using var aborted = new CancellationTokenSource();
            var (ws, run) = d.Connect(aborted: aborted.Token);
            SendStart(ws, ConsoleSignIn.Id, DesktopConsent.Ask, autoSignIn: true);
            var token = NextStatus(ws, DesktopStatusState.Connected).Resume!;
            NextStatus(ws, DesktopStatusState.SignInReady);
            ws.Receive(DesktopWire.SignInRequest("pw"));
            NextStatus(ws, DesktopStatusState.SignedIn);
            WaitFor(() => d.Desktop.AutoSigned.Owned(3) is not null && d.Helpers.SessionIds.Contains(3), "the picture in the new session");
            var sent = ws.SentOf((byte)DesktopMessage.Video); // only the helper that is the connection's now gets its frames through
            WaitFor(() => ws.SentOf((byte)DesktopMessage.Video) > sent + 3, "frames flowing from the helper in session 3");
            var helpers = d.Helpers.Started;

            // The server sees the connection reset while frames are on their way: the receive and the helper's loop are
            // cancelled together (lab C-END-12, run 20261018-133341: the cover ended 34 ms after the drop and the session
            // was signed out at once).
            aborted.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            var droppedAt = DateTime.UtcNow;
            Assert.EndsWith("(connection lost)", d.Ended());
            Thread.Sleep(hold / 2); // nothing to wait for: the session must stay as it is
            Assert.Empty(d.Sessions.LogOffs);
            Assert.Empty(d.Sessions.Disconnects);
            Assert.DoesNotContain(d.Audits, a => a.Id == A.AuditEvent.AutoSignInEnded);
            Assert.Equal(helpers, d.Helpers.Started);

            // Continued within the hold: the same session, nobody asked, the cover back.
            using var abortedAgain = new CancellationTokenSource();
            var (second, secondRun) = d.Connect(aborted: abortedAgain.Token);
            SendStart(second, 3, DesktopConsent.Ask, resume: token);
            NextStatus(second, DesktopStatusState.Connected);
            Assert.Equal(3, d.Helpers.SessionIds.Last());
            Assert.Equal(0, d.Helpers.Uis.Last().Asked);
            WaitFor(() => d.Audits.Any(a => a.Id == A.AuditEvent.DesktopResumed), "the resumed audit entry"); // written after the Connected status
            WaitFor(() => !d.Helpers.Uis.Last().Screens.IsEmpty, "the privacy screen in the continued connection");
            var remaining = droppedAt + hold + TimeSpan.FromSeconds(1) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining); // past the first hold: the resume stopped it
            Assert.Empty(d.Sessions.LogOffs);

            // Dropped again and not continued: signed out once the hold runs out, not before.
            abortedAgain.Cancel();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(15));
            var again = DateTime.UtcNow;
            WaitFor(() => d.Sessions.LogOffs.Contains(3), "sign-out after the hold");
            Assert.True(DateTime.UtcNow - again >= hold - TimeSpan.FromMilliseconds(200), "signed out before the hold ran out");
            Assert.Contains(d.Audits, a => a.Id == A.AuditEvent.AutoSignInEnded && a.Message.EndsWith("signed out (connection lost and not continued in time)"));
            Assert.Empty(d.Sessions.Disconnects);
        }
        finally { A.AutoSignInSessions.HoldAfterDrop = TimeSpan.FromMinutes(2); }
    }

    /// <summary>A helper on a real anonymous pipe (a sync handle, like the helper process's stdout); counts what was read.</summary>
    private sealed class PipeHelper : A.IDesktopHelper
    {
        private readonly AnonymousPipeServerStream _out = new(PipeDirection.Out);
        private readonly CountingStream _reader;
        public volatile bool Disposed;
        public PipeHelper() => _reader = new CountingStream(new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle));
        public long Read => _reader.Count;
        public void Write(ReadOnlySpan<byte> bytes) { _out.Write(bytes); _out.Flush(); }
        public Stream Input => Stream.Null;
        public Stream Output => _reader;
        public int ProcessId => 0;
        public void Dispose() { Disposed = true; _out.Dispose(); }
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _count;
        public long Count => Interlocked.Read(ref _count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Interlocked.Add(ref _count, n);
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Interlocked.Add(ref _count, n); return n; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Framed(byte[] m)
    {
        var framed = new byte[4 + m.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, m.Length);
        m.CopyTo(framed, 4);
        return framed;
    }

    [Fact]
    public async Task A_relay_read_cancelled_in_the_middle_of_a_frame_leaves_the_frame_to_the_cover()
    {
        var (s, auto, log) = AutoSignedSession();
        var helper = new PipeHelper();
        var frame = Framed(A.DesktopWire.Text((A.DesktopMessage)A.HelperMessage.Log, new string('x', 200)));
        helper.Write(frame.AsSpan(0, 14)); // the length and the start of a frame: the rest is still on its way
        using var connection = new CancellationTokenSource();
        var relay = A.HelperOutput.ReadAsync(helper, connection.Token);
        SpinWait.SpinUntil(() => helper.Read == 14, TimeSpan.FromSeconds(10));
        Assert.Equal(14, helper.Read);

        // The request is aborted while the relay waits for the rest of the frame; the cover takes over the helper.
        connection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay);
        auto.Dropped(3, helper, log);
        helper.Write(frame.AsSpan(14));
        helper.Write(Framed(A.DesktopWire.Text((A.DesktopMessage)A.HelperMessage.Log, "next")));
        SpinWait.SpinUntil(() => helper.Read == frame.Length + 4 + 1 + 4, TimeSpan.FromSeconds(10));
        Thread.Sleep(300); // nothing to wait for: the cover must still be there
        Assert.Empty(s.LogOffs);
        Assert.False(helper.Disposed);

        // The cover's helper ends after all: read in step with the frames, it is seen – the session is signed out at once.
        helper.Write(Framed(A.DesktopWire.Json((A.DesktopMessage)A.HelperMessage.End, new A.HelperEnd("x", false), A.HelperJson.Default.HelperEnd)));
        WaitFor(() => s.LogOffs.Contains(3), "sign-out once the cover ended");
        helper.Dispose();
    }
}
