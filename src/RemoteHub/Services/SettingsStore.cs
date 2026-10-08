using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using RemoteHub.Models;

namespace RemoteHub.Services;

/// <summary>Per-PC settings that are never shared: where the data file lives and window/UI state.</summary>
public sealed class LocalConfig
{
    /// <summary>Folder of a shared settings file (OneDrive, network share …). Null = this PC only.</summary>
    public string? SharedFolder { get; set; }
    public UiState? Ui { get; set; }
    /// <summary>Format version of this file; 2 = dark theme default, 3 = wider sidebar default.</summary>
    public int Version { get; set; }
}

/// <summary>Thrown when the settings file was changed by another PC/process since we last read or wrote it.</summary>
public sealed class SettingsConflictException() : Exception("The settings file was changed on another device.");

/// <summary>
/// Persists all settings to a single file, in one of two modes:
/// <list type="bullet">
/// <item><b>This PC</b> (default): <c>%APPDATA%\RemoteHub\settings.dat</c>, encrypted with Windows DPAPI for the
/// current user – no password needed, but only readable by this user on this PC.</item>
/// <item><b>Shared folder</b> (OneDrive, network share): <c>RemoteHub.settings</c> encrypted with AES-256-GCM using a
/// password-derived key (PBKDF2-SHA256). The password is asked once per PC; the derived key is then cached locally,
/// protected with DPAPI, so there is no prompt on every start.</item>
/// </list>
/// Window/UI state is always stored locally (<see cref="LocalConfig"/>), so it does not sync between PCs.
/// The shared folder can be preset by IT via HKLM/HKCU\SOFTWARE\Policies\RemoteHub → SettingsFolder.
/// </summary>
public static class SettingsStore
{
    // The values below date from when the app was called "RDP Manager". They stay as they are: existing settings
    // files are encrypted with them, and older versions on other PCs read the same shared file.
    private static readonly byte[] Magic = "RDPM\u0001"u8.ToArray();
    /// <summary>Settings file sealed with the startup-protection key (<see cref="AppLock"/>) inside DPAPI.</summary>
    private static readonly byte[] MagicLocked = "RDPM\u0002"u8.ToArray();
    /// <summary>Key cache sealed with the startup-protection key inside DPAPI.</summary>
    private static readonly byte[] KeyCacheLocked = "RHK2"u8.ToArray();
    private static readonly byte[] Entropy = "RdpManager.Settings.v1"u8.ToArray();
    private static readonly byte[] KeyCacheEntropy = "RdpManager.KeyCache.v1"u8.ToArray();
    private const string SharedFormat = "rdpmanager-settings";
    private const string SharedFileName = "RemoteHub.settings";
    /// <summary>Shared file name before the rename; still used when a shared folder already has it.</summary>
    private const string LegacySharedFileName = "RdpManager.settings";
    private const string LegacyFolderName = "RdpManager";
    private const int Iterations = 600_000;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%APPDATA%\RemoteHub, or the folder in REMOTEHUB_DATA_DIR (useful for testing; RDPMANAGER_DATA_DIR still works).</summary>
    public static string Directory { get; } = ResolveDirectory();

    /// <summary>Set when the settings of "RDP Manager" were copied to the RemoteHub folder at this start.</summary>
    public static string? MigratedFrom { get; private set; }

    private static string ResolveDirectory()
    {
        foreach (var v in new[] { "REMOTEHUB_DATA_DIR", "RDPMANAGER_DATA_DIR" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } custom) return custom;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "RemoteHub");
        var legacy = Path.Combine(appData, LegacyFolderName);
        return MigrateFolder(legacy, dir) ? dir : System.IO.Directory.Exists(dir) || !System.IO.Directory.Exists(legacy) ? dir : legacy;
    }

    /// <summary>
    /// First start after the rename: copies %APPDATA%\RdpManager to %APPDATA%\RemoteHub (settings, local config,
    /// remembered key, log). The old folder is left as it is, as a backup. Returns false if nothing was copied.
    /// </summary>
    public static bool MigrateFolder(string legacy, string dir)
    {
        if (System.IO.Directory.Exists(dir) || !System.IO.Directory.Exists(legacy)) return false;
        var tmp = dir + ".migrating";
        try
        {
            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            foreach (var f in System.IO.Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(tmp, Path.GetRelativePath(legacy, f));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
            }
            System.IO.Directory.CreateDirectory(tmp);
            System.IO.Directory.Move(tmp, dir);
            MigratedFrom = legacy;
            return true;
        }
        catch
        {
            try { if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true); } catch { /* best effort */ }
            return false; // keep using the old folder
        }
    }

    private static string LocalConfigPath => Path.Combine(Directory, "local.json");
    private static string KeyCachePath => Path.Combine(Directory, "keys.dat");
    public static string LocalFilePath => Path.Combine(Directory, "settings.dat");

    /// <summary>Asks the user for the shared-file password (argument: message). Return null to cancel.</summary>
    public static Func<string, string?>? PasswordPrompt { get; set; }

    /// <summary>Raised (on a background thread) when the shared file was changed by someone else.</summary>
    public static event Action? ExternalChange;

    /// <summary>Set when the shared file was unavailable at start-up and local settings are used instead.</summary>
    public static bool UsingLocalFallback { get; set; }

    private static LocalConfig _local = new();
    private static byte[]? _key, _salt;          // shared mode: active key and the file's salt
    private static byte[]? _lastHash;            // hash of the file content we last read or wrote
    private static FileSystemWatcher? _watcher;

    // ------------------------------------------------------------------ location

    /// <summary>Shared folder preset by policy (IT), with environment variables expanded. Null if none.</summary>
    public static string? PolicyFolder
    {
        get
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    // RdpManager: the policy key from before the rename, still honoured.
                    foreach (var key in new[] { @"SOFTWARE\Policies\RemoteHub", @"SOFTWARE\Policies\RdpManager" })
                    {
                        using var k = root.OpenSubKey(key);
                        if (k?.GetValue("SettingsFolder") is string s && s.Trim().Length > 0)
                            return Environment.ExpandEnvironmentVariables(s.Trim());
                    }
                }
                catch { /* no access – ignore */ }
            }
            return null;
        }
    }

    public static bool IsPolicyManaged => PolicyFolder is not null;

    /// <summary>Folder of the shared settings file in use, or null for "this PC only".</summary>
    public static string? SharedFolder =>
        UsingLocalFallback ? null
        : PolicyFolder ?? (_local.SharedFolder is { Length: > 0 } f ? Environment.ExpandEnvironmentVariables(f) : null);

    public static bool IsShared => SharedFolder is not null;

    public static string FilePath => SharedFolder is { } f ? SharedFilePathFor(f) : LocalFilePath;

    /// <summary>The shared file in a folder: RemoteHub.settings, or RdpManager.settings when only that one exists there.</summary>
    public static string SharedFilePathFor(string folder)
    {
        var dir = Environment.ExpandEnvironmentVariables(folder);
        var path = Path.Combine(dir, SharedFileName);
        var legacy = Path.Combine(dir, LegacySharedFileName);
        return !File.Exists(path) && File.Exists(legacy) ? legacy : path;
    }

    /// <summary>Suggested shared folder: OneDrive for Business, else personal OneDrive.</summary>
    public static string? SuggestedOneDriveFolder()
    {
        var od = Environment.GetEnvironmentVariable("OneDriveCommercial") ?? Environment.GetEnvironmentVariable("OneDrive");
        return string.IsNullOrEmpty(od) ? null : Path.Combine(od, "RemoteHub");
    }

    public static string Describe() => UsingLocalFallback
        ? "Settings: this PC (shared file unavailable – changes stay on this PC)"
        : SharedFolder is { } f ? $"Settings: shared – {f}" : "Settings: this PC";

    // ------------------------------------------------------------------ load / save

    public static AppData Load()
    {
        _local = LoadLocalConfig();
        AppData data;
        if (IsShared)
        {
            var path = FilePath;
            if (!File.Exists(path))
                throw new FileNotFoundException($"The shared settings file was not found:\n{path}\n\nIs the folder (e.g. OneDrive) synced and available?");
            data = LoadShared(path, password: null);
            StartWatching(path);
        }
        else
        {
            data = LoadLocalProtected();
        }
        if (_local.Ui is not null) data.Ui = _local.Ui;
        return data;
    }

    /// <summary>Saves the data. Throws <see cref="SettingsConflictException"/> if the file changed elsewhere (unless <paramref name="force"/>).</summary>
    public static void Save(AppData data, bool force = false)
    {
        SaveLocalConfig(data.Ui);
        var path = FilePath;
        if (!force && File.Exists(path) && _lastHash is not null && !Hash(File.ReadAllBytes(path)).SequenceEqual(_lastHash))
            throw new SettingsConflictException();

        var bytes = IsShared ? EncryptShared(data) : EncryptLocal(data);
        WriteAtomic(path, bytes);
        _lastHash = Hash(bytes);
    }

    private static AppData LoadLocalProtected()
    {
        if (!File.Exists(LocalFilePath)) { _lastHash = null; return new AppData(); }
        var bytes = File.ReadAllBytes(LocalFilePath);
        var plain = DecryptLocalBytes(bytes);
        try
        {
            var data = FromJson(plain);
            _lastHash = Hash(bytes);
            return data;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// The JSON inside settings.dat: DPAPI for this Windows account, plus the startup-protection key when the file was
    /// written with the protection on (works either way, so turning it on or off can't make a file unreadable).
    /// </summary>
    private static byte[] DecryptLocalBytes(byte[] bytes)
    {
        var locked = bytes.AsSpan().StartsWith(MagicLocked);
        if (!locked && !bytes.AsSpan().StartsWith(Magic))
            throw new InvalidDataException("The settings file is not a valid RemoteHub settings file.");
        var inner = ProtectedData.Unprotect(bytes[Magic.Length..], Entropy, DataProtectionScope.CurrentUser);
        if (!locked) return inner;
        try { return AppLock.Open(inner); }
        finally { CryptographicOperations.ZeroMemory(inner); }
    }

    private static byte[] EncryptLocalBytes(byte[] plain)
    {
        if (!AppLock.IsEnabled) return [.. Magic, .. ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)];
        var sealedData = AppLock.Seal(plain);
        return [.. MagicLocked, .. ProtectedData.Protect(sealedData, Entropy, DataProtectionScope.CurrentUser)];
    }

    private static byte[] EncryptLocal(AppData data)
    {
        var plain = ToJson(data);
        try { return EncryptLocalBytes(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Theme of this PC, readable before the settings are unlocked (for the unlock window).</summary>
    public static string PeekTheme() => LoadLocalConfig().Ui?.Theme ?? "Dark";

    /// <summary>
    /// Turns the startup protection on or off (<paramref name="change"/>) and rewrites what is stored on this PC with
    /// the new protection: the settings file, a local copy kept while a shared file is used, and the remembered
    /// shared-file key. Earlier copies (.bak) are removed so no unprotected copy stays behind.
    /// </summary>
    public static async Task Reprotect(AppData current, Func<Task> change)
    {
        var cache = KeyCacheRead();
        byte[]? localPlain = IsShared && File.Exists(LocalFilePath) ? DecryptLocalBytes(File.ReadAllBytes(LocalFilePath)) : null;
        try
        {
            await change();
            if (cache.Count > 0 || File.Exists(KeyCachePath)) KeyCacheWrite(cache);
            if (localPlain is not null) WriteAtomic(LocalFilePath, EncryptLocalBytes(localPlain));
            if (!IsShared) Save(current, force: true);
            foreach (var bak in new[] { LocalFilePath + ".bak", KeyCachePath + ".bak" })
                try { if (File.Exists(bak)) File.Delete(bak); } catch { /* best effort */ }
        }
        finally { if (localPlain is not null) CryptographicOperations.ZeroMemory(localPlain); }
    }

    // ------------------------------------------------------------------ shared (password) mode

    private sealed class Envelope
    {
        public string Format { get; set; } = SharedFormat;
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
        public DateTime ModifiedUtc { get; set; }
    }

    /// <summary>Decrypts a shared file. Uses the cached key, else <paramref name="password"/>, else <see cref="PasswordPrompt"/>.</summary>
    private static AppData LoadShared(string path, string? password)
    {
        var bytes = File.ReadAllBytes(path);
        var env = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Empty settings file.");
        if (env.Format != SharedFormat) throw new InvalidDataException("Not a RemoteHub shared settings file.");
        var salt = Convert.FromBase64String(env.Salt);

        var key = password is null ? KeyCacheGet(salt) : Derive(password, salt, env.Iterations);
        string message = "Enter the password of the shared settings file. It is remembered on this PC.";
        while (true)
        {
            if (key is not null && TryDecrypt(env, key) is { } data)
            {
                _key = key;
                _salt = salt;
                _lastHash = Hash(bytes);
                KeyCacheSet(salt, key);
                return data;
            }
            if (password is not null) throw new UnauthorizedAccessException("Wrong password for the shared settings file.");
            var pw = PasswordPrompt?.Invoke(message) ?? throw new OperationCanceledException("Password entry was cancelled.");
            key = Derive(pw, salt, env.Iterations);
            message = "Wrong password – please try again.";
        }
    }

    private static AppData? TryDecrypt(Envelope env, byte[] key)
    {
        var cipher = Convert.FromBase64String(env.Data);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(env.Nonce), cipher, Convert.FromBase64String(env.Tag), plain);
            return FromJson(plain);
        }
        catch (AuthenticationTagMismatchException) { return null; }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] EncryptShared(AppData data)
    {
        if (_key is null || _salt is null) throw new InvalidOperationException("The shared settings file is not unlocked.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = ToJson(data);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var env = new Envelope
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(_salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
            ModifiedUtc = DateTime.UtcNow,
        };
        return JsonSerializer.SerializeToUtf8Bytes(env, new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    // ------------------------------------------------------------------ switching location

    /// <summary>Creates a new shared settings file in <paramref name="folder"/> from <paramref name="current"/> and switches to it.</summary>
    public static void CreateShared(string folder, string password, AppData current)
    {
        var expanded = Environment.ExpandEnvironmentVariables(folder);
        System.IO.Directory.CreateDirectory(expanded);
        var path = SharedFilePathFor(folder);
        if (File.Exists(path)) throw new IOException("A shared settings file already exists in this folder.");
        _salt = RandomNumberGenerator.GetBytes(16);
        _key = Derive(password, _salt, Iterations);
        KeyCacheSet(_salt, _key);
        _local.SharedFolder = folder;
        UsingLocalFallback = false;
        _lastHash = null;
        Save(current, force: true);
        StartWatching(path);
    }

    /// <summary>Opens an existing shared settings file in <paramref name="folder"/> and switches to it. Returns its data.</summary>
    public static AppData OpenShared(string folder, string password, UiState keepUi)
    {
        var data = LoadShared(SharedFilePathFor(folder), password);
        _local.SharedFolder = folder;
        UsingLocalFallback = false;
        data.Ui = keepUi;
        SaveLocalConfig(keepUi);
        StartWatching(SharedFilePathFor(folder));
        return data;
    }

    /// <summary>Switches back to "this PC only", writing <paramref name="current"/> to the local DPAPI file.</summary>
    public static void SwitchToLocal(AppData current)
    {
        StopWatching();
        _local.SharedFolder = null;
        UsingLocalFallback = false;
        _key = _salt = null;
        _lastHash = null;
        Save(current, force: true);
    }

    // ------------------------------------------------------------------ external changes

    private static void StartWatching(string path)
    {
        StopWatching();
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            var debounce = new System.Threading.Timer(_ => CheckExternal(path));
            void Kick(object? s, FileSystemEventArgs e) => debounce.Change(1500, System.Threading.Timeout.Infinite);
            _watcher.Changed += Kick;
            _watcher.Created += Kick;
            _watcher.Renamed += (s, e) => Kick(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* watching is best effort; conflicts are still caught on save */ }
    }

    private static void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private static void CheckExternal(string path)
    {
        try
        {
            if (!File.Exists(path) || _lastHash is null) return;
            if (!Hash(File.ReadAllBytes(path)).SequenceEqual(_lastHash)) ExternalChange?.Invoke();
        }
        catch { /* file busy (sync in progress) – the next change event or save will catch it */ }
    }

    /// <summary>Reloads the current file (e.g. after it changed on another device), keeping this PC's UI state.</summary>
    public static AppData Reload(UiState keepUi)
    {
        var data = IsShared ? LoadShared(FilePath, null) : LoadLocalProtected();
        data.Ui = keepUi;
        return data;
    }

    // ------------------------------------------------------------------ local config & key cache

    private static LocalConfig LoadLocalConfig()
    {
        LocalConfig cfg;
        try
        {
            // File.ReadAllText strips a UTF-8 BOM (e.g. after editing the file in Notepad).
            cfg = File.Exists(LocalConfigPath)
                ? JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(LocalConfigPath), Json) ?? new()
                : new();
        }
        catch { cfg = new(); }
        // Up to v1.7 the theme defaulted to "System"; dark is the default now.
        if (cfg.Version < 2 && cfg.Ui is { Theme: "System" }) cfg.Ui.Theme = "Dark";
        // Up to v1.8 the sidebar defaulted to 300 px, which cut off the search box.
        if (cfg.Version < 3 && cfg.Ui is { SidebarWidth: < 340 }) cfg.Ui.SidebarWidth = 360;
        cfg.Version = 3;
        return cfg;
    }

    private static void SaveLocalConfig(UiState ui)
    {
        _local.Ui = ui;
        System.IO.Directory.CreateDirectory(Directory);
        WriteAtomic(LocalConfigPath, JsonSerializer.SerializeToUtf8Bytes(_local, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }

    private static Dictionary<string, string> KeyCacheRead()
    {
        try
        {
            if (!File.Exists(KeyCachePath)) return new();
            var bytes = File.ReadAllBytes(KeyCachePath);
            var locked = bytes.AsSpan().StartsWith(KeyCacheLocked);
            var inner = ProtectedData.Unprotect(locked ? bytes[KeyCacheLocked.Length..] : bytes, KeyCacheEntropy, DataProtectionScope.CurrentUser);
            var plain = locked ? AppLock.Open(inner) : inner;
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new(); }
            finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(inner); }
        }
        catch { return new(); }
    }

    private static byte[]? KeyCacheGet(byte[] salt) =>
        KeyCacheRead().TryGetValue(Convert.ToBase64String(salt), out var k) ? Convert.FromBase64String(k) : null;

    private static void KeyCacheSet(byte[] salt, byte[] key)
    {
        var map = KeyCacheRead();
        map[Convert.ToBase64String(salt)] = Convert.ToBase64String(key);
        KeyCacheWrite(map);
    }

    private static void KeyCacheWrite(Dictionary<string, string> map)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(map);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var bytes = AppLock.IsEnabled
                ? [.. KeyCacheLocked, .. ProtectedData.Protect(AppLock.Seal(plain), KeyCacheEntropy, DataProtectionScope.CurrentUser)]
                : ProtectedData.Protect(plain, KeyCacheEntropy, DataProtectionScope.CurrentUser);
            WriteAtomic(KeyCachePath, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>Forgets all remembered shared-file passwords on this PC.</summary>
    public static void ForgetRememberedPasswords()
    {
        if (File.Exists(KeyCachePath)) File.Delete(KeyCachePath);
    }

    // ------------------------------------------------------------------ helpers

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    private static byte[] Hash(byte[] bytes) => SHA256.HashData(bytes);

    /// <summary>Serializes the shared data. UI state is excluded – it is stored per PC in <see cref="LocalConfig"/>.</summary>
    public static byte[] ToJson(AppData data) => JsonSerializer.SerializeToUtf8Bytes(new AppData
    {
        Version = data.Version,
        Defaults = data.Defaults,
        DefaultCredentials = data.DefaultCredentials,
        DefaultCredentialId = data.DefaultCredentialId,
        SavedCredentials = data.SavedCredentials,
        Items = data.Items,
        Tools = data.Tools,
        Ui = null!,
    }, Json);

    public static AppData FromJson(byte[] json)
    {
        var data = JsonSerializer.Deserialize<AppData>(json, Json) ?? new AppData();
        data.Defaults ??= RdpOptions.BuiltInDefaults();
        data.Ui ??= new UiState();
        if (data.Ui.Theme == "System") data.Ui.Theme = "Dark"; // pre-1.8 default (only used if no local.json yet)
        data.DefaultCredentials ??= new CredentialSet();
        data.SavedCredentials ??= new();
        data.Tools ??= new RemoteToolSettings();
        data.Ui ??= new UiState();
        RelinkParents(data.Items, null);
        MigratePasswordPrompt(data);
        return data;
    }

    /// <summary>
    /// v1.5.0 had a global menu toggle and v1.5.1 an on/off setting ("copy to clipboard"); both become
    /// <see cref="RdpOptions.PasswordPrompt"/>. "Off" stays off; an explicit "on" on a folder/connection stays clipboard;
    /// the old default ("on" in global defaults) moves to the new default, auto-type.
    /// </summary>
    private static void MigratePasswordPrompt(AppData data)
    {
        if (data.Ui.CopyPasswordOnPrompt == false) data.Defaults.PasswordPrompt ??= PasswordPromptAction.Off;
        data.Ui.CopyPasswordOnPrompt = null;
        if (data.Defaults.CopyPasswordOnPrompt == false) data.Defaults.PasswordPrompt ??= PasswordPromptAction.Off;
        data.Defaults.CopyPasswordOnPrompt = null;
        // 1.9.1: automatic typing was removed – everything that used it copies to the clipboard instead.
        if (data.Defaults.PasswordPrompt == PasswordPromptAction.AutoType) data.Defaults.PasswordPrompt = PasswordPromptAction.Clipboard;

        void Walk(IEnumerable<NodeBase> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Options.CopyPasswordOnPrompt is { } v)
                    n.Options.PasswordPrompt ??= v ? PasswordPromptAction.Clipboard : PasswordPromptAction.Off;
                n.Options.CopyPasswordOnPrompt = null;
                if (n.Options.PasswordPrompt == PasswordPromptAction.AutoType) n.Options.PasswordPrompt = PasswordPromptAction.Clipboard;
                if (n is Folder f) Walk(f.Children);
            }
        }
        Walk(data.Items);
    }

    public static void RelinkParents(IEnumerable<NodeBase> items, Folder? parent)
    {
        foreach (var n in items)
        {
            n.Parent = parent;
            n.Options ??= new RdpOptions();
            n.Credentials ??= new CredentialSet();
            if (n is Folder f) RelinkParents(f.Children, f);
        }
    }
}

/// <summary>Password-protected, portable export/import (AES-256-GCM, PBKDF2-SHA256).</summary>
public static class Backup
{
    private const int Iterations = 600_000;

    private sealed class Envelope
    {
        public string Format { get; set; } = "remotehub-backup";
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Data { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    public static void Export(AppData data, string path, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
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
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }

        var env = new Envelope
        {
            Iterations = Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
            CreatedUtc = DateTime.UtcNow,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AppData Import(string path, string password)
    {
        var env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path))
                  ?? throw new InvalidDataException("Empty backup file.");
        if (env.Format is not ("remotehub-backup" or "rdpmanager-backup")) throw new InvalidDataException("Not a RemoteHub backup file.");

        var salt = Convert.FromBase64String(env.Salt);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, env.Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = Convert.FromBase64String(env.Data);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(env.Nonce), cipher, Convert.FromBase64String(env.Tag), plain);
            return SettingsStore.FromJson(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new UnauthorizedAccessException("Wrong password or the backup file is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
