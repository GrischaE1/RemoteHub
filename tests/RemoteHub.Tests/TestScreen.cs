extern alias agent;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Whether this test run's Windows session has a screen: not in the service session (CI), and not in a disconnected
/// RDP session – there capture fails and windows aren't drawn. Tests that need the screen are skipped then.
/// </summary>
internal static class TestScreen
{
    public static int SessionId => System.Diagnostics.Process.GetCurrentProcess().SessionId;

    public static bool Available => SessionId != 0 && A.WtsNative.ConnectState(SessionId) == 0; // WTSActive

    public const string Reason = "This Windows session has no screen (service session, or a disconnected RDP session).";
}
