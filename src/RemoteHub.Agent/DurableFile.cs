using System.Text;

namespace RemoteHub.Agent;

/// <summary>
/// Replaces a state file in one step, durably: the new content goes to a temporary file in the same folder, is flushed to
/// the disk, and only then takes the file's place. A power loss at any moment leaves either the old file or the new one –
/// never a truncated or zero-filled one (lab finding A-CFG-JSON: a plain <c>File.WriteAllText</c> followed by a hard power
/// off left agent.json zero-filled, and the service didn't start).
/// </summary>
/// <remarks>
/// The temporary file is created in the target's folder, so it inherits that folder's ACL (SYSTEM and Administrators only
/// for <c>%ProgramData%\RemoteHub\Agent</c>, docs/agent-spec.md §5.3), and the rename never crosses a volume. An existing
/// file is replaced with <see cref="File.Replace(string, string, string?, bool)"/>, which keeps that file's own ACL and
/// owner, so a file an administrator secured differently stays as they left it.
/// </remarks>
internal static class DurableFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <param name="beforeReplace">For tests: runs between the flushed temporary file and the replace (a crash there).</param>
    public static void WriteAllText(string path, string text, Action? beforeReplace = null) =>
        WriteAllBytes(path, Utf8.GetBytes(text), beforeReplace);

    /// <summary>Lines as <see cref="File.WriteAllLines(string, IEnumerable{string})"/> writes them, durably.</summary>
    public static void WriteAllLines(string path, IEnumerable<string> lines) =>
        WriteAllText(path, string.Concat(lines.Select(l => l + Environment.NewLine)));

    public static void WriteAllBytes(string path, byte[] content, Action? beforeReplace = null)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(folder);
        // Unique, so two writers (the service and an MSI custom action) never share a temporary file.
        var temp = Path.Combine(folder, $"{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true); // the data is on the disk before any name points to it
            }
            beforeReplace?.Invoke();
            if (File.Exists(full))
            {
                try
                {
                    File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException) { /* e.g. a reader holds it without delete sharing, or the volume can't replace: a plain move */ }
            }
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            // Gone after a successful replace or move; left over only when something above failed.
            try { File.Delete(temp); } catch { /* a small stray file that nothing reads */ }
        }
    }
}
