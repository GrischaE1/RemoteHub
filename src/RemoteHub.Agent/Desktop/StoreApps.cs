using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>
/// Store apps in app tabs (docs/app-tabs-design.md §6.6): packaged apps, started and listed by their AppUserModelID.
/// <c>IApplicationActivationManager</c> and the user's <c>shell:AppsFolder</c> only work as the user, so the service
/// starts <c>RemoteHubAgent.exe store-apps</c> with the admin's token in their session – as the helper starts
/// <c>clipboard-files</c>. Its command line is fixed: what to do comes on standard input, so an app's ID and arguments
/// never show in process auditing. It writes one answer to standard output and ends; the service trusts nothing of it
/// but the parsed answer.
/// </summary>
internal static partial class StoreApps
{
    public const string Verb = "store-apps";

    /// <summary>A Store app on a connection: <c>shell:AppsFolder\&lt;AUMID&gt;</c>, the form Explorer uses for them.</summary>
    public const string Prefix = @"shell:AppsFolder\";

    public const int MaxAppId = 256;

    /// <summary>
    /// How long a start may take: up to <see cref="ShellWait"/> for the shell after a sign-in, and a first start took 12 s
    /// in the spike.
    /// </summary>
    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(90);

    /// <summary>How long a start waits for the shell of a session just signed in to (<see cref="WaitForShell"/>).</summary>
    public static readonly TimeSpan ShellWait = TimeSpan.FromSeconds(60);

    /// <summary>How long the list may take: 0.2–2.4 s in the spike, once 89 s right after a profile's first sign-in.</summary>
    public static readonly TimeSpan ListLimit = TimeSpan.FromSeconds(120);

    public const int MaxOutputBytes = 1024 * 1024;

    // PackageFamilyName!AppId: letters, digits, '.', '-', '_' on both sides of exactly one '!'.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+![A-Za-z0-9._-]+$")]
    private static partial Regex AppIdPattern();

    public static bool IsStoreApp(string program) => program.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The AppUserModelID of <c>shell:AppsFolder\&lt;AUMID&gt;</c>, or null when it isn't one that can be used.</summary>
    public static string? AppId(string program)
    {
        var p = program.Trim();
        if (!p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = p[Prefix.Length..];
        return ValidAppId(id) ? id : null;
    }

    public static bool ValidAppId(string id) => id.Length is > 0 and <= MaxAppId && AppIdPattern().IsMatch(id);

    // ---- the store-apps process (runs as the user)

    /// <summary>
    /// <c>RemoteHubAgent.exe store-apps</c>. Input: "start", the AppUserModelID and the arguments (the rest of the input),
    /// each on its own line – or "list". Output: "started &lt;process id&gt; &lt;how&gt;" (<see cref="Shown"/>), or "apps" and
    /// one "&lt;AUMID&gt;\t&lt;name&gt;" per line, or "none &lt;why&gt;". Exit code 0 when it did what was asked.
    /// </summary>
    public static int Run(TextReader input, TextWriter output)
    {
        var what = input.ReadLine();
        string answer = "none nothing was asked\n";
        // COM (the activation manager, the shell) on a single-threaded apartment, as the shell expects.
        var t = new Thread(() =>
        {
            try
            {
                if (what == "start")
                {
                    var id = input.ReadLine() ?? "";
                    var arguments = input.ReadToEnd();
                    answer = ValidAppId(id) ? Activate(id, arguments) : "none the app's ID can't be used\n";
                }
                else if (what == "list") answer = ReadList();
            }
            catch (Exception ex) { answer = "none " + ex.GetType().Name + "\n"; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        output.Write(answer);
        output.Flush();
        return answer.StartsWith("none", StringComparison.Ordinal) ? 2 : 0;
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static unsafe string Activate(string id, string arguments)
    {
        WaitForShell();
        var clsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"); // ApplicationActivationManager
        var iid = typeof(IApplicationActivationManager).GUID;
        var hr = CoCreateInstance(in clsid, 0, 0x17 /* CLSCTX_ALL */, in iid, out var raw);
        if (hr != 0) return $"none CoCreateInstance 0x{hr:X8}\n";
        try
        {
            var manager = (IApplicationActivationManager)Wrappers.GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);
            uint pid = 0;
            int Activate()
            {
                uint started = 0;
                int result;
                fixed (char* i = id)
                fixed (char* a = arguments)
                    result = manager.ActivateApplication(i, a, 0 /* AO_NONE */, &started);
                if (result == 0) pid = started;
                return result;
            }
            hr = Activate();
            if (hr != 0 || pid == 0) return $"none ActivateApplication 0x{hr:X8}\n";
            // The shell may keep an app it activated hidden (cloaked, no frame): seen on Server 2025 right after a sign-in
            // (lab run 20261030-160524). Activated once more then, as a click on its tile would.
            var how = Shown;
            if (!WaitShown(id, pid, ShowWait))
            {
                how = Activate() == 0 && WaitShown(id, pid, ShowWait) ? Reactivated : Hidden;
            }
            return $"started {pid} {how}\n";
        }
        finally { Marshal.Release(raw); }
    }

    /// <summary>How a started app came up: on its own, after a second activation, or still hidden after it.</summary>
    public const string Shown = "shown", Reactivated = "reactivated", Hidden = "hidden";

    /// <summary>How long a start waits for the app's window to show before it activates it again.</summary>
    private static readonly TimeSpan ShowWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The app has a window the user can see: visible and not cloaked, of its process or carrying its AppUserModelID (a UWP
    /// app's frame, drawn by ApplicationFrameHost).
    /// </summary>
    private static bool WaitShown(string id, uint pid, TimeSpan limit)
    {
        var until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        do
        {
            var shown = false;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0)) return true;
                GetWindowThreadProcessId(h, out var owner);
                if (owner == pid || string.Equals(AppTabWindows.ReadAppIdOf(h), id, StringComparison.OrdinalIgnoreCase)) shown = true;
                return !shown;
            }, 0);
            if (shown) return true;
            Thread.Sleep(250);
        } while (Environment.TickCount64 < until);
        return false;
    }

    /// <summary>
    /// Waits until the session's shell can host a Store app: its immersive shell window is there. Right after an automatic
    /// sign-in the app tab's start can come before Explorer; a UWP app activated then gets no frame (ApplicationFrameHost)
    /// and shows nowhere (lab run 20261030-151905, Server 2025: Settings one second before Explorer). Goes ahead once the
    /// taskbar has been there for 10 s without it (a shell without one), or after <see cref="ShellWait"/>.
    /// </summary>
    private static void WaitForShell()
    {
        var until = Environment.TickCount64 + (long)ShellWait.TotalMilliseconds;
        long trayAt = -1;
        while (Environment.TickCount64 < until)
        {
            if (FindWindowW("ApplicationManager_ImmersiveShellWindow", null) != 0) return;
            if (FindWindowW("Shell_TrayWnd", null) != 0)
            {
                if (trayAt < 0) trayAt = Environment.TickCount64;
                else if (Environment.TickCount64 - trayAt >= 10_000) return;
            }
            Thread.Sleep(250);
        }
    }

    private static unsafe string ReadList()
    {
        var shellItem = typeof(IShellItem).GUID;
        if (SHCreateItemFromParsingName("shell:AppsFolder", 0, in shellItem, out var folderRaw) != 0) return "none the apps folder can't be opened\n";
        var text = new StringBuilder("apps\n");
        try
        {
            var folder = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(folderRaw, CreateObjectFlags.UniqueInstance);
            var bhid = new Guid("94f60519-2850-4924-aa5a-d15e84868039"); // BHID_EnumItems
            var enumIid = typeof(IEnumShellItems).GUID;
            nint enumRaw;
            if (folder.BindToHandler(0, &bhid, &enumIid, &enumRaw) != 0) return "none the apps folder can't be read\n";
            try
            {
                var items = (IEnumShellItems)Wrappers.GetOrCreateObjectForComInstance(enumRaw, CreateObjectFlags.UniqueInstance);
                var count = 0;
                while (count < AppStartMenu.MaxEntries * 2)
                {
                    nint itemRaw;
                    uint fetched;
                    if (items.Next(1, &itemRaw, &fetched) != 0 || fetched == 0) break;
                    try
                    {
                        var item = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(itemRaw, CreateObjectFlags.UniqueInstance);
                        // A packaged app's parsing name is its AppUserModelID; a desktop program's is a path or another ID.
                        var id = Name(item, 0x80018001 /* SIGDN_PARENTRELATIVEPARSING */);
                        if (id is null || !ValidAppId(id)) continue;
                        var name = Name(item, 0 /* SIGDN_NORMALDISPLAY */) ?? id;
                        text.Append(id).Append('\t').Append(Clean(name)).Append('\n');
                        count++;
                    }
                    finally { Marshal.Release(itemRaw); }
                }
            }
            finally { Marshal.Release(enumRaw); }
        }
        finally { Marshal.Release(folderRaw); }
        return text.ToString();
    }

    private static unsafe string? Name(IShellItem item, uint kind)
    {
        char* name = null;
        if (item.GetDisplayName(kind, &name) != 0 || name == null) return null;
        try { return new string(name); }
        finally { Marshal.FreeCoTaskMem((nint)name); }
    }

    /// <summary>A name on one line, without tabs or control characters.</summary>
    private static string Clean(string name) => new([.. name.Select(c => char.IsControl(c) ? ' ' : c)]);

    // ---- the service's side (LocalSystem)

    /// <summary>Starts <paramref name="appId"/> as the user of <paramref name="token"/>: its process id, or 0 and why.</summary>
    public static (int Pid, string Why) Start(SafeAccessTokenHandle token, string appId, string arguments)
    {
        var (output, why) = RunAsUser(token, $"start\n{appId}\n{arguments}", StartLimit);
        return output is null ? (0, why) : ParseStarted(output);
    }

    /// <summary>The user's Store apps (<c>shell:AppsFolder</c>), or null and why.</summary>
    public static (IReadOnlyList<(string Id, string Name)>? Apps, bool Truncated, string Why) List(SafeAccessTokenHandle token)
    {
        var (output, why) = RunAsUser(token, "list\n", ListLimit);
        return output is null ? (null, false, why) : ParseList(output);
    }

    /// <summary>
    /// The answer to a start: "started &lt;pid&gt; &lt;how&gt;" (a positive number, and <see cref="Shown"/>,
    /// <see cref="Reactivated"/> or <see cref="Hidden"/> as the Why), else 0 and why.
    /// </summary>
    internal static (int Pid, string Why) ParseStarted(byte[] output)
    {
        if (Text(output) is not { } text) return (0, "the answer wasn't understood");
        var line = text.Split('\n')[0].TrimEnd('\r');
        var parts = line.Split(' ');
        if (parts is ["started", var number, ..] && parts.Length <= 3 && int.TryParse(number, System.Globalization.NumberStyles.None, null, out var pid) && pid > 0)
            return (pid, parts.Length == 3 && parts[2] is Shown or Reactivated or Hidden ? parts[2] : "");
        return (0, line.StartsWith("none ", StringComparison.Ordinal) ? Shorten(line[5..]) : "the answer wasn't understood");
    }

    /// <summary>
    /// The answer to a list: "apps" and "&lt;AUMID&gt;\t&lt;name&gt;" lines. Only IDs that can be used, each once; names
    /// capped like a shortcut's (§6.5); at most <see cref="AppStartMenu.MaxEntries"/>.
    /// </summary>
    internal static (IReadOnlyList<(string Id, string Name)>? Apps, bool Truncated, string Why) ParseList(byte[] output)
    {
        if (Text(output) is not { } text) return (null, false, "the answer wasn't understood");
        var lines = text.Split('\n');
        var first = lines[0].TrimEnd('\r');
        if (first.StartsWith("none ", StringComparison.Ordinal)) return (null, false, Shorten(first[5..]));
        if (first != "apps") return (null, false, "the answer wasn't understood");
        var apps = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var id = line[..tab];
            var name = line[(tab + 1)..].Trim();
            if (!ValidAppId(id) || !seen.Add(id)) continue;
            if (name.Length == 0 || name.Any(char.IsControl)) name = id;
            if (name.Length > AppStartMenu.MaxName) name = name[..AppStartMenu.MaxName];
            if (apps.Count == AppStartMenu.MaxEntries) { truncated = true; break; }
            apps.Add((id, name));
        }
        return (apps, truncated, "");
    }

    private static string? Text(byte[] output)
    {
        if (output.Length > MaxOutputBytes) return null;
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output); }
        catch (DecoderFallbackException) { return null; }
    }

    /// <summary>A reason for messages and the log: short, and never more than a reason (an HRESULT, an exception's type).</summary>
    private static string Shorten(string why) => why.Length > 80 ? why[..80] : why;

    /// <summary>
    /// Runs <c>store-apps</c> as the user of <paramref name="token"/> in their session (no window, its own job, only its own
    /// pipe ends inherited), hands it <paramref name="request"/> and reads its answer up to <see cref="MaxOutputBytes"/>;
    /// it is ended after <paramref name="limit"/>. An app it started isn't its child and keeps running.
    /// </summary>
    private static (byte[]? Output, string Why) RunAsUser(SafeAccessTokenHandle token, string request, TimeSpan limit)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return (null, "the agent's path is unknown");
        var environment = IntPtr.Zero;
        IntPtr job = IntPtr.Zero, process = IntPtr.Zero;
        try
        {
            if (!Native.CreateEnvironmentBlock(out environment, token.DangerousGetHandle(), false)) environment = IntPtr.Zero;
            var (pi, stdinWrite, stdoutRead) = ChildProcess.Start(token.DangerousGetHandle(), $"\"{exe}\" {Verb}",
                Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW, environment, Path.GetDirectoryName(exe), @"winsta0\default",
                stdoutBuffer: 64 * 1024, stderrToNul: true);
            process = pi.hProcess;
            using var stdout = new FileStream(stdoutRead, FileAccess.Read, 4096);
            job = ChildProcess.RunInJob(pi);
            using (var stdin = new FileStream(stdinWrite, FileAccess.Write, 4096))
            {
                var bytes = new UTF8Encoding(false).GetBytes(request);
                stdin.Write(bytes);
            }
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
                return (null, $"no answer within {limit.TotalSeconds:0} s");
            }
            return (read.Result, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (null, "it could not be started: " + ex.GetType().Name);
        }
        finally
        {
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            if (process != IntPtr.Zero) Native.CloseHandle(process);
            if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
        }
    }

    [GeneratedComInterface, Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    internal unsafe partial interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication(char* appUserModelId, char* arguments, int options, uint* processId);
    }

    [GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    internal unsafe partial interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, Guid* handler, Guid* iid, nint* result);
        [PreserveSig] int GetParent(nint* parent);
        [PreserveSig] int GetDisplayName(uint kind, char** name);
    }

    [GeneratedComInterface, Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    internal unsafe partial interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, nint* items, uint* fetched);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string className, string? windowName);
    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);
}
