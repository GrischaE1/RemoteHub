using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>
/// Settings → Apps (docs/app-tabs-design.md §8.1): the apps a connection opens in tabs of their own through the device's
/// RemoteHub Agent – inherited from the folder like the other agent options, or a list of its own; added from the
/// device's Start menu or by hand, ordered, and edited below the list. App tabs need automatic sign-in: the page says so
/// and turns it on.
/// </summary>
public partial class EditorWindow
{
    /// <summary>
    /// Reads the device's Start menu and lets the user tick apps (owner, this item – a folder or the defaults ask which
    /// connection – and its apps now); null when cancelled. Set by the main window.
    /// </summary>
    public Func<Window, NodeBase?, IReadOnlyList<ConnectionApp>, IReadOnlyList<ConnectionApp>?>? PickAppsFromDevice { get; init; }

    /// <summary>Built into RemoteHub, editable once added (§8.3).</summary>
    internal static readonly ConnectionApp[] AppPresets =
    [
        new("Event Viewer", @"%SystemRoot%\system32\mmc.exe", "eventvwr.msc"),
        new("Services", @"%SystemRoot%\system32\mmc.exe", "services.msc"),
        new("Computer Management", @"%SystemRoot%\system32\mmc.exe", "compmgmt.msc"),
        new("Task Scheduler", @"%SystemRoot%\system32\mmc.exe", "taskschd.msc"),
        new("Hyper-V Manager", @"%SystemRoot%\system32\mmc.exe", "virtmgmt.msc"),
        new("Server Manager", @"%SystemRoot%\system32\ServerManager.exe"),
        new("Registry Editor", @"%SystemRoot%\regedit.exe", Elevated: true),
        new("Windows PowerShell", @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"),
        new("PowerShell 7", @"%ProgramFiles%\PowerShell\7\pwsh.exe"),
        new("Command Prompt", @"%SystemRoot%\system32\cmd.exe"),
    ];

    private readonly TextBlock _appsNote = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _appsTurnOn = new() { Content = "Turn on automatic sign-in", Icon = new SymbolIcon(SymbolRegular.Key24), Margin = new Thickness(0, 0, 0, 14) };
    private readonly ListBox _appsList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _appName = new(), _appProgram = new(), _appArguments = new(), _appFolder = new();
    private readonly CheckBox _appElevated = new() { Content = "Run as administrator" };
    private readonly StackPanel _appFields = new() { Margin = new Thickness(0, 14, 0, 0) };
    private Button _appUp = null!, _appDown = null!, _appRemove = null!;
    private Button _appAddFromDevice = null!, _appAddAnother = null!;
    private bool _appFilling;

    private void InitAppsPage()
    {
        _appsNote.Style = TryFindResource("Caption") as Style;
        PageApps.Children.Add(_appsNote);
        _appsTurnOn.Click += (_, _) => { _vm.TurnOnAutoSignIn(); RefreshAppsPage(); };
        PageApps.Children.Add(_appsTurnOn);

        var inherit = new ContentControl { Content = _vm.AppsRow, ContentTemplate = (DataTemplate)FindResource("OptionRowTemplate") };
        PageApps.Children.Add(inherit);
        _vm.AppsRow.PropertyChanged += (_, _) => { FillAppsList(); RefreshAppsPage(); };
        if (_vm.AutoSignInRow is { } signIn) signIn.PropertyChanged += (_, _) => RefreshAppsPage();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        _appAddFromDevice = new Button { Content = "Add from the device…", Icon = new SymbolIcon(SymbolRegular.AppsAddIn24), Margin = new Thickness(0, 0, 8, 0) };
        _appAddFromDevice.Click += (_, _) => AddAppsFromDevice();
        _appAddAnother = new Button { Icon = new SymbolIcon(SymbolRegular.Add24), Margin = new Thickness(0, 0, 8, 0) };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = "Add another app" });
        label.Children.Add(new SymbolIcon(SymbolRegular.ChevronDown24) { FontSize = 12, Margin = new Thickness(8, 1, 0, 0) });
        _appAddAnother.Content = label;
        AutomationProperties.SetName(_appAddAnother, "Add another app");
        _appAddAnother.Click += (_, _) => ShowPresetMenu();
        buttons.Children.Add(_appAddFromDevice);
        buttons.Children.Add(_appAddAnother);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _appUp = RemoteToolChrome.Tool(SymbolRegular.ArrowUp24, "Move up", () => MoveApp(-1));
        _appDown = RemoteToolChrome.Tool(SymbolRegular.ArrowDown24, "Move down", () => MoveApp(1));
        _appRemove = RemoteToolChrome.Tool(SymbolRegular.Delete24, "Remove", RemoveApp);
        _appDown.Margin = _appUp.Margin = new Thickness(0, 0, 4, 0);
        tools.Children.Add(_appUp);
        tools.Children.Add(_appDown);
        tools.Children.Add(_appRemove);
        var row = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        row.Children.Add(tools);
        row.Children.Add(buttons);
        PageApps.Children.Add(row);

        AutomationProperties.SetName(_appsList, "Apps");
        ScrollViewer.SetVerticalScrollBarVisibility(_appsList, ScrollBarVisibility.Disabled); // the page scrolls, as the editor's other lists do
        _appsList.SelectionChanged += (_, _) => ShowSelectedApp();
        PageApps.Children.Add(_appsList);

        void Field(string text, TextBox box)
        {
            _appFields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) });
            box.Margin = new Thickness(0, 0, 0, 10);
            box.TextChanged += (_, _) => EditSelectedApp();
            AutomationProperties.SetName(box, text);
            _appFields.Children.Add(box);
        }
        Field("Name", _appName);
        Field("Program", _appProgram);
        _appProgram.PlaceholderText = @"%SystemRoot%\system32\mmc.exe";
        Field("Arguments", _appArguments);
        Field("Start in", _appFolder);
        _appFolder.PlaceholderText = "The program's folder";
        _appElevated.Checked += (_, _) => EditSelectedApp();
        _appElevated.Unchecked += (_, _) => EditSelectedApp();
        _appFields.Children.Add(_appElevated);
        PageApps.Children.Add(_appFields);

        // The agent's remote desktop blocked on this PC: the page stays, its controls are greyed with the reason (P2).
        if (!Features.IsAllowed(Feature.AgentDesktop)) PolicyLock.TurnedOff(_appAddFromDevice, _appAddAnother, _appsList, _appFields, _appUp, _appDown, _appRemove);
        FillAppsList();
        RefreshAppsPage();
    }

    /// <summary>The note at the top and what can be changed: the inherited list is shown, not edited.</summary>
    private void RefreshAppsPage()
    {
        var noAgent = _vm.Node is Connection c && c.Agent is not { Status: AgentStatus.Installed };
        _appsNote.Text = !Features.IsAllowed(Feature.AgentDesktop)
                ? "The remote desktop through the RemoteHub Agent is turned off by your organisation (Settings → Policies), and with it app tabs."
            : noAgent
                ? "App tabs go through the device's RemoteHub Agent – install it first (right-click the connection → Install RemoteHub Agent…). Apps can be added by hand meanwhile."
            : !_vm.AutoSignInOn
                ? AppPolicy.AgentAutoSignInDisabled
                    ? "App tabs need automatic sign-in, which your organisation turned off on this PC (Settings → Policies)."
                    : "App tabs need automatic sign-in: each app runs in your own session on the device, and RemoteHub signs you in when nobody is."
                : "Each app opens in a tab of its own through the device's RemoteHub Agent: right-click the connection → Open app. The device's policy may refuse an app.";
        // What a policy blocks isn't offered (PolicyLock): the button only where it can turn it on.
        _appsTurnOn.Visibility = !_vm.AutoSignInOn && Features.IsAllowed(Feature.AgentDesktop) && !AppPolicy.AgentAutoSignInDisabled && _vm.AutoSignInRow?.IsEditable != false
            ? Visibility.Visible : Visibility.Collapsed;
        var own = _vm.AppsOwn && Features.IsAllowed(Feature.AgentDesktop);
        _appAddFromDevice.IsEnabled = own && !noAgent;
        _appAddAnother.IsEnabled = own;
        _appsList.IsEnabled = Features.IsAllowed(Feature.AgentDesktop); // the inherited list is shown; its fields below stay greyed
        var i = _appsList.SelectedIndex;
        _appUp.IsEnabled = own && i > 0;
        _appDown.IsEnabled = own && i >= 0 && i < _vm.Apps.Count - 1;
        _appRemove.IsEnabled = own && i >= 0;
        _appFields.IsEnabled = own && i >= 0;
        _appFields.Visibility = _vm.Apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillAppsList(int select = -1)
    {
        _appFilling = true;
        _appsList.Items.Clear();
        foreach (var app in _vm.Apps)
        {
            var item = new ListBoxItem { Content = AppRows.Row(this, app), Tag = app };
            AutomationProperties.SetName(item, app.Name);
            _appsList.Items.Add(item);
        }
        if (_vm.Apps.Count == 0)
            _appsList.Items.Add(new ListBoxItem { Content = ScriptWindowFrame.Info(_vm.AppsOwn ? "No apps yet – add them from the device or by hand." : "Nothing to inherit – no apps above this one."), IsEnabled = false });
        _appFilling = false;
        _appsList.SelectedIndex = _vm.Apps.Count == 0 ? -1 : Math.Clamp(select < 0 ? 0 : select, 0, _vm.Apps.Count - 1);
        ShowSelectedApp();
    }

    private void ShowSelectedApp()
    {
        if (_appFilling) return;
        _appFilling = true;
        var app = _appsList.SelectedIndex is var i and >= 0 && i < _vm.Apps.Count ? _vm.Apps[i] : null;
        _appName.Text = app?.Name ?? "";
        _appProgram.Text = app?.Program ?? "";
        _appArguments.Text = app?.Arguments ?? "";
        _appFolder.Text = app?.WorkingFolder ?? "";
        _appElevated.IsChecked = app?.Elevated == true;
        _appFilling = false;
        RefreshAppsPage();
    }

    /// <summary>A field changed: the selected app takes it (records are replaced, never changed in place) and its row follows.</summary>
    private void EditSelectedApp()
    {
        if (_appFilling || !_vm.AppsOwn || _appsList.SelectedIndex is not (var i and >= 0) || i >= _vm.Apps.Count) return;
        var app = _vm.Apps[i] with
        {
            Name = _appName.Text, Program = _appProgram.Text, Arguments = _appArguments.Text, WorkingFolder = _appFolder.Text, Elevated = _appElevated.IsChecked == true,
        };
        _vm.Apps[i] = app;
        if (_appsList.Items[i] is ListBoxItem item) { item.Content = AppRows.Row(this, app); item.Tag = app; AutomationProperties.SetName(item, app.Name); }
    }

    private void AddApps(IEnumerable<ConnectionApp> apps)
    {
        var added = -1;
        foreach (var app in apps.Where(a => !_vm.Apps.Any(x => x.SameApp(a))))
        {
            _vm.Apps.Add(app);
            added = _vm.Apps.Count - 1;
        }
        if (added >= 0) FillAppsList(added);
    }

    private void AddAppsFromDevice()
    {
        if (PickAppsFromDevice?.Invoke(this, _vm.IsDefaults ? null : _vm.Node, [.. _vm.Apps]) is { } picked) AddApps(picked);
    }

    /// <summary>"Add another app": an empty one to fill in, or a preset.</summary>
    private void ShowPresetMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _appAddAnother, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(RemoteToolChrome.Item("Empty app", SymbolRegular.Add24, () =>
        {
            _vm.Apps.Add(new ConnectionApp("New app", ""));
            FillAppsList(_vm.Apps.Count - 1);
            _appName.Focus();
            _appName.SelectAll();
        }));
        menu.Items.Add(new Separator());
        foreach (var preset in AppPresets)
        {
            var item = RemoteToolChrome.Item(preset.Name, SymbolRegular.Apps24, () => AddApps([preset]));
            item.IsEnabled = !_vm.Apps.Any(a => a.SameApp(preset));
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void MoveApp(int by)
    {
        var i = _appsList.SelectedIndex;
        if (!_vm.AppsOwn || i < 0 || i + by < 0 || i + by >= _vm.Apps.Count) return;
        _vm.Apps.Move(i, i + by);
        FillAppsList(i + by);
    }

    private void RemoveApp()
    {
        var i = _appsList.SelectedIndex;
        if (!_vm.AppsOwn || i < 0 || i >= _vm.Apps.Count) return;
        _vm.Apps.RemoveAt(i);
        FillAppsList(Math.Min(i, _vm.Apps.Count - 1));
    }
}
