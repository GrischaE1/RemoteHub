using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RemoteHub.Models;
using RemoteHub.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace RemoteHub.Views;

/// <summary>Manage named credentials that folders and connections can reference.</summary>
public sealed class CredentialsWindow : FluentWindow
{
    private readonly AppData _data;
    private readonly Func<IEnumerable<NodeBase>> _allNodes;
    private readonly ListBox _list = new() { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly TextBox _name = new() { PlaceholderText = "e.g. Domain admin", Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _user = new() { PlaceholderText = "user or DOMAIN\\user", Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _domain = new() { PlaceholderText = "Optional", Margin = new Thickness(0, 0, 0, 10) };
    private readonly System.Windows.Controls.CheckBox _local = new()
    {
        Content = "Local account on the target computer  (.\\user)",
        Margin = new Thickness(0, 0, 0, 10),
        ToolTip = "Signs in with a local account of whichever computer you connect to.",
    };
    private readonly PasswordBox _password = new() { PlaceholderText = "Leave empty to be asked when connecting" };
    private readonly TextBlock _usage = new() { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _details = new();
    private SavedCredential? _current;

    public CredentialsWindow(AppData data, Func<IEnumerable<NodeBase>> allNodes)
    {
        _data = data;
        _allNodes = allNodes;
        Title = "Saved credentials";
        Width = 760; Height = 520; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        _list.ItemsSource = data.SavedCredentials;
        _list.ItemTemplate = BuildItemTemplate();
        _list.SelectionChanged += (_, _) => Select(_list.SelectedItem as SavedCredential);

        var add = new Button { Content = "Add", Icon = new SymbolIcon(SymbolRegular.Add24), Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 6, 0) };
        add.Click += (_, _) => AddNew();
        var del = new Button { Content = "Delete", Icon = new SymbolIcon(SymbolRegular.Delete24) };
        del.Click += async (_, _) => await DeleteCurrent();

        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        listButtons.Children.Add(add);
        listButtons.Children.Add(del);

        var left = new DockPanel { Margin = new Thickness(20, 0, 10, 20) };
        DockPanel.SetDock(listButtons, Dock.Top);
        left.Children.Add(listButtons);
        left.Children.Add(_list);

        _name.TextChanged += (_, _) => { if (_current is not null) _current.Name = _name.Text; };
        _user.TextChanged += (_, _) => { if (_current is not null) _current.Username = NullIfEmpty(_user.Text); };
        _domain.TextChanged += (_, _) =>
        {
            if (_current is not null) _current.Domain = NullIfEmpty(_domain.Text);
            _local.IsChecked = _domain.Text == CredentialSet.LocalComputerDomain;
            _domain.IsEnabled = _local.IsChecked != true;
        };
        _local.Checked += (_, _) => { if (_domain.Text != CredentialSet.LocalComputerDomain) _domain.Text = CredentialSet.LocalComputerDomain; };
        _local.Unchecked += (_, _) => { if (_domain.Text == CredentialSet.LocalComputerDomain) _domain.Text = ""; };
        _password.PasswordChanged += (_, _) => { if (_current is not null) _current.Password = NullIfEmpty(_password.Password); };
        _usage.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _details.Children.Add(Label("Name")); _details.Children.Add(_name);
        _details.Children.Add(Label("Username")); _details.Children.Add(_user);
        _details.Children.Add(Label("Domain")); _details.Children.Add(_domain);
        _details.Children.Add(_local);
        _details.Children.Add(Label("Password")); _details.Children.Add(_password);
        _details.Children.Add(_usage);

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            Text = "Saved credentials can be selected in any folder or connection. Changing one here updates every item that uses it.",
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var right = new StackPanel { Margin = new Thickness(10, 0, 20, 20) };
        right.Children.Add(hint);
        right.Children.Add(_details);

        var close = new Button { Content = "Done", Appearance = ControlAppearance.Primary, IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        close.Click += (_, _) => Close();

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(left);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        var root = new DockPanel();
        var tb = new TitleBar { Title = Title, ShowMaximize = false, ShowMinimize = false };
        DockPanel.SetDock(tb, Dock.Top);
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(tb);
        root.Children.Add(close);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) =>
        {
            if (data.SavedCredentials.Count > 0) _list.SelectedIndex = 0;
            else Select(null);
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 0, 0, 4) };

    private static DataTemplate BuildItemTemplate()
    {
        var sp = new FrameworkElementFactory(typeof(StackPanel));
        sp.SetValue(MarginProperty, new Thickness(2));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SavedCredential.Name)));
        var user = new FrameworkElementFactory(typeof(TextBlock));
        user.SetBinding(TextBlock.TextProperty, new Binding(nameof(SavedCredential.Display)));
        user.SetValue(TextBlock.FontSizeProperty, 11.0);
        user.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        sp.AppendChild(name);
        sp.AppendChild(user);
        return new DataTemplate { VisualTree = sp };
    }

    private void Select(SavedCredential? c)
    {
        _current = null; // suppress change handlers while filling
        _details.IsEnabled = c is not null;
        _name.Text = c?.Name ?? "";
        _user.Text = c?.Username ?? "";
        _domain.Text = c?.Domain ?? "";
        _password.Password = c?.Password ?? "";
        _usage.Text = c is null ? "Click Add to create a credential."
            : $"Used by {Inheritance.UsageCount(_data, c.Id, _allNodes())} item(s).";
        _current = c;
    }

    private void AddNew()
    {
        var c = new SavedCredential { Name = $"Credential {_data.SavedCredentials.Count + 1}" };
        _data.SavedCredentials.Add(c);
        _list.SelectedItem = c;
        _name.Focus();
        _name.SelectAll();
    }

    private async Task DeleteCurrent()
    {
        if (_current is not { } c) return;
        var used = Inheritance.UsageCount(_data, c.Id, _allNodes());
        var text = used == 0
            ? $"Delete \"{c.Name}\"?"
            : $"\"{c.Name}\" is used by {used} item(s). They will fall back to inheriting credentials from their parent folder. Delete it?";
        if (!await Dialogs.Confirm(this, "Delete credential", text, "Delete")) return;

        foreach (var n in _allNodes().Where(n => n.CredentialId == c.Id))
        {
            n.CredentialId = null;
            n.InheritCredentials = true;
        }
        if (_data.DefaultCredentialId == c.Id) _data.DefaultCredentialId = null;
        _data.SavedCredentials.Remove(c);
        _list.SelectedIndex = _data.SavedCredentials.Count > 0 ? 0 : -1;
        if (_data.SavedCredentials.Count == 0) Select(null);
    }
}
