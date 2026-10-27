using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;

namespace RemoteHub.Rdp;

/// <summary>Anything that lives in a tab: an RDP session or a PowerShell remoting session.</summary>
public abstract partial class SessionBase : ObservableObject, IDisposable
{
    protected SessionBase(Connection connection)
    {
        Connection = connection;
        _title = connection.Name;
    }

    public Connection Connection { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private SessionState _state = SessionState.Idle;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isDetached;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Non-secret description of the credentials used for the last connect (source, user format, password yes/no).</summary>
    [ObservableProperty] private string _credentialInfo = "";
    /// <summary>How a remote tool tab reaches the device: "RemoteHub Agent" or "PowerShell remoting (HTTP/HTTPS)". Empty for RDP.</summary>
    [ObservableProperty] private string _transport = "";
    /// <summary>Frame/tab colour: the connection's colour tag, or the app accent.</summary>
    [ObservableProperty] private System.Windows.Media.Brush _accentBrush = Views.ColorTags.Frozen(Views.AppChrome.Accent);
    /// <summary>True while keystrokes go to this session.</summary>
    [ObservableProperty] private bool _hasKeyboardFocus;
    /// <summary>True while this tab is recorded to a video file.</summary>
    [ObservableProperty] private bool _isRecording;
    /// <summary>"REC 01:23" while recording, otherwise empty.</summary>
    [ObservableProperty] private string _recordingText = "";

    public bool IsConnected => State == SessionState.Connected;
    public bool ShowsOverlay => State is SessionState.Disconnected or SessionState.Failed or SessionState.Idle;
    /// <summary>Tab icon (SymbolRegular name).</summary>
    public abstract string KindSymbol { get; }
    /// <summary>Name of the tab in the connection's tab row ("Remote desktop", "Registry", …).</summary>
    public abstract string KindLabel { get; }
    public bool IsRdp => this is RdpSession;
    public bool IsPowerShell => !IsRdp;

    public event Action? Closed;

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowsOverlay));
    }

    public abstract void Disconnect();
    /// <summary>Disconnects (if needed) and releases resources. Raises <see cref="Closed"/>.</summary>
    public abstract void Close();
    public virtual void FocusSession() { }
    public abstract void Dispose();

    protected void RaiseClosed() => Closed?.Invoke();

    // ---- a device reached through a RemoteHub Broker: the transport says which path the session is on, and follows it

    private string? _brokerRoute;
    private string _transportText = "";

    /// <summary>
    /// <see cref="Transport"/> for a remote tool tab's target: "RemoteHub Agent", and through a RemoteHub Broker with the
    /// path new streams take – "RemoteHub Agent · through RemoteHub Broker (direct)" or "(relayed)".
    /// </summary>
    protected void ShowTransport(Remoting.PsTarget target)
    {
        _transportText = target.Transport;
        var route = target.Agent?.Broker;
        if (_brokerRoute is null && route is not null) Broker.PeerSessions.PathChanged += OnBrokerPath;
        else if (_brokerRoute is not null && route is null) Broker.PeerSessions.PathChanged -= OnBrokerPath;
        _brokerRoute = route?.Key;
        Transport = route is null ? _transportText : WithPath(Broker.PeerSessions.PathOf(route));
    }

    /// <summary>Called when the tab goes: no more path changes for it.</summary>
    protected void StopFollowingPath()
    {
        if (_brokerRoute is null) return;
        Broker.PeerSessions.PathChanged -= OnBrokerPath;
        _brokerRoute = null;
    }

    private string WithPath(Broker.PeerPath? path) => TransportWithPath(_transportText, path);

    internal static string TransportWithPath(string transport, Broker.PeerPath? path) => path switch
    {
        null => $"{transport} · through RemoteHub Broker",
        Broker.PeerPath.Relayed => $"{transport} · through RemoteHub Broker (relayed)",
        Broker.PeerPath.Network => $"{transport} · direct (the device's own address)",
        _ => $"{transport} · through RemoteHub Broker (direct)",
    };

    private void OnBrokerPath(string route, Broker.PeerPath path)
    {
        if (route != _brokerRoute) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (route == _brokerRoute) Transport = WithPath(path); });
    }
}
