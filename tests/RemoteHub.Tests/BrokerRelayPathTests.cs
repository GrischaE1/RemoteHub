extern alias agent;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// The relay address in a broker's offer (agent) and answer (console) (review S-06): only the broker's own path for that
/// organisation and session is used – anything that would resolve to another host or session is refused.
/// </summary>
public class BrokerRelayPathTests
{
    private const string Org = "o_abcdefghijklmnop", Session = "abcdefghijklmnopqrstuv";

    [Fact]
    public void The_brokers_own_relay_path_is_accepted() =>
        Assert.True(A.BrokerProtocol.IsRelayPath($"/v1/o/{Org}/relay?session={Session}", Org, Session));

    [Theory]
    [InlineData("//other.example/v1/o/o_abcdefghijklmnop/relay?session=abcdefghijklmnopqrstuv")]
    [InlineData("wss://other.example/v1/o/o_abcdefghijklmnop/relay?session=abcdefghijklmnopqrstuv")]
    [InlineData("\\\\other.example/v1/o/o_abcdefghijklmnop/relay?session=abcdefghijklmnopqrstuv")]
    [InlineData("/v1/o/o_bbbbbbbbbbbbbbbb/relay?session=abcdefghijklmnopqrstuv")] // another organisation
    [InlineData("/v1/o/o_abcdefghijklmnop/relay?session=zzzzzzzzzzzzzzzzzzzzzz")] // another session
    [InlineData("/v1/o/o_abcdefghijklmnop/relay?session=abcdefghijklmnopqrstuv&x=1")]
    [InlineData("/v1/o/o_abcdefghijklmnop/../../x/relay?session=abcdefghijklmnopqrstuv")]
    [InlineData("")]
    public void A_relay_address_on_another_host_or_for_another_session_is_refused(string relay) =>
        Assert.False(A.BrokerProtocol.IsRelayPath(relay, Org, Session));
}
