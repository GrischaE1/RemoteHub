namespace RemoteHub.Services;

/// <summary>
/// Small local diagnostic log (%APPDATA%\RemoteHub\logs\remotehub.log), capped at ~1 MB.
/// Never write passwords, usernames or host names here – only states, counts and yes/no facts.
/// </summary>
public static class DiagLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(SettingsStore.Directory, "logs", "remotehub.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
