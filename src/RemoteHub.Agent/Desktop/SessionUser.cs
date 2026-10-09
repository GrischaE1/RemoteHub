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

    /// <summary>A new folder for copied files in the user's temp folder (old ones, from before today, are removed). Call inside <see cref="Run"/>.</summary>
    public static string NewClipboardFolder()
    {
        string temp;
        using (var token = Token())
            temp = token is not null && Profile(token) is { } profile ? Path.Combine(profile, "AppData", "Local", "Temp") : Path.GetTempPath();
        var root = Path.Combine(temp, "RemoteHub Clipboard");
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
}
