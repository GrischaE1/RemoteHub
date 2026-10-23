using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHub.Services;

/// <summary>The password, decrypted, for the length of one call.</summary>
public delegate T SecretFunc<out T>(ReadOnlySpan<char> value);

/// <summary>The password, decrypted, for the length of one call.</summary>
public delegate void SecretAction(ReadOnlySpan<char> value);

/// <summary>
/// A stored password, kept encrypted in memory (AES-256-GCM with a key made for this process) and decrypted only for
/// the call that needs it, into a pinned buffer that is wiped afterwards – so a memory dump or a page file holds no
/// plain copy of passwords that aren't in use (<c>local-db-design.md §5</c>).
/// <para>
/// Immutable: changing a password means setting a new <see cref="Secret"/>. Never empty – an empty password is
/// <c>null</c>. <see cref="Reveal"/> makes a string and is only for the APIs that take nothing else (the RDP control,
/// WNet, the share libraries); that string can't be wiped and stays until the GC reuses its memory.
/// </para>
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret : IEquatable<Secret>
{
    private const int NonceSize = 12, TagSize = 16;

    // Pinned so the GC never leaves copies of the key behind when it compacts the heap.
    private static readonly byte[] ProcessKey = NewPinnedKey();

    private readonly byte[] _sealed;       // nonce | tag | UTF-8 cipher
    private readonly byte[] _fingerprint;  // HMAC of the value with the process key – for comparing without decrypting

    private Secret(byte[] sealedValue, byte[] fingerprint) => (_sealed, _fingerprint) = (sealedValue, fingerprint);

    /// <summary>The value's length in characters.</summary>
    public int Length { get; private init; }

    /// <summary>A secret holding <paramref name="value"/>, or null when it is empty.</summary>
    public static Secret? From(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return null;
        var utf8 = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(value), pinned: true);
        try
        {
            Encoding.UTF8.GetBytes(value, utf8);
            return Seal(utf8, value.Length);
        }
        finally { CryptographicOperations.ZeroMemory(utf8); }
    }

    /// <summary>A secret holding <paramref name="value"/>, or null when it is null or empty.</summary>
    public static Secret? From(string? value) => value is null ? null : From(value.AsSpan());

    /// <summary>A secret holding the UTF-8 <paramref name="utf8"/>, or null when it is empty. The caller wipes its buffer.</summary>
    public static Secret? FromUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return null;
        return Seal(utf8, Encoding.UTF8.GetCharCount(utf8));
    }

    private static Secret Seal(ReadOnlySpan<byte> utf8, int length)
    {
        var sealedValue = new byte[NonceSize + TagSize + utf8.Length];
        var nonce = sealedValue.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(ProcessKey, TagSize))
            aes.Encrypt(nonce, utf8, sealedValue.AsSpan(NonceSize + TagSize), sealedValue.AsSpan(NonceSize, TagSize));
        return new Secret(sealedValue, HMACSHA256.HashData(ProcessKey, utf8)) { Length = length };
    }

    /// <summary>Calls <paramref name="use"/> with the decrypted UTF-8 bytes, then wipes them.</summary>
    public T UseUtf8<T>(Func<byte[], int, T> use)
    {
        var length = _sealed.Length - NonceSize - TagSize;
        var utf8 = GC.AllocateUninitializedArray<byte>(length, pinned: true);
        try
        {
            using (var aes = new AesGcm(ProcessKey, TagSize))
                aes.Decrypt(_sealed.AsSpan(0, NonceSize), _sealed.AsSpan(NonceSize + TagSize), _sealed.AsSpan(NonceSize, TagSize), utf8);
            return use(utf8, length);
        }
        finally { CryptographicOperations.ZeroMemory(utf8); }
    }

    /// <summary>Calls <paramref name="use"/> with the decrypted value, then wipes it.</summary>
    public T Use<T>(SecretFunc<T> use) => UseUtf8((utf8, length) =>
    {
        var chars = GC.AllocateUninitializedArray<char>(Length, pinned: true);
        try
        {
            Encoding.UTF8.GetChars(utf8.AsSpan(0, length), chars);
            return use(chars);
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())); }
    });

    /// <summary>Calls <paramref name="use"/> with the decrypted value, then wipes it.</summary>
    public void Use(SecretAction use) => Use(v => { use(v); return 0; });

    /// <summary>The value as a read-only <see cref="SecureString"/> (PowerShell's credentials take one).</summary>
    public SecureString ToSecureString() => Use(v =>
    {
        unsafe
        {
            fixed (char* p = v)
            {
                var s = new SecureString(p, v.Length);
                s.MakeReadOnly();
                return s;
            }
        }
    });

    /// <summary>
    /// The value as a string – only for an API that takes nothing else. The string can't be wiped; keep it no longer
    /// than the call.
    /// </summary>
    public string Reveal() => Use(v => new string(v));

    /// <summary>True when the value starts or ends with white space (a common cause of a failed sign-in).</summary>
    public bool HasOuterWhiteSpace => Use(v => char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1]));

    /// <summary>True when both hold the same value. Compared in constant time, without decrypting.</summary>
    public bool Equals(Secret? other) =>
        other is not null && (ReferenceEquals(this, other) || CryptographicOperations.FixedTimeEquals(_fingerprint, other._fingerprint));

    public override bool Equals(object? obj) => Equals(obj as Secret);

    public override int GetHashCode() => BitConverter.ToInt32(_fingerprint, 0);

    /// <summary>True when both are null or hold the same value.</summary>
    public static bool AreEqual(Secret? a, Secret? b) => a is null ? b is null : a.Equals(b);

    /// <summary>A value that is equal for equal secrets, for change detection within this process (not stored).</summary>
    internal ReadOnlySpan<byte> Fingerprint => _fingerprint;

    /// <summary>Never the value, so a secret can't end up in a log or an exception message by accident.</summary>
    public override string ToString() => "(password)";

    private static byte[] NewPinnedKey()
    {
        var key = GC.AllocateUninitializedArray<byte>(32, pinned: true);
        RandomNumberGenerator.Fill(key);
        return key;
    }
}

/// <summary>
/// Reads and writes a <see cref="Secret"/> as a JSON string without making a .NET string of it. While
/// <see cref="Capture"/> or <see cref="Supply"/> is set (the database's records, <c>AppDataRecords</c>), a secret is
/// written as a placeholder and read back from one instead, so the value never enters the JSON at all.
/// </summary>
public sealed class SecretJsonConverter : JsonConverter<Secret>
{
    /// <summary>Starts every placeholder; can't be typed into a password box (U+0001).</summary>
    internal const string PlaceholderPrefix = "\u0001secret:";

    [ThreadStatic] private static List<Secret>? _capture;
    [ThreadStatic] private static IReadOnlyList<Secret>? _supply;

    /// <summary>While set on this thread, written secrets are added here and written as placeholders.</summary>
    internal static List<Secret>? Capture { get => _capture; set => _capture = value; }

    /// <summary>While set on this thread, placeholders are read as the secret at their index here.</summary>
    internal static IReadOnlyList<Secret>? Supply { get => _supply; set => _supply = value; }

    internal static string Placeholder(int index) => PlaceholderPrefix + index;

    internal static bool TryParsePlaceholder(string s, out int index)
    {
        index = -1;
        return s.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) && int.TryParse(s.AsSpan(PlaceholderPrefix.Length), out index);
    }

    public override bool HandleNull => false;

    public override Secret? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A password must be a string.");
        var max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        var chars = GC.AllocateUninitializedArray<char>(max, pinned: true);
        try
        {
            var n = reader.CopyString(chars);
            var value = chars.AsSpan(0, n);
            if (_supply is { } supply && value.StartsWith(PlaceholderPrefix, StringComparison.Ordinal)
                && int.TryParse(value[PlaceholderPrefix.Length..], out var i) && i >= 0 && i < supply.Count)
                return supply[i];
            return Secret.From(value);
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())); }
    }

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options)
    {
        if (_capture is { } capture)
        {
            capture.Add(value);
            writer.WriteStringValue(Placeholder(capture.Count - 1));
            return;
        }
        // The writer escapes into its own buffer; that buffer is the caller's output (a backup or settings file that
        // is encrypted as a whole), so the value has to be in it.
        value.Use(v => writer.WriteStringValue(v));
    }
}
