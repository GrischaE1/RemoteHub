using System.Threading;
using RemoteHub.Remoting;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

public class TransferSpeedTests
{
    private static TransferItem Item(long size) => TransferItem.Download(@"C:\remote\f.bin", @"C:\local\f.bin", "f.bin", "test", size);

    [Fact]
    public void Per_file_speed_shows_while_running_and_the_average_when_done()
    {
        var big = Item(8 * 1024 * 1024);
        big.State = TransferState.Running;
        big.SentBytes = 1024 * 1024;
        Thread.Sleep(300);
        big.SentBytes = 3 * 1024 * 1024;
        Assert.EndsWith("/s", big.SpeedText);
        Assert.DoesNotContain("avg", big.SpeedText);
        big.SentBytes = 8 * 1024 * 1024;
        big.State = TransferState.Done;
        Assert.StartsWith("avg ", big.SpeedText);
        Assert.True(big.AverageRate > 0);

        var small = Item(4096);                      // below 1 MB: no per-file speed (meaningless for batched small files)
        small.State = TransferState.Running;
        small.SentBytes = 4096;
        small.State = TransferState.Done;
        Assert.Equal("", small.SpeedText);
    }

    [Fact]
    public void Overall_average_ignores_idle_gaps_and_counts_overlaps_once()
    {
        var a = Item(100); var b = Item(100); var c = Item(100);
        foreach (var t in new[] { a, b, c }) { t.State = TransferState.Running; t.SentBytes = 100; t.State = TransferState.Done; }
        var now = DateTime.UtcNow;
        Set(a, now.AddSeconds(-100), now.AddSeconds(-98));   // 2 s
        Set(b, now.AddSeconds(-99), now.AddSeconds(-97));    // overlaps a → together 3 s
        Set(c, now.AddSeconds(-10), now.AddSeconds(-9));     // 1 s, after an idle gap of 87 s
        Assert.Equal(300 / 4.0, MainViewModel.AverageTransferRate([a, b, c], now), 3);
        Assert.Equal(0, MainViewModel.AverageTransferRate([Item(5)], now));
    }

    private static void Set(TransferItem t, DateTime start, DateTime end)
    {
        typeof(TransferItem).GetProperty(nameof(TransferItem.StartedUtc))!.SetValue(t, start);
        typeof(TransferItem).GetProperty(nameof(TransferItem.FinishedUtc))!.SetValue(t, end);
    }
}
