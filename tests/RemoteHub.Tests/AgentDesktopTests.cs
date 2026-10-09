using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>Remote desktop through the agent: wire format and video codec.</summary>
public class AgentDesktopTests
{
    private static unsafe byte[] TestImage(int w, int h, int shift)
    {
        var img = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                // Smooth gradient plus sharp "text" stripes.
                var stripe = ((x + shift) / 3 % 7 == 0 && y % 20 < 12) ? 0 : 1;
                img[i] = (byte)(stripe * (x * 255 / w));
                img[i + 1] = (byte)(stripe * (y * 255 / h));
                img[i + 2] = (byte)(stripe * 200);
                img[i + 3] = 255;
            }
        return img;
    }

    private static double MeanError(byte[] a, byte[] b, int pixels)
    {
        long sum = 0;
        for (var i = 0; i < pixels * 4; i++) if (i % 4 != 3) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)(pixels * 3);
    }

    [Fact]
    public unsafe void Video_roundtrip_keeps_the_picture()
    {
        const int w = 1280, h = 720;
        using var encoder = new H264Encoder(w, h, 30, 70);
        using var decoder = new H264Decoder();
        var decoded = new byte[w * h * 4];
        var frames = 0;
        var keyFrames = 0;
        var sizes = new List<int>();
        var sw = Stopwatch.StartNew();
        for (var n = 0; n < 10; n++)
        {
            var image = TestImage(w, h, n < 5 ? 0 : n);
            byte[]? au;
            bool key;
            fixed (byte* p = image) au = encoder.Encode(p, w * 4, n == 0, out key);
            Assert.NotNull(au);
            sizes.Add(au!.Length);
            if (key) keyFrames++;
            decoder.Decode(au, w, h, (bgra, fw, fh) =>
            {
                Assert.Equal((w, h), (fw, fh));
                new ReadOnlySpan<byte>(bgra, fw * fh * 4).CopyTo(decoded);
                frames++;
            });
            if (frames > 0 && n is 4 or 9) Assert.True(MeanError(image, decoded, w * h) < 6, $"error {MeanError(image, decoded, w * h):0.0} at frame {n}");
        }
        Assert.Equal(10, frames); // low-latency: one picture out per access unit, nothing held back
        Assert.Equal(1, keyFrames);
        Assert.True(sizes[1] < sizes[0] / 4, $"unchanged frame not small: {string.Join(",", sizes)}");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public unsafe void A_requested_key_frame_is_marked()
    {
        using var encoder = new H264Encoder(320, 240, 15, 50);
        var image = TestImage(320, 240, 0);
        fixed (byte* p = image)
        {
            encoder.Encode(p, 320 * 4, true, out var first);
            encoder.Encode(p, 320 * 4, false, out var second);
            encoder.Encode(p, 320 * 4, true, out var third);
            Assert.True(first);
            Assert.False(second);
            Assert.True(third);
        }
    }

    [Fact]
    public void Wire_messages_roundtrip()
    {
        var v = DesktopWire.Video(new VideoHeader(7, 1920, 1080, true), [1, 2, 3]);
        Assert.Equal(DesktopMessage.Video, DesktopWire.TypeOf(v));
        Assert.Equal(new VideoHeader(7, 1920, 1080, true), DesktopWire.ReadVideo(v));
        Assert.Equal([1, 2, 3], v[DesktopWire.VideoHeaderSize..]);
        Assert.Equal(42u, DesktopWire.ReadAck(DesktopWire.Ack(42)));
        var mouse = new RemoteMouse(RemoteMouseAction.Wheel, RemoteMouseButton.None, -5, 3000, -120);
        Assert.Equal(mouse, DesktopWire.ReadMouse(DesktopWire.Mouse(mouse)));
        var key = new RemoteKey(0x5B, RemoteKeyFlags.Extended | RemoteKeyFlags.Up);
        Assert.Equal(key, DesktopWire.ReadKey(DesktopWire.Key(key)));
        Assert.Equal(new RemoteCursor(10, -20, true), DesktopWire.ReadCursor(DesktopWire.Cursor(new RemoteCursor(10, -20, true))));
        var shape = DesktopWire.CursorShape(2, 1, 1, 0, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal((2, 1, 1, 0), DesktopWire.ReadCursorShape(shape));
        Assert.Equal("Grüße", DesktopWire.ReadText(DesktopWire.Text(DesktopMessage.Clipboard, "Grüße")));
        var start = new DesktopStart(3, DesktopConsent.Pin, true, 30, 60, 1);
        Assert.Equal(start, DesktopWire.ReadJson(DesktopWire.Json(DesktopMessage.Start, start, DesktopJson.Default.DesktopStart), DesktopJson.Default.DesktopStart));
    }

    [Fact]
    public async Task Framed_messages_on_a_stream()
    {
        using var ms = new MemoryStream();
        await DesktopWire.WriteFramedAsync(ms, [1, 2], default);
        await DesktopWire.WriteFramedAsync(ms, [3], default);
        ms.Position = 0;
        Assert.Equal([1, 2], await DesktopWire.ReadFramedAsync(ms, default));
        Assert.Equal([3], await DesktopWire.ReadFramedAsync(ms, default));
        Assert.Null(await DesktopWire.ReadFramedAsync(ms, default));
    }

    [Fact]
    public void Idr_detection()
    {
        Assert.True(H264.ContainsIdr([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x65, 9]));
        Assert.False(H264.ContainsIdr([0, 0, 0, 1, 0x41, 1, 2, 3]));
    }
}
