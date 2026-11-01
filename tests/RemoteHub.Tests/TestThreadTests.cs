using System.Diagnostics;
using System.Threading;

namespace RemoteHub.Tests;

/// <summary>The tests' own threads: a stuck one fails its test in time instead of holding up the run.</summary>
public class TestThreadTests
{
    [Fact]
    public void A_thread_that_never_ends_fails_its_test_in_time_and_is_left_in_the_background()
    {
        using var never = new ManualResetEventSlim();
        var stuck = new Thread(() => never.Wait());
        stuck.Start();
        var clock = Stopwatch.StartNew();
        var failure = Assert.ThrowsAny<Exception>(() => TestThread.Join(stuck, "the stuck part", timeoutSeconds: 1));
        Assert.Equal("the stuck part didn't finish within 1 s", failure.Message);
        Assert.InRange(clock.ElapsedMilliseconds, 900, 10_000);
        Assert.True(stuck.IsBackground); // doesn't keep the test host alive
        never.Set();
        TestThread.Join(stuck, "the released part"); // and one that ends is simply waited for
    }
}
