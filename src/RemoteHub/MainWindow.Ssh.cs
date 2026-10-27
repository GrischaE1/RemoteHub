using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub;

/// <summary>SSH connections (docs/network-shares.md §6): kept like a share on its own, opened in a terminal tab.</summary>
public partial class MainWindow
{
    /// <summary>Opens the SSH connection in a new terminal tab.</summary>
    private void OpenSsh(NetworkShare entry)
    {
        if (Refused(Feature.Ssh)) return;
        var c = ServerOf(entry);
        if (!TryRoute(c, quiet: false, out var via)) return;
        if (ShareAccount(entry, null) is not { } account) return;
        var s = _vm.OpenSsh(c, entry);
        s.Account = account.Credentials;
        entry.OpenTabs++;
        s.Closed += () => entry.OpenTabs = Math.Max(0, entry.OpenTabs - 1);
        var view = new TerminalView(s, () => ConnectSsh(s), s.Close);
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        Connect(s, account, via);
    }

    /// <summary>Connect button: asks for the password or passphrase again only if it isn't stored and wasn't entered before.</summary>
    private void ConnectSsh(SshSession s)
    {
        if (RefusedIn(s, Feature.Ssh)) return;
        if (!TryRoute(ServerOf(s.Entry), quiet: false, out var via)) return; // the route as it is now (it may have moved)
        if (ShareAccount(s.Entry, s.Account) is not { } account) return;
        s.Account = account.Credentials;
        Connect(s, account, via);
    }

    private void Connect(SshSession s, (CredentialSet Credentials, string Source) account, JumpRoute? via)
    {
        var entry = s.Entry;
        var cred = account.Credentials;
        s.Connect((columns, rows) => SshShell.Open(entry, cred, AskTrust(entry), fp => Dispatcher.BeginInvoke(() =>
        {
            entry.TrustedFingerprint = fp;
            _vm.RequestSave();
            DiagLog.Write("SSH: host key accepted by the user");
        }), via, columns, rows), account.Source, AccentFor(s.Connection));
    }
}
