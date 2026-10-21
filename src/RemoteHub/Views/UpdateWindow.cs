using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>What the update window needs from the main window to install: ask about open sessions, then exit.</summary>
public sealed record UpdateHost(Func<Task<bool>> ConfirmExit, Action Exit, Action SaveSettings);

/// <summary>Shows the result of an update check (or runs one) and downloads, verifies and installs a newer version.</summary>
public sealed class UpdateWindow : FluentWindow
{
    private readonly UiState _ui;
    private readonly UpdateHost _host;
    private readonly string _channel;
    private UpdateCheckResult? _result;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 4, 24, 22) };
    private CancellationTokenSource? _download;

    /// <param name="result">A result from the start-up check, or null to check now.</param>
    /// <param name="channel">Channel to check (e.g. the one just picked in Settings); default: the channel in use.</param>
    public UpdateWindow(UiState ui, UpdateHost host, UpdateCheckResult? result = null, string? channel = null)
    {
        _ui = ui;
        _host = host;
        _result = result;
        _channel = channel ?? UpdateService.EffectiveChannel(ui);
        Title = "RemoteHub updates";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "RemoteHub updates", ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(_body);
        Content = root;
        Closing += (_, _) => _download?.Cancel();

        if (_result is null) { ShowChecking(); Loaded += async (_, _) => { _result = await Task.Run(() => UpdateService.CheckAsync(_channel)); Build(); }; }
        else Build();
    }

    // ------------------------------------------------------------------ helpers

    private static TextBlock Text(string t, bool muted = false, double bottom = 8, double size = 14, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom), FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (muted) tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private void Head(string title, string? sub)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var logo = new System.Windows.Controls.Image { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        AppIcon.Apply(logo, 40);
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text(title, bottom: 2, size: 18, weight: FontWeights.SemiBold));
        if (sub is not null) names.Children.Add(Text(sub, muted: true, bottom: 0, size: 13));
        head.Children.Add(names);
        _body.Children.Add(head);
    }

    private StackPanel Buttons()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _body.Children.Add(p);
        return p;
    }

    private Button CloseButton(string label = "Close")
    {
        var b = new Button { Content = label, IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => Close();
        return b;
    }

    private string Installed => $"Installed: {UpdateService.Current} · Channel: {UpdateService.ChannelName(_channel)}";

    // ------------------------------------------------------------------ states

    private void ShowChecking()
    {
        _body.Children.Clear();
        Head("Checking for updates…", Installed);
        _body.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 4) });
        Buttons().Children.Add(CloseButton("Cancel"));
    }

    private void Build()
    {
        _body.Children.Clear();
        var r = _result!;
        switch (r.Status)
        {
            case UpdateStatus.UpToDate:
                Head("RemoteHub is up to date", Installed);
                if (r.Info?.ParsedVersion is { } latest && latest < UpdateService.Current)
                    _body.Children.Add(Text($"The newest {UpdateService.ChannelName(_channel)} release is {latest}, which is older than this version. " +
                                            "RemoteHub only updates to a higher version.", muted: true));
                Buttons().Children.Add(CloseButton());
                break;
            case UpdateStatus.NoRelease:
                Head("No release in this channel yet", Installed);
                _body.Children.Add(Text(_channel == UpdateService.Stable
                    ? "There is no GA (stable) release yet. Switch to the Beta channel in Settings → Updates & privacy to get pre-releases."
                    : "The update server has no release for this channel.", muted: true));
                Buttons().Children.Add(CloseButton());
                break;
            case UpdateStatus.Failed:
                Head("Couldn't check for updates", Installed);
                _body.Children.Add(Text(r.Error ?? "Data unavailable", muted: true));
                Buttons().Children.Add(CloseButton());
                break;
            default:
                BuildAvailable(r);
                break;
        }
    }

    private void BuildAvailable(UpdateCheckResult r)
    {
        var info = r.Info!;
        var published = info.PublishedAt is { } p ? $" · published {p.ToLocalTime():d}" : "";
        Head($"RemoteHub {info.Version} is available", Installed + published);
        if (r.Required)
        {
            var note = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
            note.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            note.SetResourceReference(Border.BorderBrushProperty, "SystemFillColorCautionBrush");
            note.Child = new TextBlock { Text = "This update is required – this version is no longer supported.", TextWrapping = TextWrapping.Wrap };
            _body.Children.Add(note);
        }

        _body.Children.Add(Text("What's new", bottom: 4, weight: FontWeights.SemiBold));
        _body.Children.Add(new TextBox
        {
            Text = UpdateService.PlainNotes(info.Notes), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, MinHeight = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8),
        });
        if (info.Download is { Size: > 0 } d)
            _body.Children.Add(Text($"{d.Name} · {d.Size / 1048576.0:0.#} MB · verified with SHA-256 before installing", muted: true, size: 12));
        _body.Children.Add(Text("Installing closes RemoteHub (open sessions are disconnected). Windows asks for administrator approval, " +
                                "then RemoteHub starts again.", muted: true, size: 12));

        var progress = new ProgressBar { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 4), Maximum = 1 };
        var status = Text("", muted: true, bottom: 0, size: 12);
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _body.Children.Add(progress);
        _body.Children.Add(status);
        _body.Children.Add(error);

        var buttons = Buttons();
        var page = new Button { Content = "Open releases page", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
        page.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); } catch { /* no browser */ } };
        var install = new Button { Content = "Download and install", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon(SymbolRegular.ArrowDownload24), MinWidth = 160 };
        var skip = new Button { Content = "Skip this version", Margin = new Thickness(8, 0, 0, 0) };
        skip.Click += (_, _) =>
        {
            _ui.SkippedUpdateVersion = info.Version;
            _host.SaveSettings();
            DiagLog.Write($"Update: version {info.Version} skipped");
            Close();
        };
        var later = CloseButton("Later");
        buttons.Children.Add(page);
        buttons.Children.Add(install);
        if (!r.Required) buttons.Children.Add(skip);
        buttons.Children.Add(later);

        install.Click += async (_, _) =>
        {
            install.IsEnabled = skip.IsEnabled = false;
            error.Text = "";
            progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            status.Text = "Downloading…";
            _download = new CancellationTokenSource();
            try
            {
                var msi = await UpdateService.DownloadAsync(info, new Progress<double>(v => { progress.Value = v; status.Text = $"Downloading… {v:P0}"; }), _download.Token);
                status.Text = Authenticode.RunningPublisher is null ? "Download verified (SHA-256)." : "Download verified (SHA-256 and signature).";
                DiagLog.Write($"Update: {info.Version} downloaded and verified");
                if (!await _host.ConfirmExit()) { status.Text = "Install cancelled. The verified download is kept until the next update."; install.IsEnabled = true; return; }
                status.Text = "Waiting for administrator approval…";
                var sha = info.Download!.Sha256!;
                // Windows' approval prompt blocks the call until it is answered: off the UI thread.
                await Task.Run(() => UpdateService.StartInstall(msi, sha));
                DiagLog.Write($"Update: installing {info.Version}");
                Close();
                _host.Exit();
            }
            catch (OperationCanceledException) { /* window closed */ }
            catch (Exception ex)
            {
                DiagLog.Write($"Update: download or install failed – {ex.GetType().Name}");
                progress.Visibility = Visibility.Collapsed;
                status.Text = "";
                error.Text = ex.Message + "\n\nYou can download the installer from the releases page instead.";
                page.Visibility = Visibility.Visible;
                install.IsEnabled = skip.IsEnabled = true;
            }
        };
    }
}
