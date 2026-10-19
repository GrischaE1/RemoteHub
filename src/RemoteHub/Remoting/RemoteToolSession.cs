using System.Management.Automation;
using System.Management.Automation.Runspaces;
using RemoteHub.Models;
using RemoteHub.Rdp;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Remoting;

/// <summary>
/// Base of the UI tabs that work over one PowerShell remoting runspace (file transfer, registry):
/// connect in the background, one remote call at a time, reconnect, close.
/// </summary>
public abstract class RemoteToolSession : SessionBase
{
    private readonly System.Threading.SemaphoreSlim _lock = new(1, 1);
    private Runspace? _rs;
    private bool _closed;
    private int _generation;

    protected RemoteToolSession(Connection connection) : base(connection) => Title = connection.Name;

    public PsTarget? Target { get; private set; }
    protected bool IsClosed => _closed;

    /// <summary>Which tool this tab is, for the policy switch of its transport (docs/admx-design.md §5).</summary>
    public abstract Services.RemoteTool Tool { get; }

    public event Action<string>? Opened;
    public event Action<string>? OpenFailed;
    /// <summary>The remote side is ready (raised on the UI thread after connecting).</summary>
    public event Action? Ready;
    public event Action? FocusRequested;

    public void Connect(PsTarget target, string credentialSource, System.Windows.Media.Brush accent)
    {
        if (_closed) return;
        Target = target;
        Transport = target.Transport;
        AccentBrush = accent;
        CredentialInfo = $"Credentials from {credentialSource} · {target.Display}";
        DropRunspace();
        var gen = ++_generation;
        if (Services.Features.WhyNot(Services.Features.For(Tool, target.Agent is not null)) is { } off)
        {
            State = SessionState.Failed;
            StatusText = off;
            return;
        }
        State = SessionState.Connecting;
        StatusText = $"Connecting to {target.Display}…";
        Task.Run(() =>
        {
            try
            {
                var rs = PsRemoting.Open(target);
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                ps.AddScript("'PowerShell ' + $PSVersionTable.PSVersion.ToString(2) + ' on ' + $env:COMPUTERNAME");
                return (rs, ps.Invoke().FirstOrDefault()?.ToString() ?? "PowerShell remoting", (string?)null);
            }
            catch (Exception ex) { return ((Runspace?)null, "", PsRemoting.Explain(ex, target)); }
        }).ContinueWith(t =>
        {
            var (rs, info, error) = t.Result;
            if (_closed || gen != _generation) { if (rs is not null) PsSession.CloseQuietly(rs); return; }
            if (rs is null)
            {
                State = SessionState.Failed;
                StatusText = error!;
                OpenFailed?.Invoke(error!);
                return;
            }
            _rs = rs;
            rs.StateChanged += (_, e) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (rs != _rs || _closed || e.RunspaceStateInfo.State is not (RunspaceState.Broken or RunspaceState.Closed)) return;
                _rs = null;
                State = e.RunspaceStateInfo.State == RunspaceState.Broken ? SessionState.Failed : SessionState.Disconnected;
                StatusText = e.RunspaceStateInfo.Reason is { } r ? "Connection lost: " + PsRemoting.Explain(r, target) : "The session was closed.";
            });
            State = SessionState.Connected;
            StatusText = "Connected – " + info;
            Opened?.Invoke(info);
            Ready?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Runs one remote call at a time on the session's runspace.</summary>
    protected async Task<T> Run<T>(Func<Runspace, T> call)
    {
        var rs = _rs ?? throw new InvalidOperationException("Not connected.");
        await _lock.WaitAsync();
        try { return await Task.Run(() => call(rs)); }
        catch (Exception ex) when (ex is not RemoteCommandException && Target is not null) { throw new RemoteCommandException(PsRemoting.Explain(ex, Target)); }
        finally { _lock.Release(); }
    }

    public override void FocusSession() => FocusRequested?.Invoke();

    public override void Disconnect()
    {
        _generation++;
        if (DropRunspace() || State == SessionState.Connecting)
        {
            State = SessionState.Disconnected;
            StatusText = "Disconnected.";
        }
    }

    private bool DropRunspace()
    {
        var rs = _rs;
        _rs = null;
        if (rs is null) return false;
        PsSession.CloseQuietly(rs);
        return true;
    }

    public override void Close()
    {
        if (_closed) return;
        Dispose();
        RaiseClosed();
    }

    public override void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _generation++;
        OnDisposed();
        DropRunspace();
    }

    protected virtual void OnDisposed() { }
}
