using System.Security.Cryptography;
using System.Text;
using RemoteHub.Services;

namespace RemoteHub.Data;

/// <summary>
/// The local database's keys, stored in <c>db.key</c> next to the database:
/// <list type="bullet">
/// <item>the DB key (256 bit, random) – encrypts the whole database file;</item>
/// <item>the secret key (256 bit, random) – encrypts each password a second time, field by field.</item>
/// </list>
/// The file is protected like settings.dat was: Windows DPAPI for the current user, plus the startup-lock key when the
/// startup protection is on (then nothing can be read before unlocking).
/// </summary>
internal static class DbKeys
{
    private static readonly byte[] Magic = "RHDK\u0001"u8.ToArray();
    private static readonly byte[] MagicLocked = "RHDK\u0002"u8.ToArray();
    private static readonly byte[] Entropy = "RemoteHub.Database.v1"u8.ToArray();
    private const int KeySize = 32;

    internal sealed class Keys : IDisposable
    {
        public byte[] Db { get; } = GC.AllocateUninitializedArray<byte>(KeySize, pinned: true);
        public byte[] Secret { get; } = GC.AllocateUninitializedArray<byte>(KeySize, pinned: true);

        public static Keys New()
        {
            var k = new Keys();
            RandomNumberGenerator.Fill(k.Db);
            RandomNumberGenerator.Fill(k.Secret);
            return k;
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Db);
            CryptographicOperations.ZeroMemory(Secret);
        }
    }

    public static Keys Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var locked = bytes.AsSpan().StartsWith(MagicLocked);
        if (!locked && !bytes.AsSpan().StartsWith(Magic)) throw new InvalidDataException("The database key file is damaged.");
        var inner = ProtectedData.Unprotect(bytes[Magic.Length..], Entropy, DataProtectionScope.CurrentUser);
        var plain = locked ? AppLock.Open(inner) : inner;
        try
        {
            if (plain.Length != 2 * KeySize) throw new InvalidDataException("The database key file is damaged.");
            var k = new Keys();
            plain.AsSpan(0, KeySize).CopyTo(k.Db);
            plain.AsSpan(KeySize).CopyTo(k.Secret);
            return k;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(inner);
        }
    }

    /// <summary>Writes the keys with the protection that is on now (DPAPI, plus the startup lock if enabled).</summary>
    public static void Write(string path, Keys keys)
    {
        var plain = GC.AllocateUninitializedArray<byte>(2 * KeySize, pinned: true);
        keys.Db.CopyTo(plain, 0);
        keys.Secret.CopyTo(plain, KeySize);
        byte[]? sealedData = null;
        try
        {
            byte[] bytes;
            if (AppLock.IsEnabled)
            {
                sealedData = AppLock.Seal(plain);
                bytes = [.. MagicLocked, .. ProtectedData.Protect(sealedData, Entropy, DataProtectionScope.CurrentUser)];
            }
            else bytes = [.. Magic, .. ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)];
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (sealedData is not null) CryptographicOperations.ZeroMemory(sealedData);
        }
    }
}

/// <summary>
/// Field-level encryption of passwords inside the (already encrypted) database: AES-256-GCM with the secret key.
/// The associated data binds each value to its place (table, row, field), so values can't be swapped between rows.
/// Layout: nonce (12) | tag (16) | cipher.
/// </summary>
internal static class SecretBox
{
    public static byte[] Seal(byte[] key, string value, string place)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plain.Length];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(place));
            return [.. nonce, .. tag, .. cipher];
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string Open(byte[] key, byte[] sealedValue, string place)
    {
        if (sealedValue.Length < 28) throw new InvalidDataException("A stored password is damaged.");
        var plain = new byte[sealedValue.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(sealedValue.AsSpan(0, 12), sealedValue.AsSpan(28), sealedValue.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(place));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException) { throw new InvalidDataException("A stored password is damaged or was moved."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
