using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RemoteHub.Lab;

/// <summary>
/// The lab's own checkout of origin/main (C:\rh-lab-main, a detached worktree of the checkout the app found): the tests
/// and lab scripts a run uses always come from main, without anyone pulling. Brought to origin/main when the app starts
/// and before every run; the git-ignored lab files and the newest MSIs of every worktree are copied in.
/// </summary>
public static class MainCheckout
{
    public static string Dir => Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "rh-lab-main");

    /// <summary>What <see cref="Update"/> did: the checkout to use, main's commit and anything worth saying.</summary>
    public sealed record Result(LabPaths Paths, string Commit, string? Msi, List<string> Notes);

    /// <summary>
    /// Fetches origin/main and moves the lab's checkout to it (made the first time). When that can't be done – offline,
    /// or files changed in it by hand – the checkout stays where it is and a note says why.
    /// </summary>
    public static async Task<Result> Update(LabPaths home)
    {
        var notes = new List<string>();
        var dir = Dir;
        if (await Git(home.Repo, "fetch", "--quiet", "origin", "main") is null) notes.Add("origin couldn't be reached – main as last fetched");
        if (!Directory.Exists(Path.Combine(dir, ".git")) && !File.Exists(Path.Combine(dir, ".git")))
        {
            if (await Git(home.Repo, "worktree", "add", "--detach", dir, "origin/main") is null)
                throw new InvalidOperationException($"Making the lab's checkout of main in {dir} failed (git worktree add).");
        }
        else if (await Git(dir, "checkout", "--quiet", "--detach", "origin/main") is null)
            notes.Add($"{dir} has changes of its own – left as it was");
        var commit = (await Git(dir, "log", "-1", "--format=%h %s"))?.FirstOrDefault() ?? "?";

        // the git-ignored files a run needs: the lab password and the lab signing certificate (copied, never read here)
        foreach (var f in new[] { "lab.local.json", "lab-signing.cer" })
        {
            var from = Path.Combine(home.LabDir, f);
            var to = Path.Combine(dir, "lab", f);
            if (File.Exists(from) && (!File.Exists(to) || !File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to))))
                File.Copy(from, to, overwrite: true);
        }

        var paths = LabPaths.TryFind(dir) ?? throw new InvalidOperationException($"No lab\\lab.json in {dir}.");
        var msi = await CollectMsis(home, paths);
        return new Result(paths, commit, msi, notes);
    }

    /// <summary>
    /// The newest agent build any worktree has (with its RemoteHub package), and the one before it for the update
    /// tests, copied to the lab checkout's artifacts\ – Start-LabRun.ps1 takes the newest there. Returns the folder the
    /// newest came from.
    /// </summary>
    private static async Task<string?> CollectMsis(LabPaths home, LabPaths main)
    {
        var folders = new List<string> { main.Artifacts, home.Artifacts };
        var list = await Git(home.Repo, "worktree", "list", "--porcelain") ?? [];
        folders.AddRange(list.Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => Path.Combine(l["worktree ".Length..].Replace('/', '\\'), "artifacts")));
        var builds = new List<(Version Version, string Folder)>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var path in SafeFiles(folder, "RemoteHub-Agent-*-x64.msi"))
            {
                var m = Regex.Match(Path.GetFileName(path), @"^RemoteHub-Agent-(\d+\.\d+\.\d+)-x64\.msi$");
                if (m.Success && File.Exists(Path.Combine(folder, $"RemoteHub-{m.Groups[1].Value}-x64.msi")))
                    builds.Add((Version.Parse(m.Groups[1].Value), folder));
            }
        }
        // per version the newest files (one worktree may have rebuilt it)
        var newest = builds.GroupBy(b => b.Version).OrderByDescending(g => g.Key).Take(2)
            .Select(g => g.OrderByDescending(b => File.GetLastWriteTimeUtc(Path.Combine(b.Folder, $"RemoteHub-Agent-{b.Version}-x64.msi"))).First()).ToList();
        if (newest.Count == 0) return null;
        Directory.CreateDirectory(main.Artifacts);
        foreach (var (version, folder) in newest)
        {
            if (string.Equals(folder, main.Artifacts, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in new[] { $"RemoteHub-Agent-{version}-x64.msi", $"RemoteHub-{version}-x64.msi" })
                CopyIfNewer(Path.Combine(folder, name), Path.Combine(main.Artifacts, name));
        }
        // the credential provider's lab build next to the newest agent, and the newest RDP Manager package (C-MSI-05)
        var cp = Path.Combine(newest[0].Folder, "credential-provider-lab");
        if (Directory.Exists(cp) && !string.Equals(newest[0].Folder, main.Artifacts, StringComparison.OrdinalIgnoreCase))
            foreach (var f in Directory.GetFiles(cp, "*", SearchOption.AllDirectories))
                CopyIfNewer(f, Path.Combine(main.Artifacts, "credential-provider-lab", Path.GetRelativePath(cp, f)));
        var legacy = folders.Where(Directory.Exists).SelectMany(f => SafeFiles(f, "RdpManager-*-x64.msi"))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (legacy is not null) CopyIfNewer(legacy, Path.Combine(main.Artifacts, Path.GetFileName(legacy)));
        return newest[0].Folder;
    }

    private static string[] SafeFiles(string folder, string pattern)
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static void CopyIfNewer(string from, string to)
    {
        if (!File.Exists(from) || string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from) && new FileInfo(to).Length == new FileInfo(from).Length) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>
    /// A git command in <paramref name="dir"/>: its output lines, or null when it fails. That folder is named as a safe
    /// directory for this call only – these checkouts keep their .git on a share, which git otherwise refuses as owned by
    /// someone else.
    /// </summary>
    public static async Task<List<string>?> Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in new[] { "-c", $"safe.directory={dir.Replace('\\', '/')}" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git didn't start.");
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 0 ? (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : null;
    }
}
