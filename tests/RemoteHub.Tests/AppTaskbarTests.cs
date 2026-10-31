extern alias agent;
using Microsoft.Win32;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// App tabs turn "show my taskbar on all displays" off for the session's user and put it back exactly as it was – the
/// value or none – by the last tab's helper, the next helper or the service after one that ended abruptly. A scratch key
/// under HKCU stands in for the user's hive, so this machine's taskbar isn't touched.
/// </summary>
[Collection("SasActions")]
public class AppTaskbarTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = @"Software\RemoteHubTests\AppTaskbar-" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rh-taskbar-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        try { Directory.Delete(_folder, true); } catch { /* none */ }
    }

    private A.AppTaskbar New() => new(_folder, sid => Registry.CurrentUser.CreateSubKey($@"{_root}\{sid}", writable: true));

    private object? Value()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{_root}\{Sid}\{A.AppTaskbar.Key}");
        return k?.GetValue(A.AppTaskbar.Name);
    }

    private void SetValue(int v)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{_root}\{Sid}\{A.AppTaskbar.Key}");
        k.SetValue(A.AppTaskbar.Name, v, RegistryValueKind.DWord);
    }

    private void SetScale(string monitor, int? v)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{_root}\{Sid}\{A.AppTaskbar.ScaleKey}\{monitor}");
        if (v is { } value) k.SetValue(A.AppTaskbar.ScaleName, value, RegistryValueKind.DWord);
    }

    private object? ScaleOf(string monitor)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{_root}\{Sid}\{A.AppTaskbar.ScaleKey}\{monitor}");
        return k?.GetValue(A.AppTaskbar.ScaleName);
    }

    [Fact]
    public void The_virtual_monitors_scale_comes_back_as_it_was_and_the_users_own_monitors_are_left_alone()
    {
        SetScale("MTT1337518463207_1C_07E8^AA", 1);   // a scale the user chose on the virtual monitor
        SetScale("MTT1337518463207_1C_07E9^BB", null); // an entry without one
        SetScale("DEL41A8^CC", 2);                     // the user's own monitor
        var t = New();
        Assert.True(t.TurnOff(Sid));

        // App tabs set their scales; Windows keeps them in the profile, also for a monitor it hadn't seen before
        SetScale("MTT1337518463207_1C_07E8^AA", 2);
        SetScale("MTT1337518463207_1C_07E9^BB", -1);
        SetScale("MTT1337518463207_1C_07EA^DD", 3);
        SetScale("DEL41A8^CC", 3);                     // the user, meanwhile, at the device
        Assert.True(t.PutBack());

        Assert.Equal(1, ScaleOf("MTT1337518463207_1C_07E8^AA"));
        Assert.Null(ScaleOf("MTT1337518463207_1C_07E9^BB"));
        Assert.Null(ScaleOf("MTT1337518463207_1C_07EA^DD"));
        Assert.Equal(3, ScaleOf("DEL41A8^CC"));
    }

    [Fact]
    public void A_scale_left_by_a_helper_that_ended_abruptly_is_put_back_by_the_service()
    {
        SetScale("MTT1337518463207_1C_07E8^AA", 0);
        Assert.True(New().TurnOff(Sid)); // never put back: its helper died
        SetScale("MTT1337518463207_1C_07E8^AA", 2);
        Assert.NotNull(New().RestoreLeftovers());
        Assert.Equal(0, ScaleOf("MTT1337518463207_1C_07E8^AA"));
    }

    [Fact]
    public void Off_while_a_tab_is_open_then_absent_again()
    {
        var t = New();
        Assert.True(t.TurnOff(Sid));
        Assert.Equal(0, Value());
        Assert.True(t.PutBack());
        Assert.Null(Value());
        Assert.False(t.Off);
    }

    [Fact]
    public void A_value_the_user_had_comes_back_as_it_was()
    {
        SetValue(1);
        var t = New();
        t.TurnOff(Sid);
        Assert.Equal(0, Value());
        t.PutBack();
        Assert.Equal(1, Value());
    }

    [Fact]
    public void The_last_tab_puts_it_back_and_the_record_keeps_the_original()
    {
        SetValue(1);
        var first = New();
        var second = New();
        first.TurnOff(Sid);
        second.TurnOff(Sid); // already 0: the record still says 1
        Assert.False(first.PutBack());
        Assert.Equal(0, Value());
        Assert.True(second.PutBack());
        Assert.Equal(1, Value());
    }

    [Fact]
    public void After_a_helper_that_ended_abruptly_the_next_one_or_the_service_puts_it_back()
    {
        SetValue(1);
        New().TurnOff(Sid);
        // the helper is gone: its owner file names a process that no longer runs
        foreach (var owner in Directory.GetFiles(_folder, "taskbar-owner-*.txt")) File.Delete(owner);
        File.WriteAllText(Path.Combine(_folder, $"taskbar-owner-{Sid}-999999-dead.txt"), "");
        Assert.True(New().Reconcile(Sid));
        Assert.Equal(1, Value());

        New().TurnOff(Sid);
        Assert.NotNull(New().RestoreLeftovers());
        Assert.Equal(1, Value());
        Assert.Null(New().RestoreLeftovers()); // nothing left
    }
}
