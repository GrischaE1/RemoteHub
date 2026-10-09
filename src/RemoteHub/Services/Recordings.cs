using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Size of the video: the tab's own size, or a fixed presentation size the tab is scaled into.</summary>
public enum RecordingResolution { Tab, FullHd, Uhd4K }

/// <summary>Frame rate and size of recordings (Settings → Security &amp; storage → Session recordings).</summary>
/// <param name="UseGpu">
/// Capture through the graphics card (needed for 30/60 fps). Off by default: it uses the graphics driver directly and is
/// only turned on by choice.
/// </param>
public sealed record RecordingOptions(int FramesPerSecond, RecordingResolution Resolution, bool UseGpu = false)
{
    public static readonly RecordingOptions Default = new(10, RecordingResolution.Tab);
}

/// <summary>Where session recordings go and what they are called.</summary>
public static class Recordings
{
    /// <summary>Choices in the settings; 10 is the default (small files, enough to follow what happened).</summary>
    public static readonly (int Fps, string Label)[] FrameRates =
    [
        (5, "5 fps – smallest files"),
        (10, "10 fps – default, enough to follow what happened"),
        (15, "15 fps"),
        (30, "30 fps – smooth, for presentations"),
        (60, "60 fps – very smooth, high CPU load"),
    ];

    public static readonly (RecordingResolution Value, string Label)[] Resolutions =
    [
        (RecordingResolution.Tab, "Same as the tab (up to 4K)"),
        (RecordingResolution.FullHd, "Full HD – 1920 × 1080"),
        (RecordingResolution.Uhd4K, "4K – 3840 × 2160"),
    ];

    /// <summary>The user's choice; unknown values fall back to the defaults.</summary>
    public static RecordingOptions Options(UiState ui) => new(
        FrameRates.Any(f => f.Fps == ui.RecordingFps) ? ui.RecordingFps : RecordingOptions.Default.FramesPerSecond,
        Enum.TryParse<RecordingResolution>(ui.RecordingResolution, out var r) ? r : RecordingOptions.Default.Resolution,
        ui.RecordingUseGpu);

    /// <summary>"10 fps, same as the tab" for the settings overview.</summary>
    public static string Describe(RecordingOptions o) =>
        $"{o.FramesPerSecond} fps, " + o.Resolution switch
        {
            RecordingResolution.FullHd => "Full HD (1920 × 1080)",
            RecordingResolution.Uhd4K => "4K (3840 × 2160)",
            _ => "same size as the tab",
        } + (o.UseGpu ? ", graphics card capture" : "");

    public static string DefaultFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "RemoteHub");

    /// <summary>The organisation's folder (policy), else the user's choice, else Videos\RemoteHub.</summary>
    public static string Folder(UiState ui) =>
        AppPolicy.RecordingFolder ?? (string.IsNullOrWhiteSpace(ui.RecordingFolder) ? DefaultFolder : ui.RecordingFolder.Trim());

    /// <summary>"Web01 - PowerShell - 2026-10-08 14-05-09.mp4", without characters Windows doesn't allow in file names.</summary>
    public static string FileName(string connection, string kind, DateTime local)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var name = new string($"{connection} - {kind}".Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return $"{name} - {local:yyyy-MM-dd HH-mm-ss}.mp4";
    }

    /// <summary>A path in <paramref name="folder"/> that doesn't exist yet ("name (2).mp4" when needed).</summary>
    public static string UniquePath(string folder, string fileName)
    {
        var path = System.IO.Path.Combine(folder, fileName);
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>"04:07", or "1:02:07" after an hour.</summary>
    public static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:mm\\:ss}";
}
