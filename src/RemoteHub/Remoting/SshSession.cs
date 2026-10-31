using System.Text;
using System.Threading.Channels;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting.Shares;
using RemoteHub.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Remoting;

/// <summary>A shell on an SSH server: the client, its shell channel, the parsed private key and the jump host's local port.</summary>
public sealed class SshShell : IDisposable
{
    internal SshShell(SshClient client, ShellStream stream, PrivateKeyFile? key, LocalForward? forward)
    {
        Client = client;
        Stream = stream;
        _key = key;
        _forward = forward;
    }

    internal SshClient Client { get; }
    internal ShellStream Stream { get; }
    private readonly PrivateKeyFile? _key;
    private readonly LocalForward? _forward;

    public string ServerVersion => Software(Client.ConnectionInfo.ServerVersion);

    /// <summary>
    /// The server's software from its identification string: "OpenSSH_for_Windows_9.5" from
    /// "SSH-2.0-OpenSSH_for_Windows_9.5" – the protocol part says nothing to the user.
    /// </summary>
    internal static string Software(string? identification)
    {
        var v = identification ?? "";
        return v.StartsWith("SSH-2.0-", StringComparison.Ordinal) ? v["SSH-2.0-".Length..] : v;
    }

    /// <summary>
    /// Connects to <paramref name="entry"/> and opens a shell of <paramref name="columns"/> × <paramref name="rows"/>
    /// (blocking). The host key must be the accepted one or the user accepts it (<paramref name="trust"/>), as for SFTP;
    /// through <paramref name="via"/> it goes over a local port to the jump host.
    /// </summary>
    /// <exception cref="ShareException">Why it didn't connect, for people.</exception>
    public static SshShell Open(NetworkShare entry, CredentialSet credentials, Func<TrustRequest, bool> trust, Action<string> accepted, JumpRoute? via,
        int columns, int rows)
    {
        if (Services.Features.WhyNot(Services.Feature.Ssh) is { } off) throw new ShareException(off);
        if (credentials.IsEmpty) throw new ShareException("SSH needs an account – set one for the connection or its folder.");
        var host = entry.EffectiveHost;
        if (host.Length == 0) throw new ShareException("The connection has no host – edit it and enter one.");
        var port = entry.EffectivePort;
        var forward = via is null ? null : JumpForwarders.Open(via, host, port);
        PrivateKeyFile? key = null;
        try
        {
            ConnectionInfo info;
            try { (info, key) = SshSignIn.Connection(forward?.Host ?? host, forward?.Port ?? port, credentials, TimeSpan.FromSeconds(20)); }
            catch (SshKeyException ex) { throw new ShareException(ex.Message); }
            SshClient client;
            try
            {
                client = SshSignIn.ConnectTrusted(() => new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) },
                    host, entry.TrustedFingerprint, trust, accepted);
            }
            catch (SshAuthenticationException) { throw new ShareException(SshSignIn.Refused(credentials)); }
            try
            {
                var stream = client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 64 * 1024);
                return new SshShell(client, stream, key, forward);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            key?.Dispose();
            forward?.Dispose();
            // When the jump host refused, its answer is the error (SSH.NET only saw the connection closed).
            if (ex is not ShareException && forward?.TakeError() is { } why) throw new ShareException(why, ex);
            throw;
        }
    }

    public void Dispose()
    {
        try { Stream.Dispose(); } catch { /* channel already gone */ }
        try { Client.Disconnect(); } catch { /* connection already gone */ }
        Client.Dispose();
        _key?.Dispose();
        _forward?.Dispose();
    }
}

/// <summary>
/// Tab of an SSH connection (docs/network-shares.md §6): a terminal on a shell channel. What the server sends is read on
/// a thread of its own and fed to <see cref="Terminal"/> under its lock; keys go out in order through a queue. When the
/// shell ends ("exit") or the connection drops, the tab shows Connect and Close tab.
/// </summary>
public sealed class SshSession : SessionBase
{
    private SshShell? _shell;
    private Channel<byte[]>? _outgoing;
    private int _generation;
    private bool _closed;

    public SshSession(Connection server, NetworkShare entry) : base(server)
    {
        Entry = entry;
        Title = server.Name;
        Terminal.Reply += Send;
    }

    public NetworkShare Entry { get; }
    public VtTerminal Terminal { get; } = new();
    /// <summary>The account last used (with a password or passphrase entered for it – memory only), for reconnecting without asking again.</summary>
    public CredentialSet? Account { get; set; }
    public override string KindSymbol => Entry.DisplaySymbol;
    public override string KindLabel => Entry.Name;

    /// <summary>The screen changed (raised on the reading thread).</summary>
    public event Action? Output;
    public event Action? FocusRequested;

    public override void FocusSession() => FocusRequested?.Invoke();

    /// <summary>Connects in the background with <paramref name="open"/> (which knows the account and asks about the host key).</summary>
    public void Connect(Func<int, int, SshShell> open, string credentialSource, System.Windows.Media.Brush accent)
    {
        if (_closed) return;
        AccentBrush = accent;
        CredentialInfo = $"Account from {credentialSource} · {Entry.Address}";
        Drop();
        var gen = ++_generation;
        State = SessionState.Connecting;
        StatusText = $"Connecting to {Entry.Address}…";
        int columns, rows;
        lock (Terminal.SyncRoot) (columns, rows) = (Terminal.Columns, Terminal.Rows);
        Task.Run(() =>
        {
            try { return (open(columns, rows), (string?)null); }
            catch (Exception ex) { return ((SshShell?)null, ShareSession.Explain(ex)); }
        }).ContinueWith(t =>
        {
            var (shell, error) = t.Result;
            if (_closed || gen != _generation) { shell?.Dispose(); return; }
            if (shell is null)
            {
                State = SessionState.Failed;
                StatusText = error!;
                return;
            }
            _shell = shell;
            _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
            Transport = Connection.JumpHost is { } jump ? $"SSH · {shell.ServerVersion} · via {jump.Name}" : $"SSH · {shell.ServerVersion}";
            State = SessionState.Connected;
            StatusText = "Connected – " + Transport;
            Services.DiagLog.Write("SSH: connected");
            // The size may have changed while connecting.
            lock (Terminal.SyncRoot) if ((Terminal.Columns, Terminal.Rows) != (columns, rows)) Resize(Terminal.Columns, Terminal.Rows);
            StartReading(shell, gen);
            _ = WriteLoop(shell, _outgoing.Reader);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StartReading(SshShell shell, int gen)
    {
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        new System.Threading.Thread(() =>
        {
            var buffer = new byte[32 * 1024];
            string? why = null;
            try
            {
                while (true)
                {
                    var n = shell.Stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    lock (Terminal.SyncRoot) Terminal.Feed(buffer.AsSpan(0, n));
                    Output?.Invoke();
                }
            }
            catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                why = ShareSession.Explain(ex);
            }
            ui.BeginInvoke(() => Ended(gen, why));
        }) { IsBackground = true, Name = "SSH reader" }.Start();
    }

    /// <summary>The shell ended (exit, or the connection dropped): the tab offers Connect again, the screen is kept.</summary>
    private void Ended(int gen, string? why)
    {
        if (_closed || gen != _generation) return;
        Drop();
        State = SessionState.Disconnected;
        StatusText = why is null ? "The session ended." : "The connection was lost: " + why;
        Services.DiagLog.Write(why is null ? "SSH: session ended" : "SSH: connection lost");
    }

    private static async Task WriteLoop(SshShell shell, ChannelReader<byte[]> reader)
    {
        try
        {
            await foreach (var data in reader.ReadAllAsync())
            {
                shell.Stream.Write(data, 0, data.Length);
                // Everything queued so far goes out together.
                while (reader.TryRead(out var more)) shell.Stream.Write(more, 0, more.Length);
                shell.Stream.Flush();
            }
        }
        catch (Exception ex) when (ex is SshException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The reader sees the connection go and ends the tab.
        }
    }

    /// <summary>Keys, a paste or the terminal's own answers – sent in order. Dropped while not connected.</summary>
    public void Send(string text)
    {
        if (text.Length == 0) return;
        _outgoing?.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The tab's size in character cells changed: the screen and the server's idea of it follow.</summary>
    public void Resize(int columns, int rows)
    {
        lock (Terminal.SyncRoot)
        {
            Terminal.Resize(columns, rows);
            (columns, rows) = (Terminal.Columns, Terminal.Rows); // as clamped
        }
        if (_shell is { } shell)
            try { shell.Stream.ChangeWindowSize((uint)columns, (uint)rows, 0, 0); }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException) { /* the reader ends the tab */ }
    }

    private void Drop()
    {
        _outgoing?.Writer.TryComplete();
        _outgoing = null;
        var shell = _shell;
        _shell = null;
        // Disconnecting can wait for the server; not on the UI thread.
        if (shell is not null) Task.Run(shell.Dispose);
    }

    public override void Disconnect()
    {
        _generation++;
        if (State is SessionState.Connected or SessionState.Connecting)
        {
            Drop();
            State = SessionState.Disconnected;
            StatusText = "Disconnected.";
        }
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
        Drop();
        Services.DiagLog.Write("SSH: closed");
    }
}
