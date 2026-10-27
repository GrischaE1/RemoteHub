using System.Security.Cryptography;
using RemoteHub.Models;
using RemoteHub.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteHub.Remoting.Shares;

/// <summary>What an imported private key is, for people: "ssh-ed25519 · SHA256:…" (never the key itself).</summary>
public sealed record SshKeyInfo(string Type, int Bits, string Fingerprint, bool Encrypted)
{
    public string Summary => $"{Type}{(Bits > 0 ? $", {Bits} bits" : "")} · {Fingerprint}{(Encrypted ? " · passphrase-protected" : "")}";
}

/// <summary>A private key that can't be used: not a key, or its passphrase is missing or wrong. The message is for people.</summary>
public sealed class SshKeyException(string message, bool needsPassphrase = false) : Exception(message)
{
    /// <summary>The key is protected and no passphrase was given.</summary>
    public bool NeedsPassphrase { get; } = needsPassphrase;
}

/// <summary>
/// Signing in over SSH – the terminal and SFTP (docs/network-shares.md §6.2): with the account's private key first when
/// it has one, then its password (also through keyboard-interactive prompts). The key's text and passphrase are
/// decrypted only while SSH.NET reads the key; the parsed key lives as long as the connection.
/// </summary>
public static class SshSignIn
{
    /// <summary>A key file is a few KB (RSA 16384 in PuTTY format stays under 20 KB); anything larger isn't one.</summary>
    public const int MaxKeyFileBytes = 64 * 1024;

    internal const string NotAKey = "This isn't a private key RemoteHub can read – use an OpenSSH, PEM or PuTTY (.ppk) private key file, not the .pub file.";
    internal const string WrongPassphrase = "The passphrase is wrong (or the file isn't a private key RemoteHub can read).";
    internal const string PassphraseNeeded = "The key is protected by a passphrase – enter it.";

    /// <summary>Reads a key file the user picked into a <see cref="Secret"/>; the file's bytes are wiped afterwards.</summary>
    /// <exception cref="SshKeyException">The file is too large, empty or not text.</exception>
    public static Secret ReadKeyFile(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is 0 or > MaxKeyFileBytes) throw new SshKeyException(NotAKey);
        var bytes = File.ReadAllBytes(path);
        try
        {
            // Key files are ASCII text; a NUL means a binary file (a .pfx, a .ppk.zip …).
            if (Array.IndexOf(bytes, (byte)0) >= 0) throw new SshKeyException(NotAKey);
            return Secret.FromUtf8(bytes) ?? throw new SshKeyException(NotAKey);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Checks the key (and passphrase) and says what it is.</summary>
    /// <exception cref="SshKeyException">Not a key, or the passphrase is missing or wrong.</exception>
    public static SshKeyInfo Describe(Secret key, Secret? passphrase)
    {
        using var file = Open(key, passphrase);
        return Info(file, encrypted: passphrase is not null && NeedsPassphrase(key, null));
    }

    /// <summary>Whether the key needs a passphrase that wasn't given (asked when connecting, then).</summary>
    public static bool NeedsPassphrase(Secret key, Secret? passphrase)
    {
        if (passphrase is not null) return false;
        try { using var _ = Open(key, null); return false; }
        catch (SshKeyException ex) { return ex.NeedsPassphrase; }
    }

    internal static PrivateKeyFile Open(Secret key, Secret? passphrase)
    {
        try
        {
            return key.UseUtf8((bytes, length) =>
            {
                using var stream = new MemoryStream(bytes, 0, length, writable: false);
                // SSH.NET takes the passphrase only as a string (Secret.Reveal is for exactly that).
                return passphrase is null ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase.Reveal());
            });
        }
        catch (SshPassPhraseNullOrEmptyException) { throw new SshKeyException(PassphraseNeeded, needsPassphrase: true); }
        // The parsers throw many kinds for a damaged file or a wrong passphrase; none of their messages is shown – they
        // could quote the file.
        catch (Exception ex) when (ex is not (OutOfMemoryException or SshKeyException))
        {
            throw new SshKeyException(passphrase is null ? NotAKey : WrongPassphrase);
        }
    }

    private static SshKeyInfo Info(PrivateKeyFile file, bool encrypted)
    {
        var algorithm = file.HostKeyAlgorithms.First();
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(algorithm.Data)).TrimEnd('=');
        return new SshKeyInfo(file.Key.ToString() ?? algorithm.Name, file.Key.KeyLength, fingerprint, encrypted);
    }

    /// <summary>A Windows-style "DOMAIN\user" for Windows OpenSSH; just the user elsewhere.</summary>
    internal static string UserName(CredentialSet c)
    {
        var user = c.Username!.Trim();
        return user.Contains('\\') || user.Contains('@') || string.IsNullOrEmpty(c.Domain) || c.IsLocalAccount ? user : $"{c.Domain}\\{user}";
    }

    /// <summary>
    /// The connection settings for <paramref name="credentials"/>: the key (when set), the password and keyboard-interactive.
    /// The returned key must be disposed with the connection.
    /// </summary>
    /// <exception cref="SshKeyException">The key can't be used (the caller asked for a missing passphrase before).</exception>
    internal static (ConnectionInfo Info, PrivateKeyFile? Key) Connection(string host, int port, CredentialSet credentials, TimeSpan timeout)
    {
        var user = UserName(credentials);
        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? key = null;
        if (credentials.PrivateKey is { } k)
        {
            key = Open(k, credentials.KeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        // With a key and no password, nothing else is offered: an empty password would only count as a failed attempt.
        if (key is null || credentials.Password is not null)
        {
            var password = credentials.Password?.Reveal() ?? "";
            var keyboard = new KeyboardInteractiveAuthenticationMethod(user);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) if (p.Request.Contains("password", StringComparison.OrdinalIgnoreCase)) p.Response = password;
            };
            methods.Add(new PasswordAuthenticationMethod(user, password));
            methods.Add(keyboard);
        }
        return (new ConnectionInfo(host, port, user, [.. methods]) { Timeout = timeout }, key);
    }

    /// <summary>
    /// Connects a client made by <paramref name="make"/> once its host key is the accepted one (<paramref name="trusted"/>)
    /// or the user accepts it (<paramref name="trust"/>, then <paramref name="accepted"/> stores it) – nothing is sent
    /// before that. A client that didn't connect is disposed.
    /// </summary>
    /// <exception cref="ShareException">The key wasn't accepted.</exception>
    internal static T ConnectTrusted<T>(Func<T> make, string host, string? trusted, Func<TrustRequest, bool> trust, Action<string> accepted) where T : BaseClient
    {
        var refused = false;
        string? acceptedNow = null;
        T Connect(string? known)
        {
            var client = make();
            client.HostKeyReceived += (_, e) =>
            {
                var fp = "SHA256:" + e.FingerPrintSHA256;
                if (fp == known) { e.CanTrust = true; return; }
                e.CanTrust = trust(new TrustRequest(host, trusted is null ? "SSH host key" : "SSH host key (changed!)", fp, $"{e.HostKeyName}, {e.KeyLength} bits"));
                if (e.CanTrust) { acceptedNow = fp; accepted(fp); }
                else refused = true;
            };
            try { client.Connect(); return client; }
            catch { client.Dispose(); throw; }
        }
        try
        {
            try { return Connect(trusted); }
            // The question waits for the user while SSH.NET's connection timeout runs, so an answer after it ends this
            // attempt ("Session operation has timed out"). The key is accepted by then: the second attempt doesn't ask.
            catch (SshOperationTimeoutException) when (acceptedNow is not null) { return Connect(acceptedNow); }
        }
        catch (Exception ex) when (refused && ex is SshConnectionException or SshOperationTimeoutException)
        {
            throw new ShareException("The server's key wasn't accepted – nothing was sent.");
        }
    }

    /// <summary>The message for a refused sign-in: what was tried.</summary>
    internal static string Refused(CredentialSet credentials) => credentials.PrivateKey is null
        ? "The user name or password is incorrect (or the server doesn't allow password sign-in)."
        : credentials.Password is null
            ? "The server didn't accept the private key for this user (is its public key in the user's authorized_keys?)."
            : "The server accepted neither the private key nor the password for this user.";
}
