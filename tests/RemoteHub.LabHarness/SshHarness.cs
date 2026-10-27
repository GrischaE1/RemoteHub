using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using RemoteHub.Models;
using RemoteHub.Rdp;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Shares;

/// <summary>
/// SSH connections against a lab VM with RemoteHub's own code – the SSH tab's SshSession, SshShell and VtTerminal – on a
/// WPF dispatcher thread, as the tab runs them:
///   ssh --host .. [--port n] --user .. [--password ..] [--key &lt;file&gt; [--passphrase ..]] [--trusted &lt;fingerprint&gt;]
///       [--accept 0|1] [--columns n --rows n] [--command &lt;text&gt; --expect &lt;regex&gt;]
///       [--resize &lt;columns&gt;x&lt;rows&gt; --resize-command &lt;text&gt; --resize-expect &lt;regex&gt;] [--exit 0|1]
///       Connects (a host key seen for the first time is accepted only with --accept 1, as a user would), waits for the
///       shell's first output, types --command and Enter and waits until the screen matches --expect; then resizes the
///       tab and does the same with --resize-command; then types "exit" and waits for the tab to say the session ended.
///       Answers ok, connected (the tab's status text), transport, asked, accepted, found, resized, ended, endText and the
///       screen at the end (its text only – what the commands printed).
/// </summary>
internal static class SshHarness
{
    public static object Run(Dictionary<string, string> o)
    {
        object? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { result = await RunAsync(o); }
                catch (Exception ex) { error = ex; }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Throw(error);
        return result!;
    }

    private static async Task<bool> Until(Func<bool> done, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.Elapsed > max) return false;
            await Task.Delay(200);
        }
        return true;
    }

    private static async Task<object> RunAsync(Dictionary<string, string> o)
    {
        var entry = new NetworkShare
        {
            Name = "lab", Protocol = ShareProtocol.Ssh, Host = o["host"],
            Port = o.TryGetValue("port", out var p) ? int.Parse(p) : null,
            TrustedFingerprint = o.GetValueOrDefault("trusted"),
        };
        var cred = ShareHarness.Credentials(o);
        var accept = o.GetValueOrDefault("accept") == "1";
        var via = LabJump.Route(o);
        var asked = new List<object>();
        string? accepted = null;
        bool Trust(TrustRequest r) { lock (asked) asked.Add(new { r.What, r.Fingerprint }); return accept; }

        var session = new SshSession(new Connection { Name = "lab", Host = o["host"] }, entry);
        session.Resize(int.Parse(o.GetValueOrDefault("columns") ?? "100"), int.Parse(o.GetValueOrDefault("rows") ?? "30"));
        string Screen() { lock (session.Terminal.SyncRoot) return session.Terminal.ScreenText(); }
        bool Shows(string pattern) => Regex.IsMatch(Screen(), pattern, RegexOptions.Multiline);
        var sw = Stopwatch.StartNew();
        try
        {
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} connect…");
            session.Connect((columns, rows) => SshShell.Open(entry, cred, Trust, fp => { accepted = fp; entry.TrustedFingerprint = fp; }, via, columns, rows),
                "lab harness", System.Windows.Media.Brushes.Transparent);
            await Until(() => session.State != SessionState.Connecting, TimeSpan.FromMinutes(2));
            var connected = session.StatusText;
            var connectMs = sw.ElapsedMilliseconds;
            if (session.State != SessionState.Connected) return new { ok = false, connected, asked, accepted, connectMs };

            var prompt = await Until(() => Screen().Trim().Length > 0, TimeSpan.FromSeconds(30));
            bool? found = null;
            if (o.TryGetValue("command", out var command))
            {
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} command…");
                session.Send(command + "\r");
                found = await Until(() => Shows(o["expect"]), TimeSpan.FromSeconds(30));
            }
            bool? resized = null;
            if (o.TryGetValue("resize", out var size))
            {
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} resize to {size}…");
                var wh = size.Split('x');
                session.Resize(int.Parse(wh[0]), int.Parse(wh[1]));
                session.Send(o["resize-command"] + "\r");
                resized = await Until(() => Shows(o["resize-expect"]), TimeSpan.FromSeconds(30));
            }
            bool? ended = null;
            string? endText = null;
            if (o.GetValueOrDefault("exit") != "0")
            {
                Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} exit…");
                session.Send("exit\r");
                ended = await Until(() => session.State == SessionState.Disconnected, TimeSpan.FromSeconds(30));
                endText = session.StatusText;
            }
            var screen = Screen();
            int columnsNow, rowsNow;
            lock (session.Terminal.SyncRoot) (columnsNow, rowsNow) = (session.Terminal.Columns, session.Terminal.Rows);
            return new
            {
                ok = prompt && found != false && resized != false && ended != false,
                connected, transport = session.Transport, asked, accepted, connectMs, prompt, found, resized, ended, endText,
                columns = columnsNow, rows = rowsNow, screen = screen.Length > 3000 ? screen[^3000..] : screen,
            };
        }
        finally { session.Close(); }
    }
}
