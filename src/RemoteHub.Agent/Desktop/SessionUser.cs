using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Agent;

/// <summary>
/// The user signed in to the helper's session. Copied files are read and written with their rights (impersonation),
/// never with the helper's LocalSystem rights. Not running as LocalSystem (tests), the current account is used.
/// </summary>
internal static class SessionUser
{
    public static T Run<T>(Func<T> work)
    {
        using var token = Token();
        if (token is null)
        {
            if (WindowsIdentity.GetCurrent().IsSystem) throw new InvalidOperationException("Nobody is signed in on the device.");
            return work();
        }
        return WindowsIdentity.RunImpersonated(token, work);
    }

    /// <summary>
    /// Where copied files go: "RemoteHub Clipboard" in the signed-in user's temp folder. Call it as LocalSystem, before
    /// <see cref="Run"/> – while impersonating, the user's token can't be looked up and Windows' own temp folder was used.
    /// </summary>
    public static string ClipboardRoot()
    {
        using var token = Token();
        var temp = token is not null && Profile(token) is { } profile ? Path.Combine(profile, "AppData", "Local", "Temp") : Path.GetTempPath();
        return Path.Combine(temp, "RemoteHub Clipboard");
    }

    /// <summary>A new folder for copied files below <paramref name="root"/> (old ones, from before today, are removed). Call inside <see cref="Run"/>.</summary>
    public static string NewClipboardFolder(string root)
    {
        try
        {
            if (Directory.Exists(root))
                foreach (var old in Directory.GetDirectories(root))
                    if (Directory.GetCreationTime(old) < DateTime.Today)
                        try { Directory.Delete(old, true); } catch { /* in use */ }
        }
        catch { /* cleaning is optional */ }
        return Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
    }

    /// <summary>Tests: the folder that stands for the user's Desktop, so nothing lands on the real one.</summary>
    internal static string? DesktopForTests { get; set; }

    /// <summary>
    /// The signed-in user's Desktop (also when it is redirected, e.g. to OneDrive). Call it as LocalSystem, before
    /// <see cref="Run"/>, like <see cref="ClipboardRoot"/>. Not running as LocalSystem (tests): the current account's.
    /// </summary>
    public static string DesktopFolder()
    {
        if (DesktopForTests is { } forTests) return forTests;
        using var token = Token();
        if (token is null) return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var id = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"); // FOLDERID_Desktop
        if (SHGetKnownFolderPath(ref id, 0, token.DangerousGetHandle(), out var path) != 0) throw new IOException("The signed-in user's Desktop can't be found.");
        try { return Marshal.PtrToStringUni(path)!; }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    /// <summary>
    /// Where dropped files go: <see cref="DesktopProtocol.DropFolder"/> on <paramref name="desktop"/> – or, when something there
    /// already has the name of a dropped file or folder, a new folder in it named after <paramref name="now"/>, so nothing
    /// is overwritten. Call inside <see cref="Run"/>: the folder belongs to the user, as their own would.
    /// </summary>
    public static string NewDropFolder(string desktop, IEnumerable<string> topNames, DateTime now)
    {
        var folder = Path.Combine(desktop, DesktopProtocol.DropFolder);
        if (Directory.Exists(folder) && topNames.Any(n => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n))))
            folder = Path.Combine(folder, now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture));
        return folder;
    }

    /// <summary>The SID of the user signed in to this session (as LocalSystem), or null when nobody is.</summary>
    public static string? Sid()
    {
        using var token = Token();
        if (token is null) return null;
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        return identity.User?.Value;
    }

    /// <summary>The signed-in user's token (as LocalSystem; the caller disposes it), or null when nobody is signed in or not LocalSystem.</summary>
    internal static SafeAccessTokenHandle? UserToken() => Token();

    private static SafeAccessTokenHandle? Token()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) return null;
        return WTSQueryUserToken(session, out var token) ? new SafeAccessTokenHandle(token) : null;
    }

    private static unsafe string? Profile(SafeAccessTokenHandle token)
    {
        var size = 260u;
        var buffer = stackalloc char[(int)size];
        return GetUserProfileDirectoryW(token.DangerousGetHandle(), buffer, ref size) ? new string(buffer) : null;
    }

    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern unsafe bool GetUserProfileDirectoryW(IntPtr token, char* dir, ref uint size);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
