using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace RemoteHub.Lab;

/// <summary>Where the lab is: the checkout (lab\, artifacts\) and the lab's working folder (lab.json's LabRoot).</summary>
public sealed record LabPaths(string Repo, string LabRoot)
{
    public string LabDir => Path.Combine(Repo, "lab");
    public string TestsDir => Path.Combine(LabDir, "tests");
    public string Artifacts => Path.Combine(Repo, "artifacts");
    /// <summary>Every run the lab made (Start-LabRun.ps1 copies the ones started from this checkout to artifacts\lab).</summary>
    public string Results => Path.Combine(LabRoot, "results");

    /// <summary>The checkout at or above <paramref name="start"/>: the first folder up with lab\lab.json; null when there is none.</summary>
    public static LabPaths? TryFind(string start)
    {
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start)) return null;
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var json = Path.Combine(dir.FullName, "lab", "lab.json");
            if (!File.Exists(json)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            var root = doc.RootElement.TryGetProperty("LabRoot", out var r) ? r.GetString() : null;
            return new LabPaths(dir.FullName, string.IsNullOrEmpty(root) ? @"D:\RemoteHubLab" : root);
        }
        return null;
    }
}

/// <summary>A case's state: its latest lab result, or where it is in the run going on.</summary>
public enum CaseState { NotRun, Passed, Failed, Skipped, Pending, Running }

public sealed record TestCase(string Id, string Title, string File);

/// <summary>
/// A test file: its group (the first bare tag of its Describe), role (LabRole), cases, and whether a quick run still
/// takes it on every device kind (LabQuick: every kind).
/// </summary>
public sealed record TestFile(string Name, string About, string Role, IReadOnlyList<string> Groups, IReadOnlyList<TestCase> Cases, bool EveryKind = false)
{
    public string Group => Groups.Count > 0 ? Groups[0] : "Other";
}

/// <summary>The latest result of a case: in which run, and the VMs it failed on.</summary>
public sealed record CaseResult(string Id, CaseState State, string Run, DateTime When, string FailedOn);

/// <summary>The tests as they are in lab\tests now – the same rules as lab\Get-LabCatalog.ps1.</summary>
public static partial class Catalog
{
    [GeneratedRegex(@"^#\s*LabRole:\s*(\S+)")] private static partial Regex RoleLine();
    [GeneratedRegex(@"^#\s*LabQuick:\s*every kind")] private static partial Regex QuickLine();
    [GeneratedRegex(@"^\s*Describe\s+(['""]).*?\1\s+-Tag\s+([^{]+)")] private static partial Regex DescribeLine();
    [GeneratedRegex(@"-Tag\s+'([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})'")] private static partial Regex CaseTag();
    [GeneratedRegex(@"^\s*It\s+'((?:[^']|'')*)'")] private static partial Regex ItSingle();
    [GeneratedRegex(@"^\s*It\s+""([^""]*)""")] private static partial Regex ItDouble();

    public static IReadOnlyList<TestFile> Read(string testsDir) =>
        Directory.GetFiles(testsDir, "*.Tests.ps1").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => Parse(Path.GetFileName(f), File.ReadAllLines(f))).ToList();

    /// <summary>A case as the tree knows it: file, ID and name (an It renamed counts as a new case).</summary>
    public static string Key(TestCase c) => $"{c.File}|{c.Id}|{c.Title}";

    /// <summary>The cases in <paramref name="fresh"/> that <paramref name="known"/> didn't have, and whether anything changed at all.</summary>
    public static (List<TestCase> Added, bool Changed) Compare(IEnumerable<TestFile> known, IEnumerable<TestFile> fresh)
    {
        var old = known.SelectMany(f => f.Cases).Select(Key).ToHashSet();
        var now = fresh.SelectMany(f => f.Cases).ToList();
        var added = now.Where(c => !old.Contains(Key(c))).ToList();
        return (added, added.Count > 0 || !old.SetEquals(now.Select(Key)));
    }

    public static TestFile Parse(string fileName, string[] lines)
    {
        var name = fileName.EndsWith(".Tests.ps1", StringComparison.OrdinalIgnoreCase) ? fileName[..^".Tests.ps1".Length] : fileName;
        var about = lines.Length > 0 && lines[0].StartsWith('#') ? lines[0].TrimStart('#', ' ') : "";
        var role = "";
        var everyKind = false;
        var groups = new List<string>();
        var cases = new List<TestCase>();
        foreach (var l in lines)
        {
            if (RoleLine().Match(l) is { Success: true } r) { role = r.Groups[1].Value; continue; }
            if (QuickLine().IsMatch(l)) { everyKind = true; continue; }
            if (DescribeLine().Match(l) is { Success: true } d)
            {
                foreach (var t in d.Groups[2].Value.Split(',').Select(s => s.Trim()))
                    if (Regex.IsMatch(t, "^[A-Za-z]+$") && !groups.Contains(t)) groups.Add(t);
                continue;
            }
            if (CaseTag().Match(l) is not { Success: true } id) continue;
            var title = ItSingle().Match(l) is { Success: true } s ? s.Groups[1].Value.Replace("''", "'")
                      : ItDouble().Match(l) is { Success: true } q ? q.Groups[1].Value : null;
            if (title is not null) cases.Add(new TestCase(id.Groups[1].Value, title, name));
        }
        return new TestFile(name, about, role, groups, cases, everyKind);
    }
}

/// <summary>What the finished runs say: each case's latest result and how long each file took on each VM kind.</summary>
public sealed partial class LabHistory
{
    [GeneratedRegex(@"^([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})\b")] private static partial Regex LeadingId();
    [GeneratedRegex(@"^\d{8}-\d{6}$")] private static partial Regex RunName();

    public Dictionary<string, CaseResult> Latest { get; } = new(StringComparer.Ordinal);
    /// <summary>(file, VM kind) → minutes of each finished file run, oldest first.</summary>
    public Dictionary<(string File, string Kind), List<double>> Minutes { get; } = new();

    public static LabHistory Read(string resultsRoot)
    {
        var h = new LabHistory();
        if (!Directory.Exists(resultsRoot)) return h;
        foreach (var run in Directory.GetDirectories(resultsRoot).Select(Path.GetFileName).OfType<string>()
                     .Where(n => RunName().IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var dir = Path.Combine(resultsRoot, run);
            foreach (var (id, state, failedOn) in ReadRun(dir))
                h.Latest[id] = new CaseResult(id, state, run, RunTime(run), failedOn);
            var log = Path.Combine(dir, "console.log");
            if (File.Exists(log))
                foreach (var e in RunLog.Parse(ReadShared(log)).Where(e => e.Done && e.Minutes is not null))
                {
                    var key = (e.File, RunLog.Kind(e.Vm));
                    if (!h.Minutes.TryGetValue(key, out var list)) h.Minutes[key] = list = [];
                    list.Add(e.Minutes!.Value);
                }
        }
        return h;
    }

    public static DateTime RunTime(string run) =>
        DateTime.TryParseExact(run, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;

    /// <summary>Each case of a run's pester XMLs (below <paramref name="dir"/>): failed if it failed on any VM.</summary>
    public static IEnumerable<(string Id, CaseState State, string FailedOn)> ReadRun(string dir, string? onlyFile = null)
    {
        var acc = new Dictionary<string, (CaseState State, List<string> Vms)>(StringComparer.Ordinal);
        foreach (var xml in Directory.EnumerateFiles(dir, onlyFile is null ? "pester-*.xml" : $"pester-{onlyFile}.xml", SearchOption.AllDirectories))
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(ReadShared(xml)));
                while (reader.ReadToFollowing("test-case"))
                {
                    var text = reader.GetAttribute("description");
                    var name = reader.GetAttribute("name") ?? "";
                    if (string.IsNullOrEmpty(text)) text = name.Split('.')[^1];
                    if (LeadingId().Match(text) is not { Success: true } m) continue;
                    var now = reader.GetAttribute("result") switch { "Success" => CaseState.Passed, "Failure" => CaseState.Failed, _ => CaseState.Skipped };
                    var vm = Regex.Replace(name.Split('.')[0], @"^\S+ (TestAutomation - )?", "");
                    var id = m.Groups[1].Value;
                    acc.TryGetValue(id, out var was);
                    was.Vms ??= [];
                    if (now == CaseState.Failed) was.Vms.Add(vm);
                    // a failure anywhere wins, then a pass
                    was.State = was.State == CaseState.Failed || now == CaseState.Failed ? CaseState.Failed
                              : was.State == CaseState.Passed || now == CaseState.Passed ? CaseState.Passed : CaseState.Skipped;
                    acc[id] = was;
                }
            }
            catch (XmlException) { /* a file still being written */ }
        }
        return acc.Select(kv => (kv.Key, kv.Value.State, string.Join(", ", kv.Value.Vms.Distinct())));
    }

    /// <summary>Expected minutes for a file on a VM kind: the median of its earlier runs there, else on any kind.</summary>
    public double? Expected(string file, string kind)
    {
        if (Minutes.TryGetValue((file, kind), out var here) && here.Count > 0) return Median(here);
        var any = Minutes.Where(kv => kv.Key.File == file).SelectMany(kv => kv.Value).ToList();
        return any.Count > 0 ? Median(any) : null;
    }

    private static double Median(List<double> v)
    {
        var s = v.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Reads a file the lab may still be writing.</summary>
    public static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>A run's console.log: "12:30:54 start  C-DATA on Console-26H2-en-US (+ ...)", "... done   C-DATA on ...: 17 passed, 1 failed, 1 skipped (47 min)".</summary>
public static partial class RunLog
{
    public sealed record Entry(TimeSpan At, bool Done, string File, string Vm, int Failed, double? Minutes);

    [GeneratedRegex(@"^(\d\d:\d\d:\d\d) (start|done)\s+(\S+) on (\S+)(?:.*?(\d+) failed)?(?:.*\((\d+(?:\.\d+)?) min\))?", RegexOptions.Multiline)]
    private static partial Regex Line();
    [GeneratedRegex(@"(\d+) file runs? on")] private static partial Regex Total();
    [GeneratedRegex(@"-\d+$")] private static partial Regex Lane();

    public static IReadOnlyList<Entry> Parse(string log) =>
        Line().Matches(log).Select(m => new Entry(
            TimeSpan.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value == "done", m.Groups[3].Value,
            m.Groups[4].Value.TrimEnd(':'), m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0,
            m.Groups[6].Success ? double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : null)).ToList();

    /// <summary>The number of file runs the lab planned ("8 file runs on 6 VMs"), once it has said so.</summary>
    public static int? PlannedRuns(string log) => Total().Match(log) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>A VM's kind – its Id without the lane ("W11-26H2-en-US-2" → "W11-26H2-en-US"): results are kept per kind.</summary>
    public static string Kind(string vmId) => Lane().Replace(vmId, "");
}

/// <summary>
/// How far a run is and how long it still takes: each file run weighs what it took before (<see cref="LabHistory.Expected"/>,
/// 10 min when unknown); the ones running count with what they've had so far, at most what they're expected to take; the
/// rest is shared by the VMs running at once.
/// </summary>
public static class Estimate
{
    public const double UnknownMinutes = 10;

    public sealed record Result(double Progress, TimeSpan Left, int Done, int Total);

    /// <param name="planned">(file, VM kind) of every file run the run will make.</param>
    /// <param name="entries">The run's console.log so far.</param>
    /// <param name="now">The time of day now, on the lab host's clock (console.log's).</param>
    /// <param name="parallel">How many file runs go at once (the VMs).</param>
    public static Result For(IReadOnlyList<(string File, string Kind)> planned, IReadOnlyList<RunLog.Entry> entries, LabHistory history,
                             TimeSpan now, int parallel)
    {
        double Expect(string file, string kind) => history.Expected(file, kind) ?? UnknownMinutes;
        var started = entries.Where(e => !e.Done).ToList();
        var done = entries.Where(e => e.Done).ToList();
        var running = started.Where(s => !done.Any(d => d.File == s.File && d.Vm == s.Vm && d.At >= s.At)).ToList();
        // what is planned and hasn't started yet (one planned entry per started file run is taken off)
        var waiting = planned.ToList();
        foreach (var s in started)
        {
            var i = waiting.FindIndex(p => p.File == s.File && p.Kind == RunLog.Kind(s.Vm));
            if (i >= 0) waiting.RemoveAt(i);
        }
        double total = 0, finished = 0, left = 0;
        foreach (var d in done) { var e = d.Minutes ?? Expect(d.File, RunLog.Kind(d.Vm)); total += e; finished += e; }
        foreach (var r in running)
        {
            var e = Expect(r.File, RunLog.Kind(r.Vm));
            var had = (now - r.At).TotalMinutes;
            if (had < 0) had += 24 * 60;   // over midnight
            total += Math.Max(e, had);
            finished += Math.Min(had, e);
            left += Math.Max(1, e - had);
        }
        foreach (var w in waiting) { var e = Expect(w.File, w.Kind); total += e; left += e; }
        var share = Math.Max(1, Math.Max(parallel, running.Count));
        var count = Math.Max(planned.Count, done.Count + running.Count + waiting.Count);
        return new Result(total <= 0 ? 0 : Math.Clamp(finished / total, 0, 1), TimeSpan.FromMinutes(left / share), done.Count, count);
    }
}

/// <summary>Which VM kinds a file runs on – the same rules as lab\Invoke-LabTests.ps1.</summary>
public static class RunPlan
{
    /// <summary>
    /// Console files on the Console kinds, device files on the others, "any" everywhere. A quick run takes the device
    /// files to one device kind only (<see cref="QuickKind"/>), unless the file asks for every kind.
    /// </summary>
    public static IEnumerable<string> Kinds(TestFile f, IReadOnlyList<string> kinds, bool quick)
    {
        var quickKind = QuickKind(kinds);
        foreach (var k in kinds)
        {
            var console = IsConsole(k);
            var fits = f.Role.ToLowerInvariant() switch { "console" => console, "device" => !console, _ => true };
            if (fits && (!quick || console || f.EveryKind || k == quickKind)) yield return k;
        }
    }

    /// <summary>A quick run's device kind: a client Windows one when there is one (the Server VMs are the busiest).</summary>
    public static string? QuickKind(IReadOnlyList<string> kinds) =>
        kinds.FirstOrDefault(k => !IsConsole(k) && !k.StartsWith("Server", StringComparison.OrdinalIgnoreCase)) ?? kinds.FirstOrDefault(k => !IsConsole(k));

    private static bool IsConsole(string kind) => kind.StartsWith("Console", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The test files a change can affect, for "Changed since full run": each changed path's area names the files that
/// exercise it. Smoke always runs; a path no rule knows selects everything – better a longer run than a gap.
/// </summary>
public static class Areas
{
    /// <summary>Files whose role is console or any (RemoteHub itself and the lab harness built from its code).</summary>
    private const string Console = "@console";

    // the first rule whose prefix the path starts with decides; null = every file, empty = none. "X*" is a name prefix.
    private static readonly (string Prefix, string[]? Files)[] Rules =
    [
        ("docs/", []), (".github/", []), ("tests/RemoteHub.Tests/", []), ("tests/RemoteHub.CredentialProvider.Tests/", []),
        ("README", []), ("THIRD-PARTY-NOTICES", []), ("CLAUDE.md", []), ("LICENSE", []), (".gitignore", []),
        // the lab's own tools, not what a test runs
        ("lab/LabConsole", []), ("build/build-lab-tool", []),("lab/Get-LabCatalog", []), ("lab/Get-LabCaseStatus", []),
        ("lab/Get-LabFailures", []), ("lab/Update-LabCaseStatus", []), ("lab/New-LabReport", []),
        ("lab/", null),
        ("src/RemoteHub.CredentialProvider/", ["C-AUTO*", "C-END*", "C-PROV", "D-SECURE"]),
        ("src/RemoteHub.Agent/", ["A-*", "C-AUTO*", "C-END*", "C-PROV", "C-REC*", "D-*", "J-*", "P-*", "POL-*", "S-*", "T-*", "UI-*", "W-*"]),
        ("src/RemoteHub/", [Console]),
        ("tests/RemoteHub.LabHarness/", [Console]),
        ("installer/", ["A-INST*", "A-UNINST", "A-UPD*", "A-MORE*", "C-AUTO*", "C-MSI", "C-POLUPD*"]),
        ("policy/", ["POL-*", "C-POLUPD*"]),
        ("build/", ["A-INST*", "A-UPD*", "C-MSI", "C-POLUPD*"]),
    ];

    /// <summary>The names of the files to run for <paramref name="changed"/> (paths relative to the checkout); null = every file.</summary>
    public static HashSet<string>? Affected(IEnumerable<string> changed, IReadOnlyList<TestFile> catalog)
    {
        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Smoke" };
        foreach (var raw in changed)
        {
            var path = raw.Replace('\\', '/').Trim();
            if (path.Length == 0) continue;
            if (path.StartsWith("lab/tests/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".Tests.ps1", StringComparison.OrdinalIgnoreCase))
            {
                picked.Add(Path.GetFileName(path)[..^".Tests.ps1".Length]);
                continue;
            }
            var rule = Rules.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
            if (rule.Prefix is null || rule.Files is null) return null;
            foreach (var f in catalog)
                if (rule.Files.Any(p => p == Console ? f.Role is "console" or "any" : Like(f.Name, p))) picked.Add(f.Name);
        }
        return picked;
    }

    private static bool Like(string name, string pattern) => pattern.EndsWith('*')
        ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest full run (no tags, not quick) whose run.json names the commit it ran from (Start-LabRun.ps1 records
    /// it): its id and commit, or null.
    /// </summary>
    public static (string Run, string Commit)? LastFullRun(string resultsRoot)
    {
        if (!Directory.Exists(resultsRoot)) return null;
        foreach (var run in Directory.GetDirectories(resultsRoot).Select(Path.GetFileName).OfType<string>()
                     .Where(n => Regex.IsMatch(n, @"^\d{8}-\d{6}$")).OrderByDescending(n => n, StringComparer.Ordinal))
        {
            var json = Path.Combine(resultsRoot, run, "run.json");
            if (!File.Exists(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var r = doc.RootElement;
                var tags = r.TryGetProperty("Tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;
                var quick = r.TryGetProperty("Quick", out var q) && q.ValueKind == JsonValueKind.True;
                var commit = r.TryGetProperty("Commit", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (tags == 0 && !quick && commit is not null && Regex.IsMatch(commit, "^[0-9a-f]{40}$")) return (run, commit);
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return null;
    }
}
/// <summary>
/// The lab's VM kinds and how many VMs (lanes) each has – from the newest run.json, which lists every VM of each kind
/// the run used (no Hyper-V rights needed). A VM that was only lent to other VMs' files has no result folder of its
/// own, so the folders alone can miss a lane.
/// </summary>
public static class LabVms
{
    public static List<(string Kind, int Lanes)> Read(string resultsRoot)
    {
        if (!Directory.Exists(resultsRoot)) return [];
        var runs = Directory.GetDirectories(resultsRoot).Select(Path.GetFileName).OfType<string>()
            .Where(n => Regex.IsMatch(n, @"^\d{8}-\d{6}$")).OrderByDescending(n => n, StringComparer.Ordinal).Take(15).ToList();
        foreach (var run in runs)
        {
            var json = Path.Combine(resultsRoot, run, "run.json");
            if (!File.Exists(json)) continue;
            try { if (FromRunJson(File.ReadAllText(json)) is { Count: > 0 } kinds) return kinds; }
            catch (JsonException) { }
            catch (IOException) { }
        }
        // no run.json with its VMs: the result folders of those runs
        return runs.SelectMany(r => Directory.GetDirectories(Path.Combine(resultsRoot, r)).Select(Path.GetFileName).OfType<string>())
            .Distinct().GroupBy(RunLog.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count())).ToList();
    }

    /// <summary>run.json's Vms: each kind's Id with the number of its Lanes (one VM when it names none).</summary>
    public static List<(string Kind, int Lanes)> FromRunJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Vms", out var vms)) return [];
        // ConvertTo-Json writes a single VM as an object, not a list
        var list = vms.ValueKind == JsonValueKind.Array ? vms.EnumerateArray().ToList() : vms.ValueKind == JsonValueKind.Object ? [vms] : [];
        return list.Where(v => v.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
            .Select(v => (v.GetProperty("Id").GetString()!,
                v.TryGetProperty("Lanes", out var l) ? l.ValueKind switch { JsonValueKind.Array => Math.Max(1, l.GetArrayLength()), JsonValueKind.String => 1, _ => 1 } : 1))
            .OrderBy(k => k.Item1, StringComparer.Ordinal).ToList();
    }
}