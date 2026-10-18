extern alias agent;

using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The agent's log folder keeps files for the retention period: its own daily logs and the credential provider's trace,
/// which LogonUI writes into the same folder (docs/credential-provider-design.md §8.6).
/// </summary>
public class AgentLogRetentionTests
{
    [Fact]
    public void Old_agent_logs_and_provider_traces_are_removed_and_other_files_kept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rh-agentlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var old = DateTime.Now.AddDays(-10);
            string Make(string name, DateTime written)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, "x");
                File.SetLastWriteTime(path, written);
                return path;
            }
            var oldAgent = Make("agent-20000101.log", old);
            var oldProvider = Make("provider-20000101.log", old);
            var recentProvider = Make("provider-20000102.log", DateTime.Now.AddDays(-1));
            var other = Make("notes.txt", old);

            new A.AgentLog(folder, 3).Write("a line"); // the first entry of the day cleans up

            Assert.False(File.Exists(oldAgent));
            Assert.False(File.Exists(oldProvider));
            Assert.True(File.Exists(recentProvider));
            Assert.True(File.Exists(other));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
