# C-DB / C-LOCK / C-AUD / C-BAK / C-SHARE (part 2) - RemoteHub's own data on the admin's PC (this client VM), the cases
# C-DATA.Tests.ps1 doesn't cover:
#   database problems   C-DB-06 .. C-DB-13 (no usable backup, an older backup when the newest fails, db.key missing, a
#                       newer schema, the daily check finding damage, a failed / repeated move of settings.dat, the
#                       30-day clean-up of settings.dat.migrated, an unreadable settings.dat)
#   audit log, backups  C-AUD-05 (retention in Settings -> Logs, and under the policy), C-AUD-06 (newest entries removed),
#                       C-AUD-08 (one summary entry for a save of more than 50 changes), C-BAK-04 (not a backup file)
#   startup protection  C-LOCK-05 .. C-LOCK-09 (change the password, the password rules, Exit while auto-locked, no
#                       auto-lock under a dialog, the old RDP Manager folder)
#   shared settings     C-SHARE-04 .. C-SHARE-07, C-LOCK-10 (keys.dat sealed) and C-AUD-07 (the other audit events, also
#                       as the viewer's CSV export labels them) - the SMB share \\<device ip>\rhsettings on the device VM
#                       and a second RemoteHub there (2VM)
# Needs: the client VM and the Server VM (device: SMB share, second RemoteHub for C-SHARE-04/05). No mock server.
# The database is read in the account's own session with RemoteHub's own sqlite3mc.dll exactly as C-DATA.Tests.ps1 does
# (the rhdb.cs / rhdb.ps1 helpers below are copied from there). Client VMs only. See docs/test-lab-cases.md section 10.2.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = (Get-LabVm $VmName).Os -ne 'Server'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $cfg = Get-LabConfig
    $password = $cfg.Password
    # test values of this file (not the lab password)
    $lockPw = 'Lab-Startup-Pw-81'
    $lockPw2 = 'Lab-Startup-Pw-82'
    $backupPw = 'Lab-Backup-Pw-83'
    $sharePw = 'Lab-Shared-Pw-84'
    $adminHome = "C:\Users\$($vm.ConsoleAccount)"
    $localDir = "$adminHome\AppData\Local\RemoteHub"
    $roamDir = "$adminHome\AppData\Roaming\RemoteHub"
    $script:rhTasks = @{}

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "c-data2-$name.json") -Encoding UTF8 }
    function Shot([string]$name, [string]$On = $VmName) { try { $null = Get-LabScreenshot -Name $On -Path (Join-Path $EvidenceDir "c-data2-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional, [string]$Account = 'Console', [string]$On = $VmName) {
        Invoke-LabUi -Name $On -Account $Account -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # A message box: the first of -Titles that shows up (RemoteHub's message boxes are named by their text, a few by
    # their title - both are tried), its text, then -Button in that same window ('' = leave it open).
    function Box([string[]]$Titles, [string]$Button = 'OK', [int]$TimeoutSec = 60, [string]$Account = 'Console', [string]$On = $VmName) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            foreach ($t in $Titles) {
                $m = Ui '*' -Title $t -Value list -Optional -TimeoutSec 3 -Account $Account -On $On
                if ($m.found) {
                    if ($Button) { $null = Ui $Button -Title ([WildcardPattern]::Escape($m.window)) -Optional -TimeoutSec 10 -Account $Account -On $On }
                    return "$($m.window)`n$(@($m.items) -join "`n")"
                }
            }
        } while ((Get-Date) -lt $deadline)
        ''
    }
    # the named texts of a window, joined (error lines of RemoteHub's own windows are plain texts)
    function WinText([string]$Title, [string]$On = $VmName) { (@((Ui '*' -Title $Title -Value list -Optional -TimeoutSec 10 -On $On).items) -join "`n") }
    # each part on its own: a failing step is written down (error, screenshot, open dialogs) and the next one still runs
    function Step($Into, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $Into["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $Into["$Name-dialogs"] = @((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
        }
    }
    function Settings-Page([string]$Page, [string]$Account = 'Console', [string]$On = $VmName) {
        $null = Ui 'Settings' -Account $Account -On $On
        $null = Ui $Page -Title 'Settings' -Account $Account -On $On
    }
    function Start-Client([string]$Account = 'Console', [string]$On = $VmName) { $script:rhTasks["$On|$Account"] = Start-LabClient -Name $On -Account $Account }
    # started without waiting for the main window (an unlock window, a message box or a password prompt may come first)
    function Start-Rh([string]$Account = 'Console', [string]$On = $VmName) {
        $script:rhTasks["$On|$Account"] = Start-LabInSession -Name $On -Account $Account -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' }
    }
    function Wait-Main([string]$Account = 'Console', [string]$On = $VmName, [int]$TimeoutSec = 90) {
        $r = Ui 'SearchBox' -Value exists -Optional -TimeoutSec $TimeoutSec -Account $Account -On $On
        if ($r.found) { $null = Ui 'NewButton' -Value fit -Optional -TimeoutSec 10 -Account $Account -On $On }
        [bool]$r.found
    }
    # RemoteHub ended by itself (Cancel at a start-up question, Exit while locked): true when no process is left
    function Wait-Gone([int]$TimeoutSec = 30, [string]$On = $VmName) {
        Invoke-LabGuest -Name $On -ArgumentList $TimeoutSec -ScriptBlock {
            param($t)
            $deadline = (Get-Date).AddSeconds($t)
            while (@(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
            @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count -eq 0
        }
    }
    # closed like the user does (so the audit log and the database are written to the end); a connected session asks
    # "Disconnect and exit?" first. Killed only when it doesn't end.
    function Close-Rh([string]$Account = 'Console', [string]$On = $VmName) {
        try { $null = Invoke-LabInSession -Name $On -Account $Account -TimeoutSec 60 -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() } } } catch { }
        try { $null = Ui 'Exit' -Title '?*' -Optional -TimeoutSec 5 -Account $Account -On $On } catch { }
        $gone = Invoke-LabGuest -Name $On -ScriptBlock {
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            $left = @(Get-Process RemoteHub -ErrorAction SilentlyContinue)
            $left | Stop-Process -Force -ErrorAction SilentlyContinue
            $left.Count -eq 0
        }
        $t = $script:rhTasks["$On|$Account"]
        if ($t) { try { Stop-LabInSession -Name $On -Task $t } catch { }; $script:rhTasks.Remove("$On|$Account") }
        $gone
    }
    function Clear-RhData {
        Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
            param($l, $r)
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Seconds 1
            Remove-Item $l, $r -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    # Windows' Save As / Open dialog: no usable UIA patterns, so the path is typed at the console keyboard into the file
    # name box (focused when the dialog opens) and Enter pressed
    function FileDialog([string]$Path, [string]$Title = 'Save As', [string]$Account = 'Console') {
        $null = Ui '*' -Title $Title -Value exists -Optional -TimeoutSec 20 -Account $Account
        Start-Sleep -Seconds 2
        $null = Ui 'File name:' -Title $Title -Value realclick -Optional -TimeoutSec 5 -Account $Account
        Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x41
        Send-LabText -Name $VmName -Text $Path
        Start-Sleep -Milliseconds 500
        Send-LabKey -Name $VmName -Key Enter
        Start-Sleep -Seconds 2
    }
    # code in the account's session with the database helpers loaded ($A = -Data); answers its output (JSON round trip)
    function InSession([string]$Code, [hashtable]$Data = @{}, [string]$Account = 'Console', [int]$TimeoutSec = 300) {
        $out = "C:\ProgramData\RemoteHubLab\session\c-data2-$([guid]::NewGuid().ToString('N')).json"
        $json = (ConvertTo-Json -InputObject $Data -Depth 6 -Compress) -replace "'", "''"
        $text = ". 'C:\ProgramData\RemoteHubLab\rhdb.ps1'`n`$A = '$json' | ConvertFrom-Json`n`$r = & {`n$Code`n}`n" +
            "ConvertTo-Json -InputObject `$r -Depth 10 -Compress | Set-Content -LiteralPath '$out' -Encoding UTF8"
        $raw = Invoke-LabInSession -Name $VmName -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($text))
        $answer = Invoke-LabGuest -Name $VmName -ArgumentList $out -ScriptBlock { param($f) if (Test-Path $f) { Get-Content -LiteralPath $f -Raw; Remove-Item -LiteralPath $f } }
        if (-not $answer) { throw "session code gave no answer: $raw" }
        # Windows PowerShell's ConvertFrom-Json hands a JSON array on as one object: written from a variable, its
        # items come one by one (so @(InSession ...) is the list itself)
        $o = $answer | ConvertFrom-Json
        $o
    }
    # SQL on Admin's database with its key (RemoteHub closed); -Lock: the startup password while the protection is on
    function Sql([string[]]$Statements, [string]$Lock) {
        $code = '$app = $null; if ($A.lock) { $app = [byte[]](Get-RhAppKey (Join-Path $env:APPDATA "RemoteHub\lock.json") $A.lock) }' + "`n" +
            '$k = Get-RhKeys (Join-Path $env:LOCALAPPDATA "RemoteHub\db.key") $app' + "`n" +
            '@(Invoke-RhSql (Join-Path $env:LOCALAPPDATA "RemoteHub\remotehub.db") $k.Db ([string[]]@($A.sql)))'
        InSession $code @{ sql = @($Statements); lock = $Lock }
    }
    # the database as stored: nodes (with their path of names), saved credentials, passwords as SHA-256, audit, meta
    # (a failed read is answered as an empty dump with the error, so the next steps still run)
    function Dump([string]$Account = 'Console', [string]$Lock) {
        try {
            if ($Lock) { InSession 'Get-RhDump -LockFile (Join-Path $env:APPDATA "RemoteHub\lock.json") -Password $A.pw' @{ pw = $Lock } -Account $Account }
            else { InSession 'Get-RhDump' -Account $Account }
        }
        catch { [pscustomobject]@{ error = "$_"; nowMs = 0; nodes = @(); credentials = @(); audit = @(); meta = @() } }
    }
    function Backups { @(Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.LastWriteTimeUtc.ToString('o'))" } }) }
    function Policy([string]$Name, $Value) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Name, $Value -ScriptBlock {
            param($n, $v)
            $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
            if ($null -eq $v) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name $n -Value $v -Type DWord }
        }
    }
    # one byte of a file inverted (the database's first page: RemoteHub reads it when opening)
    function Flip([string]$Path, [int]$Offset = 2048) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Path, $Offset -ScriptBlock {
            param($f, $o) $b = [IO.File]::ReadAllBytes($f); $b[$o] = [byte]($b[$o] -bxor 0xFF); [IO.File]::WriteAllBytes($f, $b); $b.Length
        }
    }
    # what lies in %LOCALAPPDATA%\RemoteHub: names (folders with a trailing \ and their files), the database's SHA-256
    function Snapshot {
        Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
            param($l)
            [pscustomobject]@{
                names = @(Get-ChildItem $l -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($l.Length + 1) + $(if ($_.PSIsContainer) { '\' }) } | Sort-Object)
                dbSha = $(if (Test-Path "$l\remotehub.db") { (Get-FileHash "$l\remotehub.db" -Algorithm SHA256).Hash })
                keySha = $(if (Test-Path "$l\db.key") { (Get-FileHash "$l\db.key" -Algorithm SHA256).Hash })
            }
        }
    }
    # a 0.13 settings.dat ("RDPM" 0x01 + DPAPI over the AppData JSON): -Items as given, -Raw bytes instead (unreadable)
    function New-LegacyFile([object[]]$Items, [switch]$Garbage) {
        if ($Garbage) {
            return Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock {
                param($r) $null = New-Item -ItemType Directory -Path $r -Force
                $b = New-Object byte[] 96; (New-Object Random 7).NextBytes($b)
                [IO.File]::WriteAllBytes("$r\settings.dat", [byte[]](@(82, 68, 80, 77, 1) + @($b))); (Get-Item "$r\settings.dat").Length
            }
        }
        $json = ([ordered]@{ Version = 1; SavedCredentials = @(); Items = @($Items) } | ConvertTo-Json -Depth 10 -Compress)
        InSession 'New-RhLegacySettings $A.json' @{ json = $json }
    }
    function Legacy-Connection([string]$Name, [string]$HostName) { [ordered]@{ '$type' = 'connection'; Id = [guid]::NewGuid().ToString(); Name = $Name; Host = $HostName; Port = 3389; InheritCredentials = $true } }
    function Legacy-Folder([string]$Name, [object[]]$Children) { [ordered]@{ '$type' = 'folder'; Id = [guid]::NewGuid().ToString(); Name = $Name; InheritCredentials = $true; Children = @($Children) } }
    # RemoteHub's diagnostic log (%APPDATA%\RemoteHub\logs\remotehub.log)
    function DiagLog { Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Get-Content "$r\logs\remotehub.log" -Raw -ErrorAction SilentlyContinue } }
    function Import-Backup([string]$File, [string]$Pw, [string]$Account = 'Console') {
        Settings-Page 'Security & storage' -Account $Account
        # closes the settings window first, then Windows' Open dialog
        $null = Ui 'Import backup?' -Title 'Settings' -Account $Account
        FileDialog $File -Title 'Open' -Account $Account
        $null = Ui 'type:Edit' -Title 'Unlock backup' -Value "set:$Pw" -Account $Account
        $null = Ui 'Import' -Title 'Unlock backup' -Account $Account
    }
    # Startup protection is off: fill the two password boxes and click "Turn on" (C-DATA's calibrated pattern). A
    # Windows Hello box, when it's offered and on, is unticked first (the lab VM usually has no Hello, so it's disabled).
    function Protect-On([string]$Pw, [string]$Repeat = $Pw) {
        $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$Pw"
        $null = Ui 'type:Edit#2' -Title 'Startup protection' -Value "set:$Repeat"
        $hello = Ui 'Also unlock with Windows Hello*' -Title 'Startup protection' -Value exists -Optional -TimeoutSec 5
        if ($hello.found -and $hello.enabled) { $null = Ui 'Also unlock with Windows Hello*' -Title 'Startup protection' -Value uncheck -Optional }
        $null = Ui 'Turn on' -Title 'Startup protection'
    }
    # Startup protection is on: fill the current + new + repeat boxes and click "Change password".
    function Protect-Change([string]$Current, [string]$New, [string]$Repeat = $New) {
        $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$Current"
        $null = Ui 'type:Edit#2' -Title 'Startup protection' -Value "set:$New"
        $null = Ui 'type:Edit#3' -Title 'Startup protection' -Value "set:$Repeat"
        $null = Ui 'Change password' -Title 'Startup protection'
    }
    function Protect-Off([string]$Current) {
        $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$Current"
        $null = Ui 'Turn off startup protection' -Title 'Startup protection'
        Box -Titles 'RemoteHub will open without*', 'Turn off startup protection' -Button 'Turn off' -TimeoutSec 30
    }
    function Wait-LockFile([bool]$Present, [int]$TimeoutSec = 60) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do { Start-Sleep -Seconds 3; $now = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Test-Path "$r\lock.json" } } until ($now -eq $Present -or (Get-Date) -gt $deadline)
        $now -eq $Present
    }
    function Get-SharedHash { Invoke-LabGuest -Name $device.Name -ScriptBlock { $f = 'C:\rhsettings\RemoteHub.settings'; if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } } }
    function Head([string]$Path, [int]$Count = 4) { Invoke-LabGuest -Name $VmName -ArgumentList $Path, $Count -ScriptBlock { param($f, $n) if (Test-Path $f) { @([IO.File]::ReadAllBytes($f)[0..($n - 1)] | ForEach-Object { [int]$_ }) -join ',' } } }

    # RemoteHub's database from Windows PowerShell: sqlite3mc.dll of the installed RemoteHub (P/Invoke), DPAPI, the
    # startup lock (PBKDF2-SHA256 + AES-GCM through BCrypt) and the per-password AES-GCM (SecretBox)
    $rhdbCs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RhLab
{
    public class SqlError : Exception
    {
        public int Code;
        public SqlError(int code, string message) : base(message) { Code = code; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AuthInfo
    {
        public int cbSize; public int dwInfoVersion;
        public IntPtr pbNonce; public int cbNonce;
        public IntPtr pbAuthData; public int cbAuthData;
        public IntPtr pbTag; public int cbTag;
        public IntPtr pbMacContext; public int cbMacContext;
        public int cbAAD; public long cbData; public int dwFlags;
    }

    internal static class Gcm
    {
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string id, string impl, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int size, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objSize, byte[] secret, int secretSize, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, byte[] iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipher, byte[] tag, byte[] aad)
        {
            IntPtr alg;
            int st = BCryptOpenAlgorithmProvider(out alg, "AES", null, 0);
            if (st != 0) throw new CryptographicException("BCryptOpenAlgorithmProvider 0x" + st.ToString("X8"));
            GCHandle hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            GCHandle ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
            GCHandle ha = GCHandle.Alloc(aad ?? new byte[0], GCHandleType.Pinned);
            try
            {
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                st = BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0);
                if (st != 0) throw new CryptographicException("BCryptSetProperty 0x" + st.ToString("X8"));
                IntPtr k;
                st = BCryptGenerateSymmetricKey(alg, out k, IntPtr.Zero, 0, key, key.Length, 0);
                if (st != 0) throw new CryptographicException("BCryptGenerateSymmetricKey 0x" + st.ToString("X8"));
                try
                {
                    AuthInfo info = new AuthInfo();
                    info.cbSize = Marshal.SizeOf(typeof(AuthInfo));
                    info.dwInfoVersion = 1;
                    info.pbNonce = hn.AddrOfPinnedObject(); info.cbNonce = nonce.Length;
                    info.pbTag = ht.AddrOfPinnedObject(); info.cbTag = tag.Length;
                    if (aad != null && aad.Length > 0) { info.pbAuthData = ha.AddrOfPinnedObject(); info.cbAuthData = aad.Length; }
                    byte[] plain = new byte[cipher.Length];
                    int n;
                    st = BCryptDecrypt(k, cipher, cipher.Length, ref info, null, 0, plain, plain.Length, out n, 0);
                    if (st != 0) throw new CryptographicException("AES-GCM: the data doesn't match its key or tag (0x" + st.ToString("X8") + ")");
                    return plain;
                }
                finally { BCryptDestroyKey(k); }
            }
            finally { hn.Free(); ht.Free(); ha.Free(); BCryptCloseAlgorithmProvider(alg, 0); }
        }
    }

    public static class Db
    {
        const string L = "sqlite3mc.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_key(IntPtr db, byte[] key, int n);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);

        static bool loaded;

        public static void Load(string dll)
        {
            if (loaded) return;
            if (LoadLibraryW(dll) == IntPtr.Zero) throw new Exception("LoadLibrary failed for " + dll + ": " + Marshal.GetLastWin32Error());
            loaded = true;
        }

        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        static string Utf8Z(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            List<byte> bytes = new List<byte>();
            for (int i = 0; ; i++) { byte b = Marshal.ReadByte(p, i); if (b == 0) break; bytes.Add(b); }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        static void Check(IntPtr db, int rc)
        {
            if (rc != 0 && rc != 100 && rc != 101) throw new SqlError(rc, Utf8Z(sqlite3_errmsg(db)));
        }

        // As RemoteHub opens it (LocalDb.Connect): cipher chacha20, kdf_iter 1, the key as bytes, then page 1 read.
        // key null = no key at all (a plain SQLite open).
        public static IntPtr Open(string path, byte[] key)
        {
            IntPtr db;
            int rc = sqlite3_open_v2(Z(path), out db, 2, IntPtr.Zero);
            if (rc != 0) { string m = Utf8Z(sqlite3_errmsg(db)); sqlite3_close_v2(db); throw new SqlError(rc, m); }
            try
            {
                if (key != null)
                {
                    Query(db, "PRAGMA cipher = 'chacha20'");
                    Query(db, "PRAGMA kdf_iter = 1");
                    Check(db, sqlite3_key(db, key, key.Length));
                }
                Query(db, "SELECT count(*) FROM sqlite_master");
                return db;
            }
            catch { sqlite3_close_v2(db); throw; }
        }

        public static void Close(IntPtr db) { if (db != IntPtr.Zero) sqlite3_close_v2(db); }

        public static List<Dictionary<string, object>> Query(IntPtr db, string sql)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            byte[] s = Z(sql);
            IntPtr st;
            Check(db, sqlite3_prepare_v2(db, s, s.Length, out st, IntPtr.Zero));
            try
            {
                while (true)
                {
                    int rc = sqlite3_step(st);
                    if (rc == 101) break;
                    if (rc != 100) Check(db, rc);
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    int n = sqlite3_column_count(st);
                    for (int i = 0; i < n; i++)
                    {
                        string name = Utf8Z(sqlite3_column_name(st, i));
                        int t = sqlite3_column_type(st, i);
                        object v = null;
                        if (t == 1) v = sqlite3_column_int64(st, i);
                        else if (t == 2) v = sqlite3_column_double(st, i);
                        else if (t == 3 || t == 4)
                        {
                            IntPtr p = t == 3 ? sqlite3_column_text(st, i) : sqlite3_column_blob(st, i);
                            int len = sqlite3_column_bytes(st, i);
                            byte[] b = new byte[len];
                            if (len > 0) Marshal.Copy(p, b, 0, len);
                            if (t == 3) v = Encoding.UTF8.GetString(b); else v = b;
                        }
                        row[name] = v;
                    }
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(st); }
            return rows;
        }

        // nonce (12) | tag (16) | cipher: AppLock.Seal, and SecretBox with its place as associated data
        public static byte[] OpenSealed(byte[] key, byte[] data, byte[] aad)
        {
            if (data.Length < 28) throw new InvalidDataException("sealed data too short");
            byte[] nonce = new byte[12], tag = new byte[16], cipher = new byte[data.Length - 28];
            Array.Copy(data, 0, nonce, 0, 12);
            Array.Copy(data, 12, tag, 0, 16);
            Array.Copy(data, 28, cipher, 0, cipher.Length);
            return Gcm.Decrypt(key, nonce, cipher, tag, aad);
        }

        public static string Secret(byte[] secretKey, byte[] value, string place)
        {
            return Encoding.UTF8.GetString(OpenSealed(secretKey, value, Encoding.UTF8.GetBytes(place)));
        }

        public static byte[] Pbkdf2(string password, byte[] salt, int iterations)
        {
            using (Rfc2898DeriveBytes d = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256)) return d.GetBytes(32);
        }

        // AppLock's wrapped key: { Nonce, Tag, Data } (base64), AES-GCM without associated data
        public static byte[] Unwrap(byte[] kek, string nonce, string tag, string data)
        {
            return Gcm.Decrypt(kek, Convert.FromBase64String(nonce), Convert.FromBase64String(data), Convert.FromBase64String(tag), null);
        }

        // db.key: "RHDK" 1 + DPAPI (entropy RemoteHub.Database.v1), or "RHDK" 2 + DPAPI over the startup lock.
        // Answers the 64 bytes: database key | secret key.
        public static byte[] ReadKeyFile(string path, byte[] appKey)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 6 || b[0] != (byte)'R' || b[1] != (byte)'H' || b[2] != (byte)'D' || b[3] != (byte)'K') throw new InvalidDataException("not a RemoteHub db.key");
            byte[] inner = new byte[b.Length - 5];
            Array.Copy(b, 5, inner, 0, inner.Length);
            byte[] plain = ProtectedData.Unprotect(inner, Encoding.UTF8.GetBytes("RemoteHub.Database.v1"), DataProtectionScope.CurrentUser);
            if (b[4] == 2)
            {
                if (appKey == null) throw new InvalidOperationException("db.key is sealed with the startup protection");
                plain = OpenSealed(appKey, plain, null);
            }
            if (plain.Length != 64) throw new InvalidDataException("db.key holds " + plain.Length + " bytes");
            return plain;
        }
    }
}
'@

    $rhdbPs = @'
# RemoteHub's local database read the way RemoteHub opens it (lab helper of lab\tests\C-DATA.Tests.ps1)
$ErrorActionPreference = 'Stop'
if (-not ('RhLab.Db' -as [type])) { Add-Type -Path 'C:\ProgramData\RemoteHubLab\rhdb.cs' -ReferencedAssemblies System.Security }
$script:RhDll = Get-ChildItem 'C:\Program Files\RemoteHub' -Recurse -Filter 'sqlite3mc.dll' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if ($script:RhDll) { [RhLab.Db]::Load($script:RhDll) }

function Get-RhHash([string]$s) {
    if ($null -eq $s) { return $null }
    -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString('x2') })
}

# the startup lock's key from lock.json and the password
function Get-RhAppKey([string]$LockFile, [string]$Password) {
    $l = Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
    $kek = [RhLab.Db]::Pbkdf2($Password, [Convert]::FromBase64String($l.Salt), [int]$l.Iterations)
    , [RhLab.Db]::Unwrap($kek, $l.Password.Nonce, $l.Password.Tag, $l.Password.Data)
}

function Get-RhKeys([string]$KeyFile, [byte[]]$AppKey) {
    $k = [RhLab.Db]::ReadKeyFile($KeyFile, $AppKey)
    [pscustomobject]@{ Db = [byte[]]$k[0..31]; Secret = [byte[]]$k[32..63] }
}

function Invoke-RhSql([string]$Path, [byte[]]$Key, [string[]]$Sql) {
    $db = [RhLab.Db]::Open($Path, $Key)
    try {
        foreach ($s in $Sql) {
            foreach ($row in [RhLab.Db]::Query($db, $s)) {
                $o = [ordered]@{}
                foreach ($kv in $row.GetEnumerator()) { $o[$kv.Key] = $kv.Value }
                [pscustomobject]$o
            }
        }
    }
    finally { [RhLab.Db]::Close($db) }
}

# ok, or the SQLite error code and message (26 = "file is not a database")
function Test-RhSql([string]$Path, [byte[]]$Key, [string]$Sql) {
    try { $null = Invoke-RhSql -Path $Path -Key $Key -Sql $Sql; [pscustomobject]@{ ok = $true; code = 0; error = '' } }
    catch {
        $e = $_.Exception
        while ($e -and -not ($e -is [RhLab.SqlError]) -and $e.InnerException) { $e = $e.InnerException }
        [pscustomobject]@{ ok = $false; code = $(if ($e -is [RhLab.SqlError]) { $e.Code } else { -1 }); error = "$($e.GetType().Name): $($e.Message)" }
    }
}

# what the database holds; passwords only as SHA-256
function Get-RhDump([string]$Dir = (Join-Path $env:LOCALAPPDATA 'RemoteHub'), [string]$LockFile, [string]$Password, [string]$KeyFile) {
    $app = $null
    if ($LockFile) { $app = [byte[]](Get-RhAppKey $LockFile $Password) }
    if (-not $KeyFile) { $KeyFile = Join-Path $Dir 'db.key' }
    $k = Get-RhKeys $KeyFile $app
    $p = Join-Path $Dir 'remotehub.db'
    $secrets = @{}
    foreach ($s in @(Invoke-RhSql $p $k.Db 'SELECT owner_kind, owner_id, path, value FROM secrets')) {
        $v = [RhLab.Db]::Secret($k.Secret, [byte[]]$s.value, "$($s.owner_kind)|$($s.owner_id)|$($s.path)")
        $key = "$($s.owner_kind)|$($s.owner_id)"
        if (-not $secrets.ContainsKey($key)) { $secrets[$key] = @() }
        $secrets[$key] += "$($s.path)=$(Get-RhHash $v)"
    }
    $nodes = @(Invoke-RhSql $p $k.Db 'SELECT id, parent_id, sort, kind, name, host, data FROM nodes ORDER BY parent_id, sort')
    $byId = @{}
    foreach ($n in $nodes) { $byId[$n.id] = $n }
    $outNodes = @(foreach ($n in $nodes) {
            $names = @(); $c = $n; $guard = 0
            while ($c -and $guard++ -lt 50) { $names = @($c.name) + $names; $c = if ($c.parent_id) { $byId[$c.parent_id] } else { $null } }
            [pscustomobject]@{ id = $n.id; parent = $n.parent_id; sort = $n.sort; kind = $n.kind; name = $n.name; host = $n.host; path = ($names -join ' / '); data = $n.data
                secrets = @(@($secrets["nodes|$($n.id)"]) | Where-Object { $_ } | Sort-Object) }
        })
    $creds = @(foreach ($c in @(Invoke-RhSql $p $k.Db 'SELECT id, sort, name, data FROM credentials ORDER BY sort')) {
            [pscustomobject]@{ id = $c.id; name = $c.name; data = $c.data; secrets = @(@($secrets["credentials|$($c.id)"]) | Where-Object { $_ } | Sort-Object) }
        })
    [pscustomobject]@{
        nowMs       = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        nodes       = $outNodes
        credentials = $creds
        audit       = @(Invoke-RhSql $p $k.Db 'SELECT id, time_utc, device_id, event, connection_id, target, detail FROM audit ORDER BY id')
        meta        = @(Invoke-RhSql $p $k.Db 'SELECT key, value FROM meta')
    }
}

# a 0.13 settings.dat in %APPDATA%\RemoteHub
function New-RhLegacySettings([string]$Json, [string]$Dir = (Join-Path $env:APPDATA 'RemoteHub')) {
    $null = New-Item -ItemType Directory -Path $Dir -Force
    $p = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Json), [Text.Encoding]::UTF8.GetBytes('RdpManager.Settings.v1'), [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $f = Join-Path $Dir 'settings.dat'
    [IO.File]::WriteAllBytes($f, [byte[]](@(82, 68, 80, 77, 1) + @($p)))
    (Get-Item $f).Length
}
'@

    $device = @(Get-LabVm | Where-Object Os -eq 'Server')[0]
    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        $null = Install-LabClient -Name $VmName
        $null = Invoke-LabGuest -Name $VmName -ArgumentList $rhdbCs, $rhdbPs -ScriptBlock {
            param($cs, $ps)
            $d = 'C:\ProgramData\RemoteHubLab'; $null = New-Item -ItemType Directory -Path $d -Force
            Set-Content "$d\rhdb.cs" $cs -Encoding UTF8
            Set-Content "$d\rhdb.ps1" $ps -Encoding UTF8
            icacls "$d\rhdb.cs" /grant '*S-1-5-32-545:RX' | Out-Null
            icacls "$d\rhdb.ps1" /grant '*S-1-5-32-545:RX' | Out-Null
            # C:\lab: key copies, files to import, the CSV export (every account may write)
            $null = New-Item -ItemType Directory -Path 'C:\lab' -Force
            icacls 'C:\lab' /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        }
        # the device: the shared settings folder as an SMB share, reachable by IP
        $deviceName = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue
            $everyone = (New-Object Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([Security.Principal.NTAccount]).Value
            $p = 'C:\rhsettings'; $null = New-Item -ItemType Directory -Path $p -Force
            icacls $p /grant '*S-1-1-0:(OI)(CI)M' | Out-Null
            if (-not (Get-SmbShare -Name 'rhsettings' -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name 'rhsettings' -Path $p -FullAccess $everyone }
            $env:COMPUTERNAME
        }
    }
}

AfterAll {
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        try { Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force } } catch { }
        foreach ($t in @($script:rhTasks.Keys)) { try { Stop-LabInSession -Name ($t -split '\|')[0] -Task $script:rhTasks[$t] } catch { } }
        try { Policy 'AuditRetentionDays' $null } catch { }
        try { Stop-LabVm -Name $device.Name } catch { }
    }
}

Describe 'C-DATA2 <VmName>' -Tag Client, 'C-DB', 'C-LOCK', 'C-AUD', 'C-BAK', 'C-SHARE' -Skip:(-not $isClient) {

    Context 'database problems' {
        BeforeAll {
            $d = [ordered]@{}
            function RoamFiles { @(Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Get-ChildItem $r -Force -File -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object }) }
            function LegacySha { Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) if (Test-Path "$r\settings.dat") { (Get-FileHash "$r\settings.dat" -Algorithm SHA256).Hash } } }

            # C-DB-11: moving settings.dat fails - an emptied database that refuses new nodes (a lab trigger), then a
            # settings.dat: RemoteHub deletes the database, makes a new one and keeps working from settings.dat
            Step $d 'db11' {
                Clear-RhData
                Start-Client
                $null = Close-Rh
                $d.trap = @(Sql @('DELETE FROM secrets', 'DELETE FROM nodes', 'DELETE FROM credentials', 'DELETE FROM settings',
                        "CREATE TRIGGER lab_refuse_nodes BEFORE INSERT ON nodes BEGIN SELECT RAISE(ABORT, 'lab: no new nodes'); END",
                        "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name = 'lab_refuse_nodes'") | ForEach-Object name)
                $null = New-LegacyFile @((Legacy-Folder 'Legacy folder M' @(Legacy-Connection 'Legacy server M' 'legacym.lab.test')), (Legacy-Connection 'Legacy direct M' '10.99.0.15'))
                Start-Client
                $d.failShown = Ui 'Legacy direct M' -Value exists -Optional -TimeoutSec 15
                Shot 'db11-fail'
                $null = Close-Rh
                $d.failFiles = RoamFiles
                $d.failDump = Dump
                $d.failTriggers = @(Sql "SELECT name FROM sqlite_master WHERE type = 'trigger'" | ForEach-Object name)
                $d.failLog = DiagLog
                # the next start: the (new, empty) database takes settings.dat
                Start-Client
                $null = Close-Rh
                $d.movedFiles = RoamFiles
            }
            # C-DB-12: a .bak beside it (as a save of settings.dat leaves one); a start inside the 30 days keeps both,
            # then the move dated 31 days back: the next start deletes both
            Step $d 'db12' {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) if ((Test-Path "$r\settings.dat.migrated") -and -not (Test-Path "$r\settings.dat.bak")) { Copy-Item "$r\settings.dat.migrated" "$r\settings.dat.bak" } }
                Start-Client; Start-Sleep -Seconds 5; $null = Close-Rh
                $d.keep30 = RoamFiles
                $iso = [DateTime]::UtcNow.AddDays(-31).ToString('o')
                $d.meta = @(Sql @("UPDATE meta SET value = '$iso' WHERE key = 'legacy_moved_utc'", "SELECT key, value FROM meta WHERE key = 'legacy_moved_utc'"))
                Start-Client; Start-Sleep -Seconds 5; $null = Close-Rh
                $d.after30 = RoamFiles
            }
            # C-DB-11: settings.dat appears again (an older version ran in between) next to a database with data
            Step $d 'db11z' {
                $d.beforeZ = Dump
                $null = New-LegacyFile @(Legacy-Connection 'Legacy direct Z' '10.99.0.26')
                $d.zSha = LegacySha
                Start-Client
                $d.zShown = Ui 'Legacy direct Z' -Value exists -Optional -TimeoutSec 5
                $d.mShown = Ui 'Legacy direct M' -Value exists -Optional -TimeoutSec 10
                $null = Close-Rh
                $d.zFiles = RoamFiles
                $d.zShaAfter = LegacySha
                $d.afterZ = Dump
                $d.zLog = DiagLog
            }
            Save 'db11-12' @{ d = $d; failNodes = $d.failDump.nodes; beforeZ = $d.beforeZ.nodes; afterZ = $d.afterZ.nodes }

            # C-DB-13: settings.dat that can't be decrypted ("RDPM" 0x01 and random bytes), no database yet
            Step $d 'db13' {
                Clear-RhData
                $d.garbage = New-LegacyFile -Garbage
                Start-Rh
                $d.db13msg = Box -Titles 'RemoteHub' -Button 'OK' -TimeoutSec 90
                $d.db13main = Wait-Main
                Shot 'db13'
                $null = Close-Rh
                $d.db13files = RoamFiles
                $d.db13dump = Dump
            }
            Save 'db13' @{ msg = $d.db13msg; main = $d.db13main; files = $d.db13files; nodes = $d.db13dump.nodes; error = $d.db13dump.error; stepError = $d['db13-error'] }

            # a database with a connection and its automatic backup
            Step $d 'base' {
                Clear-RhData
                Start-Client
                New-LabClientConnection -Name $VmName -Display 'DbErr' -TargetHost 'dberr.lab.test'
                Start-Sleep -Seconds 10
                $null = Close-Rh
                $d.base = Snapshot
            }
            # C-DB-08: db.key missing (put back afterwards)
            Step $d 'db08' {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) Move-Item "$l\db.key" 'C:\lab\db.key.keep' -Force }
                try {
                    Start-Rh
                    $d.db08msg = Box -Titles 'RemoteHub' -Button 'Cancel' -TimeoutSec 90
                    $d.db08gone = Wait-Gone
                    $null = Close-Rh
                    $d.db08after = Snapshot
                }
                finally { $null = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) if (Test-Path 'C:\lab\db.key.keep') { Move-Item 'C:\lab\db.key.keep' "$l\db.key" -Force } } }
            }
            # C-DB-09: schema 2 (user_version), set back afterwards
            Step $d 'db09' {
                $d.v2 = @(Sql @('PRAGMA user_version = 2', 'PRAGMA user_version') | ForEach-Object user_version)
                try {
                    Start-Rh
                    $d.db09msg = Box -Titles 'RemoteHub' -Button 'Cancel' -TimeoutSec 90
                    $d.db09gone = Wait-Gone
                    $null = Close-Rh
                }
                finally { $d.v1 = @(Sql @('PRAGMA user_version = 1', 'PRAGMA user_version') | ForEach-Object user_version) }
            }
            Save 'db08-09' @{ db08 = $d.db08msg; gone08 = $d.db08gone; after08 = $d.db08after; base = $d.base; v2 = $d.v2; db09 = $d.db09msg; gone09 = $d.db09gone; v1 = $d.v1 }

            # C-DB-10: the backup is due (dated 2 days back) and the database damaged in a way that still opens and
            # loads: a lab table's index pointed at the table's own root page (every page stays correctly encrypted;
            # PRAGMA quick_check reports the page used twice and the index's page never used)
            Step $d 'db10' {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
                    param($l) Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' | ForEach-Object { $_.LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-2) }
                }
                $d.bkBefore10 = Backups
                $null = Sql @('CREATE TABLE lab_t (x INTEGER)', 'INSERT INTO lab_t VALUES (1), (2), (3)', 'CREATE INDEX lab_ix ON lab_t (x)')
                $root = @(Sql "SELECT rootpage FROM sqlite_master WHERE name = 'lab_t'")[0].rootpage
                $null = Sql @('PRAGMA writable_schema = ON', "UPDATE sqlite_master SET rootpage = $root WHERE name = 'lab_ix'", 'PRAGMA writable_schema = OFF')
                $d.quick = @(Sql 'PRAGMA quick_check' | ForEach-Object quick_check)
                Start-Client
                Start-Sleep -Seconds 30
                $null = Close-Rh
                $d.bkAfter10 = Backups
                $d.db10dump = Dump
            }
            Save 'db10' @{ before = $d.bkBefore10; after = $d.bkAfter10; quick = $d.quick; audit = $d.db10dump.audit; stepError = $d['db10-error'] }

            # C-DB-07: two backups - the newest damaged, an older good one - and the database damaged
            Step $d 'db07' {
                Clear-RhData
                Start-Client
                New-LabClientConnection -Name $VmName -Display 'DbErr' -TargetHost 'dberr.lab.test'
                Start-Sleep -Seconds 10
                $null = Close-Rh
                $d.db07setup = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
                    param($l)
                    $newest = Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
                    $older = Join-Path "$l\backup" 'remotehub-20000101-000000.db'
                    Copy-Item $newest.FullName $older -Force
                    (Get-Item $older).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(-3)
                    $b = [IO.File]::ReadAllBytes($newest.FullName); $b[2048] = [byte]($b[2048] -bxor 0xFF); [IO.File]::WriteAllBytes($newest.FullName, $b)
                    @(Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' | Sort-Object LastWriteTimeUtc -Descending | ForEach-Object Name)
                }
                $null = Flip "$localDir\remotehub.db"
                # the backups' times as RemoteHub shows them ("g" in the account's own culture), newest first
                $d.db07times = @(InSession '@(Get-ChildItem (Join-Path $env:LOCALAPPDATA "RemoteHub\backup") -Filter "remotehub-*.db" | Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { $_.LastWriteTimeUtc.ToLocalTime().ToString("g") })')
                Start-Rh
                $d.db07msg1 = Box -Titles 'RemoteHub' -Button 'Yes' -TimeoutSec 90
                Start-Sleep -Seconds 3
                $d.db07msg2 = Box -Titles 'RemoteHub' -Button 'Yes' -TimeoutSec 60
                $d.db07main = Wait-Main
                Shot 'db07'
                $null = Close-Rh
                $d.db07files = Snapshot
                $d.db07dump = Dump
            }
            Save 'db07' @{ setup = $d.db07setup; times = $d.db07times; msg1 = $d.db07msg1; msg2 = $d.db07msg2; main = $d.db07main; files = $d.db07files; audit = $d.db07dump.audit; stepError = $d['db07-error'] }

            # C-DB-06: the database damaged, backup\ without a backup in it (a note file shows where the folder goes)
            Step $d 'db06' {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
                    param($l) $null = New-Item -ItemType Directory -Path "$l\backup" -Force; Remove-Item "$l\backup\remotehub-*.db" -Force; Set-Content "$l\backup\lab-note.txt" 'c-data2 backup folder' -Encoding ASCII
                }
                $null = Flip "$localDir\remotehub.db"
                $d.db06before = Snapshot
                Start-Rh
                $d.db06cancel = Box -Titles 'RemoteHub' -Button 'Cancel' -TimeoutSec 90
                $d.db06gone = Wait-Gone
                $null = Close-Rh
                $d.db06afterCancel = Snapshot
                Start-Rh
                $d.db06ok = Box -Titles 'RemoteHub' -Button 'OK' -TimeoutSec 90
                $d.db06main = Wait-Main
                $d.db06conn = Ui 'DbErr' -Value exists -Optional -TimeoutSec 5
                Shot 'db06'
                $null = Close-Rh
                $d.db06after = Snapshot
            }
            Save 'db06' @{ before = $d.db06before; cancel = $d.db06cancel; gone = $d.db06gone; afterCancel = $d.db06afterCancel; ok = $d.db06ok; main = $d.db06main; conn = $d.db06conn; after = $d.db06after; stepError = $d['db06-error'] }
        }

        It 'C-DB-06 unreadable database, no usable backup: "Start with empty settings?"; Cancel ends RemoteHub with nothing changed, OK sets the files aside and starts empty' -Tag 'C-DB-06' {
            $d['db06-error'] | Should -BeNullOrEmpty
            $intro = "RemoteHub's database can't be opened:\s*The database is damaged or doesn't match its key\.\s*Start with empty settings\? The unreadable database is kept in\s*$([regex]::Escape($localDir))"
            $d.db06cancel | Should -Match $intro
            $d.db06gone | Should -BeTrue -Because 'Cancel ends RemoteHub'
            # crash\ holds RemoteHub's running marker of each start (removed again at the exit): not part of the comparison
            $keep = { param($n) @($n | Where-Object { $_ -notmatch '^crash\\' }) -join '|' }
            (& $keep $d.db06afterCancel.names) | Should -Be (& $keep $d.db06before.names) -Because 'Cancel changes nothing'
            $d.db06afterCancel.dbSha | Should -Be $d.db06before.dbSha
            $d.db06ok | Should -Match $intro
            $d.db06main | Should -BeTrue
            $d.db06conn.found | Should -BeFalse -Because 'the new database is empty'
            $new = @(@($d.db06after.names) | Where-Object { @($d.db06afterCancel.names) -notcontains $_ })
            $db = @($new | Where-Object { $_ -match '^remotehub\.db\.damaged-(\d{8}-\d{6})$' })
            $db.Count | Should -Be 1
            $stamp = $db[0] -replace '^remotehub\.db\.damaged-', ''
            $new | Should -Contain "db.key.damaged-$stamp"
            $new | Should -Contain "backup.damaged-$stamp\"
            $new | Should -Contain "backup.damaged-$stamp\lab-note.txt"
            foreach ($part in '-wal', '-shm') { if (@($d.db06before.names) -contains "remotehub.db$part") { $new | Should -Contain "remotehub.db$part.damaged-$stamp" } }
            @($d.db06after.names) | Should -Contain 'remotehub.db'
            @($d.db06after.names) | Should -Contain 'db.key'
            $d.db06after.dbSha | Should -Not -Be $d.db06before.dbSha
            $d.db06after.keySha | Should -Not -Be $d.db06before.keySha -Because 'a new database gets a new key'
        }

        It 'C-DB-07 the newest backup doesn''t open either: the offer comes again for the next older one, which is restored' -Tag 'C-DB-07' {
            $d['db07-error'] | Should -BeNullOrEmpty
            @($d.db07setup).Count | Should -Be 2
            @($d.db07times).Count | Should -Be 2
            $d.db07msg1 | Should -Match "Restore the automatic backup of $([regex]::Escape($d.db07times[0]))\?"
            $d.db07msg2 | Should -Match "RemoteHub's database can't be opened:\s*The database is damaged or doesn't match its key\."
            $d.db07msg2 | Should -Match "Restore the automatic backup of $([regex]::Escape($d.db07times[1]))\?"
            $d.db07main | Should -BeTrue
            @($d.db07dump.audit | Where-Object event -eq 'storage.restored' | ForEach-Object detail) | Should -Contain "Restored the backup of $($d.db07times[1])."
        }

        It 'C-DB-08 db.key missing: reason "The database key file (db.key) is missing."; Cancel changes nothing' -Tag 'C-DB-08' {
            $d['base-error'] | Should -BeNullOrEmpty
            $d['db08-error'] | Should -BeNullOrEmpty
            $d.db08msg | Should -Match "RemoteHub's database can't be opened:\s*The database key file \(db\.key\) is missing\."
            $d.db08gone | Should -BeTrue
            $d.db08after.dbSha | Should -Be $d.base.dbSha
            @($d.db08after.names | Where-Object { $_ -like '*damaged*' }) | Should -BeNullOrEmpty
        }

        It 'C-DB-09 a database of a newer version: reason "The database was created by a newer version of RemoteHub (schema 2)."' -Tag 'C-DB-09' {
            $d['db09-error'] | Should -BeNullOrEmpty
            @($d.v2) | Should -Contain 2
            $d.db09msg | Should -Match "RemoteHub's database can't be opened:\s*The database was created by a newer version of RemoteHub \(schema 2\)\."
            $d.db09gone | Should -BeTrue
        }

        It 'C-DB-10 the daily check finds damage: no backup made, the existing ones kept, "Database check failed" in the audit log' -Tag 'C-DB-10' {
            $d['db10-error'] | Should -BeNullOrEmpty
            @($d.quick).Count | Should -BeGreaterThan 0
            (@($d.quick) -join "`n") | Should -Not -Be 'ok' -Because 'the lab damage is what PRAGMA quick_check reports'
            @($d.bkBefore10).Count | Should -BeGreaterThan 0
            (@($d.bkAfter10) -join "`n") | Should -Be (@($d.bkBefore10) -join "`n")
            $failed = @($d.db10dump.audit | Where-Object event -eq 'storage.check_failed')
            $failed.Count | Should -BeGreaterThan 0 -Because "the audit log holds the entry ($($d.db10dump.error))"
            $failed[-1].detail | Should -Be 'The database check found a problem; the existing backups were kept.'
        }

        It 'C-DB-11 moving settings.dat fails: the new database deleted, settings.dat stays in use; a settings.dat next to a database with data is ignored' -Tag 'C-DB-11' {
            $d['db11-error'] | Should -BeNullOrEmpty
            $d['db11z-error'] | Should -BeNullOrEmpty
            @($d.trap) | Should -Contain 'lab_refuse_nodes' -Because 'the lab trigger was in the database'
            $d.failShown.found | Should -BeTrue -Because 'RemoteHub works from settings.dat'
            @($d.failFiles) | Should -Contain 'settings.dat'
            @($d.failFiles) | Should -Not -Contain 'settings.dat.migrated'
            $d.failDump.error | Should -BeNullOrEmpty
            @($d.failDump.nodes).Count | Should -Be 0 -Because 'the database holds nothing of settings.dat'
            @($d.failTriggers) | Should -Not -Contain 'lab_refuse_nodes' -Because 'the database was deleted and made new'
            $d.failLog | Should -Match 'Moving settings\.dat into the database failed'
            @($d.movedFiles) | Should -Contain 'settings.dat.migrated' -Because 'the next start moved it'
            # appears again next to a database with data
            $d.zShown.found | Should -BeFalse
            $d.mShown.found | Should -BeTrue
            @($d.zFiles) | Should -Contain 'settings.dat'
            $d.zShaAfter | Should -Be $d.zSha -Because 'the file is left as it is'
            (@($d.afterZ.nodes | ForEach-Object id | Sort-Object) -join ',') | Should -Be (@($d.beforeZ.nodes | ForEach-Object id | Sort-Object) -join ',')
            $d.zLog | Should -Match 'settings\.dat found next to a database with data'
        }

        It 'C-DB-12 30 days after the move settings.dat.migrated and settings.dat.bak are deleted at start (kept before)' -Tag 'C-DB-12' {
            $d['db12-error'] | Should -BeNullOrEmpty
            @($d.keep30) | Should -Contain 'settings.dat.migrated'
            @($d.keep30) | Should -Contain 'settings.dat.bak'
            @($d.meta).Count | Should -Be 1
            @($d.after30) | Should -Not -Contain 'settings.dat.migrated'
            @($d.after30) | Should -Not -Contain 'settings.dat.bak'
        }

        It 'C-DB-13 unreadable settings.dat: a copy settings.dat.unreadable-<stamp>, the message names it, RemoteHub starts empty' -Tag 'C-DB-13' {
            # The message ends with "You can restore an exported backup via Menu -> Import." - a menu entry that doesn't
            # exist (findings table, App.xaml.cs:94); not asserted here.
            $d['db13-error'] | Should -BeNullOrEmpty
            $d.db13msg | Should -Match 'The settings file could not be read \(.+\)\.'
            $d.db13msg | Should -Match "A copy was saved to:\s*$([regex]::Escape("$roamDir\settings.dat.unreadable-"))\d{8}-\d{6}"
            @($d.db13files | Where-Object { $_ -match '^settings\.dat\.unreadable-\d{8}-\d{6}$' }).Count | Should -Be 1
            $d.db13main | Should -BeTrue
            $d.db13dump.error | Should -BeNullOrEmpty
            @($d.db13dump.nodes).Count | Should -Be 0
        }
    }

    Context 'audit log and backups' {
        BeforeAll {
            $a = [ordered]@{}
            function Verify([string]$Name) {
                Settings-Page 'Logs'
                $null = Ui 'Open audit log?' -Title 'Settings'
                $null = Ui 'Verify integrity' -Title 'Audit log' -TimeoutSec 30
                $a[$Name] = Box -Titles 'All * entries are unchanged*', '*entries were removed*', '*was changed after it was written*', 'Audit log intact', 'Audit log changed' -Button 'OK' -TimeoutSec 90
                Shot "aud06-$Name"
                $null = Ui 'Close' -Title 'Audit log' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            # C-AUD-08: a folder with 60 connections (moved in from a settings.dat), deleted in one go
            Clear-RhData
            $null = New-LegacyFile @(Legacy-Folder 'BulkFolder' @(1..60 | ForEach-Object { Legacy-Connection "Bulk $_" "bulk$_.lab.test" }))
            Start-Client
            Step $a 'bulk' {
                $null = Ui 'BulkFolder' -Value 'menu:Delete'
                $null = Ui 'Delete' -Title '?*' -TimeoutSec 15
                Start-Sleep -Seconds 5
                $a.bulkGone = Ui 'BulkFolder' -Value exists -Optional -TimeoutSec 3
            }
            New-LabClientConnection -Name $VmName -Display 'Aud1' -TargetHost 'aud1.lab.test'
            # C-AUD-05: Settings -> Logs
            Step $a 'retention' {
                Settings-Page 'Logs'
                $a.default = Ui '1 year (default)' -Title 'Settings' -Value exists -Optional -TimeoutSec 10
                $a.note = Ui 'Older entries are removed the next time RemoteHub starts.' -Title 'Settings' -Value exists -Optional -TimeoutSec 5
                $a.choices = [ordered]@{}
                foreach ($c in '90 days', '6 months', '2 years', '5 years', '10 years', '1 year (default)', '30 days') {
                    $r = Ui 'Keep entries for' -Title 'Settings' -Value "choose:$c" -Optional -TimeoutSec 10
                    $a.choices[$c] = "$($r.value)$(if ($r.PSObject.Properties['error']) { ' error: ' + $r.error })"
                }
                Shot 'aud05'
                $null = Ui 'Save' -Title 'Settings'
                Start-Sleep -Seconds 3
            }
            # C-BAK-04: a file that isn't a backup (a JSON envelope of another format)
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { Set-Content 'C:\lab\notabackup.rhbackup' '{"Format":"rdpmanager-settings","Version":1}' -Encoding ASCII }
            Step $a 'notbackup' {
                Import-Backup 'C:\lab\notabackup.rhbackup' 'Lab-Any-Pw-85'
                $a.bak04 = Box -Titles 'Not a RemoteHub backup file*', 'Import failed' -Button 'OK' -TimeoutSec 60
            }
            $null = Close-Rh
            $a.dump1 = Dump
            Save 'aud05-08' @{ a = $a; audit = $a.dump1.audit }

            # C-AUD-05 under the policy AuditRetentionDays = 400
            Policy 'AuditRetentionDays' 400
            try {
                Start-Client
                Step $a 'policy' {
                    Settings-Page 'Logs'
                    $a.policyBox = Ui 'AuditRetentionBox' -Title 'Settings' -Value exists -Optional -TimeoutSec 10
                    $a.policyItem = Ui '400 days' -Title 'Settings' -Value exists -Optional -TimeoutSec 5
                    $a.policyNote = Ui 'Set by your organisation.' -Title 'Settings' -Value exists -Optional -TimeoutSec 5
                    Shot 'aud05-policy'
                    $null = Ui 'Cancel' -Title 'Settings' -Optional
                }
                $null = Close-Rh
            }
            finally { Policy 'AuditRetentionDays' $null }

            # C-AUD-06: intact, then the newest entry deleted with the database key (the delete trigger lets it through
            # while the stored cut-off is in the future; the cut-off is put back afterwards)
            Start-Client
            Step $a 'intactRun' { Verify 'intact' }
            $null = Close-Rh
            Step $a 'cut' {
                $a.lastBefore = @(Sql 'SELECT max(id) AS id FROM audit')[0].id
                $a.cutoff = @(Sql "SELECT value FROM meta WHERE key = 'audit_cutoff'" | ForEach-Object value) | Select-Object -First 1
                $future = [DateTimeOffset]::UtcNow.AddDays(1).ToUnixTimeMilliseconds()
                $back = if ($null -ne $a.cutoff) { "UPDATE meta SET value = '$($a.cutoff)' WHERE key = 'audit_cutoff'" } else { "DELETE FROM meta WHERE key = 'audit_cutoff'" }
                $null = Sql @("INSERT INTO meta (key, value) VALUES ('audit_cutoff', '$future') ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                    'DELETE FROM audit WHERE id = (SELECT max(id) FROM audit)', $back)
                $a.lastAfter = @(Sql 'SELECT max(id) AS id FROM audit')[0].id
            }
            Start-Client
            Step $a 'removedRun' { Verify 'removed' }
            $null = Close-Rh
            Save 'aud06' @{ intact = $a.intact; removed = $a.removed; lastBefore = $a.lastBefore; lastAfter = $a.lastAfter; cutoff = $a.cutoff; errors = @($a.Keys | Where-Object { $_ -like '*-error' } | ForEach-Object { "${_}: $($a[$_])" }) }
        }

        It 'C-AUD-05 retention in Settings -> Logs: 30 days ... 10 years, "1 year (default)", the change audited; under the policy the box is off' -Tag 'C-AUD-05' {
            $a['retention-error'] | Should -BeNullOrEmpty
            $a.default.found | Should -BeTrue -Because '"1 year (default)" is selected at first'
            $a.note.found | Should -BeTrue
            foreach ($c in $a.choices.Keys) { $a.choices[$c] | Should -Be $c -Because "the choice $c is offered" }
            $changed = @($a.dump1.audit | Where-Object event -eq 'audit.retention_changed')
            $changed.Count | Should -Be 1
            $changed[0].target | Should -Be 'Audit log'
            $changed[0].detail | Should -Be 'Entries kept for 30 days (before: 1 year)'
            $a['policy-error'] | Should -BeNullOrEmpty
            $a.policyBox.found | Should -BeTrue
            $a.policyBox.enabled | Should -BeFalse
            $a.policyItem.found | Should -BeTrue
            $a.policyNote.found | Should -BeTrue
        }

        It 'C-AUD-06 Verify integrity: intact "All N entries are unchanged and complete"; the newest entry removed "The newest entries were removed: ..."' -Tag 'C-AUD-06' {
            $a['intactRun-error'] | Should -BeNullOrEmpty
            $a['cut-error'] | Should -BeNullOrEmpty
            $a.intact | Should -Match 'All \d+ entries are unchanged and complete since they were written\.'
            $a['removedRun-error'] | Should -BeNullOrEmpty
            [long]$a.lastAfter | Should -BeLessThan ([long]$a.lastBefore)
            $a.removed | Should -Match "The newest entries were removed: the log ends at entry $($a.lastAfter), but entry $($a.lastBefore) was written\."
        }

        It 'C-AUD-08 a save of more than 50 changes: one "Changed" entry "<n> items" / "<a> added, <c> changed, <d> deleted"' -Tag 'C-AUD-08' {
            $a['bulk-error'] | Should -BeNullOrEmpty
            $a.bulkGone.found | Should -BeFalse
            $sum = @($a.dump1.audit | Where-Object { $_.event -eq 'data.changed' -and $_.target -match '^\d+ items$' })
            $sum.Count | Should -Be 1
            $sum[0].target | Should -Be '61 items'
            $sum[0].detail | Should -Be '0 added, 0 changed, 61 deleted'
            @($a.dump1.audit | Where-Object { $_.event -eq 'data.deleted' -and $_.target -like 'Bulk*' }) | Should -BeNullOrEmpty
        }

        It 'C-BAK-04 not a backup file: "Import failed" - "Not a RemoteHub backup file."' -Tag 'C-BAK-04' {
            $a['notbackup-error'] | Should -BeNullOrEmpty
            $a.bak04 | Should -Match 'Import failed|Not a RemoteHub backup file'
            $a.bak04 | Should -Match 'Not a RemoteHub backup file\.'
            @($a.dump1.audit | Where-Object event -eq 'backup.imported') | Should -BeNullOrEmpty
        }
    }

    Context 'startup protection' {
        BeforeAll {
            $l = [ordered]@{}
            $rdpm = "$adminHome\AppData\Roaming\RdpManager"
            Clear-RhData
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'LockConn' -TargetHost 'lockconn.lab.test'
            # C-LOCK-09: RDP Manager's old folder (made after RemoteHub's own folder exists, so the start doesn't copy it)
            $l.legacyDir = Invoke-LabGuest -Name $VmName -ArgumentList $rdpm -ScriptBlock { param($p) $null = New-Item -ItemType Directory -Path $p -Force; Set-Content "$p\settings.dat" 'old RDP Manager settings' -Encoding ASCII; Test-Path $p }
            Step $l 'on' {
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                # the window builds itself again once it knows whether Windows Hello is there: fields filled before are lost
                Start-Sleep -Seconds 3
                $l.legacyBox = Ui 'Delete the old, unprotected copy of the RDP Manager settings*' -Title 'Startup protection' -Value exists -Optional -TimeoutSec 10
                # C-LOCK-06: the rules for a new password
                Protect-On 'short1'
                Start-Sleep -Seconds 2
                $l.ruleShort = WinText 'Startup protection'
                Protect-On 'Lab-Rule-Pw-1' 'Lab-Rule-Pw-2'
                Start-Sleep -Seconds 2
                $l.ruleMismatch = WinText 'Startup protection'
                $l.lockAfterRules = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Test-Path "$r\lock.json" }
                $l.legacyTick = Ui 'Delete the old, unprotected copy*' -Title 'Startup protection' -Value check
                Protect-On $lockPw
                $l.on = Wait-LockFile $true
                Start-Sleep -Seconds 5
                $l.onText = WinText 'Startup protection'
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $l.legacyAfter = Invoke-LabGuest -Name $VmName -ArgumentList $rdpm -ScriptBlock { param($p) Test-Path $p }
            # C-LOCK-06 (current password wrong) and C-LOCK-05 (changed)
            Step $l 'change' {
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                # the window builds itself again once it knows whether Windows Hello is there: fields filled before are lost
                Start-Sleep -Seconds 3
                Protect-Change 'Wrong-Current-Pw-1' $lockPw2
                Start-Sleep -Seconds 6
                $l.ruleCurrent = WinText 'Startup protection'
                $l.copies = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
                    param($lo, $r) Copy-Item "$r\lock.json" 'C:\lab\lock.json.before' -Force; Copy-Item "$lo\db.key" 'C:\lab\db.key.before' -Force; @(Get-ChildItem 'C:\lab' -Filter '*.before' | ForEach-Object Name)
                }
                Protect-Change $lockPw $lockPw2
                $l.changed = Box -Titles 'The startup password was changed*' -Button 'OK' -TimeoutSec 90
                if (-not $l.changed) {
                    # the message box may carry its title instead: it lies inside the protection window then
                    $l.changed = WinText 'Startup protection'
                    $null = Ui 'OK' -Title 'Startup protection' -Optional -TimeoutSec 5
                }
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $null = Close-Rh
            $l.keyHead = Head "$localDir\db.key" 5
            $l.newDump = Dump -Lock $lockPw2
            # the old lock.json with the old password: its key opens neither the new db.key nor (with the old db.key) the database
            $l.oldLock = InSession 'try { $app = [byte[]](Get-RhAppKey "C:\lab\lock.json.before" $A.pw); $null = Get-RhKeys (Join-Path $env:LOCALAPPDATA "RemoteHub\db.key") $app; [pscustomobject]@{ ok = $true; error = "" } } catch { [pscustomobject]@{ ok = $false; error = "$_" } }' @{ pw = $lockPw }
            $l.oldKey = InSession 'try { $app = [byte[]](Get-RhAppKey "C:\lab\lock.json.before" $A.pw); $k = Get-RhKeys "C:\lab\db.key.before" $app; Test-RhSql (Join-Path $env:LOCALAPPDATA "RemoteHub\remotehub.db") $k.Db "SELECT count(*) FROM nodes" } catch { [pscustomobject]@{ ok = $false; code = -2; error = "$_" } }' @{ pw = $lockPw }
            Save 'lock05' @{ l = $l; audit = $l.newDump.audit }

            # restart: the unlock window - nothing entered, the old password, the new one
            Start-Rh
            Step $l 'unlock' {
                $null = Ui 'Unlock' -Title 'RemoteHub*locked' -TimeoutSec 90
                Start-Sleep -Seconds 2
                $l.ruleEmpty = WinText 'RemoteHub*locked'
                $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw"
                $null = Ui 'Unlock' -Title 'RemoteHub*locked'
                Start-Sleep -Seconds 8
                $l.oldPwText = WinText 'RemoteHub*locked'
                $l.mainOld = Ui 'SearchBox' -Value exists -Optional -TimeoutSec 3
                $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw2"
                $null = Ui 'Unlock' -Title 'RemoteHub*locked'
                $l.mainNew = Wait-Main
            }
            # C-LOCK-08 then C-LOCK-07: auto-lock after 5 minutes; the Edit dialog open for 5 1/2 minutes without input,
            # closed (UI Automation, not input), the lock comes; Exit RemoteHub
            Step $l 'autolock' {
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                # the window builds itself again once it knows whether Windows Hello is there: fields filled before are lost
                Start-Sleep -Seconds 3
                $l.choose = Ui 'Lock when you?re away' -Title 'Startup protection' -Value 'choose:After 5 minutes'
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
                $null = Ui 'LockConn' -Value 'menu:Edit?'
                $l.dialogOpen = Ui '*' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 15
                Start-Sleep -Seconds 330
                $l.lockedUnderDialog = Ui '*' -Title 'RemoteHub*locked' -Value exists -Optional -TimeoutSec 3
                $l.dialogStill = Ui '*' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 3
                Shot 'lock08-dialog'
                $null = Ui 'Cancel' -Title 'Edit connection'
                $l.lockedAfter = Ui '*' -Title 'RemoteHub*locked' -Value exists -Optional -TimeoutSec 60
                $l.lockedText = WinText 'RemoteHub*locked'
                $l.exitButton = Ui 'Exit RemoteHub' -Title 'RemoteHub*locked' -Value exists -Optional -TimeoutSec 5
                $l.exitTip = Ui 'Disconnects all sessions and closes RemoteHub' -Title 'RemoteHub*locked' -Value exists -Optional -TimeoutSec 5
                Shot 'lock07-locked'
                $null = Ui 'Exit RemoteHub' -Title 'RemoteHub*locked'
                $l.seenAfterExit = $false
                $deadline = (Get-Date).AddSeconds(30)
                do {
                    if ((Ui 'SearchBox' -Value exists -Optional -TimeoutSec 1).found) { $l.seenAfterExit = $true }
                    $alive = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count -gt 0 }
                } while ($alive -and (Get-Date) -lt $deadline)
                $l.exited = Wait-Gone
            }
            $null = Close-Rh
            $l.dump2 = Dump -Lock $lockPw2
            Save 'lock07-08' @{ l = $l; audit = $l.dump2.audit }
        }

        It 'C-LOCK-05 change the startup password: needs the current one, a new key (the old lock.json opens nothing), database re-keyed, "The startup password was changed."' -Tag 'C-LOCK-05' {
            # "Windows Hello set up again if it was" needs Windows Hello on the VM (vTPM + PIN, see C-LOCK-03): not part of this run.
            $l['change-error'] | Should -BeNullOrEmpty
            @($l.copies).Count | Should -Be 2
            $l.changed | Should -Match 'The startup password was changed\.'
            $l.keyHead | Should -Be '82,72,68,75,2'
            $l.newDump.error | Should -BeNullOrEmpty
            @($l.newDump.nodes | ForEach-Object name) | Should -Contain 'LockConn'
            $l.oldLock.ok | Should -BeFalse -Because 'the old lock.json''s key doesn''t open the new db.key'
            $l.oldKey.ok | Should -BeFalse -Because 'the database was re-keyed'
            $l.oldKey.code | Should -BeIn 11, 26 -Because "the old key itself is readable and the database refuses it ($($l.oldKey.error))"
            @($l.newDump.audit | Where-Object event -eq 'app.protection_changed' | ForEach-Object detail | Where-Object { $_ -eq 'Startup password' }).Count | Should -BeGreaterOrEqual 2 -Because 'turned on and changed'
            $l['unlock-error'] | Should -BeNullOrEmpty
            $l.oldPwText | Should -Match 'Wrong password\.'
            $l.mainOld.found | Should -BeFalse
            $l.mainNew | Should -BeTrue
        }

        It 'C-LOCK-06 password rules: at least 8 characters, matching repeat, the current password, an empty unlock' -Tag 'C-LOCK-06' {
            $l['on-error'] | Should -BeNullOrEmpty
            $l.ruleShort | Should -Match 'Use at least 8 characters\.'
            $l.ruleMismatch | Should -Match 'The passwords do not match\.'
            $l.lockAfterRules | Should -BeFalse -Because 'nothing was turned on'
            $l.ruleCurrent | Should -Match 'The current password is wrong\.'
            $l.ruleEmpty | Should -Match 'Enter the startup password\.'
        }

        It 'C-LOCK-07 Exit RemoteHub while auto-locked ("Disconnects all sessions and closes RemoteHub") ends it without showing the windows again' -Tag 'C-LOCK-07' {
            $l['autolock-error'] | Should -BeNullOrEmpty
            $l.lockedAfter.found | Should -BeTrue
            $l.exitButton.found | Should -BeTrue
            $l.exitTip.found | Should -BeTrue -Because 'the button''s tooltip'
            $l.seenAfterExit | Should -BeFalse
            $l.exited | Should -BeTrue
            @($l.dump2.audit | Where-Object event -eq 'app.locked' | ForEach-Object detail) | Should -Contain 'After 5 min without input'
        }

        It 'C-LOCK-08 no auto-lock while a dialog is open; it locks once the dialog is closed' -Tag 'C-LOCK-08' {
            $l['autolock-error'] | Should -BeNullOrEmpty
            $l.choose.value | Should -Be 'After 5 minutes'
            $l.dialogOpen.found | Should -BeTrue
            $l.lockedUnderDialog.found | Should -BeFalse
            $l.dialogStill.found | Should -BeTrue
            $l.lockedAfter.found | Should -BeTrue
            $l.lockedText | Should -Match 'Locked after a while without input'
        }

        It 'C-LOCK-09 the old RDP Manager folder: turning on offers to delete it; ticked, it is deleted' -Tag 'C-LOCK-09' {
            $l.legacyDir | Should -BeTrue
            $l.legacyBox.found | Should -BeTrue
            $l.legacyBox.name | Should -BeLike "*($rdpm)"
            $l.legacyTick.value | Should -Be 'On'
            $l.on | Should -BeTrue
            $l.legacyAfter | Should -BeFalse
        }
    }

    Context 'shared settings folder on the device''s SMB share' {
        BeforeAll {
            $s = [ordered]@{}
            $share = "\\$deviceIp\rhsettings"
            Clear-RhData
            $null = Invoke-LabGuest -Name $VmName -ArgumentList "$adminHome\AppData\Roaming\RdpManager" -ScriptBlock { param($p) Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
            # Admin's access to the share (the device's PSAdmin)
            $s.access = InSession 'cmdkey "/add:$($A.ip)" "/user:$($A.user)" "/pass:$($A.pw)" | Out-Null; Test-Path $A.share' @{ ip = $deviceIp; user = "$deviceName\$($device.AdminAccount)"; pw = $password; share = $share }
            # B: a second RemoteHub on the device makes the shared file in C:\rhsettings, with one connection
            Step $s 'second' {
                $null = Install-LabClient -Name $device.Name
                Start-Client -On $device.Name
                New-LabClientConnection -Name $device.Name -Display 'SharedB1' -TargetHost 'sharedb1.lab.test'
                Settings-Page 'Security & storage' -On $device.Name
                $null = Ui 'Change location?' -Title 'Settings' -On $device.Name
                $null = Ui 'Shared folder*' -Title 'Settings location' -Value check -On $device.Name
                $null = Ui 'type:Edit' -Title 'Settings location' -Value 'set:C:\rhsettings' -On $device.Name
                $null = Ui 'Apply' -Title 'Settings location' -On $device.Name
                $null = Ui 'type:Edit#1' -Title 'Protect shared settings' -Value "set:$sharePw" -On $device.Name
                $null = Ui 'type:Edit#2' -Title 'Protect shared settings' -Value "set:$sharePw" -On $device.Name
                $null = Ui 'Create' -Title 'Protect shared settings' -On $device.Name
                $deadline = (Get-Date).AddSeconds(90)
                do { Start-Sleep -Seconds 3; $h = Get-SharedHash } until ($h -or (Get-Date) -gt $deadline)
                $s.bFile = $h
            }
            # A (this PC): its own connections; one that can't connect (C-AUD-07 "Connection failed")
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'LocalOnly' -TargetHost 'localonly.lab.test'
            New-LabClientConnection -Name $VmName -Display 'FailConn' -TargetHost 'nohost-c-data2.invalid'
            Step $s 'fail' {
                $null = Ui 'FailConn' -Value doubleclick
                Start-Sleep -Seconds 45
                Shot 'aud07-fail'
                $null = Ui 'Close this tab' -Optional -TimeoutSec 10
            }
            # C-SHARE-05: A joins the existing shared file
            Step $s 'join' {
                Settings-Page 'Security & storage'
                $null = Ui 'Change location?' -Title 'Settings'
                $null = Ui 'Shared folder*' -Title 'Settings location' -Value check
                $null = Ui 'type:Edit' -Title 'Settings location' -Value "set:$share"
                $null = Ui 'Apply' -Title 'Settings location'
                $s.joinText = WinText 'Open shared settings'
                Shot 'share05'
                $null = Ui 'type:Edit' -Title 'Open shared settings' -Value "set:$sharePw"
                $null = Ui 'Open' -Title 'Open shared settings'
                $s.joinB1 = Ui 'SharedB1' -Value exists -Optional -TimeoutSec 60
                $s.joinLocal = Ui 'LocalOnly' -Value exists -Optional -TimeoutSec 3
            }
            # C-SHARE-04: B changes the file, A has nothing pending
            Step $s 'change' {
                $s.h0 = Get-SharedHash
                $null = Ui 'SharedB1' -Value 'menu:Edit?' -On $device.Name
                $null = Ui 'Display name' -Title 'Edit connection' -Value 'setnext:SharedB1-renamed' -On $device.Name
                $null = Ui 'Save' -Title 'Edit connection' -On $device.Name
                $s.updated = Box -Titles 'The settings file was updated on another device*', 'Settings updated' -Button 'Reload' -TimeoutSec 60
                Shot 'share04'
                Start-Sleep -Seconds 3
                $s.renamedShown = Ui 'SharedB1-renamed' -Value exists -Optional -TimeoutSec 15
                $s.h1 = Get-SharedHash
            }
            # C-LOCK-10: the startup protection turned on with a remembered shared-file password (then off again)
            Step $s 'protect' {
                $s.keysBefore = Head "$roamDir\keys.dat"
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                # the window builds itself again once it knows whether Windows Hello is there: fields filled before are lost
                Start-Sleep -Seconds 3
                Protect-On $lockPw
                $s.on = Wait-LockFile $true
                Start-Sleep -Seconds 5
                $s.keysOn = Head "$roamDir\keys.dat"
                $s.offConfirm = Protect-Off $lockPw
                $s.off = Wait-LockFile $false
                Start-Sleep -Seconds 5
                $s.keysOff = Head "$roamDir\keys.dat"
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            # C-SHARE-07: forget the remembered passwords; asked again at the next start
            Step $s 'forget' {
                Settings-Page 'Security & storage'
                $null = Ui 'Change location?' -Title 'Settings'
                $null = Ui 'Forget remembered passwords on this PC' -Title 'Settings location'
                Start-Sleep -Seconds 2
                $s.forgetText = WinText 'Settings location'
                $null = Ui 'Cancel' -Title 'Settings location' -Optional
                $s.keysAfterForget = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Test-Path "$r\keys.dat" }
            }
            $null = Close-Rh
            Start-Rh
            $s.prompt = Ui '*' -Title 'Unlock shared settings' -Value exists -Optional -TimeoutSec 60
            if ($s.prompt.found) {
                $null = Ui 'type:Edit' -Title 'Unlock shared settings' -Value "set:$sharePw"
                $null = Ui 'Unlock' -Title 'Unlock shared settings'
            }
            $s.main7 = Wait-Main
            # C-SHARE-06: back to this PC only
            Step $s 'local' {
                Settings-Page 'Security & storage'
                $null = Ui 'Change location?' -Title 'Settings'
                $null = Ui 'This PC only' -Title 'Settings location' -Value check
                $null = Ui 'Apply' -Title 'Settings location'
                $s.localConfirm = Box -Titles 'The current settings are copied to this PC*', 'Use this PC only' -Button 'Switch' -TimeoutSec 30
                Start-Sleep -Seconds 3
                $s.localShown = Ui 'SharedB1-renamed' -Value exists -Optional -TimeoutSec 10
            }
            $s.sharedAfter = Get-SharedHash
            # C-AUD-07: a backup exported, then the audit log as CSV (the viewer's labels)
            Step $s 'export' {
                Settings-Page 'Security & storage'
                $null = Ui 'Export backup?' -Title 'Settings'
                FileDialog 'C:\lab\aud07.rhbackup'
                $null = Ui 'type:Edit#1' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'type:Edit#2' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'Export' -Title 'Protect backup'
                $s.exported = Box -Titles 'Backup saved to*', 'Export backup' -Button 'OK' -TimeoutSec 60
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            Step $s 'csv' {
                Settings-Page 'Logs'
                $null = Ui 'Open audit log?' -Title 'Settings'
                $null = Ui 'Export CSV?' -Title 'Audit log' -TimeoutSec 30
                FileDialog 'C:\lab\aud07.csv'
                Start-Sleep -Seconds 3
                $null = Ui 'Close' -Title 'Audit log' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $s.csv = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Content 'C:\lab\aud07.csv' -Raw -ErrorAction SilentlyContinue }
            $null = Close-Rh
            $s.dump = Dump
            try { $null = Close-Rh -On $device.Name } catch { }
            Save 'share' @{ s = $s; audit = $s.dump.audit }
        }

        It 'C-SHARE-04 a change on another device with nothing pending: "Settings updated" - Reload shows it' -Tag 'C-SHARE-04' {
            $s['second-error'] | Should -BeNullOrEmpty
            $s['change-error'] | Should -BeNullOrEmpty
            $s.h1 | Should -Not -Be $s.h0 -Because 'B saved the file'
            $s.updated | Should -Match 'The settings file was updated on another device\. Load the new version now\?'
            $s.updated | Should -Match 'Reload'
            $s.renamedShown.found | Should -BeTrue
        }

        It 'C-SHARE-05 joining an existing shared file: "Open shared settings" with the replace warning; the shared connections replace this PC''s' -Tag 'C-SHARE-05' {
            $s.access | Should -BeTrue -Because 'Admin reaches the share'
            $s.bFile | Should -Not -BeNullOrEmpty -Because 'B made the shared file'
            $s['join-error'] | Should -BeNullOrEmpty
            $s.joinText | Should -Match 'This folder already contains a shared settings file\. Enter its password to use it\.'
            $s.joinText | Should -Match 'The connections currently on this PC are replaced by the shared ones'
            $s.joinText | Should -Match 'Open'
            $s.joinB1.found | Should -BeTrue
            $s.joinLocal.found | Should -BeFalse
        }

        It 'C-SHARE-06 back to this PC only: "Use this PC only" ... "The shared file itself is not deleted."; audit "Settings: this PC"' -Tag 'C-SHARE-06' {
            $s['local-error'] | Should -BeNullOrEmpty
            $s.localConfirm | Should -Match 'The shared file itself is not deleted\.'
            $s.localConfirm | Should -Match 'Switch'
            $s.localShown.found | Should -BeTrue -Because 'the current settings are copied to this PC'
            $s.sharedAfter | Should -Not -BeNullOrEmpty -Because 'the shared file is not deleted'
            $moves = @($s.dump.audit | Where-Object event -eq 'storage.location_changed' | ForEach-Object detail)
            $moves[-1] | Should -Be 'Settings: this PC'
        }

        It 'C-SHARE-07 forget remembered passwords: keys.dat deleted, "Done - the password will be asked again at the next start.", asked at the next start' -Tag 'C-SHARE-07' {
            $s['forget-error'] | Should -BeNullOrEmpty
            $s.forgetText | Should -Match 'Done . the password will be asked again at the next start\.'
            $s.keysAfterForget | Should -BeFalse
            $s.prompt.found | Should -BeTrue
            $s.main7 | Should -BeTrue
        }

        It 'C-LOCK-10 with the startup protection on, keys.dat starts with RHK2' -Tag 'C-LOCK-10' {
            $s['protect-error'] | Should -BeNullOrEmpty
            $s.keysBefore | Should -Not -BeNullOrEmpty -Because 'the shared file''s password is remembered in keys.dat'
            $s.keysBefore | Should -Not -Be '82,72,75,50'
            $s.on | Should -BeTrue
            $s.keysOn | Should -Be '82,72,75,50'
            $s.off | Should -BeTrue
            $s.keysOff | Should -Not -Be '82,72,75,50' -Because 'turned off, it is DPAPI only again'
        }

        It 'C-AUD-07 more events: Added, Connection failed, Startup protection changed (Startup password / Off), Settings location changed, Backup exported' -Tag 'C-AUD-07' {
            # "Startup password and Windows Hello" needs Windows Hello on the VM (see C-LOCK-03): not part of this run.
            $s.dump.error | Should -BeNullOrEmpty
            $audit = @($s.dump.audit)
            @($audit | Where-Object event -eq 'data.added' | ForEach-Object target) | Should -Contain 'LocalOnly'
            @($audit | Where-Object event -eq 'session.failed' | ForEach-Object target) | Should -Contain 'FailConn'
            $prot = @($audit | Where-Object event -eq 'app.protection_changed' | ForEach-Object detail)
            $prot | Should -Contain 'Startup password'
            $prot | Should -Contain 'Off'
            $loc = @($audit | Where-Object event -eq 'storage.location_changed' | ForEach-Object detail)
            @($loc | Where-Object { $_ -match "^Settings: shared . $([regex]::Escape($share))$" }).Count | Should -BeGreaterThan 0
            $loc | Should -Contain 'Settings: this PC'
            $s['export-error'] | Should -BeNullOrEmpty
            $exp = @($audit | Where-Object event -eq 'backup.exported')
            $exp.Count | Should -Be 1
            $exp[0].target | Should -Be 'aud07.rhbackup'
            $exp[0].detail | Should -Match '^\d+ folders and connections, \d+ saved credentials$'
            # the viewer's labels, as the CSV export writes them next to the event code
            $s['csv-error'] | Should -BeNullOrEmpty
            foreach ($pair in 'Added,data.added', 'Connection failed,session.failed', 'Startup protection changed,app.protection_changed', 'Settings location changed,storage.location_changed', 'Backup exported,backup.exported') {
                "$($s.csv)" | Should -Match ",$([regex]::Escape($pair)),"
            }
        }
    }
}
