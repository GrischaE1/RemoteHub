extern alias agent;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        public (ScriptedSocket Socket, Task Run) Connect(string user = "admin")
        {
            var ws = new ScriptedSocket();
            var identity = new A.ShellIdentity(@"TEST\" + user, default, [Admins, Users], SidOf(user));
            return (ws, new A.DesktopEndpoint(Options, identity, Policy, "127.0.0.1").RunAsync(ws, CancellationToken.None));
        }

        public string Ended() => Audits.Single(a => a.Id == A.AuditEvent.DesktopEnded).Message;
    }

    private static DirectDevice StartDirect(A.AgentPolicy? policy = null, A.IAutoSignIn? autoSignIn = null, A.DesktopSessionInfo[]? sessionList = null,
        string? stateFile = null, bool secureScreen = false)
    {
        var helpers = new InProcessHelpers(() => new Ui(true), _ => new Screen(secureScreen), new Input(), new Apps());
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
        Assert.Contains(d.Audits, a => a.Id == A.AuditEvent.DesktopResumed);
        second.Close();
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
            var frames = ws.Sent.Count;
            WaitFor(() => ws.Sent.Count > frames, "the helper in session 3 running");

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
}
