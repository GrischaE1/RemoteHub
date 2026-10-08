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

    public static Task Info(Window owner, string title, string text) => Show(owner, title, text, null, null, "OK");

    private static Task<MessageBoxResult> Show(Window owner, string title, string text, string? primary, string? secondary, string close)
    {
        var mb = new MessageBox
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 },
            CloseButtonText = close,
            IsPrimaryButtonEnabled = primary is not null,
            IsSecondaryButtonEnabled = secondary is not null,
        };
        if (primary is not null) { mb.PrimaryButtonText = primary; mb.PrimaryButtonAppearance = ControlAppearance.Primary; }
        if (secondary is not null) mb.SecondaryButtonText = secondary;
        return mb.ShowDialogAsync();
    }
}
