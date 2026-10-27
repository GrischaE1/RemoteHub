using System.Windows;
using RemoteHub.Remoting.Shares;
using RemoteHub.Services;

namespace RemoteHub.Views;

/// <summary>
/// Picking a private key file for an SSH or SFTP account and saying what a stored one is – the same in the connection
/// editor and in saved credentials (docs/network-shares.md §6.2).
/// </summary>
internal static class PrivateKeyPicker
{
    /// <summary>
    /// Asks for a key file and reads it (checked: a public key or another file is refused here, not when connecting).
    /// Null when cancelled or refused – the reason was shown.
    /// </summary>
    public static async Task<Secret?> Pick(Window owner, string topic)
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var pick = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a private key",
            Filter = "Private keys (id_*, *.pem, *.key, *.ppk)|*.*|PuTTY keys (*.ppk)|*.ppk",
            InitialDirectory = Directory.Exists(ssh) ? ssh : "",
        };
        if (pick.ShowDialog(owner) != true) return null;
        try
        {
            var key = SshSignIn.ReadKeyFile(pick.FileName);
            if (!SshSignIn.NeedsPassphrase(key, null)) SshSignIn.Describe(key, null);
            DiagLog.Write("Private key imported");
            return key;
        }
        catch (Exception ex) when (ex is SshKeyException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.Info(owner, topic, ex is SshKeyException ? ex.Message : $"The file can't be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>What the key is, never the key: its type and fingerprint, to compare with the public key on the server.</summary>
    public static string Describe(Secret? key, Secret? passphrase)
    {
        if (key is null) return "No key – signs in with the password.";
        try { return SshSignIn.Describe(key, passphrase).Summary; }
        catch (SshKeyException ex) when (ex.NeedsPassphrase) { return "Passphrase-protected key – its fingerprint shows once the passphrase is entered."; }
        catch (SshKeyException ex) { return ex.Message; }
    }
}
