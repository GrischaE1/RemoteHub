using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Microsoft.Win32;
using RemoteHub.Remoting;

namespace RemoteHub.Tests;

/// <summary>
/// The registry scripts, run against a local runspace and against Windows PowerShell 5.1 (what servers run) over the
/// remoting protocol. Works in a scratch key below HKCU\Software that is removed afterwards.
/// </summary>
public class RemoteRegistryTests : IDisposable
{
    private readonly string _sub = @"Software\RdpManagerTests-" + Guid.NewGuid().ToString("N");
    private string Root => @"HKEY_CURRENT_USER\" + _sub;

    public RemoteRegistryTests()
    {
        using var k = Registry.CurrentUser.CreateSubKey(_sub);
        k.SetValue("", "default text");
        k.SetValue("Path", @"%SystemRoot%\x", RegistryValueKind.ExpandString);
        k.SetValue("Lines", new[] { "one", "two|pipe" }, RegistryValueKind.MultiString);
        k.SetValue("Big", unchecked((int)0xFFFFFFFE), RegistryValueKind.DWord);
        k.SetValue("Q", 5L, RegistryValueKind.QWord);
        k.SetValue("Bin", new byte[] { 1, 0xAB }, RegistryValueKind.Binary);
        k.SetValue("Ünïcode\nname", "x");
        k.CreateSubKey(@"Child\Grand").Dispose();
        k.CreateSubKey("Leaf").Dispose();
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_sub, false);

    private static Runspace Local() { var rs = RunspaceFactory.CreateRunspace(); rs.Open(); return rs; }

    private static bool HasWindowsPowerShell => File.Exists(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft")]
    [InlineData(@"HKLM:\SOFTWARE\", @"HKEY_LOCAL_MACHINE\SOFTWARE")]
    [InlineData(@"Computer\HKEY_CURRENT_USER\Software", @"HKEY_CURRENT_USER\Software")]
    [InlineData(@"Registry::HKU\.DEFAULT", @"HKEY_USERS\.DEFAULT")]
    [InlineData("hkcr", "HKEY_CLASSES_ROOT")]
    [InlineData("Computer", "")]
    [InlineData("  ", "")]
    public void Paths_are_normalized(string input, string expected) => Assert.Equal(expected, RemoteRegistry.Normalize(input));

    [Fact]
    public void Unknown_hive_is_rejected_and_top_level_keys_are_protected()
    {
        Assert.Throws<ArgumentException>(() => RemoteRegistry.Normalize(@"HKXX\Software"));
        Assert.True(RemoteRegistry.IsProtected("HKEY_LOCAL_MACHINE"));
        Assert.True(RemoteRegistry.IsProtected(@"HKEY_LOCAL_MACHINE\SOFTWARE"));
        Assert.False(RemoteRegistry.IsProtected(@"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor"));
        Assert.Equal(@"HKEY_LOCAL_MACHINE", RemoteRegistry.Parent(@"HKEY_LOCAL_MACHINE\SOFTWARE"));
        Assert.Equal("", RemoteRegistry.Parent("HKEY_LOCAL_MACHINE"));
    }

    [Theory]
    [InlineData(RegKind.DWord, "4294967295", false, true)]
    [InlineData(RegKind.DWord, "4294967296", false, false)]
    [InlineData(RegKind.DWord, "ff", true, true)]
    [InlineData(RegKind.DWord, "0x10", false, true)]
    [InlineData(RegKind.DWord, "-1", false, false)]
    [InlineData(RegKind.QWord, "18446744073709551615", false, true)]
    [InlineData(RegKind.Binary, "01 ab,FF-00", false, true)]
    [InlineData(RegKind.Binary, "0a1", false, false)]
    [InlineData(RegKind.Unknown, "", false, false)]
    public void Editor_input_is_validated(RegKind kind, string text, bool hex, bool ok) =>
        Assert.Equal(ok, RemoteRegistry.TryParse(kind, text, hex, out _, out _));

    [Fact]
    public void Numbers_display_like_regedit()
    {
        Assert.Equal("0xfffffffe (4294967294)", RemoteRegistry.Display(RegKind.DWord, "4294967294"));
        Assert.Equal("fffffffe", RemoteRegistry.EditText(RegKind.DWord, "4294967294", hex: true));
        Assert.True(RemoteRegistry.TryParse(RegKind.DWord, "fffffffe", true, out var d, out _));
        Assert.Equal(unchecked((int)0xFFFFFFFE), d);
    }

    [Fact]
    public void Local_runspace_lists_and_edits() => ListAndEdit(Local());

    [SkippableFact]
    public void Windows_PowerShell_51_lists_and_edits()
    {
        Skip.IfNot(HasWindowsPowerShell, "Windows PowerShell 5.1 isn't installed.");
        var proc = new PowerShellProcessInstance(new Version(5, 1), null, null, false);
        var rs = RunspaceFactory.CreateOutOfProcessRunspace(null, proc);
        rs.Open();
        using (var v = PowerShell.Create())
        {
            v.Runspace = rs;
            Assert.Equal(5, (int)v.AddScript("$PSVersionTable.PSVersion.Major").Invoke()[0].BaseObject);
        }
        try { ListAndEdit(rs); }
        finally { proc.Dispose(); }
    }

    private void ListAndEdit(Runspace rs)
    {
        using var owned = rs;
        var l = RemoteRegistry.List(rs, @"HKCU\" + _sub);
        Assert.Equal(Root, l.Path);
        Assert.Equal([new RegSubKey("Child", true), new RegSubKey("Leaf", false)], l.Keys);
        var v = l.Values.ToDictionary(x => x.Name);
        Assert.Equal("", l.Values[0].Name); // default first
        Assert.Equal("default text", v[""].Data);
        Assert.Equal((RegKind.ExpandString, @"%SystemRoot%\x"), (v["Path"].Kind, v["Path"].Data)); // not expanded
        Assert.Equal("one\ntwo|pipe", v["Lines"].Data);
        Assert.Equal("4294967294", v["Big"].Data);
        Assert.Equal((RegKind.QWord, "5"), (v["Q"].Kind, v["Q"].Data));
        Assert.Equal("01 AB", v["Bin"].Data);
        Assert.True(v.ContainsKey("Ünïcode\nname"));

        Assert.Contains(RemoteRegistry.List(rs, "").Keys, k => k.Name == "HKEY_LOCAL_MACHINE");
        Assert.Throws<RemoteCommandException>(() => RemoteRegistry.List(rs, Root + @"\missing"));

        // values: create, overwrite, rename, delete
        RemoteRegistry.TryParse(RegKind.MultiString, "a\r\nb", false, out var multi, out _);
        RemoteRegistry.SetValue(rs, Root, "New", RegKind.MultiString, multi, createOnly: true);
        Assert.Throws<RemoteCommandException>(() => RemoteRegistry.SetValue(rs, Root, "new", RegKind.String, "x", createOnly: true));
        RemoteRegistry.TryParse(RegKind.Binary, "", false, out var empty, out _);
        RemoteRegistry.SetValue(rs, Root, "EmptyBin", RegKind.Binary, empty, createOnly: true);
        RemoteRegistry.TryParse(RegKind.DWord, "ffffffff", true, out var dw, out _);
        RemoteRegistry.SetValue(rs, Root, "Big", RegKind.DWord, dw, createOnly: false);
        RemoteRegistry.RenameValue(rs, Root, "Path", "Path2");
        Assert.Throws<RemoteCommandException>(() => RemoteRegistry.RenameValue(rs, Root, "Q", "Bin"));
        RemoteRegistry.DeleteValues(rs, Root, ["Q", "Bin"]);
        using (var k = Registry.CurrentUser.OpenSubKey(_sub)!)
        {
            Assert.Equal(new[] { "a", "b" }, (string[])k.GetValue("New")!);
            Assert.Empty((byte[])k.GetValue("EmptyBin")!);
            Assert.Equal(-1, (int)k.GetValue("Big")!);
            Assert.Equal(RegistryValueKind.ExpandString, k.GetValueKind("Path2"));
            Assert.Equal(@"%SystemRoot%\x", k.GetValue("Path2", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Null(k.GetValue("Path"));
            Assert.Null(k.GetValue("Q"));
            Assert.Null(k.GetValue("Bin"));
        }

        // keys: create, rename (RegRenameKey), export, delete
        Assert.Equal(Root + @"\Made", RemoteRegistry.CreateKey(rs, Root, "Made"));
        Assert.Throws<RemoteCommandException>(() => RemoteRegistry.CreateKey(rs, Root, "made"));
        // Opt-in: on Windows build 26300 a key rename (RegRenameKey) once crashed the PC – a bug in the Windows kernel
        // (bugcheck 0x3B in nt!CmpKeySecurityIncrementReferenceCount under NtRenameKey). Set REMOTEHUB_TEST_REGRENAME=1 to run it.
        var child = "Child";
        if (Environment.GetEnvironmentVariable("REMOTEHUB_TEST_REGRENAME") == "1")
        {
            Assert.Equal(Root + @"\Renamed", RemoteRegistry.RenameKey(rs, Root + @"\Child", "Renamed"));
            Assert.Throws<RemoteCommandException>(() => RemoteRegistry.RenameKey(rs, Root + @"\Renamed", "Leaf"));
            child = "Renamed";
        }
        Assert.NotNull(Registry.CurrentUser.OpenSubKey(_sub + $@"\{child}\Grand"));
        var reg = System.Text.Encoding.Unicode.GetString(RemoteRegistry.Export(rs, Root));
        Assert.Contains("Windows Registry Editor Version 5.00", reg);
        Assert.Contains($@"\{child}\Grand]", reg);
        RemoteRegistry.DeleteKey(rs, Root + $@"\{child}");
        Assert.Null(Registry.CurrentUser.OpenSubKey(_sub + $@"\{child}"));
        Assert.Throws<RemoteCommandException>(() => RemoteRegistry.DeleteKey(rs, @"HKEY_CURRENT_USER\Software"));
        Assert.NotNull(Registry.CurrentUser.OpenSubKey("Software"));
    }
}
