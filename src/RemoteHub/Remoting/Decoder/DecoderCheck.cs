using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteHub.Agent;
using RemoteHub.Services;

namespace RemoteHub.Remoting.Decoder;

/// <summary>
/// <c>RemoteHub.exe --decoder-check &lt;file&gt;</c>: starts the decoder the way an agent desktop session does, has it
/// decode a picture RemoteHub encodes itself and a PNG, a BMP and a JPEG, runs its sandbox probes, and writes the results
/// as JSON to the file – for the lab (SEC-DEC-06) and for support. Nothing comes from a device.
/// </summary>
internal static class DecoderCheck
{
    public static int Run(string output)
    {
        var result = new JsonObject();
        try
        {
            using var host = DecoderHost.Start();
            result["confinement"] = host.Confinement.ToString();
            var (sent, shown, difference) = Video(host, 640, 360, 30);
            result["video"] = new JsonObject { ["sent"] = sent, ["shown"] = shown, ["meanDifference"] = Math.Round(difference, 2) };
            var images = new JsonObject();
            foreach (var (name, encoder) in Encoders()) images[name] = Image(host, encoder, 64, 48);
            result["images"] = images;
            result["probes"] = JsonNode.Parse(host.Probe());
        }
        catch (Exception ex) { result["error"] = ex.GetType().Name + ": " + ex.Message; }
        File.WriteAllText(output, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return result.ContainsKey("error") ? 1 : 0;
    }

    internal static (string Name, BitmapEncoder Encoder)[] Encoders() =>
        [("png", new PngBitmapEncoder()), ("bmp", new BmpBitmapEncoder()), ("jpeg", new JpegBitmapEncoder { QualityLevel = 95 })];

    /// <summary>A moving gradient, encoded here (H.264, software) and decoded by <paramref name="host"/>: frames sent, frames shown, mean difference of 255.</summary>
    internal static unsafe (int Sent, int Shown, double MeanDifference) Video(DecoderHost host, int width, int height, int frames)
    {
        var source = new byte[width * height * 4];
        using var encoder = new H264Encoder(width, height, 30, 80);
        int sent = 0, shown = 0;
        var difference = 0.0;
        for (var f = 0; f < frames; f++)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    (source[i], source[i + 1], source[i + 2], source[i + 3]) = ((byte)(x + f * 4), (byte)(y * 2), (byte)((x + y) / 4), 255);
                }
            byte[]? unit;
            fixed (byte* p = source) unit = encoder.Encode(p, width * 4, f == 0, out _);
            if (unit is null) continue;
            sent++;
            host.Video((uint)f, width, height, f == 0, unit, (bgra, w, h) =>
            {
                shown++;
                long sum = 0;
                for (var i = 0; i < Math.Min(bgra.Length, source.Length); i += 4)
                    sum += Math.Abs(bgra[i] - source[i]) + Math.Abs(bgra[i + 1] - source[i + 1]) + Math.Abs(bgra[i + 2] - source[i + 2]);
                difference = sum / (3.0 * w * h);
            });
        }
        return (sent, shown, difference);
    }

    /// <summary>"64×48" when the decoder gave back the image's pixels exactly (JPEG: closely), else what went wrong.</summary>
    internal static string Image(DecoderHost host, BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++) // smooth, so JPEG keeps it close
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(x * 255 / width), (byte)(y * 255 / height), 128, 255);
            }
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var image = host.Image(ms.ToArray());
        if (image is null) return "refused";
        if (image.Width != width || image.Height != height) return $"{image.Width}×{image.Height}";
        if (encoder is JpegBitmapEncoder) // lossy: close on average
            return pixels.Select((p, i) => Math.Abs(p - image.Bgra[i])).Average() < 16 ? $"{width}×{height}" : "different pixels";
        return image.Bgra.AsSpan().SequenceEqual(pixels) ? $"{width}×{height}" : "different pixels";
    }
}
