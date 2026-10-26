using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RemoteHub.Services;

namespace RemoteHub.Remoting.Decoder;

/// <summary>
/// The decoder process didn't behave – it ended, didn't answer in time, or answered something it can't have meant. It
/// has been ended; a new one can be started (docs/decoder-isolation-design.md §3.4).
/// </summary>
public sealed class DecoderFailedException(string reason) : Exception(reason);

/// <summary>
/// One <c>RemoteHubDecoder.exe</c>, confined (<see cref="DecoderSandbox"/>), and the console's side of its protocol:
/// requests one at a time, an answer within a time limit, and every answer checked before the console touches the
/// pixels it points to (§3.3) – a decoder that was taken over can only show wrong pixels in its own session's tab.
/// Thread-safe: the video thread and clipboard images take turns.
/// </summary>
public sealed unsafe class DecoderHost : IDisposable
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10), FrameTimeout = TimeSpan.FromSeconds(2), ImageTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where the decoder is: next to RemoteHub.exe.</summary>
    internal static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "RemoteHubDecoder.exe");

    private readonly object _gate = new();
    private readonly AnonymousPipeServerStream _toDecoder, _fromDecoder;
    private readonly SafeSectionHandle _section;
    private readonly byte* _view;
    private readonly DecoderSandbox.Started _process;
    private uint _nextImage;
    private bool _dead;

    public DecoderConfinement Confinement => _process.Confinement;
    public int ProcessId => _process.ProcessId;

    /// <summary>Starts a decoder and waits for it to be ready.</summary>
    /// <exception cref="DecoderUnavailableException">No sandbox could be made, or the program isn't there.</exception>
    /// <exception cref="DecoderFailedException">It started but didn't get ready.</exception>
    public static DecoderHost Start(string? path = null) => new(path ?? DefaultPath);

    private DecoderHost(string exe)
    {
        if (!File.Exists(exe)) throw new DecoderUnavailableException("RemoteHubDecoder.exe is missing from RemoteHub's folder.");
        _toDecoder = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _fromDecoder = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _section = SafeSectionHandle.Create(2L * DecoderProtocol.VideoBufferBytes, inheritable: true);
        try
        {
            _view = (byte*)Native.MapViewOfFile(_section, Native.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (_view == null) throw new Win32Exception();
            var (toDecoder, fromDecoder) = (_toDecoder.ClientSafePipeHandle.DangerousGetHandle(), _fromDecoder.ClientSafePipeHandle.DangerousGetHandle());
            var section = _section.DangerousGetHandle();
            _process = DecoderSandbox.Start(exe, $"--decoder {toDecoder} {fromDecoder} {section}", [toDecoder, fromDecoder, section]);
        }
        catch
        {
            DisposeResources();
            throw;
        }
        // The decoder has its copies now; RemoteHub keeps only its own ends (a closed pipe then means the decoder ended).
        _toDecoder.DisposeLocalCopyOfClientHandle();
        _fromDecoder.DisposeLocalCopyOfClientHandle();
        _section.SetInheritable(false);
        lock (_gate)
        {
            var ready = Answer(StartTimeout, "starting");
            if (ready is not [(byte)DecoderMessage.Ready]) Kill("it didn't say it was ready");
        }
    }

    /// <summary>
    /// Decodes one access unit. <paramref name="frame"/> gets the picture (BGRA, top-down, <c>width × 4</c> per row) if
    /// one came out – it may read it only during the call. False when the decoder couldn't use the input (it is still
    /// fine: ask for a key frame).
    /// </summary>
    /// <exception cref="DecoderFailedException">The decoder misbehaved and has been ended.</exception>
    public bool Video(uint frameId, int width, int height, bool keyFrame, ReadOnlySpan<byte> accessUnit, FrameCallback frame)
    {
        lock (_gate)
        {
            Send(DecoderProtocol.Video(frameId, width, height, keyFrame, accessUnit));
            var a = Answer(FrameTimeout, "decoding a frame");
            if (CheckVideoAnswer(a, frameId, width, height) is { } bad) Kill(bad);
            switch ((DecoderMessage)a[0])
            {
                case DecoderMessage.Frame:
                    int w = BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(6)), h = BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(8));
                    frame(new ReadOnlySpan<byte>(_view + (long)a[5] * DecoderProtocol.VideoBufferBytes, w * h * 4), w, h);
                    return true;
                case DecoderMessage.NoFrame:
                    return true;
                default: // Failed: a bad frame
                    return false;
            }
        }
    }

    public delegate void FrameCallback(ReadOnlySpan<byte> bgra, int width, int height);

    /// <summary>
    /// Why a video answer can't be right (and the decoder must go), or null: the kind, the length, the frame it names,
    /// the buffer, and a picture no larger than the one asked for.
    /// </summary>
    internal static string? CheckVideoAnswer(ReadOnlySpan<byte> a, uint frameId, int width, int height)
    {
        if (a.IsEmpty) return "an empty answer";
        var kind = (DecoderMessage)a[0];
        if (kind is not (DecoderMessage.Frame or DecoderMessage.NoFrame or DecoderMessage.Failed)) return $"answer {a[0]} to a frame";
        if (a.Length < 5) return "an answer too short for a frame id";
        if (BinaryPrimitives.ReadUInt32LittleEndian(a[1..]) != frameId) return "an answer for another frame";
        if (kind == DecoderMessage.NoFrame && a.Length != 5) return "a no-frame answer of the wrong length";
        if (kind != DecoderMessage.Frame) return null;
        if (a.Length != 10) return "a frame answer of the wrong length";
        if (a[5] > 1) return $"buffer {a[5]}";
        int w = BinaryPrimitives.ReadUInt16LittleEndian(a[6..]), h = BinaryPrimitives.ReadUInt16LittleEndian(a[8..]);
        if (w < 1 || h < 1 || w > width || h > height || w > DecoderProtocol.MaxVideoWidth || h > DecoderProtocol.MaxVideoHeight)
            return $"a picture of {w}×{h} for one of {width}×{height}";
        return null;
    }

    /// <summary>
    /// A clipboard image's pixels, or null when it isn't a PNG, BMP or JPEG within the limits (§3.5). RemoteHub reads the
    /// size from the header itself; the decoder writes exactly that many pixels into a section made for this image.
    /// </summary>
    /// <exception cref="DecoderFailedException">The decoder misbehaved and has been ended.</exception>
    public DeviceImagePixels? Image(byte[] file)
    {
        if (DeviceImage.Header(file) is not var (_, width, height)) return null;
        var bytes = (long)width * height * 4;
        using var section = SafeSectionHandle.Create(bytes, inheritable: false);
        lock (_gate)
        {
            var id = ++_nextImage;
            // The decoder gets its own handle to this section only; it closes it when done.
            if (!Native.DuplicateHandle(Native.GetCurrentProcess(), section, _process.Process, out var theirs, Native.FILE_MAP_WRITE, false, 0))
                Kill("the image's section couldn't be handed over: " + new Win32Exception().Message);
            Send(DecoderProtocol.Image(id, (ulong)(long)theirs, width, height, file));
            var a = Answer(ImageTimeout, "decoding an image");
            if (CheckImageAnswer(a, id, width, height) is { } bad) Kill(bad);
            if ((DecoderMessage)a[0] == DecoderMessage.Failed) return null;
        }
        var view = Native.MapViewOfFile(section, Native.FILE_MAP_READ, 0, 0, (UIntPtr)bytes);
        if (view == IntPtr.Zero) return null;
        try
        {
            var pixels = new byte[bytes];
            Marshal.Copy(view, pixels, 0, pixels.Length);
            return new DeviceImagePixels(width, height, pixels);
        }
        finally { Native.UnmapViewOfFile(view); }
    }

    /// <summary>Why an image answer can't be right, or null: the kind, the length, the image it names and exactly the size asked for.</summary>
    internal static string? CheckImageAnswer(ReadOnlySpan<byte> a, uint id, int width, int height)
    {
        if (a.IsEmpty) return "an empty answer";
        var kind = (DecoderMessage)a[0];
        if (kind is not (DecoderMessage.ImageDone or DecoderMessage.Failed)) return $"answer {a[0]} to an image";
        if (a.Length < 5) return "an answer too short for an image id";
        if (BinaryPrimitives.ReadUInt32LittleEndian(a[1..]) != id) return "an answer for another image";
        if (kind != DecoderMessage.ImageDone) return null;
        if (a.Length != 9) return "an image answer of the wrong length";
        int w = BinaryPrimitives.ReadUInt16LittleEndian(a[5..]), h = BinaryPrimitives.ReadUInt16LittleEndian(a[7..]);
        return w == width && h == height ? null : $"an image of {w}×{h} for one of {width}×{height}";
    }

    /// <summary>What the decoder can do in its sandbox (decoder-isolation-design.md §6): probe → "allowed: …" or "blocked: …".</summary>
    public string Probe()
    {
        lock (_gate)
        {
            Send(DecoderProtocol.Bare(DecoderMessage.Probe));
            var a = Answer(TimeSpan.FromSeconds(30), "probing");
            if (a[0] != (byte)DecoderMessage.ProbeResult) Kill("the probe answer isn't one");
            return Encoding.UTF8.GetString(a, 1, a.Length - 1);
        }
    }

    private void Send(byte[] message)
    {
        if (_dead) throw new DecoderFailedException("the decoder has ended");
        try { DecoderProtocol.Write(_toDecoder, message); }
        catch (IOException) { Kill("it closed its pipe (exit code " + ExitCode() + ")"); }
    }

    /// <summary>The next answer, or the decoder is ended: closed pipe, no answer in <paramref name="timeout"/>, a bad length.</summary>
    private byte[] Answer(TimeSpan timeout, string doing)
    {
        var read = Task.Run(() => DecoderProtocol.Read(_fromDecoder, DecoderProtocol.MaxAnswerBytes));
        try
        {
            if (!read.Wait(timeout)) Kill($"no answer within {timeout.TotalSeconds:0} s while {doing} ({State()})");
        }
        catch (AggregateException ex) { Kill($"its answer couldn't be read while {doing}: {ex.InnerException?.Message}"); }
        return read.Result ?? Kill<byte[]>($"it ended while {doing} (exit code {ExitCode()})");
    }

    /// <summary>
    /// What the decoder was doing when it didn't answer: still running with the CPU time it had used (little: it was
    /// starved or blocked; much: busy), or ended with its exit code. The lab saw starts of over 10 s, three in a row,
    /// where 0.3 s is usual (runs 20261025-052539 to 20261026-062351) – this tells the next one apart.
    /// </summary>
    private string State()
    {
        if (Native.GetExitCodeProcess(_process.Process, out var code) && code != Native.STILL_ACTIVE) return $"it had ended, exit code 0x{code:X}";
        return Native.GetProcessTimes(_process.Process, out _, out _, out var kernel, out var user)
            ? $"still running, {TimeSpan.FromTicks(kernel + user).TotalSeconds:0.00} s of CPU used"
            : "still running";
    }

    private string ExitCode()
    {
        Native.WaitForSingleObject(_process.Process, 500);
        return Native.GetExitCodeProcess(_process.Process, out var code) ? $"0x{code:X}" : "unknown";
    }

    private T Kill<T>(string reason)
    {
        Kill(reason);
        return default!;
    }

    /// <summary>Ends the decoder (the job ends everything in it) and reports why.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Kill(string reason)
    {
        _dead = true;
        Native.TerminateJobObject(_process.Job, 1);
        throw new DecoderFailedException(reason);
    }

    /// <summary>
    /// Ends the decoder. A call still waiting for it (another thread) is cut short by ending the job first; the section
    /// is unmapped only once that call has left, so nobody reads pixels from memory that is gone.
    /// </summary>
    public void Dispose()
    {
        if (!Monitor.TryEnter(_gate))
        {
            _dead = true;
            Native.TerminateJobObject(_process.Job, 1);
            Monitor.Enter(_gate);
        }
        try
        {
            if (!_dead)
            {
                try { DecoderProtocol.Write(_toDecoder, DecoderProtocol.Bare(DecoderMessage.Exit)); } catch (IOException) { }
                _dead = true;
            }
            DisposeResources();
        }
        finally { Monitor.Exit(_gate); }
    }

    private void DisposeResources()
    {
        if (_view != null) Native.UnmapViewOfFile((IntPtr)_view);
        _process?.Job.Dispose(); // KILL_ON_JOB_CLOSE: a decoder still running ends here
        _process?.Process.Dispose();
        _toDecoder.Dispose();
        _fromDecoder.Dispose();
        _section.Dispose();
    }

    /// <summary>A pagefile-backed section.</summary>
    private sealed class SafeSectionHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSectionHandle() : base(true) { }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);

        public static SafeSectionHandle Create(long bytes, bool inheritable)
        {
            var sa = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = inheritable };
            var h = Native.CreateFileMapping((IntPtr)(-1), ref sa, 0x04 /* PAGE_READWRITE */, (uint)(bytes >> 32), (uint)bytes, null);
            return h.IsInvalid ? throw new Win32Exception() : h;
        }

        public void SetInheritable(bool inheritable) => Native.SetHandleInformation(this, 1, inheritable ? 1u : 0u);
    }

    private static class Native
    {
        public const uint FILE_MAP_READ = 0x0004, FILE_MAP_WRITE = 0x0002, STILL_ACTIVE = 259;

        [StructLayout(LayoutKind.Sequential)] public struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeSectionHandle CreateFileMapping(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint high, uint low, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(SafeSectionHandle section, uint access, uint high, uint low, UIntPtr size);
        [DllImport("kernel32.dll")] public static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetHandleInformation(SafeSectionHandle h, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, SafeSectionHandle source, Microsoft.Win32.SafeHandles.SafeProcessHandle targetProcess,
            out IntPtr target, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool TerminateJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, uint code);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle h, out uint code);
        // times in 100 ns ticks
        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);
    }
}
