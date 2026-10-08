using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteHub.Rdp;

/// <summary>WinForms AxHost wrapper around MsRdpClient9NotSafeForScripting (mstscax.dll).</summary>
internal sealed class RdpClientHost : AxHost
{
    private const string ClsidMsRdpClient9 = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";

    private object? _ocx;
    private ConnectionPointCookie? _cookie;
    private EventSink? _sink;

    public RdpClientHost() : base(ClsidMsRdpClient9) { }

    /// <summary>Late-bound IMsRdpClient9 (dual interface).</summary>
    public dynamic Client => _ocx ?? throw new InvalidOperationException("RDP control not created yet.");
    public bool IsCreated => _ocx is not null;

    public IMsRdpClientNonScriptable NonScriptable => (IMsRdpClientNonScriptable)(_ocx ?? throw new InvalidOperationException());
    public IMsRdpExtendedSettings? ExtendedSettings => _ocx as IMsRdpExtendedSettings;

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action? EnterFullScreen;
    public event Action? LeaveFullScreen;
    public event Action<int>? FatalError;
    public event Action<int>? LogonError;
    public event Action<int, int>? AutoReconnecting;
    public event Action? AutoReconnected;
    public event Action? RequestMinimize;
    public event Action? ControlCreated;
    public event Action<int>? FocusReleased;
    /// <summary>The server reports a new desktop size (width, height in pixels).</summary>
    public event Action<int, int>? RemoteDesktopSizeChanged;

    protected override void AttachInterfaces() => _ocx = GetOcx();

    protected override void CreateSink()
    {
        if (_ocx is null) return;
        _sink = new EventSink(this);
        _cookie = new ConnectionPointCookie(_ocx, _sink, typeof(IMsTscAxEvents));
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
        _sink = null;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ControlCreated?.Invoke();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EventSink(RdpClientHost h) : IMsTscAxEvents
    {
        public void OnConnecting() => h.Connecting?.Invoke();
        public void OnConnected() => h.Connected?.Invoke();
        public void OnLoginComplete() => h.LoginComplete?.Invoke();
        public void OnDisconnected(int discReason) => h.Disconnected?.Invoke(discReason);
        public void OnEnterFullScreenMode() => h.EnterFullScreen?.Invoke();
        public void OnLeaveFullScreenMode() => h.LeaveFullScreen?.Invoke();
        public void OnFatalError(int errorCode) => h.FatalError?.Invoke(errorCode);
        public void OnWarning(int warningCode) { }
        public void OnRemoteDesktopSizeChange(int width, int height) => h.RemoteDesktopSizeChanged?.Invoke(width, height);
        public void OnRequestContainerMinimize() => h.RequestMinimize?.Invoke();
        public void OnLogonError(int lError) => h.LogonError?.Invoke(lError);
        public void OnFocusReleased(int iDirection) => h.FocusReleased?.Invoke(iDirection);
        public void OnAutoReconnected() => h.AutoReconnected?.Invoke();
        public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount)
            => h.AutoReconnecting?.Invoke(attemptCount, maxAttemptCount);
    }
}
