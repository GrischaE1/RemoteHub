using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteHub.Remoting;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Creates or edits one registry value (like regedit's "Edit String / DWORD / Binary" dialogs).</summary>
public sealed class RegValueDialog : FluentWindow
{
    private readonly RegKind _kind;
    private readonly bool _isNew;
    private readonly TextBox _name = new();
    private readonly TextBox _data = new();
    private readonly RadioButton _hex = new() { Content = "Hexadecimal", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _dec = new() { Content = "Decimal" };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private bool _wasHex;

    public string ValueName => _isNew ? _name.Text : _existingName;
    private readonly string _existingName = "";
    public object Data { get; private set; } = "";

    /// <param name="existing">The value to edit, or null for a new value of <paramref name="kind"/>.</param>
    public RegValueDialog(string keyPath, RegValue? existing, RegKind kind)
    {
        _kind = existing?.Kind ?? kind;
        _isNew = existing is null;
        _existingName = existing?.Name ?? "";
        var title = (_isNew ? "New " : "Edit ") + RemoteRegistry.TypeName(_kind);
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ChromeBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        body.Children.Add(Caption(keyPath));
        body.Children.Add(Label("Value name"));
        _name.Text = _isNew ? "New Value" : existing!.DisplayName;
        _name.IsReadOnly = !_isNew;
        _name.IsEnabled = _isNew;
        body.Children.Add(_name);

        body.Children.Add(Label(_kind switch
        {
            RegKind.MultiString => "Value data (one string per line)",
            RegKind.Binary or RegKind.None => "Value data (hex bytes, e.g. 01 a0 ff)",
            _ => "Value data",
        }));
        var multi = _kind is RegKind.MultiString or RegKind.Binary or RegKind.None;
        if (multi)
        {
            _data.AcceptsReturn = true;
            _data.TextWrapping = _kind == RegKind.MultiString ? TextWrapping.NoWrap : TextWrapping.Wrap;
            _data.Height = 180;
            _data.VerticalContentAlignment = VerticalAlignment.Top;
            VerticalScrollBarVisibilityHelper(_data);
        }
        if (_kind is RegKind.Binary or RegKind.None) _data.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        body.Children.Add(_data);

        if (_kind is RegKind.DWord or RegKind.QWord)
        {
            var bases = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            bases.Children.Add(new TextBlock { Text = "Base:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            bases.Children.Add(_hex);
            bases.Children.Add(_dec);
            body.Children.Add(bases);
            _hex.IsChecked = true;
            _wasHex = true;
            _hex.Checked += (_, _) => SwitchBase(true);
            _dec.Checked += (_, _) => SwitchBase(false);
        }
        _data.Text = existing is { IsSet: true } ? RemoteRegistry.EditText(_kind, existing.Data, hex: true)
                   : _kind is RegKind.DWord or RegKind.QWord ? "0" : "";
        body.Children.Add(_error);

        var ok = new Button { Content = _isNew ? "Create" : "Save", Appearance = ControlAppearance.Primary, IsDefault = !multi, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        body.Children.Add(buttons);

        var root = new StackPanel();
        root.Children.Add(new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) =>
        {
            var box = _isNew ? _name : _data;
            box.Focus();
            box.SelectAll();
        };
    }

    private static void VerticalScrollBarVisibilityHelper(TextBox t) => ScrollViewer.SetVerticalScrollBarVisibility(t, ScrollBarVisibility.Auto);

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return t;
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold };

    /// <summary>Converts the number in the box when the base changes (like regedit).</summary>
    private void SwitchBase(bool hex)
    {
        if (hex == _wasHex) return;
        if (RemoteRegistry.TryParse(_kind, _data.Text, _wasHex, out var v, out _))
        {
            var plain = _kind == RegKind.DWord ? unchecked((uint)(int)v).ToString() : unchecked((ulong)(long)v).ToString();
            _data.Text = RemoteRegistry.EditText(_kind, plain, hex);
        }
        _wasHex = hex;
    }

    private void Accept()
    {
        if (_isNew && (_name.Text.Length == 0 || _name.Text.Length > 16383))
        {
            ShowError("Enter a name for the value.");
            return;
        }
        if (!RemoteRegistry.TryParse(_kind, _data.Text, _hex.IsChecked == true, out var data, out var error))
        {
            ShowError(error);
            return;
        }
        Data = data;
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
    }
}
