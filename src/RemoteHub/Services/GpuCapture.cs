using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace RemoteHub.Services;

/// <summary>
/// Captures a window (or a monitor) through Windows.Graphics.Capture: the composed image straight from the GPU, also
/// when other windows cover it, with the mouse pointer. Fast enough for 30–60 fps at 4K, where GDI needs ~70 ms per
/// frame. Only the requested rectangle is copied to memory. Windows delivers frames only when something changes, so
/// the newest frame is kept on the GPU and cropped from there every time.
/// </summary>
internal sealed unsafe class GpuCapture : IDisposable
{
    private const int B8G8R8A8 = 87;
    private IntPtr _device, _context;
    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IntPtr _target;
    private SizeInt32 _poolSize;
    private IntPtr _cache, _staging;
    private int _cacheW, _cacheH, _stagingW, _stagingH;

    /// <summary>Null when this Windows version or GPU can't do it (the caller then uses GDI).</summary>
    public static GpuCapture? TryCreate()
    {
        try { return GraphicsCaptureSession.IsSupported() ? new GpuCapture() : null; }
        catch (Exception ex)
        {
            DiagLog.Write("GPU capture not available: " + ex.Message);
            return null;
        }
    }

    private GpuCapture()
    {
        const int bgraSupport = 0x20;
        var hr = D3D.D3D11CreateDevice(IntPtr.Zero, 1 /* hardware */, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0, 7, out _device, out _, out _context);
        if (hr < 0) hr = D3D.D3D11CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0, 7, out _device, out _, out _context);
        Mf.Check(hr, "Direct3D isn't available");
        // Windows' capture threads use the device too.
        if (Marshal.QueryInterface(_device, in D3D.IID_ID3D10Multithread, out var mt) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Mf.Slot(mt, 5))(mt, 1); // SetMultithreadProtected(TRUE)
            Marshal.Release(mt);
        }
        Mf.Check(Marshal.QueryInterface(_device, in D3D.IID_IDXGIDevice, out var dxgi), "IDXGIDevice");
        try
        {
            Mf.Check(D3D.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
            _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }
        finally { Marshal.Release(dxgi); }
    }

    /// <summary>
    /// Copies a rectangle of the newest image of a window (x/y from the window's top-left as GetWindowRect reports it)
    /// or of a monitor (screen coordinates) and hands it to <paramref name="use"/> (pixels, row pitch, width, height).
    /// False while no image arrived yet.
    /// </summary>
    public bool TryCopy(RecordingSource source, Action<IntPtr, int, int, int> use)
    {
        int x = source.X, y = source.Y;
        IntPtr target;
        if (source.Screen)
        {
            target = Gdi.MonitorFromPoint(new Gdi.POINT { X = source.X + source.Width / 2, Y = source.Y + source.Height / 2 }, 2);
            var info = new Gdi.MONITORINFO { cbSize = sizeof(Gdi.MONITORINFO) };
            if (!Gdi.GetMonitorInfo(target, ref info)) return false;
            (x, y) = (x - info.rcMonitor.Left, y - info.rcMonitor.Top);
        }
        else
        {
            target = source.Window;
            // The captured image starts at the visible frame (without the invisible resize border).
            if (Gdi.GetWindowRect(target, out var wr) && D3D.DwmGetWindowAttribute(target, 9, out var visible, sizeof(Gdi.RECT)) >= 0)
                (x, y) = (x + wr.Left - visible.Left, y + wr.Top - visible.Top);
        }
        if (target != _target) Open(target, source.Screen);

        TakeNewestFrame();
        if (_cache == IntPtr.Zero) return false;

        // Only the part of the rectangle that lies inside the image.
        int left = Math.Clamp(x, 0, _cacheW), top = Math.Clamp(y, 0, _cacheH);
        int right = Math.Clamp(x + source.Width, 0, _cacheW), bottom = Math.Clamp(y + source.Height, 0, _cacheH);
        int w = right - left, h = bottom - top;
        if (w <= 0 || h <= 0) return false;

        _staging = EnsureTexture(_staging, ref _stagingW, ref _stagingH, w, h, staging: true);
        var box = new D3D.BOX { left = (uint)left, top = (uint)top, front = 0, right = (uint)right, bottom = (uint)bottom, back = 1 };
        CopyRegion(_staging, _cache, &box);
        D3D.MAPPED map;
        Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D.MAPPED*, int>)Mf.Slot(_context, 14))(_context, _staging, 0, 1 /* READ */, 0, &map), "Map");
        try { use(map.pData, (int)map.RowPitch, w, h); }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Mf.Slot(_context, 15))(_context, _staging, 0); }
        return true;
    }

    private void Open(IntPtr target, bool monitor)
    {
        CloseSession();
        _target = target;
        _item = CreateItem(target, monitor);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _session = _pool.CreateCaptureSession(_item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            try { _session.IsCursorCaptureEnabled = true; } catch { /* no pointer in the video */ }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            try { _session.IsBorderRequired = false; } catch { /* not allowed: a yellow frame shows what is captured */ }
        _session.StartCapture();
    }

    /// <summary>Moves the newest delivered frame (if any) into the cache texture.</summary>
    private void TakeNewestFrame()
    {
        if (_pool is null) return;
        Direct3D11CaptureFrame? frame = null;
        while (_pool.TryGetNextFrame() is { } next)
        {
            frame?.Dispose();
            frame = next;
        }
        if (frame is null) return;
        using (frame)
        {
            var size = frame.ContentSize;
            var texture = TextureOf(frame.Surface);
            try
            {
                int w = Math.Min(size.Width, _poolSize.Width), h = Math.Min(size.Height, _poolSize.Height);
                if (w > 0 && h > 0)
                {
                    _cache = EnsureTexture(_cache, ref _cacheW, ref _cacheH, w, h, staging: false);
                    var box = new D3D.BOX { right = (uint)w, bottom = (uint)h, back = 1 };
                    CopyRegion(_cache, texture, &box);
                }
            }
            finally { Marshal.Release(texture); }
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // The window changed size: new buffers for the next frames.
                _poolSize = size;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
    }

    private void CopyRegion(IntPtr dst, IntPtr src, D3D.BOX* box) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D.BOX*, void>)Mf.Slot(_context, 46))(_context, dst, 0, 0, 0, 0, src, 0, box);

    private IntPtr EnsureTexture(IntPtr texture, ref int width, ref int height, int w, int h, bool staging)
    {
        if (texture != IntPtr.Zero && width == w && height == h) return texture;
        Mf.Release(texture);
        var desc = new D3D.TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = B8G8R8A8, SampleCount = 1,
            Usage = staging ? 3u : 0u, CPUAccessFlags = staging ? 0x20000u : 0u,
        };
        IntPtr created;
        Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, D3D.TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Mf.Slot(_device, 5))(_device, &desc, IntPtr.Zero, &created), "CreateTexture2D");
        (width, height) = (w, h);
        return created;
    }

    private static IntPtr TextureOf(IDirect3DSurface surface)
    {
        var abi = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Mf.Check(Marshal.QueryInterface(abi, in D3D.IID_IDirect3DDxgiInterfaceAccess, out var access), "IDirect3DDxgiInterfaceAccess");
            try
            {
                var iid = D3D.IID_ID3D11Texture2D;
                IntPtr texture;
                Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Mf.Slot(access, 3))(access, &iid, &texture), "GetInterface");
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(abi); }
    }

    private static GraphicsCaptureItem CreateItem(IntPtr target, bool monitor)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Mf.Check(D3D.WindowsCreateString(name, name.Length, out var hstring), "WindowsCreateString");
        try
        {
            var iidInterop = D3D.IID_IGraphicsCaptureItemInterop;
            Mf.Check(D3D.RoGetActivationFactory(hstring, ref iidInterop, out var interop), "GraphicsCaptureItem factory");
            try
            {
                var iidItem = D3D.IID_IGraphicsCaptureItem;
                IntPtr item;
                // CreateForWindow = slot 3, CreateForMonitor = slot 4
                Mf.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Mf.Slot(interop, monitor ? 4 : 3))(interop, target, &iidItem, &item),
                    monitor ? "This monitor can't be captured" : "This window can't be captured");
                try { return GraphicsCaptureItem.FromAbi(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(interop); }
        }
        finally { D3D.WindowsDeleteString(hstring); }
    }

    private void CloseSession()
    {
        _session?.Dispose();
        _pool?.Dispose();
        (_session, _pool, _item, _target) = (null, null, null, IntPtr.Zero);
        Mf.Release(_cache);
        _cache = IntPtr.Zero;
        _cacheW = _cacheH = 0;
    }

    public void Dispose()
    {
        CloseSession();
        Mf.Release(_staging);
        _staging = IntPtr.Zero;
        (_winrtDevice as IDisposable)?.Dispose();
        Mf.Release(_context);
        Mf.Release(_device);
        _context = _device = IntPtr.Zero;
    }
}

/// <summary>Scales BGRA images: area average when shrinking (keeps text readable), bilinear when enlarging. Parallel.</summary>
internal static unsafe class FrameScaler
{
    public static void Scale(byte* src, int srcPitch, int sw, int sh, byte* dst, int dstPitch, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            for (var y = 0; y < sh; y++) Buffer.MemoryCopy(src + (long)y * srcPitch, dst + (long)y * dstPitch, dw * 4, dw * 4);
            return;
        }
        if (dw <= sw && dh <= sh) Shrink(src, srcPitch, sw, sh, dst, dstPitch, dw, dh);
        else Bilinear(src, srcPitch, sw, sh, dst, dstPitch, dw, dh);
    }

    private static void Shrink(byte* src, int srcPitch, int sw, int sh, byte* dst, int dstPitch, int dw, int dh)
    {
        var x0 = new int[dw + 1];
        for (var x = 0; x <= dw; x++) x0[x] = (int)((long)x * sw / dw);
        long s = (long)src, d = (long)dst;
        Parallel.For(0, dh, y =>
        {
            int yStart = (int)((long)y * sh / dh), yEnd = Math.Max(yStart + 1, (int)((long)(y + 1) * sh / dh));
            var row = (uint*)(d + (long)y * dstPitch);
            for (var x = 0; x < dw; x++)
            {
                int xStart = x0[x], xEnd = Math.Max(xStart + 1, x0[x + 1]);
                uint b = 0, g = 0, r = 0;
                for (var sy = yStart; sy < yEnd; sy++)
                {
                    var p = (byte*)(s + (long)sy * srcPitch) + xStart * 4;
                    for (var sx = xStart; sx < xEnd; sx++, p += 4) { b += p[0]; g += p[1]; r += p[2]; }
                }
                var n = (uint)((xEnd - xStart) * (yEnd - yStart));
                row[x] = 0xFF000000 | ((r / n) << 16) | ((g / n) << 8) | (b / n);
            }
        });
    }

    private static void Bilinear(byte* src, int srcPitch, int sw, int sh, byte* dst, int dstPitch, int dw, int dh)
    {
        var xi = new int[dw];
        var xf = new int[dw];
        for (var x = 0; x < dw; x++)
        {
            var pos = Math.Clamp((x + 0.5) * sw / dw - 0.5, 0, sw - 1);
            xi[x] = Math.Min((int)pos, sw - 2 < 0 ? 0 : sw - 2);
            xf[x] = (int)((pos - xi[x]) * 256);
        }
        long s = (long)src, d = (long)dst;
        Parallel.For(0, dh, y =>
        {
            var pos = Math.Clamp((y + 0.5) * sh / dh - 0.5, 0, sh - 1);
            var y0 = Math.Min((int)pos, Math.Max(0, sh - 2));
            var fy = (int)((pos - y0) * 256);
            var r0 = (byte*)(s + (long)y0 * srcPitch);
            var r1 = (byte*)(s + (long)Math.Min(y0 + 1, sh - 1) * srcPitch);
            var row = (uint*)(d + (long)y * dstPitch);
            for (var x = 0; x < dw; x++)
            {
                int i = xi[x] * 4, j = Math.Min(xi[x] + 1, sw - 1) * 4, fx = xf[x];
                uint c = 0xFF000000;
                for (var k = 0; k < 3; k++)
                {
                    var top = r0[i + k] * (256 - fx) + r0[j + k] * fx;
                    var bottom = r1[i + k] * (256 - fx) + r1[j + k] * fx;
                    c |= (uint)((top * (256 - fy) + bottom * fy) >> 16) << (k * 8);
                }
                row[x] = c;
            }
        });
    }
}

internal static class D3D
{
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    public static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    public static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    [StructLayout(LayoutKind.Sequential)] public struct BOX { public uint left, top, front, right, bottom, back; }
    [StructLayout(LayoutKind.Sequential)] public struct MAPPED { public IntPtr pData; public uint RowPitch, DepthPitch; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags, IntPtr featureLevels, int levels, int sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")] public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Gdi.RECT value, int size);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] public static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] public static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
}
