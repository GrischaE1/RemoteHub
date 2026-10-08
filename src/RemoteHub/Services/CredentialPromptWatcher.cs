using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace RemoteHub.Services;

/// <summary>
/// Detects the Windows Security credential dialog ("Credential Dialog Xaml Host", shown by CredentialUIBroker.exe)
/// that the RDP client opens when it needs (other) credentials, and reports the texts shown in it.
/// </summary>
public sealed class CredentialPromptWatcher : IDisposable
{
    public const string DialogClass = "Credential Dialog Xaml Host";

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private readonly WinEventProc _proc; // keep the delegate alive while hooked
    private readonly IntPtr _hook;
    private readonly HashSet<IntPtr> _seen = new();

    /// <summary>Raised on the UI thread: dialog handle, who owns it, texts shown in it (often unreadable → empty).</summary>
    public event Action<IntPtr, PromptOwner, IReadOnlyList<string>>? PromptShown;

    public CredentialPromptWatcher()
    {
        _proc = OnWinEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != DialogClass || !_seen.Add(hwnd)) return;
        if (_seen.Count > 50) _seen.Clear();

        DiagLog.Write($"Credential dialog shown (hwnd {hwnd})");
        var ownerHwnd = GetWindow(hwnd, 4 /* GW_OWNER */);
        var owner = PromptOwner.None;
        if (ownerHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(ownerHwnd, out var pid);
            owner = pid == Environment.ProcessId ? PromptOwner.Ours : PromptOwner.Other;
        }

        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        // The dialog content is rendered shortly after the window appears; read it off the UI thread.
        Task.Run(async () =>
        {
            await Task.Delay(400);
            var texts = ReadTexts(hwnd);
            DiagLog.Write($"Credential dialog: owner={owner}, texts read={texts.Count}");
            dispatcher.BeginInvoke(() => PromptShown?.Invoke(hwnd, owner, texts));
        });
    }

    private static IReadOnlyList<string> ReadTexts(IntPtr hwnd)
    {
        var list = new List<string>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, cond))
                if (!string.IsNullOrWhiteSpace(el.Current.Name)) list.Add(el.Current.Name.Trim());
        }
        catch (Exception ex) { Debug.WriteLine("Credential prompt texts: " + ex.Message); }
        return list;
    }

    public static bool IsStillOpen(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    public static (int X, int Y, int W, int H)? Bounds(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? (r.L, r.T, r.R - r.L, r.B - r.T) : null;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
}
