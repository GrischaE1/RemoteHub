using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>About RemoteHub: version, build, components and where things are – with a button to copy it for support.</summary>
public sealed class AboutWindow : FluentWindow
{
    public static string Version
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "Data unavailable";
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info; // without the source revision hash
        }
    }

    public static string ProductLine => $"RemoteHub {Version}";

    public const string Developer = "Grischa Ernst";

    /// <summary>From the assembly (set in the project file), so the program file's properties show the same text.</summary>
    public static string Copyright =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? $"© {DateTime.Now.Year} {Developer}";

    /// <summary>Name / value pairs shown in the window and copied with "Copy".</summary>
    public static List<(string Label, string Value)> Details()
    {
        var exe = Environment.ProcessPath ?? "";
        string FileVersion(string path) { try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "Data unavailable" : "Not found"; } catch { return "Data unavailable"; } }
        string AsmVersion(Type t) => t.Assembly.GetName().Version?.ToString(3) ?? "Data unavailable";
        return
        [
            ("Version", Version),
            ("Developer", Developer),
            ("Copyright", Copyright),
            ("Built", File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "Data unavailable"),
            (".NET", RuntimeInformation.FrameworkDescription),
            ("Windows", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
            ("Remote desktop client", "mstscax.dll " + FileVersion(Path.Combine(Environment.SystemDirectory, "mstscax.dll"))),
            ("PowerShell SDK", AsmVersion(typeof(System.Management.Automation.PowerShell))),
            ("WPF UI", AsmVersion(typeof(FluentWindow))),
            ("Program", exe),
            ("Settings", SettingsStore.FilePath),
            ("Diagnostic log", DiagLog.FilePath),
        ];
    }

    public AboutWindow()
    {
        Title = "About RemoteHub";
        // As tall as the content (no scrolling); only on a very small screen does the list scroll.
        Width = 640; MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        var root = new DockPanel();
        var bar = new TitleBar { Title = "About RemoteHub", ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Copy version info", Icon = new SymbolIcon(SymbolRegular.Copy24), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, Details().Select(d => $"{d.Label}: {d.Value}"))); copy.Content = "Copied"; }
            catch { /* clipboard busy */ }
        };
        var folder = new Button { Content = "Open program folder", Icon = new SymbolIcon(SymbolRegular.FolderOpen24), Margin = new Thickness(0, 0, 8, 0) };
        folder.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Environment.ProcessPath}\"") { UseShellExecute = true }); } catch { } };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 100, Appearance = ControlAppearance.Primary };
        close.Click += (_, _) => Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(folder);
        buttons.Children.Add(close);
        footer.Child = buttons;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel { Margin = new Thickness(28, 4, 28, 16) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var logo = new Image { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        AppIcon.Apply(logo, 64);
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "RemoteHub", FontSize = 26, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = $"Version {Version}  ·  Remote desktop, PowerShell, files, registry, services and processes in one place", TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        names.Children.Add(sub);
        head.Children.Add(names);
        body.Children.Add(head);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var rows = Details();
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].Label, Margin = new Thickness(0, 3, 12, 3) };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var v = new System.Windows.Controls.TextBox
            {
                Text = rows[i].Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(0),
            };
            v.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
        }
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(1), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        body.Children.Add(card);
        var former = new TextBlock { Text = $"{Copyright}. Developed by {Developer}.\nFormerly \"RDP Manager\". Settings of earlier versions are taken over automatically.", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        former.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        body.Children.Add(former);

        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
