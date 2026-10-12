using System.Runtime.InteropServices;

namespace RemoteHub.Agent;

/// <summary>A clipboard change on the device other than text: an image (PNG or BMP bytes) or copied files.</summary>
/// <param name="Items">Received for a paste of virtual files: the local path of every item, in the transfer's order.</param>
internal sealed record HelperClipboard(byte[]? Image = null, bool Png = false, IReadOnlyList<string>? Files = null, IReadOnlyList<string>? Items = null);

/// <summary>Images and files on the device's clipboard (text is in the main part).</summary>
internal sealed unsafe partial class Win32HelperUi
{
    private const uint CF_DIB = 8, CF_HDROP = 15;
    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");
    private static readonly uint DropEffectFormat = RegisterClipboardFormatW("Preferred DropEffect");

    /// <summary>The clipboard as we left it: our own change comes back as WM_CLIPBOARDUPDATE and isn't sent back.</summary>
    private uint _ownSequence;

    public event Action<HelperClipboard>? ClipboardContent;

    /// <summary>
    /// An image from RemoteHub. A PNG goes on as "PNG" (Office, browsers) and as a bitmap (Paint and most other programs);
    /// a BMP only as the bitmap it is – never labelled PNG.
    /// </summary>
    public void SetClipboardImage(byte[] image, bool png) => Post(() =>
    {
        // A .bmp file is a 14-byte file header ("BM…") followed by the packed DIB the clipboard wants.
        var dib = png ? PngToDib(image) : image is [(byte)'B', (byte)'M', ..] && image.Length > 14 ? image[14..] : null;
        if (!png && dib is null) return;
        if (!OpenClipboardRetry()) return;
        try
        {
            EmptyClipboard();
            if (png) SetData(PngFormat, image);
            if (dib is not null) SetData(CF_DIB, dib);
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
    });

    /// <summary>Files copied from RemoteHub (already written on the device): pasted like files copied in Explorer.</summary>
    public void SetClipboardFiles(IReadOnlyList<string> paths) => Post(() =>
    {
        if (!OpenClipboardRetry()) return;
        try
        {
            EmptyClipboard();
            SetData(CF_HDROP, DropFiles(paths));
            SetData(DropEffectFormat, BitConverter.GetBytes(1)); // DROPEFFECT_COPY
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
    });

    /// <summary>CF_HDROP: a DROPFILES header, then the paths (UTF-16, each ending in \0, the list in another \0).</summary>
    private static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        var text = string.Concat(paths.Select(p => p + "\0")) + "\0";
        var drop = new byte[20 + text.Length * 2];
        fixed (byte* d = drop)
        {
            *(uint*)d = 20;          // DROPFILES.pFiles: the names follow the header
            *(int*)(d + 16) = 1;     // fWide: UTF-16
        }
        System.Text.Encoding.Unicode.GetBytes(text).CopyTo(drop, 20);
        return drop;
    }

    // ---- offered, sent when pasted (delayed rendering)

    private Func<string, HelperClipboard?>? _offer;
    private HelperClipboard? _rendered;

    /// <summary>
    /// Copied in RemoteHub: the clipboard offers files (or an image) without their data. Pasting asks Windows for it
    /// (WM_RENDERFORMAT); then <paramref name="fetch"/> gets it from RemoteHub – the paste waits for it – and the
    /// clipboard keeps it for further pastes. Nothing is copied if nobody pastes.
    /// </summary>
    public void OfferClipboard(DesktopTransfer offer, Func<string, HelperClipboard?> fetch) => Post(() =>
    {
        VirtualFiles.Release(_virtual);
        _virtual = 0;
        if (offer.Kind == DesktopTransferKind.Files)
        {
            // Virtual files: Explorer reads the list when the clipboard changes; the contents only when it pastes.
            _offer = null;
            _virtual = VirtualFiles.Set(offer, () => fetch("a program reading the files")?.Items);
            _ownSequence = GetClipboardSequenceNumber();
            return;
        }
        // An image: offered by format, rendered when asked for (WM_RENDERFORMAT).
        if (!OpenClipboardRetry()) return;
        try
        {
            EmptyClipboard(); // the previous offer (ours too) ends here – WM_DESTROYCLIPBOARD
            (_offer, _rendered) = (fetch, null);
            SetClipboardData(PngFormat, IntPtr.Zero);
            SetClipboardData(CF_DIB, IntPtr.Zero);
        }
        finally
        {
            CloseClipboard();
            _ownSequence = GetClipboardSequenceNumber();
        }
    });

    /// <summary>The virtual files offered last (a reference to the data object), or 0.</summary>
    private nint _virtual;

    /// <summary>WM_RENDERFORMAT: someone pastes – the data now (the clipboard is already open for us).</summary>
    private void Render(uint format)
    {
        if (_offer is not { } fetch) return;
        _rendered ??= fetch(Paster());
        if (_rendered is not { } r) return; // not arrived: the paste gets nothing
        if (format == CF_HDROP && r.Files is { } files) SetData(CF_HDROP, DropFiles(files));
        else if (format == PngFormat && r is { Image: { } png, Png: true }) SetData(PngFormat, png);
        else if (format == CF_DIB && r.Image is { } image &&
                 (r.Png ? PngToDib(image) : image is [(byte)'B', (byte)'M', ..] && image.Length > 14 ? image[14..] : null) is { } dib)
            SetData(CF_DIB, dib);
    }

    /// <summary>The program whose paste asks for the data (the window that opened the clipboard), e.g. "explorer (4321)".</summary>
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

    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>Our offer left the clipboard (someone copied something else): nothing to fetch any more.</summary>
    private void OfferEnded() => (_offer, _rendered) = (null, null);

    private static void SetData(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(0x0002, (nuint)Math.Max(1, bytes.Length)); // GMEM_MOVEABLE
        if (memory == IntPtr.Zero) return;
        var p = GlobalLock(memory);
        Marshal.Copy(bytes, 0, (IntPtr)p, bytes.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == IntPtr.Zero) GlobalFree(memory);
    }

    /// <summary>The clipboard holds no text: an image or files? (clipboard is open)</summary>
    private HelperClipboard? ReadOther()
    {
        if (IsClipboardFormatAvailable(PngFormat) && Read(PngFormat) is { } png) return new HelperClipboard(png, Png: true);
        if (IsClipboardFormatAvailable(CF_DIB) && Read(CF_DIB) is { } dib && DibToBmp(dib) is { } bmp) return new HelperClipboard(bmp);
        if (IsClipboardFormatAvailable(CF_HDROP))
        {
            var h = GetClipboardData(CF_HDROP);
            if (h == IntPtr.Zero) return null;
            var files = new List<string>();
            var count = DragQueryFileW(h, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count && i < DesktopProtocol.MaxTransferItems; i++)
            {
                var length = DragQueryFileW(h, i, null, 0);
                var buffer = new char[length + 1];
                fixed (char* b = buffer) DragQueryFileW(h, i, b, length + 1);
                files.Add(new string(buffer, 0, (int)length));
            }
            return files.Count > 0 ? new HelperClipboard(Files: files) : null;
        }
        return null;
    }

    private static byte[]? Read(uint format)
    {
        var data = GetClipboardData(format);
        if (data == IntPtr.Zero) return null;
        var size = (long)GlobalSize(data);
        if (size <= 0 || size > DesktopProtocol.MaxTransferBytes) return null;
        var p = GlobalLock(data);
        if (p == null) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy((IntPtr)p, bytes, 0, (int)size);
            return bytes;
        }
        finally { GlobalUnlock(data); }
    }

    /// <summary>A packed DIB (CF_DIB) as a .bmp file: the file header in front, nothing converted.</summary>
    internal static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length) return null;
        int bits = BitConverter.ToUInt16(dib, 14), compression = headerSize >= 20 ? BitConverter.ToInt32(dib, 16) : 0;
        var colors = headerSize >= 36 ? BitConverter.ToInt32(dib, 32) : 0;
        var table = bits <= 8 ? (colors != 0 ? colors : 1 << bits) * 4 : colors * 4;
        if (compression == 3 && headerSize == 40) table += 12; // BI_BITFIELDS masks after a plain header
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(14 + headerSize + table).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    /// <summary>A PNG as a 32-bit packed DIB (bottom-up), through GDI+; null if it can't be read.</summary>
    private static byte[]? PngToDib(byte[] png)
    {
        var memory = GlobalAlloc(0x0002, (nuint)png.Length);
        if (memory == IntPtr.Zero) return null;
        var p = GlobalLock(memory);
        Marshal.Copy(png, 0, (IntPtr)p, png.Length);
        GlobalUnlock(memory);
        if (CreateStreamOnHGlobal(memory, true, out var stream) < 0) { GlobalFree(memory); return null; }
        nint bitmap = 0;
        try
        {
            if (GdipCreateBitmapFromStream(stream, out bitmap) != 0) return null;
            GdipGetImageWidth(bitmap, out var w);
            GdipGetImageHeight(bitmap, out var h);
            if (w == 0 || h == 0 || (long)w * h * 4 > DesktopProtocol.MaxTransferBytes) return null;
            var rect = new GpRect { Width = (int)w, Height = (int)h };
            BitmapData data;
            if (GdipBitmapLockBits(bitmap, &rect, 1 /* read */, 0x26200A /* 32bpp ARGB */, &data) != 0) return null;
            try
            {
                var dib = new byte[40 + w * h * 4];
                fixed (byte* d = dib)
                {
                    *(int*)d = 40;
                    *(int*)(d + 4) = (int)w;
                    *(int*)(d + 8) = (int)h;         // positive: bottom-up
                    *(ushort*)(d + 12) = 1;
                    *(ushort*)(d + 14) = 32;
                    *(uint*)(d + 20) = w * h * 4;
                    for (var y = 0; y < h; y++)
                        Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, d + 40 + (long)(h - 1 - y) * w * 4, w * 4, w * 4);
                }
                return dib;
            }
            finally { GdipBitmapUnlockBits(bitmap, &data); }
        }
        finally
        {
            if (bitmap != 0) GdipDisposeImage(bitmap);
            Marshal.Release(stream);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct GpRect { public int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapData { public uint Width, Height; public int Stride, PixelFormat; public IntPtr Scan0, Reserved; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr drop, uint index, char* file, uint length);
    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, bool deleteOnRelease, out IntPtr stream);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromStream(IntPtr stream, out nint bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageWidth(nint image, out uint width);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageHeight(nint image, out uint height);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapLockBits(nint bitmap, GpRect* rect, uint flags, int format, BitmapData* data);
    [DllImport("gdiplus.dll")] private static extern int GdipBitmapUnlockBits(nint bitmap, BitmapData* data);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(nint image);
}
