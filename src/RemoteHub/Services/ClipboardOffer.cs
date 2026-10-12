using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteHub.Agent;

namespace RemoteHub.Services;

/// <summary>What a paste gets: files already on this PC (top level, and every item in the transfer's order), or an image (PNG or BMP bytes).</summary>
public sealed record OfferedContent(IReadOnlyList<string>? Files = null, byte[]? Image = null, bool Png = false, IReadOnlyList<string>? Items = null);

/// <summary>
/// Files or an image copied on a device, offered on this PC's clipboard without their data (delayed rendering): only a
/// paste asks for it (WM_RENDERFORMAT) – then the device sends it and the paste waits. Nothing moves if nobody pastes.
/// The clipboard is owned by a hidden window on a thread of its own, so a waiting paste never blocks RemoteHub's window.
/// </summary>
public sealed class ClipboardOffer
{
    private const int WM_RENDERFORMAT = 0x0305, WM_RENDERALLFORMATS = 0x0306, WM_DESTROYCLIPBOARD = 0x0307;
    private const uint CF_DIB = 8, CF_HDROP = 15;
    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");
    private static readonly uint DropEffectFormat = RegisterClipboardFormatW("Preferred DropEffect");

    private static readonly Lazy<ClipboardOffer> Shared = new(() => new ClipboardOffer());
    public static ClipboardOffer Instance => Shared.Value;

    private readonly Dispatcher _dispatcher;
    private HwndSource _window = null!;
    private Func<string, OfferedContent?>? _fetch;
    private OfferedContent? _rendered;

    private ClipboardOffer()
    {
        var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            _threadId = GetCurrentThreadId();
            _window = new HwndSource(new HwndSourceParameters("RemoteHub clipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 });
            _window.AddHook(Hook);
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Clipboard offer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    /// <summary>This PC's clipboard holds our offer (or what a paste of it rendered) – RemoteHub mustn't send it back.</summary>
    public bool OwnsClipboard => GetClipboardOwner() is var owner && owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out _) == _threadId;

    /// <summary>This class's thread: it owns the clipboard through its own window or the one OLE makes for the virtual files.</summary>
    private uint _threadId;

    private nint _virtual;

    /// <summary>
    /// Offers files (as virtual files: the list now, the contents when pasted) or an image (rendered when asked for);
    /// <paramref name="fetch"/> runs when something pastes (on this class's thread; its argument names the program,
    /// e.g. "explorer (4321)") and returns what arrived, or null.
    /// </summary>
    public void Offer(RemoteHub.Agent.DesktopTransfer offer, Func<string, OfferedContent?> fetch) => _dispatcher.BeginInvoke(() =>
    {
        VirtualFiles.Release(_virtual);
        _virtual = 0;
        if (offer.Kind == RemoteHub.Agent.DesktopTransferKind.Files)
        {
            (_fetch, _rendered) = (null, null);
            _virtual = VirtualFiles.Set(offer, () => fetch("a program reading the files")?.Items);
            return;
        }
        if (!OpenClipboardRetry()) return;
        try
        {
            EmptyClipboard(); // the previous offer ends here (WM_DESTROYCLIPBOARD)
            (_fetch, _rendered) = (fetch, null);
            SetClipboardData(PngFormat, IntPtr.Zero);
            SetClipboardData(CF_DIB, IntPtr.Zero);
        }
        finally { CloseClipboard(); }
    });

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_RENDERFORMAT:
                handled = true;
                try { Render((uint)wParam); } catch { /* the paste gets nothing */ }
                break;
            case WM_RENDERALLFORMATS: // RemoteHub closes: an offer nobody pasted is gone, nothing is fetched
                handled = true;
                break;
            case WM_DESTROYCLIPBOARD:
                (_fetch, _rendered) = (null, null);
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void Render(uint format)
    {
        if (_fetch is not { } fetch) return;
        _rendered ??= fetch(Paster());
        if (_rendered is not { } r) return;
        if (format == CF_HDROP && r.Files is { } files) SetData(CF_HDROP, DropFiles(files));
        else if (format == PngFormat && r is { Image: { } png, Png: true }) SetData(PngFormat, png);
        else if (format == CF_DIB && r.Image is { } image && (r.Png ? PngToDib(image) : image is [(byte)'B', (byte)'M', ..] && image.Length > 14 ? image[14..] : null) is { } dib)
            SetData(CF_DIB, dib);
    }

    /// <summary>
    /// Programs that read every new clipboard entry at once – RDP's clipboard sync, Windows' clipboard history – not a
    /// paste: they get nothing (the device's agent does the same).
    /// </summary>
    public static bool BackgroundReader(string paster) =>
        paster.StartsWith("rdpclip ", StringComparison.OrdinalIgnoreCase) || paster.StartsWith("svchost ", StringComparison.OrdinalIgnoreCase);

    private static string Paster()
    {
        try
        {
            var window = GetOpenClipboardWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return "unknown";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return $"{p.ProcessName} ({pid})";
        }
        catch { return "unknown"; }
    }

    private bool OpenClipboardRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(_window.Handle)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>CF_HDROP: a DROPFILES header, then the paths (UTF-16, each ending in \0, the list in another \0).</summary>
    internal static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var text = string.Concat(paths.Select(p => p + "\0")) + "\0";
        var drop = new byte[20 + text.Length * 2];
        BitConverter.GetBytes(20).CopyTo(drop, 0); // DROPFILES.pFiles: the names follow the header
        BitConverter.GetBytes(1).CopyTo(drop, 16);  // fWide: UTF-16
        System.Text.Encoding.Unicode.GetBytes(text).CopyTo(drop, 20);
        return drop;
    }

    /// <summary>A PNG as a 32-bit packed DIB (bottom-up); null if it can't be read.</summary>
    internal static byte[]? PngToDib(byte[] png)
    {
        try
        {
            var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);
            var dib = new byte[40 + pixels.Length];
            BitConverter.GetBytes(40).CopyTo(dib, 0);
            BitConverter.GetBytes(w).CopyTo(dib, 4);
            BitConverter.GetBytes(h).CopyTo(dib, 8);   // positive: bottom-up
            BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
            BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
            BitConverter.GetBytes(pixels.Length).CopyTo(dib, 20);
            for (var y = 0; y < h; y++) Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
            return dib;
        }
        catch { return null; }
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(0x0002, (UIntPtr)Math.Max(1, bytes.Length)); // GMEM_MOVEABLE
        if (memory == IntPtr.Zero) return;
        Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
