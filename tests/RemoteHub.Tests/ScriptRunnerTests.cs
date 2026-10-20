using System.Collections.Generic;
using System.Threading.Tasks;
using System.Management.Automation.Runspaces;
using System.Threading;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Remoting;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// Running a script (docs/scripts-view-design.md §6.2): the device-side script, run here in a local session as "As me" –
/// a child process of the session, in the account's temp folder – so nothing is registered with Task Scheduler and
/// nothing runs as SYSTEM on the test machine. System and User runs are covered by the lab (S-SCRIPT).
/// </summary>
public class ScriptRunnerTests
{
    private static readonly PsTarget Nowhere = new("localhost", false, new CredentialSet());

    private static Runspace Local()
    {
        var rs = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        rs.Open();
        return rs;
    }

    private static bool HasWindowsPowerShell =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"));

    private static ScriptRunner Runner(string body, int timeoutSec = ScriptStore.DefaultTimeoutSec, IReadOnlyDictionary<string, object?>? args = null) =>
        new(new ScriptRunRequest(Nowhere, "node", "script", 1,
            new ScriptContent(body, "", ScriptStore.ContextAsMe, ScriptStore.RiskR0, ScriptStore.OsBoth, timeoutSec), ScriptStore.ContextAsMe, null, args), Local);

    private static string RunsFolder => Path.Combine(Path.GetTempPath(), "RemoteHub-Runs");

    [SkippableFact]
    public async Task A_script_runs_with_its_output_and_exit_code_and_leaves_nothing_behind()
    {
        Skip.IfNot(HasWindowsPowerShell, "needs Windows PowerShell 5.1");
        var runner = Runner("Write-Output 'hello from the device'\r\nWrite-Warning 'careful'\r\nexit 3");
        var seen = new System.Text.StringBuilder();
        runner.OutputReceived += t => { lock (seen) seen.Append(t); };
        var result = await runner.RunAsync(CancellationToken.None);
        Assert.True(result.ExitCode == 3, result.Message + " | " + runner.Output);
        Assert.Equal(ScriptRunState.Failed, result.State);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("hello from the device", runner.Output);
        Assert.Contains("careful", runner.Output);
        Assert.Equal(runner.Output, seen.ToString());
        Assert.False(Directory.Exists(Path.Combine(RunsFolder, runner.RunId)), "the run folder was left behind");
    }

    [SkippableFact]
    public async Task Exit_codes_0_and_3010_succeed_and_a_terminating_error_fails()
    {
        Skip.IfNot(HasWindowsPowerShell, "needs Windows PowerShell 5.1");
        Assert.Equal(ScriptRunState.Succeeded, (await Runner("'ok'").RunAsync(CancellationToken.None)).State);
        Assert.Equal(ScriptRunState.SucceededRestart, (await Runner("exit 3010").RunAsync(CancellationToken.None)).State);
        var thrown = Runner("throw 'it broke'");
        var result = await thrown.RunAsync(CancellationToken.None);
        Assert.Equal((ScriptRunState.Failed, 1), (result.State, result.ExitCode));
        Assert.Contains("it broke", thrown.Output);
    }

    [SkippableFact]
    public async Task Parameters_reach_the_script_by_name()
    {
        Skip.IfNot(HasWindowsPowerShell, "needs Windows PowerShell 5.1");
        var runner = Runner("param([string]$Name, [int]$Days, [switch]$Apply, [string[]]$Hosts)\r\n\"$Name/$Days/$Apply/$($Hosts -join '+')\"",
            args: new Dictionary<string, object?> { ["Name"] = "it's \"quoted\" `$x", ["Days"] = 45L, ["Apply"] = true, ["Hosts"] = new[] { "a", "b" } });
        await runner.RunAsync(CancellationToken.None);
        Assert.Contains("it's \"quoted\" `$x/45/True/a+b", runner.Output);
    }

    [SkippableFact]
    public async Task Text_is_passed_as_a_file_and_survives_quotes_and_non_ascii()
    {
        Skip.IfNot(HasWindowsPowerShell, "needs Windows PowerShell 5.1");
        var runner = Runner("$s = 'Größe – \"x\" `$y'; [Console]::OutputEncoding = [Text.Encoding]::UTF8; Write-Output $s");
        await runner.RunAsync(CancellationToken.None);
        Assert.Contains("Größe – \"x\" `$y", runner.Output);
    }

    [SkippableFact]
    public async Task A_run_past_its_timeout_is_stopped()
    {
        Skip.IfNot(HasWindowsPowerShell, "needs Windows PowerShell 5.1");
        var runner = new ScriptRunner(new ScriptRunRequest(Nowhere, "node", "script", 1,
            new ScriptContent("'started'; Start-Sleep -Seconds 300", "", ScriptStore.ContextAsMe, null, null, 1), ScriptStore.ContextAsMe), Local);
        // The minimum timeout is a minute; the test cancels instead, which takes the same path (stop, read the rest, clean up).
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(cts.Token);
        Assert.True(SpinWait.SpinUntil(() => runner.Output.Contains("started"), TimeSpan.FromSeconds(60)), "the script didn't start");
        cts.Cancel();
        var result = await run;
        Assert.Equal(ScriptRunState.Cancelled, result.State);
        Assert.True(result.Duration < TimeSpan.FromSeconds(60));
        Assert.False(Directory.Exists(Path.Combine(RunsFolder, runner.RunId)), "the run folder was left behind");
    }

    [Fact]
    public void The_devices_refusals_are_explained()
    {
        Assert.Equal("Running as System or User needs an administrator account on the device.", ScriptRunner.Refusal(new Exception("NOT_ADMIN: x")));
        Assert.Equal("The device's group policy only allows signed scripts.", ScriptRunner.Refusal(new Exception("GPO_POLICY: MachinePolicy is AllSigned")));
        Assert.Equal("No user is signed in on the device.", ScriptRunner.Refusal(new Exception("NO_USER: x")));
        Assert.Equal("PowerShell 7 isn't installed on the device.", ScriptRunner.Refusal(new Exception("NO_PWSH: x")));
        Assert.Null(ScriptRunner.Refusal(new Exception("Access is denied")));
    }

    [Fact]
    public void PowerShell_7_is_asked_for_with_requires()
    {
        Assert.True(ScriptRunner.WantsPowerShell7("#Requires -PSEdition Core\nGet-Date"));
        Assert.False(ScriptRunner.WantsPowerShell7("# needs: #Requires -PSEdition Core\nGet-Date"));
        Assert.False(ScriptRunner.WantsPowerShell7("#Requires -Version 5.1"));
    }

    [Fact]
    public void The_device_script_is_in_the_app_ascii_only_and_parses()
    {
        var text = ScriptRunner.DeviceScript;
        Assert.All(text, ch => Assert.True(ch < 0x80 || ch == '﻿', $"non-ASCII character U+{(int)ch:X4}"));
        System.Management.Automation.Language.Parser.ParseInput(text, out _, out var errors);
        Assert.Empty(errors);
        // The script text never goes on a command line: it is written to a file in the run folder.
        Assert.Contains("[IO.File]::WriteAllText((Join-Path $dir 'script.ps1'), $ScriptText", text);
        Assert.DoesNotContain("-EncodedCommand", text);
    }
}
