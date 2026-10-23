using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteHub.Remoting.Decoder;

/// <summary>How a decoder process was confined.</summary>
public enum DecoderConfinement { AppContainer, LowIntegrity }

/// <summary>The decoder can't be started in any sandbox: RemoteHub doesn't decode the device's data then (§9, decision 2).</summary>
public sealed class DecoderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Starts <c>RemoteHubDecoder.exe</c> confined (docs/decoder-isolation-design.md §3.2): in an AppContainer with no
/// capabilities – no network, no access to the user's files, registry writes or the clipboard – or, where that can't be
/// made, with a restricted token at low integrity. Either way inside a job (ended when RemoteHub closes it, no child
/// processes, 1 GB, all UI limits, no error dialog) and inheriting exactly the handles given: nothing else of
/// RemoteHub's. Its command line carries only those handle values.
/// </summary>
internal static class DecoderSandbox
{
    private const string ContainerName = "RemoteHub.Decoder";
    private const int MemoryLimit = 1 << 30;

    /// <summary>Tests: start at low integrity, as when no AppContainer can be made.</summary>
    internal static bool ForceLowIntegrity { get; set; }

    private static IntPtr _containerSid;
    private static readonly object Gate = new();

    /// <summary>The started process (suspended until it is in the job, then running) and its job.</summary>
    public sealed record Started(SafeProcessHandle Process, SafeFileHandle Job, int ProcessId, DecoderConfinement Confinement);

    public static Started Start(string exe, string arguments, IntPtr[] inherit)
    {
        var job = NewJob();
        try
        {
            Exception? first = null;
            if (!ForceLowIntegrity)
            {
                try { return Launch(exe, arguments, inherit, job, ContainerSid(), DecoderConfinement.AppContainer); }
                catch (Exception ex) when (ex is Win32Exception or COMException or UnauthorizedAccessException) { first = ex; }
            }
            try { return Launch(exe, arguments, inherit, job, IntPtr.Zero, DecoderConfinement.LowIntegrity); }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                throw new DecoderUnavailableException(
                    $"The decoder can't be started in a sandbox ({(first is null ? "" : first.Message + "; ")}{ex.Message}).", ex);
            }
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>The AppContainer's SID, made once per Windows user (the profile stays for later starts).</summary>
    private static IntPtr ContainerSid()
    {
        lock (Gate)
        {
            if (_containerSid != IntPtr.Zero) return _containerSid;
            var hr = Native.CreateAppContainerProfile(ContainerName, "RemoteHub decoder",
                "Decodes a remote device's picture and clipboard images for RemoteHub, without access to anything else.", IntPtr.Zero, 0, out var sid);
            if (hr == unchecked((int)0x800700B7)) hr = Native.DeriveAppContainerSidFromAppContainerName(ContainerName, out sid); // already there
            Marshal.ThrowExceptionForHR(hr);
            return _containerSid = sid;
        }
    }

    private static SafeFileHandle NewJob()
    {
        var job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS | PROCESS_MEMORY | DIE_ON_UNHANDLED_EXCEPTION
        limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x8 | 0x100 | 0x400;
        limits.BasicLimitInformation.ActiveProcessLimit = 1;
        limits.ProcessMemoryLimit = (UIntPtr)MemoryLimit;
        if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(limits))) throw new Win32Exception();
        // handles, read and write clipboard, system parameters, display settings, global atoms, desktop, exit Windows
        var ui = 0xFFu;
        if (!Native.SetInformationJobObject(job, 4, ref ui, 4)) throw new Win32Exception();
        return job;
    }

    private static Started Launch(string exe, string arguments, IntPtr[] inherit, SafeFileHandle job, IntPtr containerSid, DecoderConfinement confinement)
    {
        if (confinement == DecoderConfinement.AppContainer) EnsureReadable(Path.GetDirectoryName(exe)!);
        var attributes = containerSid == IntPtr.Zero ? 1 : 2;
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributes, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        var caps = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SECURITY_CAPABILITIES>());
        var handles = Marshal.AllocHGlobal(IntPtr.Size * inherit.Length);
        var token = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(list, attributes, 0, ref size)) throw new Win32Exception();
            initialized = true;
            for (var i = 0; i < inherit.Length; i++) Marshal.WriteIntPtr(handles, i * IntPtr.Size, inherit[i]);
            if (!Native.UpdateProcThreadAttribute(list, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, IntPtr.Size * inherit.Length, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            if (containerSid != IntPtr.Zero)
            {
                Marshal.StructureToPtr(new Native.SECURITY_CAPABILITIES { AppContainerSid = containerSid }, caps, false);
                if (!Native.UpdateProcThreadAttribute(list, 0, Native.PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, caps,
                        Marshal.SizeOf<Native.SECURITY_CAPABILITIES>(), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
            }
            var si = new Native.STARTUPINFOEX { StartupInfo = new() { cb = Marshal.SizeOf<Native.STARTUPINFOEX>() }, lpAttributeList = list };
            const uint flags = 0x80000 | 0x4 | 0x08000000; // EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW
            var commandLine = new StringBuilder($"\"{exe}\" {arguments}"); // CreateProcess may write to it: a buffer
            Native.PROCESS_INFORMATION pi;
            if (containerSid != IntPtr.Zero)
            {
                if (!Native.CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
                    throw new Win32Exception();
            }
            else
            {
                token = LowIntegrityToken();
                if (!Native.CreateProcessAsUser(token, exe, commandLine, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out pi))
                    throw new Win32Exception();
            }
            var process = new SafeProcessHandle(pi.hProcess, true);
            try
            {
                if (!Native.AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception();
                Native.ResumeThread(pi.hThread);
            }
            catch
            {
                Native.TerminateProcess(pi.hProcess, 1);
                process.Dispose();
                throw;
            }
            finally { Native.CloseHandle(pi.hThread); }
            return new Started(process, job, pi.dwProcessId, confinement);
        }
        finally
        {
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            Marshal.FreeHGlobal(caps);
            Marshal.FreeHGlobal(handles);
            if (token != IntPtr.Zero) Native.CloseHandle(token);
        }
    }

    /// <summary>
    /// RemoteHub's own token without its privileges and groups for administrators, at low integrity (§9, decision 2):
    /// weaker than an AppContainer – it can still read most files – but it can't write the user's files, the registry
    /// outside the low-integrity key, or reach RemoteHub's process.
    /// </summary>
    private static IntPtr LowIntegrityToken()
    {
        // TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x2 | 0x1 | 0x8 | 0x80, out var own)) throw new Win32Exception();
        try
        {
            // DISABLE_MAX_PRIVILEGE | LUA_TOKEN
            if (!Native.CreateRestrictedToken(own, 0x1 | 0x4, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted)) throw new Win32Exception();
            var low = new SecurityIdentifier("S-1-16-4096");
            var sid = new byte[low.BinaryLength];
            low.GetBinaryForm(sid, 0);
            var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
            try
            {
                var label = new Native.TOKEN_MANDATORY_LABEL { Sid = pinned.AddrOfPinnedObject(), Attributes = 0x20 /* SE_GROUP_INTEGRITY */ };
                if (!Native.SetTokenInformation(restricted, 25 /* TokenIntegrityLevel */, ref label, Marshal.SizeOf(label) + sid.Length))
                {
                    var e = new Win32Exception();
                    Native.CloseHandle(restricted);
                    throw e;
                }
            }
            finally { pinned.Free(); }
            return restricted;
        }
        finally { Native.CloseHandle(own); }
    }

    /// <summary>
    /// An AppContainer reads only what all application packages may read – Program Files does that. A copy elsewhere
    /// (a build folder, the tests) gets that same read access, for its own folder only; a folder that can't be changed
    /// is left as it is.
    /// </summary>
    private static void EnsureReadable(string folder)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (folder.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var info = new DirectoryInfo(folder);
            var acl = info.GetAccessControl();
            var allPackages = new SecurityIdentifier("S-1-15-2-1");
            if (acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                   .Any(r => r.IdentityReference == allPackages && r.AccessControlType == AccessControlType.Allow
                             && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute))
                return;
            acl.AddAccessRule(new FileSystemAccessRule(allPackages, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { /* then the low-integrity start */ }
    }

    private static class Native
    {
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES = 0x20009;

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        public static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr caps, int count, out IntPtr sid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        public static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern SafeFileHandle CreateJobObject(IntPtr sa, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(SafeFileHandle job, int cls, ref uint info, int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir,
            ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(IntPtr token, string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env,
            string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CreateRestrictedToken(IntPtr token, uint flags, int disableCount, IntPtr disable, int deleteCount, IntPtr delete,
            int restrictCount, IntPtr restrict, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] public struct IO_COUNTERS { public ulong Reads, Writes, Others, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SECURITY_CAPABILITIES { public IntPtr AppContainerSid, Capabilities; public int CapabilityCount, Reserved; }
        [StructLayout(LayoutKind.Sequential)] public struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)] public struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    }
}
