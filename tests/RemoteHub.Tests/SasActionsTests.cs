extern alias agent;
using Microsoft.Win32;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// While local input is blocked, the Ctrl+Alt+Del screen offers only Cancel: the policy values are set, and put back
/// exactly as they were – absent, or the value that was there – also after a helper that ended without putting them
/// back. Scratch keys under HKCU stand in for the users' hives and HKLM, so nothing on this machine is hidden.
/// </summary>
[Collection("SasActions")]
public class SasActionsTests : IDisposable
{
    private const string Sid1 = "S-1-5-21-1-2-3-1001", Sid2 = "S-1-5-21-1-2-3-1002";
    private readonly string _root = @"Software\RemoteHubTests\SasActions-" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rh-sas-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _machine;

    public SasActionsTests() => _machine = Registry.CurrentUser.CreateSubKey(_root + @"\machine");

    public void Dispose()
    {
        _machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        try { Directory.Delete(_folder, true); } catch { /* none */ }
    }

    private A.SasActions New() => new(_folder, sid => Registry.CurrentUser.CreateSubKey($@"{_root}\{sid}", writable: true), _machine);

    private object? User(string sid, string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"{_root}\{sid}\{key}");
        return k?.GetValue(name);
    }

    private object? Machine(string key, string name)
    {
        using var k = _machine.OpenSubKey(key);
        return k?.GetValue(name);
    }

    private void SetUser(string sid, string key, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{_root}\{sid}\{key}");
        k.SetValue(name, value, kind);
    }

    private void AllHidden(string sid)
    {
        foreach (var (key, name) in A.SasActions.UserValues) Assert.Equal(1, User(sid, key, name));
        foreach (var (key, name) in A.SasActions.MachineValues) Assert.Equal(1, Machine(key, name));
    }

    [Fact]
    public void Hidden_while_blocked_and_removed_again_when_there_were_none()
    {
        var sas = New();
        Assert.NotNull(sas.Hide(Sid1));
        Assert.True(sas.Hidden);
        AllHidden(Sid1);
        Assert.Null(sas.Hide(Sid1)); // once

        Assert.NotNull(sas.Show());
        Assert.False(sas.Hidden);
        foreach (var (key, name) in A.SasActions.UserValues) Assert.Null(User(Sid1, key, name));
        foreach (var (key, name) in A.SasActions.MachineValues) Assert.Null(Machine(key, name));
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Null(sas.Show()); // nothing to put back
    }

    [Fact]
    public void Values_that_were_there_come_back_and_a_non_DWORD_is_left_alone()
    {
        var (system, explorer) = (A.SasActions.UserValues[0].Key, A.SasActions.UserValues[3].Key);
        SetUser(Sid1, system, "DisableTaskMgr", 0);
        SetUser(Sid1, explorer, "NoClose", "keep", RegistryValueKind.String);
        using (var k = _machine.CreateSubKey(A.SasActions.MachineValues[0].Key)) k.SetValue("HideFastUserSwitching", 1, RegistryValueKind.DWord);

        var sas = New();
        sas.Hide(Sid1);
        Assert.Equal(1, User(Sid1, system, "DisableTaskMgr"));
        Assert.Equal("keep", User(Sid1, explorer, "NoClose"));
        sas.Show();

        Assert.Equal(0, User(Sid1, system, "DisableTaskMgr"));
        Assert.Equal("keep", User(Sid1, explorer, "NoClose"));
        Assert.Null(User(Sid1, system, "DisableLockWorkstation"));
        Assert.Equal(1, Machine(A.SasActions.MachineValues[0].Key, "HideFastUserSwitching")); // it was 1 before
    }

    [Fact]
    public void A_helper_that_ended_abruptly_is_made_good_by_the_next_one_and_by_the_service()
    {
        var crashed = New();
        crashed.Hide(Sid1); // never shown again
        var next = New();
        next.Hide(Sid1); // the record from before is the original
        next.Show();
        foreach (var (key, name) in A.SasActions.UserValues) Assert.Null(User(Sid1, key, name));

        New().Hide(Sid1); // crashed again; the service starts
        Assert.NotNull(New().RestoreLeftovers());
        foreach (var (key, name) in A.SasActions.UserValues) Assert.Null(User(Sid1, key, name));
        foreach (var (key, name) in A.SasActions.MachineValues) Assert.Null(Machine(key, name));
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Null(New().RestoreLeftovers()); // nothing left
    }

    [Fact]
    public void Switch_user_stays_hidden_until_the_last_session_that_hid_it_is_done()
    {
        var (key, name) = A.SasActions.MachineValues[0];
        var one = New();
        var two = New();
        one.Hide(Sid1);
        two.Hide(Sid2);
        one.Show();
        Assert.Equal(1, Machine(key, name));
        foreach (var (k, n) in A.SasActions.UserValues) Assert.Null(User(Sid1, k, n));
        AllHidden(Sid2);
        two.Show();
        Assert.Null(Machine(key, name));
    }
}
