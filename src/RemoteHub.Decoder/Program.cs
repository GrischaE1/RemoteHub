using System.Buffers.Binary;
using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RemoteHub.Agent;
using RemoteHub.Remoting.Decoder;
using RemoteHub.Services;

namespace RemoteHub.Decoder;

/// <summary>
/// The decoder process (docs/decoder-isolation-design.md §3.1): started by RemoteHub only, in an AppContainer with no
/// capabilities and a job (or at low integrity), with exactly two pipe ends and the video section inherited. It answers
/// one request at a time and holds nothing but the decoders – whatever a crafted stream does here can't reach the
/// network, the user's files, the clipboard or RemoteHub.
/// </summary>
internal static unsafe class Program
{
    [STAThread] // WIC through WPF's imaging classes
    private static int Main(string[] args)
    {
        if (args is not ["--decoder", var toDecoder, var fromDecoder, var section]) return 2;
        using var input = new AnonymousPipeClientStream(PipeDirection.In, toDecoder);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, fromDecoder);
        var view = (byte*)Native.MapViewOfFile((IntPtr)long.Parse(section), Native.FILE_MAP_WRITE, 0, 0, (UIntPtr)(2L * DecoderProtocol.VideoBufferBytes));
        if (view == null) return 3;
        DecoderProtocol.Write(output, DecoderProtocol.Bare(DecoderMessage.Ready));

        H264Decoder? video = null;
        var next = 0;
        try
        {
            while (DecoderProtocol.Read(input, DecoderProtocol.MaxRequestBytes) is { } m)
            {
                switch ((DecoderMessage)m[0])
                {
                    case DecoderMessage.Video when m.Length > 10:
                    {
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(1));
                        int width = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(5)), height = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(7));
                        if (width is < 1 or > DecoderProtocol.MaxVideoWidth || height is < 1 or > DecoderProtocol.MaxVideoHeight)
                        {
                            DecoderProtocol.Write(output, DecoderProtocol.WithId(DecoderMessage.Failed, id, "picture size outside the limits"));
                            break;
                        }
                        int shownW = 0, shownH = 0, buffer = next;
                        try
                        {
                            video ??= new H264Decoder();
                            video.Decode(m.AsSpan(10), width, height, (bgra, w, h) =>
                            {
                                if (w > width || h > height) return; // never more than asked for (and so than the buffer)
                                Buffer.MemoryCopy(bgra, view + (long)buffer * DecoderProtocol.VideoBufferBytes, DecoderProtocol.VideoBufferBytes, (long)w * h * 4);
                                (shownW, shownH) = (w, h);
                            });
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            try { video?.Flush(); } catch { video?.Dispose(); video = null; }
                            DecoderProtocol.Write(output, DecoderProtocol.WithId(DecoderMessage.Failed, id, ex.Message));
                            break;
                        }
                        if (shownW == 0) DecoderProtocol.Write(output, DecoderProtocol.WithId(DecoderMessage.NoFrame, id));
                        else
                        {
                            DecoderProtocol.Write(output, DecoderProtocol.Frame(id, buffer, shownW, shownH));
                            next = 1 - next;
                        }
                        break;
                    }
                    case DecoderMessage.Image when m.Length > 17:
                    {
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(1));
                        var handle = (IntPtr)(long)BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(5));
                        int width = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(13)), height = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(15));
                        try
                        {
                            var image = DeviceImage.Decode(m[17..]);
                            if (image is null || image.Width != width || image.Height != height)
                            {
                                DecoderProtocol.Write(output, DecoderProtocol.WithId(DecoderMessage.Failed, id, "not a PNG, BMP or JPEG of the stated size"));
                                break;
                            }
                            var target = Native.MapViewOfFile(handle, Native.FILE_MAP_WRITE, 0, 0, (UIntPtr)image.Bgra.Length);
                            if (target == IntPtr.Zero) throw new Win32Exception();
                            try { Marshal.Copy(image.Bgra, 0, target, image.Bgra.Length); }
                            finally { Native.UnmapViewOfFile(target); }
                            DecoderProtocol.Write(output, DecoderProtocol.ImageDone(id, width, height));
                        }
                        finally { Native.CloseHandle(handle); }
                        break;
                    }
                    case DecoderMessage.Probe:
                        DecoderProtocol.Write(output, DecoderProtocol.Text(DecoderMessage.ProbeResult, JsonSerializer.Serialize(Probes())));
                        break;
                    case DecoderMessage.Exit:
                        return 0;
                    default:
                        return 4; // RemoteHub never sends anything else
                }
            }
            return 0;
        }
        finally { video?.Dispose(); }
    }

    // ---- what the sandbox must prevent: each tried, the result reported (docs/decoder-isolation-design.md §6, §11)

    private static string Try(Func<string> attempt)
    {
        try { return "allowed: " + attempt(); }
        catch (Exception ex) { return "blocked: " + ex.GetType().Name; }
    }

    private static Dictionary<string, string> Probes()
    {
        // The user's own folders: in an AppContainer, TEMP and LOCALAPPDATA point to the container's private storage.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(profile, "AppData", "Local");
        return new()
        {
            ["sandbox"] = Native.Sandbox(),
            ["network"] = Try(() =>
            {
                using var c = new TcpClient();
                if (!c.ConnectAsync("1.1.1.1", 443).Wait(3000)) throw new TimeoutException();
                return "connected";
            }),
            ["loopback"] = Try(() =>
            {
                using var c = new TcpClient();
                if (!c.ConnectAsync("127.0.0.1", 135).Wait(3000)) throw new TimeoutException();
                return "connected";
            }),
            ["profile"] = Try(() => Directory.GetFiles(profile).Length + " files"),
            ["database"] = Try(() => Directory.GetFiles(Path.Combine(local, "RemoteHub")).Length + " files"),
            ["write"] = Try(() =>
            {
                var f = Path.Combine(local, "Temp", "rh-decoder-probe.txt");
                File.WriteAllText(f, "x");
                File.Delete(f);
                return f;
            }),
            ["registry"] = Try(() =>
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\RemoteHubDecoderProbe");
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"Software\RemoteHubDecoderProbe");
                return "written";
            }),
            ["clipboard"] = Try(Native.ClipboardText),
            ["process"] = Try(() =>
            {
                using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0")
                                            { UseShellExecute = false, CreateNoWindow = true })!;
                return "started";
            }),
            ["parent"] = Try(() =>
            {
                using var me = Process.GetCurrentProcess();
                var parent = Native.ParentProcessId();
                var h = Native.OpenProcess(0x0010 /* VM_READ */, false, parent);
                if (h == IntPtr.Zero) throw new Win32Exception();
                Native.CloseHandle(h);
                return "opened";
            }),
            ["window"] = Try(() => Native.WindowText("Shell_TrayWnd")),
        };
    }
}

internal static unsafe class Native
{
    public const uint FILE_MAP_WRITE = 0x0002;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr section, uint access, uint high, uint low, UIntPtr size);
    [DllImport("kernel32.dll")] public static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int cls, out PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }

    public static int ParentProcessId() =>
        NtQueryInformationProcess(GetCurrentProcess(), 0, out var info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
            ? (int)info.InheritedFromUniqueProcessId : throw new Win32Exception();

    public static string ClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Win32Exception();
        try { return GetClipboardData(13) == IntPtr.Zero ? throw new Win32Exception() : "read"; }
        finally { CloseClipboard(); }
    }

    /// <summary>Finding a window works anywhere; using another process's window must fail (job UI limit: error 1400).</summary>
    public static string WindowText(string cls)
    {
        var hwnd = FindWindow(cls, null);
        if (hwnd == IntPtr.Zero) throw new Win32Exception();
        var text = new StringBuilder(256);
        if (GetWindowText(hwnd, text, 256) == 0 && Marshal.GetLastWin32Error() is var e and not 0) throw new Win32Exception(e);
        return "read";
    }

    /// <summary>"AppContainer", "low integrity" or what else the token is.</summary>
    public static string Sandbox()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x8 /* TOKEN_QUERY */, out var token)) return "unknown";
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            if (GetTokenInformation(token, 29 /* TokenIsAppContainer */, buffer, 64, out _) && Marshal.ReadInt32(buffer) != 0) return "AppContainer";
            if (!GetTokenInformation(token, 25 /* TokenIntegrityLevel */, buffer, 64, out _)) return "unknown";
            var level = new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return level == "S-1-16-4096" ? "low integrity" : "integrity " + level;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }
}
