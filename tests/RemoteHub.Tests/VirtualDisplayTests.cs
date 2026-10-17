extern alias agent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteHub.Agent;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>The virtual monitor's pieces that don't install anything: shipped files, driver settings, choices.</summary>
public class VirtualDisplayTests
{
    private static string ThirdParty => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\third-party\VirtualDisplayDriver\24.12.24"));

    [Fact]
    public void The_shipped_driver_files_match_their_hashes()
    {
        A.VirtualDisplay.VerifyFiles(ThirdParty);
        Assert.True(File.Exists(Path.Combine(ThirdParty, "LICENSE.txt")));
    }

    [Fact]
    public void A_changed_driver_file_is_refused()
    {
        var dir = Directory.CreateTempSubdirectory("rh-vdd-").FullName;
        try
        {
            foreach (var f in A.VirtualDisplay.Files.Keys) File.Copy(Path.Combine(ThirdParty, f), Path.Combine(dir, f));
            File.AppendAllText(Path.Combine(dir, "MttVDD.inf"), "; changed");
            Assert.Throws<InvalidDataException>(() => A.VirtualDisplay.VerifyFiles(dir));
            File.Copy(Path.Combine(ThirdParty, "MttVDD.inf"), Path.Combine(dir, "MttVDD.inf"), overwrite: true);
            File.Delete(Path.Combine(dir, "MttVDD.dll"));
            Assert.Throws<FileNotFoundException>(() => A.VirtualDisplay.VerifyFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_driver_settings_offer_the_window_size_first_at_60_hz()
    {
        var x = A.VirtualDisplay.Settings(1422, 958);
        Assert.Equal("1", x.Root!.Element("monitors")!.Element("count")!.Value);
        var sizes = x.Root.Element("resolutions")!.Elements("resolution").Select(r => (int.Parse(r.Element("width")!.Value), int.Parse(r.Element("height")!.Value))).ToList();
        Assert.Equal((1422, 958), sizes[0]);
        Assert.Equal(sizes.Count, sizes.Distinct().Count());
        Assert.All(x.Root.Element("resolutions")!.Elements("resolution"), r => Assert.Equal("60", r.Element("refresh_rate")!.Value));
        Assert.Single(A.VirtualDisplay.Settings(1920, 1080).Root!.Element("resolutions")!.Elements("resolution"), r => r.Element("width")!.Value == "1920");
    }

    [Theory]
    [InlineData(1421, 957, 1420, 956)]
    [InlineData(640, 480, 800, 600)]
    [InlineData(9000, 5000, 7680, 4320)]
    public void Window_sizes_become_even_and_within_limits(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), A.VirtualDisplay.Normalize(w, h));

    [Fact]
    public void The_state_file_round_trips()
    {
        var s = new A.VirtualDisplay.State(@"ROOT\DISPLAY\0000", "oem7.inf", true);
        Assert.Equal(s, A.VirtualDisplay.State.Parse(s.Serialize()));
        Assert.Null(A.VirtualDisplay.State.Parse("oem=oem7.inf"));
    }

    [Theory]
    [InlineData("auto", "1", null, "auto")]
    [InlineData("auto", "0", true, "off")]       // the MSI wins over the enrollment file
    [InlineData("off", "", true, "auto")]        // no MSI choice: the enrollment file
    [InlineData("always", "1", null, "always")]  // switched on: "always" set by hand stays
    [InlineData("off", "", null, "off")]         // nothing chosen: the device keeps its setting
    [InlineData("auto", null, false, "off")]
    public void The_install_choice_sets_the_mode(string current, string? msi, bool? enrollment, string expected) =>
        Assert.Equal(expected, A.Enrollment.VirtualDisplayMode(current, msi, enrollment));

    [Fact]
    public void The_enrollment_file_carries_the_choice()
    {
        var (e, _) = AgentEnrollment.Create("dev", 58732, virtualDisplay: false);
        Assert.False(AgentEnrollment.Parse(e.Serialize()).VirtualDisplay);
        var (unset, _) = AgentEnrollment.Create("dev", 58732);
        Assert.Null(AgentEnrollment.Parse(unset.Serialize()).VirtualDisplay);
    }

    [Fact]
    public void The_configuration_accepts_only_known_modes()
    {
        var c = new A.AgentConfig { VirtualDisplay = "sometimes" };
        Assert.Throws<InvalidDataException>(c.Validate);
        c.VirtualDisplay = "always";
        c.Validate();
    }

    [Theory]
    [InlineData(@"ROOT\DISPLAY\0000", true)]
    [InlineData(@"SWD\MttVDD\{5620e0c7-8062-4dce-aeb7-520c7ef76171}#1", true)]
    [InlineData("", false)]
    [InlineData(@"ROOT\DISPLAY\0000"" /force", false)]
    [InlineData(@"ROOT\DISPLAY\0000 /subtree", false)]
    public void Only_plain_instance_ids_reach_pnputil(string id, bool valid)
    {
        Assert.Equal(valid, A.VirtualDisplay.ValidInstanceId(id));
        Assert.Equal(valid, A.VirtualDisplay.State.Parse($"instance={id}\noem=oem2.inf\n") is not null);
    }

    [Fact]
    public void Pnputil_says_why_in_its_last_two_lines() =>
        Assert.Equal(@"Failed to enable device:  ROOT\DISPLAY\0000 The device is not connected.",
            A.VirtualDisplay.LastLines("Microsoft PnP Utility\r\n\r\nFailed to enable device:  ROOT\\DISPLAY\\0000\r\nThe device is not connected.\r\n\n"));

    [Fact]
    public void A_device_that_switches_on_is_left_as_it_is()
    {
        var d = new FakeDevice { EnableExit = 0 };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.Equal(new A.VirtualDisplay.SwitchedOn(Old, null), on);
        Assert.Equal(["enable " + Old], d.Calls);
    }

    [Fact]
    public void A_device_that_cant_be_switched_but_runs_reloads_its_settings()
    {
        // Hyper-V: pnputil answers "not supported", the device stays on
        var d = new FakeDevice { EnableExit = 50, Running = { Old } };
        Assert.Equal(new A.VirtualDisplay.SwitchedOn(Old, null), A.VirtualDisplay.SwitchOn(d, Old));
        Assert.Equal(["enable " + Old, "reload"], d.Calls);
    }

    [Fact]
    public void A_device_not_connected_after_a_restart_comes_back_with_a_rescan_if_it_can()
    {
        var d = new FakeDevice { EnableExit = 1167, RescanHelps = true };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.Equal(Old, on.InstanceId);
        Assert.Contains("rescan", on.Recovered);
        Assert.Contains("1167", on.Recovered);
        Assert.Equal(["enable " + Old, "rescan", "enable " + Old], d.Calls);
    }

    [Fact]
    public void A_device_not_connected_after_a_restart_is_created_again()
    {
        var d = new FakeDevice { EnableExit = 1167 };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.Equal(New, on.InstanceId);
        Assert.Contains("created again as " + New, on.Recovered);
        Assert.Contains("The device is not connected", on.Recovered);
        Assert.Equal(["enable " + Old, "rescan", "enable " + Old, "remove " + Old, "create"], d.Calls);
    }

    [Fact]
    public void A_device_windows_no_longer_reports_is_created_again_without_enabling_it_first()
    {
        var d = new FakeDevice { Gone = true };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.Equal(New, on.InstanceId);
        Assert.Contains("isn't there", on.Recovered);
        Assert.Equal(["rescan", "remove " + Old, "create"], d.Calls);
    }

    [Fact]
    public void A_created_device_that_doesnt_start_is_switched_on_and_otherwise_reported()
    {
        var d = new FakeDevice { EnableExit = 1167, CreatedStarts = false };
        Assert.Equal(New, A.VirtualDisplay.SwitchOn(d, Old).InstanceId); // its own enable works (EnableExit is only for the old one)
        Assert.Equal("enable " + New, d.Calls[^1]);

        var stuck = new FakeDevice { EnableExit = 1167, CreatedStarts = false, NewEnableExit = 1167 };
        var ex = Assert.Throws<InvalidOperationException>(() => A.VirtualDisplay.SwitchOn(stuck, Old));
        Assert.Contains("didn't start", ex.Message);
    }

    [Fact]
    public void When_the_device_cant_be_created_again_the_message_says_both_whys()
    {
        var d = new FakeDevice { EnableExit = 1167, CreateFails = true };
        var ex = Assert.Throws<InvalidOperationException>(() => A.VirtualDisplay.SwitchOn(d, Old));
        Assert.Contains("pnputil exit code 1167", ex.Message);
        Assert.Contains("hash mismatch", ex.Message);
    }

    [Fact]
    public void An_old_node_that_cant_be_removed_doesnt_stop_the_new_device()
    {
        var d = new FakeDevice { EnableExit = 1167, RemoveFails = true };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.Equal(New, on.InstanceId);
        Assert.Contains("couldn't be removed", on.Recovered);
    }

    private const string Old = @"ROOT\DISPLAY\0000";
    private const string New = @"ROOT\DISPLAY\0001";

    /// <summary>The device as the lab saw it after a restart: there, but enabling it answers "not connected".</summary>
    private sealed class FakeDevice : A.VirtualDisplay.IDevice
    {
        public int EnableExit = 1167;
        public int NewEnableExit;
        public bool Gone, RescanHelps, CreateFails, RemoveFails, CreatedStarts = true;
        public HashSet<string> Running = [];
        public List<string> Calls = [];
        private bool _rescanned;

        public bool Present(string instance) => instance == New || !Gone;
        public bool Started(string instance) => Running.Contains(instance);

        public int SetEnabled(string instance, bool on, out string said)
        {
            Calls.Add((on ? "enable " : "disable ") + instance);
            var exit = instance == New ? NewEnableExit : _rescanned && RescanHelps ? 0 : EnableExit;
            said = exit == 1167 ? $"Failed to enable device:  {instance} The device is not connected." : exit == 0 ? "" : "The request is not supported.";
            if (exit == 0 && on) Running.Add(instance);
            return exit;
        }

        public void Rescan() { Calls.Add("rescan"); _rescanned = true; }
        public bool Remove(string instance) { Calls.Add("remove " + instance); return !RemoveFails; }

        public string Create()
        {
            Calls.Add("create");
            if (CreateFails) throw new InvalidDataException("Virtual display file MttVDD.dll isn't the shipped one (hash mismatch).");
            if (CreatedStarts) Running.Add(New);
            return New;
        }

        public void Reload() => Calls.Add("reload");
    }
}
