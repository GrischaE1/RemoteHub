using System.Globalization;
using System.Management.Automation.Runspaces;
using System.Text;

namespace RemoteHub.Remoting;

/// <summary>Registry value types (names match <c>Microsoft.Win32.RegistryValueKind</c>).</summary>
public enum RegKind { String, ExpandString, MultiString, DWord, QWord, Binary, None, Unknown }

/// <summary>One registry value. <see cref="Data"/> is its text form (numbers in decimal, binary as hex pairs, multi-string lines).</summary>
public sealed record RegValue(string Name, RegKind Kind, string Data, bool IsSet = true)
{
    public bool IsDefault => Name.Length == 0;
    public string DisplayName => IsDefault ? "(Default)" : Name;
    public string TypeName => RemoteRegistry.TypeName(Kind);
    public string DisplayData => IsSet ? RemoteRegistry.Display(Kind, Data) : "(value not set)";
    public bool CanEdit => Kind != RegKind.Unknown;
    public string Symbol => Kind switch
    {
        RegKind.String or RegKind.ExpandString => "TextT24",
        RegKind.MultiString => "TextBulletListLtr24",
        RegKind.DWord or RegKind.QWord => "NumberSymbol24",
        _ => "Code24",
    };

    /// <summary>The order the value list shows: the default value first (as Regedit does), then by the column, then by name.</summary>
    public static int Compare(RegValue a, RegValue b, RegSortColumn by, bool descending)
    {
        if (a.IsDefault != b.IsDefault) return a.IsDefault ? -1 : 1;
        var c = by switch
        {
            RegSortColumn.Type => StringComparer.CurrentCultureIgnoreCase.Compare(a.TypeName, b.TypeName),
            RegSortColumn.Data => StringComparer.CurrentCultureIgnoreCase.Compare(a.DisplayData, b.DisplayData),
            _ => 0,
        };
        if (c == 0) c = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        return descending ? -c : c;
    }

    /// <summary>The value list's filter: the name or the data contains the text, ignoring case.</summary>
    public static bool Matches(RegValue v, string filter)
    {
        var f = filter.Trim();
        return v.DisplayName.Contains(f, StringComparison.OrdinalIgnoreCase) || v.DisplayData.Contains(f, StringComparison.OrdinalIgnoreCase);
    }
}

public enum RegSortColumn { Name, Type, Data }

public sealed record RegSubKey(string Name, bool HasChildren);

/// <summary>One key: its canonical path ("" = the hive list), sub keys and values (default value first).</summary>
public sealed record RegListing(string Path, List<RegSubKey> Keys, List<RegValue> Values);

/// <summary>
/// Registry access for the registry tab. Only the key that is shown gets read (one call per key); writes are single,
/// explicit operations. Everything runs in the remote PowerShell over the 64-bit registry view.
/// </summary>
public static class RemoteRegistry
{
    public static readonly string[] Hives = ["HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG"];

    private static readonly Dictionary<string, string> HiveAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKEY_CLASSES_ROOT"] = "HKEY_CLASSES_ROOT", ["HKCR"] = "HKEY_CLASSES_ROOT",
        ["HKEY_CURRENT_USER"] = "HKEY_CURRENT_USER", ["HKCU"] = "HKEY_CURRENT_USER",
        ["HKEY_LOCAL_MACHINE"] = "HKEY_LOCAL_MACHINE", ["HKLM"] = "HKEY_LOCAL_MACHINE",
        ["HKEY_USERS"] = "HKEY_USERS", ["HKU"] = "HKEY_USERS",
        ["HKEY_CURRENT_CONFIG"] = "HKEY_CURRENT_CONFIG", ["HKCC"] = "HKEY_CURRENT_CONFIG",
    };

    private static readonly Dictionary<string, string> HiveEnum = new()
    {
        ["HKEY_CLASSES_ROOT"] = "ClassesRoot", ["HKEY_CURRENT_USER"] = "CurrentUser", ["HKEY_LOCAL_MACHINE"] = "LocalMachine",
        ["HKEY_USERS"] = "Users", ["HKEY_CURRENT_CONFIG"] = "CurrentConfig",
    };

    /// <summary>
    /// Canonical path ("HKEY_LOCAL_MACHINE\SOFTWARE\…") from what users paste: HKLM\…, HKLM:\…, Registry::…, Computer\… (regedit).
    /// "" or "Computer" = the hive list. Throws <see cref="ArgumentException"/> for an unknown hive.
    /// </summary>
    public static string Normalize(string? path)
    {
        var p = (path ?? "").Trim().Trim('"').Replace('/', '\\');
        foreach (var prefix in new[] { @"Microsoft.PowerShell.Core\Registry::", "Registry::", @"Computer\", "Computer" })
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p = p[prefix.Length..]; break; }
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (!HiveAliases.TryGetValue(parts[0].TrimEnd(':'), out var hive))
            throw new ArgumentException($"Unknown registry hive \"{parts[0]}\". Use e.g. HKLM\\SOFTWARE or HKEY_CURRENT_USER\\Software.");
        return string.Join('\\', parts.Skip(1).Prepend(hive));
    }

    public static (string Hive, string Sub) Split(string canonical)
    {
        var i = canonical.IndexOf('\\');
        return i < 0 ? (canonical, "") : (canonical[..i], canonical[(i + 1)..]);
    }

    /// <summary>Parent key; "" above a hive.</summary>
    public static string Parent(string canonical)
    {
        var i = canonical.LastIndexOf('\\');
        return i < 0 ? "" : canonical[..i];
    }

    public static string Leaf(string canonical) => canonical[(canonical.LastIndexOf('\\') + 1)..];

    public static string Combine(string parent, string name) => parent.Length == 0 ? name : parent + "\\" + name;

    /// <summary>Hives and their direct children (e.g. HKLM\SOFTWARE) are never deleted or renamed from here.</summary>
    public static bool IsProtected(string canonical) => canonical.Count(c => c == '\\') < 2;

    /// <summary>A key or value name: no backslash for keys, at most 255 characters (Windows limits).</summary>
    public static bool IsValidKeyName(string name) => name.Length is > 0 and <= 255 && !name.Contains('\\') && name.Trim().Length > 0;

    public static string TypeName(RegKind k) => k switch
    {
        RegKind.String => "REG_SZ", RegKind.ExpandString => "REG_EXPAND_SZ", RegKind.MultiString => "REG_MULTI_SZ",
        RegKind.DWord => "REG_DWORD", RegKind.QWord => "REG_QWORD", RegKind.Binary => "REG_BINARY", RegKind.None => "REG_NONE",
        _ => "(unknown type)",
    };

    /// <summary>How regedit shows the data: numbers as hex + decimal, multi-strings on one line.</summary>
    public static string Display(RegKind k, string data)
    {
        switch (k)
        {
            case RegKind.DWord when uint.TryParse(data, out var d): return $"0x{d:x8} ({d})";
            case RegKind.QWord when ulong.TryParse(data, out var q): return $"0x{q:x16} ({q})";
            case RegKind.MultiString: return data.Replace("\n", " ");
            case RegKind.Binary or RegKind.None or RegKind.Unknown:
                var hex = data.ToLowerInvariant();
                return hex.Length == 0 ? "(zero-length binary value)" : hex.Length > 300 ? hex[..300] + " …" : hex;
            default: return data;
        }
    }

    /// <summary>
    /// Turns the text from the editor into the object that is written. Numbers accept decimal or hex (0x… or with
    /// <paramref name="hex"/>); binary accepts hex pairs with optional spaces, commas or dashes.
    /// </summary>
    public static bool TryParse(RegKind kind, string text, bool hex, out object data, out string error)
    {
        error = "";
        data = text;
        switch (kind)
        {
            case RegKind.String or RegKind.ExpandString:
                return true;
            case RegKind.MultiString:
                data = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
                return true;
            case RegKind.DWord:
                if (!TryNumber(text, hex, out var d) || d > uint.MaxValue) { error = "Enter a number from 0 to 4294967295 (0xFFFFFFFF)."; return false; }
                data = unchecked((int)(uint)d);
                return true;
            case RegKind.QWord:
                if (!TryNumber(text, hex, out var q)) { error = "Enter a number from 0 to 18446744073709551615."; return false; }
                data = unchecked((long)q);
                return true;
            case RegKind.Binary or RegKind.None:
                var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not (',' or '-')).ToArray());
                if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
                if (clean.Length % 2 != 0 || !clean.All(Uri.IsHexDigit)) { error = "Enter hex byte pairs, e.g. 01 a0 ff."; return false; }
                data = Convert.FromHexString(clean);
                return true;
            default:
                error = "This value type can't be edited here.";
                return false;
        }
    }

    private static bool TryNumber(string text, bool hex, out ulong value)
    {
        var t = text.Trim().Replace("_", "");
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { t = t[2..]; hex = true; }
        return ulong.TryParse(t, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Data shown in the editor for the chosen base.</summary>
    public static string EditText(RegKind kind, string data, bool hex) => kind switch
    {
        RegKind.DWord when hex && uint.TryParse(data, out var d) => d.ToString("x"),
        RegKind.QWord when hex && ulong.TryParse(data, out var q) => q.ToString("x"),
        RegKind.MultiString => data.Replace("\n", Environment.NewLine),
        _ => data,
    };

    // ------------------------------------------------------------------ remote scripts

    // Shared prologue: opens the hive in the 64-bit view and the key ($k), read-only or writable.
    private const string Open = """
        $ErrorActionPreference = 'Stop'
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]$Hive, [Microsoft.Win32.RegistryView]::Registry64)
        $k = if ($Sub) { $base.OpenSubKey($Sub, [bool]$Write) } else { $base }
        if (-not $k) { throw "Key not found: $Sub" }
        """;

    // "S|hasChildren|name" per sub key, "V|kind|b64(name)|b64(data)" per value: compact and safe for any characters.
    private const string ListScript = "param([string]$Hive, [string]$Sub, [bool]$Write = $false)\n" + Open + """

        $u = [Text.Encoding]::UTF8
        foreach ($n in $k.GetSubKeyNames()) {
            $c = 1
            try { $s = $k.OpenSubKey($n, $false); if ($s) { $c = $s.SubKeyCount; $s.Close() } } catch { }
            'S|' + [int]($c -gt 0) + '|' + $n
        }
        foreach ($n in $k.GetValueNames()) {
            $kind = $k.GetValueKind($n)
            $v = $k.GetValue($n, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $t = if ($null -eq $v) { '' }
                 elseif ($kind -eq 'MultiString') { @($v) -join "`n" }
                 elseif ($kind -eq 'DWord') { [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$v), 0).ToString() }
                 elseif ($kind -eq 'QWord') { [BitConverter]::ToUInt64([BitConverter]::GetBytes([long]$v), 0).ToString() }
                 elseif ($v -is [byte[]]) { [BitConverter]::ToString($v).Replace('-', ' ') }
                 else { [string]$v }
            'V|' + $kind + '|' + [Convert]::ToBase64String($u.GetBytes($n)) + '|' + [Convert]::ToBase64String($u.GetBytes($t))
        }
        """;

    private const string SetScript = "param([string]$Hive, [string]$Sub, [string]$Name, [string]$Kind, $Data, [bool]$CreateOnly, [bool]$Write = $true)\n" + Open + """

        if ($CreateOnly -and ($k.GetValueNames() -contains $Name)) { throw "A value named '$Name' already exists." }
        $vk = [Microsoft.Win32.RegistryValueKind]$Kind
        # Assign (not pipeline output) so arrays keep their type and empty arrays stay arrays.
        [object]$d = [string]$Data
        if ($Kind -eq 'MultiString') { [string[]]$d = @($Data | Where-Object { $null -ne $_ }) }
        elseif ($Kind -eq 'DWord') { $d = [int]$Data }
        elseif ($Kind -eq 'QWord') { $d = [long]$Data }
        elseif ($Kind -eq 'Binary' -or $Kind -eq 'None') { [byte[]]$d = @($Data | Where-Object { $null -ne $_ }) }
        $k.SetValue($Name, $d, $vk)
        """;

    private const string DeleteValueScript = "param([string]$Hive, [string]$Sub, [string[]]$Names, [bool]$Write = $true)\n" + Open + """

        foreach ($n in $Names) { $k.DeleteValue($n, $true) }
        """;

    private const string RenameValueScript = "param([string]$Hive, [string]$Sub, [string]$Name, [string]$NewName, [bool]$Write = $true)\n" + Open + """

        $names = $k.GetValueNames()
        if (-not ($names -contains $Name)) { throw "Value not found: $Name" }
        if ($names -contains $NewName) { throw "A value named '$NewName' already exists." }
        $kind = $k.GetValueKind($Name)
        if ($kind -eq 'Unknown') { throw "Values of this type can't be renamed here." }
        $k.SetValue($NewName, $k.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames), $kind)
        $k.DeleteValue($Name, $true)
        """;

    private const string CreateKeyScript = "param([string]$Hive, [string]$Sub, [string]$Name, [bool]$Write = $true)\n" + Open + """

        $e = $k.OpenSubKey($Name, $false)
        if ($e) { $e.Close(); throw "A key named '$Name' already exists." }
        $k.CreateSubKey($Name).Close()
        """;

    // $Sub = parent key, $Name = key to delete (with everything below it).
    private const string DeleteKeyScript = "param([string]$Hive, [string]$Sub, [string]$Name, [bool]$Write = $true)\n" + """
        if (-not $Sub -or -not $Name) { throw 'Refusing to delete a top-level key.' }

        """ + Open + """

        $k.DeleteSubKeyTree($Name)
        """;

    // RegRenameKey is the API regedit uses: a real rename that keeps permissions (no copy + delete).
    private const string RenameKeyScript = "param([string]$Hive, [string]$Sub, [string]$Name, [string]$NewName, [bool]$Write = $true)\n" + Open + """

        $e = $k.OpenSubKey($NewName, $false)
        if ($e) { $e.Close(); throw "A key named '$NewName' already exists." }
        if (-not ('RemoteHubRegistry' -as [type])) {
            Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
                public static class RemoteHubRegistry {
                    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
                    public static extern int RegRenameKey(SafeRegistryHandle key, string subKey, string newName);
                }'
        }
        $r = [RemoteHubRegistry]::RegRenameKey($k.Handle, $Name, $NewName)
        if ($r -ne 0) { throw (New-Object ComponentModel.Win32Exception $r).Message }
        """;

    private const string ExportScript = """
        param([string]$Path)
        $ErrorActionPreference = 'Stop'
        $f = [IO.Path]::GetTempFileName()
        try {
            $out = & reg.exe export $Path $f /y 2>&1
            if ($LASTEXITCODE -ne 0) { throw ("reg export failed: " + ($out -join ' ')) }
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($f))
        } finally { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
        """;

    private static (string, object)[] Key(string canonical, params (string, object)[] more)
    {
        var (hive, sub) = Split(canonical);
        if (!HiveEnum.TryGetValue(hive, out var h)) throw new RemoteCommandException("Choose a key below one of the hives.");
        return [("Hive", h), ("Sub", sub), .. more];
    }

    public static RegListing ParseListing(string path, IEnumerable<string> lines)
    {
        var keys = new List<RegSubKey>();
        var values = new List<RegValue>();
        var utf8 = Encoding.UTF8;
        foreach (var line in lines)
        {
            if (line.StartsWith("S|") && line.Length > 4) keys.Add(new RegSubKey(line[4..], line[2] == '1'));
            else if (line.StartsWith("V|") && line.Split('|') is [_, var kind, var name, var data])
                values.Add(new RegValue(utf8.GetString(Convert.FromBase64String(name)),
                    Enum.TryParse<RegKind>(kind, out var k) ? k : RegKind.Unknown, utf8.GetString(Convert.FromBase64String(data))));
        }
        // regedit always shows the default value first, even when it isn't set.
        var def = values.FirstOrDefault(v => v.IsDefault) ?? new RegValue("", RegKind.String, "", IsSet: false);
        values.Remove(def);
        values = values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Prepend(def).ToList();
        return new RegListing(path, keys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList(), values);
    }

    /// <summary>Lists a key ("" = the hives).</summary>
    public static RegListing List(Runspace rs, string path)
    {
        var p = Normalize(path);
        if (p.Length == 0) return new RegListing("", Hives.Select(h => new RegSubKey(h, true)).ToList(), []);
        return ParseListing(p, RemoteFs.Invoke(rs, ListScript, Key(p)).Select(o => o?.ToString() ?? ""));
    }

    public static void SetValue(Runspace rs, string key, string name, RegKind kind, object data, bool createOnly) =>
        RemoteFs.Invoke(rs, SetScript, Key(key, ("Name", name), ("Kind", kind.ToString()), ("Data", data), ("CreateOnly", createOnly)));

    public static void DeleteValues(Runspace rs, string key, IEnumerable<string> names) =>
        RemoteFs.Invoke(rs, DeleteValueScript, Key(key, ("Names", names.ToArray())));

    public static void RenameValue(Runspace rs, string key, string name, string newName) =>
        RemoteFs.Invoke(rs, RenameValueScript, Key(key, ("Name", name), ("NewName", newName)));

    public static string CreateKey(Runspace rs, string parent, string name)
    {
        RemoteFs.Invoke(rs, CreateKeyScript, Key(parent, ("Name", name)));
        return Combine(parent, name);
    }

    /// <summary>Deletes a key with all its sub keys and values. Hives and their direct children are refused.</summary>
    public static void DeleteKey(Runspace rs, string key)
    {
        if (IsProtected(key)) throw new RemoteCommandException($"Refusing to delete a top-level key: {key}");
        RemoteFs.Invoke(rs, DeleteKeyScript, Key(Parent(key), ("Name", Leaf(key))));
    }

    public static string RenameKey(Runspace rs, string key, string newName)
    {
        if (IsProtected(key)) throw new RemoteCommandException($"Refusing to rename a top-level key: {key}");
        RemoteFs.Invoke(rs, RenameKeyScript, Key(Parent(key), ("Name", Leaf(key)), ("NewName", newName)));
        return Combine(Parent(key), newName);
    }

    /// <summary>The key as a .reg file (what regedit's Export writes), read from the remote computer.</summary>
    public static byte[] Export(Runspace rs, string key)
    {
        if (!key.Contains('\\')) throw new RemoteCommandException("Choose a key below the hive to export.");
        var b64 = RemoteFs.Invoke(rs, ExportScript, ("Path", key)).FirstOrDefault()?.ToString() ?? "";
        return Convert.FromBase64String(b64);
    }
}
