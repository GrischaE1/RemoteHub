using System.Collections.Generic;
using RemoteHub.Agent;
using RemoteHub.Models;
using RemoteHub.Services;
using RemoteHub.ViewModels;

namespace RemoteHub.Tests;

/// <summary>
/// The console's settings and connection settings by policy (docs/admx-design.md §6, §7): what wins where a value is
/// used, the editor's locked rows, and that every value has a place that locks it.
/// </summary>
[Collection("Policies")]
public class ConsoleSettingsPolicyTests
{
    private sealed class FakeSource(Dictionary<(string Key, string Name), object> values) : IPolicySource
    {
        public object? Read(PolicyScope scope, string key, string name) =>
            scope == PolicyScope.Computer && values.TryGetValue((key.ToUpperInvariant(), name), out var v) ? v : null;
    }

    private static readonly string Root = PolicyCatalog.Root.ToUpperInvariant();
    private static readonly string Conn = (PolicyCatalog.Root + "\\" + PolicyCatalog.ConnectionsKey).ToUpperInvariant();

    private static FakeSource Set(params (string Key, string Name, object Value)[] values) => new(values.ToDictionary(v => (v.Key, v.Name), v => v.Value));

    private static void WithPolicy(FakeSource source, Action test)
    {
        AppPolicy.Source = source;
        try { test(); }
        finally { AppPolicy.Source = RegistryPolicySource.Instance; }
    }

    [Fact]
    public void A_connection_setting_set_by_policy_wins_over_the_connection_its_folder_and_the_defaults()
    {
        var data = new AppData();
        data.Defaults.RedirectDrives = true;
        var folder = new Folder { Name = "f", Options = new RdpOptions { Audio = AudioMode.PlayRemote } };
        var c = new Connection { Name = "srv", Host = "srv", Parent = folder, Options = new RdpOptions { RedirectDrives = true, ColorDepth = 16 } };

        WithPolicy(Set((Conn, "RedirectDrives", 0), (Conn, "Audio", "none"), (Conn, "ColorDepth", 24), (Conn, "ServerAuthentication", "DoNotConnect")), () =>
        {
            var o = Inheritance.ResolveOptions(c, data);
            Assert.False(o.RedirectDrives);
            Assert.Equal(AudioMode.None, o.Audio); // the words are matched without case
            Assert.Equal(24, o.ColorDepth);
            Assert.Equal(ServerAuth.DoNotConnect, o.ServerAuthentication);
            Assert.True(c.Options.RedirectDrives, "the connection keeps its own value");
        });
        Assert.True(Inheritance.ResolveOptions(c, data).RedirectDrives);
    }

    [Fact]
    public void Invalid_connection_values_count_as_not_set()
    {
        var c = new Connection { Name = "srv", Host = "srv", Options = new RdpOptions { ColorDepth = 16, FixedResolution = "1280x720" } };
        WithPolicy(Set((Conn, "ColorDepth", 20), (Conn, "FixedResolution", "huge"), (Conn, "Quality", "Ultra")), () =>
        {
            var o = Inheritance.ResolveOptions(c, new AppData());
            Assert.Equal(16, o.ColorDepth); // 20 is in the range but not a colour depth
            Assert.Equal("1280x720", o.FixedResolution);
            Assert.Equal(VisualQuality.Balanced, o.Quality);
        });
    }

    [Fact]
    public void Free_text_values_with_a_form_of_their_own_say_when_they_are_wrong()
    {
        var resolution = PolicyCatalog.ConnectionEntries.Single(e => e.Name == "FixedResolution");
        Assert.Null(resolution.Check("2560x1440").Problem);
        Assert.Contains("<width>x<height>", resolution.Check("huge").Problem);
        var scope = PolicyCatalog.AgentInstallEntries.Single(e => e.Name == "FirewallScope");
        Assert.Null(scope.Check("10.0.0.0/8; 192.168.0.0/16").Problem); // ";" as well as ","
        Assert.NotNull(scope.Check("everyone").Problem);
    }

    [Fact]
    public void Each_caller_gets_its_own_copy_of_the_connection_settings()
    {
        WithPolicy(Set((Conn, "RedirectDrives", 0)), () =>
        {
            AppPolicy.Connections().RedirectDrives = true;
            Assert.False(AppPolicy.Connections().RedirectDrives);
        });
    }

    [Fact]
    public void UseAgent_by_policy_decides_below_a_jump_host_too()
    {
        var jump = new Connection { Name = "jump", Host = "jump" };
        var c = new Connection { Name = "srv", Host = "srv", Parent = jump, Agent = new AgentDevice { Fingerprint = "AB", Status = AgentStatus.Installed } };
        Assert.True(Inheritance.UsesAgent(c, new AppData()));
        WithPolicy(Set((Conn, "UseAgent", 0)), () => Assert.False(Inheritance.UsesAgent(c, new AppData())));
    }

    [Fact]
    public void The_editor_shows_the_policys_value_greyed_out_and_saving_keeps_the_stored_one()
    {
        var data = new AppData();
        var c = new Connection { Name = "srv", Host = "srv", Options = new RdpOptions { RedirectDrives = true } };
        WithPolicy(Set((Conn, "RedirectDrives", 0)), () =>
        {
            var vm = new EditorViewModel(c, data, isNew: false);
            var row = vm.ResourceRows.Single(r => r.Label == "Local drives");
            Assert.False(row.IsEditable);
            Assert.True(row.FixedByPolicy);
            Assert.Equal(PolicyReport.ManagedNote, row.PolicyNote);
            Assert.Equal(false, row.Selected.Value);
            Assert.True(vm.Apply());
            Assert.True(c.Options.RedirectDrives);

            // The global defaults show it the same way.
            Assert.False(new EditorViewModel(null, data, isNew: false).ResourceRows.Single(r => r.Label == "Local drives").IsEditable);
        });
    }

    [Fact]
    public void Every_connection_setting_has_an_editor_row_that_locks_it()
    {
        var all = PolicyCatalog.ConnectionEntries.Select(e => (e.Key, e.Name, Value: e.Kind == PolicyKind.Number ? (object)(e.Words is { } w ? int.Parse(w[0]) : 1) : e.Words?[0] ?? "1920x1080"));
        WithPolicy(new FakeSource(all.ToDictionary(v => ((PolicyCatalog.Root + "\\" + v.Key).ToUpperInvariant(), v.Name), v => v.Value)), () =>
        {
            var vm = new EditorViewModel(new Connection { Name = "srv", Host = "srv" }, new AppData(), isNew: false);
            var rows = vm.DisplayRows.Concat(vm.ResourceRows).Concat(vm.SecurityRows).Concat(vm.PsRows).ToList();
            Assert.Equal(PolicyCatalog.ConnectionEntries.Count, rows.Count(r => r.FixedByPolicy));
            Assert.All(PolicyCatalog.ConnectionEntries, e => Assert.NotNull(typeof(RdpOptions).GetProperty(e.Name)));
        });
    }

    [Fact]
    public void Every_console_setting_has_a_reader()
    {
        // §6 values in the root key: AppPolicy has a property of the same name, which the settings window and the code use.
        var settings = PolicyCatalog.ConsoleEntries.Where(e => e.Key.Length == 0 && e.Since != "0.18.11" && !PolicyCatalog.FeatureEntries.Contains(e) && e.Name != "AllowSharedSettings");
        Assert.All(settings, e => Assert.True(typeof(AppPolicy).GetProperty(e.Name) is not null, $"AppPolicy has no {e.Name}"));
    }

    [Fact]
    public void Disable_values_at_0_lock_updates_and_usage_data_on()
    {
        var ui = new UiState { UpdateCheckEnabled = false, TelemetryEnabled = false };
        WithPolicy(Set((Root, "DisableUpdateCheck", 0), (Root, "DisableTelemetry", 0)), () =>
        {
            Assert.True(UpdateService.CheckEnabled(ui));
            Assert.True(Telemetry.Enabled(ui));
        });
        WithPolicy(Set((Root, "DisableUpdateCheck", 1)), () => Assert.False(UpdateService.CheckEnabled(new UiState { UpdateCheckEnabled = true })));
        Assert.False(UpdateService.CheckEnabled(ui));
    }

    [Fact]
    public void Recording_settings_by_policy_replace_the_users()
    {
        var ui = new UiState { RecordingFps = 10, RecordingResolution = "Tab", RecordingUseGpu = false, RecordingShowDetails = false };
        WithPolicy(Set((Root, "RecordingFps", 30), (Root, "RecordingResolution", "FullHd"), (Root, "RecordingUseGpu", 1), (Root, "RecordingIncludeDetails", 1)), () =>
        {
            var o = Recordings.Options(ui);
            Assert.Equal(30, o.FramesPerSecond);
            Assert.Equal(RecordingResolution.FullHd, o.Resolution);
            Assert.True(o.UseGpu);
            Assert.True(Recordings.IncludeDetails(ui));
        });
        WithPolicy(Set((Root, "RecordingFps", 25)), () => Assert.Equal(10, Recordings.Options(ui).FramesPerSecond)); // not one of 5, 10, 15, 30, 60
    }

    [Fact]
    public void The_policies_page_flags_a_connection_value_a_feature_switch_blocks()
    {
        var src = Set((Root, "AllowAgentDesktop", 0), (Conn, "RemoteDesktopVia", "Agent"), (Conn, "UseAgent", 1));
        var report = PolicyReport.Build(agentInstalled: false, src);
        var via = report.Console.Single(r => r.Registry.EndsWith(@"\RemoteDesktopVia"));
        Assert.Equal("Remote desktop through the RemoteHub Agent is turned off by your organisation, so there is no remote desktop.", via.Conflict);
        Assert.Equal("Connection settings – Remoting and agent", via.Group);
        Assert.Null(report.Console.Single(r => r.Registry.EndsWith(@"\UseAgent")).Conflict); // the agent itself is allowed
        Assert.Contains("CONFLICT: Remote desktop through the RemoteHub Agent", report.ToText());
    }
}
