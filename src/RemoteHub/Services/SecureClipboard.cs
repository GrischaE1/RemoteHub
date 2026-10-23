using System.Runtime.InteropServices;

namespace RemoteHub.Services;

/// <summary>
/// Puts a secret on the clipboard for a short time.
/// - Marked with the documented Windows formats that keep it out of clipboard history (Win+V) and cloud clipboard
///   ("ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory"=0, "CanUploadToCloudClipboard"=0).
/// - Cleared automatically after a timeout, on <see cref="ClearIfOurs"/>, or at app exit – but only if nobody has
///   copied something else in the meantime.
/// </summary>
public static class SecureClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private static uint _ourSequence;
    private static System.Windows.Threading.DispatcherTimer? _timer;

    public static bool HoldsSecret => _ourSequence != 0 && GetClipboardSequenceNumber() == _ourSequence;

    public static bool CopySecret(IntPtr ownerHwnd, string secret, TimeSpan clearAfter) => CopySecret(ownerHwnd, secret.AsSpan(), clearAfter);

    /// <summary>Copies a stored password; it is decrypted only while it is written to the clipboard.</summary>
    public static bool CopySecret(IntPtr ownerHwnd, Secret secret, TimeSpan clearAfter)
    {
        var done = false;
        secret.Use(v => done = CopySecret(ownerHwnd, v, clearAfter));
        return done;
    }

    private static bool CopySecret(IntPtr ownerHwnd, ReadOnlySpan<char> secret, TimeSpan clearAfter)
    {
        if (!OpenWithRetry(ownerHwnd)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            SetText(secret);
            SetData(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0]);
            SetData(RegisterClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
            SetData(RegisterClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
        }
        finally { CloseClipboard(); }

        _ourSequence = GetClipboardSequenceNumber();
        _timer?.Stop();
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = clearAfter };
        _timer.Tick += (_, _) => ClearIfOurs();
        _timer.Start();
        return true;
    }

    /// <summary>Empties the clipboard if it still holds the secret we put there.</summary>
    public static void ClearIfOurs()
    {
        _timer?.Stop();
        if (!HoldsSecret) { _ourSequence = 0; return; }
        // Opened without an owner: the window that copied may be gone by now (the remoting setup dialog is usually
        // closed before the timeout), and OpenClipboard fails for a destroyed window (error 1400). Emptying needs none.
        if (!OpenWithRetry(IntPtr.Zero))
        {
            // Another program holds the clipboard: the secret is still there, so keep it marked as ours and try again
            // shortly – auto-lock and exit can still clear it too.
            if (_timer != null) { _timer.Interval = RetryAfter; _timer.Start(); }
            return;
        }
        try { EmptyClipboard(); }
        finally { CloseClipboard(); }
        _ourSequence = 0;
    }

    private static bool OpenWithRetry(IntPtr owner)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(owner)) return true;
            System.Threading.Thread.Sleep(20); // another process may hold the clipboard briefly
        }
        return false;
    }

    /// <summary>CF_UNICODETEXT straight from the span, with no managed copy of the text.</summary>
    private static unsafe void SetText(ReadOnlySpan<char> text)
    {
        var size = (text.Length + 1) * sizeof(char);
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)size);
        if (h == IntPtr.Zero) return;
        var p = (char*)GlobalLock(h);
        text.CopyTo(new Span<char>(p, text.Length));
        p[text.Length] = '\0';
        GlobalUnlock(h);
        if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
        {
            // Not handed over: wipe it before freeing, as the system would never see it.
            p = (char*)GlobalLock(h);
            new Span<byte>(p, size).Clear();
            GlobalUnlock(h);
            GlobalFree(h);
        }
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (h == IntPtr.Zero) return;
        var p = GlobalLock(h);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h); // on success the system owns the memory
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
}
