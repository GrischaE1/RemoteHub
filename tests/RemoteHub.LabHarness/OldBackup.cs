using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteHub.Services;

/// <summary>
/// old-backup --in &lt;.rhbackup&gt; --out &lt;.rdpmbackup&gt; --password &lt;pw&gt;
///     Writes the settings of a RemoteHub backup as RDP Manager's .rdpmbackup: format "rdpmanager-backup", version 1,
///     sealed without associated data (C-BAK-03). Relabelling a current file isn't enough: since version 4 its sections
///     are bound to the format, and a relabelled file is refused on purpose (Backup.Import).
/// </summary>
internal static class OldBackup
{
    public static object Run(Dictionary<string, string> o)
    {
        var input = o.GetValueOrDefault("in") ?? throw new ArgumentException("--in is missing");
        var output = o.GetValueOrDefault("out") ?? throw new ArgumentException("--out is missing");
        var password = o.GetValueOrDefault("password") ?? throw new ArgumentException("--password is missing");
        var data = Backup.Import(input, password, out _, out _);
        const int iterations = 600_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        var plain = SettingsStore.ToJson(data);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
        var envelope = new
        {
            Format = "rdpmanager-backup", Version = 1, Kdf = "PBKDF2-SHA256", Iterations = iterations,
            Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher), CreatedUtc = DateTime.UtcNow,
        };
        File.WriteAllText(output, JsonSerializer.Serialize(envelope));
        return new { ok = true, output, items = data.Items.Count };
    }
}
