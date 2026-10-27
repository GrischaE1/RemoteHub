using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;
using RemoteHub.Views;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Tests;

/// <summary>SSH in the UI: the editor for SSH and the key fields, the terminal tab, and the session's states.</summary>
public class SshViewTests
{
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    private static ShareEditorWindow Editor(Window owner, NetworkShare share, Connection? server = null)
    {
        var w = Sta.Theme(new ShareEditorWindow(share, server, new AppData(), isNew: true) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        w.Show();
        Sta.Pump();
        return w;
    }

    /// <summary>Opens the editor as a dialog, makes <paramref name="change"/> and clicks Add; true when it closed with it.</summary>
    private static bool? SaveWith(Window owner, NetworkShare share, Action<ShareEditorWindow> change)
    {
        var w = Sta.Theme(new ShareEditorWindow(share, null, new AppData(), isNew: false) { Owner = owner, Left = -4000, WindowStartupLocation = WindowStartupLocation.Manual });
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() =>
        {
            change(w);
            w.GetType().GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(w, null);
        }, System.Windows.Threading.DispatcherPriority.Background);
        return w.ShowDialog();
    }

    private static void Pick(ShareEditorWindow w, ShareProtocol kind) =>
        Field<ComboBox>(w, "_protocol").SelectedItem = Field<ComboBox>(w, "_protocol").Items.Cast<ComboBoxItem>().Single(i => (ShareProtocol)i.Tag == kind);

    private static List<ShareProtocol> Kinds(ShareEditorWindow w) =>
        Field<ComboBox>(w, "_protocol").Items.Cast<ComboBoxItem>().Select(i => (ShareProtocol)i.Tag).ToList();

    [Fact]
    public void The_editor_opens_with_the_kind_chosen_under_New_and_offers_SSH_only_on_its_own() => Sta.Run(owner =>
    {
        var w = Editor(owner, new NetworkShare { Protocol = ShareProtocol.Ssh });
        Assert.Equal("New SSH connection", w.Title);
        Assert.Equal([ShareProtocol.Ssh, ShareProtocol.Smb, ShareProtocol.Sftp, ShareProtocol.Ftp, ShareProtocol.WebDav], Kinds(w));
        Assert.Equal(ShareProtocol.Ssh, (ShareProtocol)((ComboBoxItem)Field<ComboBox>(w, "_protocol").SelectedItem).Tag);
        Assert.Equal(Visibility.Collapsed, Field<Wpf.Ui.Controls.TextBox>(w, "_path").Visibility);
        Assert.Equal("22", Field<Wpf.Ui.Controls.TextBox>(w, "_port").Text);
        Assert.Equal(Visibility.Visible, Field<StackPanel>(w, "_keyFields").Visibility);

        // Another kind: the title and the fields follow; SMB has no key.
        Field<ComboBox>(w, "_protocol").SelectedIndex = 1;
        Sta.Pump();
        Assert.Equal("New network share", w.Title);
        Assert.Equal(Visibility.Collapsed, Field<StackPanel>(w, "_keyFields").Visibility);
        Assert.Equal(Visibility.Visible, Field<Wpf.Ui.Controls.TextBox>(w, "_path").Visibility);
        w.Close();

        var server = new Connection { Name = "SRV01", Host = "srv01" };
        var shareOfServer = Editor(owner, new NetworkShare { Server = server, Protocol = ShareProtocol.Sftp }, server);
        Assert.DoesNotContain(ShareProtocol.Ssh, Kinds(shareOfServer));
        Assert.Equal(Visibility.Visible, Field<StackPanel>(shareOfServer, "_keyFields").Visibility);
        shareOfServer.Close();
    });

    [Fact]
    public void A_stored_key_shows_what_it_is_and_is_kept_or_removed_on_save() => Sta.Run(owner =>
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var key = Services.Secret.From(rsa.ExportRSAPrivateKeyPem())!;
        var share = new NetworkShare { Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01", InheritCredentials = false,
            Credentials = { Username = "deploy", PrivateKey = key } };
        var w = Editor(owner, share);
        Assert.StartsWith("ssh-rsa, 2048 bits · SHA256:", Field<TextBlock>(w, "_keyText").Text);
        Assert.Equal(Visibility.Visible, Field<Wpf.Ui.Controls.Button>(w, "_removeKey").Visibility);
        Assert.Equal("Replace key…", Field<Wpf.Ui.Controls.Button>(w, "_importKey").Content);
        Assert.Contains("Optional with a key", Field<Wpf.Ui.Controls.PasswordBox>(w, "_password").PlaceholderText);
        w.Close();

        Assert.True(SaveWith(owner, share, _ => { }));
        Assert.Same(key, share.Credentials.PrivateKey);

        // Switched to FTP, the key isn't kept: FTP doesn't sign in with one.
        Assert.True(SaveWith(owner, share, e => Pick(e, ShareProtocol.Ftp)));
        Assert.Equal(ShareProtocol.Ftp, share.Protocol);
        Assert.Null(share.Credentials.PrivateKey);
    });

    [Fact]
    public void Switching_between_SFTP_and_SSH_keeps_the_accepted_host_key() => Sta.Run(owner =>
    {
        var share = new NetworkShare { Name = "nas", Protocol = ShareProtocol.Sftp, Host = "nas", TrustedFingerprint = "SHA256:abc",
            InheritCredentials = false, Credentials = { Username = "admin", Password = Services.Secret.From(Guid.NewGuid().ToString("N")) } };
        Assert.True(SaveWith(owner, share, e => Pick(e, ShareProtocol.Ssh)));
        Assert.Equal(ShareProtocol.Ssh, share.Protocol);
        Assert.Equal("SHA256:abc", share.TrustedFingerprint);
        // Another host is another server: its key is asked again.
        Assert.True(SaveWith(owner, share, e => Field<Wpf.Ui.Controls.TextBox>(e, "_host").Text = "nas2"));
        Assert.Null(share.TrustedFingerprint);
    });

    // ------------------------------------------------------------------ the terminal tab

    private static (SshSession Session, TerminalView View) Tab(Window owner)
    {
        var entry = new NetworkShare { Name = "web01", Protocol = ShareProtocol.Ssh, Host = "web01" };
        var session = new SshSession(new Connection { Name = "web01", Host = "web01" }, entry);
        var view = new TerminalView(session, () => { }, session.Close);
        owner.Content = view;
        Sta.Pump();
        return (session, view);
    }

    [Fact]
    public void The_terminal_draws_what_the_server_sent_and_screen_readers_can_read_it() => Sta.Run(owner =>
    {
        var (session, view) = Tab(owner);
        lock (session.Terminal.SyncRoot) session.Terminal.Feed("\x1b[1;32mdeploy@web01\x1b[0m:~$ ls\r\n日本 \x1b[4munderlined\x1b[0m ✓\r\n");
        view.Surface.InvalidateVisual();
        Sta.Pump();
        // Drawn without failing (wide characters, a fallback glyph, attributes), and the size fits the tab.
        var bitmap = new RenderTargetBitmap((int)Math.Max(1, view.ActualWidth), (int)Math.Max(1, view.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(view);
        var (columns, rows) = view.Surface.CellsFor(new Size(800, 400));
        Assert.InRange(columns, 60, 140);
        Assert.InRange(rows, 15, 40);

        var peer = UIElementAutomationPeer.CreatePeerForElement(view.Surface);
        var value = (IValueProvider)peer.GetPattern(PatternInterface.Value)!;
        Assert.True(value.IsReadOnly);
        Assert.StartsWith("deploy@web01:~$ ls\r\n日本 underlined ✓", value.Value);
        Assert.Equal(AutomationControlType.Document, peer.GetAutomationControlType());
        session.Close();
    });

    [Fact]
    public void The_tab_follows_its_size_and_keys_go_to_the_terminal_not_the_window() => Sta.Run(owner =>
    {
        var (session, view) = Tab(owner);
        view.Surface.Focus();
        Sta.Pump();
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); lock (session.Terminal.SyncRoot) return session.Terminal.Columns != 80 || session.Terminal.Rows != 24; },
            TimeSpan.FromSeconds(5)), "the terminal didn't take the tab's size");
        Assert.True(view.Surface.IsKeyboardFocused);
        Assert.True(session.HasKeyboardFocus);
        var ctrlW = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.Surface)!, 0, System.Windows.Input.Key.W);
        Assert.True(TerminalView.OwnsKey(ctrlW));
        session.Close();
    });

    [Fact]
    public void A_connection_that_fails_says_why_and_offers_Connect_again() => Sta.Run(owner =>
    {
        var (session, view) = Tab(owner);
        var asked = (0, 0);
        session.Connect((columns, rows) =>
        {
            asked = (columns, rows);
            throw new ShareException("The server's key wasn't accepted – nothing was sent.");
        }, "this connection", System.Windows.Media.Brushes.SteelBlue);
        Assert.Equal(SessionState.Connecting, session.State);
        Assert.True(SpinWait.SpinUntil(() => { Sta.Pump(); return session.State == SessionState.Failed; }, TimeSpan.FromSeconds(10)));
        Assert.Equal("The server's key wasn't accepted – nothing was sent.", session.StatusText);
        Assert.True(asked.Item1 >= 2 && asked.Item2 >= 1); // the shell is asked for the terminal's size
        Assert.True(session.ShowsOverlay);
        Assert.True(Sta.Find<Wpf.Ui.Controls.Button>(view, b => Equals(b.Content, "Connect")).IsVisible);
        session.Send("ignored while not connected");
        session.Close();
    });
}
