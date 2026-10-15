using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;
using RemoteHub.Services;
using RemoteHub.Views;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteHub;

/// <summary>Network shares – of a server, or on their own in a folder: add, edit, delete, and open them in a file tab (no agent, no remoting).</summary>
public partial class MainWindow
{
    /// <summary>
    /// Shares on their own have no server; their tabs still need one (tab group, colour, history). One stand-in per
    /// share, not stored, kept up to date with the share's name, host and folder.
    /// </summary>
    private readonly Dictionary<Guid, Connection> _shareHosts = new();

    private Connection ServerOf(NetworkShare share)
    {
        if (share.Server is { } server) return server;
        if (!_shareHosts.TryGetValue(share.Id, out var c)) _shareHosts[share.Id] = c = new Connection { Id = share.Id };
        c.Name = share.Name;
        c.Host = share.EffectiveHost;
        c.Parent = share.Parent;
        return c;
    }

    private void CmAddShare_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) AddShare(c); }

    /// <summary>New → Network share: a share on its own, in the selected folder.</summary>
    private void NewShare_Click(object sender, RoutedEventArgs e)
    {
        var share = new NetworkShare();
        var parent = TreeLayout.Allowed(share, _vm.TargetFolderForNew());
        share.Parent = parent;
        if (new ShareEditorWindow(share, null, _vm.Data, isNew: true) { Owner = this }.ShowDialog() != true) return;
        _vm.Add(share, parent);
        share.IsSelected = true;
        DiagLog.Write($"Network share added on its own: {share.ProtocolLabel}");
    }

    private void AddShare(Connection c)
    {
        var share = new NetworkShare { Server = c };
        if (new ShareEditorWindow(share, c, _vm.Data, isNew: true) { Owner = this }.ShowDialog() != true) return;
        c.Shares.Add(share);
        c.IsExpanded = true;
        _vm.RequestSave();
        DiagLog.Write($"Network share added to a server: {share.ProtocolLabel}");
    }

    private void EditShare(NetworkShare share)
    {
        if (new ShareEditorWindow(share, share.Server, _vm.Data, isNew: false) { Owner = this }.ShowDialog() != true) return;
        var server = ServerOf(share);
        foreach (var s in _vm.Sessions.OfType<ShareSession>().Where(s => s.Share == share)) s.Title = server.Name;
        _vm.RequestSave();
    }

    private async Task DeleteShare(NetworkShare share)
    {
        var open = _vm.Sessions.OfType<ShareSession>().Where(s => s.Share == share).ToList();
        if (!await Dialogs.Confirm(this, "Delete network share",
                $"Remove the share \"{share.Name}\" ({share.Address}){(share.Server is { } c ? $" from {c.Name}" : "")}?" +
                (open.Count > 0 ? "\n\nIts open tab is closed." : "") + "\n\nNothing on the share itself is deleted.", "Remove")) return;
        foreach (var s in open) s.Close();
        if (share.Server is { } server)
        {
            server.Shares.Remove(share);
            _vm.RequestSave();
        }
        else
        {
            _vm.Remove(share);
            _shareHosts.Remove(share.Id);
        }
    }

    /// <summary>Right-click on a share in the navigation.</summary>
    private void ShareItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetworkShare share) return;
        e.Handled = true;
        share.IsSelected = true;
        var cm = new ContextMenu();
        var why = WhyNotOpen(share);
        var open = new MenuItem { Header = "Open", FontWeight = FontWeights.SemiBold, Icon = new SymbolIcon(Enum.Parse<SymbolRegular>(share.DisplaySymbol)),
            IsEnabled = why is null, ToolTip = why };
        ToolTipService.SetShowOnDisabled(open, true);
        open.Click += (_, _) => OpenShare(share);
        cm.Items.Add(open);
        cm.Items.Add(new Separator());
        cm.Items.Add(MenuEntry("Copy address", SymbolRegular.Link24, () => { try { Clipboard.SetText(share.Address); } catch { /* clipboard busy */ } }));
        var edit = MenuEntry("Edit…", SymbolRegular.Edit24, () => EditShare(share));
        edit.InputGestureText = "F2";
        cm.Items.Add(edit);
        if (share.IsStandalone)
        {
            cm.Items.Add(MenuEntry("Duplicate", SymbolRegular.Copy24, () => _vm.Duplicate(share).IsSelected = true));
            var moveUnder = new MenuItem { Header = "Move under", Icon = new SymbolIcon(SymbolRegular.Router24) };
            FillMoveUnder(moveUnder, share);
            cm.Items.Add(moveUnder);
        }
        var remove = MenuEntry("Remove", SymbolRegular.Delete24, () => _ = DeleteShare(share));
        remove.InputGestureText = "Del";
        cm.Items.Add(remove);
        if (share.IsStandalone)
        {
            // Like the tree's own menu: new items go next to the share (it is selected above)
            cm.Items.Add(new Separator());
            var add = Submenu("New", SymbolRegular.Add24);
            add.Items.Add(MenuEntry("Connection", SymbolRegular.Add24, () => NewConnection_Click(this, new RoutedEventArgs())));
            add.Items.Add(MenuEntry("Network share", SymbolRegular.FolderLink24, () => NewShare_Click(this, new RoutedEventArgs())));
            add.Items.Add(MenuEntry("Folder", SymbolRegular.FolderAdd24, () => NewFolder_Click(this, new RoutedEventArgs())));
            cm.Items.Add(add);
        }
        cm.PlacementTarget = (UIElement)sender;
        cm.Placement = PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    private static string ShareLabel(NetworkShare share) => share.Server is { } c ? $"{c.Name} › {share.Name}" : share.Name;

    /// <summary>The tooltip of "Add network share…" for <paramref name="c"/>.</summary>
    private static string AddShareTip(Connection? c) => c?.JumpHost is { } jump
        ? $"An SMB, SFTP or WebDAV share of this server, reached through {jump.Name} – opened in a file tab (FTP can't go through a jump host)"
        : "An SMB, SFTP, FTP or WebDAV share of this server – opened in a file tab, no agent needed";

    /// <summary>Why the share can't be opened now (shown on its menu entries), or null.</summary>
    private static string? WhyNotOpen(NetworkShare share) =>
        share.Protocol == ShareProtocol.Ftp && (share.Server ?? (NodeBase)share).JumpHost is { } jump
            ? $"Reached through {jump.Name}. {ShareConnector.FtpNotThroughJumpHost}" : null;

    /// <summary>Opens the share in a new tab: this PC on the left, the share on the right.</summary>
    private void OpenShare(NetworkShare share)
    {
        if (WhyNotOpen(share) is not null)
        {
            _ = Dialogs.Info(this, "Network share", $"{share.Name} is reached through {(share.Server ?? (NodeBase)share).JumpHost!.Name}. {ShareConnector.FtpNotThroughJumpHost}");
            return;
        }
        var c = ServerOf(share);
        if (!TryRoute(c, quiet: false, out var via)) return;
        if (ShareAccount(share, null) is not { } account) return;
        var s = _vm.OpenShare(c, share);
        s.Account = account.Credentials;
        share.OpenTabs++;
        s.Closed += () => share.OpenTabs = Math.Max(0, share.OpenTabs - 1);
        var localStart = Directory.Exists(_vm.Data.Ui.LastLocalFolder) ? _vm.Data.Ui.LastLocalFolder!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var view = new FileBrowserView(s, localStart, () => ConnectShare(s), s.Close);
        view.Local.Navigated += p => { if (p.Length > 0) _vm.Data.Ui.LastLocalFolder = p; };
        view.UploadRequested += async (files, folder) =>
        {
            await UploadToShare(s, files, folder);
            await view.Remote.Refresh();
        };
        view.DownloadRequested += async (drag, folder) =>
        {
            await Download(drag, folder);
            await view.Local.Refresh();
        };
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        Connect(s, account, via);
    }

    /// <summary>Reconnect button: asks for the password again only if it isn't stored and wasn't entered before.</summary>
    private void ConnectShare(ShareSession s)
    {
        if (!TryRoute(ServerOf(s.Share), quiet: false, out var via)) return; // the route as it is now (the share may have moved)
        if (ShareAccount(s.Share, s.Account) is not { } account) return;
        s.Account = account.Credentials;
        Connect(s, account, via);
    }

    /// <param name="via">The jump host the share is reached through (null: directly).</param>
    private void Connect(ShareSession s, (CredentialSet Credentials, string Source) account, JumpRoute? via)
    {
        var share = s.Share;
        var cred = account.Credentials;
        s.Connect(() => ShareConnector.Open(share, cred, AskTrust(share), fp => Dispatcher.BeginInvoke(() =>
        {
            share.TrustedFingerprint = fp;
            _vm.RequestSave();
            DiagLog.Write($"Share ({share.ProtocolLabel}): key/certificate accepted by the user");
        }), via), account.Source, AccentFor(s.Connection));
    }

    /// <summary>
    /// The share's account (its own or the server's). Without a stored password the user is asked – kept in memory
    /// only. SMB and WebDAV without an account use the Windows account, FTP signs in anonymously. Null: cancelled.
    /// </summary>
    private (CredentialSet Credentials, string Source)? ShareAccount(NetworkShare share, CredentialSet? previous)
    {
        var (resolved, source) = Inheritance.ResolveCredentials(share, _vm.Data);
        var cred = resolved.Clone();
        if (cred.IsEmpty)
        {
            if (!ShareConnector.AccountOptional(share.Protocol))
            {
                _ = Dialogs.Info(this, "Network share", $"{share.Name} ({share.ProtocolLabel}) needs an account. Right-click it → Edit… and set one, or set credentials on {(share.Server is { } c ? c.Name : "its folder")}.");
                return null;
            }
            return (cred, share.Protocol == ShareProtocol.Ftp ? "anonymous sign-in (no account set)" : "your Windows account (no account set)");
        }
        if (string.IsNullOrEmpty(cred.Password))
        {
            if (previous is { Password.Length: > 0 } p && p.Username == cred.Username && p.Domain == cred.Domain) cred.Password = p.Password;
            else
            {
                var user = string.IsNullOrEmpty(cred.Domain) || cred.Username!.Contains('\\') ? cred.Username : $"{cred.Domain}\\{cred.Username}";
                var dlg = new PasswordDialog(confirm: false, title: "Network share",
                    message: $"No password is stored for {user}. Enter it to open {ShareLabel(share)} – it is kept in memory only.",
                    okText: "Connect") { Owner = this };
                if (dlg.ShowDialog() != true) return null;
                cred.Password = dlg.Password;
                source += ", password entered";
            }
        }
        return (cred, source);
    }

    /// <summary>Asks the user (on the UI thread, from the connecting thread) whether to trust a key or certificate.</summary>
    private Func<TrustRequest, bool> AskTrust(NetworkShare share) => req => Dispatcher.Invoke(() =>
    {
        var changed = req.What.Contains("changed");
        var text = changed
            ? $"{share.Address} presents a DIFFERENT {req.What.Replace(" (changed!)", "")} than the one you accepted before.\n\n" +
              "This happens when the server was reinstalled or its key replaced – or when someone intercepts the connection. Only continue if you know why it changed.\n\n"
            : $"{share.Address} presents a {req.What} that RemoteHub doesn't know yet.\n\n";
        text += $"Fingerprint (SHA-256):\n{req.Fingerprint}\n\n{req.Details}\n\nCompare it with the server's (ask its administrator if unsure). " +
                "If you trust it, RemoteHub remembers it for this share and asks again only if it changes.";
        return Dialogs.Confirm(this, changed ? "Server key changed" : "Trust this server?", text, "Trust and connect");
    }).GetAwaiter().GetResult();

    /// <summary>Uploads local files/folders into <paramref name="folder"/> on the share (progress in the transfers panel).</summary>
    private async Task UploadToShare(ShareSession s, IReadOnlyList<string> localPaths, string folder)
    {
        List<(string Local, string Relative)> list;
        try { list = FileUploader.Expand(localPaths); }
        catch (Exception ex) { await Dialogs.Info(this, "Upload", ex.Message); return; }
        if (list.Count == 0) return;

        bool? overwrite;
        try { overwrite = await AskOverwrite(await s.ExistingAsync(folder, localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\')))), folder); }
        catch (Exception ex) { await Dialogs.Info(this, "Upload", ex.Message); return; }
        if (overwrite is null) return;

        var items = list.Select(x => new TransferItem(x.Local, x.Relative, ShareLabel(s.Share))).ToList();
        await RunTransfers(s.Connection, items, "upload", () => ShareTransfer.UploadAsync(s.OpenAnother, folder, overwrite.Value, items, a => Dispatcher.BeginInvoke(a)),
            $"share {s.Share.Address}");
    }
}
