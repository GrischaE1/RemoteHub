using System.Diagnostics;

namespace RemoteHub.Tests;

/// <summary>
/// The lab's second attempt of a failed file (lab\LabRetry.ps1): the first results moved where the readers of
/// pester-*.xml don't count them, put back when the run ends first, the flaky cases told apart from the failures, and
/// their history over runs (Get-LabFlaky.ps1).
/// </summary>
public class LabRetryTests
{
    private static string Lab => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\lab"));

    private static (int Exit, string Output) PowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                $"$ErrorActionPreference = 'Stop'; . '{Path.Combine(Lab, "LabRetry.ps1")}'; " + script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // the PowerShell 7 SDK in this process changes PSModulePath; Windows PowerShell needs its own
        start.Environment.Remove("PSModulePath");
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "Windows PowerShell didn't end within 2 minutes");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>A Pester result as Invoke-LabTestFile.ps1 writes it: one test-case per (case ID, result).</summary>
    private static void PesterXml(string path, params (string Id, string Result)[] cases)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = string.Join("\n", cases.Select(c =>
            $"<test-case description=\"{c.Id} does a thing\" name=\"F VM.ctx.{c.Id} does a thing\" result=\"{c.Result}\">" +
            (c.Result == "Failure" ? $"<failure><message>{c.Id} broke</message></failure>" : "") + "</test-case>"));
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><test-results><test-suite><results>{body}</results></test-suite></test-results>");
    }

    private static T InTemp<T>(Func<string, T> test)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rh-labretry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { return test(dir); }
        finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
    }

    [Fact]
    public void A_case_that_failed_then_passed_is_flaky_and_one_that_failed_twice_is_not() => InTemp(dir =>
    {
        var kind = Path.Combine(dir, "W11");
        PesterXml(Path.Combine(kind, "pester-T-X.xml"), ("T-X-01", "Failure"), ("T-X-02", "Success"), ("T-X-03", "Failure"));
        File.WriteAllText(Path.Combine(kind, "pester-T-X.xml.summary.json"), "{\"Passed\":1,\"Failed\":2}");
        File.WriteAllText(Path.Combine(kind, "console-T-X.log"), "first");
        var (exit, output) = PowerShell($@"
            $first = Move-LabAttempt -KindDir '{kind}' -File 'T-X'
            'moved=' + (Split-Path $first -Leaf)
            'left=' + (@(Get-ChildItem '{kind}' -Filter 'pester-*' -File).Count)
            'counted=' + (@(Get-ChildItem '{kind}' -Recurse -Filter 'pester-*.xml').Count)
        ");
        Assert.True(exit == 0, output);
        Assert.Contains("moved=attempt1-T-X.xml", output);
        Assert.Contains("left=0", output);
        Assert.Contains("counted=0", output); // the readers of pester-*.xml (Get-LabFailures, LabData) don't see it
        Assert.True(File.Exists(Path.Combine(kind, "attempt1", "attempt1-console-T-X.log")));

        PesterXml(Path.Combine(kind, "pester-T-X.xml"), ("T-X-01", "Success"), ("T-X-02", "Success"), ("T-X-03", "Failure"));
        (exit, output) = PowerShell($@"
            Get-LabFlakyCases -FirstXml '{Path.Combine(kind, "attempt1", "attempt1-T-X.xml")}' -SecondXml '{Path.Combine(kind, "pester-T-X.xml")}' -File 'T-X' -Kind 'W11' -Vm 'W11-2' |
                ForEach-Object {{ 'flaky ' + $_.Id + ' ' + $_.Vm + ' ' + $_.Message }}
        ");
        Assert.True(exit == 0, output);
        Assert.Contains("flaky T-X-01 W11-2 T-X-01 broke", output);
        Assert.DoesNotContain("T-X-03", output); // failed both times: a failure of the run
        Assert.DoesNotContain("T-X-02", output); // passed both times
        return 0;
    });

    [Fact]
    public void After_a_time_limit_every_case_that_passed_the_second_time_is_flaky() => InTemp(dir =>
    {
        var second = Path.Combine(dir, "pester-T-Y.xml");
        PesterXml(second, ("T-Y-01", "Success"), ("T-Y-02", "Failure"));
        var (exit, output) = PowerShell($@"Get-LabFlakyCases -SecondXml '{second}' -File 'T-Y' -Kind 'Server' -TimedOut | ForEach-Object {{ 'flaky ' + $_.Id + ': ' + $_.Message }}");
        Assert.True(exit == 0, output);
        Assert.Contains("flaky T-Y-01: the first attempt ended at its time limit", output);
        Assert.DoesNotContain("T-Y-02", output);
        return 0;
    });

    [Fact]
    public void A_second_attempt_that_never_runs_gives_the_first_results_back() => InTemp(dir =>
    {
        PesterXml(Path.Combine(dir, "pester-T-Z.xml"), ("T-Z-01", "Failure"));
        File.WriteAllText(Path.Combine(dir, "console-T-Z.log"), "first");
        var (exit, output) = PowerShell($@"$null = Move-LabAttempt -KindDir '{dir}' -File 'T-Z'; Restore-LabAttempt -KindDir '{dir}' -File 'T-Z'");
        Assert.True(exit == 0, output);
        Assert.True(File.Exists(Path.Combine(dir, "pester-T-Z.xml")), "the run's failures must still count");
        Assert.True(File.Exists(Path.Combine(dir, "console-T-Z.log")));
        return 0;
    });

    [Fact]
    public void The_history_counts_failures_and_flaky_cases_over_finished_runs() => InTemp(root =>
    {
        void Run(string id, bool done, string flakyJson, params (string Id, string Result)[] cases)
        {
            var run = Path.Combine(root, id);
            PesterXml(Path.Combine(run, "W11", "pester-F.xml"), cases);
            File.WriteAllText(Path.Combine(run, "flaky.json"), flakyJson);
            if (done) File.WriteAllText(Path.Combine(run, "done.json"), "{}");
        }
        Run("20261101-100000", true, "[]", ("A-01", "Failure"), ("B-01", "Success"));
        // two flaky cases: Windows PowerShell 5.1 hands a JSON array on as one object, and once they were one row
        Run("20261102-100000", true, "[{\"Id\":\"B-01\",\"File\":\"F\",\"Kind\":\"W11\",\"Vm\":\"W11\",\"Message\":\"B-01 timed out\"}," +
                                     "{\"Id\":\"C-01\",\"File\":\"F\",\"Kind\":\"W11\",\"Vm\":\"W11\",\"Message\":\"C-01 slow\"}]",
            ("A-01", "Failure"), ("B-01", "Success"), ("C-01", "Success"));
        Run("20261103-100000", false, "[]", ("A-01", "Failure"));   // not finished: not counted
        var (exit, output) = PowerShell($@"Get-LabFlakyHistory -Root '{root}' | ForEach-Object {{ '{{0}} ran={{1}} failed={{2}} flaky={{3}} last={{4}} {{5}} msg={{6}}' -f $_.Id, $_.Ran, $_.Failed, $_.Flaky, $_.Last, $_.LastResult, $_.Message }}");
        Assert.True(exit == 0, output);
        Assert.Contains("A-01 ran=2 failed=2 flaky=0 last=20261102-100000 failed msg=A-01 broke", output);
        Assert.Contains("B-01 ran=2 failed=0 flaky=1 last=20261102-100000 flaky msg=B-01 timed out", output);
        Assert.Contains("C-01 ran=1 failed=0 flaky=1 last=20261102-100000 flaky msg=C-01 slow", output);
        return 0;
    });
}
