using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>The highlights of the versions an upgrade brought – once on the first start after it, or from Help &amp; about.</summary>
public sealed class WhatsNewWindow : FluentWindow
{
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 4, 24, 22) };

    /// <param name="entries">The versions to show, newest first (<see cref="FirstStart.Since"/>).</param>
    /// <param name="updatedFrom">The version before the upgrade, if known – shown under the title.</param>
    public WhatsNewWindow(IReadOnlyList<WhatsNewEntry> entries, AppVersion? updatedFrom = null)
    {
        Title = "What's new";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        ShowInTaskbar = false;

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = "What's new", ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(_body);
        Content = root;

        // Named after the newest entry: a version without highlights of its own (a patch) shows the last ones there were.
        Head($"What's new in RemoteHub {(entries.Count > 0 ? entries[0].Version : UpdateService.Current.ToString())}",
             updatedFrom is null ? $"Installed: {UpdateService.Current}" : $"Updated from {updatedFrom} to {UpdateService.Current}");
        var list = new StackPanel();
        foreach (var entry in entries)
        {
            if (entries.Count > 1) list.Children.Add(Text(entry.Version, bottom: 4, weight: FontWeights.SemiBold));
            list.Children.Add(Highlights(entry.Highlights));
        }
        // A big upgrade can bring several versions: their list scrolls, the buttons stay in view.
        _body.Children.Add(new ScrollViewer { Content = list, MaxHeight = SystemParameters.WorkArea.Height * 0.6, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var notes = new Button { Content = "All release notes", Icon = new SymbolIcon(SymbolRegular.Open24) };
        notes.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage) { UseShellExecute = true }); } catch { /* no browser */ } };
        var close = new Button { Content = "Close", Appearance = ControlAppearance.Primary, IsCancel = true, IsDefault = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(notes);
        buttons.Children.Add(close);
        _body.Children.Add(buttons);
    }

    private static TextBlock Text(string t, bool muted = false, double bottom = 8, double size = 14, FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom), FontSize = size, FontWeight = weight ?? FontWeights.Normal };
        if (muted) tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private void Head(string title, string sub)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var logo = new System.Windows.Controls.Image { Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        AppIcon.Apply(logo, 40);
        DockPanel.SetDock(logo, Dock.Left);
        head.Children.Add(logo);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Text(title, bottom: 2, size: 18, weight: FontWeights.SemiBold));
        names.Children.Add(Text(sub, muted: true, bottom: 0, size: 13));
        head.Children.Add(names);
        _body.Children.Add(head);
    }

    /// <summary>One card per version, a line per highlight.</summary>
    private static Border Highlights(IEnumerable<string> lines)
    {
        var list = new StackPanel();
        foreach (var line in lines)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var bullet = new SymbolIcon(SymbolRegular.Sparkle16) { FontSize = 14, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            bullet.SetResourceReference(IconElement.ForegroundProperty, "SystemAccentColorPrimaryBrush");
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            list.Children.Add(row);
        }
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1), Child = list };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }
}
