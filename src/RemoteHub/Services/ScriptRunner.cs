using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using RemoteHub.Data;
using RemoteHub.Remoting;

namespace RemoteHub.Services;

/// <summary>How a run ended (docs/scripts-view-design.md §6.2).</summary>
public enum ScriptRunState { Running, Succeeded, SucceededRestart, Failed, TimedOut, Cancelled, ConnectionLost, Refused }

/// <summary>A user signed in on the device, for <c>User</c> runs (§6.1).</summary>
public sealed record DeviceUser(string User, int SessionId, bool IsConsole);

/// <summary>The device, as the run window shows it before a run.</summary>
public sealed record DeviceInfo(bool? IsServer, string Caption, bool IsAdmin, IReadOnlyList<DeviceUser> Users);

/// <summary>
/// One run of one script in one context on one device. <see cref="Context"/> is System, User or AsMe – Both is two runs.
/// <see cref="Version"/> null = a draft under test (§6.4). <see cref="Parameters"/> go to the script by name (§11, phase 4).
/// </summary>
public sealed record ScriptRunRequest(PsTarget Target, string NodeId, string ScriptId, int? Version, ScriptContent Content, string Context,
    string? RunUser = null, IReadOnlyDictionary<string, object?>? Parameters = null);

public sealed record ScriptRunResult(ScriptRunState State, int? ExitCode, string Message, bool OutputCut, TimeSpan Duration);

/// <summary>
/// Runs a script on a device through the PowerShell session RemoteHub already opens for it – WinRM or the agent – with
/// one implementation for both (§6.2): <c>Assets/Scripts/RunScript.ps1</c> is loaded into the remote session, and its
/// functions start a scheduled task (System, User) or a child process (As me), report the output as it comes, and remove
/// everything on every path. Nothing here logs the script's name, text or output.
/// </summary>
public sealed class ScriptRunner
{
    /// <summary>Output kept and shown per result (§6.2); RemoteHub stops reading after it.</summary>
    public const int MaxOutputChars = 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ScriptRunRequest _request;
    private readonly StringBuilder _output = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private bool _cut;

    /// <summary>New output, on the run's thread (the UI marshals it to the dispatcher).</summary>
    public event Action<string>? OutputReceived;

    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string Output { get { lock (_output) return _output.ToString(); } }
    public DateTime StartedUtc { get; private set; }

    private readonly Func<Runspace>? _open;

    /// <param name="open">Opens the session to run in; tests pass a local one. Default: the connection's (WinRM or agent).</param>
    public ScriptRunner(ScriptRunRequest request, Func<Runspace>? open = null)
    {
        _request = request;
        _open = open;
    }

    /// <summary>The device-side functions (RunScript.ps1), embedded in the app.</summary>
    internal static string DeviceScript
    {
        get
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Scripts/RunScript.ps1")
                          ?? throw new InvalidOperationException("RunScript.ps1 is missing from the app.");
            using var r = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return r.ReadToEnd();
        }
    }

    /// <summary>Asks for PowerShell 7 with <c>#Requires -PSEdition Core</c> (§6.2).</summary>
    public static bool WantsPowerShell7(string body) =>
        System.Text.RegularExpressions.Regex.IsMatch(body, @"^\s*#Requires\s+-PSEdition\s+Core\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// What the run window shows before a run (blocking – call from a background thread): whether the device runs a
    /// server Windows (null when unknown), its name, whether the account is an administrator there, and who is signed in.
    /// </summary>
    public static DeviceInfo Info(PsTarget target, Func<Runspace>? open = null)
    {
        using var rs = open?.Invoke() ?? PsRemoting.Open(target);
        Load(rs);
        var o = Invoke(rs, "RemoteHubRun-Info", []).FirstOrDefault() ?? throw new InvalidOperationException("The device didn't answer.");
        var users = new List<DeviceUser>();
        if (o.Properties["Users"]?.Value is { } raw)
            foreach (var u in raw is PSObject { BaseObject: System.Collections.IEnumerable list } ? list.Cast<object>() : raw is System.Collections.IEnumerable e ? e.Cast<object>() : [raw])
                if (u is PSObject p && Prop<string>(p, "User") is { Length: > 0 } name)
                    users.Add(new DeviceUser(name, Prop<int>(p, "SessionId"), Prop<bool>(p, "IsConsole")));
        var type = Prop<int>(o, "ProductType");
        return new DeviceInfo(type == 0 ? null : type != 1, Prop<string>(o, "Caption") ?? "", Prop<bool>(o, "IsAdmin"), users);
    }

    /// <summary>Runs the script and waits for it, the timeout or <paramref name="cancel"/>. Never throws for the device's reasons.</summary>
    public Task<ScriptRunResult> RunAsync(CancellationToken cancel) => Task.Run(() => Run(cancel), CancellationToken.None);

    private ScriptRunResult Run(CancellationToken cancel)
    {
        StartedUtc = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_request.Content.TimeoutSec ?? ScriptStore.DefaultTimeoutSec, ScriptStore.MinTimeoutSec, ScriptStore.MaxTimeoutSec));
        Runspace? rs = null;
        string? folder = null, task = null;
        var pid = 0;
        try
        {
            try
            {
                rs = _open?.Invoke() ?? PsRemoting.Open(_request.Target);
                Load(rs);
            }
            catch (Exception ex) { return End(ScriptRunState.Failed, null, PsRemoting.Explain(ex, _request.Target)); }
            if (cancel.IsCancellationRequested) return End(ScriptRunState.Cancelled, null, "Cancelled.");

            PSObject started;
            try { started = Invoke(rs, "RemoteHubRun-Start", StartParameters()).FirstOrDefault() ?? throw new InvalidOperationException("The device didn't start the run."); }
            catch (Exception ex) when (Refusal(ex) is { } why) { return End(ScriptRunState.Refused, null, why); }
            catch (Exception ex) when (IsLost(ex, rs)) { return End(ScriptRunState.ConnectionLost, null, LostMessage); }
            catch (Exception ex) { return End(ScriptRunState.Failed, null, Short(Unwrap(ex).Message)); }
            folder = Prop<string>(started, "Folder");
            task = Prop<string>(started, "Task");
            pid = Prop<int>(started, "ProcessId");

            long offset = 0;
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (cancel.WaitHandle.WaitOne(PollInterval))
                {
                    Drain(rs, folder, task, pid, ref offset);
                    return End(ScriptRunState.Cancelled, null, "Cancelled – the script was stopped.");
                }
                PSObject? poll;
                try { poll = Invoke(rs, "RemoteHubRun-Poll", new() { ["Folder"] = folder, ["Task"] = task, ["ProcessId"] = pid, ["Offset"] = offset }).FirstOrDefault(); }
                catch (Exception ex) when (IsLost(ex, rs)) { folder = task = null; pid = 0; return End(ScriptRunState.ConnectionLost, null, LostMessage); }
                if (poll is null) continue;
                Append(Prop<string>(poll, "Chunk"));
                offset = Prop<long>(poll, "Offset");
                if (Prop<bool>(poll, "Done"))
                {
                    Drain(rs, folder, task, pid, ref offset);
                    var exit = poll.Properties["ExitCode"]?.Value is { } e && int.TryParse(e.ToString(), out var code) ? code : (int?)null;
                    return Prop<string>(poll, "State") switch
                    {
                        "Ended" => exit switch
                        {
                            0 => End(ScriptRunState.Succeeded, 0, "Succeeded."),
                            3010 => End(ScriptRunState.SucceededRestart, 3010, "Succeeded – restart needed."),
                            _ => End(ScriptRunState.Failed, exit, $"Failed (exit code {exit})."),
                        },
                        _ => End(ScriptRunState.Failed, exit, exit is { } x && x != 0
                            ? $"The script ended without a result (0x{x:X8})." : "The script ended without a result."),
                    };
                }
                if (DateTime.UtcNow > deadline)
                {
                    Drain(rs, folder, task, pid, ref offset);
                    return End(ScriptRunState.TimedOut, null, $"Timed out after {Minutes(timeout)} – the script was stopped.");
                }
            }
        }
        finally
        {
            // On every path: the task or process stops, the task and the folder go (§6.3). Without a connection the next
            // run on the device cleans up.
            if (rs is { RunspaceStateInfo.State: RunspaceState.Opened } && (folder is not null || task is not null || pid > 0))
                try { Invoke(rs, "RemoteHubRun-Finish", new() { ["Folder"] = folder, ["Task"] = task, ["ProcessId"] = pid }); }
                catch (Exception ex) { DiagLog.Write("Scripts: cleanup on the device failed: " + ex.GetType().Name); }
            try { rs?.Dispose(); } catch { /* closing a broken session */ }
        }
    }

    private Dictionary<string, object?> StartParameters()
    {
        var p = new Dictionary<string, object?>
        {
            ["RunId"] = RunId,
            ["Context"] = _request.Context,
            ["ScriptText"] = _request.Content.Body,
            ["TimeoutSec"] = _request.Content.TimeoutSec ?? ScriptStore.DefaultTimeoutSec,
        };
        if (_request.Context == ScriptStore.ContextUser) p["RunUser"] = _request.RunUser;
        if (_request.Parameters is { Count: > 0 } args) p["ParamsJson"] = JsonSerializer.Serialize(args);
        if (WantsPowerShell7(_request.Content.Body)) p["PowerShell7"] = true;
        return p;
    }

    /// <summary>Reads what is left of the output after the run ended or was stopped.</summary>
    private void Drain(Runspace rs, string? folder, string? task, int pid, ref long offset)
    {
        if (folder is null) return;
        try
        {
            Invoke(rs, "RemoteHubRun-Stop", new() { ["Task"] = task, ["ProcessId"] = pid });
            for (var i = 0; i < 40 && !_cut; i++) // at most 40 × 64 KB, past the limit anyway
            {
                var poll = Invoke(rs, "RemoteHubRun-Poll", new() { ["Folder"] = folder, ["Task"] = task, ["ProcessId"] = pid, ["Offset"] = offset }).FirstOrDefault();
                var chunk = poll is null ? null : Prop<string>(poll, "Chunk");
                if (string.IsNullOrEmpty(chunk)) break;
                Append(chunk);
                offset = Prop<long>(poll!, "Offset");
            }
        }
        catch (Exception ex) { DiagLog.Write("Scripts: reading the rest of the output failed: " + ex.GetType().Name); }
    }

    private void Append(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || _cut) return;
        var bytes = Convert.FromBase64String(base64);
        var chars = new char[_decoder.GetCharCount(bytes, 0, bytes.Length)];
        _decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        var text = new string(chars);
        lock (_output)
        {
            var room = MaxOutputChars - _output.Length;
            if (text.Length > room)
            {
                text = text[..Math.Max(0, room)] + Environment.NewLine + "Output cut off after 1 MB." + Environment.NewLine;
                _cut = true;
            }
            _output.Append(text);
        }
        OutputReceived?.Invoke(text);
    }

    private ScriptRunResult End(ScriptRunState state, int? exit, string message) => new(state, exit, message, _cut, DateTime.UtcNow - StartedUtc);

    private static void Load(Runspace rs)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        // In the session's global scope, so the functions stay for the calls that follow.
        ps.AddScript(DeviceScript, useLocalScope: false);
        ps.Invoke();
        if (ps.HadErrors && ps.Streams.Error.FirstOrDefault() is { } e) throw e.Exception ?? new InvalidOperationException("RunScript.ps1 failed to load.");
    }

    private static Collection<PSObject> Invoke(Runspace rs, string command, Dictionary<string, object?> args)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddCommand(command);
        foreach (var (k, v) in args)
        {
            if (v is null) continue;
            if (v is true && command == "RemoteHubRun-Start" && k == "PowerShell7") ps.AddParameter(k);
            else ps.AddParameter(k, v);
        }
        var result = ps.Invoke();
        if (ps.HadErrors && ps.Streams.Error.FirstOrDefault() is { } e) throw e.Exception ?? new InvalidOperationException(e.ToString());
        return result;
    }

    private const string LostMessage = "Connection lost – the script may still be running on the device.";

    /// <summary>The device refused the run for a reason RemoteHub explains (RunScript.ps1 throws "CODE: details").</summary>
    internal static string? Refusal(Exception ex)
    {
        var msg = Unwrap(ex).Message;
        if (msg.StartsWith("NOT_ADMIN:", StringComparison.Ordinal)) return "Running as System or User needs an administrator account on the device.";
        if (msg.StartsWith("GPO_POLICY:", StringComparison.Ordinal)) return "The device's group policy only allows signed scripts.";
        if (msg.StartsWith("NO_USER:", StringComparison.Ordinal)) return "No user is signed in on the device.";
        if (msg.StartsWith("NO_PWSH:", StringComparison.Ordinal)) return "PowerShell 7 isn't installed on the device.";
        return null;
    }

    private static bool IsLost(Exception ex, Runspace? rs) =>
        rs?.RunspaceStateInfo.State is RunspaceState.Broken or RunspaceState.Closed || Unwrap(ex) is System.Management.Automation.Remoting.PSRemotingTransportException;

    private static Exception Unwrap(Exception ex)
    {
        while (ex is RuntimeException { InnerException: { } inner } && ex.GetType() == typeof(RuntimeException)) ex = inner;
        if (ex is CmdletInvocationException { InnerException: { } i }) ex = i;
        return ex;
    }

    private static string Short(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    private static T? Prop<T>(PSObject o, string name)
    {
        var v = o.Properties[name]?.Value;
        if (v is PSObject p) v = p.BaseObject;
        if (v is null) return default;
        if (v is T t) return t;
        try { return (T)Convert.ChangeType(v, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        catch { return default; }
    }

    /// <summary>The run's state as stored in the run history and the audit log.</summary>
    public static string StateName(ScriptRunState s) => s.ToString();
}
