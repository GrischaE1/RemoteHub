using System.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Remoting;

public sealed partial class AgentDesktopSession
{
    /// <summary>How long the device gets to read its Start menu.</summary>
    internal static TimeSpan AppListTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The apps in the device's Start menu, for picking app tabs (docs/app-tabs-design.md §6.5, §8.2): a connection of its
    /// own that asks for the list instead of a start, and ends. Nothing starts on the device and no session is needed.
    /// Throws with a sentence for the user when the device doesn't offer app tabs to this account or doesn't answer.
    /// </summary>
    public static async Task<DesktopAppList> ListAppsAsync(PsTarget target, CancellationToken ct = default)
    {
        using var client = await Open(target, ct);
        if (AppTabsRefusal(client.Hello, target.Host) is { } why) throw new InvalidOperationException(why);
        var answer = new TaskCompletionSource<DesktopAppList>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += m =>
        {
            try
            {
                switch (DesktopWire.TypeOf(m))
                {
                    case DesktopMessage.AppList:
                        answer.TrySetResult(DesktopWire.ReadJson(m, DesktopJson.Default.DesktopAppList));
                        break;
                    case DesktopMessage.Status when DesktopWire.ReadJson(m, DesktopJson.Default.DesktopStatus) is { State: DesktopStatusState.Failed } failed:
                        answer.TrySetException(new InvalidOperationException(failed.Message));
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { answer.TrySetException(ex); }
        };
        client.Closed += why => answer.TrySetException(new InvalidOperationException($"{target.Host} ended the connection before it sent its apps ({why})."));
        client.Start();
        client.Send(DesktopWire.Empty(DesktopMessage.AppList));
        try { return await answer.Task.WaitAsync(AppListTimeout, ct); }
        catch (TimeoutException) { throw new InvalidOperationException($"{target.Host} didn't send its apps within {AppListTimeout.TotalSeconds:0} s."); }
        finally { await client.CloseAsync(TimeSpan.FromSeconds(2)); }
    }

    /// <summary>
    /// Why this device doesn't offer app tabs to this account, or null when it does: an agent without them (older, or no
    /// virtual display) or one where the account may not – the device decides that (automatic sign-in, policy).
    /// </summary>
    public static string? AppTabsRefusal(DesktopHello hello, string device) =>
        hello.Features?.Contains(DesktopFeatures.Apps) != true
            ? $"The RemoteHub Agent on {device} doesn't offer app tabs – it needs version 0.22 or later and its virtual display (install option \"Virtual display for servers without a monitor\")."
            : !hello.Rights.Apps
                ? $"{device} doesn't offer app tabs to your account – they need automatic sign-in there (right-click → Install credential provider) and the device's policy (DesktopApps)."
                : null;
}
