using System.Threading.Tasks;
using System.Windows;
using RemoteHub.Views;

namespace RemoteHub.Tests;

public class DialogsTests
{
    /// <summary>The extra button (e.g. "Delete recording…") closes the dialog and reports <see cref="Dialogs.Extra"/>.</summary>
    [Fact]
    public void Choose_ExtraButtonClosesTheDialog()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "dialog did not close");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run()
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var owner = new Window { Width = 300, Height = 200, ShowInTaskbar = false };
        owner.Show();
        var frame = new System.Windows.Threading.DispatcherFrame();
        Wpf.Ui.Controls.MessageBoxResult result = default;
        string? failureInDialog = null;
        // The dialog is modal: schedule the click before opening it.
        owner.Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(500);
            var dialog = Application.Current?.Windows.OfType<Wpf.Ui.Controls.MessageBox>().FirstOrDefault()
                         ?? owner.OwnedWindows.OfType<Wpf.Ui.Controls.MessageBox>().First();
            var button = FindButton(dialog, "Delete recording…");
            if (button is null) { failureInDialog = "extra button not found"; ((Window)dialog).Close(); return; }
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        var task = Dialogs.Choose(owner, "Recording saved", "test.mp4", "Play", "Show in folder", "Close", ("Delete recording…", Wpf.Ui.Controls.SymbolRegular.Delete24));
        task.ContinueWith(t => { if (t.IsFaulted) failureInDialog = t.Exception!.InnerException!.ToString(); else result = t.Result; frame.Continue = false; }, TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.Null(failureInDialog);
        Assert.Equal(Dialogs.Extra, result);
        Assert.DoesNotContain(owner.OwnedWindows.OfType<Wpf.Ui.Controls.MessageBox>(), w => w.IsVisible);
        owner.Close();
    }

    private static Wpf.Ui.Controls.Button? FindButton(DependencyObject root, string text)
    {
        if (root is Wpf.Ui.Controls.Button b && Equals(b.Content, text)) return b;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindButton(child, text) is { } found) return found;
        return null;
    }
}
