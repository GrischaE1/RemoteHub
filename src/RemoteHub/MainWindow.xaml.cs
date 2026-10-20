using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Services;
using RemoteHub.ViewModels;
using RemoteHub.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace RemoteHub;

public partial class MainWindow : FluentWindow
{
    private const string NodeFormat = "RemoteHub.Node";

    private readonly MainViewModel _vm;
    private readonly Dictionary<SessionBase, FrameworkElement> _views = new();
    private readonly Dictionary<SessionBase, DetachedWindow> _detached = new();
    private readonly CredentialPromptWatcher _promptWatcher = new();
    private ClipboardToast? _toast;
    private Point _dragStart;
    private NodeBase? _dragCandidate;
    private bool _forceClose;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        AppIcon.Apply(TitleIcon, 18);
        AppIcon.ApplyTo(this);
        DataContext = vm;

        var ui = vm.Data.Ui;
        Width = ui.Width;
        Height = ui.Height;
        SidebarColumn.Width = new GridLength(ui.SidebarWidth);
        ApplySidebarState();
        InitViews();
        if (ui.Maximized) WindowState = WindowState.Maximized;
        SwitchHotkeys.Current = SwitchHotkeys.Get(AppPolicy.SwitchKeys ?? ui.SwitchKeys);
        _promptWatcher.PromptShown += OnCredentialPrompt;
        vm.Conflict += () => Dispatcher.BeginInvoke(OnSettingsConflict);
        SettingsStore.ExternalChange += () => Dispatcher.BeginInvoke(OnExternalSettingsChange);
        UpdateShortcutHint();

        vm.PropertyChanged += Vm_PropertyChanged;
        vm.Sessions.CollectionChanged += Sessions_CollectionChanged;
        HookItems();

        AppChrome.UseAccentBorder(this);
        ApplicationThemeManager.Changed += (theme, _) => AppChrome.ApplyPalette(theme == ApplicationTheme.Dark);
        PreviewKeyDown += Window_PreviewKeyDown;
        DragOver += Window_DragOver;
        Drop += Window_Drop;
        Loaded += (_, _) => ApplyTheme(AppPolicy.Theme ?? vm.Data.Ui.Theme);
        Loaded += (_, _) => StartupChecks();
        Loaded += (_, _) => { UpdateRecordButton(); UpdateSendFilesButton(); };
        Loaded += (_, _) =>
        {
            // A read-only tree (policy AllowConnectionEditing = 0, docs/admx-design.md §5.4): its buttons aren't offered.
            if (Features.IsAllowed(Feature.ConnectionEditing)) return;
            NewButton.Visibility = RailNewButton.Visibility = DeleteUnusedButton.Visibility = EditConnectionButton.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) => StartAutoLock();
        Closing += Window_Closing;
    }

    // =====================================================================
    // Theme
    // =====================================================================

    private void ApplyTheme(string theme)
    {
        if (AppPolicy.Theme is null) _vm.Data.Ui.Theme = theme; // a theme set by policy isn't stored as the user's
        SystemThemeWatcher.UnWatch(this);
        if (theme == "System")
        {
            var sys = ApplicationThemeManager.GetSystemTheme();
            var dark = sys is SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion or SystemTheme.HCBlack;
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, true);
            AppChrome.ApplyPalette(dark);
            SystemThemeWatcher.Watch(this, WindowBackdropType.None, true);
        }
        else
        {
            ApplicationThemeManager.Apply(theme == "Light" ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.None, true);
            AppChrome.ApplyPalette(theme != "Light");
        }
        foreach (var w in _detached.Values) ApplicationThemeManager.Apply(w);
    }

    // =====================================================================
    // Windows Security prompt during connect -> stored password to clipboard
    // =====================================================================

    private const int ClipboardSeconds = 30;

    /// <summary>
    /// The RDP client opened the Windows Security dialog (e.g. the server refuses saved credentials).
    /// If it clearly belongs to one of our connecting sessions with a stored password, copy that password to the
    /// clipboard (the user pastes it). The dialog itself is never filled in automatically.
    /// </summary>
    private void OnCredentialPrompt(IntPtr dialog, PromptOwner owner, IReadOnlyList<string> texts)
    {
        foreach (var s in _vm.Sessions.OfType<RdpSession>())
            DiagLog.Write($"  session {s.GetHashCode():x8}: state={s.State}, passwordStored={!string.IsNullOrEmpty(s.UsedCredentials.Password)}, " +
                          $"ageSeconds={(int)(DateTime.UtcNow - s.ConnectStartedUtc).TotalSeconds}, action={s.PasswordPrompt}, " +
                          $"userShownInDialog={texts.Any(t => CredentialPromptMatcher.UserMatches(t, s.UsedCredentials))}");
        var candidates = _vm.Sessions.OfType<RdpSession>()
            .Where(s => s.State == SessionState.Connecting
                     && !string.IsNullOrEmpty(s.UsedCredentials.Password)
                     && DateTime.UtcNow - s.ConnectStartedUtc < TimeSpan.FromMinutes(3))
            .ToList();
        var pick = CredentialPromptMatcher.Pick(candidates, s => s.UsedCredentials, s => s.ConnectStartedUtc, owner, texts, DateTime.UtcNow);
        // Match against all sessions first, then honour the per-connection setting – so a dialog of a session with
        // the feature switched off can never pick up the password of another session.
        DiagLog.Write($"Credential dialog matched a session: {pick is not null}");
        // Policy AllowPasswordCopy = 0 (docs/admx-design.md §5.6): a stored password never goes to the clipboard.
        if (pick is null || pick.PasswordPrompt == PasswordPromptAction.Off || !Features.IsAllowed(Feature.PasswordCopy)) return;
        var password = pick.UsedCredentials.Password!;
        var name = pick.Connection.Name;

        CopyPasswordForPrompt(dialog, name, password, pick);
    }

    private void CopyPasswordForPrompt(IntPtr dialog, string connectionName, string password, RdpSession session)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!SecureClipboard.CopySecret(hwnd, password, TimeSpan.FromSeconds(ClipboardSeconds))) return;
        Data.AuditLog.Write(Data.AuditEvents.PasswordCopied, session.Connection, $"For the Windows password prompt (cleared after {ClipboardSeconds} s)");
        // Show it in the window that holds the session (main window, or its detached window).
        var host = _detached.TryGetValue(session, out var w) ? new System.Windows.Interop.WindowInteropHelper(w).Handle : hwnd;
        ShowPromptToast(ClipboardToast.Copied(connectionName, ClipboardSeconds, dialog, host));
    }

    private void ShowPromptToast(ClipboardToast toast)
    {
        _toast?.Close();
        _toast = toast;
        _toast.Closed += (_, _) => { if (_toast == toast) _toast = null; };
        _toast.Show();
    }

    // =====================================================================
    // Settings location (this PC / shared folder) and sync conflicts
    // =====================================================================

    private bool _syncPromptOpen;

    private void SettingsLocation_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveNow();
        var dlg = new SettingsLocationWindow(_vm.Data) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            if (dlg.OpenedData is { } opened) _vm.ReplaceData(opened, save: false);
            _vm.RefreshStorageInfo();
            Data.AuditLog.Write(Data.AuditEvents.StorageLocationChanged, null, null, SettingsStore.Describe());
        }
    }

    /// <summary>Saving found that the file changed on another device since we loaded it.</summary>
    private async void OnSettingsConflict()
    {
        if (_syncPromptOpen) return;
        _syncPromptOpen = true;
        try
        {
            var choice = await Dialogs.Ask(this, "Settings changed on another device",
                "The settings file was changed on another PC (or by another admin) after RemoteHub loaded it.\n\n" +
                "Reload: use the other version – your most recent change here is lost.\n" +
                "Keep mine: overwrite the file with the version on this PC – the other change is lost.",
                "Reload", "Keep mine");
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary) ReloadSettings();
            else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary) _vm.ForceSave();
        }
        finally { _syncPromptOpen = false; }
    }

    /// <summary>The file changed on disk (e.g. OneDrive synced a change from another PC).</summary>
    private async void OnExternalSettingsChange()
    {
        if (_syncPromptOpen) return;
        if (_vm.HasPendingSave) { _vm.SaveNow(); return; } // our unsaved change -> conflict prompt handles it
        _syncPromptOpen = true;
        try
        {
            if (await Dialogs.Confirm(this, "Settings updated",
                    "The settings file was updated on another device. Load the new version now?", "Reload"))
                ReloadSettings();
        }
        finally { _syncPromptOpen = false; }
    }

    private void ReloadSettings()
    {
        try { _vm.ReplaceData(SettingsStore.Reload(_vm.Data.Ui), save: false); }
        catch (Exception ex) { _ = Dialogs.Info(this, "Reload failed", ex.Message); }
    }

    private void SetSwitchKeys(string id)
    {
        SwitchHotkeys.Current = SwitchHotkeys.Get(id);
        if (AppPolicy.SwitchKeys is null) _vm.Data.Ui.SwitchKeys = SwitchHotkeys.Current.Id;
        foreach (var s in _vm.Sessions.OfType<RdpSession>()) s.RefreshSwitchHotkeys();
        UpdateShortcutHint();
    }

    private void UpdateShortcutHint()
    {
        var quick = Features.IsAllowed(Feature.QuickConnect);
        ShortcutHint.Text = $"Ctrl+N new connection  ·  Ctrl+F search{(quick ? " / quick connect" : "")}\n" +
                            SwitchHotkeys.Current.Hint + " – also inside a session and in full screen (use the left Alt key)";
        if (!quick) SearchBox.ToolTip = "Search folders & connections (Ctrl+F)";
    }


    // =====================================================================
    // Session hosting
    // =====================================================================

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSession))
        {
            UpdateSessionVisibility();
            UpdateRecordButton();
            UpdateSendFilesButton();
            if (_vm.SelectedSession is not null) ShowView(RemoteViewId); // a session opened or picked from anywhere
        }
        if (e.PropertyName == nameof(MainViewModel.Items)) HookItems();
        if (e.PropertyName is nameof(MainViewModel.HasTransfers) or nameof(MainViewModel.ShowTransfers)) ApplyTransfersPane();
    }

    private void HookItems()
    {
        // Favourites (links, not saved as part of the tree) above the tree itself.
        Tree.ItemsSource = new System.Windows.Data.CompositeCollection { new System.Windows.Data.CollectionContainer { Collection = _vm.FavoriteRoots }, new System.Windows.Data.CollectionContainer { Collection = _vm.Items } };
        _vm.Items.CollectionChanged -= Items_CollectionChanged;
        _vm.Items.CollectionChanged += Items_CollectionChanged;
        Items_CollectionChanged(null, null);
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs? e) =>
        EmptyHint.Visibility = _vm.Items.Count == 0 && !_vm.IsSearching ? Visibility.Visible : Visibility.Collapsed;

    private void Sessions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (SessionBase s in e.OldItems)
            {
                if (_recorders.TryGetValue(s, out var recorder)) recorder.Stop();
                if (_detached.Remove(s, out var w)) { w.ReleaseContent(); w.ForceClose(); }
                if (_views.Remove(s, out var v))
                {
                    SessionPanel.Children.Remove(v);
                    if (v is SessionView sv)
                    {
                        sv.Host.Child = null;
                        sv.Host.Dispose();
                    }
                }
            }
        UpdateSessionVisibility();
    }

    private void UpdateSessionVisibility()
    {
        var sel = _vm.SelectedSession;
        foreach (var (s, v) in _views)
            v.Visibility = s == sel ? Visibility.Visible : Visibility.Hidden;
        DetachedHint.Visibility = sel is { IsDetached: true } ? Visibility.Visible : Visibility.Collapsed;
        // Window/taskbar title shows the selected session, so the window is easy to find.
        Title = sel is null ? "RemoteHub" : $"{sel.Title} · {sel.KindLabel} – RemoteHub";
        AppTitleBar.Title = Title;
        if (sel is not null)
        {
            Tabs.ScrollIntoView(sel);
            if (_vm.SelectedGroup is { } g) GroupTabs.ScrollIntoView(g);
            if (sel.IsDetached && _detached.TryGetValue(sel, out var w)) w.Activate();
            else Dispatcher.BeginInvoke(sel.FocusSession, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// "Remote desktop" for a connection: through the RemoteHub Agent when one is installed (or the connection says so),
    /// otherwise RDP. Switches to an existing tab unless <paramref name="forceNew"/>.
    /// </summary>
    public void OpenConnection(Connection c, bool forceNew = false)
    {
        // IT fixes remote desktop to the agent but blocks the agent desktop: no remote desktop, not RDP instead (§7).
        if (AppPolicy.Connections().RemoteDesktopVia == DesktopVia.Agent && Refused(Feature.AgentDesktop)) return;
        if (AgentDesktopFor(c)) OpenAgentDesktop(c, forceNew);
        else OpenRdp(c, forceNew);
    }

    /// <summary>
    /// Remote desktop through the agent unless the connection is set to RDP (Automatic: when an agent is installed). With
    /// the agent desktop blocked by policy its setting is greyed out and doesn't count.
    /// </summary>
    private bool AgentDesktopFor(Connection c) => Features.IsAllowed(Feature.AgentDesktop) &&
        (Inheritance.ResolveOptions(c, _vm.Data).RemoteDesktopVia ?? DesktopVia.Automatic) switch
        {
            DesktopVia.Rdp => false,
            DesktopVia.Agent => true,
            _ => c.Agent is { Status: AgentStatus.Installed, Fingerprint.Length: > 0 },
        };

    /// <summary>
    /// A feature this PC's policy blocks (docs/admx-design.md P3): says so and returns true. Checked where a session or
    /// action starts, so a shortcut, a dropped file or a reconnect can't get round a hidden menu entry.
    /// </summary>
    private bool Refused(Feature feature)
    {
        if (Features.WhyNot(feature) is not { } why) return false;
        _ = Dialogs.Info(this, Features.Topic(feature), why);
        return true;
    }

    /// <summary>A remote tool through the transport the connection uses now (agent or WinRM).</summary>
    private bool Refused(RemoteTool tool, Connection c) => Refused(Features.For(tool, UsesAgent(c)));

    /// <summary>
    /// A reconnect of a tab whose feature this PC's policy blocks: the tab fails and says why, as it does for any other
    /// reason it can't connect (no dialog, no password asked).
    /// </summary>
    private static bool RefusedIn(SessionBase s, Feature feature)
    {
        if (Features.WhyNot(feature) is not { } why) return false;
        s.State = SessionState.Failed;
        s.StatusText = why;
        return true;
    }

    private void StartRdp(RdpSession s)
    {
        if (!RefusedIn(s, Feature.Rdp)) _vm.StartSession(s);
    }

    /// <summary>Opens an RDP session, or switches to the existing one.</summary>
    public void OpenRdp(Connection c, bool forceNew = false)
    {
        if (!forceNew && _vm.FindSession(c) is { } existing)
        {
            _vm.SelectedSession = existing;
            if (existing.State is SessionState.Disconnected or SessionState.Failed) StartRdp(existing);
            return;
        }
        if (Refused(Feature.Rdp)) return;

        var s = _vm.OpenSession(c);
        s.Route = () => RdpRouteAsync(s.Connection); // asked at each connect: the connection may have moved
        var view = new SessionView(s, () => StartRdp(s), () => s.Close());
        _views[s] = view;
        SessionPanel.Children.Add(view);
        s.FocusReleased += dir => SwitchFromSession(s, dir);
        s.LoggedIn += () =>
        {
            // Login done: the password is no longer needed on the clipboard.
            if (SecureClipboard.HoldsSecret) SecureClipboard.ClearIfOurs();
            _toast?.Close();
            AutoCheckPsRemoting(s);
        };
        s.MinimizeRequested += () =>
        {
            if (_detached.TryGetValue(s, out var w)) w.WindowState = WindowState.Minimized;
            else WindowState = WindowState.Minimized;
        };
        UpdateSessionVisibility();
        _vm.StartSession(s);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>AltGr is reported as Ctrl+Alt; it is "AltGr" when only the right Alt key is held.</summary>
    private static bool IsAltGr() => IsDown(VK_RMENU) && !IsDown(VK_LMENU);

    /// <summary>The switch hotkey was pressed inside a session: the control handed focus back, we switch tabs.</summary>
    private void SwitchFromSession(RdpSession from, int direction)
    {
        var keys = SwitchHotkeys.Current;
        if (IsAltGr())
        {
            // The user was typing a character (e.g. AltGr+< = "|"), not switching: pass it on to the session.
            from.SendCtrlAltKey(IsDown(keys.PrevVk) ? keys.PrevVk : keys.NextVk);
            return;
        }
        // Prefer the keys that are actually held down; fall back to the direction reported by the control.
        int delta = keys.ShiftReverses ? (IsDown(VK_SHIFT) ? -1 : 1)
                  : IsDown(keys.PrevVk) ? -1
                  : IsDown(keys.NextVk) ? 1
                  : direction < 0 ? -1 : 1;
        var wasFullScreen = from.IsFullScreen;
        if (wasFullScreen) from.LeaveFullScreen();
        _vm.SelectNextSession(delta);
        // Keep "full screen mode" when cycling through sessions.
        if (wasFullScreen && _vm.SelectedSession is RdpSession { IsConnected: true, IsDetached: false } next && next != from)
            Dispatcher.BeginInvoke(next.EnterFullScreen, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ToggleDetach(SessionBase s)
    {
        var view = _views[s];
        if (s.IsDetached && _detached.Remove(s, out var w))
        {
            w.ReleaseContent();
            w.ForceClose();
            SessionPanel.Children.Add(view);
            s.IsDetached = false;
            _vm.SelectedSession = s;
            Activate();
        }
        else if (!s.IsDetached)
        {
            SessionPanel.Children.Remove(view);
            view.Visibility = Visibility.Visible;
            var tools = new List<(SymbolRegular, string?, string, Action)>
            {
                (SymbolRegular.ArrowClockwise24, null, "Reconnect", () => Restart(s)),
                (SymbolRegular.PlugDisconnected24, null, "Disconnect", s.Disconnect),
            };
            var viaAgent = UsesAgent(s.Connection);
            if (s is RdpSession r)
            {
                tools.Add((SymbolRegular.Keyboard24, "Ctrl+Alt+Del", "Send Ctrl+Alt+Del", r.SendCtrlAltDel));
                tools.Add((SymbolRegular.FullScreenMaximize24, null, "Full screen", r.EnterFullScreen));
            }
            if (s is AgentDesktopSession d)
            {
                tools.Add((SymbolRegular.Keyboard24, "Ctrl+Alt+Del", "Send Ctrl+Alt+Del", d.SendCtrlAltDel));
                if (d.CanUsePrivacyScreen) tools.Add((SymbolRegular.EyeOff24, "Privacy screen", "Turn the privacy screen on or off (the person at the device can't see what you do)", d.TogglePrivacyScreen));
            }
            if (RecordingAllowed) tools.Add((SymbolRegular.Record24, null, "Start / stop recording this tab (the header shows REC while recording)", () => ToggleRecording(s)));
            if (s is PsSession p)
            {
                if (Features.IsAllowed(RemoteTool.FileTransfer, viaAgent))
                    tools.Add((SymbolRegular.FolderSwap24, "File transfer", "Open a file transfer tab at the current remote folder", () => OpenFileTransfer(p.Connection, p.Location, p.Target)));
                if (Features.IsAllowed(RemoteTool.Registry, viaAgent))
                    tools.Add((SymbolRegular.CubeTree24, "Registry", "Open the remote registry in a new tab", () => OpenRegistry(p.Connection, p.Target)));
                if (Features.IsAllowed(RemoteTool.Services, viaAgent))
                    tools.Add((SymbolRegular.WrenchScrewdriver24, "Services", "Open the remote services in a new tab", () => OpenServices(p.Connection, p.Target)));
                if (Features.IsAllowed(RemoteTool.Processes, viaAgent))
                    tools.Add((SymbolRegular.Pulse24, "Processes", "Open the remote processes in a new tab", () => OpenProcesses(p.Connection, p.Target)));
            }
            var win = new DetachedWindow(s, view, tools)
            {
                Width = Math.Max(800, SessionPanel.ActualWidth),
                Height = Math.Max(600, SessionPanel.ActualHeight + 80),
            };
            WindowPlacement.CenterOn(win, this);
            win.DockRequested += () => ToggleDetach(s);
            win.Activated += (_, _) => { if (_vm.SelectedSession != s) _vm.SelectedSession = s; };
            _detached[s] = win;
            s.IsDetached = true;
            win.Show();
            ApplicationThemeManager.Apply(win);
        }
        UpdateSessionVisibility();
    }

    private SessionBase? SessionOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SessionBase ?? _vm.SelectedSession;

    /// <summary>Reconnect button / menu: works for RDP and PowerShell tabs.</summary>
    private void Restart(SessionBase s)
    {
        if (s is RdpSession r) StartRdp(r);
        else if (s is AgentDesktopSession d) ConnectAgentDesktop(d);
        else if (s is PsSession p) ConnectPs(p);
        else if (s is FileBrowserSession f) ConnectFileBrowser(f);
        else if (s is ShareSession sh) ConnectShare(sh);
        else if (s is RemoteToolSession t) ConnectTool(t);
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) Restart(s); }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSession is { } s && await ConfirmSignOut(s)) s.Disconnect();
    }
    private void CtrlAltDel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSession is RdpSession r) r.SendCtrlAltDel();
        else if (_vm.SelectedSession is AgentDesktopSession d) d.SendCtrlAltDel();
    }
    private void FullScreen_Click(object sender, RoutedEventArgs e) => (_vm.SelectedSession as RdpSession)?.EnterFullScreen();
    private void ToolbarSendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSession is PsSession p) OpenFileTransfer(p.Connection, p.Location, p.Target);
    }

    /// <summary>The toolbar's File transfer button: only for a PowerShell tab whose connection's transport allows it (as OpenFileTransfer checks).</summary>
    private void UpdateSendFilesButton() =>
        SendFilesButton.Visibility = _vm.SelectedSession is PsSession p && Features.IsAllowed(RemoteTool.FileTransfer, UsesAgent(p.Connection))
            ? Visibility.Visible : Visibility.Collapsed;
    private void Detach_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) ToggleDetach(s); }
    private void ShowDetached_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s && _detached.TryGetValue(s, out var w)) w.Activate(); }
    private void EditSessionConnection_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) EditNode(s.Connection); }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (SessionOf(sender) is { } s) CloseTab(s);
        e.Handled = true;
    }

    /// <summary>
    /// Ending an agent desktop whose session RemoteHub signed in automatically, with the end action "sign out": unsaved
    /// work in that session is lost – asked first (design §9.2).
    /// </summary>
    private async Task<bool> ConfirmSignOut(SessionBase s) =>
        s is not AgentDesktopSession { SignsOutWhenEnded: true }
        || await Dialogs.Confirm(this, "Sign out on the device?",
            $"RemoteHub signed in on {s.Connection.Name} automatically. When you disconnect, that session is signed out and unsaved work in it is lost.\n\nDisconnect?",
            "Disconnect");

    private async void CloseTab(SessionBase s)
    {
        if (await ConfirmSignOut(s)) s.Close();
    }

    private void Tabs_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is SessionBase s)
        {
            CloseTab(s);
            e.Handled = true;
        }
    }

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DependencyObject d && FindDescendant<ScrollViewer>(d) is { } sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var sw = SwitchHotkeys.Current;
        var altGr = Keyboard.IsKeyDown(Key.RightAlt) && !Keyboard.IsKeyDown(Key.LeftAlt);
        if (ctrl && alt && !altGr && (key == sw.Prev || key == sw.Next))
        {
            ShowView(RemoteViewId);
            _vm.SelectNextSession(sw.ShiftReverses ? (shift ? -1 : 1) : key == sw.Prev ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (HandleViewKeys(e, ctrl)) return;
        if (ctrl && e.Key == Key.N) { if (shift) NewFolder_Click(this, e); else NewConnection_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.F && FocusedFilterable() is { } view) { view.FocusFilter(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F)
        {
            if (_vm.Data.Ui.SidebarCollapsed) ToggleSidebar();
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (ctrl && e.Key == Key.B) { ToggleSidebar(); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { OpenSettings(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab) { _vm.SelectNextSession(shift ? -1 : 1); e.Handled = true; }
        else if (ctrl && e.Key == Key.W && _vm.SelectedSession is { } s) { CloseTab(s); e.Handled = true; }
    }

    /// <summary>The file pane, registry or list view the keyboard focus is in, if any: Ctrl+F there goes to its filter.</summary>
    private static IFilterableView? FocusedFilterable()
    {
        for (var d = Keyboard.FocusedElement as DependencyObject; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is IFilterableView v) return v;
        return null;
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is NetworkShare share)
        {
            switch (e.Key)
            {
                case Key.Enter: OpenShare(share); e.Handled = true; break;
                case Key.F2: EditShare(share); e.Handled = true; break;
                case Key.Delete: _ = DeleteShare(share); e.Handled = true; break;
            }
            return;
        }
        // Del on a favourite only unpins it: the connection itself stays.
        if (Tree.SelectedItem is FavoriteLink link && e.Key == Key.Delete) { _vm.SetFavorite(link.Target, false); e.Handled = true; return; }
        if (_vm.SelectedNode is not { } n) return;
        switch (e.Key)
        {
            case Key.Enter when n is Connection c: OpenConnection(c); e.Handled = true; break;
            case Key.F2: EditNode(n); e.Handled = true; break;
            case Key.Delete: _ = DeleteNode(n); e.Handled = true; break;
        }
    }

    // =====================================================================
    // Search / quick connect
    // =====================================================================

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _vm.SearchResults.Count > 0)
        {
            SearchList.SelectedIndex = 0;
            (SearchList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.SearchText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.IsSearching)
        {
            if (_vm.SearchResults.FirstOrDefault() is { } first) ActivateHit(first);
            else QuickConnect(_vm.SearchText.Trim());
            e.Handled = true;
        }
    }

    private void QuickConnect(string address)
    {
        if (address.Contains(' ') || address.Length == 0 || Refused(Feature.QuickConnect)) return;
        var (host, port) = RdpFileParser.SplitAddress(address);
        // Ad-hoc connection: uses global defaults, not saved.
        OpenConnection(new Connection { Name = host, Host = host, Port = port });
        _vm.SearchText = "";
    }

    private void SearchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is SearchHit hit) ActivateHit(hit);
    }

    private void SearchList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchList.SelectedItem is SearchHit hit) { ActivateHit(hit); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.SearchText = ""; Tree.Focus(); e.Handled = true; }
    }

    /// <summary>Connection or share: open it. Folder: jump to it in the list.</summary>
    private void ActivateHit(SearchHit hit)
    {
        if (hit.Node is Connection c) OpenConnection(c);
        else if (hit.Node is NetworkShare s) OpenShare(s);
        else RevealInTree(hit.Node);
    }

    private void SearchOpen_Click(object sender, RoutedEventArgs e)
    {
        if (SearchList.SelectedItem is SearchHit hit) ActivateHit(hit);
    }

    private void SearchReveal_Click(object sender, RoutedEventArgs e)
    {
        if (SearchList.SelectedItem is SearchHit hit) RevealInTree(hit.Node);
    }

    private void SearchList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is { } item) item.IsSelected = true;
    }

    /// <summary>Ends the search, expands the parent folders and selects + scrolls to the item in the tree.</summary>
    private void RevealInTree(NodeBase node)
    {
        _vm.SearchText = "";
        foreach (var a in TreeLayout.Containers(node))
        {
            if (a is Folder f) f.IsExpanded = true;
            if (a is Connection jumpHost) jumpHost.IsExpanded = true;
        }
        node.IsSelected = true;
        Dispatcher.BeginInvoke(() =>
        {
            ItemsControl parent = Tree;
            foreach (var level in TreeLayout.Containers(node).Reverse().Append(node))
            {
                parent.UpdateLayout();
                if (parent.ItemContainerGenerator.ContainerFromItem(level) is not TreeViewItem tvi)
                {
                    // The tree only builds the rows on screen: scroll the item in first so it gets one.
                    var index = parent.Items.IndexOf(level);
                    if (index < 0 || FindDescendant<VirtualizingStackPanel>(parent) is not { } panel) return;
                    panel.BringIndexIntoViewPublic(index);
                    parent.UpdateLayout();
                    if (parent.ItemContainerGenerator.ContainerFromItem(level) is not TreeViewItem shown) return;
                    tvi = shown;
                }
                tvi.BringIntoView();
                parent = tvi;
            }
            ((TreeViewItem)parent).Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // =====================================================================
    // Tree: selection, context menu, editing
    // =====================================================================

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue switch
        {
            NetworkShare { Server: not null } => null, // a server's share isn't a node of its own
            FavoriteLink link => link.Target,     // a favourite acts on the connection itself
            _ => e.NewValue as NodeBase,
        };

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        switch (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext)
        {
            case Connection c: OpenConnection(c); e.Handled = true; break;
            case FavoriteLink link: OpenConnection(link.Target); e.Handled = true; break;
            case NetworkShare s: OpenShare(s); e.Handled = true; break;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is { } tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
        else if (Tree.SelectedItem is FavoriteLink link)
        {
            link.IsSelected = false; // the link, not its connection, is what the tree has selected
            _vm.SelectedNode = null;
        }
        else if (_vm.SelectedNode is { } n)
        {
            n.IsSelected = false;
            _vm.SelectedNode = null;
        }
    }

    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        var n = _vm.SelectedNode;
        Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesMenu(n);
        // What this PC's policy blocks isn't offered (docs/admx-design.md §4.2).
        var rdpOn = Features.IsAllowed(Feature.Rdp);
        var agentDesktopOn = Features.IsAllowed(Feature.AgentDesktop);
        var agentDefault = n is Connection dc && AgentDesktopFor(dc);
        CmConnect.Visibility = V(n is Connection && (agentDefault ? agentDesktopOn : rdpOn));
        CmConnectNew.Visibility = V(n is Connection && rdpOn);
        CmConnectNew.Header = agentDefault ? "Open an RDP session" : "Open additional session";
        // The other way to the screen: RDP when the agent is the default, the agent when it is installed but RDP is the default.
        CmDesktopOther.Visibility = V(n is Connection oc && (agentDefault ? rdpOn : agentDesktopOn && oc.Agent is { Status: AgentStatus.Installed }));
        CmDesktopOther.Header = agentDefault ? "Remote desktop via RDP" : "Remote desktop via RemoteHub Agent";
        CmDesktopOther.Icon = new SymbolIcon(agentDefault ? SymbolRegular.Desktop24 : SymbolRegular.DesktopCursor24);
        CmAddShare.ToolTip = AddShareTip(n as Connection);
        // A read-only tree (policy AllowConnectionEditing = 0, §5.4): nothing that creates, changes, moves or deletes an item.
        var editable = Features.IsAllowed(Feature.ConnectionEditing);
        // With jump hosts blocked only "out of" a jump host is left to offer.
        CmMoveUnder.Visibility = V(editable && n is Connection or Folder && (Features.IsAllowed(Feature.JumpHosts) || n.Parent is Connection));
        if (n is Connection or Folder) FillMoveUnder(CmMoveUnder, n);
        CmConnectAll.Visibility = V(n is Folder && (rdpOn || agentDesktopOn));
        CmEdit.Visibility = CmDuplicate.Visibility = CmDelete.Visibility = V(editable && n is not null);
        CmNew.Visibility = CmNewSep.Visibility = V(editable);
        CmHistory.Visibility = V(n is Connection);
        CmSaveRdp.Visibility = V(n is not null && Features.IsAllowed(Feature.RdpFileExport));
        CmSaveRdp.Header = n is Folder ? "Export folder as .rdp files…" : "Save as .rdp file…";
        CmNewShare.Visibility = V(Features.IsAllowed(Feature.NetworkShares));
        CmFavorite.Visibility = V(n is Connection);
        if (n is Connection fav) CmFavorite.Header = _vm.IsFavorite(fav) ? "Remove from favourites" : "Add to favourites";
        // On a favourite the connection is moved, duplicated or deleted from its folder, not from here.
        if (Tree.SelectedItem is FavoriteLink) CmMoveUnder.Visibility = CmDuplicate.Visibility = CmDelete.Visibility = Visibility.Collapsed;

        var c = n as Connection;
        var viaAgent = c is not null && UsesAgent(c);
        var psOn = c is not null && Features.AnyTool(viaAgent); // manual test always offered; the setting only controls the automatic check
        var st = c is null ? PsRemotingState.Unknown : PsState(c);
        var name = viaAgent ? "RemoteHub Agent" : "PowerShell remoting";
        var available = psOn && st == PsRemotingState.Available;
        CmPsSep.Visibility = V(c is not null);
        CmPs.Visibility = V(available && Features.IsAllowed(RemoteTool.PowerShell, viaAgent));
        CmSendFiles.Visibility = V(available && Features.IsAllowed(RemoteTool.FileTransfer, viaAgent));
        CmRegistry.Visibility = V(available && Features.IsAllowed(RemoteTool.Registry, viaAgent));
        CmServices.Visibility = V(available && Features.IsAllowed(RemoteTool.Services, viaAgent));
        CmProcesses.Visibility = V(available && Features.IsAllowed(RemoteTool.Processes, viaAgent));
        CmPsTest.Visibility = V(psOn && st != PsRemotingState.Available);
        CmPsTest.Header = c?.PsChecking == true ? $"Testing {name}…"
                        : st == PsRemotingState.Unavailable ? $"Test {name} again" : $"Test {name}";
        CmPsTest.IsEnabled = c?.PsChecking != true;
        CmPsTest.ToolTip = st == PsRemotingState.Unavailable ? $"Last result: {c!.PsRemoting!.Message}" : null;
        CmPsSetup.Visibility = V(psOn && st == PsRemotingState.Unavailable && !viaAgent && Features.IsAllowed(Feature.RemotingSetup));
        CmAgent.Visibility = V(c is not null && Features.IsAllowed(Feature.AgentInstall));
        CmAddShare.Visibility = V(c is not null && editable && Features.IsAllowed(Feature.NetworkShares));
        CmAgent.Header = c?.Agent is { Status: AgentStatus.Installed } ? "Update RemoteHub Agent…" : "Install RemoteHub Agent…";
        CmCredentialProvider.Visibility = V(c is not null && CredentialProviderOffered(c));
        CmAgentUninstall.Visibility = V(c?.Agent is { Status: AgentStatus.Installed } && Features.IsAllowed(Feature.AgentInstall));
        if (c is not null) CmCredentialProvider.Header = CredentialProviderHeader(c);

        // The server's own shares in Tools, as in the tab menus (filled fresh each time).
        foreach (var old in CmTools.Items.OfType<MenuItem>().Where(i => Equals(i.Tag, "share")).ToList()) CmTools.Items.Remove(old);
        if (c is not null)
        {
            var at = CmTools.Items.IndexOf(CmSharesSep) + 1;
            foreach (var share in c.Shares.Where(s => Features.IsAllowed(s.Protocol)))
            {
                var item = ShareEntry(share);
                item.Tag = "share";
                CmTools.Items.Insert(at++, item);
            }
        }
        // A group shows only when something in it does.
        CmSharesSep.Visibility = V(CmAddShare.Visibility == Visibility.Visible &&
                                   new[] { CmPs, CmSendFiles, CmRegistry, CmServices, CmProcesses, CmPsTest }.Any(i => i.Visibility == Visibility.Visible));
        CmTools.Visibility = V(GroupHasItems(CmTools));
        CmManage.Visibility = V(GroupHasItems(CmManage));
        CmMore.Visibility = V(GroupHasItems(CmMore));
        // Separators only between groups that show: with RDP and the agent desktop blocked the menu may start with Tools or Edit.
        var connectShown = new[] { CmConnect, CmConnectNew, CmDesktopOther, CmConnectAll }.Any(i => i.Visibility == Visibility.Visible);
        var toolsShown = CmTools.Visibility == Visibility.Visible || CmManage.Visibility == Visibility.Visible;
        CmPsSep.Visibility = V(connectShown && toolsShown);
        // …and only with something after it: Edit and the rest (a read-only tree hides them), or More.
        CmSep1.Visibility = V(n is not null && (connectShown || toolsShown || CmNotes.Visibility == Visibility.Visible)
                              && (CmEdit.Visibility == Visibility.Visible || CmMore.Visibility == Visibility.Visible));
        // Nothing left to offer (a read-only tree, right-click on empty space): no empty menu.
        if (!TreeMenu.Items.OfType<MenuItem>().Any(i => i.Visibility == Visibility.Visible)) TreeMenu.IsOpen = false;
    }

    private static bool GroupHasItems(MenuItem group) => group.Items.OfType<MenuItem>().Any(i => i.Visibility == Visibility.Visible);

    private void CmAgent_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _ = InstallAgent(c); }
    private void CmCredentialProvider_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _ = ChangeCredentialProvider(c); }
    private void CmAgentUninstall_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _ = UninstallAgent(c); }
    private void CmFavorite_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _vm.SetFavorite(c, !_vm.IsFavorite(c)); }

    private void CmPsSetup_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _ = ShowRemotingSetup(c); }

    private void CmPs_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenPowerShell(c); }
    private void CmSendFiles_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenFileTransfer(c, null, null); }
    private void CmRegistry_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenRegistry(c, null); }
    private void CmServices_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenServices(c, null); }
    private void CmProcesses_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenProcesses(c, null); }
    private void CmPsTest_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) _ = TestPsRemoting(c, interactive: true); }

    private static MenuItem MenuEntry(string header, SymbolRegular icon, Action action, bool enabled = true, string? tip = null)
    {
        var mi = new MenuItem { Header = header, Icon = new SymbolIcon(icon), IsEnabled = enabled, ToolTip = tip };
        ToolTipService.SetShowOnDisabled(mi, true); // a switched-off entry says why
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>A group in a context menu: opens on hover (like "Move under"); its entries are added by the caller.</summary>
    private static MenuItem Submenu(string header, SymbolRegular icon) => new() { Header = header, Icon = new SymbolIcon(icon) };

    /// <summary>Credentials already in use by a remoting tab of this connection (no second password prompt).</summary>
    private PsTarget? KnownTarget(Connection c) => _vm.Sessions.Where(x => x.Connection == c)
        .Select(x => x switch { RemoteToolSession r => r.Target, PsSession p => p.Target, _ => null })
        .FirstOrDefault(t => t is not null && (t.Agent is not null) == UsesAgent(c)); // same way (agent / WinRM) as the connection uses now

    /// <summary>Tabs that can be opened for a connection: remote desktop, and PowerShell / file transfer / registry (or the remoting test).</summary>
    private void AddConnectionTools(ItemsControl cm, Connection c, SessionBase? from)
    {
        var rdp = _vm.FindSession(c);
        var agentDesktop = _vm.FindAgentDesktop(c);
        var viaAgent = AgentDesktopFor(c);
        var jumped = c.JumpHost is not null;
        // What this PC's policy blocks isn't offered (docs/admx-design.md §4.2); a group left empty isn't shown.
        var rdpOn = Features.IsAllowed(Feature.Rdp);
        if (viaAgent)
        {
            cm.Items.Add(MenuEntry(agentDesktop is null ? "Remote desktop" : "Show remote desktop", SymbolRegular.DesktopCursor24, () => OpenAgentDesktop(c),
                tip: jumped ? $"The device's screen through the RemoteHub Agent, reached through {c.JumpHost!.Name}" : "The device's screen through the RemoteHub Agent (no RDP needed)"));
            if (rdpOn) cm.Items.Add(MenuEntry(rdp is null ? "Remote desktop via RDP" : "Show RDP session", SymbolRegular.Desktop24, () => OpenRdp(c)));
        }
        else
        {
            if (rdpOn) cm.Items.Add(MenuEntry(rdp is null ? "Remote desktop" : "Show remote desktop", SymbolRegular.Desktop24, () => OpenRdp(c)));
            if (rdpOn && rdp is not null) cm.Items.Add(MenuEntry("Additional remote desktop session", SymbolRegular.WindowNew24, () => OpenRdp(c, forceNew: true)));
            if (c.Agent is { Status: AgentStatus.Installed } && Features.IsAllowed(Feature.AgentDesktop))
                cm.Items.Add(MenuEntry(agentDesktop is null ? "Remote desktop via RemoteHub Agent" : "Show agent desktop", SymbolRegular.DesktopCursor24, () => OpenAgentDesktop(c),
                    tip: "The device's screen through the RemoteHub Agent – for devices where RDP is off"));
        }
        var st = PsState(c);
        var toolsViaAgent = UsesAgent(c);
        var name = toolsViaAgent ? "RemoteHub Agent" : "PowerShell remoting";
        var ps = from as PsSession;

        // Groups open on hover, as in the connection tree's menu: tools for the device, and setting it up.
        var tools = Submenu("Tools", SymbolRegular.Toolbox24);
        void Tool(RemoteTool tool, string header, SymbolRegular icon, Action open)
        {
            if (Features.IsAllowed(tool, toolsViaAgent)) tools.Items.Add(MenuEntry(header, icon, open));
        }
        // No tool allowed over the connection's transport: nothing to test either.
        if (Features.AnyTool(toolsViaAgent) && st == PsRemotingState.Available)
        {
            Tool(RemoteTool.PowerShell, "PowerShell", SymbolRegular.WindowConsole20, () => OpenPowerShell(c));
            Tool(RemoteTool.FileTransfer, "File transfer", SymbolRegular.FolderSwap24, () => OpenFileTransfer(c, ps?.Location, ps?.Target ?? KnownTarget(c)));
            Tool(RemoteTool.Registry, "Registry", SymbolRegular.CubeTree24, () => OpenRegistry(c, KnownTarget(c)));
            Tool(RemoteTool.Services, "Services", SymbolRegular.WrenchScrewdriver24, () => OpenServices(c, KnownTarget(c)));
            Tool(RemoteTool.Processes, "Processes", SymbolRegular.Pulse24, () => OpenProcesses(c, KnownTarget(c)));
        }
        else if (Features.AnyTool(toolsViaAgent))
            tools.Items.Add(MenuEntry(c.PsChecking ? $"Testing {name}…" : st == PsRemotingState.Unavailable ? $"Test {name} again" : $"Test {name}",
                SymbolRegular.ArrowSync24, () => _ = TestPsRemoting(c, interactive: true), !c.PsChecking,
                st == PsRemotingState.Unavailable ? $"Last result: {c.PsRemoting!.Message}" : null));
        if (Features.IsAllowed(Feature.NetworkShares))
        {
            // Network shares need neither remoting nor the agent (behind a jump host only its tunnel; FTP can't go through).
            var shares = c.Shares.Where(s => Features.IsAllowed(s.Protocol)).ToList();
            var canAdd = Features.IsAllowed(Feature.ConnectionEditing); // a read-only tree takes no new share
            if (tools.Items.Count > 0 && (shares.Count > 0 || canAdd)) tools.Items.Add(new Separator());
            foreach (var share in shares) tools.Items.Add(ShareEntry(share));
            if (canAdd) tools.Items.Add(MenuEntry("Add network share…", SymbolRegular.FolderLink24, () => AddShare(c), tip: jumped ? AddShareTip(c) : null));
        }
        if (tools.Items.Count > 0) cm.Items.Add(tools);

        var manage = Submenu("RemoteHub Agent", SymbolRegular.Shield24);
        if (st == PsRemotingState.Unavailable && !toolsViaAgent && Features.AnyTool(viaAgent: false) && Features.IsAllowed(Feature.RemotingSetup))
            manage.Items.Add(MenuEntry("Set up PowerShell remoting on the target…", SymbolRegular.Code24, () => _ = ShowRemotingSetup(c)));
        if (Features.IsAllowed(Feature.AgentInstall))
            manage.Items.Add(MenuEntry(c.Agent is { Status: AgentStatus.Installed } ? "Update RemoteHub Agent…" : "Install RemoteHub Agent…",
                SymbolRegular.Shield24, () => _ = InstallAgent(c)));
        if (CredentialProviderOffered(c))
            manage.Items.Add(MenuEntry(CredentialProviderHeader(c), SymbolRegular.Key24, () => _ = ChangeCredentialProvider(c),
                tip: "Lets the RemoteHub Agent sign in automatically on the device's sign-in screen"));
        if (c.Agent is { Status: AgentStatus.Installed } && Features.IsAllowed(Feature.AgentInstall))
            manage.Items.Add(MenuEntry("Uninstall RemoteHub Agent…", SymbolRegular.Delete24, () => _ = UninstallAgent(c),
                tip: "Removes the agent from the device; the connection goes back to RDP and PowerShell remoting"));
        if (manage.Items.Count > 0) cm.Items.Add(manage);
    }


    /// <summary>One of the server's network shares: opens it in a file tab (disabled, with the reason, where it can't).</summary>
    private MenuItem ShareEntry(NetworkShare share) =>
        MenuEntry($"Share {share.Name}", Enum.Parse<SymbolRegular>(share.DisplaySymbol), () => OpenShare(share), WhyNotOpen(share) is null,
            WhyNotOpen(share) ?? $"{share.ProtocolLabel} · {share.Address}");

    private static void ShowMenu(ContextMenu cm, UIElement target, PlacementMode placement)
    {
        cm.PlacementTarget = target;
        cm.Placement = placement;
        cm.IsOpen = true;
    }

    /// <summary>"+" next to the tabs: open another tab for the selected connection.</summary>
    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedGroup is not { } g) return;
        var cm = new ContextMenu();
        AddConnectionTools(cm, g.Connection, _vm.SelectedSession);
        if (cm.Items.Count == 0) // every way in is blocked by this PC's policy: say so instead of an empty menu
            cm.Items.Add(MenuEntry("Nothing else can be opened", SymbolRegular.Info24, () => { }, enabled: false, tip: Features.TurnedOff));
        ShowMenu(cm, AddTabButton, PlacementMode.Bottom);
    }

    private async void CloseGroup(SessionGroup g)
    {
        // An agent desktop in it that would sign the device's session out: asked first, as for a single tab.
        if (g.Sessions.FirstOrDefault(s => s is AgentDesktopSession { SignsOutWhenEnded: true }) is { } signsOut && !await ConfirmSignOut(signsOut)) return;
        foreach (var s in g.Sessions.ToList()) s.Close();
    }

    private void GroupClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionGroup g) CloseGroup(g);
        e.Handled = true;
    }

    private void GroupTabs_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is SessionGroup g)
        {
            CloseGroup(g);
            e.Handled = true;
        }
    }

    /// <summary>Right-click on a connection tab: open its tools, edit, close all.</summary>
    private void GroupTabs_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is not { DataContext: SessionGroup g } item) return;
        var cm = new ContextMenu();
        AddConnectionTools(cm, g.Connection, null);
        if (cm.Items.Count > 0) cm.Items.Add(new Separator()); // nothing above when policy blocks every way in
        if (Features.IsAllowed(Feature.ConnectionEditing))
        {
            cm.Items.Add(MenuEntry("Edit connection…", SymbolRegular.Edit24, () => EditNode(g.Connection)));
            cm.Items.Add(new Separator());
        }
        cm.Items.Add(MenuEntry(g.Sessions.Count > 1 ? $"Close all {g.Sessions.Count} tabs" : "Close", SymbolRegular.Dismiss24, () => CloseGroup(g)));
        ShowMenu(cm, item, PlacementMode.MousePoint);
        e.Handled = true;
    }

    /// <summary>Right-click on a tab: session actions plus the other tools for its connection.</summary>
    private void Tabs_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource) is not { DataContext: SessionBase s } item) return;
        var c = s.Connection;
        var cm = new ContextMenu();
        void Add(string header, SymbolRegular icon, Action action, bool enabled = true, string? tip = null) => cm.Items.Add(MenuEntry(header, icon, action, enabled, tip));
        AddConnectionTools(cm, c, s);
        if (cm.Items.Count > 0) cm.Items.Add(new Separator());
        Add("Reconnect", SymbolRegular.ArrowClockwise24, () => Restart(s));
        Add("Disconnect", SymbolRegular.PlugDisconnected24, async () => { if (await ConfirmSignOut(s)) s.Disconnect(); }, s.State is SessionState.Connected or SessionState.Connecting);
        if (s is RdpSession r)
        {
            Add("Send Ctrl+Alt+Del", SymbolRegular.Keyboard24, r.SendCtrlAltDel, r.IsConnected);
            Add("Full screen", SymbolRegular.FullScreenMaximize24, r.EnterFullScreen, r.IsConnected);
        }
        if (s is AgentDesktopSession d)
        {
            Add("Send Ctrl+Alt+Del", SymbolRegular.Keyboard24, d.SendCtrlAltDel, d.IsConnected && !d.ViewOnly);
            if (d.CanUsePrivacyScreen)
                Add(d.Privacy.Screen == DesktopPrivacyStyle.Off ? "Turn on the privacy screen" : "Turn off the privacy screen", d.Privacy.Screen == DesktopPrivacyStyle.Off ? SymbolRegular.EyeOff24 : SymbolRegular.Eye24, d.TogglePrivacyScreen, d.IsConnected && !d.ViewOnly);
        }
        if (RecordingAllowed)
            Add(s.IsRecording ? "Stop recording" : "Record to a video file…", s.IsRecording ? SymbolRegular.RecordStop24 : SymbolRegular.Record24, () => ToggleRecording(s));
        Add(s.IsDetached ? "Dock back" : "Detach into its own window", SymbolRegular.WindowNew24, () => ToggleDetach(s));
        if (Features.IsAllowed(Feature.ConnectionEditing)) Add("Edit connection…", SymbolRegular.Edit24, () => EditNode(c));
        cm.Items.Add(new Separator());
        Add("Close", SymbolRegular.Dismiss24, s.Close);
        cm.PlacementTarget = item;
        cm.Placement = PlacementMode.MousePoint;
        cm.IsOpen = true;
        e.Handled = true;
    }

    // =====================================================================
    // PowerShell remoting: check, PowerShell tab, file transfer
    // =====================================================================

    private bool PsEnabled(Connection c) => Inheritance.ResolveOptions(c, _vm.Data).PsRemoting == true;

    /// <summary>The connection goes through the RemoteHub Agent instead of WinRM.</summary>
    private bool UsesAgent(Connection c) => Inheritance.UsesAgent(c, _vm.Data);

    /// <summary>Last check result – only if it was made the way the connection works now (agent or WinRM).</summary>
    private PsRemotingState PsState(Connection c) =>
        c.PsRemoting is { } st && st.ViaAgent == UsesAgent(c) ? st.State : PsRemotingState.Unknown;

    private Brush AccentFor(Connection c) =>
        ColorTags.Brush(Inheritance.ResolveOptions(c, _vm.Data).ColorTag) ?? ColorTags.Frozen(AppChrome.Accent);

    /// <summary>
    /// Host, protocol and the connection's (inherited) credentials. Without a stored password the user is asked;
    /// that password stays in memory only. Returns null when the user cancels.
    /// </summary>
    private (PsTarget Target, string Source)? BuildTarget(Connection c, PsTarget? previous, bool? viaAgent = null)
    {
        var agent = viaAgent ?? UsesAgent(c);
        var (resolved, source) = Inheritance.ResolveCredentials(c, _vm.Data);
        var cred = resolved.Clone();
        if (agent && AgentEndpointFor(c) is null) return null;
        if (!agent && !TryRoute(c, quiet: false, out _)) return null; // WinRM through a jump host: its password now, if needed
        if (agent && cred.IsEmpty)
        {
            _ = Dialogs.Info(this, "RemoteHub Agent", $"No credentials are set for {c.Name}. The RemoteHub Agent signs in with a Windows account " +
                                                     "of the device (an administrator there) – set credentials on the connection or its folder.");
            return null;
        }
        if (cred.IsEmpty) source = "your Windows account (no credentials set)";
        if (PsRemoting.NeedsPassword(cred))
        {
            if (previous?.Credentials is { Password.Length: > 0 } p && p.Username == cred.Username && p.Domain == cred.Domain)
                cred.Password = p.Password;
            else
            {
                var dlg = new PasswordDialog(confirm: false, title: agent ? "RemoteHub Agent" : "PowerShell remoting",
                    message: $"No password is stored for {PsRemoting.UserName(cred, c.Host)}. Enter it to connect to {c.Name} – it is kept in memory only.",
                    okText: "Connect") { Owner = this };
                if (dlg.ShowDialog() != true) return null;
                cred.Password = dlg.Password;
                source += ", password entered";
            }
        }
        if (previous?.Credentials is { } same && same.Username == cred.Username && same.Domain == cred.Domain && same.Password == cred.Password) cred = same;
        return (MakeTarget(c, cred, agent)!, source);
    }

    /// <summary>The target for given credentials: WinRM (through the jump host, if any), or the agent with its pinned certificate.</summary>
    private PsTarget? MakeTarget(Connection c, CredentialSet cred, bool? viaAgent = null)
    {
        var agent = viaAgent ?? UsesAgent(c);
        var opt = Inheritance.ResolveOptions(c, _vm.Data);
        if (!agent) return TryRoute(c, quiet: true, out var via) ? new PsTarget(c.Host.Trim(), opt.PsUseSsl == true, cred, Via: via) : null;
        return AgentEndpointFor(c, quiet: true) is { } ep ? new PsTarget(c.Host.Trim(), false, cred, ep) : null;
    }

    /// <summary>The device's agent – through its jump host, if it is under one (null: none, or the jump host can't be used now).</summary>
    private AgentEndpoint? AgentEndpointFor(Connection c, bool quiet = false)
    {
        if (c.Agent is { Fingerprint.Length: > 0 } d) return EndpointOf(c, d, quiet);
        if (!quiet)
            _ = Dialogs.Info(this, "RemoteHub Agent", c.JumpHost is { } j
                ? $"{c.Name} is reached through {j.Name}, which only works with a RemoteHub Agent on {c.Name} – none is installed for it yet. " +
                  "Right-click the connection → Install RemoteHub Agent… (the installer files can be saved and installed by hand)."
                : $"{c.Name} is set to connect through the RemoteHub Agent, but no agent is installed for it yet. " +
                  "Right-click the connection → Install RemoteHub Agent (or switch it back to PowerShell remoting in its settings).");
        return null;
    }

    private void SetPsStatus(Connection c, PsRemotingState state, string? message)
    {
        var old = c.PsRemoting;
        if (old is not null && old.State == state && old.Message == message && DateTime.UtcNow - old.CheckedUtc < TimeSpan.FromMinutes(1)) return;
        var viaAgent = UsesAgent(c);
        c.PsRemoting = new PsRemotingStatus { State = state, Message = message, CheckedUtc = DateTime.UtcNow, UploadFolder = old?.UploadFolder, RegistryPath = old?.RegistryPath, ViaAgent = viaAgent };
        if (viaAgent && state == PsRemotingState.Available && c.Agent is { Status: AgentStatus.NotInstalled } a)
        {
            a.Status = AgentStatus.Installed; // first successful connection confirms an install done by hand
            a.InstalledUtc = DateTime.UtcNow;
            DiagLog.Write("RemoteHub Agent: first connection succeeded – marked as installed");
        }
        _vm.RequestSave();
    }

    /// <summary>First RDP login of a connection with the check switched on: test PowerShell remoting in the background.</summary>
    private void AutoCheckPsRemoting(RdpSession s)
    {
        var c = s.Connection;
        if (!(PsEnabled(c) || UsesAgent(c)) || c.PsChecking || PsState(c) != PsRemotingState.Unknown) return;
        if (!Features.AnyTool(UsesAgent(c))) return; // nothing the result would offer
        if (UsesAgent(c) && c.Agent is null) return;
        var cred = s.UsedCredentials.Clone();
        if (PsRemoting.NeedsPassword(cred)) return; // would have to ask – the user can start the test from the menu
        _ = TestPsRemoting(c, interactive: false, cred);
    }

    private async Task TestPsRemoting(Connection c, bool interactive, CredentialSet? credentials = null)
    {
        if (c.PsChecking) return;
        // The test only decides which tools are offered: none allowed over this transport, nothing to test.
        if (!Features.AnyTool(UsesAgent(c)))
        {
            var main = UsesAgent(c) ? Feature.Agent : Feature.PowerShellRemoting;
            if (interactive && !Refused(main))
                _ = Dialogs.Info(this, Features.Topic(main), $"PowerShell, file transfer, registry, services and processes through {(UsesAgent(c) ? "the RemoteHub Agent" : "PowerShell remoting")} " +
                                                             "are turned off by your organisation – see Settings → Policies.");
            return;
        }
        PsTarget target;
        if (credentials is not null)
        {
            if (MakeTarget(c, credentials) is not { } made) return;
            target = made;
        }
        else if (BuildTarget(c, null) is { } t) target = t.Target;
        else return;

        c.PsChecking = true;
        DiagLog.Write($"PowerShell remoting check started ({(target.Agent is not null ? "agent" : target.UseSsl ? "HTTPS" : "HTTP")}, interactive={interactive})");
        PsRemotingStatus st;
        try { st = await PsRemoting.TestAsync(target); }
        finally { c.PsChecking = false; }
        DiagLog.Write($"PowerShell remoting check: {st.State}");
        SetPsStatus(c, st.State, st.Message);
        if (!interactive) return;
        if (target.Agent is not null)
        {
            if (st.State == PsRemotingState.Available)
                await Dialogs.Info(this, "RemoteHub Agent", $"The RemoteHub Agent works ({st.Message}).\n\nRight-click the connection or its tab → PowerShell, File transfer, Registry, Services or Processes.");
            else if (!Features.IsAllowed(Feature.AgentInstall))
                await Dialogs.Info(this, "RemoteHub Agent", $"The RemoteHub Agent on {c.Name} could not be used.\n\n{st.Message}");
            else if (await Dialogs.Confirm(this, "RemoteHub Agent", $"The RemoteHub Agent on {c.Name} could not be used.\n\n{st.Message}", "Install / update the agent…"))
                await InstallAgent(c);
            return;
        }
        // HTTPS answers, but this PC doesn't trust the certificate the setup script made yet: compare and trust it here
        // (also when the setup window was closed before its "Test again").
        if (st.State != PsRemotingState.Available && target.UseSsl && st.Message == PsRemoting.UntrustedCertificate)
        {
            if (!await Dialogs.Confirm(this, "PowerShell remoting",
                    $"{c.Name} answers over HTTPS, but this PC doesn't trust its certificate yet.\n\n" +
                    "Compare its thumbprint with the one the setup script printed on the target, and trust it if they're the same?",
                    "Check the certificate…"))
                return;
            var (_, error) = await RemotingSetupDialog.CheckAndTrustCertificate(this, c.Host.Trim(), target.Via);
            if (error is null) await TestPsRemoting(c, interactive: true, credentials);
            else await Dialogs.Info(this, "PowerShell remoting", error);
            return;
        }
        // Signed in but refused (or a wrong password): the setup script wouldn't change anything – no offer to run it again.
        if (st.State != PsRemotingState.Available && st.Message == PsRemoting.AccessDenied)
        {
            await Dialogs.Info(this, "PowerShell remoting", $"PowerShell remoting answers on {c.Name}, but the account was refused.\n\n{st.Message}");
            return;
        }
        if (st.State == PsRemotingState.Available)
            await Dialogs.Info(this, "PowerShell remoting", $"PowerShell remoting works ({st.Message}).\n\nRight-click the connection or its tab → PowerShell or File transfer.");
        else if (!Features.IsAllowed(Feature.RemotingSetup))
            await Dialogs.Info(this, "PowerShell remoting", $"PowerShell remoting is not available for {c.Name}.\n\n{st.Message}");
        else if (await Dialogs.Confirm(this, "PowerShell remoting",
                     $"PowerShell remoting is not available for {c.Name}.\n\n{st.Message}\n\n" +
                     "RemoteHub can create a ready-to-run setup script (with this PC's address and a certificate for HTTPS) to paste into an elevated PowerShell on the target.",
                     "Set up on the target…"))
            await ShowRemotingSetup(c);
    }

    /// <summary>Setup script for the target: the admin pastes and runs it there; then "Test again".</summary>
    private async Task ShowRemotingSetup(Connection c)
    {
        if (Refused(Feature.RemotingSetup)) return;
        var host = c.Host.Trim();
        // Behind a jump host the target sees the jump host's address in its network, not this PC's.
        JumpRoute? via = null;
        if (c.JumpHost is not null && (!TryRoute(c, quiet: false, out via) || via is null)) return;
        var local = via is null
            ? await Task.Run(() => RemotingSetup.LocalAddressFor(host))
            : await JumpLocalAddressAsync(c, Inheritance.ResolveOptions(c, _vm.Data).PsUseSsl == true ? 5986 : 5985);
        var dlg = new RemotingSetupDialog(c.Name, host, c.PsRemoting?.Message, local, via) { Owner = this };
        var testAgain = dlg.ShowDialog() == true;
        if (dlg.SwitchToHttps && Inheritance.ResolveOptions(c, _vm.Data).PsUseSsl != true)
        {
            c.Options.PsUseSsl = true;
            _vm.RequestSave();
        }
        if (testAgain) await TestPsRemoting(c, interactive: true);
    }

    // =====================================================================
    // RemoteHub Agent: install / update
    // =====================================================================

    /// <summary>Right-click → Install / Update RemoteHub Agent.</summary>
    private async Task InstallAgent(Connection c)
    {
        if (Refused(Feature.AgentInstall)) return;
        var package = AgentInstaller.FindPackage();
        if (package is null)
        {
            var pick = new Microsoft.Win32.OpenFileDialog { Title = "RemoteHub Agent installer", Filter = "RemoteHub Agent installer (RemoteHub-Agent*.msi)|RemoteHub-Agent*.msi" };
            if (pick.ShowDialog(this) != true) return;
            package = pick.FileName;
        }

        // Silent install needs WinRM: offered when the connection's remoting check is on or WinRM worked before (also through a jump host).
        var jump = c.JumpHost;
        var remoting = PsEnabled(c) || c.PsRemoting is { ViaAgent: false, State: PsRemotingState.Available };
        var agentReachable = false;
        string[]? managed = null; // what the device's policy decides (names only), for the dialog's notes
        if (c.Agent is { Status: AgentStatus.Installed } a && EndpointOf(c, a, quiet: jump is null) is { } endpoint)
            try
            {
                managed = (await AgentInstaller.HealthAsync(c.Host, endpoint, new System.Threading.CancellationTokenSource(jump is null ? 4000 : 20_000).Token)).Managed;
                agentReachable = true;
            }
            catch { /* offer the other ways */ }
        // The firewall scope's suggestion: the address the device sees – this PC's, or the jump host's in the device's network.
        var local = jump is null
            ? await Task.Run(() => RemotingSetup.LocalAddressFor(c.Host.Trim()))
            : await JumpLocalAddressAsync(c, c.Agent?.Port ?? Agent.AgentProtocol.DefaultPort);

        var dlg = new AgentInstallDialog(c, remoting, agentReachable, local, jump?.Name, AgentInstaller.PackageHasCredentialProvider(package), managed) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Options is not { } o) return;

        // The progress in a window of its own: a row per step with a check mark when it's done.
        var fresh = o.NewIdentity || c.Agent is null;
        var expected = o.Method switch
        {
            AgentInstallMethod.Remoting => fresh ? 6 : 5,      // connect, copy MSI (+ enrollment), install, check, reachable
            AgentInstallMethod.ThroughAgent => fresh ? 5 : 4,  // connect, copy MSI (+ enrollment), start installer, restart
            _ => 1,
        };
        var progress = new AgentInstallProgressWindow(dlg.Title, $"{c.Name}  ·  {c.Host}", expected) { Owner = this };
        progress.Show();
        bool ok;
        try { ok = await RunAgentInstall(c, package, o, progress.Report); }
        catch (Exception ex) { progress.Report("Failed: " + ex.Message); ok = false; }
        progress.Finish(ok);
    }

    // RemoteHub Agent: credential provider (automatic sign-in, docs/credential-provider-design.md)

    private (string Package, bool Has)? _packageHasProvider;

    /// <summary>Offered for an installed agent when RemoteHub's agent package has the credential provider in it.</summary>
    private bool CredentialProviderOffered(Connection c)
    {
        if (c.Agent is not { Status: AgentStatus.Installed } || !Features.IsAllowed(Feature.CredentialProviderInstall) || AgentInstaller.FindPackage() is not { } package) return false;
        if (_packageHasProvider is not { } known || known.Package != package)
            _packageHasProvider = known = (package, AgentInstaller.PackageHasCredentialProvider(package));
        return known.Has;
    }

    private static string CredentialProviderHeader(Connection c) =>
        c.Agent?.CredentialProvider == true ? "Remove credential provider" : "Install credential provider";

    /// <summary>
    /// Right-click → Install / Remove credential provider: the agent's own MSI adds or removes its feature on the device
    /// (never the agent at runtime, design §8.5). The agent keeps running.
    /// </summary>
    private async Task ChangeCredentialProvider(Connection c)
    {
        if (Refused(Feature.CredentialProviderInstall)) return;
        if (c.Agent is not { Status: AgentStatus.Installed } agent || AgentInstaller.FindPackage() is not { } package) return;
        var install = agent.CredentialProvider != true;
        var title = CredentialProviderHeader(c);
        var text = install
            ? $"Installs the RemoteHub Agent's credential provider on {c.Name} and allows automatic sign-in there for the accounts that may " +
              "use the agent. RemoteHub can then sign in when nobody is signed in on the device.\n\nA policy IT has set on the device stays as it is. " +
              "Security tools may report the new sign-in component.\n\nInstall it?"
            : $"Removes the credential provider from {c.Name} and the permission RemoteHub set for automatic sign-in. The agent keeps running.\n\nRemove it?";
        if (!await Dialogs.Confirm(this, title, text, install ? "Install" : "Remove")) return;

        var progress = new AgentInstallProgressWindow(title, $"{c.Name}  ·  {c.Host}", install ? 4 : 3) { Owner = this };
        progress.Show();
        var ok = false;
        try
        {
            var previous = KnownTarget(c) is { Agent: not null } known ? known : null;
            if (BuildTarget(c, previous, viaAgent: true) is not { } t) progress.Report("Cancelled.");
            else
            {
                progress.Report($"Connecting to {t.Target.Display}…");
                try
                {
                    var result = await Task.Run(() =>
                    {
                        using var rs = PsRemoting.Open(t.Target);
                        return AgentInstaller.ChangeCredentialProvider(rs, package, install, progress.Report);
                    });
                    agent.CredentialProvider = result.Installed;
                    _vm.RequestSave();
                    Data.AuditLog.Write(Data.AuditEvents.AgentCredentialProvider, c, result.Installed ? "Installed – credential provider" : "Removed – credential provider");
                    progress.Report(result.Message);
                    ok = true;
                }
                catch (Exception ex)
                {
                    progress.Report("Failed: " + (ex is RemoteCommandException ? ex.Message : PsRemoting.Explain(ex, t.Target)));
                }
            }
        }
        catch (Exception ex) { progress.Report("Failed: " + ex.Message); }
        progress.Finish(ok);
    }

    /// <summary>
    /// Right-click → Uninstall RemoteHub Agent: msiexec /x on the device through the agent's own PowerShell (detached, as
    /// an update is), then waits for the agent to stop. The connection forgets the agent and goes back to RDP and
    /// PowerShell remoting. If the device can't be reached, the agent can be forgotten here only.
    /// </summary>
    private async Task UninstallAgent(Connection c)
    {
        if (Refused(Feature.AgentInstall)) return;
        if (c.Agent is not { Status: AgentStatus.Installed } device) return;
        const string title = "Uninstall RemoteHub Agent";
        var through = _vm.AllNodes().OfType<Connection>().Count(x => x.JumpHost == c);
        var text = $"Removes the RemoteHub Agent from {c.Name}, with its credential provider, certificate and firewall rule. Open agent tabs " +
                   "of this device lose their connection, and the connection goes back to RDP and PowerShell remoting." +
                   (through > 0 ? $"\n\n{(through == 1 ? "1 connection reaches its device" : $"{through} connections reach their devices")} through " +
                                  $"{c.Name}'s agent and can't connect that way any more." : "") +
                   "\n\nUninstall it?";
        if (!await Dialogs.Confirm(this, title, text, "Uninstall")) return;

        var progress = new AgentInstallProgressWindow(title, $"{c.Name}  ·  {c.Host}", 3) { Owner = this };
        progress.Show();
        var ok = false;
        string? failure = null;
        try
        {
            var previous = KnownTarget(c) is { Agent: not null } known ? known : null;
            if (BuildTarget(c, previous, viaAgent: true) is not { } t) progress.Report("Cancelled.");
            else
            {
                progress.Report($"Connecting to {t.Target.Display}…");
                try
                {
                    // Once the script is sent, a dropped connection may be the uninstall stopping the agent that carries it:
                    // that counts as "maybe started", and the wait below decides.
                    var sent = false;
                    await Task.Run(() =>
                    {
                        var rs = PsRemoting.Open(t.Target);
                        try
                        {
                            sent = true;
                            AgentInstaller.StartUninstall(rs, progress.Report);
                        }
                        catch (Exception ex) when (ex is not RemoteCommandException) { /* maybe started – see the wait */ }
                        finally
                        {
                            try { rs.Dispose(); } catch { /* the agent may already be gone */ }
                        }
                    });
                    var endpoint = EndpointOf(c, device) ?? new AgentEndpoint(device.Port, device.Fingerprint);
                    if (sent && await AgentInstaller.WaitForStopAsync(c.Host, endpoint, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(20), progress.Report, System.Threading.CancellationToken.None))
                    {
                        ForgetAgent(c, "Uninstalled");
                        progress.Report($"Uninstalled – {c.Name} no longer runs the RemoteHub Agent.");
                        ok = true;
                    }
                    else failure = $"The agent still answers after 3 minutes. On the device, see {AgentInstaller.UninstallLog}.";
                }
                catch (Exception ex)
                {
                    failure = ex is RemoteCommandException ? ex.Message : PsRemoting.Explain(ex, t.Target);
                }
            }
        }
        catch (Exception ex) { failure = ex.Message; }
        if (failure is not null) progress.Report("Failed: " + failure);
        progress.Finish(ok);
        if (failure is not null && c.Agent == device &&
            await Dialogs.Confirm(this, title, $"The agent couldn't be uninstalled from {c.Name}:\n{failure}\n\nRemove it from this connection in RemoteHub " +
                                               "only? The agent stays on the device; uninstall it there in Settings → Apps.", "Remove here"))
            ForgetAgent(c, "Removed here only – the device wasn't changed");
    }

    /// <summary>The connection no longer has an agent and goes back to RDP and WinRM.</summary>
    private void ForgetAgent(Connection c, string auditDetail)
    {
        Data.AuditLog.Write(Data.AuditEvents.AgentUninstalled, c, auditDetail);
        c.Agent = null;
        if (c.Options.UseAgent == true) c.Options.UseAgent = null;
        c.PsRemoting = null;
        c.RefreshLabels();
        _vm.RequestSave();
        DiagLog.Write("RemoteHub Agent removed from a connection");
    }

    /// <summary>
    /// After an install by hand (clipboard): asks the agent every 10 s, for up to 30 minutes, whether it answers with the
    /// device certificate. Then it's marked installed and – if chosen – the connection switches to it. Until then the
    /// connection keeps working the way it did.
    /// </summary>
    private async Task WaitForManualInstall(Connection c, bool useAgent)
    {
        var until = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < until && c.Agent is { Status: AgentStatus.NotInstalled } d)
        {
            try
            {
                // Through its jump host, if it has one (only with a known password – this runs in the background).
                if (EndpointOf(c, d) is not { } endpoint) throw new InvalidOperationException("The jump host can't be used now.");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(endpoint.Via is null ? 5 : 20));
                var health = await AgentInstaller.HealthAsync(c.Host, endpoint, cts.Token);
                if (c.Agent != d) return; // replaced meanwhile
                d.Status = AgentStatus.Installed;
                d.Version = health.Version;
                d.InstalledUtc = DateTime.UtcNow;
                if (useAgent && !UsesAgent(c)) c.Options.UseAgent = true;
                c.PsRemoting = null;
                c.RefreshLabels();
                _vm.RequestSave();
                Data.AuditLog.Write(Data.AuditEvents.AgentInstalled, c, $"Installed – answered as RemoteHub Agent {health.Version}");
                DiagLog.Write("RemoteHub Agent: installed by hand – it answers now");
                return;
            }
            catch { /* not installed yet */ }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    private async Task<bool> RunAgentInstall(Connection c, string package, AgentInstallOptions o, Action<string> report)
    {
        AgentEnrollment? enrollment = null;
        AgentDevice device;
        if (o.NewIdentity || c.Agent is null)
        {
            var (e, fingerprint) = await Task.Run(() => AgentEnrollment.Create(o.Label, o.Port, o.AllowedGroups, o.FirewallScope, virtualDisplay: o.VirtualDisplay));
            enrollment = e;
            device = new AgentDevice { DeviceId = e.DeviceId, Label = e.DeviceLabel, Port = e.ListenPort, Fingerprint = fingerprint, Status = AgentStatus.NotInstalled, CreatedUtc = DateTime.UtcNow };
            report($"New device certificate: SHA-256 …{fingerprint[^16..]}");
        }
        else device = c.Agent.Clone();
        device.VirtualDisplay = o.VirtualDisplay;
        if (o.AutoSignIn is { } autoSignIn) device.CredentialProvider = autoSignIn;

        var (folder, msi, enrollmentFile) = AgentInstaller.Prepare(package, enrollment);
        DiagLog.Write($"RemoteHub Agent {(enrollment is null ? "update" : "install")} started ({o.Method})");
        void Finish(AgentDevice d, bool switchNow = true)
        {
            Data.AuditLog.Write(Data.AuditEvents.AgentInstalled, c, $"{(enrollment is null ? "Updated" : switchNow ? "Installed" : "Prepared")} – {o.Method}");
            c.Agent = d;
            c.RefreshLabels();
            if (switchNow && o.UseAgentAfterwards && !UsesAgent(c)) c.Options.UseAgent = true;
            c.PsRemoting = null; // test again the new way
            _vm.RequestSave();
        }

        try
        {
            switch (o.Method)
            {
                case AgentInstallMethod.SaveFiles:
                {
                    var pick = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder for the RemoteHub Agent installer of {c.Name}" };
                    if (pick.ShowDialog(this) != true) { report("Cancelled."); return false; }
                    var saved = new List<string>();
                    foreach (var file in new[] { msi, enrollmentFile }.OfType<string>())
                    {
                        var to = Path.Combine(pick.FolderName, Path.GetFileName(file));
                        File.Copy(file, to, overwrite: true);
                        saved.Add(to);
                    }
                    // Not installed yet: the connection keeps its current way until the agent answers (see WaitForManualInstall).
                    if (enrollment is not null)
                    {
                        Finish(device, switchNow: false);
                        _ = WaitForManualInstall(c, o.UseAgentAfterwards);
                    }
                    report(enrollmentFile is null
                        ? $"Saved {AgentInstaller.PackageName} to {pick.FolderName}. On {c.Name}: run it as administrator (or deploy it with software distribution)."
                        : $"Saved {AgentInstaller.PackageName} and {Path.GetFileName(enrollmentFile)} to {pick.FolderName}.\n\n" +
                          $"On {c.Name}:\n  1. Copy both files into the same folder – over a secure channel: the .rhagent file contains the device key.\n" +
                          $"  2. Run {AgentInstaller.PackageName} as administrator (or with software distribution: msiexec /i {AgentInstaller.PackageName} " +
                          "ENROLLMENT=<path of the .rhagent file> /qn).\n" +
                          "  3. The agent imports and deletes the .rhagent file – if it's still there, delete it.\n\n" +
                          "Delete the files in the chosen folder once they are installed. " +
                          "RemoteHub checks for the agent every few seconds and marks it installed as soon as it answers" +
                          (o.UseAgentAfterwards && !UsesAgent(c) ? " – then the connection switches to it." : "."));
                    return true;
                }

                case AgentInstallMethod.Clipboard:
                {
                    // The .rhagent file holds this device's private key; a remote desktop tab would send it to another device.
                    if (enrollmentFile is not null
                        && _vm.Sessions.OfType<AgentDesktopSession>().Where(s => s.SendsClipboard && s.Connection != c).Select(s => s.Connection.Name).ToList() is [_, ..] others
                        && !await Dialogs.Confirm(this, "Copy the device key?",
                            $"The remote desktop tab of {string.Join(", ", others)} sends everything copied on this PC to that device – including " +
                            $"{Path.GetFileName(enrollmentFile)}, which holds {c.Name}'s device key.\n\nClose that tab first, or copy anyway?", "Copy anyway"))
                    {
                        report("Cancelled – close the remote desktop tab, then try again.");
                        return false;
                    }
                    var files = new StringCollection { msi };
                    if (enrollmentFile is not null) files.Add(enrollmentFile);
                    Clipboard.SetFileDropList(files);
                    folder = null; // the files must stay until they are pasted – deleted when RemoteHub closes
                    // Not installed yet: the connection keeps its current way until the agent answers (see WaitForManualInstall).
                    if (enrollment is not null)
                    {
                        Finish(device, switchNow: false);
                        _ = WaitForManualInstall(c, o.UseAgentAfterwards);
                    }
                    report(enrollmentFile is null
                        ? $"Copied {AgentInstaller.PackageName} to the clipboard. In the RDP session of {c.Name}: paste it and run it as administrator."
                        : $"Copied {AgentInstaller.PackageName} and {Path.GetFileName(enrollmentFile)} to the clipboard.\n\n" +
                          $"In the RDP session of {c.Name}:\n  1. Paste both files into the same folder.\n  2. Run {AgentInstaller.PackageName} as administrator.\n" +
                          "  3. The agent imports and deletes the .rhagent file – if it's still there, delete it (it contains the device key).\n\n" +
                          "This needs clipboard redirection for the RDP session. The files on this PC are deleted when RemoteHub closes. " +
                          "RemoteHub checks for the agent every few seconds and marks it installed as soon as it answers" +
                          (o.UseAgentAfterwards ? " – then the connection switches to it." : "."));
                    return true;
                }

                case AgentInstallMethod.Remoting:
                {
                    var previous = KnownTarget(c) is { Agent: null } known ? known : null;
                    if (BuildTarget(c, previous, viaAgent: false) is not { } t) { report("Cancelled."); return false; }
                    report($"Connecting to {t.Target.Display}…");
                    AgentInstaller.Installed installed;
                    try
                    {
                        installed = await Task.Run(() =>
                        {
                            using var rs = PsRemoting.Open(t.Target);
                            return AgentInstaller.InstallOverRemoting(rs, msi, enrollmentFile, report, o.VirtualDisplay, o.AutoSignIn);
                        });
                    }
                    catch (Exception ex)
                    {
                        report("Failed: " + (ex is RemoteCommandException ? ex.Message : PsRemoting.Explain(ex, t.Target)));
                        return false;
                    }
                    if (!string.Equals(installed.Fingerprint, device.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        report($"The device's agent uses a certificate RemoteHub didn't create for it (…{installed.Fingerprint[^16..]}). " +
                               "Install again with \"Create a new device certificate\".");
                        return false;
                    }
                    device.Port = installed.Port;
                    report($"Installed – service {installed.ServiceStatus}.");
                    await ConfirmReachable(c, device, report);
                    device.Status = AgentStatus.Installed;
                    device.InstalledUtc = DateTime.UtcNow;
                    Finish(device);
                    if (UsesAgent(c)) _ = TestPsRemoting(c, interactive: false, t.Target.Credentials);
                    return true;
                }

                case AgentInstallMethod.ThroughAgent:
                {
                    var previous = KnownTarget(c) is { Agent: not null } known ? known : null;
                    if (BuildTarget(c, previous, viaAgent: true) is not { } t) { report("Cancelled."); return false; }
                    report($"Connecting to {t.Target.Display}…");
                    try
                    {
                        await Task.Run(() =>
                        {
                            using var rs = PsRemoting.Open(t.Target);
                            AgentInstaller.StartUpdateThroughAgent(rs, msi, enrollmentFile, report, o.VirtualDisplay, o.AutoSignIn);
                        });
                    }
                    catch (Exception ex)
                    {
                        report("Failed: " + (ex is RemoteCommandException ? ex.Message : PsRemoting.Explain(ex, t.Target)));
                        return false;
                    }
                    var expected = AgentInstaller.PackageVersion(msi);
                    var health = await AgentInstaller.WaitForRestartAsync(c.Host, EndpointOf(c, device) ?? new AgentEndpoint(device.Port, device.Fingerprint), expected, report, System.Threading.CancellationToken.None);
                    const string logs = "On the device, see %TEMP%\\RemoteHub-Agent-update.log of the signing-in account and the agent's log in %ProgramData%\\RemoteHub\\Agent\\logs.";
                    if (health is null)
                    {
                        report("The agent didn't answer again within a few minutes. " + logs);
                        return false;
                    }
                    if (expected is not null && health.Version != expected)
                    {
                        report($"The update didn't install: the device still runs RemoteHub Agent {health.Version} (expected {expected}). " + logs);
                        return false;
                    }
                    device.Version = health.Version;
                    device.Status = AgentStatus.Installed;
                    device.InstalledUtc = DateTime.UtcNow;
                    Finish(device);
                    report($"Updated – RemoteHub Agent {health.Version} is running.");
                    if (UsesAgent(c)) _ = TestPsRemoting(c, interactive: false, t.Target.Credentials);
                    return true;
                }
            }
            return false;
        }
        finally
        {
            if (folder is not null) try { Directory.Delete(folder, true); } catch { /* cleaned up later */ }
            DiagLog.Write("RemoteHub Agent install/update finished");
        }
    }

    /// <summary>Checks from this PC that the agent answers with the expected certificate (network path, firewalls).</summary>
    private async Task ConfirmReachable(Connection c, AgentDevice device, Action<string> report)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var h = await AgentInstaller.HealthAsync(c.Host, EndpointOf(c, device) ?? new AgentEndpoint(device.Port, device.Fingerprint), new System.Threading.CancellationTokenSource(c.JumpHost is null ? 10_000 : 30_000).Token);
                device.Version = h.Version;
                report($"RemoteHub Agent {h.Version} answers on port {device.Port} with the expected certificate.");
                return;
            }
            catch (Exception ex) when (attempt < 4) { _ = ex; await Task.Delay(2000); }
            catch (Exception ex)
            {
                report($"Installed, but this PC can't reach the agent on {c.Host}:{device.Port} ({ex.GetBaseException().Message}). " +
                       "Check firewalls between this PC and the device, and the agent's \"Allow connections from\" scope.");
                return;
            }
        }
    }

    /// <summary>
    /// The device's screen through the RemoteHub Agent in a new tab (or the existing one). Signs in with the
    /// connection's credentials – or those of an open agent tab – then the tab asks which Windows session to show.
    /// </summary>
    private void OpenAgentDesktop(Connection c, bool forceNew = false)
    {
        if (!forceNew && _vm.FindAgentDesktop(c) is { } existing)
        {
            _vm.SelectedSession = existing;
            if (existing.Phase is DesktopPhase.Ended or DesktopPhase.Idle) ConnectAgentDesktop(existing);
            return;
        }
        if (Refused(Feature.AgentDesktop)) return;
        if (AgentDesktopTarget(c, null) is not { } t) return;
        var s = _vm.OpenAgentDesktop(c);
        var ui = _vm.Data.Ui;
        var view = new AgentDesktopView(s, () => ConnectAgentDesktop(s), s.Close, () => { s.Close(); OpenRdp(c); },
            // What this PC's policy fixes (docs/admx-design.md §6.8) is used, and not remembered as the user's choice.
            () => DesktopQualityPreset.Find(AppPolicy.AgentDesktopQuality ?? ui.AgentDesktopQuality),
            (quality, consent) =>
            {
                if (AppPolicy.AgentDesktopQuality is null) ui.AgentDesktopQuality = quality.Id;
                if (consent != AppPolicy.AgentDesktopConsent) ui.AgentDesktopConsent = consent; // chosen by the user, not the policy's
                _vm.RequestSave();
            },
            () => AppPolicy.AgentDesktopFitToWindow is { } fit ? (fit ? DesktopResolution.Fit : DesktopResolution.Device)
                : DesktopResolution.FromSettings(ui.AgentDesktopResolution, ui.AgentDesktopFitToWindow),
            resolution =>
            {
                if (AppPolicy.AgentDesktopFitToWindow is not null || ui.AgentDesktopResolution == resolution.Id) return;
                ui.AgentDesktopResolution = resolution.Id;
                ui.AgentDesktopFitToWindow = resolution.Kind == DesktopResolutionKind.Fit;
                _vm.RequestSave();
            },
            () => Inheritance.ResolveOptions(c, _vm.Data).AgentAutoSignIn == true,
            () => Inheritance.ResolveOptions(c, _vm.Data).AgentAutoSignInEndAction);
        view.RememberedConsent(ui.AgentDesktopConsent);
        view.UsePreferences(ui.AgentDesktopShowInfo, ui.AgentDesktopKeyCombinations, (info, keys) =>
        {
            if (AppPolicy.AgentDesktopShowInfo is null) ui.AgentDesktopShowInfo = info;
            if (AppPolicy.AgentDesktopKeyCombinations is null) ui.AgentDesktopKeyCombinations = keys;
            _vm.RequestSave();
        });
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        s.Connect(t.Target, t.Source, AccentFor(c));
    }

    private void ConnectAgentDesktop(AgentDesktopSession s)
    {
        if (RefusedIn(s, Feature.AgentDesktop) || AgentDesktopTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>Agent credentials: those of an open agent tab of the connection, else the connection's own (asks for a missing password).</summary>
    private (PsTarget Target, string Source)? AgentDesktopTarget(Connection c, PsTarget? previous)
    {
        var known = previous ?? _vm.Sessions.Where(x => x.Connection == c)
            .Select(x => x switch { RemoteToolSession r => r.Target, PsSession p => p.Target, _ => null })
            .FirstOrDefault(t => t?.Agent is not null);
        if (known?.Agent is not null && AgentEndpointFor(c, quiet: true) is { } ep && known.Agent == ep && !PsRemoting.NeedsPassword(known.Credentials))
            return (known, "the open agent tab");
        return BuildTarget(c, previous, viaAgent: true);
    }

    /// <summary>Opens a PowerShell remoting session to the connection's host in a new tab.</summary>
    private void OpenPowerShell(Connection c)
    {
        if (Refused(RemoteTool.PowerShell, c) || BuildTarget(c, null) is not { } t) return;
        var s = _vm.OpenPsSession(c);
        var view = new PsConsoleView(s, () => ConnectPs(s), s.Close, canSendFiles: () => Features.IsAllowed(RemoteTool.FileTransfer, UsesAgent(c)));
        view.UploadRequested += files => UploadToConsole(s, files);
        view.UploadPickRequested += () => OpenFileTransfer(c, s.Location, s.Target);
        s.Opened += info => SetPsStatus(c, PsRemotingState.Available, info);
        s.OpenFailed += why => SetPsStatus(c, PsRemotingState.Unavailable, why);
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        s.Connect(t.Target, t.Source, AccentFor(c));
    }

    private void ConnectPs(PsSession s)
    {
        if (RefusedIn(s, Features.For(RemoteTool.PowerShell, UsesAgent(s.Connection))) || BuildTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>
    /// Opens a file transfer tab (this PC | remote computer). <paramref name="remoteStart"/> is the remote folder shown
    /// first (e.g. the current folder of a PowerShell tab); <paramref name="known"/> reuses that tab's credentials.
    /// </summary>
    private void OpenFileTransfer(Connection c, string? remoteStart, PsTarget? known)
    {
        PsTarget target;
        string source;
        if (Refused(RemoteTool.FileTransfer, c)) return;
        if (known is not null && (known.Agent is not null) == UsesAgent(c)) { target = known; source = "the PowerShell tab"; }
        else if (BuildTarget(c, null) is { } t) { target = t.Target; source = t.Source; }
        else return;

        var s = _vm.OpenFileBrowser(c, remoteStart ?? c.PsRemoting?.UploadFolder);
        var localStart = Directory.Exists(_vm.Data.Ui.LastLocalFolder) ? _vm.Data.Ui.LastLocalFolder!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var view = new FileBrowserView(s, localStart, () => ConnectFileBrowser(s), s.Close);
        view.Local.Navigated += p => { if (p.Length > 0) _vm.Data.Ui.LastLocalFolder = p; };
        view.Remote.Navigated += p => { if (p.Length > 0 && c.PsRemoting is { } st) st.UploadFolder = p; };
        view.UploadRequested += async (files, folder) =>
        {
            if (s.Target is not { } tg) return;
            await Upload(c, tg, files, folder, names => s.ExistingAsync(folder, names));
            await view.Remote.Refresh();
        };
        view.DownloadRequested += async (drag, folder) =>
        {
            await Download(drag, folder);
            await view.Local.Refresh();
        };
        s.Opened += info => SetPsStatus(c, PsRemotingState.Available, info);
        s.OpenFailed += why => SetPsStatus(c, PsRemotingState.Unavailable, why);
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        s.Connect(target, source, AccentFor(c));
    }

    private void ConnectFileBrowser(FileBrowserSession s)
    {
        if (RefusedIn(s, Features.For(RemoteTool.FileTransfer, UsesAgent(s.Connection))) || BuildTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>Credentials for a new remote tool tab: those of an open remoting tab of the connection, else the connection's own.</summary>
    private (PsTarget Target, string Source)? ToolTarget(Connection c, PsTarget? known) =>
        known is not null && (known.Agent is not null) == UsesAgent(c) ? (known, "the open tab") : BuildTarget(c, null);

    /// <summary>Shows a new remote tool tab (registry, services, processes) and connects it.</summary>
    private void ShowTool(RemoteToolSession s, FrameworkElement view, (PsTarget Target, string Source) t)
    {
        var c = s.Connection;
        s.Opened += info => SetPsStatus(c, PsRemotingState.Available, info);
        s.OpenFailed += why => SetPsStatus(c, PsRemotingState.Unavailable, why);
        _views[s] = view;
        SessionPanel.Children.Add(view);
        UpdateSessionVisibility();
        s.Connect(t.Target, t.Source, AccentFor(c));
    }

    private void ConnectTool(RemoteToolSession s)
    {
        if (RefusedIn(s, Features.For(s.Tool, UsesAgent(s.Connection))) || BuildTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>Opens the remote registry (tree + values) in a new tab, starting at the last key used for this connection.</summary>
    private void OpenRegistry(Connection c, PsTarget? known)
    {
        if (Refused(RemoteTool.Registry, c) || ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenRegistry(c, c.PsRemoting?.RegistryPath);
        var view = new RegistryView(s, () => ConnectTool(s), s.Close);
        view.Navigated += p => { if (c.PsRemoting is { } st && st.RegistryPath != p) { st.RegistryPath = p; _vm.RequestSave(); } };
        ShowTool(s, view, t);
    }

    /// <summary>Opens the remote services in a new tab (refreshed regularly, see Global defaults → Remote tools).</summary>
    private void OpenServices(Connection c, PsTarget? known)
    {
        if (Refused(RemoteTool.Services, c) || ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenServices(c);
        ShowTool(s, new ServicesView(s, () => AppPolicy.ServiceRefreshSeconds ?? _vm.Data.Tools.ServiceRefreshSeconds, () => ConnectTool(s), s.Close), t);
    }

    /// <summary>Opens the remote processes in a new tab (refreshed regularly, see Global defaults → Remote tools).</summary>
    private void OpenProcesses(Connection c, PsTarget? known)
    {
        if (Refused(RemoteTool.Processes, c) || ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenProcesses(c);
        ShowTool(s, new ProcessesView(s, () => AppPolicy.ProcessRefreshSeconds ?? _vm.Data.Tools.ProcessRefreshSeconds, () => ConnectTool(s), s.Close), t);
    }

    /// <summary>Asks what to do when some of the names already exist. Null = cancel.</summary>
    private async Task<bool?> AskOverwrite(IReadOnlyCollection<string> existing, string where)
    {
        if (existing.Count == 0) return false;
        var list = string.Join("\n", existing.Take(8)) + (existing.Count > 8 ? $"\n… and {existing.Count - 8} more" : "");
        var r = await Dialogs.Ask(this, "Files already exist", $"{existing.Count} item(s) already exist in {where}:\n\n{list}", "Overwrite", "Skip existing");
        return r switch { Wpf.Ui.Controls.MessageBoxResult.Primary => true, Wpf.Ui.Controls.MessageBoxResult.Secondary => false, _ => null };
    }

    /// <summary>Uploads local files/folders into <paramref name="remoteFolder"/> (progress in the transfers panel).</summary>
    private async Task Upload(Connection c, PsTarget target, IReadOnlyList<string> localPaths, string remoteFolder,
                              Func<IEnumerable<string>, Task<List<string>>> existing)
    {
        List<(string Local, string Relative)> list;
        try { list = FileUploader.Expand(localPaths); }
        catch (Exception ex) { await Dialogs.Info(this, "Upload", ex.Message); return; }
        if (list.Count == 0) return;

        bool? overwrite;
        try { overwrite = await AskOverwrite(await existing(localPaths.Select(p => Path.GetFileName(p.TrimEnd('\\')))), remoteFolder); }
        catch (Exception ex) { await Dialogs.Info(this, "Upload", ex.Message); return; }
        if (overwrite is null) return;

        var items = list.Select(x => new TransferItem(x.Local, x.Relative, c.Name)).ToList();
        await RunTransfers(c, items, "upload", () => FileUploader.UploadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target),
            remoteFolder, overwrite.Value, items, a => Dispatcher.BeginInvoke(a)));
    }

    /// <summary>Downloads remote files/folders into <paramref name="localFolder"/>.</summary>
    private async Task Download(RemoteDrag drag, string localFolder)
    {
        PsTarget? target = null;
        if (drag.Source is FileBrowserSession fb) { if ((target = fb.Target) is null) return; }
        else if (drag.Source is not ShareSession) return;
        List<(long Size, string FullPath, string Relative)> files;
        try { files = await drag.Source.ExpandAsync(drag.Paths); }
        catch (Exception ex) { await Dialogs.Info(this, "Download", ex.Message); return; }
        if (files.Count == 0) return;
        // The paths come from the device: each one must stay inside the chosen folder (no "..", no absolute path).
        var targets = files.Select(f => DesktopWire.PathBelow(localFolder, f.Relative)).ToList();
        if (targets.IndexOf(null) is var bad and >= 0)
        {
            await Dialogs.Info(this, "Download", $"The device sent a file path that would end up outside the chosen folder, so nothing was downloaded:\n{files[bad].Relative}");
            return;
        }

        var existing = drag.Paths.Select(p => Path.GetFileName(p.TrimEnd('\\')))
            .Where(n => File.Exists(Path.Combine(localFolder, n)) || Directory.Exists(Path.Combine(localFolder, n))).ToList();
        var overwrite = await AskOverwrite(existing, localFolder);
        if (overwrite is null) return;

        var c = drag.Source.Session.Connection;
        if (drag.Source is ShareSession share)
        {
            var items = files.Select((f, i) => TransferItem.Download(f.FullPath, targets[i]!, f.Relative, ShareLabel(share.Share), f.Size)).ToList();
            await RunTransfers(c, items, "download", () => Remoting.Shares.ShareTransfer.DownloadAsync(share.OpenAnother, overwrite.Value, items, a => Dispatcher.BeginInvoke(a)),
                $"share {share.Share.Address}");
        }
        else
        {
            var items = files.Select((f, i) => TransferItem.Download(f.FullPath, targets[i]!, f.Relative, c.Name, f.Size)).ToList();
            await RunTransfers(c, items, "download", () => FileDownloader.DownloadAsync(() => PsRemoting.Open(target!), ex => PsRemoting.Explain(ex, target!),
                overwrite.Value, items, a => Dispatcher.BeginInvoke(a)));
        }
    }

    private async Task RunTransfers(Connection c, List<TransferItem> items, string kind, Func<Task> run, string where = "the device")
    {
        for (int i = items.Count - 1; i >= 0; i--) _vm.Transfers.Insert(0, items[i]);
        _vm.ShowTransfers = true;
        DiagLog.Write($"File {kind} started: {items.Count} file(s)");
        await run();
        // Let the queued progress updates land before counting.
        await Dispatcher.InvokeAsync(() => DiagLog.Write(
            $"File {kind} finished: {items.Count(i => i.IsDone)} done, {items.Count(i => i.State == TransferState.Failed)} failed, " +
            $"{items.Count(i => i.State is TransferState.Cancelled or TransferState.Skipped)} cancelled/skipped"),
            System.Windows.Threading.DispatcherPriority.Background);
        await Dispatcher.InvokeAsync(() => Data.AuditLog.Write(Data.AuditEvents.FilesTransferred, c,
            $"{(kind == "upload" ? $"Upload to {where}" : $"Download from {where}")}: {items.Count(i => i.IsDone)} done, " +
            $"{items.Count(i => i.State == TransferState.Failed)} failed, {items.Count(i => i.State is TransferState.Cancelled or TransferState.Skipped)} cancelled or skipped, " +
            $"{TransferItem.Size(items.Where(i => i.IsDone).Sum(i => i.TotalBytes))}"),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Files dropped on a PowerShell tab go into its current folder.</summary>
    private void UploadToConsole(PsSession s, IReadOnlyList<string> files)
    {
        if (s.Target is not { } target || s.Location is not { } folder || Refused(Features.For(RemoteTool.FileTransfer, target.Agent is not null))) return;
        _ = Upload(s.Connection, target, files, folder, names => Task.Run(() =>
        {
            using var rs = PsRemoting.Open(target);
            return RemoteFs.ExistingRemote(rs, folder, names);
        }));
    }

    private void ToggleTransfers_Click(object sender, RoutedEventArgs e) => _vm.ShowTransfers = !_vm.ShowTransfers;

    /// <summary>
    /// Right pane: hidden while there are no transfers, otherwise expanded (resizable, width remembered per PC) or
    /// collapsed to a slim rail – like the navigation on the left.
    /// </summary>
    private void ApplyTransfersPane()
    {
        var ui = _vm.Data.Ui;
        if (!_vm.HasTransfers)
        {
            TransfersPane.Visibility = Visibility.Collapsed;
            TransfersSplitter.Visibility = Visibility.Collapsed;
            TransfersColumn.MinWidth = 0;
            TransfersColumn.Width = new GridLength(0);
            SessionArea.Margin = new Thickness(2, 0, 10, 10);
            return;
        }
        TransfersPane.Visibility = Visibility.Visible;
        SessionArea.Margin = new Thickness(2, 0, 2, 10);
        if (_vm.ShowTransfers)
        {
            TransfersFull.Visibility = Visibility.Visible;
            TransfersRail.Visibility = Visibility.Collapsed;
            TransfersSplitter.Visibility = Visibility.Visible;
            TransfersColumn.MinWidth = 260;
            TransfersColumn.MaxWidth = 700;
            TransfersColumn.Width = new GridLength(Math.Clamp(ui.TransfersWidth, 260, 700));
        }
        else
        {
            TransfersFull.Visibility = Visibility.Collapsed;
            TransfersRail.Visibility = Visibility.Visible;
            TransfersSplitter.Visibility = Visibility.Collapsed;
            TransfersColumn.MinWidth = 0;
            TransfersColumn.Width = new GridLength(RailWidth);
        }
    }

    private void TransfersSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_vm.ShowTransfers) return;
        _vm.Data.Ui.TransfersWidth = TransfersColumn.ActualWidth;
        _vm.RequestSave();
    }
    private void ClearTransfers_Click(object sender, RoutedEventArgs e) => _vm.ClearFinishedTransfers();

    private async void CancelAllTransfers_Click(object sender, RoutedEventArgs e)
    {
        var active = _vm.ActiveTransfers;
        if (active == 0) return;
        if (active > 1 && !await Dialogs.Confirm(this, "Cancel transfers", $"Cancel all {active} running and waiting transfers? " +
                "Files that are already complete stay; partial files are removed.", "Cancel all")) return;
        _vm.CancelAllTransfers();
        DiagLog.Write($"File transfers: cancel all ({active})");
    }
    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as TransferItem)?.Cancel();

    private void CmConnect_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenConnection(c); }
    private void CmConnectNew_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenRdp(c, forceNew: !AgentDesktopFor(c)); }
    private void CmDesktopOther_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is not Connection c) return;
        if (AgentDesktopFor(c)) OpenRdp(c); else OpenAgentDesktop(c);
    }
    private void CmEdit_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is { } n) EditNode(n); }
    private void CmDuplicate_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is { } n && !Refused(Feature.ConnectionEditing)) _vm.Duplicate(n).IsSelected = true; }
    private void CmHistory_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenAuditLog(c); }

    private void OpenAuditLog(Connection? c)
    {
        _vm.SaveNow(); // pending changes are in the log too
        new AuditLogWindow(_vm.AllNodes().OfType<Connection>(), c) { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this }.Show();
    }
    private void CmDelete_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is { } n) _ = DeleteNode(n); }

    private async void CmConnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedNode is not Folder f) return;
        var list = f.Descendants().OfType<Connection>().ToList();
        if (list.Count > 10 && !await Dialogs.Confirm(this, "Connect all", $"Open {list.Count} sessions at once?", "Connect")) return;
        foreach (var c in list) OpenConnection(c);
    }

    private async void CmSaveRdp_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.RdpFileExport)) return;
        var n = _vm.SelectedNode;
        var conns = n is Folder f ? f.Descendants().OfType<Connection>().ToList()
                  : n is Connection c ? new List<Connection> { c } : new List<Connection>();
        if (conns.Count == 0) return;

        string? file = null, folder = null;
        if (n is Connection one)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Remote Desktop file (*.rdp)|*.rdp",
                FileName = RdpFileWriter.SafeFileName(one.Name) + ".rdp",
            };
            if (dlg.ShowDialog(this) != true) return;
            file = dlg.FileName;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Export {conns.Count} connection(s) as .rdp files" };
            if (dlg.ShowDialog(this) != true) return;
            folder = dlg.FolderName;
        }

        var withPw = conns.Any(x => !string.IsNullOrEmpty(Inheritance.ResolveCredentials(x, _vm.Data).Credentials.Password))
            && await Dialogs.Ask(this, "Save as .rdp",
                "Include saved passwords? They are encrypted for your Windows account, so the file only works for you on this PC (mstsc can use it). Without a password, mstsc asks when connecting.",
                "Without passwords", "Include passwords") == Wpf.Ui.Controls.MessageBoxResult.Secondary;

        var errors = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in conns)
        {
            try
            {
                var path = file;
                if (path is null)
                {
                    var name = RdpFileWriter.SafeFileName(x.Name);
                    for (int i = 2; !used.Add(name); i++) name = $"{RdpFileWriter.SafeFileName(x.Name)} ({i})";
                    path = Path.Combine(folder!, name + ".rdp");
                }
                RdpFileWriter.Write(path, x, Inheritance.ResolveOptions(x, _vm.Data), Inheritance.ResolveCredentials(x, _vm.Data).Credentials, withPw);
            }
            catch (Exception ex) { errors.Add($"{x.Name}: {ex.Message}"); }
        }
        if (errors.Count > 0) await Dialogs.Info(this, "Save as .rdp", string.Join("\n", errors));
        else if (folder is not null) await Dialogs.Info(this, "Save as .rdp", $"{conns.Count} file(s) saved to\n{folder}");
    }

    private void SettingsOverview_Click(object sender, RoutedEventArgs e)
    {
        var actions = new SettingsActions(
            EditDefaults: () => Defaults_Click(this, e),
            ChangeLocation: () => SettingsLocation_Click(this, e),
            ManageCredentials: () => Credentials_Click(this, e),
            StartupProtection: () => StartupProtection_Click(this, e),
            OpenFolder: () => OpenSettingsFolder_Click(this, e),
            OpenLog: () => OpenLog_Click(this, e),
            Policies: () => OpenSettings(EditorWindow.PagePolicySettings));
        new SettingsOverviewWindow(() => _vm.Data, _vm.AllNodes, actions) { Owner = this }.ShowDialog();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void StartupProtection_Click(object sender, RoutedEventArgs e)
    {
        _vm.SaveNow();
        new StartupProtectionWindow(() => _vm.Data) { Owner = this }.ShowDialog();
    }

    private void Credentials_Click(object sender, RoutedEventArgs e)
    {
        new CredentialsWindow(_vm.Data, _vm.AllNodes) { Owner = this }.ShowDialog();
        _vm.RequestSave();
    }

    private void DefaultApp_Click(object sender, RoutedEventArgs e)
    {
        // Lands on RemoteHub's page once the installer has registered it (RegisteredApplications).
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppMachine=RemoteHub") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }

    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        var parent = _vm.TargetFolderForNew();
        var c = new Connection { Parent = parent };
        if (new EditorWindow(c, _vm.Data, isNew: true) { Owner = this }.ShowDialog() == true)
        {
            _vm.Add(c, parent);
            c.IsSelected = true;
        }
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        var parent = _vm.TargetFolderForNew();
        var f = new Folder { Parent = parent };
        if (new EditorWindow(f, _vm.Data, isNew: true) { Owner = this }.ShowDialog() == true)
        {
            _vm.Add(f, parent);
            f.IsSelected = true;
        }
    }

    private void EditNode(NodeBase n)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (new EditorWindow(n, _vm.Data, isNew: false) { Owner = this }.ShowDialog() == true)
        {
            foreach (var s in _vm.Sessions.Where(s => s.Connection == n)) s.Title = n.Name;
            _vm.RequestSave();
        }
    }

    // ---- "not used for" filter

    private void UnusedFilter_Click(object sender, RoutedEventArgs e)
    {
        if (UnusedFilterButton.ContextMenu is not { } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = Equals(item.Tag, _vm.UnusedDays.ToString());
        menu.PlacementTarget = UnusedFilterButton;
        menu.IsOpen = true;
    }

    private void UnusedFilterItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var days)) SetUnusedFilter(days);
    }

    private void ClearUnusedFilter_Click(object sender, RoutedEventArgs e) => SetUnusedFilter(0);

    private void SetUnusedFilter(int days)
    {
        _vm.UnusedDays = days;
        UnusedFilterButton.Appearance = days != 0 ? ControlAppearance.Primary : ControlAppearance.Secondary;
        UnusedFilterButton.ToolTip = days != 0
            ? $"Showing connections {MainViewModel.FilterText(days)} – click to change"
            : "Show connections that haven't been used for a while (to clean them up)";
    }

    /// <summary>Deletes every connection the filter shows, after one question (open tabs of them are closed).</summary>
    private async void DeleteUnused_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        var list = _vm.SearchResults.Select(h => h.Node).OfType<Connection>().ToList();
        if (list.Count == 0) return;
        var names = string.Join("\n", list.Take(12).Select(c => "• " + c.Name)) + (list.Count > 12 ? $"\n… and {list.Count - 12} more" : "");
        if (!await Dialogs.Confirm(this, "Delete unused connections",
                $"Delete these {list.Count} connection(s), {MainViewModel.FilterText(_vm.UnusedDays)}?\n\n{names}\n\n" +
                "Their saved credentials and agent pairing go with them. A backup (Settings → Security & storage → Export backup…) can bring them back.", "Delete")) return;
        foreach (var c in list)
        {
            foreach (var s in _vm.Sessions.Where(s => s.Connection == c).ToList()) s.Close();
            _vm.Remove(c);
        }
        DiagLog.Write($"Deleted {list.Count} connection(s) {MainViewModel.FilterText(_vm.UnusedDays)}");
        RefreshUnused();
    }

    private void RefreshUnused()
    {
        var days = _vm.UnusedDays;
        _vm.UnusedDays = 0;
        _vm.UnusedDays = days;
    }

    private async Task DeleteNode(NodeBase n)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (n is Connection { Children.Count: > 0 } jumpHost)
        {
            // What's reached through it: kept by default – it moves next to the jump host and is reached directly.
            var count = jumpHost.Descendants().Count();
            var choice = await Dialogs.Ask(this, "Delete",
                $"Delete connection \"{n.Name}\"?\n\n{count} item(s) are reached through it. Keep them (they move next to it and are " +
                "reached directly), or delete them too?",
                "Keep them", "Delete all");
            if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                var list = _vm.ChildrenOf(n.Parent);
                var at = list.IndexOf(n);
                foreach (var child in jumpHost.Children.ToList()) _vm.Move(child, n.Parent, ++at);
                _vm.Remove(n);
            }
            else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary) _vm.Remove(n);
            return;
        }
        var what = n is Folder f
            ? $"Delete folder \"{n.Name}\" and the {f.Descendants().Count()} item(s) inside it?"
            : $"Delete connection \"{n.Name}\"?";
        if (await Dialogs.Confirm(this, "Delete", what, "Delete")) _vm.Remove(n);
    }

    // =====================================================================
    // Settings window, update check and telemetry
    // =====================================================================

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    private void Defaults_Click(object sender, RoutedEventArgs e) => OpenSettings(EditorWindow.PageDefaults);

    private void OpenSettings(string? page)
    {
        var e = new RoutedEventArgs();
        Window ActiveOwner() => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var host = new AppSettingsHost(
            ApplyTheme: ApplyTheme,
            ApplySwitchKeys: SetSwitchKeys,
            StartupProtection: () => StartupProtection_Click(this, e),
            ManageCredentials: () => Credentials_Click(this, e),
            ChangeLocation: () => SettingsLocation_Click(this, e),
            OpenFolder: () => OpenSettingsFolder_Click(this, e),
            OpenLog: () => OpenLog_Click(this, e),
            ExportBackup: () => ExportBackup_Click(this, e),
            ImportBackup: () => ImportBackup_Click(this, e),
            AuditLog: () => OpenAuditLog(null),
            DefaultApp: () => DefaultApp_Click(this, e),
            CheckForUpdates: channel => ShowUpdateWindow(null, channel),
            SettingsOverview: () => SettingsOverview_Click(this, e),
            About: () => new AboutWindow { Owner = ActiveOwner() }.ShowDialog(),
            WhatsNew: () => new WhatsNewWindow(FirstStart.Since(null, UpdateService.Current)) { Owner = ActiveOwner() }.ShowDialog(),
            Welcome: () => new WelcomeWindow { Owner = ActiveOwner() }.ShowDialog());
        if (new EditorWindow(null, _vm.Data, isNew: false, host, page) { Owner = this }.ShowDialog() == true) _vm.RequestSave();
        UpdateRailVersion();
    }

    private UpdateCheckResult? _availableUpdate;

    /// <summary>The welcome tour after a new install, What's new after an upgrade – once each, then the version is remembered.</summary>
    private void ShowFirstStart(UiState ui)
    {
        var current = UpdateService.Current;
        var last = AppVersion.Parse(ui.LastSeenVersion);
        var kind = FirstStart.Decide(ui.LastSeenVersion, _vm.ConnectionCount > 0, current);
        if (ui.LastSeenVersion != current.ToString())
        {
            ui.LastSeenVersion = current.ToString();
            _vm.RequestSave();
        }
        DiagLog.Write($"First start: {kind}");
        if (kind == FirstStartKind.Welcome) new WelcomeWindow { Owner = this }.ShowDialog();
        else if (kind == FirstStartKind.WhatsNew) new WhatsNewWindow(FirstStart.Since(last, current), last) { Owner = this }.ShowDialog();
    }

    /// <summary>After the window is shown: the anonymous usage data (if on) and the update check (if on). Neither delays the start.</summary>
    private async void StartupChecks()
    {
        var ui = _vm.Data.Ui;
        Telemetry.AppStart(ui, Telemetry.Count(_vm.Data.Items));
        // Policy AllowSavedPasswords = 0: passwords stored on this PC are removed, after saying so (§14 Q2). A shared settings
        // file keeps them – other PCs may allow them – but they aren't used here either way.
        if (!Features.IsAllowed(Feature.SavedPasswords) && !SettingsStore.IsShared && SavedPasswords.Remove(_vm.Data, _vm.AllNodes()) is var removed and > 0)
        {
            _vm.RequestSave();
            DiagLog.Write($"Saved passwords: {removed} removed (policy AllowSavedPasswords)");
            await Dialogs.Info(this, "Passwords", $"Your organisation doesn't allow saved passwords on this PC – {(removed == 1 ? "1 was" : $"{removed} were")} removed. " +
                                                  "RemoteHub asks for a password when it connects and keeps it in memory only.");
        }
        // Policy SettingsLocation = Local with a shared folder chosen before: its settings aren't used – say so, not silently.
        if (SettingsStore.IgnoredSharedFolder is { } ignored)
            await Dialogs.Info(this, "Settings location", $"Your organisation keeps RemoteHub's settings on this PC only, so the shared settings in {ignored} " +
                                                          "aren't used. The shared file isn't changed.");
        // Not awaited: the update check below starts while the tour or What's new is open.
        _ = Dispatcher.BeginInvoke(() => ShowFirstStart(ui));
        // Everything below runs on a background thread (policy lookup, network, parsing); only the result comes back here.
        var check = await Task.Run(async () =>
        {
            if (!UpdateService.CheckEnabled(ui)) return default((string, UpdateCheckResult)?);
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var ch = UpdateService.EffectiveChannel(ui);
            return (ch, await UpdateService.CheckAsync(ch).ConfigureAwait(false));
        });
        if (check is not var (channel, result)) return;
        DiagLog.Write($"Update check ({channel}): {result.Status}" + (result.Info is { } i ? $", latest {i.Version}" : ""));
        if (result.Status != UpdateStatus.Available) return;
        if (result.Required) { ShowUpdateWindow(result, channel); return; }
        if (result.Info!.Version == ui.SkippedUpdateVersion) return;
        _availableUpdate = result;
        UpdateRailVersion();
        UpdateBannerText.Text = $"RemoteHub {result.Info.Version} is available – you have {UpdateService.Current}.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void UpdateDetails_Click(object sender, RoutedEventArgs e) => ShowUpdateWindow(_availableUpdate, null);

    private void UpdateBannerClose_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => ShowUpdateWindow(null, null);

    /// <summary>Shows an update (or checks now when <paramref name="result"/> is null), owned by whichever window is in front.</summary>
    private void ShowUpdateWindow(UpdateCheckResult? result, string? channel)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        var host = new UpdateHost(ConfirmExitForUpdate, ExitForUpdate, _vm.RequestSave);
        new UpdateWindow(_vm.Data.Ui, host, result, channel) { Owner = owner }.ShowDialog();
        if (_availableUpdate?.Info is { } shown && shown.Version == _vm.Data.Ui.SkippedUpdateVersion) UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> ConfirmExitForUpdate()
    {
        var active = _vm.Sessions.Count(s => s.State is SessionState.Connected or SessionState.Connecting);
        var transfers = _vm.ActiveTransfers;
        if (active == 0 && transfers == 0) return true;
        var what = string.Join(" and ", new[]
        {
            active > 0 ? $"{active} session(s) are still connected" : null,
            transfers > 0 ? $"{transfers} file transfer(s) are not finished" : null,
        }.Where(x => x is not null));
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? this;
        return await Dialogs.Confirm(owner, "Install update", $"{what}. Installing the update disconnects them and closes RemoteHub. Continue?", "Install");
    }

    private void ExitForUpdate()
    {
        _forceClose = true;
        Shutdown();
        Close();
    }

    /// <summary>"New" (and + on the collapsed rail): new connection, new folder, import .rdp files.</summary>
    private void NewMenu_Click(object sender, RoutedEventArgs e)
    {
        var cm = NewButton.ContextMenu!;
        NewMenuShare.Visibility = Features.IsAllowed(Feature.NetworkShares) ? Visibility.Visible : Visibility.Collapsed;
        NewMenuImport.Visibility = NewMenuImportSep.Visibility = Features.IsAllowed(Feature.RdpFileImport) ? Visibility.Visible : Visibility.Collapsed;
        cm.PlacementTarget = sender as UIElement ?? NewButton;
        cm.Placement = ReferenceEquals(sender, NewButton) ? PlacementMode.Bottom : PlacementMode.Right;
        cm.IsOpen = true;
    }

    // ---------- collapsible navigation ----------

    private const double RailWidth = 76;

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        var ui = _vm.Data.Ui;
        if (!ui.SidebarCollapsed) ui.SidebarWidth = SidebarColumn.ActualWidth;
        ui.SidebarCollapsed = !ui.SidebarCollapsed;
        ApplySidebarState();
        _vm.RequestSave();
    }

    private void ApplySidebarState()
    {
        var ui = _vm.Data.Ui;
        if (ui.SidebarCollapsed)
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(RailWidth);
            SidebarFull.Visibility = Visibility.Collapsed;
            SidebarRail.Visibility = Visibility.Visible;
            MainSplitter.IsEnabled = false;
        }
        else
        {
            SidebarColumn.MinWidth = 300;
            SidebarColumn.Width = new GridLength(Math.Max(300, ui.SidebarWidth));
            SidebarFull.Visibility = Visibility.Visible;
            SidebarRail.Visibility = Visibility.Collapsed;
            MainSplitter.IsEnabled = true;
        }
    }

    private void RailSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Data.Ui.SidebarCollapsed) ToggleSidebar();
        SearchBox.Focus();
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_vm.Data.Ui.SidebarCollapsed) return;
        _vm.Data.Ui.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.RequestSave();
    }

    // =====================================================================
    // Drag & drop: moving nodes and importing .rdp files
    // =====================================================================

    private enum DropPos { Into, Before, After }

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        // A server's shares stay with it; a share on its own moves like a connection.
        _dragCandidate = FindAncestor<ToggleButton>(e.OriginalSource) is null
                         && FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is NodeBase n and not NetworkShare { Server: not null }
            ? n
            : null;
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(Tree, new DataObject(NodeFormat, node), DragDropEffects.Move | DragDropEffects.Link); // Link: pinned to Favourites
        HideDropIndicator();
    }

    private (NodeBase? Target, DropPos Pos, FrameworkElement? Header) HitTest(DragEventArgs e)
    {
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource);
        // Over a server's share: as if below the server (shares themselves don't move).
        if (tvi?.DataContext is NetworkShare { Server: { } server }) return (server, DropPos.After, tvi.Template.FindName("PART_Header", tvi) as FrameworkElement ?? tvi);
        if (tvi?.DataContext is not NodeBase target) return (null, DropPos.Into, null);
        var header = tvi.Template.FindName("PART_Header", tvi) as FrameworkElement ?? tvi;
        var y = e.GetPosition(header).Y;
        var h = Math.Max(1, header.ActualHeight);
        // Folders and connections take items in their middle (a connection: as their jump host); the edges mean before/after.
        DropPos pos = target switch
        {
            Folder f => y < h * 0.25 ? DropPos.Before : y > h * 0.75 && !f.IsExpanded ? DropPos.After : DropPos.Into,
            Connection => y < h * 0.3 ? DropPos.Before : y > h * 0.7 ? DropPos.After : DropPos.Into,
            _ => y < h / 2 ? DropPos.Before : DropPos.After,
        };
        return (target, pos, header);
    }

    /// <summary>Where a dragged item lands: (container, index; −1 = at the end). A share never goes into a connection here.</summary>
    private (NodeBase? Into, int Index) DropPlace(NodeBase node, NodeBase? target, DropPos pos)
    {
        if (target is null) return (null, -1);
        if (pos == DropPos.Into && target is Folder or Connection && !(node is NetworkShare && target is Connection)) return (target, -1);
        if (pos == DropPos.Into) pos = DropPos.After;
        return (target.Parent, _vm.ChildrenOf(target.Parent).IndexOf(target) + (pos == DropPos.After ? 1 : 0));
    }

    /// <summary>Why the dragged item can't go where it is over – shown next to the pointer.</summary>
    private ToolTip? _dropRefusal;

    private void ShowDropRefusal(string? reason)
    {
        if (reason is null)
        {
            if (_dropRefusal is not null) _dropRefusal.IsOpen = false;
            return;
        }
        if (_dropRefusal is { IsOpen: true } && Equals(_dropRefusal.Content, reason)) return;
        _dropRefusal ??= new ToolTip { PlacementTarget = Tree, Placement = PlacementMode.Mouse };
        _dropRefusal.IsOpen = false;
        _dropRefusal.Content = reason;
        _dropRefusal.IsOpen = true;
    }

    private static string[] RdpFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        // Over Favourites: a connection dropped there is pinned (a link); nothing is moved into it.
        if (OverFavorites(e))
        {
            HideDropIndicator();
            var pin = RdpFiles(e).Length == 0 && e.Data.GetData(NodeFormat) is Connection;
            e.Effects = pin ? DragDropEffects.Link : DragDropEffects.None;
            ShowDropRefusal(pin || RdpFiles(e).Length > 0 ? null : "Only connections can be pinned to Favourites");
            e.Handled = true;
            return;
        }
        var (target, pos, header) = HitTest(e);
        if (RdpFiles(e).Length > 0)
        {
            e.Effects = DragDropEffects.Copy;
            ShowDropIndicator(target is Folder ? header : null, DropPos.Into);
        }
        else if (e.Data.GetData(NodeFormat) is NodeBase node)
        {
            var (into, _) = DropPlace(node, target, pos);
            if (pos == DropPos.Into && into != target) pos = DropPos.After;
            var why = target == node ? null : WhyNotMove(node, into);
            var ok = target != node && why is null;
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            if (ok) ShowDropIndicator(header, pos); else HideDropIndicator();
            ShowDropRefusal(why);
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }


    /// <summary>The pointer is over the Favourites group or one of its links.</summary>
    private static bool OverFavorites(DragEventArgs e)
    {
        for (var tvi = FindAncestor<TreeViewItem>(e.OriginalSource); tvi is not null; tvi = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(tvi)))
            if (tvi.DataContext is FavoritesGroup or FavoriteLink) return true;
        return false;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        ShowDropRefusal(null);
    }

    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        ShowDropRefusal(null);
        e.Handled = true;
        if (OverFavorites(e))
        {
            if (RdpFiles(e).Length == 0 && e.Data.GetData(NodeFormat) is Connection pinned) _vm.SetFavorite(pinned, true);
            return;
        }
        var (target, pos, _) = HitTest(e);
        var files = RdpFiles(e);
        if (files.Length > 0)
        {
            // Into the folder or jump host under the pointer; where a connection can't go (below a jump host's child), next to it.
            var into = target is Folder or Connection && pos == DropPos.Into ? target : target?.Parent;
            if (into is not null && WhyNotMove(new Connection(), into) is not null) into = into.Parent;
            ImportRdpFiles(files, into, connectAfterImport: false);
        }
        else if (e.Data.GetData(NodeFormat) is NodeBase node && target != node)
        {
            var (into, idx) = DropPlace(node, target, pos);
            if (WhyNotMove(node, into) is not null) return;
            if (!await ConfirmMove(node, into)) return;
            _vm.Move(node, into, idx);
            node.IsSelected = true;
        }
    }

    /// <summary>
    /// Moving an item into another folder (or out of one) changes the credentials it inherits – easy to do by accident
    /// when dropping just below a folder. Asks first, naming both sources.
    /// </summary>
    private async Task<bool> ConfirmMove(NodeBase node, NodeBase? into)
    {
        if (_vm.CredentialChange(node, into) is not { } change) return true;
        var where = into switch
        {
            null => "the top level (outside all folders)",
            Connection c => $"connection \"{c.Name}\" (reached through it)",
            _ => $"folder \"{into.Name}\"",
        };
        var inside = node is Folder ? " Items inside it that inherit credentials change too." : "";
        var text = $"Move \"{node.Name}\" to {where}?\n\nIts credentials change:\n   now: {change.From}\n   after: {change.To}\n\n" +
                   $"Remote desktop and PowerShell remoting will use the new credentials.{inside}";
        return await Dialogs.Confirm(this, "Move – credentials change", text, "Move");
    }

    /// <summary>Tree tooltip: where the item is and where its credentials come from.</summary>
    private void TreeItem_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if ((fe.DataContext as NodeBase ?? (fe.DataContext as SearchHit)?.Node) is not { } n) { e.Handled = true; return; }
        var (_, source) = Inheritance.ResolveCredentials(n, _vm.Data);
        var lines = new List<string> { Inheritance.Location(n), $"Credentials: {source}" };
        if (n is Folder f) lines.Add(f.Children.Count == 0 ? "Empty folder" : $"{f.Descendants().OfType<Connection>().Count()} connection(s) inside");
        if (n is Connection { AgentMode: not AgentMode.None } c)
            lines.Add(c.AgentMode == AgentMode.Active ? "RemoteHub Agent: active" : "RemoteHub Agent: installed, not used (connection uses PowerShell remoting)");
        fe.ToolTip = string.Join("\n", lines);
    }

    private void ShowDropIndicator(FrameworkElement? header, DropPos pos)
    {
        if (header is null) { HideDropIndicator(); return; }
        var container = (FrameworkElement)DropIndicator.Parent;
        var top = header.TranslatePoint(new Point(0, 0), container).Y;
        DropIndicator.Visibility = Visibility.Visible;
        if (pos == DropPos.Into)
        {
            DropIndicator.Height = header.ActualHeight;
            DropIndicator.Background = Brushes.Transparent;
            DropIndicator.BorderBrush = (Brush)FindResource("SystemAccentColorPrimaryBrush");
            DropIndicator.BorderThickness = new Thickness(1.5);
            DropIndicator.CornerRadius = new CornerRadius(4);
            DropIndicator.Margin = new Thickness(0, top, 0, 0);
        }
        else
        {
            DropIndicator.Height = 2;
            DropIndicator.Background = (Brush)FindResource("SystemAccentColorPrimaryBrush");
            DropIndicator.BorderThickness = new Thickness(0);
            DropIndicator.Margin = new Thickness(header.TranslatePoint(new Point(0, 0), container).X, top + (pos == DropPos.After ? header.ActualHeight : 0) - 1, 0, 0);
        }
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = RdpFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var files = RdpFiles(e);
        if (files.Length > 0) ImportRdpFiles(files, _vm.TargetFolderForNew(), connectAfterImport: false);
    }

    public void ImportRdpFiles(IEnumerable<string> files, NodeBase? target, bool connectAfterImport)
    {
        // Opening a .rdp file connects over RDP, so that switch decides too.
        if (Refused(Feature.RdpFileImport) || (connectAfterImport && Refused(Feature.Rdp))) return;
        var errors = new List<string>();
        Connection? last = null;
        var lastIsNew = false;
        foreach (var file in files)
        {
            try
            {
                var c = RdpFileParser.Parse(file);
                if (!Features.IsAllowed(Feature.SavedPasswords)) c.Credentials.Password = null; // a password in the file isn't kept (§5.6)
                // Opening a .rdp file (e.g. via file association) reuses an existing entry for the same server.
                if (connectAfterImport && _vm.AllNodes().OfType<Connection>().FirstOrDefault(x =>
                        string.Equals(x.Host, c.Host, StringComparison.OrdinalIgnoreCase) && x.Port == c.Port) is { } existing)
                {
                    (last, lastIsNew) = (existing, false);
                    continue;
                }
                // A read-only tree (policy AllowConnectionEditing = 0) takes no new entries; an existing one still opens above.
                if (Features.WhyNot(Feature.ConnectionEditing) is { } readOnly) { errors.Add($"{Path.GetFileName(file)}: {readOnly}"); continue; }
                _vm.Add(c, target);
                (last, lastIsNew) = (c, true);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        if (last is not null)
        {
            last.IsSelected = true;
            // An entry the user already has connects at once; one from the file only after they've seen where it goes.
            if (connectAfterImport && lastIsNew) _ = ConfirmAndOpen(last);
            else if (connectAfterImport) OpenConnection(last);
        }
        if (errors.Count > 0) _ = Dialogs.Info(this, "Import .rdp files", string.Join("\n", errors));
    }

    /// <summary>A connection imported from an opened .rdp file: shows the server and what the file sets, then connects.</summary>
    private async Task ConfirmAndOpen(Connection c)
    {
        var port = c.Port is 3389 ? "" : $":{c.Port}";
        var text = $"Connect to {c.Host}{port}?\n\nThe connection was added from a .rdp file. Only connect if you trust where the file came from.\n\n"
            + string.Join("\n", RdpFileParser.Risks(c).Select(r => "• " + r));
        if (await Dialogs.Confirm(this, "Open .rdp file", text, "Connect")) OpenConnection(c);
    }

    private void ImportRdp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Remote Desktop files (*.rdp)|*.rdp", Multiselect = true };
        if (dlg.ShowDialog(this) == true) ImportRdpFiles(dlg.FileNames, _vm.TargetFolderForNew(), false);
    }

    // =====================================================================
    // Backup
    // =====================================================================

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.BackupExport)) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "RemoteHub backup (*.rhbackup)|*.rhbackup",
            FileName = $"RemoteHub-{DateTime.Now:yyyy-MM-dd}.rhbackup",
        };
        if (dlg.ShowDialog(this) != true) return;
        var pw = new PasswordDialog(confirm: true) { Owner = this };
        if (pw.ShowDialog() != true) return;
        try
        {
            _ = Notes?.Save(sync: true);
            var notes = Data.LocalDb.IsOpen ? Data.NoteStore.ExportAll() : null;
            Backup.Export(_vm.Data, dlg.FileName, pw.Password, notes);
            Data.AuditLog.Write(Data.AuditEvents.BackupExported, null, Path.GetFileName(dlg.FileName),
                $"{_vm.AllNodes().Count()} folders and connections, {_vm.Data.SavedCredentials.Count} saved credentials, {notes?.Pages.Count ?? 0} pages");
            _ = Dialogs.Info(this, "Export backup", $"Backup saved to\n{dlg.FileName}\n\nKeep the password safe – it cannot be recovered.");
        }
        catch (Exception ex) { _ = Dialogs.Info(this, "Export failed", ex.Message); }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        if (Refused(Feature.ConnectionEditing)) return; // read-only tree by policy (docs/admx-design.md §5.4)
        if (Refused(Feature.BackupImport)) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "RemoteHub backup (*.rhbackup;*.rdpmbackup)|*.rhbackup;*.rdpmbackup|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var pw = new PasswordDialog(confirm: false) { Owner = this };
        if (pw.ShowDialog() != true) return;

        AppData imported;
        Data.NotesExport? importedNotes;
        try { imported = Backup.Import(dlg.FileName, pw.Password, out importedNotes); }
        catch (Exception ex) { await Dialogs.Info(this, "Import failed", ex.Message); return; }
        // Policy AllowSavedPasswords = 0: what comes in is stored without its passwords (§5.6).
        if (!Features.IsAllowed(Feature.SavedPasswords) && SavedPasswords.Remove(imported, imported.Items.SelectMany(i => (IEnumerable<NodeBase>)[i, .. i.Descendants()])) is > 0 and var dropped)
            DiagLog.Write($"Backup import: {dropped} passwords left out (policy AllowSavedPasswords)");

        var choice = await Dialogs.Ask(this, "Import backup",
            "Add the backup as a new folder next to your current connections, or replace everything?",
            "Add as folder", "Replace everything");
        if (choice is Wpf.Ui.Controls.MessageBoxResult.Primary or Wpf.Ui.Controls.MessageBoxResult.Secondary)
            Data.AuditLog.Write(Data.AuditEvents.BackupImported, null, Path.GetFileName(dlg.FileName),
                (choice == Wpf.Ui.Controls.MessageBoxResult.Primary ? "Added as a folder" : "Replaced all settings") + (importedNotes is { IsEmpty: false } n0 ? $", and {n0.Pages.Count} pages" : ""));
        if (choice is Wpf.Ui.Controls.MessageBoxResult.Primary or Wpf.Ui.Controls.MessageBoxResult.Secondary && importedNotes is { IsEmpty: false } notes && Data.LocalDb.IsOpen)
        {
            // Added: the backup's notebooks come in next to yours, named after the file. Replaced: yours go to Recently deleted.
            _ = Notes?.Save(sync: true);
            try
            {
                Data.NoteStore.Import(notes, replace: choice == Wpf.Ui.Controls.MessageBoxResult.Secondary,
                    addedSuffix: choice == Wpf.Ui.Controls.MessageBoxResult.Primary ? Path.GetFileNameWithoutExtension(dlg.FileName) : null);
                Notes?.Reload();
            }
            catch (Exception ex) { await Dialogs.Info(this, "Import failed", "The notes in the backup could not be imported: " + ex.Message); }
        }
        if (choice == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            foreach (var sc in imported.SavedCredentials)
                if (Inheritance.FindSaved(_vm.Data, sc.Id) is null) _vm.Data.SavedCredentials.Add(sc);
            var folder = new Folder
            {
                Name = $"Imported {DateTime.Now:yyyy-MM-dd HH:mm}",
                Options = imported.Defaults.Clone(),
                InheritCredentials = imported.DefaultCredentials.IsEmpty && imported.DefaultCredentialId is null,
                CredentialId = imported.DefaultCredentialId,
                Credentials = imported.DefaultCredentials.Clone(),
            };
            foreach (var n in imported.Items) folder.Children.Add(n);
            SettingsStore.RelinkParents(folder.Children, folder);
            _vm.Add(folder, null);
        }
        else if (choice == Wpf.Ui.Controls.MessageBoxResult.Secondary)
        {
            imported.Ui = _vm.Data.Ui;
            _vm.ReplaceData(imported);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(DiagLog.FilePath)) DiagLog.Write("Log opened (no events yet).");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{DiagLog.FilePath}\"") { UseShellExecute = true });
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.Directory}\"") { UseShellExecute = true });
    }

    // =====================================================================
    // Shutdown
    // =====================================================================

    private bool _closePromptOpen;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        var active = _vm.Sessions.Count(s => s.State is SessionState.Connected or SessionState.Connecting);
        var transfers = _vm.ActiveTransfers;
        var recordings = _recorders.Count;
        if (active > 0 || transfers > 0 || recordings > 0)
        {
            // WPF forbids showing windows (the confirmation) or calling Close() while the window is closing,
            // so cancel this close and ask once the Closing event has finished.
            e.Cancel = true;
            if (_closePromptOpen) return;
            _closePromptOpen = true;
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var what = string.Join(" and ", new[]
                    {
                        active > 0 ? $"{active} session(s) are still connected" : null,
                        transfers > 0 ? $"{transfers} file transfer(s) are not finished" : null,
                        recordings > 0 ? $"{recordings} tab(s) are being recorded" : null,
                    }.Where(x => x is not null));
                    var saved = recordings > 0 ? " Recordings are saved." : "";
                    if (!await Dialogs.Confirm(this, "Exit", $"{what}. Disconnect and exit?{saved}", "Exit")) return;
                    _forceClose = true;
                    Shutdown();
                    Close();
                }
                finally { _closePromptOpen = false; }
            });
            return;
        }
        Shutdown();
    }

    /// <param name="locked">Exited from the auto-lock: the settings key is gone – they were saved when it locked.</param>
    private void Shutdown(bool locked = false)
    {
        if (!locked) _ = Notes?.Save(sync: true); // the open page, before the database closes
        if (!locked)
        {
            var ui = _vm.Data.Ui;
            ui.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { ui.Width = Width; ui.Height = Height; }
            if (!ui.SidebarCollapsed) ui.SidebarWidth = SidebarColumn.ActualWidth;
            _vm.SaveNow();
        }
        _vm.EndOpenSessions(); // before the sessions are disposed: their history entries get an end time

        StopAllRecordings();
        SecureClipboard.ClearIfOurs();
        _toast?.Close();
        _promptWatcher.Dispose();
        foreach (var w in _detached.Values.ToList()) { w.ReleaseContent(); w.ForceClose(); }
        _detached.Clear();
        foreach (var t in _vm.Transfers) t.Cancel();
        foreach (var s in _vm.Sessions.ToList())
        {
            s.Disconnect();
            s.Dispose();
        }
        // Give remote PowerShell sessions a moment to close cleanly (otherwise they linger on the server until they time out),
        // and agent desktops time to have their close read (otherwise the agent may take it for a drop). All in parallel;
        // each agent close gives up after AgentDesktopClient.CloseLimit on its own.
        try { Task.WaitAll([.. PsSession.Closing.Keys, .. AgentDesktopClient.Closing.Keys], TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    internal static T? FindAncestor<T>(object? o) where T : DependencyObject
    {
        var d = o as DependencyObject;
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is { } r) return r;
        }
        return null;
    }
}
