extern alias agent;
using System.Text;

using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The agent MSI's BROKER_SECRET_FILE (review S-02): the device secret comes from a local file, so it is on no
/// process's command line during the install, and only a plain local file of a secret's size is read.
/// </summary>
public class AgentBrokerSecretFileTests
{
    private static string[] EnrollArgs(params string[] options) => ["enroll", @"C:\Deploy", "broker-url=https://rh.contoso.com", .. options];

    [Fact]
    public void Enroll_reads_the_broker_secret_from_a_file_so_it_is_on_no_command_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rh-secret-{Guid.NewGuid():N}.txt");
        try
        {
            // as Notepad or Set-Content -Encoding UTF8 writes it: a byte order mark, the secret, a line break, more
            File.WriteAllText(path, "rhd1.abc-DEF_123\r\nignored second line\r\n", new UTF8Encoding(true));
            var broker = A.Enrollment.Broker(EnrollArgs($"broker-secret-file={path}", "broker-secret=", "broker-only=1"));
            Assert.NotNull(broker);
            Assert.Equal("rhd1.abc-DEF_123", broker.Value.Secret);
            Assert.Equal("https://rh.contoso.com", broker.Value.Url);
            Assert.False(broker.Value.Listen);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Without_a_secret_file_the_inline_broker_secret_is_used()
    {
        Assert.Equal("rhd1.inline", A.Enrollment.Broker(EnrollArgs("broker-secret=rhd1.inline", "broker-secret-file="))!.Value.Secret);
        Assert.Null(A.Enrollment.Broker(["enroll", @"C:\Deploy", "broker-url=", "broker-secret=rhd1.inline"]));
    }

    [Fact]
    public void A_broker_secret_file_on_a_share_relative_missing_or_too_large_is_refused()
    {
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile(@"\\server\deploy\secret.txt"));
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile(@"\\?\C:\Deploy\secret.txt"));
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile("secret.txt"));
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile("C:secret.txt"));
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile(Path.Combine(Path.GetTempPath(), $"rh-missing-{Guid.NewGuid():N}.txt")));
        Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile(Path.GetTempPath()));
        var large = Path.Combine(Path.GetTempPath(), $"rh-secret-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(large, "rhd1." + new string('a', A.Enrollment.MaxSecretFileBytes));
            Assert.Throws<ArgumentException>(() => A.Enrollment.ReadSecretFile(large));
        }
        finally { File.Delete(large); }
    }
}
