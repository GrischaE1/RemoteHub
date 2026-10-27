using System.Management.Automation.Runspaces;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteHub;

/// <summary>
/// A connection's Device actions – lock, send a message, sign out, restart, shut down, update Group Policy, sync with
/// Intune – in the tree's and the session tab's menu. They run as fixed PowerShell over the connection's agent or WinRM
/// (<see cref="DeviceActions"/>), one at a time per device.
/// </summary>
public partial class MainWindow
{
    /// <summary>Devices with an action under way: their entries wait until it has finished.</summary>
    private readonly HashSet<Connection> _deviceActionsRunning = [];

    /// <summary>Fills a Device actions group afresh (empty when <paramref name="offered"/> is false, so the group hides).</summary>
    private void FillDeviceActions(MenuItem group, Connection? c, bool offered) =>
        FillDeviceActions(group, offered && c is not null, c is not null && _deviceActionsRunning.Contains(c), a => _ = RunDeviceAction(c!, a));

    internal static void FillDeviceActions(MenuItem group, bool offered, bool busy, Action<DeviceAction> run)
    {
        group.Items.Clear();
        if (!offered) return;
        foreach (var a in Enum.GetValues<DeviceAction>())
        {
            // Those that interrupt users first, then the two that only refresh the device's settings.
            if (a == DeviceAction.GroupPolicyUpdate) group.Items.Add(new Separator());
            group.Items.Add(MenuEntry(DeviceActions.Header(a), DeviceActionIcon(a), () => run(a), !busy,
                busy ? "Another device action is still running on this device" : DeviceActions.Tip(a)));
        }
    }

    private static SymbolRegular DeviceActionIcon(DeviceAction a) => a switch
    {
        DeviceAction.Lock => SymbolRegular.LockClosed24,
        DeviceAction.SendMessage => SymbolRegular.Chat24,
        DeviceAction.SignOut => SymbolRegular.SignOut24,
        DeviceAction.Restart => SymbolRegular.ArrowRepeatAll24, // as the services' Restart
        DeviceAction.ShutDown => SymbolRegular.Power24,
        DeviceAction.GroupPolicyUpdate => SymbolRegular.DocumentSync24,
        _ => SymbolRegular.CloudSync24,
    };

    private async Task RunDeviceAction(Connection c, DeviceAction a)
    {
        if (Refused(RemoteTool.DeviceActions, c) || _deviceActionsRunning.Contains(c)) return;
        var title = DeviceActions.Title(a);
        string? message = null;
        if (a == DeviceAction.SendMessage)
        {
            var dlg = new InputDialog(title, $"Shown on the screen of everyone signed in on {c.Name}, for up to an hour. One line; text after " +
                                             $"{DeviceActions.MaxMessageLength} characters is cut off.", "") { Owner = this };
            if (dlg.ShowDialog() != true || (message = DeviceActions.CleanMessage(dlg.Value)).Length == 0) return;
        }
        if (ToolTarget(c, KnownTarget(c)) is not { } t) return;

        _deviceActionsRunning.Add(c);
        Runspace? rs = null;
        DeviceActionResult? result = null;
        string? failure = null;
        try
        {
            // Connecting and asking who is signed in take a moment before anything else shows (as the unlock window).
            Cursor = System.Windows.Input.Cursors.Wait;
            rs = await Task.Run(() => PsRemoting.Open(t.Target));
            if (DeviceActions.NamesUsers(a))
            {
                var users = await Task.Run(() => DeviceActions.SignedIn(rs));
                Cursor = null;
                if (a == DeviceAction.SignOut && users.Count == 0)
                {
                    await Dialogs.Info(this, title, DeviceActions.NobodySignedIn(a, c.Name));
                    return;
                }
                var ask = DeviceActions.Confirmation(a, c.Name, users)!.Value;
                if (!await Dialogs.Confirm(this, ask.Title, ask.Text, ask.Button)) return;
                Cursor = System.Windows.Input.Cursors.Wait;
            }
            var open = rs;
            result = await Task.Run(() => DeviceActions.Run(open, a, c.Name, message));
        }
        catch (RemoteCommandException ex) { failure = ex.Message; }
        catch (Exception ex) { failure = PsRemoting.Explain(ex, t.Target); }
        finally
        {
            Cursor = null;
            _deviceActionsRunning.Remove(c);
            // A restarting device may not answer the close any more: closed in the background.
            if (rs is { } done) _ = Task.Run(() => PsSession.CloseQuietly(done));
        }
        DiagLog.Write($"Device action {a} ({(t.Target.Agent is not null ? "agent" : "WinRM")}): " +
                      (failure is not null ? "failed" : result!.Ok ? "done" : "partly done or nothing to do"));
        Data.AuditLog.Write(Data.AuditEvents.DeviceAction, c, DeviceActions.AuditDetail(a, result, failure));
        await Dialogs.Info(this, title, failure is not null ? $"{DeviceActions.Header(a).TrimEnd('…')} didn't work on {c.Name}.\n\n{failure}" : result!.Text);
    }
}
