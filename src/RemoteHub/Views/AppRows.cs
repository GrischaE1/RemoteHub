using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteHub.Models;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace RemoteHub.Views;

/// <summary>
/// An app as RemoteHub shows it in a list (docs/app-tabs-design.md §8): its icon, its name, what it starts, and a note
/// on the right – the same row in the Start menu picker and on a connection's Apps page. Written like the pickers' rows
/// (RunScriptWindow.PickRow).
/// </summary>
public static class AppRows
{
    /// <summary>The app's icon at 32 px: the device's PNG when there is one that decodes, else a generic symbol.</summary>
    public static FrameworkElement Icon(byte[]? png, double size = 32)
    {
        if (IconImages.ToImage(png) is { } image) return new Image { Source = image, Width = size, Height = size };
        var symbol = new SymbolIcon(SymbolRegular.Apps24) { FontSize = size * 0.75, Width = size, Height = size };
        symbol.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        return symbol;
    }

    /// <summary>The row: icon, name, then <c>Program Arguments</c> as a caption; <paramref name="note"/> on the right ("Runs as administrator", "Added").</summary>
    public static FrameworkElement Row(FrameworkElement host, string name, string program, string arguments, byte[]? icon, string note = "")
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var picture = Icon(icon);
        picture.Margin = new Thickness(0, 0, 10, 0);
        picture.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        if (note.Length > 0)
        {
            var right = new TextBlock { Text = note, Style = host.TryFindResource("Caption") as Style, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = arguments.Trim().Length > 0 ? $"{program} {arguments}" : program,
            Style = host.TryFindResource("Caption") as Style, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(text);
        return row;
    }

    /// <summary>A connection's app as a row.</summary>
    public static FrameworkElement Row(FrameworkElement host, ConnectionApp app, string note = "") =>
        Row(host, app.Name, app.Program, app.Arguments, app.Icon, note.Length > 0 ? note : app.Elevated ? "Runs as administrator" : "");
}
