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
    /// <summary>Frame/tab colour: the connection's colour tag, or the app accent.</summary>
    [ObservableProperty] private System.Windows.Media.Brush _accentBrush = Views.ColorTags.Frozen(Views.AppChrome.Accent);
    /// <summary>True while keystrokes go to this session.</summary>
    [ObservableProperty] private bool _hasKeyboardFocus;

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
}
