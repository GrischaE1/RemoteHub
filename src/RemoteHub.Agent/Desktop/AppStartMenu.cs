using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Win32;

namespace RemoteHub.Agent;

/// <summary>
/// The device's Start menu as a list of apps for app tabs (docs/app-tabs-design.md §6.5): the shortcuts in the common
/// Start menu and in the account's own, read by the service without running anything – <c>IShellLinkW</c> loads each
/// one and is never asked to <c>Resolve</c> it. Shortcuts are files other programs wrote: every field is capped and
/// checked, only programs (<c>.exe</c>) and consoles (<c>.msc</c>) that are on the device are listed, and nothing
/// behind a junction or symbolic link in the Start menu is read.
/// </summary>
internal static unsafe partial class AppStartMenu
{
    public const int MaxEntries = 2000, MaxBytes = 4 * 1024 * 1024, MaxDepth = 8;
    public const int MaxPath = 1024, MaxArguments = 4096, MaxName = 256;

    /// <summary>What a shortcut says, as written (environment variables not expanded).</summary>
    internal sealed record Shortcut(string Name, string Group, string Target, string Arguments, string WorkingFolder, bool RunAsAdministrator);

    /// <summary>
    /// The Start menu's apps for <paramref name="accountSid"/>'s list: the common Start menu, then theirs when their
    /// profile is on this device. <paramref name="read"/> reads one shortcut file (null: unreadable).
    /// </summary>
    public static DesktopAppList List(AgentPolicy p, string? accountSid) =>
        List(Roots(accountSid), p, ReadAll, Environment.ExpandEnvironmentVariables, File.Exists, IconsOf);

    /// <summary>
    /// The list from <paramref name="roots"/>: read, checked, filtered by policy, without duplicates, within the limits.
    /// <paramref name="icons"/> gives each listed app's icon from the file its shortcut points to (the program, or the
    /// console of an <c>.msc</c>); icons are left out rather than apps once the list would pass <see cref="MaxBytes"/>.
    /// </summary>
    internal static DesktopAppList List(IEnumerable<string> roots, AgentPolicy p, Func<IReadOnlyList<(string File, string Group)>, IReadOnlyList<Shortcut?>> read,
        Func<string, string> expand, Func<string, bool> exists, Func<IReadOnlyList<string>, IReadOnlyList<byte[]?>>? icons = null)
    {
        var files = new List<(string File, string Group)>();
        var truncated = false;
        foreach (var root in roots)
            foreach (var f in Files(root))
            {
                if (files.Count == MaxEntries * 2) { truncated = true; break; } // more than the list can hold is never read
                files.Add(f);
            }
        var apps = new List<DesktopListedApp>();
        var sources = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var s in read(files))
        {
            if (s is null || Entry(s, p, expand, exists) is not { } app) continue;
            if (!seen.Add(AppIds.Normalize(expand(app.Program)) + "\n" + app.Arguments)) continue;
            bytes += 2L * (app.Name.Length + app.Group.Length + app.Program.Length + app.Arguments.Length + app.WorkingFolder.Length) + 64;
            if (apps.Count == MaxEntries || bytes > MaxBytes) { truncated = true; break; }
            apps.Add(app);
            sources.Add(expand(s.Target));
        }
        if (icons is not null && apps.Count > 0)
        {
            var found = icons(sources);
            for (var i = 0; i < apps.Count && i < found.Count; i++)
            {
                // JSON sends the PNG as base64: a third more than its bytes
                if (found[i] is not { Length: > 0 and <= IconPng.MaxBytes } png || !IconPng.IsPng(png) || bytes + png.Length * 4L / 3 > MaxBytes) continue;
                bytes += png.Length * 4L / 3;
                apps[i] = apps[i] with { Icon = png };
            }
        }
        return new DesktopAppList([.. apps.OrderBy(a => a.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)], truncated);
    }

    /// <summary>
    /// The <c>.lnk</c> files under <paramref name="root"/> with their group (the folder below the root, "" for the top),
    /// at most <see cref="MaxDepth"/> folders deep. A root, folder or file that is a reparse point is left out – a link
    /// there could point anywhere on the device.
    /// </summary>
    internal static IEnumerable<(string File, string Group)> Files(string root)
    {
        if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) yield break;
        var folders = new Queue<(string Path, int Depth)>([(root, 0)]);
        while (folders.Count > 0)
        {
            var (folder, depth) = folders.Dequeue();
            FileSystemInfo[] items;
            try { items = new DirectoryInfo(folder).GetFileSystemInfos(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // By name without ".lnk": of two shortcuts to the same app, "Tool" comes before "Tool Copy" and is the one kept.
            foreach (var item in items.OrderBy(i => Path.GetFileNameWithoutExtension(i.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (item is DirectoryInfo d) { if (depth + 1 < MaxDepth) folders.Enqueue((d.FullName, depth + 1)); }
                else if (item.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    yield return (item.FullName, Path.GetRelativePath(root, folder) is var g && g != "." ? g : "");
            }
        }
    }

    /// <summary>
    /// A shortcut as an app, or null when it isn't one: a program (<c>.exe</c>) as it is, a console (<c>.msc</c>) as
    /// <c>mmc.exe</c> with the console first; anything else – a document, a web link, a folder – and a target that isn't
    /// on the device or a field over its cap is left out. With <see cref="AgentPolicy.DesktopAppsAllowed"/> only allowed
    /// programs; without <see cref="AgentPolicy.DesktopAppsElevated"/> nothing is marked to run as administrator.
    /// </summary>
    internal static DesktopListedApp? Entry(Shortcut s, AgentPolicy p, Func<string, string> expand, Func<string, bool> exists)
    {
        var name = s.Name.Trim();
        if (name.Length is 0 or > MaxName || name.Any(char.IsControl) || s.Group.Length > MaxPath) return null;
        if (s.Target.Length is 0 or > MaxPath || s.Arguments.Length > MaxArguments || s.WorkingFolder.Length > MaxPath) return null;
        if (s.Target.Any(char.IsControl) || s.Arguments.Contains('\0') || s.WorkingFolder.Any(char.IsControl)) return null;
        var target = expand(s.Target);
        if (!Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal) || !exists(target)) return null;
        var extension = Path.GetExtension(target);
        string program, arguments;
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) (program, arguments) = (s.Target, s.Arguments);
        else if (extension.Equals(".msc", StringComparison.OrdinalIgnoreCase))
            (program, arguments) = (@"%SystemRoot%\system32\mmc.exe", ("\"" + s.Target + "\" " + s.Arguments).Trim());
        else return null;
        if (p.DesktopAppsAllowed is { } allowed)
        {
            var list = allowed.Select(a => AppIds.Normalize(Environment.ExpandEnvironmentVariables(a))).ToHashSet(StringComparer.Ordinal);
            if (!list.Contains(AppIds.Normalize(target)) && !list.Contains(AppIds.Normalize(expand(program)))) return null;
        }
        return new DesktopListedApp(name, s.Group, program, arguments, s.WorkingFolder, s.RunAsAdministrator && p.DesktopAppsElevated, null);
    }

    /// <summary>The common Start menu, and the account's own when their profile is on this device (not redirected elsewhere).</summary>
    private static IEnumerable<string> Roots(string? accountSid)
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs");
        if (accountSid is null || accountSid.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) yield break;
        string? profile = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + accountSid);
            profile = key?.GetValue("ProfileImagePath") as string;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (profile is null) yield break;
        profile = Environment.ExpandEnvironmentVariables(profile);
        if (Path.IsPathFullyQualified(profile) && !profile.StartsWith(@"\\", StringComparison.Ordinal))
            yield return Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs");
    }

    // ---- reading shortcuts (IShellLinkW, on an STA thread of its own)

    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid UnknownIid = new("00000000-0000-0000-C000-000000000046");

    private static IReadOnlyList<Shortcut?> ReadAll(IReadOnlyList<(string File, string Group)> files)
    {
        var read = new List<Shortcut?>(files.Count);
        var t = new Thread(() =>
        {
            foreach (var (file, group) in files)
            {
                try { read.Add(Read(file, group)); }
                catch (Exception e) when (e is COMException or InvalidCastException or IOException or ArgumentException) { read.Add(null); }
            }
        }) { IsBackground = true, Name = "Start menu" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return read;
    }

    /// <summary>
    /// The icons of <paramref name="files"/> (programs and consoles on this device, already checked), 32×32 as the shell
    /// shows them (SHGetFileInfo on the file itself, never the shortcut, so no link is followed); null where there is none.
    /// </summary>
    private static IReadOnlyList<byte[]?> IconsOf(IReadOnlyList<string> files)
    {
        var icons = new List<byte[]?>(files.Count);
        var t = new Thread(() =>
        {
            foreach (var file in files)
            {
                var info = new SHFILEINFOW();
                byte[]? png = null;
                if (SHGetFileInfoW(file, 0, ref info, (uint)sizeof(SHFILEINFOW), 0x100 /* SHGFI_ICON, large */) != 0 && info.hIcon != 0)
                {
                    try { png = IconPng.FromIcon(info.hIcon); }
                    catch (ArgumentException) { }
                    finally { DestroyIcon(info.hIcon); }
                }
                icons.Add(png);
            }
        }) { IsBackground = true, Name = "Start menu icons" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return icons;
    }

    private static Shortcut? Read(string file, string group)
    {
        if (CoCreateInstance(in ShellLinkClass, 0, 1 /* CLSCTX_INPROC_SERVER */, in UnknownIid, out var unknown) != 0) return null;
        try
        {
            var link = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            fixed (char* path = file)
                if (((IPersistFile)link).Load(path, 0 /* STGM_READ */) != 0) return null;
            var shell = (IShellLinkW)link;
            var buffer = new char[MaxArguments + 1];
            string Text(Func<IntPtr, int, int> get)
            {
                Array.Clear(buffer);
                fixed (char* b = buffer)
                    if (get((IntPtr)b, buffer.Length) != 0) return "";
                var end = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, end < 0 ? buffer.Length : end);
            }
            var target = Text((b, n) => shell.GetPath((char*)b, n, 0, 0x4 /* SLGP_RAWPATH: as written, variables kept */));
            var arguments = Text((b, n) => shell.GetArguments((char*)b, n));
            var folder = Text((b, n) => shell.GetWorkingDirectory((char*)b, n));
            uint flags = 0;
            var runAs = ((IShellLinkDataList)link).GetFlags(&flags) == 0 && (flags & 0x2000 /* SLDF_RUNAS_USER */) != 0;
            return new Shortcut(DisplayName(file), group, target, arguments, folder, runAs);
        }
        finally { Marshal.Release(unknown); }
    }

    /// <summary>The name the device shows for the shortcut ("Event Viewer", "Ereignisanzeige"), else its file name.</summary>
    private static string DisplayName(string file)
    {
        var info = new SHFILEINFOW();
        return SHGetFileInfoW(file, 0, ref info, (uint)sizeof(SHFILEINFOW), 0x200 /* SHGFI_DISPLAYNAME */) != 0 && new string(info.szDisplayName) is { Length: > 0 } shown
            ? shown
            : Path.GetFileNameWithoutExtension(file);
    }

    [GeneratedComInterface, Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        [PreserveSig] int GetPath(char* file, int size, nint findData, uint flags);
        [PreserveSig] int GetIDList(nint* list);
        [PreserveSig] int SetIDList(nint list);
        [PreserveSig] int GetDescription(char* name, int size);
        [PreserveSig] int SetDescription(char* name);
        [PreserveSig] int GetWorkingDirectory(char* folder, int size);
        [PreserveSig] int SetWorkingDirectory(char* folder);
        [PreserveSig] int GetArguments(char* arguments, int size);
    }

    [GeneratedComInterface, Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        [PreserveSig] int GetClassID(Guid* id);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(char* file, uint mode);
    }

    [GeneratedComInterface, Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1")]
    internal partial interface IShellLinkDataList
    {
        [PreserveSig] int AddDataBlock(nint block);
        [PreserveSig] int CopyDataBlock(uint signature, nint* block);
        [PreserveSig] int RemoveDataBlock(uint signature);
        [PreserveSig] int GetFlags(uint* flags);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        public fixed char szDisplayName[260];
        public fixed char szTypeName[80];
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);
}
