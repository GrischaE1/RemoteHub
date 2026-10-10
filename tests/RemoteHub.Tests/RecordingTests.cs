using System.Threading.Tasks;
using RemoteHub.Services;
using Xunit;

namespace RemoteHub.Tests;

public class RecordingTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1281, 721, 1280, 720)]   // H.264 needs even sizes
    [InlineData(7680, 2160, 3840, 1080)] // capped at 4K, same aspect
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(10, 10, 64, 64)]
    public void OutputSize_IsEvenAndCapped(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), SessionRecorder.OutputSize(w, h));

    [Theory]
    [InlineData(RecordingResolution.FullHd, 1920, 1080)]
    [InlineData(RecordingResolution.Uhd4K, 3840, 2160)]
    public void OutputSize_FixedPresentationSizes(RecordingResolution resolution, int ew, int eh)
    {
        Assert.Equal((ew, eh), SessionRecorder.OutputSize(1000, 900, resolution));
        Assert.Equal((ew, eh), SessionRecorder.OutputSize(5000, 1000, resolution));
    }

    [Fact]
    public void Options_FallBackToDefaultsForUnknownValues()
    {
        Assert.Equal(new RecordingOptions(30, RecordingResolution.Uhd4K), Recordings.Options(new Models.UiState { RecordingFps = 30, RecordingResolution = "Uhd4K" }));
        Assert.Equal(RecordingOptions.Default, Recordings.Options(new Models.UiState { RecordingFps = 7, RecordingResolution = "8K" }));
        Assert.Equal(RecordingOptions.Default, Recordings.Options(new Models.UiState()));
    }

    [Fact]
    public void Fit_LetterboxesWhenTheAspectChanges()
    {
        Assert.Equal((0, 0, 1280, 720), SessionRecorder.Fit(1280, 720, 1280, 720));
        Assert.Equal((0, 0, 1280, 720), SessionRecorder.Fit(2560, 1440, 1280, 720));
        Assert.Equal((320, 0, 640, 720), SessionRecorder.Fit(800, 900, 1280, 720));
    }

    [Fact]
    public void FileName_RemovesInvalidCharacters()
    {
        var name = Recordings.FileName("srv:01/a*b", "Remote desktop", new DateTime(2026, 10, 8, 14, 5, 9));
        Assert.Equal("srv_01_a_b - Remote desktop - 2026-10-08 14-05-09.mp4", name);
    }

    [Fact]
    public void UniquePath_AddsANumber()
    {
        var dir = Directory.CreateTempSubdirectory("rh-rec-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            Assert.Equal(Path.Combine(dir, "a (2).mp4"), Recordings.UniquePath(dir, "a.mp4"));
            Assert.Equal(Path.Combine(dir, "b.mp4"), Recordings.UniquePath(dir, "b.mp4"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Elapsed_Format()
    {
        Assert.Equal("00:05", Recordings.Elapsed(TimeSpan.FromSeconds(5)));
        Assert.Equal("12:34", Recordings.Elapsed(new TimeSpan(0, 12, 34)));
        Assert.Equal("1:02:07", Recordings.Elapsed(new TimeSpan(1, 2, 7)));
    }

    /// <summary>Encodes red-over-blue frames and decodes the file again: playable, right way up, right length.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)] // not closed (crash, power loss): fragmented MP4 still plays
    public async Task Mp4Writer_WritesAPlayableVideo(bool fragmented, bool finish)
    {
        var dir = Directory.CreateTempSubdirectory("rh-rec-").FullName;
        var path = Path.Combine(dir, "test.mp4");
        try
        {
            const int w = 320, h = 240, fps = 10;
            var frames = finish ? 20 : 300; // fragments are written every few seconds
            var bits = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (y < h / 2) { bits[i + 2] = 255; } // BGRA: red on top
                    else { bits[i] = 255; }                // blue below
                }
            unsafe
            {
                using var writer = new Mp4Writer(path, w, h, fps, SessionRecorder.BitRate(w, h, fps), fragmented);
                fixed (byte* p = bits)
                    for (var f = 0; f < frames; f++) writer.Write(p, f * TimeSpan.TicksPerSecond / fps, TimeSpan.TicksPerSecond / fps);
                if (finish) writer.Finish();
            }
            Assert.True(new FileInfo(path).Length > 1000, $"length {new FileInfo(path).Length}");

            if (!finish)
            {
                // Players open it through the Media Foundation pipeline (the editing API below wants a closed file).
                var source = Windows.Media.Core.MediaSource.CreateFromStorageFile(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                await source.OpenAsync();
                Assert.True(source.IsOpen);
                source.Dispose();
                return;
            }
            var (duration, top, bottom) = await Decode(path, TimeSpan.FromSeconds(1));
            Assert.InRange(duration.TotalSeconds, 1.8, 2.2);
            Assert.True(top.R > 200 && top.B < 60, $"top {top}");
            Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom {bottom}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The whole recorder with a screen area and a hidden phase (placeholder) – stops cleanly with a playable file.</summary>
    [Fact]
    public async Task Recorder_RecordsTheScreenAndStops()
    {
        var dir = Directory.CreateTempSubdirectory("rh-rec-").FullName;
        var path = Path.Combine(dir, "screen.mp4");
        try
        {
            var recorder = new SessionRecorder(path, new RecordingSource(IntPtr.Zero, 0, 0, 640, 360, Screen: true));
            recorder.Start();
            await Task.Delay(800);
            recorder.Update(RecordingSource.Hidden);
            await Task.Delay(500);
            recorder.Stop();
            await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var (duration, _, _) = await Decode(path, TimeSpan.Zero);
            // 1.3 s recorded: the video ends where Stop was called. Lower bound loose – starting the encoder can take a
            // while on a busy machine, and the clock starts after it.
            Assert.InRange(duration.TotalSeconds, 0.5, 2.0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Window capture includes hosted child windows (the remote desktop control is one): a WPF window with a WPF area on
    /// top and a WinForms panel below, partly covered by another window – both halves land in the frame at the right offset.
    /// GPU capture (Windows.Graphics.Capture) and the GDI fallback; borderless and with a normal window frame.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void FrameGrabber_CapturesHostedChildWindowsAndCoveredContent(bool gpu, bool bordered)
    {
        // GPU capture talks to the graphics driver: only when asked for (a test run on a dual-GPU laptop ended in a bluescreen).
        if (gpu && Environment.GetEnvironmentVariable("REMOTEHUB_GPU_TESTS") != "1") return;
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { CaptureWindow(gpu, bordered); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static unsafe void CaptureWindow(bool gpu, bool bordered)
    {
        var grid = new System.Windows.Controls.Grid();
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        var top = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Red };
        var area = new System.Windows.Controls.Grid(); // the "view": rows 1 and 2
        System.Windows.Controls.Grid.SetRow(area, 1);
        System.Windows.Controls.Grid.SetRowSpan(area, 2);
        area.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        area.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
        var green = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Lime };
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.Blue } };
        System.Windows.Controls.Grid.SetRow(host, 1);
        area.Children.Add(green);
        area.Children.Add(host);
        grid.Children.Add(top);
        grid.Children.Add(area);
        var window = new System.Windows.Window
        {
            Width = 400, Height = 330, Left = 50, Top = 50, Content = grid, ShowInTaskbar = false, Topmost = true,
            WindowStyle = bordered ? System.Windows.WindowStyle.SingleBorderWindow : System.Windows.WindowStyle.None,
        };
        window.Show();
        // Another window over the lower part: must not show up in the capture.
        var cover = new System.Windows.Window { Width = 200, Height = 120, Left = 60, Top = 300, Background = System.Windows.Media.Brushes.Yellow, WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false, Topmost = true };
        cover.Show();
        Pump(TimeSpan.FromMilliseconds(700));
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Gdi.GetWindowRect(hwnd, out var w);
            var tl = area.PointToScreen(new System.Windows.Point(0, 0));
            var br = area.PointToScreen(new System.Windows.Point(area.ActualWidth, area.ActualHeight));
            var source = new RecordingSource(hwnd, (int)tl.X - w.Left, (int)tl.Y - w.Top, (int)(br.X - tl.X), (int)(br.Y - tl.Y), false);
            var (ow, oh) = SessionRecorder.OutputSize(source.Width, source.Height);
            using var grabber = new FrameGrabber(ow, oh, gpu);
            Assert.Equal(gpu, grabber.UsesGpu);
            (byte R, byte G, byte B) Px(int x, int y) { var p = grabber.Bits + (y * ow + x) * 4; return (p[2], p[1], p[0]); }
            (byte R, byte G, byte B) upper = default, lower = default;
            // The GPU delivers its first image asynchronously.
            for (var i = 0; i < 40; i++)
            {
                grabber.Capture(source, DateTime.Now);
                (upper, lower) = (Px(ow / 4, oh / 4), Px(ow / 4, oh * 3 / 4));
                if (upper.G > 200 && lower.B > 200) break;
                Pump(TimeSpan.FromMilliseconds(50));
            }
            // Edges of the area: no red from above, no window frame.
            var edge = Px(ow / 2, 1);
            Assert.True(edge is { G: > 200, R: < 60 }, $"top edge {edge}");
            Assert.True(upper is { G: > 200, R: < 60, B: < 60 }, $"WPF part {upper}");
            Assert.True(lower is { B: > 200, R: < 60, G: < 60 }, $"hosted WinForms part {lower}");
        }
        finally
        {
            cover.Close();
            window.Close();
        }
    }

    [Fact]
    public unsafe void FrameScaler_ShrinksByAveragingAndEnlargesSmoothly()
    {
        // 4 × 2 source: left half black, right half white -> 2 × 1: black, white; 4 × 2 -> 1 × 1: mid grey.
        var src = new uint[8];
        for (var i = 0; i < 8; i++) src[i] = i % 4 >= 2 ? 0xFFFFFFFFu : 0xFF000000u;
        var dst = new uint[2];
        fixed (uint* s = src) fixed (uint* d = dst)
        {
            FrameScaler.Scale((byte*)s, 16, 4, 2, (byte*)d, 8, 2, 1);
            Assert.Equal(0xFF000000u, dst[0]);
            Assert.Equal(0xFFFFFFFFu, dst[1]);
            FrameScaler.Scale((byte*)s, 16, 4, 2, (byte*)d, 4, 1, 1);
            Assert.InRange(dst[0] & 0xFF, 120u, 135u);
        }
        // Enlarging a single colour keeps it.
        var one = new uint[] { 0xFF336699, 0xFF336699, 0xFF336699, 0xFF336699 };
        var big = new uint[36];
        fixed (uint* s = one) fixed (uint* d = big)
            FrameScaler.Scale((byte*)s, 8, 2, 2, (byte*)d, 24, 6, 6);
        Assert.All(big, p => Assert.Equal(0xFF336699u, p));
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static async Task<(TimeSpan Duration, Windows.UI.Color Top, Windows.UI.Color Bottom)> Decode(string path, TimeSpan at)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(file);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(clip);
        using var stream = await composition.GetThumbnailAsync(at, 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = (await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage)).DetachPixelData();
        int pw = (int)decoder.PixelWidth, ph = (int)decoder.PixelHeight;
        Windows.UI.Color At(int x, int y) { var i = (y * pw + x) * 4; return Windows.UI.Color.FromArgb(255, pixels[i + 2], pixels[i + 1], pixels[i]); }
        return (clip.OriginalDuration, At(pw / 2, ph / 4), At(pw / 2, ph * 3 / 4));
    }
}
