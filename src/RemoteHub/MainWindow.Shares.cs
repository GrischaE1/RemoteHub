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

    /// <summary>New → Connection → SSH, SFTP, FTP, SMB or WebDAV: an item on its own, in the selected folder.</summary>
    private void NewShare(ShareProtocol kind)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (Refused(Features.For(kind))) return; // AllowSsh, or AllowNetworkShares and the protocol's switch
        var share = new NetworkShare { Protocol = kind };
        var parent = TreeLayout.Allowed(share, _vm.TargetFolderForNew());
        share.Parent = parent;
        if (new ShareEditorWindow(share, null, _vm.Data, isNew: true) { Owner = this }.ShowDialog() != true) return;
        _vm.AddNow(share, parent);
        RevealInTree(share);
        DiagLog.Write(share.IsSsh ? "SSH connection added" : $"Network share added on its own: {share.ProtocolLabel}");
    }

    /// <summary>
    /// The kinds of connection under New → Connection, the same in every New menu: Remote Control (a Windows device), SSH,
    /// then the file shares. A kind this PC's policy blocks isn't offered.
    /// </summary>
    private void FillNewConnection(ItemsControl menu)
    {
        menu.Items.Clear();
        foreach (var shown in NewConnectionMenu())
        {
            if (shown is not { } k)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var kind = k.Kind;
            var entry = MenuEntry(k.Header, Enum.Parse<SymbolRegular>(k.Symbol),
                () => { if (kind is { } p) NewShare(p); else NewConnection_Click(this, new RoutedEventArgs()); }, tip: k.Tip);
            if (kind is null) entry.InputGestureText = "Ctrl+N";
            menu.Items.Add(entry);
        }
    }

    /// <summary>New → Connection as it is shown: the kinds, and null for the one line before the first file share.</summary>
    internal static IEnumerable<(ShareProtocol? Kind, string Header, string Symbol, string Tip)?> NewConnectionMenu(Agent.IPolicySource? policy = null)
    {
        var line = false;
        foreach (var k in NewConnectionKinds(policy))
        {
            if (k.Kind is not (null or ShareProtocol.Ssh) && !line)
            {
                line = true;
                yield return null;
            }
            yield return k;
        }
    }

    /// <summary>The entries of New → Connection in their order (Kind null: Remote Control), without the ones policy blocks.</summary>
    internal static IEnumerable<(ShareProtocol? Kind, string Header, string Symbol, string Tip)> NewConnectionKinds(Agent.IPolicySource? policy = null)
    {
        yield return (null, "Remote Control", "Desktop24", "A Windows device – opened in a remote desktop tab (RDP or the RemoteHub Agent), with PowerShell and the remote tools");
        if (Features.IsAllowed(Feature.Ssh, policy))
            yield return (ShareProtocol.Ssh, "SSH", NetworkShare.Symbol(ShareProtocol.Ssh),
                "A Linux or macOS server, a NAS or a network device – opened in a terminal tab, signs in with a private key or a password");
        foreach (var (kind, header, tip) in new (ShareProtocol Kind, string Header, string Tip)[]
                 {
                     (ShareProtocol.Sftp, "SFTP", "Files over SSH – opened in a file tab"),
                     (ShareProtocol.Ftp, "FTP / FTPS", "Files over FTP or FTPS – opened in a file tab"),
                     (ShareProtocol.Smb, "SMB", "A Windows file share or a NAS – opened in a file tab"),
                     (ShareProtocol.WebDav, "WebDAV", "Files over WebDAV, e.g. Nextcloud – opened in a file tab"),
                 })
            if (Features.IsAllowed(kind, policy)) yield return (kind, header, NetworkShare.Symbol(kind), tip);
    }

    private void AddShare(Connection c)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (Refused(Feature.NetworkShares)) return;
        // The editor offers the kinds policy allows; with every one of them blocked there is nothing to add.
        var kinds = ShareEditorWindow.Kinds(standalone: false, current: null).ToList();
        if (kinds.Count == 0)
        {
            _ = Dialogs.Info(this, "Network share", "Every kind of network share is turned off by your organisation – see Settings → Policies.");
            return;
        }
        var share = new NetworkShare { Server = c, Protocol = kinds[0].Kind };
        if (new ShareEditorWindow(share, c, _vm.Data, isNew: true) { Owner = this }.ShowDialog() != true) return;
        c.Shares.Add(share);
        c.IsExpanded = true;
        _vm.SaveNow(); // a new item is written at once, as AddNow does
        DiagLog.Write($"Network share added to a server: {share.ProtocolLabel}");
    }

    private void EditShare(NetworkShare share)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (new ShareEditorWindow(share, share.Server, _vm.Data, isNew: false) { Owner = this }.ShowDialog() != true) return;
        var server = ServerOf(share);
        foreach (var s in TabsOf(share)) s.Title = server.Name;
        _vm.RequestSave();
    }

    private async Task DeleteShare(NetworkShare share)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        var open = TabsOf(share).ToList();
        var confirmed = share.IsSsh
            ? await Dialogs.Confirm(this, "Delete SSH connection", $"Remove the SSH connection \"{share.Name}\" ({share.Address})?" +
                (open.Count > 0 ? "\n\nIts open tab is closed." : "") + "\n\nNothing on the server is changed.", "Remove")
            : await Dialogs.Confirm(this, "Delete network share",
                $"Remove the share \"{share.Name}\" ({share.Address}){(share.Server is { } c ? $" from {c.Name}" : "")}?" +
                (open.Count > 0 ? "\n\nIts open tab is closed." : "") + "\n\nNothing on the share itself is deleted.", "Remove");
        if (!confirmed) return;
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
        // Blocked by this PC's policy: the share stays in the tree, but opening it isn't offered (docs/admx-design.md §4.3).
        if (Features.IsAllowed(share.Protocol))
        {
            var why = WhyNotOpen(share);
            var open = new MenuItem { Header = "Open", FontWeight = FontWeights.SemiBold, Icon = new SymbolIcon(Enum.Parse<SymbolRegular>(share.DisplaySymbol)),
                IsEnabled = why is null, ToolTip = why };
            ToolTipService.SetShowOnDisabled(open, true);
            open.Click += (_, _) => OpenShare(share);
            cm.Items.Add(open);
            cm.Items.Add(new Separator());
        }
        cm.Items.Add(MenuEntry("Copy address", SymbolRegular.Link24, () => { try { Clipboard.SetText(share.Address); } catch { /* clipboard busy */ } }));
        // A read-only tree (policy AllowConnectionEditing = 0, §5.4): only opening and copying the address are left.
        var editable = Features.IsAllowed(Feature.ConnectionEditing);
        if (editable)
        {
            var edit = MenuEntry("Edit…", SymbolRegular.Edit24, () => EditShare(share));
            edit.InputGestureText = "F2";
            cm.Items.Add(edit);
        }
        if (editable && share.IsStandalone)
        {
            cm.Items.Add(MenuEntry("Duplicate", SymbolRegular.Copy24, () => { if (!Refused(Feature.ConnectionEditing)) _vm.Duplicate(share).IsSelected = true; }));
            if (Features.IsAllowed(Feature.JumpHosts) || share.Parent is Connection) // blocked: only "out of" a jump host
            {
                var moveUnder = new MenuItem { Header = "Move under", Icon = new SymbolIcon(SymbolRegular.Router24) };
                FillMoveUnder(moveUnder, share);
                cm.Items.Add(moveUnder);
            }
        }
        if (editable)
        {
            var remove = MenuEntry("Remove", SymbolRegular.Delete24, () => _ = DeleteShare(share));
            remove.InputGestureText = "Del";
            cm.Items.Add(remove);
        }
        if (editable && share.IsStandalone)
        {
            // Like the tree's own menu: new items go next to the share (it is selected above)
            cm.Items.Add(new Separator());
            var add = Submenu("New", SymbolRegular.Add24);
            var connection = Submenu("Connection", SymbolRegular.Add24);
            FillNewConnection(connection);
            add.Items.Add(connection);
            add.Items.Add(MenuEntry("Folder", SymbolRegular.FolderAdd24, () => NewFolder_Click(this, new RoutedEventArgs())));
            cm.Items.Add(add);
        }
        cm.PlacementTarget = (UIElement)sender;
        cm.Placement = PlacementMode.MousePoint;
        cm.IsOpen = true;
    }

    /// <summary>The open tabs of a share or SSH connection.</summary>
    private IEnumerable<Rdp.SessionBase> TabsOf(NetworkShare share) =>
        _vm.Sessions.Where(s => s is ShareSession sh && sh.Share == share || s is SshSession ssh && ssh.Entry == share);

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
        if (share.IsSsh) { OpenSsh(share); return; }
        if (Refused(Features.For(share.Protocol))) return;
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
        if (RefusedIn(s, Features.For(s.Share.Protocol))) return;
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
    /// only. SMB and WebDAV without an account use the Windows account, FTP signs in anonymously. An account with a
    /// private key (SSH, SFTP) isn't asked for a password, but for the key's passphrase when it has one that isn't stored.
    /// Null: cancelled.
    /// </summary>
    private (CredentialSet Credentials, string Source)? ShareAccount(NetworkShare share, CredentialSet? previous)
    {
        var (resolved, source) = Inheritance.ResolveCredentials(share, _vm.Data);
        var cred = resolved.Clone();
        var topic = share.IsSsh ? "SSH connection" : "Network share";
        if (cred.IsEmpty)
        {
            if (!ShareConnector.AccountOptional(share.Protocol))
            {
                _ = Dialogs.Info(this, topic, $"{share.Name} ({share.ProtocolLabel}) needs an account. Right-click it → Edit… and set one, or set credentials on {(share.Server is { } c ? c.Name : "its folder")}.");
                return null;
            }
            return (cred, share.Protocol == ShareProtocol.Ftp ? "anonymous sign-in (no account set)" : "your Windows account (no account set)");
        }
        // A key is only for SSH and SFTP; an account a folder passes on keeps its key for those and signs in to the rest
        // with its password.
        if (!ShareConnector.TakesKey(share.Protocol)) cred.PrivateKey = cred.KeyPassphrase = null;
        if (cred.PrivateKey is { } key)
        {
            if (SshSignIn.NeedsPassphrase(key, cred.KeyPassphrase))
            {
                if (previous is { KeyPassphrase: { } known } && Secret.AreEqual(previous.PrivateKey, key)) cred.KeyPassphrase = known;
                else
                {
                    var dlg = new PasswordDialog(confirm: false, title: topic,
                        message: $"The private key for {cred.Username} is protected by a passphrase. Enter it to open {ShareLabel(share)} – it is kept in memory only.",
                        okText: "Connect") { Owner = this };
                    if (dlg.ShowDialog() != true) return null;
                    cred.KeyPassphrase = Secret.From(dlg.Password);
                    // Checked here, so a mistyped passphrase isn't kept for the next reconnect.
                    try { SshSignIn.Describe(key, cred.KeyPassphrase); }
                    catch (SshKeyException ex) { _ = Dialogs.Info(this, topic, ex.Message); return null; }
                    source += ", key passphrase entered";
                }
            }
            return (cred, source + ", private key");
        }
        if (cred.Password is null)
        {
            if (previous is { Password.Length: > 0 } p && p.Username == cred.Username && p.Domain == cred.Domain) cred.Password = p.Password;
            else
            {
                var user = string.IsNullOrEmpty(cred.Domain) || cred.Username!.Contains('\\') ? cred.Username : $"{cred.Domain}\\{cred.Username}";
                var dlg = new PasswordDialog(confirm: false, title: topic,
                    message: $"No password is stored for {user}. Enter it to open {ShareLabel(share)} – it is kept in memory only.",
                    okText: "Connect") { Owner = this };
                if (dlg.ShowDialog() != true) return null;
                cred.Password = Secret.From(dlg.Password);
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
