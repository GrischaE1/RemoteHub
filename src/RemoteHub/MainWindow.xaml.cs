using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        DataContext = vm;

        var ui = vm.Data.Ui;
        Width = ui.Width;
        Height = ui.Height;
        SidebarColumn.Width = new GridLength(ui.SidebarWidth);
        ApplySidebarState();
        if (ui.Maximized) WindowState = WindowState.Maximized;
        SwitchHotkeys.Current = SwitchHotkeys.Get(ui.SwitchKeys);
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
        Loaded += (_, _) => ApplyTheme(vm.Data.Ui.Theme);
        Loaded += (_, _) => StartupChecks();
        Closing += Window_Closing;
    }

    // =====================================================================
    // Theme
    // =====================================================================

    private void ApplyTheme(string theme)
    {
        _vm.Data.Ui.Theme = theme;
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
        if (pick is null || pick.PasswordPrompt == PasswordPromptAction.Off) return;
        var password = pick.UsedCredentials.Password!;
        var name = pick.Connection.Name;

        CopyPasswordForPrompt(dialog, name, password, pick);
    }

    private void CopyPasswordForPrompt(IntPtr dialog, string connectionName, string password, RdpSession session)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!SecureClipboard.CopySecret(hwnd, password, TimeSpan.FromSeconds(ClipboardSeconds))) return;
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
        _vm.Data.Ui.SwitchKeys = SwitchHotkeys.Current.Id;
        foreach (var s in _vm.Sessions.OfType<RdpSession>()) s.RefreshSwitchHotkeys();
        UpdateShortcutHint();
    }

    private void UpdateShortcutHint()
    {
        ShortcutHint.Text = "Ctrl+N new connection  ·  Ctrl+F search / quick connect\n" +
                            SwitchHotkeys.Current.Hint + " – also inside a session and in full screen (use the left Alt key)";
    }


    // =====================================================================
    // Session hosting
    // =====================================================================

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSession)) UpdateSessionVisibility();
        if (e.PropertyName == nameof(MainViewModel.Items)) HookItems();
        if (e.PropertyName is nameof(MainViewModel.HasTransfers) or nameof(MainViewModel.ShowTransfers)) ApplyTransfersPane();
    }

    private void HookItems()
    {
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

    /// <summary>Opens a connection, or switches to its existing session.</summary>
    public void OpenConnection(Connection c, bool forceNew = false)
    {
        if (!forceNew && _vm.FindSession(c) is { } existing)
        {
            _vm.SelectedSession = existing;
            if (existing.State is SessionState.Disconnected or SessionState.Failed) _vm.StartSession(existing);
            return;
        }

        var s = _vm.OpenSession(c);
        var view = new SessionView(s, () => _vm.StartSession(s), () => s.Close());
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
            if (s is RdpSession r)
            {
                tools.Add((SymbolRegular.Keyboard24, "Ctrl+Alt+Del", "Send Ctrl+Alt+Del", r.SendCtrlAltDel));
                tools.Add((SymbolRegular.FullScreenMaximize24, null, "Full screen", r.EnterFullScreen));
            }
            else if (s is PsSession p)
            {
                tools.Add((SymbolRegular.FolderSwap24, "File transfer", "Open a file transfer tab at the current remote folder", () => OpenFileTransfer(p.Connection, p.Location, p.Target)));
                tools.Add((SymbolRegular.CubeTree24, "Registry", "Open the remote registry in a new tab", () => OpenRegistry(p.Connection, p.Target)));
                tools.Add((SymbolRegular.WrenchScrewdriver24, "Services", "Open the remote services in a new tab", () => OpenServices(p.Connection, p.Target)));
                tools.Add((SymbolRegular.Pulse24, "Processes", "Open the remote processes in a new tab", () => OpenProcesses(p.Connection, p.Target)));
            }
            var win = new DetachedWindow(s, view, tools)
            {
                Width = Math.Max(800, SessionPanel.ActualWidth),
                Height = Math.Max(600, SessionPanel.ActualHeight + 80),
            };
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
        if (s is RdpSession r) _vm.StartSession(r);
        else if (s is PsSession p) ConnectPs(p);
        else if (s is FileBrowserSession f) ConnectFileBrowser(f);
        else if (s is RemoteToolSession t) ConnectTool(t);
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) Restart(s); }
    private void Disconnect_Click(object sender, RoutedEventArgs e) => _vm.SelectedSession?.Disconnect();
    private void CtrlAltDel_Click(object sender, RoutedEventArgs e) => (_vm.SelectedSession as RdpSession)?.SendCtrlAltDel();
    private void FullScreen_Click(object sender, RoutedEventArgs e) => (_vm.SelectedSession as RdpSession)?.EnterFullScreen();
    private void ToolbarSendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSession is PsSession p) OpenFileTransfer(p.Connection, p.Location, p.Target);
    }
    private void Detach_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) ToggleDetach(s); }
    private void ShowDetached_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s && _detached.TryGetValue(s, out var w)) w.Activate(); }
    private void EditSessionConnection_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedSession is { } s) EditNode(s.Connection); }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        SessionOf(sender)?.Close();
        e.Handled = true;
    }

    private void Tabs_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && FindAncestor<ListBoxItem>(e.OriginalSource)?.DataContext is SessionBase s)
        {
            s.Close();
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
            _vm.SelectNextSession(sw.ShiftReverses ? (shift ? -1 : 1) : key == sw.Prev ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.N) { if (shift) NewFolder_Click(this, e); else NewConnection_Click(this, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.F)
        {
            if (_vm.Data.Ui.SidebarCollapsed) ToggleSidebar();
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (ctrl && e.Key == Key.B) { ToggleSidebar(); e.Handled = true; }
        else if (ctrl && e.Key == Key.OemComma) { OpenSettings(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab) { _vm.SelectNextSession(shift ? -1 : 1); e.Handled = true; }
        else if (ctrl && e.Key == Key.W && _vm.SelectedSession is { } s) { s.Close(); e.Handled = true; }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
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
        if (address.Contains(' ') || address.Length == 0) return;
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

    /// <summary>Connection: open it. Folder: jump to it in the list.</summary>
    private void ActivateHit(SearchHit hit)
    {
        if (hit.Node is Connection c) OpenConnection(c);
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
        foreach (var a in node.Ancestors()) a.IsExpanded = true;
        node.IsSelected = true;
        Dispatcher.BeginInvoke(() =>
        {
            ItemsControl parent = Tree;
            foreach (var level in node.Ancestors().Reverse().Cast<NodeBase>().Append(node))
            {
                parent.UpdateLayout();
                if (parent.ItemContainerGenerator.ContainerFromItem(level) is not TreeViewItem tvi) return;
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
        _vm.SelectedNode = e.NewValue as NodeBase;

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext is Connection c)
        {
            OpenConnection(c);
            e.Handled = true;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource) is { } tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
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
        CmConnect.Visibility = V(n is Connection);
        CmConnectNew.Visibility = V(n is Connection);
        CmConnectAll.Visibility = V(n is Folder);
        CmSep1.Visibility = V(n is not null);
        CmEdit.Visibility = CmDuplicate.Visibility = CmDelete.Visibility = V(n is not null);
        CmSaveRdp.Visibility = V(n is not null);
        CmSaveRdp.Header = n is Folder ? "Export folder as .rdp files…" : "Save as .rdp file…";

        var c = n as Connection;
        var psOn = c is not null; // manual test always offered; the setting only controls the automatic check
        var st = c?.PsRemoting?.State ?? PsRemotingState.Unknown;
        CmPsSep.Visibility = V(psOn);
        CmPs.Visibility = CmSendFiles.Visibility = CmRegistry.Visibility = CmServices.Visibility = CmProcesses.Visibility = V(psOn && st == PsRemotingState.Available);
        CmPsTest.Visibility = V(psOn && st != PsRemotingState.Available);
        CmPsTest.Header = c?.PsChecking == true ? "Testing PowerShell remoting…"
                        : st == PsRemotingState.Unavailable ? "Test PowerShell remoting again" : "Test PowerShell remoting";
        CmPsTest.IsEnabled = c?.PsChecking != true;
        CmPsTest.ToolTip = st == PsRemotingState.Unavailable ? $"Last result: {c!.PsRemoting!.Message}" : null;
        CmPsSetup.Visibility = V(psOn && st == PsRemotingState.Unavailable);
    }

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
        mi.Click += (_, _) => action();
        return mi;
    }

    /// <summary>Credentials already in use by a remoting tab of this connection (no second password prompt).</summary>
    private PsTarget? KnownTarget(Connection c) => _vm.Sessions.Where(x => x.Connection == c)
        .Select(x => x switch { RemoteToolSession r => r.Target, PsSession p => p.Target, _ => null }).FirstOrDefault(t => t is not null);

    /// <summary>Tabs that can be opened for a connection: remote desktop, and PowerShell / file transfer / registry (or the remoting test).</summary>
    private void AddConnectionTools(ItemsControl cm, Connection c, SessionBase? from)
    {
        var rdp = _vm.FindSession(c);
        cm.Items.Add(MenuEntry(rdp is null ? "Remote desktop" : "Show remote desktop", SymbolRegular.Desktop24, () => OpenConnection(c)));
        if (rdp is not null) cm.Items.Add(MenuEntry("Additional remote desktop session", SymbolRegular.WindowNew24, () => OpenConnection(c, forceNew: true)));
        var st = c.PsRemoting?.State ?? PsRemotingState.Unknown;
        var ps = from as PsSession;
        if (st == PsRemotingState.Available)
        {
            cm.Items.Add(MenuEntry("PowerShell", SymbolRegular.WindowConsole20, () => OpenPowerShell(c)));
            cm.Items.Add(MenuEntry("File transfer", SymbolRegular.FolderSwap24, () => OpenFileTransfer(c, ps?.Location, ps?.Target ?? KnownTarget(c))));
            cm.Items.Add(MenuEntry("Registry", SymbolRegular.CubeTree24, () => OpenRegistry(c, KnownTarget(c))));
            cm.Items.Add(MenuEntry("Services", SymbolRegular.WrenchScrewdriver24, () => OpenServices(c, KnownTarget(c))));
            cm.Items.Add(MenuEntry("Processes", SymbolRegular.Pulse24, () => OpenProcesses(c, KnownTarget(c))));
        }
        else
            cm.Items.Add(MenuEntry(c.PsChecking ? "Testing PowerShell remoting…" : st == PsRemotingState.Unavailable ? "Test PowerShell remoting again" : "Test PowerShell remoting",
                SymbolRegular.ArrowSync24, () => _ = TestPsRemoting(c, interactive: true), !c.PsChecking,
                st == PsRemotingState.Unavailable ? $"Last result: {c.PsRemoting!.Message}" : null));
        if (st == PsRemotingState.Unavailable)
            cm.Items.Add(MenuEntry("Set up PowerShell remoting on the target…", SymbolRegular.Code24, () => _ = ShowRemotingSetup(c)));
    }

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
        ShowMenu(cm, AddTabButton, PlacementMode.Bottom);
    }

    private void CloseGroup(SessionGroup g)
    {
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
        cm.Items.Add(new Separator());
        cm.Items.Add(MenuEntry("Edit connection…", SymbolRegular.Edit24, () => EditNode(g.Connection)));
        cm.Items.Add(new Separator());
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
        cm.Items.Add(new Separator());
        Add("Reconnect", SymbolRegular.ArrowClockwise24, () => Restart(s));
        Add("Disconnect", SymbolRegular.PlugDisconnected24, s.Disconnect, s.State is SessionState.Connected or SessionState.Connecting);
        if (s is RdpSession r)
        {
            Add("Send Ctrl+Alt+Del", SymbolRegular.Keyboard24, r.SendCtrlAltDel, r.IsConnected);
            Add("Full screen", SymbolRegular.FullScreenMaximize24, r.EnterFullScreen, r.IsConnected);
        }
        Add(s.IsDetached ? "Dock back" : "Detach into its own window", SymbolRegular.WindowNew24, () => ToggleDetach(s));
        Add("Edit connection…", SymbolRegular.Edit24, () => EditNode(c));
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

    private Brush AccentFor(Connection c) =>
        ColorTags.Brush(Inheritance.ResolveOptions(c, _vm.Data).ColorTag) ?? ColorTags.Frozen(AppChrome.Accent);

    /// <summary>
    /// Host, protocol and the connection's (inherited) credentials. Without a stored password the user is asked;
    /// that password stays in memory only. Returns null when the user cancels.
    /// </summary>
    private (PsTarget Target, string Source)? BuildTarget(Connection c, PsTarget? previous)
    {
        var opt = Inheritance.ResolveOptions(c, _vm.Data);
        var (resolved, source) = Inheritance.ResolveCredentials(c, _vm.Data);
        var cred = resolved.Clone();
        if (cred.IsEmpty) source = "your Windows account (no credentials set)";
        if (PsRemoting.NeedsPassword(cred))
        {
            if (previous?.Credentials is { Password.Length: > 0 } p && p.Username == cred.Username && p.Domain == cred.Domain)
                cred.Password = p.Password;
            else
            {
                var dlg = new PasswordDialog(confirm: false, title: "PowerShell remoting",
                    message: $"No password is stored for {PsRemoting.UserName(cred, c.Host)}. Enter it to connect to {c.Name} – it is kept in memory only.",
                    okText: "Connect") { Owner = this };
                if (dlg.ShowDialog() != true) return null;
                cred.Password = dlg.Password;
                source += ", password entered";
            }
        }
        return (new PsTarget(c.Host.Trim(), opt.PsUseSsl == true, cred), source);
    }

    private void SetPsStatus(Connection c, PsRemotingState state, string? message)
    {
        var old = c.PsRemoting;
        if (old is not null && old.State == state && old.Message == message && DateTime.UtcNow - old.CheckedUtc < TimeSpan.FromMinutes(1)) return;
        c.PsRemoting = new PsRemotingStatus { State = state, Message = message, CheckedUtc = DateTime.UtcNow, UploadFolder = old?.UploadFolder, RegistryPath = old?.RegistryPath };
        _vm.RequestSave();
    }

    /// <summary>First RDP login of a connection with the check switched on: test PowerShell remoting in the background.</summary>
    private void AutoCheckPsRemoting(RdpSession s)
    {
        var c = s.Connection;
        if (!PsEnabled(c) || c.PsChecking || (c.PsRemoting?.State ?? PsRemotingState.Unknown) != PsRemotingState.Unknown) return;
        var cred = s.UsedCredentials.Clone();
        if (PsRemoting.NeedsPassword(cred)) return; // would have to ask – the user can start the test from the menu
        _ = TestPsRemoting(c, interactive: false, cred);
    }

    private async Task TestPsRemoting(Connection c, bool interactive, CredentialSet? credentials = null)
    {
        if (c.PsChecking) return;
        PsTarget target;
        if (credentials is not null)
            target = new PsTarget(c.Host.Trim(), Inheritance.ResolveOptions(c, _vm.Data).PsUseSsl == true, credentials);
        else if (BuildTarget(c, null) is { } t) target = t.Target;
        else return;

        c.PsChecking = true;
        DiagLog.Write($"PowerShell remoting check started ({(target.UseSsl ? "HTTPS" : "HTTP")}, interactive={interactive})");
        PsRemotingStatus st;
        try { st = await PsRemoting.TestAsync(target); }
        finally { c.PsChecking = false; }
        DiagLog.Write($"PowerShell remoting check: {st.State}");
        SetPsStatus(c, st.State, st.Message);
        if (!interactive) return;
        if (st.State == PsRemotingState.Available)
            await Dialogs.Info(this, "PowerShell remoting", $"PowerShell remoting works ({st.Message}).\n\nRight-click the connection or its tab → PowerShell or File transfer.");
        else if (await Dialogs.Confirm(this, "PowerShell remoting",
                     $"PowerShell remoting is not available for {c.Name}.\n\n{st.Message}\n\n" +
                     "RemoteHub can create a ready-to-run setup script (with this PC's address and a certificate for HTTPS) to paste into an elevated PowerShell on the target.",
                     "Set up on the target…"))
            await ShowRemotingSetup(c);
    }

    /// <summary>Setup script for the target: the admin pastes and runs it there; then "Test again".</summary>
    private async Task ShowRemotingSetup(Connection c)
    {
        var host = c.Host.Trim();
        var local = await Task.Run(() => RemotingSetup.LocalAddressFor(host));
        var dlg = new RemotingSetupDialog(c.Name, host, c.PsRemoting?.Message, local) { Owner = this };
        var testAgain = dlg.ShowDialog() == true;
        if (dlg.SwitchToHttps && Inheritance.ResolveOptions(c, _vm.Data).PsUseSsl != true)
        {
            c.Options.PsUseSsl = true;
            _vm.RequestSave();
        }
        if (testAgain) await TestPsRemoting(c, interactive: true);
    }

    /// <summary>Opens a PowerShell remoting session to the connection's host in a new tab.</summary>
    private void OpenPowerShell(Connection c)
    {
        if (BuildTarget(c, null) is not { } t) return;
        var s = _vm.OpenPsSession(c);
        var view = new PsConsoleView(s, () => ConnectPs(s), s.Close);
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
        if (BuildTarget(s.Connection, s.Target) is not { } t) return;
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
        if (known is not null) { target = known; source = "the PowerShell tab"; }
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
        if (BuildTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>Credentials for a new remote tool tab: those of an open remoting tab of the connection, else the connection's own.</summary>
    private (PsTarget Target, string Source)? ToolTarget(Connection c, PsTarget? known) =>
        known is not null ? (known, "the open tab") : BuildTarget(c, null);

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
        if (BuildTarget(s.Connection, s.Target) is not { } t) return;
        s.Connect(t.Target, t.Source, AccentFor(s.Connection));
    }

    /// <summary>Opens the remote registry (tree + values) in a new tab, starting at the last key used for this connection.</summary>
    private void OpenRegistry(Connection c, PsTarget? known)
    {
        if (ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenRegistry(c, c.PsRemoting?.RegistryPath);
        var view = new RegistryView(s, () => ConnectTool(s), s.Close);
        view.Navigated += p => { if (c.PsRemoting is { } st && st.RegistryPath != p) { st.RegistryPath = p; _vm.RequestSave(); } };
        ShowTool(s, view, t);
    }

    /// <summary>Opens the remote services in a new tab (refreshed regularly, see Global defaults → Remote tools).</summary>
    private void OpenServices(Connection c, PsTarget? known)
    {
        if (ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenServices(c);
        ShowTool(s, new ServicesView(s, () => _vm.Data.Tools.ServiceRefreshSeconds, () => ConnectTool(s), s.Close), t);
    }

    /// <summary>Opens the remote processes in a new tab (refreshed regularly, see Global defaults → Remote tools).</summary>
    private void OpenProcesses(Connection c, PsTarget? known)
    {
        if (ToolTarget(c, known) is not { } t) return;
        var s = _vm.OpenProcesses(c);
        ShowTool(s, new ProcessesView(s, () => _vm.Data.Tools.ProcessRefreshSeconds, () => ConnectTool(s), s.Close), t);
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
        await RunTransfers(items, "upload", () => FileUploader.UploadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target),
            remoteFolder, overwrite.Value, items, a => Dispatcher.BeginInvoke(a)));
    }

    /// <summary>Downloads remote files/folders into <paramref name="localFolder"/>.</summary>
    private async Task Download(RemoteDrag drag, string localFolder)
    {
        if (drag.Source.Target is not { } target) return;
        List<(long Size, string FullPath, string Relative)> files;
        try { files = await drag.Source.ExpandAsync(drag.Paths); }
        catch (Exception ex) { await Dialogs.Info(this, "Download", ex.Message); return; }
        if (files.Count == 0) return;

        var existing = drag.Paths.Select(p => Path.GetFileName(p.TrimEnd('\\')))
            .Where(n => File.Exists(Path.Combine(localFolder, n)) || Directory.Exists(Path.Combine(localFolder, n))).ToList();
        var overwrite = await AskOverwrite(existing, localFolder);
        if (overwrite is null) return;

        var name = drag.Source.Connection.Name;
        var items = files.Select(f => TransferItem.Download(f.FullPath, Path.Combine(localFolder, f.Relative), f.Relative, name, f.Size)).ToList();
        await RunTransfers(items, "download", () => FileDownloader.DownloadAsync(() => PsRemoting.Open(target), ex => PsRemoting.Explain(ex, target),
            overwrite.Value, items, a => Dispatcher.BeginInvoke(a)));
    }

    private async Task RunTransfers(List<TransferItem> items, string kind, Func<Task> run)
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
    }

    /// <summary>Files dropped on a PowerShell tab go into its current folder.</summary>
    private void UploadToConsole(PsSession s, IReadOnlyList<string> files)
    {
        if (s.Target is not { } target || s.Location is not { } folder) return;
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
    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as TransferItem)?.Cancel();

    private void CmConnect_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenConnection(c); }
    private void CmConnectNew_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is Connection c) OpenConnection(c, forceNew: true); }
    private void CmEdit_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is { } n) EditNode(n); }
    private void CmDuplicate_Click(object sender, RoutedEventArgs e) { if (_vm.SelectedNode is { } n) _vm.Duplicate(n).IsSelected = true; }
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
            OpenLog: () => OpenLog_Click(this, e));
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
        if (new EditorWindow(n, _vm.Data, isNew: false) { Owner = this }.ShowDialog() == true)
        {
            foreach (var s in _vm.Sessions.Where(s => s.Connection == n)) s.Title = n.Name;
            _vm.RequestSave();
        }
    }

    private async Task DeleteNode(NodeBase n)
    {
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
        var host = new AppSettingsHost(
            ApplyTheme: ApplyTheme,
            ApplySwitchKeys: SetSwitchKeys,
            StartupProtection: () => StartupProtection_Click(this, e),
            ManageCredentials: () => Credentials_Click(this, e),
            ChangeLocation: () => SettingsLocation_Click(this, e),
            OpenFolder: () => OpenSettingsFolder_Click(this, e),
            OpenLog: () => OpenLog_Click(this, e),
            ExportBackup: () => ExportBackup_Click(this, e),
            DefaultApp: () => DefaultApp_Click(this, e),
            CheckForUpdates: channel => ShowUpdateWindow(null, channel));
        if (new EditorWindow(null, _vm.Data, isNew: false, host, page) { Owner = this }.ShowDialog() == true) _vm.RequestSave();
    }

    private UpdateCheckResult? _availableUpdate;

    /// <summary>After the window is shown: the anonymous start ping (if on) and the update check (if on). Neither delays the start.</summary>
    private async void StartupChecks()
    {
        var ui = _vm.Data.Ui;
        Telemetry.AppStart(ui);
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

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var cm = MenuButton.ContextMenu!;
        cm.PlacementTarget = MenuButton;
        cm.Placement = PlacementMode.Bottom;
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
        _dragCandidate = FindAncestor<ToggleButton>(e.OriginalSource) is null
            ? FindAncestor<TreeViewItem>(e.OriginalSource)?.DataContext as NodeBase
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
        DragDrop.DoDragDrop(Tree, new DataObject(NodeFormat, node), DragDropEffects.Move);
        HideDropIndicator();
    }

    private (NodeBase? Target, DropPos Pos, FrameworkElement? Header) HitTest(DragEventArgs e)
    {
        var tvi = FindAncestor<TreeViewItem>(e.OriginalSource);
        if (tvi?.DataContext is not NodeBase target) return (null, DropPos.Into, null);
        var header = tvi.Template.FindName("PART_Header", tvi) as FrameworkElement ?? tvi;
        var y = e.GetPosition(header).Y;
        var h = Math.Max(1, header.ActualHeight);
        DropPos pos = target is Folder
            ? (y < h * 0.25 ? DropPos.Before : y > h * 0.75 && !((Folder)target).IsExpanded ? DropPos.After : DropPos.Into)
            : (y < h / 2 ? DropPos.Before : DropPos.After);
        return (target, pos, header);
    }

    private static string[] RdpFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        var (target, pos, header) = HitTest(e);
        if (RdpFiles(e).Length > 0)
        {
            e.Effects = DragDropEffects.Copy;
            ShowDropIndicator(target is Folder ? header : null, DropPos.Into);
        }
        else if (e.Data.GetData(NodeFormat) is NodeBase node)
        {
            var parent = target is null ? null : pos == DropPos.Into ? target as Folder : target.Parent;
            var ok = target != node && MainViewModel.CanMove(node, parent);
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            if (ok) ShowDropIndicator(header, pos); else HideDropIndicator();
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        HideDropIndicator();
        e.Handled = true;
        var (target, pos, _) = HitTest(e);
        var files = RdpFiles(e);
        if (files.Length > 0)
        {
            ImportRdpFiles(files, target as Folder ?? target?.Parent, connectAfterImport: false);
        }
        else if (e.Data.GetData(NodeFormat) is NodeBase node && target != node)
        {
            Folder? into;
            var idx = -1;
            if (target is null) into = null;
            else if (pos == DropPos.Into && target is Folder f) into = f;
            else
            {
                into = target.Parent;
                idx = _vm.ChildrenOf(into).IndexOf(target) + (pos == DropPos.After ? 1 : 0);
            }
            if (!await ConfirmMove(node, into)) return;
            _vm.Move(node, into, idx);
            node.IsSelected = true;
        }
    }

    /// <summary>
    /// Moving an item into another folder (or out of one) changes the credentials it inherits – easy to do by accident
    /// when dropping just below a folder. Asks first, naming both sources.
    /// </summary>
    private async Task<bool> ConfirmMove(NodeBase node, Folder? into)
    {
        if (_vm.CredentialChange(node, into) is not { } change) return true;
        var where = into is null ? "the top level (outside all folders)" : $"folder \"{into.Name}\"";
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

    public void ImportRdpFiles(IEnumerable<string> files, Folder? target, bool connectAfterImport)
    {
        var errors = new List<string>();
        Connection? last = null;
        foreach (var file in files)
        {
            try
            {
                var c = RdpFileParser.Parse(file);
                // Opening a .rdp file (e.g. via file association) reuses an existing entry for the same server.
                if (connectAfterImport && _vm.AllNodes().OfType<Connection>().FirstOrDefault(x =>
                        string.Equals(x.Host, c.Host, StringComparison.OrdinalIgnoreCase) && x.Port == c.Port) is { } existing)
                {
                    last = existing;
                    continue;
                }
                _vm.Add(c, target);
                last = c;
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        if (last is not null)
        {
            last.IsSelected = true;
            if (connectAfterImport) OpenConnection(last);
        }
        if (errors.Count > 0) _ = Dialogs.Info(this, "Import .rdp files", string.Join("\n", errors));
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
            Backup.Export(_vm.Data, dlg.FileName, pw.Password);
            _ = Dialogs.Info(this, "Export backup", $"Backup saved to\n{dlg.FileName}\n\nKeep the password safe – it cannot be recovered.");
        }
        catch (Exception ex) { _ = Dialogs.Info(this, "Export failed", ex.Message); }
    }

    private async void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "RemoteHub backup (*.rhbackup;*.rdpmbackup)|*.rhbackup;*.rdpmbackup|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var pw = new PasswordDialog(confirm: false) { Owner = this };
        if (pw.ShowDialog() != true) return;

        AppData imported;
        try { imported = Backup.Import(dlg.FileName, pw.Password); }
        catch (Exception ex) { await Dialogs.Info(this, "Import failed", ex.Message); return; }

        var choice = await Dialogs.Ask(this, "Import backup",
            "Add the backup as a new folder next to your current connections, or replace everything?",
            "Add as folder", "Replace everything");
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
        if (active > 0 || transfers > 0)
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
                    }.Where(x => x is not null));
                    if (!await Dialogs.Confirm(this, "Exit", $"{what}. Disconnect and exit?", "Exit")) return;
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

    private void Shutdown()
    {
        var ui = _vm.Data.Ui;
        ui.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { ui.Width = Width; ui.Height = Height; }
        if (!ui.SidebarCollapsed) ui.SidebarWidth = SidebarColumn.ActualWidth;
        _vm.SaveNow();

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
        // Give remote PowerShell sessions a moment to close cleanly (otherwise they linger on the server until they time out).
        try { Task.WaitAll(PsSession.Closing.Keys.ToArray(), TimeSpan.FromSeconds(3)); } catch { /* best effort */ }
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
