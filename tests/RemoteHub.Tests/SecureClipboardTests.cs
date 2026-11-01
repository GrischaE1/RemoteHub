using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>Secrets on the clipboard (stored passwords, the remoting setup script): cleared after the timeout.</summary>
[Collection("Clipboard")]
public class SecureClipboardTests
{
    [SkippableFact]
    public void A_secret_is_cleared_after_the_timeout_when_the_copying_window_is_already_closed()
    {
        bool? copied = null, clearedInTime = null;
        string? left = null;
        RunSta(() =>
        {
            // The remoting setup dialog copies the script and is usually closed before the timeout.
            var window = HiddenWindow();
            copied = SecureClipboard.CopySecret(window.Handle, "setup script", TimeSpan.FromMilliseconds(300));
            window.Dispose();
            if (copied != true) return;
            clearedInTime = PumpUntil(() => !SecureClipboard.HoldsSecret, TimeSpan.FromSeconds(10));
            left = ClipboardText();
        });
        Skip.IfNot(copied == true, "no clipboard in this session");
        Assert.True(clearedInTime, "the secret wasn't cleared after its window was closed");
        Assert.Null(left);
    }

    [SkippableFact]
    public void Clearing_now_works_after_the_copying_window_is_closed()
    {
        bool? copied = null, holdsBefore = null, holdsAfter = null;
        string? left = null;
        RunSta(() =>
        {
            var window = HiddenWindow();
            copied = SecureClipboard.CopySecret(window.Handle, "setup script", TimeSpan.FromMinutes(2));
            window.Dispose();
            if (copied != true) return;
            holdsBefore = SecureClipboard.HoldsSecret;
            SecureClipboard.ClearIfOurs(); // as at auto-lock or exit
            holdsAfter = SecureClipboard.HoldsSecret;
            left = ClipboardText();
        });
        Skip.IfNot(copied == true, "no clipboard in this session");
        Assert.True(holdsBefore);
        Assert.False(holdsAfter);
        Assert.Null(left);
    }

    [SkippableFact]
    public void Something_copied_afterwards_is_not_cleared()
    {
        bool? copied = null;
        string? left = null;
        RunSta(() =>
        {
            var window = HiddenWindow();
            try
            {
                copied = SecureClipboard.CopySecret(window.Handle, "password", TimeSpan.FromMinutes(2));
                if (copied != true) return;
                WithClipboard(() => System.Windows.Clipboard.SetText("copied by the user"));
                SecureClipboard.ClearIfOurs();
                left = ClipboardText();
                WithClipboard(System.Windows.Clipboard.Clear);
            }
            finally { window.Dispose(); }
        });
        Skip.IfNot(copied == true, "no clipboard in this session");
        Assert.Equal("copied by the user", left);
    }

    private static HwndSource HiddenWindow() => new(new HwndSourceParameters("RemoteHub test") { Width = 1, Height = 1, WindowStyle = 0 });

    private static string? ClipboardText() =>
        WithClipboard(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null);

    private static void WithClipboard(Action action) => WithClipboard<object?>(() => { action(); return null; });

    /// <summary>
    /// Retried while pumping messages: a clipboard viewer (rdpclip, the agent's helper) may hold the clipboard after
    /// each change and wait for our window to answer before it lets go.
    /// </summary>
    private static T WithClipboard<T>(Func<T> action)
    {
        for (var i = 0; ; i++)
        {
            try { return action(); }
            catch (System.Runtime.InteropServices.COMException) when (i < 20) { PumpUntil(() => false, TimeSpan.FromMilliseconds(50)); }
        }
    }

    /// <summary>Runs the dispatcher (the clear timer is a DispatcherTimer) until the condition holds or time runs out.</summary>
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            var frame = new DispatcherFrame();
            _ = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
        }
        return true;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally
            {
                SecureClipboard.ClearIfOurs();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        TestThread.Join(t, "the STA part");
        if (error != null) throw new Xunit.Sdk.XunitException("The STA part failed: " + error);
    }
}
