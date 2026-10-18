using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteHub.Agent;

/// <summary>
/// Reads the file list copied at the device in the signed-in user's session, as that user. On Windows 11 26H2 the
/// LocalSystem helper is shown only "DataObject" for a file copy, while the user's own programs read FileDrop (lab runs
/// 20261018-112637 to -133341): so the helper starts <c>RemoteHubAgent.exe clipboard-files</c> with the user's token.
/// The reader takes no input (fixed command line, standard input closed), reads CF_HDROP through OLE like any of the
/// user's programs, writes the paths to its standard output and ends. The helper trusts nothing of that but the parsed
/// path list (size, count and path checks as for a list read itself); the files are read as the user afterwards anyway.
/// </summary>
internal static class ClipboardFilesReader
{
    public const string Verb = "clipboard-files";

    /// <summary>How long the helper waits for the reader before it ends it (with everything it started).</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(3);

    /// <summary>What the reader writes at most: 1000 paths of up to 32,767 characters would be far more – long paths are rare.</summary>
    public const int MaxOutputBytes = 1024 * 1024;

    /// <summary>The paths, or null with why (never a path).</summary>
    public sealed record Result(IReadOnlyList<string>? Files, string Why);

    // ---- the reader (runs as the user)

    /// <summary>
    /// <c>RemoteHubAgent.exe clipboard-files</c>: "files" and one path per line, or "none" and why. Exit code 0 when
    /// there were files. Nothing is written anywhere else, nothing persisted.
    /// </summary>
    public static int Run(TextWriter output)
    {
        IReadOnlyList<string>? files = null;
        var why = "";
        // OLE needs a single-threaded apartment.
        var t = new Thread(() =>
        {
            // Another program may have the clipboard open for a moment (CLIPBRD_E_CANT_OPEN): tried again briefly,
            // holding nothing in between. The helper's time limit bounds it all.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { files = Win32HelperUi.OleClipboardFiles(out why); }
                catch (Exception ex) { why = ex.GetType().Name; }
                if (files is not null || !why.StartsWith("OleGetClipboard", StringComparison.Ordinal)) break;
                Thread.Sleep(50);
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        output.Write(Format(files, why));
        output.Flush();
        return files is { Count: > 0 } ? 0 : 2;
    }

    /// <summary>The reader's output for a result.</summary>
    internal static string Format(IReadOnlyList<string>? files, string why) =>
        files is { Count: > 0 } ? "files\n" + string.Concat(files.Select(f => f + "\n")) : "none " + why.ReplaceLineEndings(" ") + "\n";

    // ---- the helper's side (LocalSystem)

    /// <summary>
    /// Parses what a reader wrote: UTF-8, "files" then one path per line. Every path must pass the checks of a list the
    /// helper reads itself (fully qualified, no device paths, nothing with a NUL); at most
    /// <see cref="DesktopProtocol.MaxTransferItems"/> are kept. Anything else – too long, not UTF-8, no header – is refused.
    /// </summary>
    internal static Result Parse(byte[] output)
    {
        if (output.Length > MaxOutputBytes) return new(null, "the reader wrote too much");
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output); }
        catch (DecoderFallbackException) { return new(null, "the reader wrote no text"); }
        var lines = text.Split('\n');
        if (lines[0].StartsWith("none", StringComparison.Ordinal))
            return new(null, lines[0].Length > 5 ? Shorten(lines[0][5..].Trim()) : "no file list");
        if (lines[0].TrimEnd('\r') != "files") return new(null, "the reader's answer was not understood");
        var files = new List<string>();
        foreach (var raw in lines.Skip(1))
        {
            var path = raw.TrimEnd('\r');
            if (path.Length == 0) continue;
            if (!Win32HelperUi.UsablePath(path)) continue; // e.g. a device path: left out, as in a list read here
            files.Add(path);
            if (files.Count == DesktopProtocol.MaxTransferItems) break;
        }
        return files.Count > 0 ? new(files, "") : new(null, "no usable path");
    }

    /// <summary>The reader's reason for the log: short, and never more than a reason (an HRESULT or an exception's type).</summary>
    private static string Shorten(string why) => why.Length > 80 ? why[..80] : why;

    /// <summary>
    /// Starts the reader in this process's session as the signed-in user (no window, its own job, only its own pipe
    /// ends inherited), reads its output up to <see cref="MaxOutputBytes"/> and ends it after <paramref name="limit"/>.
    /// Call as LocalSystem (the helper); without a user's token nothing is started.
    /// </summary>
    public static Result ReadAsUser(TimeSpan limit)
    {
        using var token = SessionUser.UserToken();
        if (token is null) return new(null, "nobody is signed in");
        var exe = Environment.ProcessPath;
        if (exe is null) return new(null, "the agent's path is unknown");
        var environment = IntPtr.Zero;
        IntPtr job = IntPtr.Zero, process = IntPtr.Zero;
        try
        {
            if (!Native.CreateEnvironmentBlock(out environment, token.DangerousGetHandle(), false)) environment = IntPtr.Zero;
            var (pi, stdinWrite, stdoutRead) = ChildProcess.Start(token.DangerousGetHandle(), $"\"{exe}\" {Verb}",
                Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW, environment, Path.GetDirectoryName(exe), @"winsta0\default",
                stdoutBuffer: 64 * 1024, stderrToNul: true);
            process = pi.hProcess;
            stdinWrite.Dispose(); // the reader takes no input
            using var stdout = new FileStream(stdoutRead, FileAccess.Read, 4096);
            job = ChildProcess.RunInJob(pi);
            // Read while it writes (a long list would fill the pipe), up to the cap; the time limit covers it all.
            var read = Task.Run(() =>
            {
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int n;
                while ((n = stdout.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxOutputBytes) break;
                }
                return buffer.ToArray();
            });
            if (!read.Wait(limit))
            {
                Native.CloseHandle(job); // ends it now, before its output pipe is closed under the read
                job = IntPtr.Zero;
                return new(null, $"the reader gave no answer within {limit.TotalSeconds:0} s");
            }
            return Parse(read.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new(null, "the reader could not be started: " + ex.GetType().Name);
        }
        finally
        {
            if (job != IntPtr.Zero) Native.CloseHandle(job); // ends the reader if it still runs, and anything it started
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
        }
    }
}
