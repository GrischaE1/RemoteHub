extern alias agent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The broker's unsealed "orgDeleted" message (review S-04): the agent doesn't trust the broker, so it stops the link
/// for an hour and keeps its broker keys – only <c>broker leave</c> removes them.
/// </summary>
public class BrokerLinkOrgDeletedTests
{
    [Fact]
    public void An_orgDeleted_message_stops_the_link_but_keeps_the_keys_in_agent_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rh-agent-{Guid.NewGuid():N}.json");
        try
        {
            var broker = new A.BrokerSettings
            {
                Url = "https://rh.contoso.com",
                Org = "o_abcdefghijklmnop",
                SecretId = "s_abcdefgh",
                EnrollKey = A.BrokerProtocol.B64u(RandomNumberGenerator.GetBytes(32)),
                RecordKey = A.BrokerProtocol.B64u(RandomNumberGenerator.GetBytes(32)),
            };
            var config = new A.AgentConfig { DeviceId = Guid.NewGuid().ToString(), Broker = broker };
            config.Save(path);
            config = A.AgentConfig.Load(path);

            using var key = RSA.Create(2048);
            using var cert = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var link = new A.BrokerLink(() => config, cert, () => A.AgentPolicy.Defaults(config), new A.AgentLog("", 1), new A.PeerTransport());

            Assert.Equal(A.BrokerLink.Outcome.Stop, link.OrgDeleted(config.Broker!, new Uri("https://rh.contoso.com")));

            var kept = A.AgentConfig.Load(path).Broker;
            Assert.NotNull(kept);
            Assert.Equal(broker.EnrollKey, kept.EnrollKey);
            Assert.Equal(broker.RecordKey, kept.RecordKey);
            Assert.Equal("o_abcdefghijklmnop", kept.Org);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
        }
    }
}
