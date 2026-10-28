extern alias agent;
using System.Security.Principal;
using Microsoft.Win32;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// "RDP windows keep drawing when minimized" while the admin is connected: set for the session's user, and put back
/// exactly as it was – absent, or the user's own value – also after a helper that ended without putting it back.
/// Uses the current user's own key (restored at the end of each test).
/// </summary>
[Collection("KeepRdpDrawing")]
public class KeepRdpDrawingTests : IDisposable
{
    private const string KeyPath = @"Software\Microsoft\Terminal Server Client", Name = "RemoteDesktop_SuppressWhenMinimized";
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rh-rdpmin-" + Guid.NewGuid().ToString("N"));
    private readonly object? _original;

    public KeepRdpDrawingTests()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        _original = key.GetValue(Name);
        key.DeleteValue(Name, false);
    }

    public void Dispose()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (_original is int v) key.SetValue(Name, v, RegistryValueKind.DWord); else key.DeleteValue(Name, false);
        try { Directory.Delete(_folder, true); } catch { /* none */ }
    }

    private static object? Value()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(Name);
    }

    [Fact]
    public void Set_while_connected_and_removed_again_when_there_was_none()
    {
        using (var keep = new A.KeepRdpDrawing(_folder))
        {
            Assert.NotNull(keep.Apply(_sid));
            Assert.Equal(2, Value());
            Assert.Null(keep.Apply(_sid)); // once
        }
        Assert.Null(Value());
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void The_users_own_value_comes_back_and_their_own_2_is_left_alone()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) key.SetValue(Name, 1, RegistryValueKind.DWord);
        using (var keep = new A.KeepRdpDrawing(_folder)) keep.Apply(_sid);
        Assert.Equal(1, Value());

        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) key.SetValue(Name, 2, RegistryValueKind.DWord);
        using (var keep = new A.KeepRdpDrawing(_folder)) Assert.Null(keep.Apply(_sid));
        Assert.Equal(2, Value());
    }

    [Fact]
    public void A_helper_that_ended_abruptly_is_made_good_by_the_next_connection()
    {
        var crashed = new A.KeepRdpDrawing(_folder);
        crashed.Apply(_sid); // never disposed
        Assert.Equal(2, Value());
        using (var next = new A.KeepRdpDrawing(_folder)) next.Apply(_sid);
        Assert.Null(Value()); // what was there before the first one
    }

    [Fact]
    public void A_record_left_by_a_crashed_helper_is_restored_at_service_start()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath)) key.SetValue(Name, 1, RegistryValueKind.DWord);
        new A.KeepRdpDrawing(_folder).Apply(_sid); // never disposed
        Assert.Equal(2, Value());
        // Another user's record whose profile isn't loaded stays for later.
        var absent = Path.Combine(_folder, "rdp-minimized-S-1-5-21-1-2-3-999999.txt");
        File.WriteAllText(absent, "absent");

        Assert.NotNull(new A.KeepRdpDrawing(_folder).RestoreLeftovers());
        Assert.Equal(1, Value());
        Assert.Equal([absent], Directory.GetFiles(_folder));
        Assert.Null(new A.KeepRdpDrawing(_folder).RestoreLeftovers()); // nothing left that can be put back
    }
}
