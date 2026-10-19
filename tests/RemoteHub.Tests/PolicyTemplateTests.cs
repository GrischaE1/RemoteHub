extern alias agent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RemoteHub.Agent;
using RemoteHub.Services;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// policy\RemoteHub.admx and its ADML against PolicyCatalog (the names, types and ranges both products read), and the
/// catalogue's reader and Settings → Policies (docs/admx-design.md §11.1).
/// </summary>
public class PolicyTemplateTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static string PolicyFolder
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "policy", "RemoteHub.admx"))) return Path.Combine(dir.FullName, "policy");
            throw new DirectoryNotFoundException("policy\\RemoteHub.admx not found above " + AppContext.BaseDirectory);
        }
    }

    private static XDocument Admx => XDocument.Load(Path.Combine(PolicyFolder, "RemoteHub.admx"));
    private static XDocument Adml => XDocument.Load(Path.Combine(PolicyFolder, "en-US", "RemoteHub.adml"));

    /// <summary>One registry value a policy writes, and everything it can write there.</summary>
    private sealed record Write(string Policy, string PolicyClass, string Key, string Name, string Element, List<object> Values, int? Min = null, int? Max = null,
        bool Expandable = false, int? MaxLength = null);

    private static List<Write> Writes()
    {
        var writes = new List<Write>();
        foreach (var policy in Admx.Descendants(P + "policy"))
        {
            var name = (string)policy.Attribute("name")!;
            var cls = (string)policy.Attribute("class")!;
            var key = (string)policy.Attribute("key")!;
            if (policy.Attribute("valueName") is { } valueName)
            {
                var values = new List<object>();
                foreach (var v in new[] { policy.Element(P + "enabledValue"), policy.Element(P + "disabledValue") })
                    if (v?.Element(P + "decimal") is { } d) values.Add((int)d.Attribute("value")!);
                    else if (v?.Element(P + "string") is { } s) values.Add(s.Value);
                writes.Add(new Write(name, cls, key, (string)valueName, "policy", values));
            }
            foreach (var e in policy.Element(P + "elements")?.Elements() ?? [])
            {
                var eKey = (string?)e.Attribute("key") ?? key;
                var eName = (string)e.Attribute("valueName")!;
                var values = new List<object>();
                if (e.Name.LocalName == "enum")
                    foreach (var item in e.Elements(P + "item"))
                        if (item.Element(P + "value")!.Element(P + "decimal") is { } d) values.Add((int)d.Attribute("value")!);
                        else values.Add(item.Element(P + "value")!.Element(P + "string")!.Value);
                writes.Add(new Write(name, cls, eKey, eName, e.Name.LocalName, values,
                    (int?)e.Attribute("minValue"), (int?)e.Attribute("maxValue"), (bool?)e.Attribute("expandable") ?? false, (int?)e.Attribute("maxLength")));
            }
        }
        return writes;
    }

    private static string Id(string key, string name) => (key + "\\" + name).ToUpperInvariant();

    [Fact]
    public void Every_catalogue_value_is_written_by_exactly_one_policy_and_nothing_else()
    {
        var writes = Writes();
        var byId = writes.GroupBy(w => Id(w.Key, w.Name)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var entry in PolicyCatalog.All)
        {
            Assert.True(byId.TryGetValue(Id(entry.FullKey, entry.Name), out var found), $"{entry.FullKey}\\{entry.Name} has no policy in RemoteHub.admx");
            Assert.True(found!.Count == 1, $"{entry.Name} is written by {found.Count} policies: {string.Join(", ", found.Select(f => f.Policy))}");
        }
        var known = PolicyCatalog.All.Select(e => Id(e.FullKey, e.Name)).ToHashSet();
        Assert.Empty(writes.Where(w => !known.Contains(Id(w.Key, w.Name))).Select(w => $"{w.Policy}: {w.Key}\\{w.Name}"));
    }

    [Fact]
    public void Each_policy_writes_the_type_and_range_the_products_accept()
    {
        var entries = PolicyCatalog.All.ToDictionary(e => Id(e.FullKey, e.Name));
        foreach (var w in Writes())
        {
            var entry = entries[Id(w.Key, w.Name)];
            var allowed = entry.Kind switch
            {
                PolicyKind.Number => new[] { "policy", "decimal", "enum" },
                PolicyKind.Text => ["text", "enum"],
                _ => ["multiText", "enum"],
            };
            Assert.True(allowed.Contains(w.Element), $"{w.Policy}: a {w.Element} element can't write {entry.Name} ({entry.Kind})");
            if (w.Element == "decimal")
            {
                Assert.True(w.Min == entry.Min && w.Max == entry.Max, $"{w.Policy}: {entry.Name} range {w.Min}–{w.Max} in the ADMX, {entry.Min}–{entry.Max} in the code");
            }
            if (w.Element == "text")
            {
                Assert.True(w.Expandable == entry.Expand, $"{w.Policy}: expandable=\"{w.Expandable}\" in the ADMX, but the product {(entry.Expand ? "expands" : "doesn't expand")} {entry.Name}");
                Assert.True(w.MaxLength == (entry.Max > 0 ? entry.Max : null), $"{w.Policy}: maxLength {w.MaxLength} in the ADMX, {entry.Max} in the code");
            }
            // Every value Enabled or Disabled can write must be one the product accepts.
            foreach (var value in w.Values)
            {
                var raw = entry.Kind == PolicyKind.Number ? value : (object)(string)value;
                Assert.True(entry.Check(raw).Problem is null, $"{w.Policy}: writes \"{value}\" to {entry.Name}, which the product rejects: {entry.Check(raw).Problem}");
            }
        }
    }

    [Fact]
    public void Agent_policies_are_per_computer_and_console_policies_per_computer_or_user_each_in_its_own_key()
    {
        var entries = PolicyCatalog.All.ToDictionary(e => Id(e.FullKey, e.Name));
        foreach (var w in Writes())
        {
            var entry = entries[Id(w.Key, w.Name)];
            // The agent's key itself or below it – not the console's AgentInstall next to it.
            var agentKey = PolicyCatalog.Root + "\\" + PolicyCatalog.AgentKey;
            var underAgent = w.Key.Equals(agentKey, StringComparison.OrdinalIgnoreCase) || w.Key.StartsWith(agentKey + "\\", StringComparison.OrdinalIgnoreCase);
            if (entry.Product == PolicyProduct.Agent)
            {
                Assert.Equal("Machine", w.PolicyClass);
                Assert.True(underAgent, $"{w.Policy} writes outside the agent's key");
                Assert.StartsWith("Agent_", w.Policy);
            }
            else
            {
                Assert.Equal("Both", w.PolicyClass);
                Assert.False(underAgent, $"{w.Policy} writes into the agent's key");
                Assert.StartsWith("Console_", w.Policy);
            }
        }
    }

    [Fact]
    public void No_policy_element_can_empty_a_whole_key()
    {
        // A list element with explicitValue or additive="false" deletes every value in its key when disabled – on a device
        // with console and agent that would take the other product's values with it (docs/admx-design.md §3).
        Assert.Empty(Admx.Descendants(P + "list").Select(l => (string?)l.Attribute("id")));
    }

    [Fact]
    public void Every_string_and_presentation_the_ADMX_uses_is_in_the_ADML_and_nothing_else()
    {
        var admx = Admx.ToString();
        var strings = Adml.Descendants(P + "string").Select(s => (string)s.Attribute("id")!).ToList();
        var presentations = Adml.Descendants(P + "presentation").ToDictionary(p => (string)p.Attribute("id")!);
        Assert.Equal(strings.Count, strings.Distinct().Count());

        var usedStrings = Regex.Matches(admx, @"\$\(string\.([^)]+)\)").Select(m => m.Groups[1].Value).ToHashSet();
        var usedPresentations = Regex.Matches(admx, @"\$\(presentation\.([^)]+)\)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Empty(usedStrings.Except(strings));
        Assert.Empty(strings.Except(usedStrings));
        Assert.Empty(usedPresentations.Except(presentations.Keys));
        Assert.Empty(presentations.Keys.Except(usedPresentations));

        // Each presentation shows exactly the elements of the policies that use it.
        foreach (var policy in Admx.Descendants(P + "policy").Where(p => p.Attribute("presentation") is not null))
        {
            var id = Regex.Match((string)policy.Attribute("presentation")!, @"\$\(presentation\.([^)]+)\)").Groups[1].Value;
            var elements = policy.Element(P + "elements")!.Elements().Select(e => (string)e.Attribute("id")!).OrderBy(x => x);
            var refs = presentations[id].Elements().Select(e => (string)e.Attribute("refId")!).OrderBy(x => x);
            Assert.Equal(elements, refs);
        }
        foreach (var policy in Admx.Descendants(P + "policy").Where(p => p.Element(P + "elements") is not null))
            Assert.True(policy.Attribute("presentation") is not null, $"{policy.Attribute("name")} has elements but no presentation");
    }

    [Fact]
    public void A_number_box_starts_at_a_value_its_element_accepts()
    {
        // Enabling a policy without typing writes the box's default: it must be one the element (and so the product) takes.
        var ranges = Admx.Descendants(P + "decimal").Where(d => d.Attribute("id") is not null)
            .ToDictionary(d => (string)d.Attribute("id")!, d => ((int)d.Attribute("minValue")!, (int)d.Attribute("maxValue")!));
        foreach (var box in Adml.Descendants(P + "decimalTextBox"))
        {
            var id = (string)box.Attribute("refId")!;
            var start = (int?)box.Attribute("defaultValue") ?? 1;
            Assert.True(ranges.TryGetValue(id, out var r) && start >= r.Item1 && start <= r.Item2, $"{id}: the box starts at {start}, outside {r.Item1}–{r.Item2}");
        }
    }

    [Fact]
    public void Policy_names_and_categories_are_unique_and_every_category_exists()
    {
        var policies = Admx.Descendants(P + "policy").Select(p => (string)p.Attribute("name")!).ToList();
        Assert.Equal(policies.Count, policies.Distinct().Count());
        var categories = Admx.Descendants(P + "category").Select(c => (string)c.Attribute("name")!).ToHashSet();
        foreach (var parent in Admx.Descendants(P + "parentCategory"))
            Assert.Contains((string)parent.Attribute("ref")!, categories);
        var supported = Admx.Descendants(P + "definition").Select(d => (string)d.Attribute("name")!).ToHashSet();
        foreach (var policy in Admx.Descendants(P + "policy"))
            Assert.Contains((string)policy.Element(P + "supportedOn")!.Attribute("ref")!, supported);
    }

    // ------------------------------------------------------------------ what the agent does with the template's values

    internal sealed class Values(Dictionary<string, object> values) : A.AgentPolicy.IPolicyValues
    {
        public int? Number(string? subKey, string name) => values.TryGetValue(Key(subKey, name), out var v) && v is int i ? i : null;
        public string? Text(string? subKey, string name) => values.TryGetValue(Key(subKey, name), out var v) && v is string s ? s : null;
        // As the registry: a REG_SZ is split at ';' and ','.
        public IReadOnlyList<string>? Strings(string? subKey, string name) => values.TryGetValue(Key(subKey, name), out var v)
            ? v switch { string[] s => s, string s => s.Split([';', ','], StringSplitOptions.RemoveEmptyEntries), _ => null } : null;
        private static string Key(string? sub, string name) => sub is null ? name : sub + "\\" + name;
    }

    [Fact]
    public void The_agent_applies_what_the_template_writes()
    {
        var config = new A.AgentConfig { AllowedGroups = ["S-1-5-32-544"] };
        A.AgentPolicy Apply(string name, object value) => A.AgentPolicy.From(config, new Values(new() { [name] = value }));

        // Disabled writes 0 for the on/off policies.
        Assert.False(Apply("DesktopEnabled", 0).DesktopEnabled);
        Assert.False(Apply("TunnelEnabled", 0).TunnelEnabled);
        Assert.False(Apply("AuditToEventLog", 0).AuditToEventLog);
        Assert.True(Apply("AutoSignInRequirePrivacyScreen", 1).AutoSignInRequirePrivacyScreen);
        // Each consent combination of the dropdown (REG_SZ separated by ;).
        var consent = Writes().Single(w => w.Name == "AllowedConsentModes");
        Assert.Equal(7, consent.Values.Count);
        foreach (string combination in consent.Values)
            Assert.Equal(combination.Split(';'), Apply("AllowedConsentModes", combination).ConsentModes);
        // Range limits from the ADMX are accepted at both ends.
        Assert.Equal(5, Apply("ConsentTimeoutSeconds", 5).ConsentTimeoutSeconds);
        Assert.Equal(600, Apply("ConsentTimeoutSeconds", 600).ConsentTimeoutSeconds);
        Assert.Equal(1, Apply("MaxFramesPerSecond", 1).MaxFramesPerSecond);
        Assert.Equal(1024, Apply("MaxTunnelStreams", 1024).MaxTunnelStreams);
        Assert.Equal(TimeSpan.FromMinutes(1440), Apply("DesktopIdleTimeoutMinutes", 1440).IdleTimeout);
        // Outside the range: the default.
        Assert.Equal(30, Apply("ConsentTimeoutSeconds", 601).ConsentTimeoutSeconds);
        // Words.
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, Apply("NoUserBehavior", "AutoSignIn").NoUserBehavior);
        Assert.Equal("SignOut", Apply("AutoSignInEndAction", "SignOut").AutoSignInEndAction);
        // A right from the multiText element (REG_MULTI_SZ).
        var rights = A.AgentPolicy.From(config, new Values(new() { [@"Rights\Desktop"] = new[] { "S-1-0-0" } }));
        Assert.Equal(["S-1-0-0"], rights.Groups(A.AgentRight.Desktop));
    }

    [Fact]
    public void Every_layer_of_automatic_sign_in_that_is_on_by_default_has_a_policy_that_turns_it_off()
    {
        var writes = Writes();
        bool Writes_(string keyEnd, string name, object value) =>
            writes.Any(w => w.Key.EndsWith(keyEnd, StringComparison.OrdinalIgnoreCase) && w.Name == name && w.Values.Contains(value));

        // The agent: on for the allowed groups when nothing is set ...
        var config = new A.AgentConfig { AllowedGroups = ["S-1-5-32-544"] };
        var fresh = A.AgentPolicy.From(config, null);
        Assert.Equal(A.NoUserBehaviors.AutoSignIn, fresh.NoUserBehavior);
        Assert.Equal(["S-1-5-32-544"], fresh.Groups(A.AgentRight.AutoSignIn));
        // ... off with "When nobody is signed in" = Show the sign-in screen, or nobody in "Who may sign in automatically".
        Assert.True(Writes_(@"RemoteHub\Agent", "NoUserBehavior", "SignInScreen"));
        Assert.Equal(A.NoUserBehaviors.SignInScreen, A.AgentPolicy.From(config, new Values(new() { ["NoUserBehavior"] = "SignInScreen" })).NoUserBehavior);
        Assert.Contains(writes, w => w.Key.EndsWith(@"RemoteHub\Agent\Rights", StringComparison.OrdinalIgnoreCase) && w.Name == "AutoSignIn" && w.Element == "multiText");
        Assert.Equal(["S-1-0-0"], A.AgentPolicy.From(config, new Values(new() { [@"Rights\AutoSignIn"] = new[] { "S-1-0-0" } })).Groups(A.AgentRight.AutoSignIn));

        // The console: the credential provider entries, the install dialog's option (ticked by default) and signing in.
        Assert.True(Writes_("RemoteHub", "AllowCredentialProviderInstall", 0));
        Assert.True(Writes_(@"RemoteHub\AgentInstall", "AutoSignIn", 0));
        Assert.True(Writes_("RemoteHub", "DisableAgentAutoSignIn", 1));
    }

    [Fact]
    public void The_catalogue_lists_every_agent_right()
    {
        Assert.Equal(Enum.GetNames<A.AgentRight>(), PolicyCatalog.Rights.Select(r => r.Name));
    }

    // ------------------------------------------------------------------ reading

    private sealed class FakeSource(Dictionary<(PolicyScope, string, string), object> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            values.TryGetValue((scope, key.ToUpperInvariant(), name), out var v) ? v : null;
    }

    private static FakeSource Source(params (PolicyScope Scope, string Key, string Name, object Value)[] values) =>
        new(values.ToDictionary(v => (v.Scope, v.Key.ToUpperInvariant(), v.Name), v => v.Value));

    private const string Root = PolicyCatalog.Root;

    [Fact]
    public void The_computer_wins_over_the_user_and_an_invalid_value_doesnt_hide_a_valid_one()
    {
        var src = Source(
            (PolicyScope.Computer, Root, "AuditRetentionDays", 90),
            (PolicyScope.User, Root, "AuditRetentionDays", 30),
            (PolicyScope.Computer, Root, "UpdateChannel", "nightly"),
            (PolicyScope.User, Root, "UpdateChannel", "beta"));
        Assert.Equal(90, PolicyCatalog.Number(PolicyCatalog.AuditRetentionDays, src));
        Assert.Equal(PolicyScope.Computer, PolicyCatalog.Read(PolicyCatalog.AuditRetentionDays, src)!.Scope);
        Assert.Equal("beta", PolicyCatalog.Text(PolicyCatalog.UpdateChannel, src));
        Assert.Equal(PolicyScope.User, PolicyCatalog.Read(PolicyCatalog.UpdateChannel, src)!.Scope);
    }

    [Fact]
    public void Invalid_values_count_as_not_set_and_say_why()
    {
        var src = Source(
            (PolicyScope.Computer, Root, "AuditRetentionDays", 40_000),
            (PolicyScope.Computer, Root, "DisableTelemetry", "1"),
            (PolicyScope.Computer, Root + @"\Agent", "AllowedConsentModes", new[] { "Ask", "Maybe" }));
        Assert.Null(PolicyCatalog.Number(PolicyCatalog.AuditRetentionDays, src));
        Assert.Contains("outside 1–36500", PolicyCatalog.Read(PolicyCatalog.AuditRetentionDays, src)!.Problem);
        Assert.Contains("must be a DWORD", PolicyCatalog.Read(PolicyCatalog.DisableTelemetry, src)!.Problem);
        Assert.Contains("\"Maybe\"", PolicyCatalog.Read(PolicyCatalog.AllowedConsentModes, src)!.Problem);
    }

    [Fact]
    public void Agent_values_are_read_from_the_computer_only_and_the_old_key_only_for_the_settings_folder()
    {
        var src = Source(
            (PolicyScope.User, Root + @"\Agent", "DesktopEnabled", 0),
            (PolicyScope.User, PolicyCatalog.LegacyRoot, "SettingsFolder", @"\\server\old"),
            (PolicyScope.Computer, PolicyCatalog.LegacyRoot, "DisableTelemetry", 1));
        Assert.Null(PolicyCatalog.Read(PolicyCatalog.DesktopEnabled, src));
        Assert.Null(PolicyCatalog.Read(PolicyCatalog.DisableTelemetry, src));
        var folder = PolicyCatalog.Read(PolicyCatalog.SettingsFolder, src)!;
        Assert.Equal(@"\\server\old", folder.Value);
        Assert.Equal(PolicyCatalog.LegacyRoot, folder.FromKey);
    }

    [Fact]
    public void Text_values_expand_environment_variables_where_the_template_allows_it()
    {
        var src = Source((PolicyScope.Computer, Root, "RecordingFolder", @"%SystemRoot%\Rec"));
        Assert.Equal(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\Rec"), PolicyCatalog.Text(PolicyCatalog.RecordingFolder, src));
    }

    [Fact]
    public void The_policies_page_lists_what_is_set_with_source_and_problems()
    {
        var src = Source(
            (PolicyScope.Computer, Root, "DisableTelemetry", 1),
            (PolicyScope.User, Root, "AuditRetentionDays", 0),
            (PolicyScope.Computer, Root + @"\Agent", "DesktopClipboard", 1),
            (PolicyScope.Computer, Root + @"\Agent\Rights", "Desktop", new[] { "S-1-5-32-544", "CONTOSO\\Helpdesk" }));

        var withoutAgent = PolicyReport.Build(agentInstalled: false, src);
        Assert.Null(withoutAgent.Agent);
        Assert.Equal(2, withoutAgent.Console.Count);
        Assert.Equal("2 settings of RemoteHub on this PC are set by your organisation (1 for the computer, 1 for your user account).", withoutAgent.Summary);

        var telemetry = withoutAgent.Console.Single(r => r.Registry.EndsWith("DisableTelemetry"));
        Assert.Equal(("Turned off", "Computer", "Updates and usage data"), (telemetry.Value, telemetry.Source, telemetry.Group));
        Assert.Equal(@"HKLM\SOFTWARE\Policies\RemoteHub\DisableTelemetry", telemetry.Registry);
        var retention = withoutAgent.Console.Single(r => r.Registry.EndsWith("AuditRetentionDays"));
        Assert.False(retention.Valid);
        Assert.Equal("User", retention.Source);
        Assert.Contains("ignored", retention.Problem);

        var withAgent = PolicyReport.Build(agentInstalled: true, src);
        Assert.Equal(2, withAgent.Agent!.Count);
        Assert.Equal("Text", withAgent.Agent.Single(r => r.Registry.EndsWith("DesktopClipboard")).Value);
        var right = withAgent.Agent.Single(r => r.Group == "Access rights");
        Assert.Equal(@"S-1-5-32-544; CONTOSO\Helpdesk", right.Value);

        var text = withAgent.ToText();
        Assert.Contains("Clipboard: Text", text);
        Assert.Contains("INVALID: AuditRetentionDays 0 is outside 1–36500 – ignored", text);
        Assert.Contains("RemoteHub Agent on this PC", text);
    }

    [Fact]
    public void Nothing_set_says_so()
    {
        var report = PolicyReport.Build(agentInstalled: true, Source());
        Assert.Equal("No policies are set for RemoteHub on this PC.", report.Summary);
        Assert.Contains("RemoteHub Agent on this PC: no policies set.", report.ToText());
    }
}
