extern alias agent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void A_device_created_again_is_switched_off_when_hidden()
    {
        var d = new FakeDevice { EnableExit = 1167 };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        Assert.True(d.Started(on.InstanceId));
        var waits = new List<int>();
        var line = A.VirtualDisplay.SwitchOff(d, on.InstanceId, waits.Add);
        Assert.StartsWith("Virtual display off: " + New + " switched off", line);
        Assert.False(d.On(New));
        Assert.Equal("disable " + New, d.Calls[^1]);
        Assert.Empty(waits);
    }

    [Fact]
    public void After_a_re_create_every_device_windows_shows_on_is_switched_off()
    {
        // the state file names the old node Windows no longer reports; the new one runs, and a third shows OK without running
        const string third = @"ROOT\DISPLAY\0002";
        var d = new FakeDevice { Gone = true, Others = { New, third }, Running = { New } };
        var line = A.VirtualDisplay.SwitchOff(d, Old, _ => { });
        Assert.False(d.On(New));
        Assert.False(d.On(third));
        Assert.Contains(New + " switched off", line);
        Assert.Contains(third + " switched off", line);
        Assert.DoesNotContain("disable " + Old, d.Calls);
    }

    [Fact]
    public void A_switch_off_doesnt_hold_the_lock_between_tries_and_ends_when_a_connection_switches_it_on()
    {
        var gate = new object();
        var wanted = true;
        var d = new FakeDevice { Running = { Old }, DisableBusy = 99 };
        var lockFree = false;
        var line = A.VirtualDisplay.SwitchOff(d, Old, _ =>
        {
            // a connection's Show on another thread: it gets the lock during the wait and switches the monitor on
            lockFree = Task.Run(() => { lock (gate) wanted = false; }).Wait(TimeSpan.FromSeconds(5));
        }, removeIfStuck: true, () => wanted, gate);
        Assert.True(lockFree);
        Assert.Equal("Virtual display left on: a connection switched it on again meanwhile", line);
        Assert.Single(d.Calls, "disable " + Old);
        Assert.DoesNotContain("remove " + Old, d.Calls);
        Assert.True(d.On(Old));
    }

    [Fact]
    public void A_device_created_again_and_still_in_use_is_switched_off_on_a_later_try()
    {
        // Server 2025: right after the connection ended the new device stayed on
        var d = new FakeDevice { EnableExit = 1167 };
        var on = A.VirtualDisplay.SwitchOn(d, Old);
        d.DisableBusy = 2;
        var waits = new List<int>();
        Assert.Contains(New + " switched off on try 3", A.VirtualDisplay.SwitchOff(d, on.InstanceId, waits.Add));
        Assert.False(d.On(New));
        Assert.Equal(A.VirtualDisplay.OffRetryWaits.Take(2), waits);
        Assert.DoesNotContain("remove " + New, d.Calls);
    }

    [Fact]
    public void A_device_that_wont_switch_off_is_removed_and_that_is_logged()
    {
        var d = new FakeDevice { Running = { Old }, DisableBusy = 99 };
        var line = A.VirtualDisplay.SwitchOff(d, Old, _ => { });
        Assert.Contains("not switched off", line);
        Assert.Contains("3010", line);
        Assert.Contains("removed instead", line);
        Assert.False(d.Started(Old));
        Assert.Equal(A.VirtualDisplay.OffRetryWaits.Length + 1, d.Calls.Count(c => c == "disable " + Old));

        // and the next connection creates it again
        d.Gone = true;
        Assert.Equal(New, A.VirtualDisplay.SwitchOn(d, Old).InstanceId);
    }

    [Fact]
    public void A_device_that_neither_switches_off_nor_goes_away_is_logged()
    {
        var d = new FakeDevice { Running = { Old }, DisableBusy = 99, RemoveFails = true };
        var line = A.VirtualDisplay.SwitchOff(d, Old, _ => { });
        Assert.Contains("couldn't be removed", line);
        Assert.True(d.Started(Old));
    }

    [Fact]
    public void A_device_already_off_is_logged_with_its_state()
    {
        // service start after a restart: the old node has a problem or isn't reported at all – nothing to switch, but said
        var disabled = new FakeDevice { Off = { Old } };
        Assert.Equal($@"Virtual display off: no device to switch off ({Old}: not running, problem 22 (disabled))",
            A.VirtualDisplay.SwitchOff(disabled, Old, _ => throw new InvalidOperationException("no waiting")));
        Assert.Empty(disabled.Calls);
        Assert.Equal($"Virtual display off: no device to switch off ({Old}: not reported by Windows)",
            A.VirtualDisplay.SwitchOff(new FakeDevice { Gone = true }, Old, _ => throw new InvalidOperationException("no waiting")));
    }

    [Fact]
    public async Task The_service_start_doesnt_wait_for_a_device_that_hangs_and_wont_switch_off()
    {
        // Server 2025, 0.18.18: switching off at the service start kept the service from reporting running
        using var hang = new ManualResetEventSlim(false);
        var d = new FakeDevice { Running = { Old }, DisableBusy = 99, Hang = hang };
        var lines = new List<string>();
        var watch = Stopwatch.StartNew();
        var reset = Switch(d, Old).ResetInBackground(l => { lock (lines) lines.Add(l); });
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the start path took {watch.Elapsed}");
        Assert.False(reset.IsCompleted);

        hang.Set(); // pnputil answers (or is stopped at its time limit)
        await reset.WaitAsync(TimeSpan.FromSeconds(10));
        // every failed try is logged as it happens, then the result
        Assert.Equal(A.VirtualDisplay.OffRetryWaits.Length + 1, lines.Count(l => l.Contains("still on after try", StringComparison.Ordinal)));
        var line = lines[^1];
        Assert.Contains("not switched off", line);
        Assert.Contains("until a connection that used it ends or the agent starts again", line);
        Assert.DoesNotContain(d.Calls, c => c.StartsWith("remove", StringComparison.Ordinal)); // never removed at the start
    }

    [Fact]
    public async Task The_service_start_switches_the_device_off_and_says_so()
    {
        var d = new FakeDevice { Running = { Old } };
        var lines = new List<string>();
        await Switch(d, Old).ResetInBackground(lines.Add).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(d.On(Old));
        Assert.StartsWith($"Virtual display off: {Old} switched off", Assert.Single(lines));
    }

    [Fact]
    public void Re_created_on_the_first_show_then_two_connections_end_switches_off_once_at_the_last_end()
    {
        // W11 and Server 2025, 0.18.20: after a re-create no switch-off line was ever written
        var d = new FakeDevice { EnableExit = 1167 };
        var state = Old;
        var sw = new A.VirtualDisplay.Switch(d, () => state, id => state = id, (_, _) => { }, _ => { });
        var a = A.VirtualDisplay.NewLease();
        var b = A.VirtualDisplay.NewLease();

        Assert.Null(sw.Show(a, 1600, 900, out var note));
        Assert.Contains("created again as " + New, note);
        Assert.Equal(New, state);
        Assert.Null(sw.Show(a, 1280, 720, out _)); // a resize
        Assert.Null(sw.Show(b, 1600, 900, out _)); // the second connection
        Assert.Equal("Virtual display stays on for 1 other connection(s)", sw.Hide(a));
        Assert.True(d.On(New));

        d.ReloadFails = true; // the driver's pipe doesn't answer: b still counts, at the size it had
        Assert.Null(sw.Show(b, 1280, 720, out note));
        Assert.Contains("kept its size 1600×900", note);

        var line = sw.Hide(b);
        Assert.StartsWith($"Virtual display off: {New} switched off", line);
        Assert.Single(d.Calls, "disable " + New);
        Assert.False(d.On(New));
        Assert.Null(sw.Hide(b)); // a second end of the same connection changes nothing
        Assert.Null(sw.Hide(a));
        Assert.Single(d.Calls, "disable " + New);
    }

    [Fact]
    public void A_connection_that_ends_while_its_monitor_is_switched_on_switches_it_off_again()
    {
        var d = new FakeDevice { EnableExit = 1167 };
        var state = Old;
        var sw = new A.VirtualDisplay.Switch(d, () => state, id => state = id, (_, _) => { }, _ => { });
        var a = A.VirtualDisplay.NewLease();
        string? ended = "not called";
        d.OnCreate = () => ended = sw.Hide(a); // the end arrives while the device is created again
        Assert.Equal("the connection ended while the virtual monitor was switched on", sw.Show(a, 1600, 900, out var note));
        Assert.Null(ended); // nothing was counted yet
        Assert.Contains($"Virtual display off: {New} switched off", note);
        Assert.False(d.On(New));
        Assert.Equal("the connection has ended", sw.Show(a, 1600, 900, out _));
    }

    [Fact]
    public async Task A_connection_end_isnt_held_up_by_another_connections_device_step()
    {
        using var reload = new ManualResetEventSlim(false);
        var d = new FakeDevice { Running = { Old }, EnableExit = 0 };
        var sw = Switch(d, Old);
        var a = A.VirtualDisplay.NewLease();
        var b = A.VirtualDisplay.NewLease();
        Assert.Null(sw.Show(a, 1600, 900, out _));
        Assert.Null(sw.Show(b, 1600, 900, out _));
        d.ReloadBlock = reload;
        var resize = Task.Run(() => sw.Show(b, 1280, 720, out _)); // waits on the driver's pipe
        Assert.True(d.ReloadStarted.Wait(TimeSpan.FromSeconds(5)));
        var watch = Stopwatch.StartNew();
        Assert.Equal("Virtual display stays on for 1 other connection(s)", sw.Hide(a));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the end waited {watch.Elapsed}");
        reload.Set();
        Assert.Null(await resize.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.StartsWith($"Virtual display off: {Old} switched off", sw.Hide(b));
    }

    private static A.VirtualDisplay.Switch Switch(FakeDevice d, string instance) => new(d, () => instance, _ => { }, (_, _) => { }, _ => { });

    [Fact]
    public void A_program_that_doesnt_answer_in_time_is_stopped()
    {
        var watch = Stopwatch.StartNew();
        var exit = A.VirtualDisplay.Run(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 30 127.0.0.1"),
            TimeSpan.FromSeconds(1), out var output);
        Assert.Equal(A.VirtualDisplay.TimedOut, exit);
        Assert.Contains("didn't answer within 1 s", output);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void A_program_that_ends_gives_its_exit_code_and_output()
    {
        var exit = A.VirtualDisplay.Run(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo said & exit 3"),
            TimeSpan.FromSeconds(20), out var output);
        Assert.Equal(3, exit);
        Assert.Contains("said", output);
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
        /// <summary>Reported with a problem (disabled): Windows shows them as not OK.</summary>
        public HashSet<string> Off = [];
        /// <summary>Further devices with the hardware ID that Windows reports.</summary>
        public HashSet<string> Others = [];
        public List<string> Calls = [];
        private readonly HashSet<string> _removed = [];
        private bool _rescanned, _created;

        public bool Present(string instance) => !_removed.Contains(instance) && (instance == Old ? !Gone : instance == New ? _created || Others.Contains(New) : Others.Contains(instance));
        public bool Started(string instance) => Present(instance) && Running.Contains(instance);
        public bool On(string instance) => Present(instance) && !Off.Contains(instance);
        public IReadOnlyList<string> Nodes() => new[] { Old, New }.Concat(Others).Distinct().Where(Present).ToList();
        public string Describe(string instance) => !Present(instance) ? $"{instance}: not reported by Windows"
            : $"{instance}: {(Running.Contains(instance) ? "running" : "not running")}{(Off.Contains(instance) ? ", problem 22 (disabled)" : "")}";

        /// <summary>Switching off: refused this many times while the device still runs (in use), then it switches off.</summary>
        public int DisableBusy;

        /// <summary>pnputil hangs until this is set.</summary>
        public ManualResetEventSlim? Hang;

        public int SetEnabled(string instance, bool on, out string said)
        {
            Hang?.Wait();
            Calls.Add((on ? "enable " : "disable ") + instance);
            if (!on)
            {
                if (DisableBusy > 0) { DisableBusy--; said = "Failed to disable device: " + instance; return 3010; }
                said = "";
                Running.Remove(instance);
                Off.Add(instance);
                return 0;
            }
            var exit = instance == New ? NewEnableExit : _rescanned && RescanHelps ? 0 : EnableExit;
            said = exit == 1167 ? $"Failed to enable device:  {instance} The device is not connected." : exit == 0 ? "" : "The request is not supported.";
            if (exit == 0) { Running.Add(instance); Off.Remove(instance); }
            return exit;
        }

        public void Rescan() { Calls.Add("rescan"); _rescanned = true; }
        public bool Remove(string instance)
        {
            Calls.Add("remove " + instance);
            if (!RemoveFails) { Running.Remove(instance); _removed.Add(instance); }
            return !RemoveFails;
        }

        public string Create()
        {
            Calls.Add("create");
            OnCreate?.Invoke();
            if (CreateFails) throw new InvalidDataException("Virtual display file MttVDD.dll isn't the shipped one (hash mismatch).");
            _created = true;
            _removed.Remove(New);
            if (CreatedStarts) Running.Add(New);
            return New;
        }

        /// <summary>Something else that happens while the device is created again (a connection's end).</summary>
        public Action? OnCreate;
        /// <summary>The driver's control pipe doesn't answer.</summary>
        public bool ReloadFails;
        /// <summary>The driver's control pipe answers only when this is set.</summary>
        public ManualResetEventSlim? ReloadBlock;
        public ManualResetEventSlim ReloadStarted { get; } = new(false);

        public void Reload()
        {
            Calls.Add("reload");
            ReloadStarted.Set();
            ReloadBlock?.Wait();
            if (ReloadFails) throw new IOException("the driver's control pipe didn't answer within 3 s");
        }
    }
}
