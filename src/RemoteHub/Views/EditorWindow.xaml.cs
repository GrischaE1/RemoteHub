using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;
using Wpf.Ui.Controls;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>What the settings window (global settings) can do in the main window.</summary>
public sealed record AppSettingsHost(
    Action<string> ApplyTheme,
    Action<string> ApplySwitchKeys,
    Action StartupProtection,
    Action ManageCredentials,
    Action ChangeLocation,
    Action OpenFolder,
    Action OpenLog,
    Action ExportBackup,
    Action ImportBackup,
    Action AuditLog,
    Action DefaultApp,
    Action<string> CheckForUpdates,
    Action SettingsOverview,
    Action About);

/// <summary>
/// Edits a connection or folder – or, without a node, the global settings: RemoteHub's own settings on this PC
/// (general, updates &amp; privacy, security &amp; storage) and the connection defaults. One page per group.
/// </summary>
public partial class EditorWindow : FluentWindow
{
    public const string PageAppSettings = "PageApp", PageUpdateSettings = "PageUpdates", PageDataSettings = "PageData", PageHelpSettings = "PageHelp",
        PageRecordingSettings = "PageRecordings",
        PageDefaults = "PageDisplay";

    private readonly EditorViewModel _vm;
    private readonly AppData _data;
    private readonly AppSettingsHost? _host;
    private readonly Dictionary<string, (string Title, string Hint)> _pages = new();

    public EditorWindow(NodeBase? node, AppData data, bool isNew, AppSettingsHost? host = null, string? page = null)
    {
        InitializeComponent();
        _data = data;
        _host = host;
        DataContext = _vm = new EditorViewModel(node, data, isNew);
        PasswordInput.Password = _vm.Password ?? "";
        BuildNav();
        if (_vm.IsDefaults) InitAppPages();
        Show(page ?? (_vm.IsDefaults ? PageAppSettings : "PageGeneral"));
        Loaded += (_, _) =>
        {
            if (_vm.IsDefaults) return;
            if (_vm.IsConnection && isNew) HostBox.Focus();
            else NameBox.Focus();
        };
    }

    // ------------------------------------------------------------------ navigation

    private void BuildNav()
    {
        var item = _vm.Subtitle;
        if (_vm.IsDefaults)
        {
            Group("RemoteHub", AppHeaderIcon());
            Page(PageAppSettings, "General", SymbolRegular.Settings24, "Appearance and keys of RemoteHub on this PC.");
            Page(PageUpdateSettings, "Updates & privacy", SymbolRegular.ArrowSync24, "Stored on this PC.");
            Page(PageDataSettings, "Security & storage", SymbolRegular.Shield24, "Startup protection, saved credentials, where the settings are kept, backup.");
            Page(PageRecordingSettings, "Recordings", SymbolRegular.Record24, "Frame rate, video size, graphics card and folder of tab recordings.");
            Page(PageHelpSettings, "Help & about", SymbolRegular.QuestionCircle24, "Version, settings overview, diagnostic log and keyboard shortcuts.");
            Group("Connection defaults", new SymbolIcon(SymbolRegular.PlugConnected24) { FontSize = 18 });
            Page("PageCredentials", "Credentials", SymbolRegular.Key24, item);
            Page("PageDisplay", "Display", SymbolRegular.Desktop24, item);
            Page("PageResources", "Local resources & keyboard", SymbolRegular.Keyboard24, item);
            Page("PageSecurity", "Security & session", SymbolRegular.LockClosed24, item);
            Page("PagePs", "PowerShell remoting", SymbolRegular.WindowConsole20, item);
            Page("PageAppearance", "Colour", SymbolRegular.Color24, item);
            Page("PageTools", "Remote tools", SymbolRegular.WrenchScrewdriver24, "How often the Processes and Services tabs refresh.");
        }
        else
        {
            Group(null, null);
            Page("PageGeneral", "General", SymbolRegular.Info24, item);
            Page("PageAppearance", "Appearance", SymbolRegular.Color24, item);
            Page("PageCredentials", "Credentials", SymbolRegular.Key24, item);
            Page("PageDisplay", "Display", SymbolRegular.Desktop24, item);
            Page("PageResources", "Local resources & keyboard", SymbolRegular.Keyboard24, item);
            Page("PageSecurity", "Security & session", SymbolRegular.LockClosed24, item);
            Page("PagePs", "PowerShell remoting", SymbolRegular.WindowConsole20, item);
        }
    }

    private readonly List<ListBox> _groups = new();
    private bool _selecting;

    private static FrameworkElement AppHeaderIcon()
    {
        var img = new System.Windows.Controls.Image { Width = 18, Height = 18 };
        AppIcon.Apply(img, 18);
        return img;
    }

    /// <summary>
    /// Starts a group of pages: a frame in the accent colour (with a light tint of it) around a header with an icon and
    /// the group's pages – like the folder frames in the main navigation. Without a title: just a list, no frame.
    /// </summary>
    private void Group(string? title, FrameworkElement? icon)
    {
        var list = new ListBox { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += Group_SelectionChanged;
        _groups.Add(list);
        if (title is null) { Nav.Children.Add(list); return; }

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 6) };
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(icon);
        }
        head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(list);

        var accent = (TryFindResource("BrandAccentBrush") as System.Windows.Media.SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF);
        var frame = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(6, 8, 6, 6),
            Margin = new Thickness(0, Nav.Children.Count == 0 ? 0 : 12, 0, 0),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, accent.R, accent.G, accent.B)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, accent.R, accent.G, accent.B)),
        };
        Nav.Children.Add(frame);
    }

    private void Page(string name, string title, SymbolRegular icon, string hint)
    {
        _pages[name] = (title, hint);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new SymbolIcon(icon) { FontSize = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        _groups[^1].Items.Add(new ListBoxItem { Content = row, Tag = name, ToolTip = title });
    }

    private void Show(string name)
    {
        if (!_pages.ContainsKey(name)) name = _pages.Keys.First();
        foreach (var g in _groups)
            if (g.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == name) is { } item) { g.SelectedItem = item; return; }
    }

    /// <summary>One selection across all groups: selecting a page clears the selection in the other groups.</summary>
    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || sender is not ListBox list || list.SelectedItem is not ListBoxItem { Tag: string name }) return;
        _selecting = true;
        foreach (var g in _groups) if (g != list) g.SelectedItem = null;
        _selecting = false;
        foreach (var key in _pages.Keys) ((FrameworkElement)FindName(key)).Visibility = key == name ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = _pages[name].Title;
        PageHint.Text = _pages[name].Hint;
        PageScroll.ScrollToTop();
    }

    // ------------------------------------------------------------------ RemoteHub's own settings (global settings only)

    private static readonly (string Id, string Label)[] Themes = [("System", "Same as Windows"), ("Dark", "Dark"), ("Light", "Light")];

    private void InitAppPages()
    {
        var ui = _data.Ui;
        ThemeBox.ItemsSource = Themes.Select(t => t.Label).ToList();
        ThemeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Themes, t => t.Id == ui.Theme));
        SwitchKeysBox.ItemsSource = SwitchHotkeys.All.Select(p => p.Label).ToList();
        SwitchKeysBox.SelectedIndex = Math.Max(0, SwitchHotkeys.All.ToList().FindIndex(p => p.Id == SwitchHotkeys.Get(ui.SwitchKeys).Id));
        DefaultAppButton.Click += (_, _) => _host?.DefaultApp();

        // Updates & privacy
        VersionText.Text = $"Installed version: {UpdateService.Current}";
        UpdateCheckToggle.IsChecked = UpdateService.CheckEnabled(ui);
        var channel = UpdateService.EffectiveChannel(ui);
        ChannelStable.IsChecked = channel == UpdateService.Stable;
        ChannelBeta.IsChecked = channel == UpdateService.Beta;
        if (AppPolicy.UpdateCheckDisabled || AppPolicy.UpdateChannel is not null)
        {
            UpdateCheckToggle.IsEnabled = !AppPolicy.UpdateCheckDisabled;
            ChannelStable.IsEnabled = ChannelBeta.IsEnabled = AppPolicy.UpdateChannel is null;
            UpdatePolicyText.Text = "Some of these settings are managed by your organisation.";
            UpdatePolicyText.Visibility = Visibility.Visible;
        }
        CheckNowButton.IsEnabled = !AppPolicy.UpdateCheckDisabled;
        CheckNowButton.Click += (_, _) => _host?.CheckForUpdates(SelectedChannel);
        TelemetryToggle.IsChecked = Telemetry.Enabled(ui);
        if (AppPolicy.TelemetryDisabled)
        {
            TelemetryToggle.IsEnabled = false;
            TelemetryPolicyText.Text = "Turned off by your organisation.";
            TelemetryPolicyText.Visibility = Visibility.Visible;
        }

        // Security & storage
        RefreshDataPage();
        ProtectionButton.Click += (_, _) => { _host?.StartupProtection(); RefreshDataPage(); };
        CredentialsButton.Click += (_, _) => { _host?.ManageCredentials(); RefreshDataPage(); };
        LocationButton.Click += (_, _) =>
        {
            // Changing the location can replace the settings in memory, so this window (and its edits) closes first.
            DialogResult = false;
            Dispatcher.BeginInvoke(() => _host?.ChangeLocation());
        };
        FolderButton.Click += (_, _) => _host?.OpenFolder();
        ExportButton.Click += (_, _) => _host?.ExportBackup();
        AuditButton.Click += (_, _) => _host?.AuditLog();
        ImportButton.Click += (_, _) =>
        {
            // Importing replaces the settings in memory, so this window (and its edits) closes first.
            DialogResult = false;
            Dispatcher.BeginInvoke(() => _host?.ImportBackup());
        };
        _recordingFolder = ui.RecordingFolder;
        var recordingOptions = Recordings.Options(ui);
        RecordingFpsBox.ItemsSource = Recordings.FrameRates.Select(f => f.Label).ToList();
        RecordingFpsBox.SelectedIndex = Array.FindIndex(Recordings.FrameRates, f => f.Fps == recordingOptions.FramesPerSecond);
        RecordingResolutionBox.ItemsSource = Recordings.Resolutions.Select(r => r.Label).ToList();
        RecordingResolutionBox.SelectedIndex = Array.FindIndex(Recordings.Resolutions, r => r.Value == recordingOptions.Resolution);
        RecordingGpuToggle.IsChecked = recordingOptions.UseGpu;
        RecordingDetailsToggle.IsChecked = _data.Ui.RecordingShowDetails;
        RefreshRecordingSection();
        RecordingFolderButton.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for session recordings", InitialDirectory = CurrentRecordingFolder };
            if (dlg.ShowDialog(this) != true) return;
            _recordingFolder = dlg.FolderName;
            RefreshRecordingSection();
        };
        RecordingDefaultButton.Click += (_, _) => { _recordingFolder = null; RefreshRecordingSection(); };
        RecordingOpenButton.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(CurrentRecordingFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{CurrentRecordingFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { RecordingInfoText.Text = $"The folder can't be opened: {ex.Message}"; }
        };
        HelpVersionText.Text = $"RemoteHub {Services.AppVersion.Current}";
        AboutButton.Click += (_, _) => _host?.About();
        OverviewButton.Click += (_, _) => _host?.SettingsOverview();
        HelpLogButton.Click += (_, _) => _host?.OpenLog();
    }

    private string? _recordingFolder;

    private string CurrentRecordingFolder => Recordings.Folder(new UiState { RecordingFolder = _recordingFolder });

    private void RefreshRecordingSection()
    {
        var managed = AppPolicy.RecordingFolder is not null;
        RecordingIntroText.Text = AppPolicy.RecordingDisabled
            ? "Recording is turned off by your organisation."
            : "Record any tab – remote desktop, PowerShell, file transfer, registry, services, processes – with the Record button in the " +
              "toolbar or by right-clicking the tab. Videos are MP4 files without sound. They aren't encrypted and can contain personal " +
              "data or passwords shown on screen.";
        RecordingInfoText.Text = CurrentRecordingFolder + (managed ? " (set by your organisation)" : "");
        RecordingFolderButton.IsEnabled = RecordingDefaultButton.IsEnabled = !managed && !AppPolicy.RecordingDisabled;
        RecordingDefaultButton.Visibility = string.IsNullOrWhiteSpace(_recordingFolder) ? Visibility.Collapsed : Visibility.Visible;
        RecordingOpenButton.IsEnabled = !AppPolicy.RecordingDisabled;
        RecordingFpsBox.IsEnabled = RecordingResolutionBox.IsEnabled = RecordingGpuToggle.IsEnabled = RecordingDetailsToggle.IsEnabled = !AppPolicy.RecordingDisabled;
    }

    private string SelectedChannel => ChannelBeta.IsChecked == true ? UpdateService.Beta : UpdateService.Stable;

    private void RefreshDataPage()
    {
        ProtectionText.Text = $"Ask for a password or Windows Hello when RemoteHub starts. Currently: {AppLock.Describe()}.";
        CredentialsText.Text = _data.SavedCredentials.Count switch
        {
            0 => "Named credentials that folders and connections can use. None saved yet.",
            1 => "Named credentials that folders and connections can use. 1 saved.",
            var n => $"Named credentials that folders and connections can use. {n} saved.",
        };
        LocationText.Text = SettingsStore.Describe() + (SettingsStore.IsPolicyManaged ? " (set by your organisation)" : "");
        LocationButton.IsEnabled = !SettingsStore.IsPolicyManaged;
    }

    /// <summary>Writes the RemoteHub settings of this window into <see cref="UiState"/> and applies them.</summary>
    private void ApplyAppSettings()
    {
        var ui = _data.Ui;
        var theme = Themes[Math.Max(0, ThemeBox.SelectedIndex)].Id;
        if (theme != ui.Theme) _host?.ApplyTheme(theme);
        var keys = SwitchHotkeys.All[Math.Max(0, SwitchKeysBox.SelectedIndex)].Id;
        if (keys != ui.SwitchKeys) _host?.ApplySwitchKeys(keys);

        if (!AppPolicy.UpdateCheckDisabled) ui.UpdateCheckEnabled = UpdateCheckToggle.IsChecked == true;
        // Only store the channel when it differs from what was in effect, so the default can still follow the version.
        if (AppPolicy.UpdateChannel is null && SelectedChannel != UpdateService.EffectiveChannel(ui)) ui.UpdateChannel = SelectedChannel;
        if (AppPolicy.RecordingFolder is null) ui.RecordingFolder = string.IsNullOrWhiteSpace(_recordingFolder) ? null : _recordingFolder;
        if (RecordingFpsBox.SelectedIndex >= 0) ui.RecordingFps = Recordings.FrameRates[RecordingFpsBox.SelectedIndex].Fps;
        ui.RecordingUseGpu = RecordingGpuToggle.IsChecked == true;
        ui.RecordingShowDetails = RecordingDetailsToggle.IsChecked == true;
        if (RecordingResolutionBox.SelectedIndex >= 0) ui.RecordingResolution = Recordings.Resolutions[RecordingResolutionBox.SelectedIndex].Value.ToString();
        if (!AppPolicy.TelemetryDisabled)
        {
            var on = TelemetryToggle.IsChecked == true;
            if (ui.TelemetryEnabled && !on) { Telemetry.ForgetInstallId(); DiagLog.Write("Telemetry: turned off"); }
            ui.TelemetryEnabled = on;
        }
    }

    // ------------------------------------------------------------------ item editing

    private void PickIconImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.IconImage = IconImages.LoadAsBase64Png(dlg.FileName); }
        catch (Exception ex) { _vm.Error = "The image could not be loaded: " + ex.Message; }
    }

    private void RemoveIconImage_Click(object sender, RoutedEventArgs e) => _vm.IconImage = null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordInput.Password;
        if (!_vm.Apply())
        {
            // Show the page with the problem.
            var err = _vm.Error ?? "";
            if (err.Contains("username", StringComparison.OrdinalIgnoreCase) || err.Contains("credential", StringComparison.OrdinalIgnoreCase)) Show("PageCredentials");
            else if (err.Contains("colour", StringComparison.OrdinalIgnoreCase) || err.Contains("color", StringComparison.OrdinalIgnoreCase)) Show("PageAppearance");
            else if (!_vm.IsDefaults) Show("PageGeneral");
            return;
        }
        if (_vm.IsDefaults) ApplyAppSettings();
        DialogResult = true;
    }
}
