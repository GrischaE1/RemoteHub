extern alias agent;
using System.Collections.Generic;

using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Policies phase 2 (docs/admx-design.md §9, §11.3): the agent values that were in agent.json only, PowerShellEnabled,
/// the policy command, what health tells RemoteHub, and automatic sign-in moving out of the policy key.
/// </summary>
public class AgentPolicyPhase2Tests
{
    private const string Admins = "S-1-5-32-544", Helpdesk = "S-1-5-21-1-2-3-1100";

    private static A.AgentPolicy From(A.AgentConfig config, Dictionary<string, object>? values) =>
        A.AgentPolicy.From(config, values is null ? null : new PolicyTemplateTests.Values(values));

    [Fact]
    public void Values_from_agent_json_apply_unless_the_policy_sets_them()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins], VirtualDisplay = "always", ListenPort = 60000, LogRetentionDays = 7, MaxSessions = 5 };
        var d = From(config, null);
        Assert.Equal(("always", 60000, 7, 5), (d.VirtualDisplay, d.ListenPort, d.LogRetentionDays, d.MaxSessions));
        Assert.True(d.PowerShellEnabled);
        Assert.Null(d.PrivacyScreenMessage);

        var p = From(config, new()
        {
            ["VirtualDisplay"] = "OFF", ["ListenPort"] = 61000, ["LogRetentionDays"] = 90, ["MaxSessions"] = 50,
            ["PowerShellEnabled"] = 0, ["PrivacyScreenMessage"] = "Contoso IT, the helpdesk; is working on this PC",
        });
        Assert.Equal(("off", 61000, 90, 50), (p.VirtualDisplay, p.ListenPort, p.LogRetentionDays, p.MaxSessions));
        Assert.False(p.PowerShellEnabled);
        Assert.Equal("Contoso IT, the helpdesk; is working on this PC", p.PrivacyScreenMessage); // free text: not split at , or ;

        // Invalid values keep what agent.json says.
        var bad = From(config, new()
        {
            ["VirtualDisplay"] = "sometimes", ["ListenPort"] = 70000, ["LogRetentionDays"] = 0, ["MaxSessions"] = 201,
            ["PrivacyScreenMessage"] = new string('x', 121),
        });
        Assert.Equal(("always", 60000, 7, 5), (bad.VirtualDisplay, bad.ListenPort, bad.LogRetentionDays, bad.MaxSessions));
        Assert.Null(bad.PrivacyScreenMessage);
    }

    [Fact]
    public void AllowedGroups_replaces_the_install_groups_for_every_right_not_set_on_its_own()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins] };
        var p = From(config, new() { ["AllowedGroups"] = new[] { Helpdesk, " " }, [@"Rights\Tunnel"] = new[] { Admins } });
        foreach (var right in new[] { A.AgentRight.PowerShell, A.AgentRight.Desktop, A.AgentRight.SecureDesktop, A.AgentRight.PrivacyScreen, A.AgentRight.AutoSignIn })
            Assert.Equal([Helpdesk], p.Groups(right));
        Assert.Equal([Admins], p.Groups(A.AgentRight.Tunnel)); // set on its own: wins
        // Never given by the default groups.
        Assert.Empty(p.Groups(A.AgentRight.DesktopViewOnly));
        Assert.Empty(p.Groups(A.AgentRight.ConsentBypass));
        // Groups the install chose for automatic sign-in stay its own.
        var chosen = From(new A.AgentConfig { AllowedGroups = [Admins], AutoSignInGroups = [Admins] }, new() { ["AllowedGroups"] = new[] { Helpdesk } });
        Assert.Equal([Admins], chosen.Groups(A.AgentRight.AutoSignIn));
    }

    [Fact]
    public void Automatic_sign_in_comes_from_agent_json_and_the_policy_wins()
    {
        var installed = new A.AgentConfig { AllowedGroups = [Admins], NoUserBehavior = "AutoSignIn", AutoSignInGroups = [Admins] };
        var d = From(installed, null);
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, d.NoUserBehavior);
        Assert.Equal([Admins], d.Groups(A.AgentRight.AutoSignIn));

        var p = From(installed, new() { ["NoUserBehavior"] = "SignInScreen", [@"Rights\AutoSignIn"] = new[] { Helpdesk } });
        Assert.Equal(A.NoUserBehaviors.SignInScreen, p.NoUserBehavior);
        Assert.Equal([Helpdesk], p.Groups(A.AgentRight.AutoSignIn));
        // An invalid policy value still means the sign-in screen: a policy that restricts must not turn into a permission.
        Assert.Equal(A.NoUserBehaviors.SignInScreen, From(installed, new() { ["NoUserBehavior"] = "Maybe" }).NoUserBehavior);
    }

    [Fact]
    public void Automatic_sign_in_is_on_by_default_for_the_allowed_groups_and_a_policy_turns_it_off()
    {
        // Nothing chosen by the install, nothing set by IT: allowed, for the groups that have the other rights.
        var fresh = new A.AgentConfig { AllowedGroups = [Admins, Helpdesk] };
        var d = From(fresh, null);
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, d.NoUserBehavior);
        Assert.Equal([Admins, Helpdesk], d.Groups(A.AgentRight.AutoSignIn));
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, From(fresh, new() { ["DesktopEnabled"] = 1 }).NoUserBehavior); // a policy key without the value

        // IT turns it off, or narrows who may.
        Assert.Equal(A.NoUserBehaviors.SignInScreen, From(fresh, new() { ["NoUserBehavior"] = "SignInScreen" }).NoUserBehavior);
        Assert.Empty(From(fresh, new() { [@"Rights\AutoSignIn"] = new[] { " " } }).Groups(A.AgentRight.AutoSignIn));
        Assert.Equal([Helpdesk], From(fresh, new() { [@"Rights\AutoSignIn"] = new[] { Helpdesk } }).Groups(A.AgentRight.AutoSignIn));

        // The install turned it off (AUTOSIGNIN=0, "Remove credential provider"), or wrote something unknown: off.
        Assert.Equal(A.NoUserBehaviors.SignInScreen, From(new A.AgentConfig { NoUserBehavior = "SignInScreen" }, null).NoUserBehavior);
        Assert.Equal(A.NoUserBehaviors.SignInScreen, From(new A.AgentConfig { NoUserBehavior = "Maybe" }, null).NoUserBehavior);

        // A policy key that can't be read still turns it off.
        var refused = A.AgentPolicy.Refused("access denied");
        Assert.Equal(A.NoUserBehaviors.SignInScreen, refused.NoUserBehavior);
        Assert.Empty(refused.Groups(A.AgentRight.AutoSignIn));
    }

    [Fact]
    public void A_policy_key_that_cant_be_read_turns_PowerShell_off_too()
    {
        Assert.False(A.AgentPolicy.Refused("access denied").PowerShellEnabled);
    }

    [Fact]
    public void Health_names_only_the_device_actions_a_valid_policy_decides()
    {
        A.PolicyValue Set(A.PolicyEntry e, object value, string? problem = null) => new(e, value, A.PolicyScope.Computer, problem);
        var autoRight = A.PolicyCatalog.Rights.Single(r => r.Name == "AutoSignIn");
        Assert.Equal([A.AgentManaged.AutoSignIn, A.AgentManaged.VirtualDisplay, A.AgentManaged.Port],
            A.AgentPolicy.ManagedNames([Set(autoRight, new[] { Admins }), Set(A.PolicyCatalog.VirtualDisplay, "off"), Set(A.PolicyCatalog.ListenPort, 1),
                                        Set(A.PolicyCatalog.DesktopEnabled, 0), Set(A.PolicyCatalog.MaxSessions, 4)]));
        Assert.Equal([A.AgentManaged.AutoSignIn], A.AgentPolicy.ManagedNames([Set(A.PolicyCatalog.NoUserBehavior, "SignInScreen")]));
        Assert.Empty(A.AgentPolicy.ManagedNames([Set(A.PolicyCatalog.ListenPort, 0, "0 is outside 1–65535")]));
    }

    [Fact]
    public void The_policy_command_shows_each_value_with_its_source()
    {
        var config = new A.AgentConfig { AllowedGroups = [Admins], ListenPort = 60000, NoUserBehavior = "AutoSignIn", AutoSignInGroups = [Admins] };
        var values = new Dictionary<string, object> { ["MaxSessions"] = 50, ["PowerShellEnabled"] = 0, ["ConsentTimeoutSeconds"] = 9999 };
        var effective = From(config, values);
        var configured = new List<A.PolicyValue>
        {
            new(A.PolicyCatalog.MaxSessions, 50, A.PolicyScope.Computer, null),
            new(A.PolicyCatalog.PowerShellEnabled, 0, A.PolicyScope.Computer, null),
            new(A.PolicyCatalog.ConsentTimeoutSeconds, 9999, A.PolicyScope.Computer, "9999 is outside 5–600"),
        };
        var report = A.PolicyCommand.Report(config, null, effective, configured);
        Assert.Matches(@"MaxSessions\s+50\s+\[policy\]", report);
        Assert.Matches(@"PowerShellEnabled\s+Off\s+\[policy\]", report);
        Assert.Matches(@"ListenPort\s+60000\s+\[agent.json\]", report);
        Assert.Matches(@"NoUserBehavior\s+AutoSignIn\s+\[agent.json\]", report);
        Assert.Matches(@"Rights\\AutoSignIn\s+S-1-5-32-544\s+\[agent.json\]", report);
        Assert.Matches(@"Rights\\ConsentBypass\s+nobody\s+\[default\]", report);
        Assert.Matches(@"ConsentTimeoutSeconds\s+30\s+\[default\]", report);
        Assert.Contains("INVALID in the policy: 9999 is outside 5–600 – ignored", report);
        // Every agent value is listed.
        foreach (var e in A.PolicyCatalog.AgentEntries) Assert.Contains(e.Name, report);

        Assert.Contains("THE POLICY KEY CAN'T BE READ", A.PolicyCommand.Report(config, null, A.AgentPolicy.Refused("denied"), []));

        // Not chosen by the install: on, for the allowed groups from agent.json.
        var fresh = new A.AgentConfig { AllowedGroups = [Admins] };
        var defaults = A.PolicyCommand.Report(fresh, null, From(fresh, null), []);
        Assert.Matches(@"NoUserBehavior\s+AutoSignIn\s+\[default\]", defaults);
        Assert.Matches(@"Rights\\AutoSignIn\s+S-1-5-32-544\s+\[agent.json\]", defaults);
    }

    [Theory]
    [InlineData("Rule Name:   RemoteHub Agent\r\nLocalPort:                            58732\r\nRemotePort: Any", "58732")]
    [InlineData("Regelname:   RemoteHub Agent\r\nLokaler Port:                         58732", "")]
    public void The_firewall_rule_port_is_read_from_netsh(string output, string expected) =>
        Assert.Equal([expected], A.Enrollment.RulePorts(output));

    // ------------------------------------------------------------------ automatic sign-in setup (agent.json) and the migration

    private sealed class FakeKey(Dictionary<string, object> values) : A.AutoSignInSetup.IPolicyKey
    {
        public Dictionary<string, object> Values => values;
        public string? Text(string name) => values.TryGetValue(name, out var v) ? v as string : null;
        public string[]? List(string subKey, string name) => values.TryGetValue(subKey + "\\" + name, out var v) ? v as string[] : null;
        public void Delete(string? subKey, string name) => values.Remove(subKey is null ? name : subKey + "\\" + name);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rh-asi-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { /* in use */ } }
    }

    private static string NewConfig(TempFolder dir, Action<A.AgentConfig>? change = null)
    {
        var config = new A.AgentConfig { DeviceId = "dev", CertificateThumbprint = "AB", AllowedGroups = [Admins] };
        change?.Invoke(config);
        var path = dir.File("agent.json");
        config.Save(path);
        return path;
    }

    [Fact]
    public void On_and_off_write_agent_json_and_never_the_policy_key()
    {
        using var dir = new TempFolder();
        var path = NewConfig(dir);
        var answer = A.AutoSignInSetup.On([Admins], path, (null, null));
        Assert.Equal("Automatic sign-in allowed on this device for Administrators.", answer);
        var on = A.AgentConfig.Load(path);
        Assert.Equal("AutoSignIn", on.NoUserBehavior);
        Assert.Equal([Admins], on.AutoSignInGroups!);

        Assert.Contains("it decides, so automatic sign-in stays off", A.AutoSignInSetup.On([Admins], path, ("SignInScreen", null)));
        Assert.Contains(@"Rights\AutoSignIn is set on this device", A.AutoSignInSetup.On([Admins], path, (null, [Helpdesk])));

        Assert.Equal("Automatic sign-in: turned off on this device.", A.AutoSignInSetup.Off(path, (null, null)));
        var off = A.AgentConfig.Load(path);
        Assert.Equal("SignInScreen", off.NoUserBehavior); // said explicitly: not set would mean on (the default)
        Assert.Null(off.AutoSignInGroups);
        Assert.Equal(A.NoUserBehaviors.SignInScreen, From(off, null).NoUserBehavior);
        Assert.Contains("still allows it", A.AutoSignInSetup.Off(path, ("AutoSignIn", null)));
    }

    [Fact]
    public void The_running_service_sees_on_and_off_without_a_restart()
    {
        using var dir = new TempFolder();
        var path = NewConfig(dir);
        var loaded = A.AgentConfig.Load(path); // as the service holds it
        Assert.Same(loaded, loaded.WithCurrentAutoSignIn());
        A.AutoSignInSetup.On([Admins], path, (null, null));
        var now = loaded.WithCurrentAutoSignIn();
        Assert.Equal("AutoSignIn", now.NoUserBehavior);
        Assert.Equal([Admins], now.AutoSignInGroups!);
        Assert.Null(loaded.NoUserBehavior); // the service's own copy is left alone
        var made = new A.AgentConfig(); // made in code: no file to read again
        Assert.Same(made, made.WithCurrentAutoSignIn());
    }

    [Fact]
    public void Values_an_older_version_wrote_into_the_policy_key_move_to_agent_json_once()
    {
        using var dir = new TempFolder();
        var path = NewConfig(dir);
        var marker = dir.File("autosignin.policy");
        File.WriteAllLines(marker, ["NoUserBehavior=AutoSignIn", @"Rights\AutoSignIn=" + Admins]);
        var key = new FakeKey(new() { ["NoUserBehavior"] = "AutoSignIn", [@"Rights\AutoSignIn"] = new[] { Admins }, ["DesktopEnabled"] = 1 });

        var result = A.AutoSignInSetup.Migrate(path, key, marker);
        Assert.Equal(@"Automatic sign-in: moved NoUserBehavior and Rights\AutoSignIn from the policy key to agent.json.", result);
        Assert.Equal(["DesktopEnabled"], key.Values.Keys); // IT's own values stay
        var config = A.AgentConfig.Load(path);
        Assert.Equal("AutoSignIn", config.NoUserBehavior);
        Assert.Equal([Admins], config.AutoSignInGroups!);
        Assert.False(File.Exists(marker));
        Assert.Null(A.AutoSignInSetup.Migrate(path, key, marker)); // once
    }

    [Fact]
    public void Values_IT_changed_since_the_install_stay_in_the_policy_key()
    {
        using var dir = new TempFolder();
        var path = NewConfig(dir);
        var marker = dir.File("autosignin.policy");
        File.WriteAllLines(marker, ["NoUserBehavior=AutoSignIn", @"Rights\AutoSignIn=" + Admins]);
        // NoUserBehavior changed by IT; Rights\AutoSignIn removed since.
        var key = new FakeKey(new() { ["NoUserBehavior"] = "SignInScreen" });

        var result = A.AutoSignInSetup.Migrate(path, key, marker);
        Assert.Equal("Automatic sign-in: nothing to move from the policy key; NoUserBehavior changed since the install – left in the policy key (IT's now).", result);
        Assert.Equal("SignInScreen", key.Values["NoUserBehavior"]);
        var config = A.AgentConfig.Load(path);
        Assert.Null(config.NoUserBehavior);
        Assert.Null(config.AutoSignInGroups); // removed since: not brought back
    }

    [Fact]
    public void Nothing_to_migrate_without_the_marker()
    {
        using var dir = new TempFolder();
        Assert.Null(A.AutoSignInSetup.Migrate(NewConfig(dir), new FakeKey(new() { ["NoUserBehavior"] = "AutoSignIn" }), dir.File("autosignin.policy")));
    }
}
