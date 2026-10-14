using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RemoteHub.Services;
using RemoteHub.Views;

namespace RemoteHub;

/// <summary>
/// Auto-lock: with the startup protection on, RemoteHub locks itself after the set time without any input on this PC
/// (mouse or keyboard, in any program – like the screen saver). Its windows are hidden, the settings key is wiped from
/// memory, and the unlock window asks for Windows Hello or the password. Sessions, transfers and recordings keep
/// running behind it.
/// </summary>
public partial class MainWindow
{
    private DispatcherTimer? _autoLockTimer;
    private bool _locked;

    private void StartAutoLock()
    {
        _autoLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _autoLockTimer.Tick += (_, _) => { if (AutoLockDue()) LockNow(); };
        _autoLockTimer.Start();
    }

    private bool AutoLockDue() =>
        !_locked && AppLock.IsEnabled && _vm.Data.Ui.AutoLockMinutes is > 0 and var minutes && IdleTime() >= TimeSpan.FromMinutes(minutes)
        // A dialog is open (e.g. editing a connection): hiding it would end it. Locks when it's closed.
        && !ComponentDispatcher.IsThreadModal;

    /// <summary>Hides RemoteHub until it's unlocked again.</summary>
    private void LockNow()
    {
        if (_locked || !AppLock.IsEnabled) return;
        _locked = true;
        try
        {
            _vm.SaveNow();
            SecureClipboard.ClearIfOurs();
            var shown = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList();
            foreach (var w in shown) w.Hide();
            AppLock.Lock();
            DiagLog.Write("Startup protection: locked after a while without input");
            Data.AuditLog.Write(Data.AuditEvents.AppLocked, null, null, $"After {_vm.Data.Ui.AutoLockMinutes} min without input");
            // Not unlocked (Exit, or the window closed): RemoteHub ends – without showing anything again.
            var unlock = new UnlockWindow(whileRunning: true);
            WindowPlacement.CenterOn(unlock, this); // where RemoteHub was, not the middle of the screen
            if (unlock.ShowDialog() != true)
            {
                DiagLog.Write("Startup protection: exited while locked");
                _forceClose = true;
                Shutdown(locked: true);
                Application.Current.Shutdown();
                return;
            }
            foreach (var w in shown) w.Show();
            Activate();
        }
        finally { _locked = false; }
    }

    /// <summary>Time since the last mouse or keyboard input in this Windows session.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
