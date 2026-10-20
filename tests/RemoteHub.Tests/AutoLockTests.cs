using RemoteHub.Views;

namespace RemoteHub.Tests;

/// <summary>Lock when you're away: the times offered, and when the lock is due.</summary>
public class AutoLockTests
{
    [Fact]
    public void One_minute_is_offered_after_Never_and_the_times_go_up()
    {
        var choices = StartupProtectionWindow.LockAfterChoices;
        Assert.Equal((0, "Never"), choices[0]);
        Assert.Equal((1, "After 1 minute"), choices[1]);
        Assert.Equal([0, 1, 5, 15, 30, 60], choices.Select(c => c.Minutes));
        Assert.Equal(choices.Length, choices.Select(c => c.Label).Distinct().Count());
    }

    [Theory]
    [InlineData(1, "After 1 minute")]
    [InlineData(20, "After 20 minutes")]
    public void A_time_set_by_policy_gets_a_label(int minutes, string label) =>
        Assert.Equal(label, StartupProtectionWindow.LockAfterLabel(minutes));

    [Theory]
    [InlineData(1, 59, false)]
    [InlineData(1, 60, true)]
    [InlineData(5, 299, false)]
    [InlineData(5, 300, true)]
    [InlineData(0, 86400, false)] // never
    public void The_lock_is_due_once_there_was_no_input_for_the_time(int minutes, int idleSeconds, bool due) =>
        Assert.Equal(due, MainWindow.LockDue(minutes, TimeSpan.FromSeconds(idleSeconds)));
}
