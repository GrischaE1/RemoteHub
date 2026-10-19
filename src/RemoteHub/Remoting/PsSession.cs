using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteHub.Models;
using RemoteHub.Rdp;
using SessionState = RemoteHub.Rdp.SessionState;

namespace RemoteHub.Remoting;

/// <summary>Console output. <paramref name="IsCommand"/>: an echoed command line, shown with syntax colours.</summary>
/// <summary>One completion candidate (plain values, independent of the PowerShell version on either side).</summary>
public sealed record CompletionMatch(string CompletionText, string ListItemText, string ResultType, string ToolTip);

/// <summary>Completion result: replace <see cref="ReplacementLength"/> characters at <see cref="ReplacementIndex"/>.</summary>
public sealed record CompletionSet(int ReplacementIndex, int ReplacementLength, List<CompletionMatch> Matches)
{
    /// <summary>Reads the result of TabExpansion2 – a live CommandCompletion (local) or its deserialized form (remote, any version).</summary>
    public static CompletionSet? From(PSObject? r)
    {
        if (r is null) return null;
        int Int(string name) => r.Properties[name]?.Value is { } v ? Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var raw = r.Properties["CompletionMatches"]?.Value;
        if (raw is PSObject p) raw = p.BaseObject;
        var matches = new List<CompletionMatch>();
        if (raw is System.Collections.IEnumerable list)
            foreach (var item in list)
            {
                var m = PSObject.AsPSObject(item);
                string S(string name) => m.Properties[name]?.Value?.ToString() ?? "";
                var text = S("CompletionText");
                if (text.Length == 0) continue;
                var shown = S("ListItemText");
                matches.Add(new CompletionMatch(text, shown.Length > 0 ? shown : text, S("ResultType"), S("ToolTip")));
            }
        return matches.Count == 0 ? null : new CompletionSet(Int("ReplacementIndex"), Int("ReplacementLength"), matches);
    }
}

public readonly record struct OutputChunk(string Text, ConsoleColor? Color, bool IsCommand = false);

/// <summary>An interactive PowerShell remoting session shown as a tab (one remote runspace).</summary>
public sealed partial class PsSession : SessionBase, IConsoleSink
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly ConcurrentQueue<OutputChunk> _pending = new();
    private readonly DispatcherTimer _flush;
    private readonly PsConsoleHost _host;
    private readonly List<string> _history = new();
    private Runspace? _rs;
    private PowerShell? _running;
    private TaskCompletionSource<string?>? _readLine;
    private bool _closed, _completing;
    private int _connectGeneration;

    public PsSession(Connection connection) : base(connection)
    {
        _host = new PsConsoleHost(this);
        _flush = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
        Title = connection.Name;
    }

    public override string KindSymbol => "WindowConsole20";
    public override string KindLabel => "PowerShell";

    /// <summary>Host, protocol and credentials of the session (password in memory only) – reused for uploads.</summary>
    public PsTarget? Target { get; private set; }

    [ObservableProperty] private string _promptText = "PS>";
    [ObservableProperty] private string? _location;
    [ObservableProperty] private bool _isBusy;
    /// <summary>The remote command waits for a line of input (Read-Host, confirmation).</summary>
    [ObservableProperty] private bool _isReadingInput;
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private int _progressPercent = -1;

    /// <summary>Characters per line used for formatting the output (set by the view).</summary>
    public int Width { get; set; } = 120;

    /// <summary>Batched output, raised on the UI thread.</summary>
    public event Action<IReadOnlyList<OutputChunk>>? Output;
    public event Action? ClearRequested;
    public event Action? FocusRequested;
    /// <summary>The session opened (argument: "PowerShell 5.1 on SRV01").</summary>
    public event Action<string>? Opened;
    /// <summary>Opening the session failed (argument: explanation).</summary>
    public event Action<string>? OpenFailed;

    public IReadOnlyList<string> History => _history;

    // ---------------------------------------------------------------- connect

    public void Connect(PsTarget target, string credentialSource, System.Windows.Media.Brush accent)
    {
        if (_closed) return;
        Target = target;
        Transport = target.Transport;
        AccentBrush = accent;
        Title = Connection.Name;
        CredentialInfo = $"Credentials from {credentialSource} · {target.Display}";
        DropRunspace();
        var gen = ++_connectGeneration;
        if (Services.Features.WhyNot(Services.Features.For(Services.RemoteTool.PowerShell, target.Agent is not null)) is { } off)
        {
            State = SessionState.Failed;
            StatusText = off;
            Write(off + "\n", ConsoleColor.Yellow);
            return;
        }
        State = SessionState.Connecting;
        StatusText = $"Connecting to {target.Display}…";
        Write($"Connecting to {target.Display}…\n", ConsoleColor.DarkGray);

        Task.Run(() =>
        {
            try
            {
                var rs = PsRemoting.Open(target, _host);
                string info;
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = rs;
                    ps.AddScript("'PowerShell ' + $PSVersionTable.PSVersion.ToString(2) + ' on ' + $env:COMPUTERNAME");
                    info = ps.Invoke().FirstOrDefault()?.ToString() ?? "PowerShell remoting";
                }
                return (rs, info, (string?)null);
            }
            catch (Exception ex) { return ((Runspace?)null, "", PsRemoting.Explain(ex, target)); }
        }).ContinueWith(t =>
        {
            var (rs, info, error) = t.Result;
            if (_closed || gen != _connectGeneration) { if (rs is not null) CloseQuietly(rs); return; }
            if (rs is null)
            {
                State = SessionState.Failed;
                StatusText = error!;
                Write(error + "\n", ConsoleColor.Red);
                OpenFailed?.Invoke(error!);
                return;
            }
            _rs = rs;
            rs.StateChanged += (_, e) => _ui.BeginInvoke(() => OnRunspaceState(rs, e.RunspaceStateInfo));
            State = SessionState.Connected;
            StatusText = "Connected – " + info;
            Write(info + "  ·  Tab completes, ↑/↓ history, Ctrl+C stops, drop files here to upload them to the current folder.\n", ConsoleColor.DarkGray);
            Opened?.Invoke(info);
            RefreshLocation();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnRunspaceState(Runspace rs, RunspaceStateInfo info)
    {
        if (rs != _rs || _closed) return;
        if (info.State is RunspaceState.Broken or RunspaceState.Closed)
        {
            _rs = null;
            CancelRead();
            IsBusy = false;
            var why = info.Reason is null ? "The session was closed." : "Connection lost: " + PsRemoting.Explain(info.Reason, Target!);
            State = info.State == RunspaceState.Broken ? SessionState.Failed : SessionState.Disconnected;
            StatusText = why;
            Write(why + "\n", ConsoleColor.Red);
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Runs a line typed by the user (or answers a pending Read-Host).</summary>
    public void Execute(string line)
    {
        if (_readLine is { } pending)
        {
            Write(line + "\n", null);
            _readLine = null;
            IsReadingInput = false;
            pending.TrySetResult(line);
            return;
        }
        if (IsBusy || _rs is null) return;

        Write(PromptText + " ", ConsoleColor.DarkGray);
        _pending.Enqueue(new OutputChunk(line + "\n", null, IsCommand: true));
        var cmd = line.Trim();
        if (cmd.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != line) _history.Add(line);
        if (_history.Count > 500) _history.RemoveAt(0);

        if (cmd.Equals("cls", StringComparison.OrdinalIgnoreCase) || cmd.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            cmd.Equals("Clear-Host", StringComparison.OrdinalIgnoreCase))
        {
            ClearRequested?.Invoke();
            return;
        }
        if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase)) { Close(); return; }

        var rs = _rs;
        var width = Math.Clamp(Width, 40, 1000);
        IsBusy = true;
        Task.Run(() =>
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            _running = ps;
            ps.AddScript(line).AddCommand("Out-String").AddParameter("Stream").AddParameter("Width", width);
            var output = new PSDataCollection<PSObject>();
            output.DataAdded += (_, e) => Write(output[e.Index]?.ToString() + "\n", null);
            ps.Streams.Error.DataAdded += (_, e) => WriteError(ps.Streams.Error[e.Index]);
            try { ps.Invoke(null, output); }
            catch (PipelineStoppedException) { Write("Stopped.\n", ConsoleColor.DarkGray); }
            catch (RuntimeException ex) when (ex is not PSRemotingTransportException) { WriteError(ex.ErrorRecord); }
            catch (Exception ex) { Write(ex.Message + "\n", ConsoleColor.Red); }
            finally { _running = null; }
        }).ContinueWith(_ =>
        {
            IsBusy = false;
            ProgressText = null;
            ProgressPercent = -1;
            RefreshLocation();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Ctrl+C: stops the running command (or a pending Read-Host).</summary>
    public void Stop()
    {
        CancelRead();
        try { _running?.BeginStop(null, null); } catch { /* already finished */ }
    }

    private void CancelRead()
    {
        var r = _readLine;
        _readLine = null;
        IsReadingInput = false;
        r?.TrySetResult(null);
    }

    private void RefreshLocation()
    {
        var rs = _rs;
        if (rs is null) return;
        Task.Run(() =>
        {
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                ps.AddScript("(Get-Location).ProviderPath");
                return ps.Invoke().FirstOrDefault()?.ToString();
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result is { Length: > 0 } loc) Location = loc;
            PromptText = Location is null ? "PS>" : $"PS {Location}>";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Completion by the remote computer's own TabExpansion2 (Windows PowerShell 3+ and PowerShell 7). The result is
    /// read field by field, so it works across versions. Null when nothing matches or the session is busy.
    /// </summary>
    public async Task<CompletionSet?> CompleteAsync(string input, int cursor)
    {
        var rs = _rs;
        if (rs is null || IsBusy || _completing || _readLine is not null) return null;
        _completing = true;
        try
        {
            return await Task.Run(() => CompleteOn(rs, input, cursor));
        }
        finally { _completing = false; }
    }

    public static CompletionSet? CompleteOn(Runspace rs, string input, int cursor)
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddCommand("TabExpansion2").AddParameter("inputScript", input).AddParameter("cursorColumn", cursor);
            return CompletionSet.From(ps.Invoke().FirstOrDefault());
        }
        catch (Exception ex)
        {
            Services.DiagLog.Write($"PowerShell completion failed: {ex.GetType().Name}");
            return null;
        }
    }

    // ---------------------------------------------------------------- IConsoleSink (background threads)

    public void Write(string text, ConsoleColor? color)
    {
        if (text.Length > 0) _pending.Enqueue(new OutputChunk(text, color));
    }

    private void WriteError(ErrorRecord e)
    {
        var text = e.ToString();
        if (e.InvocationInfo?.PositionMessage is { Length: > 0 } pos && e.Exception is ParseException) text += "\n" + pos;
        Write(text.TrimEnd() + "\n", ConsoleColor.Red);
    }

    public string? ReadLine()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Invoke(() => { _readLine = tcs; IsReadingInput = true; FocusRequested?.Invoke(); });
        return tcs.Task.GetAwaiter().GetResult();
    }

    public string? ReadSecret(string caption, string message) => _ui.Invoke(() =>
    {
        var owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                    ?? System.Windows.Application.Current.MainWindow;
        var dlg = new Views.PasswordDialog(confirm: false, title: string.IsNullOrWhiteSpace(caption) ? "PowerShell" : caption,
            message: $"{Connection.Name}: {message}", okText: "OK") { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    });

    public void Progress(ProgressRecord r) => _ui.BeginInvoke(() =>
    {
        if (r.RecordType == ProgressRecordType.Completed) { ProgressText = null; ProgressPercent = -1; return; }
        ProgressText = string.IsNullOrEmpty(r.StatusDescription) ? r.Activity : $"{r.Activity} – {r.StatusDescription}";
        ProgressPercent = r.PercentComplete;
    });

    private void Flush()
    {
        if (_pending.IsEmpty) return;
        var batch = new List<OutputChunk>();
        while (batch.Count < 5000 && _pending.TryDequeue(out var c)) batch.Add(c);
        Output?.Invoke(batch);
    }

    // ---------------------------------------------------------------- lifetime

    public override void FocusSession() => FocusRequested?.Invoke();

    public override void Disconnect()
    {
        _connectGeneration++;
        Stop();
        if (DropRunspace() || State == SessionState.Connecting)
        {
            State = SessionState.Disconnected;
            StatusText = "Disconnected.";
            Write("Disconnected.\n", ConsoleColor.DarkGray);
        }
    }

    /// <summary>Closes the remote runspace in the background. Returns true if one was open.</summary>
    private bool DropRunspace()
    {
        var rs = _rs;
        _rs = null;
        IsBusy = false;
        if (rs is null) return false;
        CloseQuietly(rs);
        return true;
    }

    /// <summary>Remote runspaces that are still being closed (awaited briefly on exit).</summary>
    internal static readonly ConcurrentDictionary<Task, byte> Closing = new();

    internal static void CloseQuietly(Runspace rs)
    {
        var t = Task.Run(() => { try { rs.Dispose(); } catch { /* broken connection */ } });
        Closing[t] = 0;
        t.ContinueWith(x => Closing.TryRemove(x, out _));
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
        _connectGeneration++;
        Stop();
        _flush.Stop();
        DropRunspace();
    }
}
