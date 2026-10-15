using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RemoteHub.Models;
using RemoteHub.Remoting;

/// <summary>
/// RemoteHub's PowerShell connection and remote tools against a lab VM, over the RemoteHub Agent or over WinRM HTTPS -
/// the same code the tabs use (PsRemoting.Open, RemoteFs, FileUploader/FileDownloader, RemoteRegistry, RemoteServices,
/// RemoteProcesses, RemotingSetup). One operation per call; the lab checks the result on the device through
/// PowerShell Direct, a channel RemoteHub doesn't use.
///
/// Connection (every op except script/trust):
///   --via agent|winrm --host .. --user .. --password .. [--local 1] [--pin .. --port n]
///       agent: pinned TLS to the agent (default port 58732); winrm: HTTPS 5986. --local 1 = ".\user" (local account).
/// Ops:
///   test                                     PsRemoting.TestAsync (what "Test PowerShell remoting" shows)
///   ps --script ..                           output and errors of a script
///   ps-host --script .. [--lines a|b] [--secrets a|b]   the script under the PowerShell tab's host (PsConsoleHost): prompts answered in order
///   ps-big --in-mb n [--out-mb n]            an n MB argument there, n MB of output back, both checked
///   complete --line .. [--cursor n]          TabExpansion2 in the remote session (what Tab shows)
///   fs-list --path ..                        RemoteFs.ListRemote
///   fs-upload --files a|b --remote .. [--overwrite 1] [--cancel-after-mb n]   FileUploader.UploadAsync
///   fs-download --remote a|b --local ..      RemoteFs.ExpandRemote + FileDownloader.DownloadAsync
///   fs-mkdir --path .. | fs-rename --path .. --name .. | fs-delete --paths a|b
///   reg-list --path .. | reg-set --key .. --name .. --kind .. --data .. [--hex 1] | reg-rename-value --key .. --name .. --new ..
///   reg-delete-value --key .. --name .. | reg-create-key --parent .. --name .. | reg-rename-key --key .. --new ..
///   reg-delete-key --key .. | reg-export --key .. --out <local file>
///   svc-list | svc-control --name .. --action Start|Stop|Restart | svc-startup --name .. --startup Automatic|AutomaticDelayed|Manual|Disabled
///   proc-list | proc-stop --ids 1|2 --names a|b [--tree 1]
///   script --host .. --allow a|b --out <file> [--https 0|1] [--local-admins 0|1]   RemotingSetup.BuildScript
///   fetch --host .. [--port 5986]            FetchCertificateAsync: the certificate the device presents
///   trust --host .. [--port 5986]            FetchCertificateAsync + TrustOnThisPc (CurrentUser\Root - asks the user, so not in the lab account)
/// --cert <thumbprint> (winrm): pin the session to that certificate instead of trusting it on this PC.
/// A refused action answers { refused: "<RemoteHub's message>" } instead of failing the call.
/// </summary>
internal static class LabTools
{
    public static async Task<object> Run(Dictionary<string, string> o)
    {
        var op = o["op"];
        switch (op)
        {
            case "script":
                var script = RemotingSetup.BuildScript("lab", o["host"], o["allow"].Split('|'), o.GetValueOrDefault("https") != "0",
                    DateTime.Now, o.GetValueOrDefault("local-admins") != "0");
                File.WriteAllText(o["out"], script);
                return new { file = o["out"], lines = script.Split('\n').Length };
            case "fetch":
            {
                // the certificate "Test again" shows the thumbprint of, without trusting it
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cert = await RemotingSetup.FetchCertificateAsync(o["host"], int.Parse(o.GetValueOrDefault("port") ?? "5986"), cts.Token, LabJump.Route(o));
                return new { thumbprint = cert.Thumbprint, subject = cert.Subject, notAfter = cert.NotAfter };
            }
            case "trust":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var cert = await RemotingSetup.FetchCertificateAsync(o["host"], int.Parse(o.GetValueOrDefault("port") ?? "5986"), cts.Token, LabJump.Route(o));
                RemotingSetup.TrustOnThisPc(cert, o["host"]);
                return new { thumbprint = cert.Thumbprint, subject = cert.Subject, notAfter = cert.NotAfter };
            }
        }

        var target = Target(o);
        if (target.Agent is null && o.TryGetValue("cert", out var expectedCert))
        {
            // The lab account can't answer the Windows prompt TrustOnThisPc raises, so instead of trusting the
            // certificate the session is pinned: the certificate the device presents (fetched the way RemoteHub fetches
            // it) must be exactly the one PowerShell Direct read from the listener; only then is the CA check skipped.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var presented = await RemotingSetup.FetchCertificateAsync(target.Host, target.Port, cts.Token, target.Via);
            if (!string.Equals(presented.Thumbprint, expectedCert, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"WinRM certificate mismatch: the device presents {presented.Thumbprint}, expected {expectedCert}.");
            _pinned = presented.Thumbprint;
        }
        if (op == "test")
        {
            if (_pinned is null)
            {
                var status = await PsRemoting.TestAsync(target);
                return new { state = status.State.ToString(), status.Message, transport = target.Transport };
            }
            // TestAsync's own steps, on the pinned connection
            try
            {
                using var rs = Open(target);
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                ps.AddScript("'PowerShell ' + $PSVersionTable.PSVersion.ToString(2) + ' on ' + $env:COMPUTERNAME");
                return new { state = "Available", message = ps.Invoke().FirstOrDefault()?.ToString(), transport = target.Transport };
            }
            catch (Exception ex) { return new { state = "Unavailable", message = PsRemoting.Explain(ex, target), transport = target.Transport }; }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object result = op switch
            {
                "ps" => Ps(target, o),
                "ps-host" => PsHost(target, o),
                "ps-big" => PsBig(target, o),
                "complete" => Complete(target, o),
                "fs-upload" => await Upload(target, o),
                "fs-download" => await Download(target, o),
                _ => WithRunspace(target, rs => Simple(rs, op, o)),
            };
            return new { op, transport = target.Transport, seconds = Math.Round(sw.Elapsed.TotalSeconds, 2), result };
        }
        catch (RemoteCommandException ex)
        {
            return new { op, transport = target.Transport, refused = ex.Message };
        }
        catch (RuntimeException ex) when (ex.Message.StartsWith("Refusing", StringComparison.Ordinal))
        {
            return new { op, transport = target.Transport, refused = ex.Message };
        }
    }

    public static PsTarget Target(Dictionary<string, string> o)
    {
        var cred = new CredentialSet
        {
            Username = o["user"],
            Password = o["password"],
            Domain = o.GetValueOrDefault("local") == "0" ? null : CredentialSet.LocalComputerDomain,
        };
        return o["via"] switch
        {
            "agent" => new PsTarget(o["host"], true, cred, LabJump.Endpoint(o, int.Parse(o.GetValueOrDefault("port") ?? "58732"), o["pin"])),
            "winrm" => new PsTarget(o["host"], true, cred, Via: LabJump.Route(o)),
            var v => throw new ArgumentException("--via agent|winrm, not " + v),
        };
    }

    private static string? _pinned;

    /// <summary>The WinRM runspace "install --via winrm" installs over (RemoteHub's way when the agent isn't there yet),
    /// pinned to --cert like every WinRM op here, through the jump host when one is given.</summary>
    internal static async Task<Runspace> OpenWinRm(Dictionary<string, string> o)
    {
        var target = Target(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var presented = await RemotingSetup.FetchCertificateAsync(target.Host, target.Port, cts.Token, target.Via);
        if (!string.Equals(presented.Thumbprint, o["cert"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"WinRM certificate mismatch: the device presents {presented.Thumbprint}, expected {o["cert"]}.");
        _pinned = presented.Thumbprint;
        return Open(target);
    }

    // PsRemoting.Open, or - for a pinned WinRM session - the same connection info with the CA/CN checks skipped; with
    // the PowerShell tab's host for ps-host
    private static Runspace Open(PsTarget t, System.Management.Automation.Host.PSHost? host = null)
    {
        if (t.Agent is not null || _pinned is null) return PsRemoting.Open(t, host);
        // behind a jump host through a local forward, as PsRemoting.Open does it
        var forward = t.Via is { } via ? JumpForwarders.Open(via, t.Host, t.Port) : null;
        var ci = (WSManConnectionInfo)PsRemoting.ConnectionInfo(t, forward);
        ci.SkipCACheck = true;
        ci.SkipCNCheck = true;
        ci.SkipRevocationCheck = true;
        var rs = host is null ? RunspaceFactory.CreateRunspace(ci) : RunspaceFactory.CreateRunspace(host, ci);
        if (forward is not null)
            rs.StateChanged += (_, e) => { if (e.RunspaceStateInfo.State is RunspaceState.Closed or RunspaceState.Broken) forward.Dispose(); };
        try { rs.Open(); }
        catch { rs.Dispose(); forward?.Dispose(); throw; }
        return rs;
    }

    private static T WithRunspace<T>(PsTarget t, Func<Runspace, T> f)
    {
        using var rs = Open(t);
        return f(rs);
    }

    private static object Ps(PsTarget t, Dictionary<string, string> o) => WithRunspace(t, rs =>
    {
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        var output = ps.AddScript(o["script"]).Invoke().Select(x => x?.ToString()).ToList();
        return new { output, errors = ps.Streams.Error.Select(e => e.ToString()).ToList() };
    });

    // The PowerShell tab's host (PsConsoleHost): Read-Host, choices and Get-Credential answered from --lines and
    // --secrets in order, as someone typing in the console and the password dialog would.
    private static object PsHost(PsTarget t, Dictionary<string, string> o)
    {
        var sink = new ScriptedConsole(o.GetValueOrDefault("lines")?.Split('|') ?? [], o.GetValueOrDefault("secrets")?.Split('|') ?? []);
        using var rs = Open(t, new PsConsoleHost(sink)); // the same pinned path as the other operations (--via winrm --cert)
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        var output = ps.AddScript(o["script"]).Invoke().Select(x => x?.ToString()).ToList();
        return new { output, errors = ps.Streams.Error.Select(e => e.ToString()).ToList(), written = sink.Written.ToString(), secretPrompts = sink.SecretPrompts, linesLeft = sink.LinesLeft };
    }

    private sealed class ScriptedConsole(IEnumerable<string> lines, IEnumerable<string> secrets) : IConsoleSink
    {
        private readonly Queue<string> _lines = new(lines);
        private readonly Queue<string> _secrets = new(secrets);
        public readonly System.Text.StringBuilder Written = new();
        public readonly List<string> SecretPrompts = [];
        public int LinesLeft => _lines.Count;
        public void Write(string text, ConsoleColor? color) { lock (Written) Written.Append(text); }
        public string? ReadLine() => _lines.TryDequeue(out var l) ? l : null;
        public string? ReadSecret(string caption, string message) { SecretPrompts.Add($"{caption}: {message}"); return _secrets.TryDequeue(out var s) ? s : null; }
        public void Progress(ProgressRecord record) { }
        public int Width => 120;
    }

    // A --in-mb argument (random text) to the device and --out-mb of output back: lengths and SHA-256 compared on both
    // sides. Too large a message ends the session - answered as { failed: <message> }.
    private static object PsBig(PsTarget t, Dictionary<string, string> o)
    {
        var inBytes = (int)(double.Parse(o["in-mb"], System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
        var outBytes = (int)(double.Parse(o.GetValueOrDefault("out-mb") ?? "0", System.Globalization.CultureInfo.InvariantCulture) * 1024 * 1024);
        var input = Convert.ToBase64String(RandomNumberGenerator.GetBytes(inBytes * 3 / 4 + 3))[..inBytes];
        var inHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
        try
        {
            return WithRunspace(t, rs =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = rs;
                ps.AddScript("""
                    param([string]$s, [int]$n)
                    $s.Length
                    [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s))).Replace('-', '')
                    if ($n -gt 0) { 'y' * $n }
                    """).AddParameter("s", input).AddParameter("n", outBytes);
                var r = ps.Invoke().Select(x => x?.ToString() ?? "").ToList();
                var back = r.Count > 2 ? r[2] : "";
                return (object)new
                {
                    sentChars = input.Length, deviceChars = int.Parse(r[0]), hashEqual = r[1] == inHash,
                    receivedChars = back.Length, receivedOk = back.Length == outBytes && back.All(c => c == 'y'),
                    errors = ps.Streams.Error.Select(e => e.ToString()).ToList(),
                };
            });
        }
        catch (Exception ex) { return new { failed = ex.GetBaseException().Message }; }
    }

    private static object Complete(PsTarget t, Dictionary<string, string> o) => WithRunspace(t, rs =>
    {
        // what the PowerShell tab does on Tab: the remote TabExpansion2, read field by field
        var line = o["line"];
        var cursor = int.TryParse(o.GetValueOrDefault("cursor"), out var c) ? c : line.Length;
        var r = PsSession.CompleteOn(rs, line, cursor);
        return new { replacementIndex = r?.ReplacementIndex, matches = r?.Matches.Select(m => m.CompletionText).Take(50).ToList() ?? [] };
    });

    private static object Simple(Runspace rs, string op, Dictionary<string, string> o)
    {
        switch (op)
        {
            case "fs-list":
            {
                var l = RemoteFs.ListRemote(rs, o.GetValueOrDefault("path") ?? "");
                return new { l.Path, entries = l.Entries.Select(e => new { e.Name, e.FullPath, kind = e.Kind.ToString(), e.Size, e.Hidden }) };
            }
            case "fs-mkdir": return new { path = RemoteFs.MkdirRemote(rs, o["path"]) };
            case "fs-rename": return new { path = RemoteFs.RenameRemote(rs, o["path"], o["name"]) };
            case "fs-delete": RemoteFs.DeleteRemote(rs, o["paths"].Split('|')); return new { deleted = o["paths"].Split('|') };

            case "reg-list":
            {
                var l = RemoteRegistry.List(rs, RemoteRegistry.Normalize(o["path"]));
                return new { l.Path, keys = l.Keys.Select(k => k.Name), values = l.Values.Select(v => new { v.Name, kind = v.Kind.ToString(), v.Data }) };
            }
            case "reg-set":
            {
                var kind = Enum.Parse<RegKind>(o["kind"]);
                if (!RemoteRegistry.TryParse(kind, o["data"].Replace("\\n", "\n"), o.GetValueOrDefault("hex") == "1", out var data, out var error))
                    throw new RemoteCommandException(error);
                RemoteRegistry.SetValue(rs, RemoteRegistry.Normalize(o["key"]), o["name"], kind, data, createOnly: false);
                return new { set = o["name"] };
            }
            case "reg-rename-value": RemoteRegistry.RenameValue(rs, RemoteRegistry.Normalize(o["key"]), o["name"], o["new"]); return new { renamed = o["new"] };
            case "reg-delete-value": RemoteRegistry.DeleteValues(rs, RemoteRegistry.Normalize(o["key"]), [o["name"]]); return new { deleted = o["name"] };
            case "reg-create-key": return new { key = RemoteRegistry.CreateKey(rs, RemoteRegistry.Normalize(o["parent"]), o["name"]) };
            case "reg-rename-key": return new { key = RemoteRegistry.RenameKey(rs, RemoteRegistry.Normalize(o["key"]), o["new"]) };
            case "reg-delete-key": RemoteRegistry.DeleteKey(rs, RemoteRegistry.Normalize(o["key"])); return new { deleted = o["key"] };
            case "reg-export":
            {
                var bytes = RemoteRegistry.Export(rs, RemoteRegistry.Normalize(o["key"]));
                File.WriteAllBytes(o["out"], bytes);
                return new { file = o["out"], bytes = bytes.Length };
            }

            case "svc-list":
                return new { services = RemoteServices.List(rs).Select(s => new { s.Name, s.DisplayName, s.State, startup = s.Startup.ToString(), s.Account, s.ProcessId }) };
            case "svc-control": RemoteServices.Control(rs, o["name"], Enum.Parse<ServiceAction>(o["action"])); return new { o = o["action"] };
            case "svc-startup": RemoteServices.SetStartup(rs, o["name"], Enum.Parse<ServiceStartup>(o["startup"])); return new { o = o["startup"] };

            case "proc-list":
            {
                var s = RemoteProcesses.List(rs);
                return new { s.OwnPid, s.Cpus, processes = s.Processes.Select(p => new { p.Id, p.ParentId, p.Name, p.User, p.SessionId }) };
            }
            case "proc-stop":
            {
                var ids = o["ids"].Split('|').Select(int.Parse).ToArray();
                var names = o["names"].Split('|');
                RemoteProcesses.Stop(rs, ids.Select((id, i) => (id, names[i])).ToList(), o.GetValueOrDefault("tree") == "1");
                return new { stopped = ids };
            }
        }
        throw new ArgumentException("unknown op " + op);
    }

    // FileUploader as the file transfer tab uses it: folders expanded the same way, a runspace per transfer.
    private static async Task<object> Upload(PsTarget t, Dictionary<string, string> o)
    {
        var items = FileUploader.Expand(o["files"].Split('|')).Select(x => new TransferItem(x.Local, x.Relative, "lab")).ToList();
        var task = FileUploader.UploadAsync(() => Open(t), ex => PsRemoting.Explain(ex, t), o["remote"], o.GetValueOrDefault("overwrite") == "1", items, a => a());
        if (int.TryParse(o.GetValueOrDefault("cancel-after-mb"), out var mb))
        {
            while (!task.IsCompleted && items.Sum(i => i.SentBytes) < mb * 1024L * 1024) await Task.Delay(50);
            foreach (var i in items) i.Cancel();
        }
        await task;
        return Report(items);
    }

    private static async Task<object> Download(PsTarget t, Dictionary<string, string> o)
    {
        var local = o["local"];
        List<(long Size, string FullPath, string Relative)> found;
        using (var rs = Open(t)) found = RemoteFs.ExpandRemote(rs, o["remote"].Split('|'));
        var items = found.Select(f => TransferItem.Download(f.FullPath, Path.Combine(local, f.Relative), f.Relative, "lab", f.Size)).ToList();
        await FileDownloader.DownloadAsync(() => Open(t), ex => PsRemoting.Explain(ex, t), true, items, a => a());
        var report = items.Select(i => new
        {
            i.RelativePath,
            state = i.State.ToString(),
            i.StatusText,
            sha256 = File.Exists(i.LocalPath) && i.State == TransferState.Done ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(i.LocalPath))) : null,
        }).ToList();
        return new { items = report };
    }

    private static object Report(List<TransferItem> items) => new
    {
        items = items.Select(i => new { i.RelativePath, state = i.State.ToString(), i.StatusText, i.TotalBytes, i.SentBytes }).ToList(),
        done = items.Count(i => i.State == TransferState.Done),
        skipped = items.Count(i => i.State == TransferState.Skipped),
        failed = items.Count(i => i.State == TransferState.Failed),
        cancelled = items.Count(i => i.State == TransferState.Cancelled),
    };
}
