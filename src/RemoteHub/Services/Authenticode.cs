using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RemoteHub.Services;

/// <summary>
/// Authenticode checks for the update installer: the signature must chain to a root this PC trusts (WinVerifyTrust, no
/// revocation check – the PC may be offline) and the signer must be the publisher that signed the running RemoteHub.
/// The publisher is compared by the certificate's subject, not its thumbprint, so a renewed certificate still matches.
/// </summary>
internal static class Authenticode
{
    /// <summary>The subject of the certificate that signed the running RemoteHub.exe; null for an unsigned build (settable for tests).</summary>
    internal static string? RunningPublisher { get; set; } = Environment.ProcessPath is { } exe ? SignerSubject(exe) : null;

    /// <summary>The subject of the certificate that signed <paramref name="file"/> (embedded signature), or null if it isn't signed.</summary>
    public static string? SignerSubject(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the signer of a signed file; X509CertificateLoader has no equivalent
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Signed, the signature matches the file, and it chains to a root this PC trusts.</summary>
    public static bool Trusted(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, info, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = info,
                dwProvFlags = 0x00000080,  // WTD_REVOCATION_CHECK_NONE
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(info);
            Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>
    /// Why <paramref name="file"/> must not be installed, or null when it may: signed by <paramref name="publisher"/>
    /// with a signature this PC trusts. A null <paramref name="publisher"/> (the running build isn't signed – a test
    /// release or a local build) can't name a publisher, so only the checksum protects the download then.
    /// </summary>
    public static string? Refusal(string file, string? publisher)
    {
        if (publisher is null) return null;
        if (SignerSubject(file) is not { } signer) return "isn't signed";
        if (!Trusted(file)) return "has a signature that doesn't match the file or that this PC doesn't trust";
        if (!string.Equals(signer, publisher, StringComparison.Ordinal)) return $"is signed by \"{signer}\", not by RemoteHub's publisher";
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
