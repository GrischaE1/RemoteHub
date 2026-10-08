using System.Runtime.InteropServices;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace RemoteHub.Services;

/// <summary>
/// Windows Hello (PIN, fingerprint, face) through a Hello key pair of this user on this PC (KeyCredentialManager).
/// The private key never leaves Windows (TPM where available); RemoteHub only gets signatures.
/// </summary>
public static class WindowsHello
{
    public static async Task<bool> IsAvailableAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>Creates (or replaces) the Hello key and signs <paramref name="challenge"/> with it.</summary>
    public static async Task<(byte[]? Signature, string? Error)> CreateAndSignAsync(string name, byte[] challenge)
    {
        try
        {
            if (!await IsAvailableAsync()) return (null, "Windows Hello isn't set up on this PC (Settings → Accounts → Sign-in options).");
            BringPromptToFront();
            var r = await KeyCredentialManager.RequestCreateAsync(name, KeyCredentialCreationOption.ReplaceExisting);
            if (r.Status != KeyCredentialStatus.Success) return (null, Explain(r.Status));
            return await Sign(r.Credential, challenge);
        }
        catch (Exception ex) { return (null, "Windows Hello failed: " + ex.Message); }
    }

    /// <summary>Signs <paramref name="challenge"/> with the existing Hello key (Windows asks for PIN, fingerprint or face).</summary>
    public static async Task<(byte[]? Signature, string? Error)> SignAsync(string name, byte[] challenge)
    {
        try
        {
            if (!await IsAvailableAsync()) return (null, "Windows Hello isn't available right now – use the password.");
            var r = await KeyCredentialManager.OpenAsync(name);
            if (r.Status != KeyCredentialStatus.Success) return (null, Explain(r.Status));
            return await Sign(r.Credential, challenge);
        }
        catch (Exception ex) { return (null, "Windows Hello failed: " + ex.Message); }
    }

    public static async Task DeleteAsync(string name)
    {
        try { await KeyCredentialManager.DeleteAsync(name); } catch { /* already gone */ }
    }

    private static async Task<(byte[]?, string?)> Sign(KeyCredential credential, byte[] challenge)
    {
        BringPromptToFront();
        var s = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (s.Status != KeyCredentialStatus.Success) return (null, Explain(s.Status));
        CryptographicBuffer.CopyToByteArray(s.Result, out var sig);
        return (sig, null);
    }

    private static string Explain(KeyCredentialStatus s) => s switch
    {
        KeyCredentialStatus.UserCanceled => "Windows Hello was cancelled.",
        KeyCredentialStatus.NotFound => "The Windows Hello key of RemoteHub wasn't found on this PC – unlock with the password and set up Windows Hello again.",
        KeyCredentialStatus.UserPrefersPassword => "Use the password.",
        KeyCredentialStatus.SecurityDeviceLocked => "Windows Hello is locked (too many attempts) – use the password.",
        _ => $"Windows Hello didn't work ({s}).",
    };

    // The Hello prompt of a desktop app can open behind the app window. Bring it to the front while it appears.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static void BringPromptToFront() => Task.Run(async () =>
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            var h = FindWindow("Credential Dialog Xaml Host", null);
            if (h != IntPtr.Zero) { SetForegroundWindow(h); return; }
        }
    });
}
