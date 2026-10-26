using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Controls;
using RemoteHub.Agent;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// A connection's Device actions (lock, message, sign out, restart, shut down, Group Policy, Intune): when they are
/// offered, the menu group, the scripts, what the device's answers are read as and what the user is told.
/// </summary>
public class DeviceActionsTests
{
    private sealed class FakeSource(Dictionary<string, int> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            scope == PolicyScope.Computer && key.Equals(PolicyCatalog.Root, StringComparison.OrdinalIgnoreCase) && values.TryGetValue(name, out var v) ? v : null;
    }

    private static FakeSource Set(params (string Name, int Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    private static readonly SignedInUser Anna = new(1, @"CONTOSO\anna"), Ben = new(3, @"PC1\ben");

    [Fact]
    public void Offered_where_PowerShell_is_available_over_either_transport_by_default()
    {
        Assert.True(DeviceActions.Offered(powerShellAvailable: true, viaAgent: true, Set()));
        Assert.True(DeviceActions.Offered(powerShellAvailable: true, viaAgent: false, Set()));
        Assert.False(DeviceActions.Offered(powerShellAvailable: false, viaAgent: true, Set()));
    }

    [Fact]
    public void Each_transport_has_its_own_switch_and_its_main_switch_wins()
    {
        Assert.False(DeviceActions.Offered(true, viaAgent: true, Set(("AllowAgentDeviceActions", 0))));
        Assert.True(DeviceActions.Offered(true, viaAgent: false, Set(("AllowAgentDeviceActions", 0))));
        Assert.False(DeviceActions.Offered(true, viaAgent: false, Set(("AllowRemotingDeviceActions", 0))));
        Assert.True(DeviceActions.Offered(true, viaAgent: true, Set(("AllowRemotingDeviceActions", 0))));
        Assert.False(DeviceActions.Offered(true, viaAgent: true, Set(("AllowAgent", 0), ("AllowAgentDeviceActions", 1))));
        Assert.False(DeviceActions.Offered(true, viaAgent: false, Set(("AllowPowerShellRemoting", 0), ("AllowRemotingDeviceActions", 1))));
    }

    [Fact]
    public void Device_actions_alone_still_count_as_a_tool_so_the_test_is_offered()
    {
        (string, int)[] tabsOff = [("AllowAgentPowerShell", 0), ("AllowAgentFileTransfer", 0), ("AllowAgentRegistry", 0), ("AllowAgentServices", 0), ("AllowAgentProcesses", 0)];
        Assert.True(Services.Features.AnyTool(viaAgent: true, Set(tabsOff)));
        Assert.False(Services.Features.AnyTool(viaAgent: true, Set([.. tabsOff, ("AllowAgentDeviceActions", 0)])));
    }

    [Fact]
    public void Every_script_parses_and_takes_its_parameters_first()
    {
        foreach (var (name, script) in DeviceActions.Scripts)
        {
            var ast = Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, $"{name}: {string.Join("; ", errors.Select(e => e.Message))}");
            // a param() after a function would be a syntax error, but a script without one must not use $Action or $Kind
            if (script.Contains("$Action") || script.Contains("$Kind")) Assert.True(ast.ParamBlock is not null, $"{name} has no param block");
        }
    }

    /// <summary>
    /// The devices run it in Windows PowerShell 5.1, where reading process owners needs an administrator's rights (7.x
    /// dropped that): elevated it lists only real accounts, one per session; otherwise it says why rather than "nobody".
    /// </summary>
    [Fact]
    public async Task The_signed_in_users_script_lists_real_accounts_or_says_it_lacks_an_administrators_rights()
    {
        var script = DeviceActions.Scripts.Single(s => s.Name == "SignedInScript").Script;
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var ps = System.Diagnostics.Process.Start(psi)!;
        var outputRead = ps.StandardOutput.ReadToEndAsync();
        var errorsRead = ps.StandardError.ReadToEndAsync();
        Assert.True(ps.WaitForExit(60_000), "Windows PowerShell didn't finish");
        var output = await outputRead;
        var errors = await errorsRead;
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Equal("", errors.Trim());
            var users = DeviceActions.ParseUsers(output.Split('\n', StringSplitOptions.TrimEntries));
            Assert.All(users, u =>
            {
                Assert.True(u.SessionId > 0);
                Assert.DoesNotContain("DWM-", u.Account);
                Assert.DoesNotContain("UMFD-", u.Account);
            });
            Assert.Equal(users.Count, users.Select(u => u.SessionId).Distinct().Count());
        }
        else Assert.Contains("isn't an administrator on the device", errors + output);
    }

    [Fact]
    public void A_message_becomes_one_line_without_characters_that_would_end_it_early()
    {
        Assert.Equal("Please save – restart at 5", DeviceActions.CleanMessage("  Please save – restart at 5 \r\n"));
        Assert.Equal("Say ”hi” now", DeviceActions.CleanMessage("Say \"hi\"\nnow"));
        Assert.Equal(@"C:\Temp", DeviceActions.CleanMessage(@"C:\Temp\\"));
        Assert.Equal(DeviceActions.MaxMessageLength, DeviceActions.CleanMessage(new string('x', 400)).Length);
        Assert.Equal("", DeviceActions.CleanMessage(" \t\r\n "));
    }

    [Fact]
    public void Answers_from_the_device_are_read_and_malformed_lines_skipped()
    {
        Assert.Equal([Anna, Ben], DeviceActions.ParseUsers(["1\tCONTOSO\\anna", "", "x\tbad", "2\t", "3\tPC1\\ben", "4\ta\tb"]));
        var outcomes = DeviceActions.ParseOutcomes(["1\tCONTOSO\\anna\t0\t", "3\tPC1\\ben\t5\tError 5\tAccess is denied.", "junk", "2\tx\tno\t"]);
        Assert.Equal([new SessionOutcome(1, @"CONTOSO\anna", 0, ""), new SessionOutcome(3, @"PC1\ben", 5, "Error 5\tAccess is denied.")], outcomes);
    }

    [Fact]
    public void The_confirmations_name_who_loses_unsaved_work_and_lock_asks_nothing()
    {
        var restart = DeviceActions.Confirmation(DeviceAction.Restart, "SRV01", [Anna, Ben, new(4, @"contoso\ANNA")])!.Value;
        Assert.Equal(("Restart device", "Restart"), (restart.Title, restart.Button));
        Assert.Contains(@"Signed in: CONTOSO\anna, PC1\ben – unsaved work", restart.Text); // once each, whatever the case
        Assert.StartsWith("Restart SRV01 now?", restart.Text);

        var off = DeviceActions.Confirmation(DeviceAction.ShutDown, "SRV01", [])!.Value;
        Assert.Contains("Nobody is signed in.", off.Text);
        Assert.Contains("can't switch it on again", off.Text);
        Assert.Equal(("Shut down device", "Shut down"), (off.Title, off.Button));

        Assert.Equal("Sign out", DeviceActions.Confirmation(DeviceAction.SignOut, "SRV01", [Anna])!.Value.Button);
        Assert.Null(DeviceActions.Confirmation(DeviceAction.Lock, "SRV01", [Anna]));
        Assert.Null(DeviceActions.Confirmation(DeviceAction.GroupPolicyUpdate, "SRV01", [Anna]));
        Assert.True(DeviceActions.NamesUsers(DeviceAction.Restart) && !DeviceActions.NamesUsers(DeviceAction.Lock));
    }

    [Fact]
    public void Each_session_is_reported_and_one_refusal_doesnt_hide_the_others()
    {
        var all = DeviceActions.DescribeSessions(DeviceAction.Lock, [new(1, @"CONTOSO\anna", 0, ""), new(3, @"PC1\ben", 0, "")], "SRV01");
        Assert.True(all.Ok);
        Assert.Equal(@"Locked on SRV01: CONTOSO\anna, PC1\ben.", all.Text);

        var part = DeviceActions.DescribeSessions(DeviceAction.SignOut, [new(1, @"CONTOSO\anna", 0, ""), new(3, @"PC1\ben", 1, "Access is denied.")], "SRV01");
        Assert.False(part.Ok);
        Assert.Contains(@"Signed out on SRV01: CONTOSO\anna.", part.Text);
        Assert.Contains(@"PC1\ben (session 3) couldn't be signed out: Access is denied.", part.Text);

        var silent = DeviceActions.DescribeSessions(DeviceAction.SendMessage, [new(3, @"PC1\ben", 7, "")], "SRV01");
        Assert.Equal(@"PC1\ben (session 3) couldn't be shown the message (exit code 7).", silent.Text);

        Assert.Equal("Nobody is signed in on SRV01 – there's nothing to lock.", DeviceActions.DescribeSessions(DeviceAction.Lock, [], "SRV01").Text);
    }

    [Fact]
    public void Group_Policy_and_Intune_say_what_happened()
    {
        Assert.True(DeviceActions.DescribeGroupPolicy(0, "Computer Policy update has completed successfully.", "SRV01").Ok);
        var gp = DeviceActions.DescribeGroupPolicy(1, "The processing of Group Policy failed.", "SRV01");
        Assert.False(gp.Ok);
        Assert.Contains("exit code 1", gp.Text);
        Assert.Contains("The processing of Group Policy failed.", gp.Text);

        Assert.True(DeviceActions.DescribeIntune(1, "PC1").Ok);
        var none = DeviceActions.DescribeIntune(0, "PC1");
        Assert.False(none.Ok);
        Assert.Contains("isn't enrolled", none.Text);
    }

    [Fact]
    public void The_audit_detail_names_the_action_and_result_never_the_message()
    {
        const string secret = "The new door code is 4711";
        var shown = DeviceActions.DescribeSessions(DeviceAction.SendMessage, [new(1, @"CONTOSO\anna", 0, "")], "PC1");
        Assert.DoesNotContain(secret, shown.Text);
        Assert.Equal("Done – Send message", DeviceActions.AuditDetail(DeviceAction.SendMessage, shown, null));
        Assert.Equal("Failed – Restart device", DeviceActions.AuditDetail(DeviceAction.Restart, null, "Access is denied."));
        Assert.Equal("Partly done or nothing to do – Lock device", DeviceActions.AuditDetail(DeviceAction.Lock, new(false, "x"), null));
        Assert.Contains(Data.AuditEvents.DeviceAction, Data.AuditEvents.Categories.SelectMany(c => c.Events));
        Assert.Equal("Device action", Data.AuditEvents.Label(Data.AuditEvents.DeviceAction));
    }

    [Fact]
    public void The_menu_group_lists_every_action_in_order_and_waits_while_one_runs()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var group = new MenuItem();
                var clicked = new List<DeviceAction>();
                MainWindow.FillDeviceActions(group, offered: true, busy: false, clicked.Add);
                var entries = group.Items.OfType<MenuItem>().ToList();
                Assert.Equal(["Lock", "Send message…", "Sign out users…", "Restart…", "Shut down…", "Update Group Policy", "Sync with Intune"],
                    entries.Select(e => (string)e.Header));
                // the separator sits between what interrupts users and what only refreshes the device's settings
                Assert.IsType<Separator>(group.Items[5]);
                Assert.All(entries, e => Assert.True(e.IsEnabled && e.ToolTip is string { Length: > 0 }));
                entries[3].RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal([DeviceAction.Restart], clicked);

                MainWindow.FillDeviceActions(group, offered: true, busy: true, clicked.Add);
                Assert.All(group.Items.OfType<MenuItem>(), e =>
                {
                    Assert.False(e.IsEnabled);
                    Assert.Equal("Another device action is still running on this device", e.ToolTip);
                });

                MainWindow.FillDeviceActions(group, offered: false, busy: false, clicked.Add);
                Assert.Empty(group.Items); // an empty group hides
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "the menu test did not finish");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
