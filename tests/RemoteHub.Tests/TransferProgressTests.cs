using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>The progress text of a copy in the remote desktop tab: "X of Y · speed · time left".</summary>
public class TransferProgressTests
{
    [Fact]
    public void Progress_shows_size_then_speed_and_time_left()
    {
        var meter = new AgentDesktopSession.TransferMeter(40L * 1024 * 1024);
        var t0 = DateTime.UtcNow;
        Assert.Equal("0 B of 40 MB", meter.Detail(0, t0));
        // 10 MB in one second: 10 MB/s, 30 MB left = about 3 s.
        Assert.Equal("10 MB of 40 MB · 10 MB/s · about 3 s left", meter.Detail(10L * 1024 * 1024, t0.AddSeconds(1)));
    }

    [Theory]
    [InlineData(0.4, "almost done")]
    [InlineData(5.2, "about 6 s left")]
    [InlineData(59, "about 59 s left")]
    [InlineData(61, "about 2 min left")]
    public void Time_left_is_rounded_up(double seconds, string expected) =>
        Assert.Equal(expected, AgentDesktopSession.TransferMeter.Left(seconds));
}
