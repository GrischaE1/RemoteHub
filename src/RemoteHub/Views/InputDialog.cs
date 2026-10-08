using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Asks for one line of text.</summary>
public sealed class InputDialog : FluentWindow
{
    private readonly TextBox _box;

    public string Value => _box.Text;

    public InputDialog(string title, string message, string initial)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var info = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", Appearance = ControlAppearance.Primary, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(info);
        body.Children.Add(_box);
        body.Children.Add(buttons);
        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
