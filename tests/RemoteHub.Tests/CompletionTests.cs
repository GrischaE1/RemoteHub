using System.Management.Automation;
using System.Management.Automation.Runspaces;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

public class CompletionTests
{
    [Fact]
    public void Completion_reads_live_and_deserialized_results()
    {
        using var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        ps.AddCommand("TabExpansion2").AddParameter("inputScript", "Get-ChildItem -Pa").AddParameter("cursorColumn", 17);
        var live = ps.Invoke().Single();

        // The deserialized (remote) form is covered by Completion_works_against_Windows_PowerShell_51.
        foreach (var r in new[] { live })
        {
            var set = CompletionSet.From(r)!;
            Assert.Equal(14, set.ReplacementIndex);
            Assert.Equal(3, set.ReplacementLength);
            Assert.Contains(set.Matches, m => m.CompletionText == "-Path" && m.ResultType == "ParameterName");
        }
    }

    /// <summary>
    /// Windows PowerShell 5.1 as the "remote" side, over the same remoting protocol as WinRM (out-of-process transport).
    /// This is what a typical Windows Server target runs.
    /// </summary>
    [SkippableFact]
    public void Completion_works_against_Windows_PowerShell_51()
    {
        Skip.IfNot(File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")), "Windows PowerShell 5.1 isn't installed.");
        using var proc = new PowerShellProcessInstance(new Version(5, 1), null, null, false);
        using var rs = RunspaceFactory.CreateOutOfProcessRunspace(null, proc);
        rs.Open();
        using (var v = PowerShell.Create())
        {
            v.Runspace = rs;
            Assert.Equal(5, (int)v.AddScript("$PSVersionTable.PSVersion.Major").Invoke()[0].BaseObject);
        }
        var cmd = PsSession.CompleteOn(rs, "Get-ChildI", 10);
        Assert.NotNull(cmd);
        Assert.Contains(cmd!.Matches, m => m.CompletionText == "Get-ChildItem");
        var param = PsSession.CompleteOn(rs, "Get-Service -Na", 15);
        Assert.Contains(param!.Matches, m => m.CompletionText == "-Name");
        Assert.Equal(12, param.ReplacementIndex);
    }
}
