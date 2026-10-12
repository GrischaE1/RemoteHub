extern alias agent;
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
}
