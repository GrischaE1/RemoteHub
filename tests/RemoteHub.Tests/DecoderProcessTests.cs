using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Remoting.Decoder;
using RemoteHub.Services;
using RemoteHub.Views;
using Button = Wpf.Ui.Controls.Button;

namespace RemoteHub.Tests;

/// <summary>
/// The decoder process (docs/decoder-isolation-design.md): the console's checks of its answers, the restart limit, the
/// real <c>RemoteHubDecoder.exe</c> in its sandbox (decoding, what it can't do, the low-integrity fallback, a crash),
/// malformed input fed to it, and the tab when the session gives up.
/// </summary>
[Collection("Decoder")] // the low-integrity switch is global
public class DecoderProcessTests
{
    // ---- the console's checks: a decoder that was taken over can say anything

    private static byte[] Frame(uint id, int buffer, int w, int h) => DecoderProtocol.Frame(id, buffer, w, h);

    [Fact]
    public void A_frame_answer_is_taken_only_for_the_frame_sent_a_real_buffer_and_no_larger_picture()
    {
        Assert.Null(DecoderHost.CheckVideoAnswer(Frame(7, 1, 1920, 1080), 7, 1920, 1080));
        Assert.Null(DecoderHost.CheckVideoAnswer(Frame(7, 0, 1918, 1080), 7, 1920, 1080)); // cropped is fine
        Assert.Null(DecoderHost.CheckVideoAnswer(DecoderProtocol.WithId(DecoderMessage.NoFrame, 7), 7, 1920, 1080));
        Assert.Null(DecoderHost.CheckVideoAnswer(DecoderProtocol.WithId(DecoderMessage.Failed, 7, "bad"), 7, 1920, 1080));

        Assert.NotNull(DecoderHost.CheckVideoAnswer([], 7, 1920, 1080));
        Assert.Contains("another frame", DecoderHost.CheckVideoAnswer(Frame(8, 0, 1920, 1080), 7, 1920, 1080));
        Assert.Contains("buffer 2", DecoderHost.CheckVideoAnswer(Frame(7, 2, 1920, 1080), 7, 1920, 1080));
        Assert.Contains("picture of", DecoderHost.CheckVideoAnswer(Frame(7, 0, 1921, 1080), 7, 1920, 1080));
        Assert.Contains("picture of", DecoderHost.CheckVideoAnswer(Frame(7, 0, 4096, 2305), 7, 4096, 4096));
        Assert.Contains("picture of", DecoderHost.CheckVideoAnswer(Frame(7, 0, 0, 1080), 7, 1920, 1080));
        Assert.Contains("wrong length", DecoderHost.CheckVideoAnswer([.. Frame(7, 0, 10, 10), 0], 7, 1920, 1080));
        Assert.Contains("answer 6", DecoderHost.CheckVideoAnswer(DecoderProtocol.ImageDone(7, 10, 10), 7, 1920, 1080));
        Assert.Contains("too short", DecoderHost.CheckVideoAnswer([(byte)DecoderMessage.Frame, 7], 7, 1920, 1080));
    }

    [Fact]
    public void An_image_answer_must_name_the_image_and_have_exactly_its_size()
    {
        Assert.Null(DecoderHost.CheckImageAnswer(DecoderProtocol.ImageDone(3, 64, 48), 3, 64, 48));
        Assert.Null(DecoderHost.CheckImageAnswer(DecoderProtocol.WithId(DecoderMessage.Failed, 3, "no"), 3, 64, 48));
        Assert.Contains("image of", DecoderHost.CheckImageAnswer(DecoderProtocol.ImageDone(3, 64, 49), 3, 64, 48));
        Assert.Contains("another image", DecoderHost.CheckImageAnswer(DecoderProtocol.ImageDone(4, 64, 48), 3, 64, 48));
        Assert.Contains("answer 3", DecoderHost.CheckImageAnswer(Frame(3, 0, 64, 48), 3, 64, 48));
    }

    [Fact]
    public void The_decoder_and_the_desktop_protocol_agree_on_the_largest_picture()
    {
        Assert.Equal(RemoteHub.Agent.DesktopProtocol.MaxVideoWidth, DecoderProtocol.MaxVideoWidth);
        Assert.Equal(RemoteHub.Agent.DesktopProtocol.MaxVideoHeight, DecoderProtocol.MaxVideoHeight);
    }

    // ---- restarts

    [Fact]
    public void A_session_gives_up_after_more_than_three_decoder_failures_in_a_minute()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var stops = new List<(string Reason, bool GaveUp)>();
        using var session = new SessionDecoder(() => throw new DecoderFailedException("didn't start"), (r, g) => stops.Add((r, g)), () => now);
        for (var i = 0; i < 3; i++)
        {
            Assert.Throws<DecoderFailedException>(() => session.Use(d => 0));
            now = now.AddSeconds(10);
        }
        Assert.False(session.GaveUp);
        Assert.Throws<DecoderFailedException>(() => session.Use(d => 0)); // the fourth within a minute
        Assert.True(session.GaveUp);
        Assert.Equal([false, false, false, true], stops.Select(s => s.GaveUp));
        Assert.All(stops, s => Assert.Equal("didn't start", s.Reason));
        Assert.Throws<DecoderFailedException>(() => session.Use(d => 0)); // nothing more: no fifth start
        Assert.Equal(4, stops.Count);
    }

    [Fact]
    public void Failures_spread_over_more_than_a_minute_are_not_counted_together()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        using var session = new SessionDecoder(() => throw new DecoderFailedException("x"), (_, _) => { }, () => now);
        for (var i = 0; i < 10; i++)
        {
            Assert.Throws<DecoderFailedException>(() => session.Use(d => 0));
            now = now.AddSeconds(25);
        }
        Assert.False(session.GaveUp);
    }

    // ---- the real decoder process

    private static DecoderHost StartOrSkip()
    {
        try { return DecoderHost.Start(); }
        catch (DecoderUnavailableException ex)
        {
            Skip.If(true, "no sandbox on this machine: " + ex.Message);
            throw;
        }
    }

    private static Dictionary<string, string> Probes(DecoderHost host) => JsonSerializer.Deserialize<Dictionary<string, string>>(host.Probe())!;

    [SkippableFact]
    public void The_decoder_runs_in_an_AppContainer_and_decodes_video_and_images()
    {
        using var host = StartOrSkip();
        Assert.Equal(DecoderConfinement.AppContainer, host.Confinement);
        var (sent, shown, difference) = DecoderCheck.Video(host, 640, 360, 20);
        Assert.Equal(20, sent);
        Assert.Equal(sent, shown);
        Assert.InRange(difference, 0, 5);
        foreach (var (name, encoder) in DecoderCheck.Encoders()) Assert.True(DecoderCheck.Image(host, encoder, 64, 48) == "64×48", name);
    }

    [SkippableFact]
    public void In_its_AppContainer_the_decoder_reaches_no_network_file_registry_clipboard_process_or_window()
    {
        using var host = StartOrSkip();
        var probes = Probes(host);
        Assert.Equal("AppContainer", probes["sandbox"]);
        foreach (var probe in new[] { "network", "loopback", "profile", "database", "write", "registry", "clipboard", "process", "parent", "window" })
            Assert.True(probes[probe].StartsWith("blocked", StringComparison.Ordinal), $"{probe}: {probes[probe]}");
    }

    [SkippableFact]
    public void Without_an_AppContainer_the_decoder_runs_at_low_integrity_and_still_cant_write_or_reach_RemoteHub()
    {
        DecoderSandbox.ForceLowIntegrity = true;
        try
        {
            using var host = StartOrSkip();
            Assert.Equal(DecoderConfinement.LowIntegrity, host.Confinement);
            Assert.Equal(5, DecoderCheck.Video(host, 320, 240, 5).Shown);
            var probes = Probes(host);
            Assert.Equal("low integrity", probes["sandbox"]);
            // Weaker than the AppContainer (it may read files and use the network, §9): but no writes, no clipboard,
            // no processes, nothing of RemoteHub's.
            foreach (var probe in new[] { "write", "registry", "clipboard", "process", "parent", "window" })
                Assert.True(probes[probe].StartsWith("blocked", StringComparison.Ordinal), $"{probe}: {probes[probe]}");
        }
        finally { DecoderSandbox.ForceLowIntegrity = false; }
    }

    [SkippableFact]
    public void A_decoder_that_dies_is_reported_and_the_session_starts_a_new_one()
    {
        var stops = new List<string>();
        using var session = new SessionDecoder(() => DecoderHost.Start(), (r, _) => stops.Add(r));
        int first;
        try { first = session.Use(d => d.ProcessId); }
        catch (DecoderUnavailableException ex) { Skip.If(true, ex.Message); throw; }
        Process.GetProcessById(first).Kill();
        var watch = Stopwatch.StartNew();
        Assert.Throws<DecoderFailedException>(() => session.Use(d => d.Video(1, 64, 64, true, [0, 0, 0, 1, 0x65], (_, _, _) => { })));
        Assert.True(watch.Elapsed < DecoderHost.FrameTimeout + TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
        Assert.Single(stops);
        var second = session.Use(d => d.ProcessId);
        Assert.NotEqual(first, second);
        Assert.False(session.GaveUp);
    }

    [SkippableFact]
    public void Closing_the_session_ends_a_waiting_decoder_at_once_and_isnt_reported_as_a_failure()
    {
        var stops = new List<string>();
        var session = new SessionDecoder(() => DecoderHost.Start(), (r, _) => stops.Add(r));
        try { session.Use(d => d.ProcessId); }
        catch (DecoderUnavailableException ex) { Skip.If(true, ex.Message); throw; }
        // The probes wait a few seconds for the network: a call that is still waiting when the tab closes.
        var waiting = Task.Run(() => session.Use(d => d.Probe()));
        Thread.Sleep(500);
        var watch = Stopwatch.StartNew();
        session.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"closing took {watch.Elapsed}");
        var ex2 = Assert.Throws<AggregateException>(() => waiting.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<DecoderFailedException>(ex2.InnerException);
        Assert.Empty(stops);
        Assert.True(session.Stopped);
    }

    // ---- malformed input (SEC-DEC-01/02 in CI): every input ends in a frame, a bad frame or a contained failure

    [SkippableFact]
    public unsafe void Malformed_video_never_hangs_or_harms_the_console_and_a_good_stream_decodes_afterwards()
    {
        // A short real stream to break: its key frame (SPS, PPS, IDR) and following frames.
        var units = new List<byte[]>();
        var source = new byte[320 * 240 * 4];
        using (var encoder = new RemoteHub.Agent.H264Encoder(320, 240, 30, 70))
            for (var f = 0; f < 6; f++)
            {
                Array.Fill(source, (byte)(f * 30));
                fixed (byte* p = source)
                    if (encoder.Encode(p, 320 * 4, f == 0, out _) is { } u) units.Add(u);
            }
        var host = StartOrSkip();
        var random = new Random(1701);
        int frames = 0, bad = 0, failures = 0;
        try
        {
            for (var n = 0; n < 120; n++)
            {
                var unit = units[n % units.Count];
                byte[] input = (n % 4) switch
                {
                    0 => unit[..random.Next(1, unit.Length)],                                     // truncated
                    1 => Flip(unit, random, 1 + random.Next(16)),                                  // bit flips
                    2 => [0, 0, 0, 1, .. RandomBytes(random, random.Next(1, 4096))],               // noise after a start code
                    _ => [.. unit, .. unit[..random.Next(1, unit.Length)]],                        // a unit with a broken copy behind it
                };
                var watch = Stopwatch.StartNew();
                try
                {
                    if (host.Video((uint)n, n % 8 == 0 ? 4096 : 320, n % 8 == 0 ? 2304 : 240, n % 4 != 2, input, (_, _, _) => frames++)) { } else bad++;
                }
                catch (DecoderFailedException)
                {
                    failures++;
                    host.Dispose();
                    host = DecoderHost.Start();
                }
                Assert.True(watch.Elapsed < DecoderHost.FrameTimeout + TimeSpan.FromSeconds(2), $"input {n} took {watch.Elapsed}");
            }
            using var fresh = DecoderHost.Start();
            Assert.Equal(10, DecoderCheck.Video(fresh, 320, 240, 10).Shown);
        }
        finally { host.Dispose(); }
        Assert.True(frames + bad + failures > 0);
    }

    [SkippableFact]
    public void Malformed_images_are_refused_or_decoded_never_more_than_their_header_states()
    {
        using var host = StartOrSkip();
        var random = new Random(4242);
        foreach (var (name, encoder) in DecoderCheck.Encoders())
        {
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 32 * 4], 32 * 4)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var good = ms.ToArray();
            for (var n = 0; n < 40; n++)
            {
                var bad = n % 2 == 0 ? good[..random.Next(1, good.Length)] : Flip(good, random, 1 + random.Next(8));
                var image = host.Image(bad);
                if (image is null) continue;
                var header = DeviceImage.Header(bad)!.Value;
                Assert.Equal((header.Width, header.Height, header.Width * header.Height * 4), (image.Width, image.Height, image.Bgra.Length));
            }
            Assert.NotNull(host.Image(good)); // still working
        }
    }

    private static byte[] Flip(byte[] data, Random random, int bits)
    {
        var copy = (byte[])data.Clone();
        for (var i = 0; i < bits; i++) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
        return copy;
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var b = new byte[length];
        random.NextBytes(b);
        return b;
    }

    // ---- the tab

    [Fact]
    public void A_session_that_gives_up_on_its_decoder_ends_with_a_message_and_a_way_to_connect_again() => Sta.Run(owner =>
    {
        var session = new AgentDesktopSession(new Connection { Name = "pc", Host = "pc.contoso.local" });
        var view = new AgentDesktopView(session, () => { }, () => { }, () => { }, () => DesktopQualityPreset.Find(null), (_, _) => { },
            () => new DesktopResolution(DesktopResolutionKind.Fit), _ => { });
        owner.Content = view;
        Sta.Pump();

        session.DecoderStopped("no answer within 2 s while decoding a frame", gaveUp: false);
        Sta.Pump();
        Assert.NotEqual(DesktopPhase.Ended, session.Phase); // one restart: the session goes on

        session.DecoderStopped("no answer within 2 s while decoding a frame", gaveUp: true);
        Sta.Pump();
        Assert.Equal(DesktopPhase.Ended, session.Phase);
        Assert.Equal("The picture can't be shown – the device keeps sending data RemoteHub can't decode. Disconnect and check the device.", session.StatusText);
        Assert.Contains(Sta.All<TextBlock>(view), t => t.IsVisible && t.Text == session.StatusText);
        Assert.Contains(Sta.All<Button>(view), b => Equals(b.Content, "Connect") && b.IsVisible); // (the choose page has one too, hidden)
    });
}
