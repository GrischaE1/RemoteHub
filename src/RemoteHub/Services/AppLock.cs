using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteHub.Services;

/// <summary>The settings on this PC are protected by the startup password and can't be read before unlocking.</summary>
public sealed class AppLockedException() : Exception("RemoteHub is locked – unlock it with the startup password or Windows Hello first.");

/// <summary>
/// Optional startup protection. A random settings key (256 bit) encrypts what RemoteHub stores on this PC – the
/// settings file and the remembered password key of a shared settings file – inside the existing Windows (DPAPI)
/// encryption. The settings key itself is stored wrapped:
/// <list type="bullet">
/// <item>with the startup password (PBKDF2-SHA256, 600,000 iterations, AES-256-GCM), always;</item>
/// <item>optionally with Windows Hello: a Hello key of this PC (kept in the TPM, unlocked by PIN, fingerprint or face)
/// signs a fixed random challenge; the SHA-256 of the signature wraps the settings key. Without Hello nobody can
/// produce that signature.</item>
/// </list>
/// Without the password or Hello the settings can't be opened – not even by RemoteHub. Only this PC's lock file and
/// files are affected; a shared settings file keeps its own password.
/// </summary>
public static class AppLock
{
    private const int Iterations = 600_000;
    private static byte[]? _key;

    private sealed class Wrapped
    {
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
    }

    private sealed class HelloWrapped
    {
        public string Credential { get; set; } = "";
        public string Challenge { get; set; } = "";
        public Wrapped Key { get; set; } = new();
    }

    private sealed class LockFile
    {
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; } = AppLock.Iterations;
        public string Salt { get; set; } = "";
        public Wrapped Password { get; set; } = new();
        public HelloWrapped? Hello { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public static string FilePath => Path.Combine(SettingsStore.Directory, "lock.json");

    public static bool IsEnabled => File.Exists(FilePath);
    public static bool IsUnlocked => _key is not null;
    public static bool HasHello => Read()?.Hello is not null;

    public static string Describe() => !IsEnabled ? "Off" : HasHello ? "Startup password and Windows Hello" : "Startup password";

    private static LockFile? Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<LockFile>(File.ReadAllBytes(FilePath)) : null; }
        catch { return null; }
    }

    private static void Write(LockFile f)
    {
        System.IO.Directory.CreateDirectory(SettingsStore.Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(f, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // ------------------------------------------------------------------ unlock

    public static bool TryUnlock(string password)
    {
        if (Read() is not { } f) return false;
        var kek = Derive(password, Convert.FromBase64String(f.Salt), f.Iterations);
        try { return Unwrap(f.Password, kek) is { } k && Set(k); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Asks Windows Hello (PIN, fingerprint, face). Returns null on success, else why it didn't work.</summary>
    public static async Task<string?> TryUnlockWithHello()
    {
        if (Read() is not { Hello: { } h }) return "Windows Hello isn't set up for RemoteHub on this PC.";
        var (sig, error) = await WindowsHello.SignAsync(h.Credential, Convert.FromBase64String(h.Challenge));
        if (sig is null) return error;
        var kek = SHA256.HashData(sig);
        try
        {
            return Unwrap(h.Key, kek) is { } k && Set(k)
                ? null
                : "Windows Hello answered, but the key didn't match (the Hello key was renewed?). Unlock with the password, then set up Windows Hello again.";
        }
        finally { CryptographicOperations.ZeroMemory(kek); CryptographicOperations.ZeroMemory(sig); }
    }

    private static bool Set(byte[] key) { _key = key; return true; }

    /// <summary>Checks the password without changing anything (for changing or turning off the protection).</summary>
    public static bool VerifyPassword(string password)
    {
        if (Read() is not { } f) return false;
        var kek = Derive(password, Convert.FromBase64String(f.Salt), f.Iterations);
        try { return Unwrap(f.Password, kek) is { } k && k.AsSpan().SequenceEqual(_key ?? k); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    // ------------------------------------------------------------------ set up / change / turn off (call via SettingsStore.Reprotect)

    /// <summary>Turns the protection on with a new settings key. Only changes the lock file and the in-memory key.</summary>
    internal static void Enable(string password)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(password, salt, Iterations);
        try
        {
            Write(new LockFile { Salt = Convert.ToBase64String(salt), Password = Wrap(key, kek), CreatedUtc = DateTime.UtcNow });
            _key = key;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Removes the lock file (and the Windows Hello key) and forgets the settings key.</summary>
    internal static async Task Disable()
    {
        if (Read()?.Hello is { } h) await WindowsHello.DeleteAsync(h.Credential);
        File.Delete(FilePath);
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    /// <summary>
    /// A new password with a new settings key – so an old copy of lock.json with the old password opens nothing any more.
    /// Call via <see cref="SettingsStore.Reprotect"/> (it stores everything again with the new key). Windows Hello wrapped
    /// the old key: its key is removed and has to be set up again (<see cref="AddHello"/>).
    /// </summary>
    internal static async Task ChangePassword(string newPassword)
    {
        if (Read() is not { } f || _key is null) throw new InvalidOperationException("The protection isn't on or not unlocked.");
        if (f.Hello is { } h) await WindowsHello.DeleteAsync(h.Credential);
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = await Task.Run(() => Derive(newPassword, salt, Iterations));
        try
        {
            Write(new LockFile { Salt = Convert.ToBase64String(salt), Password = Wrap(key, kek), CreatedUtc = f.CreatedUtc });
            var old = _key;
            _key = key;
            CryptographicOperations.ZeroMemory(old);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Creates a Windows Hello key for RemoteHub and lets it unlock the settings key. Returns null or the error.</summary>
    public static async Task<string?> AddHello()
    {
        if (Read() is not { } f || _key is null) return "Turn on the startup password first.";
        var name = HelloCredentialName();
        var challenge = RandomNumberGenerator.GetBytes(32);
        var (sig, error) = await WindowsHello.CreateAndSignAsync(name, challenge);
        if (sig is null) return error;
        var kek = SHA256.HashData(sig);
        try
        {
            f.Hello = new HelloWrapped { Credential = name, Challenge = Convert.ToBase64String(challenge), Key = Wrap(_key, kek) };
            Write(f);
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(kek); CryptographicOperations.ZeroMemory(sig); }
    }

    public static async Task RemoveHello()
    {
        if (Read() is not { Hello: { } h } f) return;
        await WindowsHello.DeleteAsync(h.Credential);
        f.Hello = null;
        Write(f);
    }

    /// <summary>One Hello key per settings folder (tests and REMOTEHUB_DATA_DIR use other folders).</summary>
    private static string HelloCredentialName() =>
        "RemoteHub-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SettingsStore.Directory.ToLowerInvariant())))[..12];

    // ------------------------------------------------------------------ sealing the stored data

    /// <summary>Encrypts with the settings key: nonce (12) | tag (16) | cipher.</summary>
    internal static byte[] Seal(byte[] plain)
    {
        var key = _key ?? throw new AppLockedException();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    internal static byte[] Open(byte[] sealedData)
    {
        var key = _key ?? throw new AppLockedException();
        if (sealedData.Length < 28) throw new InvalidDataException("The protected settings are damaged.");
        var plain = new byte[sealedData.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedData.AsSpan(0, 12), sealedData.AsSpan(28), sealedData.AsSpan(12, 16), plain);
        return plain;
    }

    private static Wrapped Wrap(byte[] key, byte[] kek)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[key.Length];
        using (var aes = new AesGcm(kek, 16)) aes.Encrypt(nonce, key, cipher, tag);
        return new Wrapped { Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Data = Convert.ToBase64String(cipher) };
    }

    private static byte[]? Unwrap(Wrapped w, byte[] kek)
    {
        try
        {
            var cipher = Convert.FromBase64String(w.Data);
            var key = new byte[cipher.Length];
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(Convert.FromBase64String(w.Nonce), cipher, Convert.FromBase64String(w.Tag), key);
            return key;
        }
        catch (Exception ex) when (ex is AuthenticationTagMismatchException or FormatException or CryptographicException) { return null; }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Locks while running (auto-lock): the settings key is wiped from memory until the next unlock.</summary>
    public static void Lock()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    /// <summary>For tests: forget the settings key (as after a restart).</summary>
    internal static void Forget() => _key = null;
}
