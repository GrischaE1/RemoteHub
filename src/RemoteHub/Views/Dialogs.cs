using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace RemoteHub.Views;

/// <summary>Fluent message boxes (separate windows, so they are never hidden behind an RDP session).</summary>
public static class Dialogs
{
    public static async Task<bool> Confirm(Window owner, string title, string text, string primary) =>
        await Show(owner, title, text, primary, null, "Cancel") == MessageBoxResult.Primary;

    public static Task<MessageBoxResult> Ask(Window owner, string title, string text, string primary, string secondary) =>
        Show(owner, title, text, primary, secondary, "Cancel");

    /// <summary>Result of <see cref="Choose"/> when the extra button was clicked.</summary>
    public const MessageBoxResult Extra = (MessageBoxResult)100;

    /// <summary>
    /// Two choices plus Close; optionally an extra action (e.g. Delete) as a separate button below the text – it returns
    /// <see cref="Extra"/>.
    /// </summary>
    public static async Task<MessageBoxResult> Choose(Window owner, string title, string text, string primary, string secondary, string close,
        (string Text, SymbolRegular Icon)? extra = null)
    {
        if (extra is null) return await Show(owner, title, text, primary, secondary, close);
        var box = new ChoiceBox();
        Setup(box, owner, title, text, primary, secondary, close);
        var clicked = false;
        var button = new Wpf.Ui.Controls.Button
        {
            Content = extra.Value.Text, Icon = new SymbolIcon(extra.Value.Icon), Appearance = ControlAppearance.Danger,
            Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => { clicked = true; box.CloseWith(Wpf.Ui.Controls.MessageBoxButton.Close); };
        var panel = new System.Windows.Controls.StackPanel();
        var message = (UIElement)box.Content;
        box.Content = null; // detach before moving it into the panel
        panel.Children.Add(message);
        panel.Children.Add(button);
        box.Content = panel;
        var result = await box.ShowDialogAsync();
        return clicked ? Extra : result;
    }

    public static Task Info(Window owner, string title, string text) => Show(owner, title, text, null, null, "OK");

    private static Task<MessageBoxResult> Show(Window owner, string title, string text, string? primary, string? secondary, string close)
    {
        var mb = new MessageBox();
        Setup(mb, owner, title, text, primary, secondary, close);
        return mb.ShowDialogAsync();
    }

    private static void Setup(MessageBox mb, Window owner, string title, string text, string? primary, string? secondary, string close)
    {
        mb.Owner = owner;
        mb.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        mb.Title = title;
        mb.Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        mb.CloseButtonText = close;
        // The footer splits its width evenly between the buttons: with three, wide enough for "Show in folder".
        var buttons = 1 + (primary is null ? 0 : 1) + (secondary is null ? 0 : 1);
        var longest = new[] { primary, secondary, close }.Max(t => t?.Length ?? 0);
        if (buttons == 3) mb.MinWidth = Math.Max(480, 3 * (longest * 9 + 48) + 64);
        mb.IsPrimaryButtonEnabled = primary is not null;
        mb.IsSecondaryButtonEnabled = secondary is not null;
        if (primary is not null) { mb.PrimaryButtonText = primary; mb.PrimaryButtonAppearance = ControlAppearance.Primary; }
        if (secondary is not null) mb.SecondaryButtonText = secondary;
    }

    /// <summary>MessageBox that an extra button can close the same way its own Close button does.</summary>
    private sealed class ChoiceBox : MessageBox
    {
        // The theme's style is keyed by MessageBox: without this the subclass has no template (no title, no buttons).
        public ChoiceBox() => SetResourceReference(StyleProperty, typeof(MessageBox));

        public void CloseWith(Wpf.Ui.Controls.MessageBoxButton button) => OnButtonClick(button);
    }
}
