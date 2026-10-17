extern alias agent;
using System.Collections.Generic;
using A = agent::RemoteHub.Agent;

namespace RemoteHub.Tests;

/// <summary>
/// Which windows the agent hides for the HiddenApps policy and the user's choice: entries by program path, by
/// AppUserModelID and by a Store app's path in another version of its package (D-HIDE-01).
/// </summary>
public class HiddenAppMatchTests
{
    private const string Notepad = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe";
    private const string NotepadAumid = "aumid:microsoft.windowsnotepad_8wekyb3d8bbwe!app";

    private static HashSet<string> Hidden(params string[] entries) => [.. entries.Select(A.AppIds.Normalize)];

    private static bool IsHidden(HashSet<string> hidden, string app, string exe, bool unresolved = false) =>
        A.AppIds.IsHidden(hidden, A.AppIds.Normalize(app), A.AppIds.Normalize(exe), unresolved);

    private sealed class Apps(params A.AppWindow[] windows) : A.IAppWindows
    {
        public IReadOnlyList<A.AppWindow> Visible() => windows;
        public IReadOnlyList<A.RunningApp> Running() => [];
        public A.AppIdentity ForegroundApp() => A.AppIdentity.None;
        public A.AppIdentity ClipboardOwnerApp() => A.AppIdentity.None;
    }

    [Fact]
    public void A_store_app_listed_by_its_program_path_is_covered()
    {
        // What the helper reports for the Store Notepad: the app by its AUMID, the program's path next to it.
        var notepad = new A.AppWindow(1, NotepadAumid, new A.ScreenRect(0, 0, 100, 100), true, Exe: A.AppIds.Normalize(Notepad));
        var mask = new A.AppMask(new Apps(notepad));
        var rects = mask.Compute(Hidden(Notepad), 0, out _);
        Assert.Contains(rects, r => r.Contains(50, 50));
    }

    [Fact]
    public void A_store_app_stays_hidden_after_an_update_moves_its_folder()
    {
        var updated = Notepad.Replace("11.2607.14.0", "11.2611.3.0");
        Assert.True(IsHidden(Hidden(Notepad), NotepadAumid, updated));
        Assert.True(IsHidden(Hidden(updated), NotepadAumid, Notepad));
        Assert.True(IsHidden(Hidden(Notepad.Replace("_x64_", "_arm64_")), NotepadAumid, Notepad));
        // Packages on another volume (installed to D:) are the same app.
        Assert.True(IsHidden(Hidden(Notepad), NotepadAumid, @"D:\WindowsApps\Microsoft.WindowsNotepad_11.2611.3.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe"));
    }

    [Fact]
    public void Another_program_or_another_package_is_not_hidden_by_a_store_app_path()
    {
        var hidden = Hidden(Notepad);
        Assert.False(IsHidden(hidden, NotepadAumid.Replace("!app", "!other"), Notepad.Replace(@"\Notepad.exe", @"\Other.exe")));
        Assert.False(IsHidden(hidden, "aumid:contoso.notepad_1234567890abc!app", Notepad.Replace("Microsoft.WindowsNotepad", "Contoso.Notepad")));
        Assert.False(IsHidden(hidden, "aumid:microsoft.windowsnotepad_otherpublisher!app", Notepad.Replace("8wekyb3d8bbwe", "otherpublisher")));
        Assert.False(IsHidden(hidden, @"c:\windows\system32\notepad.exe", @"c:\windows\system32\notepad.exe"));
    }

    [Fact]
    public void An_app_is_hidden_by_its_id_or_its_path_however_the_entry_is_written()
    {
        Assert.True(IsHidden(Hidden(NotepadAumid.ToUpperInvariant()), NotepadAumid, Notepad));
        Assert.True(IsHidden(Hidden($"\"{Notepad.ToUpperInvariant()}\""), NotepadAumid, Notepad));
        Assert.True(IsHidden(Hidden(@"C:/Apps/Vault.exe"), @"c:\apps\vault.exe", @"c:\apps\vault.exe"));
        Assert.True(IsHidden(Hidden(@"C:\Apps\Tools\..\\Vault.exe"), @"c:\apps\vault.exe", @"c:\apps\vault.exe"));
        Assert.True(IsHidden(Hidden(@"\\?\C:\Apps\Vault.exe"), @"c:\apps\vault.exe", @"c:\apps\vault.exe"));
        Assert.False(IsHidden(Hidden(@"C:\Apps\Vault.exe"), @"c:\apps\vault2.exe", @"c:\apps\vault2.exe"));
        Assert.False(IsHidden(Hidden(), NotepadAumid, Notepad));
    }

    [Fact]
    public void The_shell_is_never_hidden_as_an_app_even_when_its_program_is_listed()
    {
        // The taskbar and desktop are explorer.exe too; the helper names them "" so they stay visible.
        var hidden = Hidden(@"C:\Windows\explorer.exe");
        Assert.False(IsHidden(hidden, "", @"c:\windows\explorer.exe"));
        Assert.True(IsHidden(hidden, @"c:\windows\explorer.exe", @"c:\windows\explorer.exe")); // a folder window
    }

    [Fact]
    public void An_unresolved_store_frame_is_hidden_while_a_store_app_is_listed_by_path()
    {
        const string frameHost = @"c:\windows\system32\applicationframehost.exe";
        Assert.True(IsHidden(Hidden(Notepad), "", frameHost, unresolved: true));
        Assert.True(IsHidden(Hidden(NotepadAumid), "", frameHost, unresolved: true));
        Assert.False(IsHidden(Hidden(@"C:\Apps\Vault.exe"), "", frameHost, unresolved: true));
    }

    [Theory]
    [InlineData(Notepad, @"microsoft.windowsnotepad_8wekyb3d8bbwe\notepad\notepad.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.23.1.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", @"microsoft.windowsterminal_8wekyb3d8bbwe\windowsterminal.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_8wekyb3d8bbwe\Notepad.exe", "")] // a family name, not a package folder
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe\", "")]
    [InlineData(@"C:\Program Files\WindowsApps\Notepad.exe", "")]
    [InlineData(@"C:\Program Files\WindowsApps\_1.0_x64__pub\a.exe", "")]
    [InlineData(@"C:\Apps\Vault.exe", "")]
    [InlineData("aumid:microsoft.windowsnotepad_8wekyb3d8bbwe!app", "")]
    public void A_package_key_is_the_family_and_the_path_inside_the_package(string path, string key) =>
        Assert.Equal(key, A.AppIds.PackageKey(A.AppIds.Normalize(path)));
}
