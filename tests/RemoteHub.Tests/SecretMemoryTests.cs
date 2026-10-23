using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using RemoteHub.Data;
using RemoteHub.Models;
using RemoteHub.Services;

namespace RemoteHub.Tests;

/// <summary>
/// A full memory dump of the test process holds no stored password that isn't in use: only a password made into a
/// string (the control) is found. The test never holds the password itself in one piece – only two random halves that
/// are combined byte by byte while the dump is searched – so the search can't find its own copy.
/// </summary>
[Collection(Name)]
public class SecretMemoryTests
{
    public const string Name = "Memory dump";


    private const int MiniDumpWithFullMemory = 0x2, MiniDumpIgnoreInaccessibleMemory = 0x20000;

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeFileHandle file, int dumpType, IntPtr exception, IntPtr userStream, IntPtr callback);

    /// <summary>A random password of letters and digits, kept as two halves that are XORed (never as plain text).</summary>
    private sealed class Masked
    {
        public readonly byte[] Mask, Value;
        public readonly Secret Secret;

        public Masked(int length)
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var plain = GC.AllocateUninitializedArray<char>(length, pinned: true);
            Mask = RandomNumberGenerator.GetBytes(length);
            Value = new byte[length];
            for (var i = 0; i < length; i++)
            {
                plain[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                Value[i] = (byte)(plain[i] ^ Mask[i]);
            }
            Secret = Secret.From(plain)!;
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(plain.AsSpan()));
        }

        public byte At(int i) => (byte)(Value[i] ^ Mask[i]);

        /// <summary>The password as UTF-8 JSON <c>{"Password":"…"}</c> in a buffer the caller wipes.</summary>
        public byte[] Json()
        {
            var head = "{\"Password\":\""u8;
            var json = GC.AllocateUninitializedArray<byte>(head.Length + Value.Length + 2, pinned: true);
            head.CopyTo(json);
            for (var i = 0; i < Value.Length; i++) json[head.Length + i] = At(i);
            json[^2] = (byte)'"';
            json[^1] = (byte)'}';
            return json;
        }

        /// <summary>Where the password is in <paramref name="data"/> as UTF-8 (wide: false) or UTF-16 (wide: true), or -1.</summary>
        public int Find(ReadOnlySpan<byte> data, bool wide)
        {
            var step = wide ? 2 : 1;
            var first = At(0);
            for (var start = 0; ;)
            {
                var hit = data[start..].IndexOf(first);
                if (hit < 0) return -1;
                var p = start + hit;
                var match = p + Value.Length * step <= data.Length;
                for (var i = 0; match && i < Value.Length; i++)
                    match = data[p + i * step] == At(i) && (!wide || data[p + i * step + 1] == 0);
                if (match) return p;
                start = p + 1;
            }
        }
    }

    [SkippableFact]
    public void A_memory_dump_holds_no_stored_password_that_is_not_in_use()
    {
        var stored = new Masked(28);
        var control = new Masked(28);

        // What the app does with a stored password, each a path a plain copy could be left behind on:
        Assert.Equal(28, stored.Secret.Use(v => v.Length));                                       // used for a call
        stored.Secret.ToSecureString().Dispose();                                                 // PowerShell credentials
        var key = RandomNumberGenerator.GetBytes(32);
        var opened = SecretBox.OpenSecret(key, SecretBox.Seal(key, stored.Secret, "p"), "p");      // database row, both ways
        Assert.Equal(stored.Secret, opened);
        var conn = new Connection { Name = "srv", Host = "srv", InheritCredentials = false, Credentials = { Username = "u", Password = stored.Secret } };
        Assert.Single(AppDataRecords.From(new AppData { Items = [conn] }).Single(r => r.Id == conn.Id.ToString()).Secrets); // record, no JSON value
        var json = stored.Json();
        try { Assert.Equal(stored.Secret, JsonSerializer.Deserialize<CredentialSet>(json, SettingsStore.Json)!.Password); } // read from a file
        finally { CryptographicOperations.ZeroMemory(json); }

        // The control: made into a string, as for an API that takes nothing else. It can't be wiped, so it must be found.
        var leaked = control.Secret.Reveal();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var path = Path.Combine(Path.GetTempPath(), $"rh-secret-{Guid.NewGuid():N}.dmp");
        try
        {
            using var self = Process.GetCurrentProcess();
            bool written;
            using (var file = File.Create(path))
                written = MiniDumpWriteDump(self.Handle, self.Id, file.SafeFileHandle, MiniDumpWithFullMemory | MiniDumpIgnoreInaccessibleMemory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Skip.IfNot(written, $"A memory dump of the test process couldn't be written (error {Marshal.GetLastWin32Error()}).");
            GC.KeepAlive(leaked);

            var (storedFound, controlFound) = (false, false);
            const int chunk = 64 << 20, overlap = 256;
            var buffer = new byte[chunk + overlap];
            using var dump = File.OpenRead(path);
            var carried = 0;
            int read;
            while ((read = dump.Read(buffer, carried, chunk)) > 0)
            {
                var data = buffer.AsSpan(0, carried + read);
                storedFound |= stored.Find(data, wide: false) >= 0 || stored.Find(data, wide: true) >= 0;
                controlFound |= control.Find(data, wide: true) >= 0;
                carried = Math.Min(overlap, data.Length);
                data[^carried..].CopyTo(buffer);
            }
            Assert.True(controlFound, "The control password (a string) wasn't found – the search doesn't work.");
            Assert.False(storedFound, "A stored password was found in plain text in the memory dump.");
        }
        finally { File.Delete(path); }
    }
}

/// <summary>
/// The dump test runs alone, after every other test: MiniDumpWriteDump on its own process suspends the other threads,
/// and one that holds a lock the dump needs deadlocks it – it hung the whole run beside the decoder tests.
/// </summary>
[CollectionDefinition(SecretMemoryTests.Name, DisableParallelization = true)]
public sealed class MemoryDumpCollection;
