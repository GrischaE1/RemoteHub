using System.Xml.Linq;

namespace RemoteHub.Tests;

/// <summary>RemoteHub's MSI (installer\Package.wxs): what it leaves on the PC that has to survive an upgrade.</summary>
public class InstallerTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    private static XDocument Package
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "installer", "Package.wxs"))) return XDocument.Load(Path.Combine(dir.FullName, "installer", "Package.wxs"));
            throw new FileNotFoundException("installer\\Package.wxs not found above " + AppContext.BaseDirectory);
        }
    }

    [Fact]
    public void The_Start_menu_shortcut_takes_its_icon_from_the_exe_so_a_pinned_copy_keeps_it_after_an_upgrade()
    {
        var shortcut = Assert.Single(Package.Descendants(Wix + "Shortcut"));
        // Advertised, or with an Icon from the Icon table, the icon lives in C:\Windows\Installer\{ProductCode}: gone
        // after the next upgrade, and the taskbar pin shows a blank page.
        Assert.Contains((string?)shortcut.Attribute("Advertise"), new[] { null, "no" });
        Assert.Null(shortcut.Attribute("Icon"));
        Assert.Equal("[INSTALLFOLDER]RemoteHub.exe", (string?)shortcut.Attribute("Target")); // a plain shortcut to the exe
        Assert.Contains(Package.Descendants(Wix + "File"), f => ((string?)f.Attribute("Source"))?.EndsWith(@"\RemoteHub.exe") == true);
    }

    [Fact]
    public void Every_version_is_a_major_upgrade_so_the_product_code_changes()
    {
        // The reason the shortcut must not depend on the product code's folder.
        Assert.Single(Package.Descendants(Wix + "MajorUpgrade"));
        Assert.Null(Package.Descendants(Wix + "Package").Single().Attribute("ProductCode"));
    }
}
