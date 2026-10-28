# C-DB / C-LOCK / C-AUD / C-BAK / C-SHARE - RemoteHub's own data on the admin's PC (this client VM): the encrypted
# database (DPAPI key, the move from a 0.13 settings.dat, daily backups, a damaged file, no stored password in
# RemoteHub's memory), the startup protection (password, auto-lock, Windows Hello, turning it off), the audit log (entries, append-only and hash chain, retention
# policy, CSV export), password-protected backups (.rhbackup round trip to normaluser, wrong password, old .rdpmbackup)
# and the shared settings folder on an SMB share of the device VM (\\<device ip>\rhsettings, a second RemoteHub on the
# device for the sync conflict, the share offline at start).
# The database is read in the account's own session with RemoteHub's own sqlite3mc.dll and the same settings RemoteHub
# uses (cipher chacha20, kdf_iter 1, the raw key from db.key - DPAPI, plus the startup lock when it is on): see $rhdbCs /
# $rhdbPs below, written to C:\ProgramData\RemoteHubLab. Client VMs only. See docs/test-lab-cases.md section 10.2.
# TimeLimitMin: 200   (nine client starts a simulated day apart, the 1.5 min auto-lock wait, the SMB share, a second account: about 2.5 h)
# LabRole: console
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $cfg = Get-LabConfig
    $password = $cfg.Password
    # test values of this file (not the lab password)
    $lockPw = 'Lab-Startup-Pw-71'
    $backupPw = 'Lab-Backup-Pw-72'
    $sharePw = 'Lab-Shared-Pw-73'
    $adminHome = "C:\Users\$($vm.ConsoleAccount)"
    $localDir = "$adminHome\AppData\Local\RemoteHub"
    $roamDir = "$adminHome\AppData\Roaming\RemoteHub"
    $script:rhTasks = @{}

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "c-data-$name.json") -Encoding UTF8 }
    function Shot([string]$name, [string]$On = $VmName) { try { $null = Get-LabScreenshot -Name $On -Path (Join-Path $EvidenceDir "c-data-$name.png") } catch { } }
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
    # The title of a message box that is still open, from the window itself (GetWindowText): RemoteHub's WPF-UI message
    # boxes (Dialogs.Info / Ask / Confirm) are named by their text in UI Automation, and their title bar text is not in
    # the element list, so Box answers the text only (run 20261016-212514, C-BAK-02 and C-SHARE-02). -Box: Box's answer.
    function Box-Title([string]$Box, [string]$Account = 'Console') {
        $name = "$Box" -split "`n" | Select-Object -First 1
        if (-not $name) { return '' }
        $code = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('RhLabWinText' -as [type])) {
    Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public static class RhLabWinText { [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n); public static string Get(int h) { var b = new StringBuilder(1024); GetWindowTextW(new IntPtr(h), b, 1024); return b.ToString(); } }'
}
$ae = [Windows.Automation.AutomationElement]
$isWin = New-Object Windows.Automation.PropertyCondition ($ae::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
$tops = @($ae::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition))
$owned = @($tops | Where-Object { $_.Current.Name -like 'RemoteHub*' -or $_.Current.Name -like '* RemoteHub' } | ForEach-Object { @($_.FindAll([Windows.Automation.TreeScope]::Descendants, $isWin)) })
$w = @(@($owned) + @($tops) | Where-Object { $_.Current.Name -like $A.name -and $_.Current.NativeWindowHandle }) | Select-Object -First 1
if ($w) { [RhLabWinText]::Get($w.Current.NativeWindowHandle) } else { $null }
'@
        # the window is found by the name's first line (the text may run over several lines)
        $pattern = [WildcardPattern]::Escape($name.TrimEnd("`r")) + '*'
        try { "$(InSession $code @{ name = $pattern } -Account $Account)" } catch { "error: $_" }
    }
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
    function Start-Rh([string]$Account = 'Console', [string]$DataDir, [string]$On = $VmName) {
        $code = "& 'C:\Program Files\RemoteHub\RemoteHub.exe'"
        if ($DataDir) { $code = "`$env:REMOTEHUB_DATA_DIR = '$DataDir'; $code" }
        $script:rhTasks["$On|$Account"] = Start-LabInSession -Name $On -Account $Account -ScriptBlock ([scriptblock]::Create($code))
    }
    function Wait-Main([string]$Account = 'Console', [string]$On = $VmName, [int]$TimeoutSec = 90) {
        $r = Ui 'SearchBox' -Value exists -Optional -TimeoutSec $TimeoutSec -Account $Account -On $On
        # the welcome tour of a new profile (or What's new after an upgrade) closed as a user would - it is modal
        if ($r.found) { $null = Close-LabFirstStart -Name $On -Account $Account; $null = Ui 'NewButton' -Value fit -Optional -TimeoutSec 10 -Account $Account -On $On }
        [bool]$r.found
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
    # name box (focused when the dialog opens) and Enter pressed.
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
        $out = "C:\ProgramData\RemoteHubLab\session\c-data-$([guid]::NewGuid().ToString('N')).json"
        $json = (ConvertTo-Json -InputObject $Data -Depth 6 -Compress) -replace "'", "''"
        $text = ". 'C:\ProgramData\RemoteHubLab\rhdb.ps1'`n`$A = '$json' | ConvertFrom-Json`n`$r = & {`n$Code`n}`n" +
            "`$j = if (`$null -eq `$r) { 'null' } else { ConvertTo-Json -InputObject `$r -Depth 10 -Compress }`nSet-Content -LiteralPath '$out' -Encoding UTF8 -Value `$j"
        $raw = Invoke-LabInSession -Name $VmName -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($text))
        $answer = Invoke-LabGuest -Name $VmName -ArgumentList $out -ScriptBlock { param($f) if (Test-Path $f) { Get-Content -LiteralPath $f -Raw; Remove-Item -LiteralPath $f } }
        if (-not $answer) { throw "session code gave no answer: $raw" }
        $answer | ConvertFrom-Json
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
    function Get-Sha([string]$s) { -join ([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString('x2') }) }
    # a record's JSON without what differs between copies of the same item (its id) or changes by just using RemoteHub
    function Norm([string]$Json) {
        $o = $Json | ConvertFrom-Json
        foreach ($p in 'Id', 'IsExpanded', 'LastConnectedUtc', 'PsRemoting', 'JumpHostId') { $o.PSObject.Properties.Remove($p) }
        $o | ConvertTo-Json -Depth 20 -Compress
    }
    # the items as "path|kind|host|data|passwords", the path relative to the top-level folder -Under (or the root)
    function Proj($dump, [string]$Under) {
        @(foreach ($n in @($dump.nodes)) {
                $p = $n.path
                if ($Under) { if (-not $p.StartsWith("$Under / ")) { continue }; $p = $p.Substring($Under.Length + 3) }
                "$p|$($n.kind)|$($n.host)|$(Norm $n.data)|$(@($n.secrets) -join ';')"
            }) | Sort-Object
    }
    function Creds($dump) { @(foreach ($c in @($dump.credentials)) { "$($c.name)|$(Norm $c.data)|$(@($c.secrets) -join ';')" }) | Sort-Object }
    function Backups { @(Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object Name }) }
    function Policy([string]$Name, $Value) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Name, $Value -ScriptBlock {
            param($n, $v)
            $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
            if ($null -eq $v) { Remove-ItemProperty $k -Name $n -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name $n -Value $v -Type DWord }
        }
    }
    # the guest's clock moved by whole days from the host's time, Hyper-V time sync and w32time off; 0 = back, sync on
    function Set-GuestClock([int]$Days) {
        $sync = @(Get-VMIntegrationService -VMName $vm.Name | Where-Object { $_.Name -match 'Time Synchronization|Zeitsynchronisierung' })
        if ($Days -ne 0) { $sync | Disable-VMIntegrationService }
        # the guest's own time services too (run 20261016-024318: with only the host side off, every other day the clock
        # was back to the real time when RemoteHub started). Stopping them was not enough either (run 20261016-212514:
        # rounds 1, 3, 6, 8 and 9 found the real time again a few minutes later): w32time is trigger-started (domain,
        # system state changes) and sets the clock back as soon as it runs. So both are Disabled while the clock is
        # moved, their start types noted in the guest and put back with -Days 0. Set until it holds for a few seconds.
        Invoke-LabGuest -Name $VmName -ArgumentList ([DateTime]::UtcNow.AddDays($Days).Ticks), ($Days -ne 0) -ScriptBlock {
            param($t, $off)
            $keep = 'C:\ProgramData\RemoteHubLab\c-data-clock-services.json'
            $svc = 'w32time', 'vmictimesync'
            if ($off) {
                if (-not (Test-Path -LiteralPath $keep)) {
                    $start = @{}
                    foreach ($s in $svc) {
                        $p = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$s" -ErrorAction SilentlyContinue
                        $start[$s] = if (-not $p) { 'demand' } elseif ($p.Start -eq 2 -and $p.PSObject.Properties['DelayedAutostart'] -and $p.DelayedAutostart -eq 1) { 'delayed-auto' } elseif ($p.Start -eq 2) { 'auto' } elseif ($p.Start -eq 4) { 'disabled' } else { 'demand' }
                    }
                    $null = New-Item -ItemType Directory -Path (Split-Path $keep) -Force
                    Set-Content -LiteralPath $keep -Encoding UTF8 -Value (ConvertTo-Json -InputObject $start)
                }
                foreach ($s in $svc) { $null = sc.exe config $s start= disabled; Stop-Service $s -Force -ErrorAction SilentlyContinue }
            }
            elseif (Test-Path -LiteralPath $keep) {
                $start = Get-Content -LiteralPath $keep -Raw | ConvertFrom-Json
                foreach ($s in $svc) { $mode = "$($start.$s)"; if (-not $mode) { $mode = 'demand' }; $null = sc.exe config $s start= $mode }
                Remove-Item -LiteralPath $keep -Force
            }
            $want = [DateTime]::new([long]$t, [DateTimeKind]::Utc)
            foreach ($try in 1..5) {
                $null = Set-Date -Date $want.ToLocalTime()
                Start-Sleep -Seconds 3
                if ([math]::Abs(([DateTime]::UtcNow - $want).TotalMinutes) -lt 2) { break }
            }
            if (-not $off) { Start-Service vmictimesync, w32time -ErrorAction SilentlyContinue }
        }
        if ($Days -eq 0) { $sync | Enable-VMIntegrationService }
    }
    # the guest's clock (UTC, ISO 8601) - recorded with each daily-backup round
    function Get-GuestUtc { (Get-LabGuestTime -Name $VmName).ToUniversalTime().ToString('o') }
    # a 0.13 settings.dat: "RDPM" 0x01 + DPAPI (entropy RdpManager.Settings.v1) over the AppData JSON - a folder with a
    # saved credential, a connection in it and one with its own password
    function New-Legacy([string]$Tag) {
        $ids = @{ folder = [guid]::NewGuid().ToString(); conn1 = [guid]::NewGuid().ToString(); conn2 = [guid]::NewGuid().ToString(); cred = [guid]::NewGuid().ToString() }
        $pw1 = "Lab-Saved-$Tag-41"; $pw2 = "Lab-Conn-$Tag-42"
        $data = [ordered]@{
            Version          = 1
            SavedCredentials = @([ordered]@{ Id = $ids.cred; Name = "Lab credential $Tag"; Username = 'labadmin'; Domain = 'LAB'; Password = $pw1 })
            Items            = @(
                [ordered]@{ '$type' = 'folder'; Id = $ids.folder; Name = "Legacy folder $Tag"; InheritCredentials = $false; CredentialId = $ids.cred
                    Children = @([ordered]@{ '$type' = 'connection'; Id = $ids.conn1; Name = "Legacy server $Tag"; Host = 'legacy01.lab.test'; Port = 3389; InheritCredentials = $true })
                },
                [ordered]@{ '$type' = 'connection'; Id = $ids.conn2; Name = "Legacy direct $Tag"; Host = '10.99.0.5'; Port = 3390; InheritCredentials = $false
                    Credentials = [ordered]@{ Username = 'labuser'; Domain = '.'; Password = $pw2 }
                }
            )
        }
        [pscustomobject]@{ json = ($data | ConvertTo-Json -Depth 10 -Compress); ids = $ids; pw1 = $pw1; pw2 = $pw2; tag = $Tag }
    }
    # the device: is PSAdmin's RDP session there and active?
    function Get-DeviceSession {
        Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
            param($u) $q = (quser 2>$null) -join "`n"; [bool]($q -match "(?im)^\W?$u\s+rdp-tcp\S*\s+\d+\s+(Active|Aktiv)")
        }
    }
    # the RDP tab of "Device" (doubleclick = Connect); Windows' certificate question answered; waits for the session
    function Connect-Device {
        $null = Ui 'Device' -Value doubleclick
        $null = Ui 'Yes' -Title '*Remote Desktop*' -Optional -TimeoutSec 20
        $deadline = (Get-Date).AddSeconds(120)
        do { Start-Sleep -Seconds 5; $up = Get-DeviceSession } until ($up -or (Get-Date) -gt $deadline)
        $up
    }
    function Get-SharedHash { Invoke-LabGuest -Name $device.Name -ScriptBlock { $f = 'C:\rhsettings\RemoteHub.settings'; if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } } }
    # the shared file's hash once it differs from -From (a save on the device written), or as it is after -TimeoutSec
    function Wait-SharedChange([string]$From, [int]$TimeoutSec = 30) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $h = Get-SharedHash
            if ("$h" -ne $From) { return $h }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
        $h
    }
    function Import-Backup([string]$File, [string]$Pw, [string]$Account = 'User') {
        Settings-Page 'Security & storage' -Account $Account
        # closes the settings window first, then Windows' Open dialog
        $null = Ui 'Import backup?' -Title 'Settings' -Account $Account
        FileDialog $File -Title 'Open' -Account $Account
        $null = Ui 'type:Edit' -Title 'Unlock backup' -Value "set:$Pw" -Account $Account
        $null = Ui 'Import' -Title 'Unlock backup' -Account $Account
    }

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

    if ($isClient) {
        $device = Get-LabDevice -For $VmName
        Reset-LabVm -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # always from the checkpoint: the file damages and replaces RemoteHub's database and settings
        $null = Initialize-LabVm -Name $VmName -Client -Clean
        $clientIp = Get-LabVmAddress -Name $VmName
        $null = Invoke-LabGuest -Name $VmName -ArgumentList $rhdbCs, $rhdbPs -ScriptBlock {
            param($cs, $ps)
            $d = 'C:\ProgramData\RemoteHubLab'; $null = New-Item -ItemType Directory -Path $d -Force
            Set-Content "$d\rhdb.cs" $cs -Encoding UTF8
            Set-Content "$d\rhdb.ps1" $ps -Encoding UTF8
            icacls "$d\rhdb.cs" /grant '*S-1-5-32-545:RX' | Out-Null
            icacls "$d\rhdb.ps1" /grant '*S-1-5-32-545:RX' | Out-Null
            # C:\lab: exports, copies of keys, the CSV files (every account may write)
            $null = New-Item -ItemType Directory -Path 'C:\lab' -Force
            icacls 'C:\lab' /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
            # a folder to upload (C-AUD-01): its content must never reach the audit log
            $up = 'C:\Users\Public\UpFolder'; $null = New-Item -ItemType Directory -Path $up -Force
            1..3 | ForEach-Object { Set-Content -Encoding ASCII "$up\f$_.txt" "c-data audit file content marker $_" }
        }
        # the device: RDP on, two SMB shares (the shared settings folder, an upload target), reachable by IP
        $deviceName = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue
            $everyone = (New-Object Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([Security.Principal.NTAccount]).Value
            foreach ($n in 'rhsettings', 'rhupload') {
                $p = "C:\$n"; $null = New-Item -ItemType Directory -Path $p -Force
                icacls $p /grant '*S-1-1-0:(OI)(CI)M' | Out-Null
                if (-not (Get-SmbShare -Name $n -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name $n -Path $p -FullAccess $everyone }
            }
            $env:COMPUTERNAME
        }
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        try { Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force } } catch { }
        foreach ($t in @($script:rhTasks.Keys)) { try { Stop-LabInSession -Name ($t -split '\|')[0] -Task $script:rhTasks[$t] } catch { } }
        try { Set-GuestClock -Days 0 } catch { }
        try { Clear-LabAutoLogon -Name $VmName } catch { }
        try { Stop-LabVm -Name $device.Name } catch { }
        # RemoteHub's data, the clock, a second account here; SMB shares and a second RemoteHub on the device
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $device.Name }
    }
}

Describe 'C-DATA <VmName>' -Tag Client, 'C-DB', 'C-LOCK', 'C-AUD', 'C-BAK', 'C-SHARE' -Skip:(-not $isClient) {

    Context 'database' {
        BeforeAll {
            $d = [ordered]@{}
            # C-DB-01: the first start, one connection with the lab password, closed
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'LabTarget' -TargetHost 'labtarget.lab.test' -User $device.AdminAccount -Password $password
            $d.closed1 = Close-Rh
            $d.db01 = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir, $password -ScriptBlock {
                param($local, $roam, $pw)
                $db = [IO.File]::ReadAllBytes("$local\remotehub.db")
                $key = [IO.File]::ReadAllBytes("$local\db.key")
                # the password as UTF-8 and as UTF-16LE, searched byte by byte (Latin-1 maps bytes 1:1 to chars)
                $latin = [Text.Encoding]::GetEncoding(28591)
                $needles = @($latin.GetString([Text.Encoding]::UTF8.GetBytes($pw)), $latin.GetString([Text.Encoding]::Unicode.GetBytes($pw)))
                $files = @(Get-ChildItem $local, $roam -Recurse -File -Force -ErrorAction SilentlyContinue)
                $hits = @(foreach ($f in $files) {
                        $t = try { $latin.GetString([IO.File]::ReadAllBytes($f.FullName)) } catch { '' }
                        if (@($needles | Where-Object { $t.Contains($_) }).Count -gt 0) { $f.FullName }
                    })
                [pscustomobject]@{
                    head     = $latin.GetString($db, 0, 16)
                    headHex  = (@($db[0..15] | ForEach-Object { $_.ToString('x2') }) -join ' ')
                    keyHead  = @($key[0..4] | ForEach-Object { [int]$_ })
                    searched = @($files | ForEach-Object FullName)
                    hits     = $hits
                }
            }
            $d.plain = InSession 'Test-RhSql (Join-Path $env:LOCALAPPDATA "RemoteHub\remotehub.db") $null "SELECT * FROM nodes"'
            $d.keyed = InSession '@(Get-RhDump).nodes | ForEach-Object name'
            # C-DB-14: RemoteHub started again - the password comes from the database, it isn't typed in this process -
            # and its memory dumped in full: the password is in it neither as UTF-8 nor as UTF-16 (it stays encrypted
            # until a connection uses it, local-db-design.md section 17). The connection's host name is the control
            # that the dump and the search work. Only the counts go to the evidence; the dump is deleted.
            Start-Client
            $controlHost = 'labtarget.lab.test'
            $d.db14 = Invoke-LabGuest -Name $VmName -ArgumentList $controlHost, $password -ScriptBlock {
                param($control, $pw)
                Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class RhMemDump {
    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, int processId, SafeFileHandle file, int type, IntPtr exception, IntPtr userStream, IntPtr callback);
    // full memory, unreadable regions left out
    public static string Write(int pid, string path) {
        using (Process p = Process.GetProcessById(pid))
        using (FileStream f = File.Create(path)) {
            if (!MiniDumpWriteDump(p.Handle, pid, f.SafeFileHandle, 0x2 | 0x20000, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                return "MiniDumpWriteDump failed: error " + Marshal.GetLastWin32Error();
        }
        return null;
    }
    // how often each needle is in the file, read in 64 MB chunks that overlap by the longest needle
    public static long[] Count(string path, byte[][] needles) {
        long[] counts = new long[needles.Length];
        int longest = 1;
        foreach (byte[] n in needles) longest = Math.Max(longest, n.Length);
        const int chunk = 64 << 20;
        byte[] buf = new byte[chunk + longest];
        int carried = 0, read;
        using (FileStream f = File.OpenRead(path)) {
            while ((read = f.Read(buf, carried, chunk)) > 0) {
                int len = carried + read;
                for (int k = 0; k < needles.Length; k++) {
                    byte[] n = needles[k];
                    for (int i = 0; i + n.Length <= len; i++) {
                        if (buf[i] != n[0] || i + n.Length <= carried) continue; // (a match inside the carried part was counted)
                        int j = 1;
                        while (j < n.Length && buf[i + j] == n[j]) j++;
                        if (j == n.Length) counts[k]++;
                    }
                }
                carried = Math.Min(longest - 1, len);
                Buffer.BlockCopy(buf, len - carried, buf, 0, carried);
            }
        }
        return counts;
    }
}
'@
                $rh = @(Get-Process RemoteHub -ErrorAction SilentlyContinue)
                if ($rh.Count -ne 1) { return [pscustomobject]@{ error = "$($rh.Count) RemoteHub processes"; size = 0; utf8 = -1; utf16 = -1; control = -1 } }
                try { [Diagnostics.Process]::EnterDebugMode() } catch { }   # the console account's process, dumped from this (admin) session
                $path = 'C:\lab\c-db-14.dmp'
                $null = New-Item -ItemType Directory -Path 'C:\lab' -Force
                try {
                    $err = [RhMemDump]::Write($rh[0].Id, $path)
                    $size = if (Test-Path $path) { (Get-Item $path).Length } else { 0 }
                    $counts = if ($err) { @(-1, -1, -1) } else {
                        [RhMemDump]::Count($path, [byte[][]]@([Text.Encoding]::UTF8.GetBytes($pw), [Text.Encoding]::Unicode.GetBytes($pw), [Text.Encoding]::Unicode.GetBytes($control)))
                    }
                    [pscustomobject]@{ error = $err; size = $size; utf8 = $counts[0]; utf16 = $counts[1]; control = $counts[2] }
                }
                finally { Remove-Item $path -Force -ErrorAction SilentlyContinue }
            }
            $d.closed14 = Close-Rh
            Save 'db14' $d.db14
            # C-DB-02: Admin's database and key where normaluser can read them (opened later, in normaluser's session)
            $d.copy = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $vm.UserAccount -ScriptBlock {
                param($local, $user)
                $dst = 'C:\lab\dbcopy'; $null = New-Item -ItemType Directory -Path $dst -Force
                Copy-Item "$local\remotehub.db*", "$local\db.key" $dst -Force
                icacls $dst /grant "${user}:(OI)(CI)M" /T | Out-Null
                @(Get-ChildItem $dst | ForEach-Object Name)
            }
            Save 'db01' $d

            # C-DB-03: no database yet, a 0.13 settings.dat in %APPDATA%\RemoteHub
            Clear-RhData
            $legacy = New-Legacy 'A'
            $d.legacyBytes = InSession 'New-RhLegacySettings $A.json' @{ json = $legacy.json }
            Start-Client
            Start-Sleep -Seconds 5
            $d.closed3 = Close-Rh
            $d.db03files = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
                param($l, $r) [pscustomobject]@{ roaming = @(Get-ChildItem $r -Force | ForEach-Object Name); local = @(Get-ChildItem $l -Force | ForEach-Object Name) }
            }
            $d.db03 = Dump
            $d.legacyIds = $legacy.ids
            Save 'db03' $d

            # C-DB-04: backup\ emptied, then one start a day (clock moved forward, time sync off)
            Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) Remove-Item "$l\backup\*" -Force -ErrorAction SilentlyContinue }
            # the backup runs in the background after the start: waited for (a new file, at most 7, its size settled)
            # before RemoteHub is closed, so it isn't cut short by the exit
            function Wait-NewBackup([string[]]$Before, [int]$TimeoutSec = 90) {
                $deadline = (Get-Date).AddSeconds($TimeoutSec)
                $size = -1
                do {
                    $now = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
                        param($l) @(Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "$($_.Name)|$($_.Length)" })
                    }
                    $new = @(@($now) | Where-Object { @($Before) -notcontains ($_ -split '\|')[0] })
                    if ($new.Count -and @($now).Count -le 7) {
                        $s = [long](($new[-1] -split '\|')[1])
                        if ($s -gt 0 -and $s -eq $size) { return $true }
                        $size = $s
                    }
                    Start-Sleep -Seconds 3
                } while ((Get-Date) -lt $deadline)
                $false
            }
            Start-Client; $d.bk1Made = Wait-NewBackup @(); $null = Close-Rh
            $d.bk1 = Backups
            # the same day: no new backup - there is no condition to wait for, so RemoteHub gets the time a backup takes
            Start-Client; Start-Sleep -Seconds 30; $null = Close-Rh
            $d.bk2 = Backups
            $d.rounds = @()
            try {
                foreach ($i in 1..9) {
                    $before = @(Backups)
                    Set-GuestClock -Days $i
                    $atStart = Get-GuestUtc
                    Start-Client
                    $made = Wait-NewBackup $before
                    $atBackup = Get-GuestUtc
                    $null = Close-Rh
                    # the guest's clock at the start, after the backup and with the files (a day without a backup: was the
                    # clock moved, did it stay there, did the client run?)
                    $d.rounds += , [pscustomobject]@{ day = $i; files = @(Backups); made = $made; startUtc = $atStart; backupUtc = $atBackup; guestUtc = (Get-GuestUtc) }
                }
            }
            finally { Set-GuestClock -Days 0 }
            Save 'db04' @{ bk1 = $d.bk1; bk1Made = $d.bk1Made; bk2 = $d.bk2; rounds = $d.rounds }

            # C-DB-05: a connection added after the newest backup, then one byte of the database flipped (in its first
            # page: RemoteHub reads it when opening - a byte in a page it never reads at the start wouldn't be noticed)
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'AfterBackup' -TargetHost 'afterbackup.lab.test'
            $null = Close-Rh
            $d.flip = Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock {
                param($l)
                $f = "$l\remotehub.db"
                $b = [IO.File]::ReadAllBytes($f)
                $b[2048] = [byte]($b[2048] -bxor 0xFF)
                [IO.File]::WriteAllBytes($f, $b)
                [pscustomobject]@{ size = $b.Length; wal = (Test-Path "$f-wal"); backups = @(Get-ChildItem "$l\backup" -Filter 'remotehub-*.db' | Sort-Object LastWriteTimeUtc | ForEach-Object { "$($_.Name) $($_.LastWriteTimeUtc.ToString('o'))" }) }
            }
            Start-Rh
            $d.db05msg = Box -Titles 'RemoteHub' -Button 'Yes' -TimeoutSec 90
            $d.db05main = Wait-Main
            $d.afterMissing = Ui 'AfterBackup' -Value exists -Optional -TimeoutSec 5
            $d.backupItem = Ui 'Legacy direct A' -Value exists -Optional -TimeoutSec 10
            Shot 'db05'
            $null = Close-Rh
            $d.db05files = @(Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($l) Get-ChildItem $l -Force | ForEach-Object Name })
            $d.db05 = Dump
            Save 'db05' @{ flip = $d.flip; msg = $d.db05msg; main = $d.db05main; afterMissing = $d.afterMissing; backupItem = $d.backupItem; files = $d.db05files; audit = $d.db05.audit }
        }

        It 'C-DB-01 a new database: remotehub.db no plain SQLite file, db.key RHDK 0x01 (DPAPI), the lab password in no file' -Tag 'C-DB-01' {
            $d.db01.head | Should -Not -BeLike 'SQLite format 3*'
            $d.plain.ok | Should -BeFalse
            $d.plain.error | Should -Match 'file is not a database'
            (@($d.db01.keyHead) -join ',') | Should -Be '82,72,68,75,1'
            @($d.keyed) | Should -Contain 'LabTarget' -Because 'the database opens with its key and holds the connection'
            @($d.db01.searched).Count | Should -BeGreaterThan 2
            @($d.db01.hits) | Should -BeNullOrEmpty
        }

        It 'C-DB-14 a stored password not in use: a full memory dump of RemoteHub holds it neither as UTF-8 nor as UTF-16' -Tag 'C-DB-14' {
            $d.db14.error | Should -BeNullOrEmpty
            $d.db14.size | Should -BeGreaterThan 10MB
            $d.db14.control | Should -BeGreaterThan 0 -Because 'the connection''s host name is in memory - the dump and the search work'
            $d.db14.utf8 | Should -Be 0
            $d.db14.utf16 | Should -Be 0
        }

        It 'C-DB-03 0.13 settings.dat moved into the database: renamed .migrated, every field equal (passwords too), audit "Settings moved to the database"' -Tag 'C-DB-03' {
            $d.db03files.roaming | Should -Contain 'settings.dat.migrated'
            $d.db03files.roaming | Should -Not -Contain 'settings.dat'
            $d.db03files.local | Should -Contain 'remotehub.db'
            $d.db03files.local | Should -Contain 'db.key'
            $ids = $d.legacyIds
            $n = @{}; foreach ($x in @($d.db03.nodes)) { $n[$x.id] = $x }
            $c = @{}; foreach ($x in @($d.db03.credentials)) { $c[$x.id] = $x }
            $bad = [Collections.Generic.List[string]]::new()
            function Expect($what, $actual, $expected) { if ("$actual" -ne "$expected") { $bad.Add("${what}: '$actual', expected '$expected'") } }
            $f = $n[$ids.folder]; $s1 = $n[$ids.conn1]; $s2 = $n[$ids.conn2]; $cr = $c[$ids.cred]
            foreach ($x in $f, $s1, $s2, $cr) { $x | Should -Not -BeNullOrEmpty -Because "every record of settings.dat is in the database $($d.db03.error)" }
            $fd = $f.data | ConvertFrom-Json; $d1 = $s1.data | ConvertFrom-Json; $d2 = $s2.data | ConvertFrom-Json; $cd = $cr.data | ConvertFrom-Json
            Expect 'folder kind' $f.kind 'folder'; Expect 'folder name' $f.name 'Legacy folder A'; Expect 'folder parent' $f.parent ''
            Expect 'folder InheritCredentials' $fd.InheritCredentials 'False'; Expect 'folder CredentialId' ("$($fd.CredentialId)".ToLower()) $ids.cred
            Expect 'conn1 kind' $s1.kind 'connection'; Expect 'conn1 name' $s1.name 'Legacy server A'; Expect 'conn1 host' $s1.host 'legacy01.lab.test'
            Expect 'conn1 parent' $s1.parent $ids.folder; Expect 'conn1 Port' $d1.Port 3389; Expect 'conn1 InheritCredentials' $d1.InheritCredentials 'True'
            Expect 'conn2 name' $s2.name 'Legacy direct A'; Expect 'conn2 host' $s2.host '10.99.0.5'; Expect 'conn2 parent' $s2.parent ''
            Expect 'conn2 Port' $d2.Port 3390; Expect 'conn2 InheritCredentials' $d2.InheritCredentials 'False'
            Expect 'conn2 Username' $d2.Credentials.Username 'labuser'; Expect 'conn2 Domain' $d2.Credentials.Domain '.'
            Expect 'conn2 password' (@($s2.secrets) -join ';') "Credentials/Password=$(Get-Sha 'Lab-Conn-A-42')"
            Expect 'credential name' $cr.name 'Lab credential A'; Expect 'credential Username' $cd.Username 'labadmin'; Expect 'credential Domain' $cd.Domain 'LAB'
            Expect 'credential password' (@($cr.secrets) -join ';') "Password=$(Get-Sha 'Lab-Saved-A-41')"
            @($bad) | Should -BeNullOrEmpty
            $moved = @($d.db03.audit | Where-Object event -eq 'storage.migrated')
            $moved.Count | Should -Be 1
            $moved[0].detail | Should -Be '3 folders and connections, 1 saved credentials moved from settings.dat to the encrypted database.'
        }

        It 'C-DB-04 daily backup: one copy per day (UTC name), none more the same day, 7 kept, the oldest removed first' -Tag 'C-DB-04' {
            @($d.bk1).Count | Should -Be 1
            @($d.bk1)[0] | Should -Match '^remotehub-\d{8}-\d{6}\.db$'
            (@($d.bk2) -join ',') | Should -Be (@($d.bk1) -join ',') -Because 'less than 24 h since the newest backup'
            $all = [Collections.Generic.List[string]]::new(); foreach ($x in @($d.bk1)) { $all.Add($x) }
            @($d.rounds).Count | Should -Be 9
            foreach ($r in $d.rounds) {
                $new = @(@($r.files) | Where-Object { $all -notcontains $_ })
                $new.Count | Should -Be 1 -Because "day +$($r.day) makes one new backup (guest clock at the start $($r.startUtc), after waiting for the backup $($r.backupUtc))"
                $all.Add($new[0])
                (@($r.files) -join ',') | Should -Be (@($all | Sort-Object | Select-Object -Last 7) -join ',') -Because "day +$($r.day): the newest 7, the oldest removed first"
            }
        }

        It 'C-DB-05 a damaged database: RemoteHub offers the latest backup, restores it, keeps the damaged file as remotehub.db.damaged-[stamp]' -Tag 'C-DB-05' {
            $d.db05msg | Should -Match "RemoteHub's database can't be opened:\s*The database is damaged or doesn't match its key\.\s*Restore the automatic backup of .+\?\s*Changes made after it are lost\. The current database is kept beside it\."
            $d.db05main | Should -BeTrue
            $d.backupItem.found | Should -BeTrue
            $d.afterMissing.found | Should -BeFalse -Because 'the connection was added after the backup'
            @($d.db05files | Where-Object { $_ -match '^remotehub\.db\.damaged-\d{8}-\d{6}$' }) | Should -Not -BeNullOrEmpty
            @($d.db05files) | Should -Contain 'remotehub.db'
            $restored = @($d.db05.audit | Where-Object event -eq 'storage.restored')
            $restored.Count | Should -BeGreaterThan 0
            $restored[-1].detail | Should -Match '^Restored the backup of .+\.$'
        }
    }

    Context 'audit log' {
        BeforeAll {
            $a = [ordered]@{}
            $newHost = "newhost-$(Get-Random -Minimum 1000 -Maximum 9999).lab.test"
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
            New-LabClientConnection -Name $VmName -Display 'Device2' -TargetHost 'device2.lab.test'
            New-LabClientConnection -Name $VmName -Display 'ToDelete' -TargetHost 'todelete.lab.test'
            # C-AUD-01: the password-prompt helper (the device always asks for the password: RemoteHub copies it)
            Step $a 'pwcopy' {
                # Remote Desktop Services reads the policy when it starts (run 20261016-024318: set only in the registry,
                # the device didn't ask and nothing was copied)
                Invoke-LabGuest -Name $device.Name -ScriptBlock {
                    $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'; if (-not (Test-Path $k)) { $null = New-Item $k -Force }; Set-ItemProperty $k -Name fPromptForPassword -Value 1 -Type DWord
                }
                Restart-LabRdpService -Name $device.Name
                $null = Ui 'Device' -Value doubleclick
                $null = Ui 'Yes' -Title '*Remote Desktop*' -Optional -TimeoutSec 20
                $a.credDialog = Ui '*' -Title 'Credential Dialog Xaml Host' -Value exists -Optional -TimeoutSec 60
                Start-Sleep -Seconds 3
                Shot 'aud-pwcopy'
                foreach ($i in 1..2) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B; Start-Sleep -Seconds 1 }
                $null = Ui 'Close this tab' -Optional -TimeoutSec 10
            }
            Invoke-LabGuest -Name $device.Name -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -Name fPromptForPassword -ErrorAction SilentlyContinue }
            Restart-LabRdpService -Name $device.Name
            # connect, record 8 s, disconnect
            Step $a 'rdp' {
                $a.connected = Connect-Device
                $null = Ui 'Record this tab to a video file' -TimeoutSec 20
                $null = Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 10
                Start-Sleep -Seconds 8
                $null = Ui 'Stop recording and save the video'
                $a.recordingSaved = Box -Titles 'Recording saved', '*.mp4*' -Button 'Close' -TimeoutSec 30
                $null = Ui 'Disconnect'
                Start-Sleep -Seconds 5
                $null = Ui 'Close this tab' -Optional -TimeoutSec 10
            }
            # edit a host, delete a connection
            Step $a 'edit' {
                $null = Ui 'Device2' -Value 'menu:Edit?'
                $null = Ui 'Computer name or IP address' -Title 'Edit connection' -Value "setnext:$newHost"
                $null = Ui 'Save' -Title 'Edit connection'
                Start-Sleep -Seconds 3
            }
            Step $a 'delete' {
                $null = Ui 'ToDelete' -Value 'menu:Delete'
                $null = Ui 'Delete' -Title '?*' -TimeoutSec 15
                Start-Sleep -Seconds 3
            }
            # files: a folder uploaded to an SMB share of the device (a network share of the connection, RemoteHub's
            # file transfer tab)
            Step $a 'files' {
                $null = Ui 'Device' -Value 'menu:Tools>Add network share*'
                $null = Ui 'Name' -Title 'Add network share to Device' -Value 'setnext:labshare'
                $null = Ui 'Share name' -Title 'Add network share to Device' -Value 'setnext:rhupload'
                $null = Ui 'Add' -Title 'Add network share to Device'
                Start-Sleep -Seconds 3
                $null = Ui 'labshare' -Value doubleclick -TimeoutSec 20
                $null = Ui 'Path This PC' -Value 'keys:^aC:\Users\Public{ENTER}' -TimeoutSec 60
                Start-Sleep -Seconds 3
                $null = Ui 'UpFolder' -Value realclick
                $null = Ui 'Upload selected files'
                Start-Sleep -Seconds 15
                $a.uploaded = @(Invoke-LabGuest -Name $device.Name -ScriptBlock { Get-ChildItem 'C:\rhupload' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object Name })
                Shot 'aud-files'
            }
            # all entries to CSV
            Step $a 'csvAll' {
                Settings-Page 'Logs'
                $null = Ui 'Open audit log?' -Title 'Settings'
                $null = Ui 'Export CSV?' -Title 'Audit log' -TimeoutSec 30
                FileDialog 'C:\lab\audit-all.csv'
                Start-Sleep -Seconds 3
                $a.csvAllStatus = (Ui '*' -Title 'Audit log' -Value list).items -join "`n"
                $null = Ui 'Close' -Title 'Audit log' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $a.csvAll = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Content 'C:\lab\audit-all.csv' -Raw -ErrorAction SilentlyContinue }
            # C-AUD-04: names a spreadsheet would run as a formula
            $a.names = @("=cmd|'/c calc'!A1", '+1', '-2', '@SUM(1)')
            $i = 0
            foreach ($n in $a.names) { $i++; Step $a "name$i" { New-LabClientConnection -Name $VmName -Display $n -TargetHost "aud4-$i.lab.test" } }
            Step $a 'csv' {
                Settings-Page 'Logs'
                $null = Ui 'Open audit log?' -Title 'Settings'
                $null = Ui 'Export CSV?' -Title 'Audit log' -TimeoutSec 30
                FileDialog 'C:\lab\audit.csv'
                Start-Sleep -Seconds 3
                $a.csvStatus = (Ui '*' -Title 'Audit log' -Value list).items -join "`n"
                $null = Ui 'Close' -Title 'Audit log' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $a.csv = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Content 'C:\lab\audit.csv' -Raw -ErrorAction SilentlyContinue }
            $null = Close-Rh
            $a.dump1 = Dump
            $a.recordings = @(Invoke-LabGuest -Name $VmName -ArgumentList $adminHome -ScriptBlock { param($h) Get-ChildItem "$h\Videos\RemoteHub" -Filter *.mp4 -ErrorAction SilentlyContinue | ForEach-Object Name })
            Save 'aud01' @{ steps = $a; audit = $a.dump1.audit }

            # C-AUD-02: RemoteHub closed; with the database key: update, delete, then the trigger dropped and an entry changed
            $a.sql = InSession @'
$p = Join-Path $env:LOCALAPPDATA 'RemoteHub\remotehub.db'
$k = Get-RhKeys (Join-Path $env:LOCALAPPDATA 'RemoteHub\db.key')
$id = [long](@(Invoke-RhSql $p $k.Db 'SELECT id FROM audit ORDER BY id LIMIT 1 OFFSET 1')[0].id)
[pscustomobject]@{
    id      = $id
    update  = Test-RhSql $p $k.Db "UPDATE audit SET detail = 'x' WHERE id = $id"
    delete  = Test-RhSql $p $k.Db "DELETE FROM audit WHERE id = $id"
    drop    = Test-RhSql $p $k.Db 'DROP TRIGGER audit_no_update'
    tamper  = Test-RhSql $p $k.Db "UPDATE audit SET detail = 'changed by the lab' WHERE id = $id"
    restore = Test-RhSql $p $k.Db "CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit BEGIN SELECT RAISE(ABORT, 'The audit log is append-only.'); END"
}
'@
            Start-Client
            Step $a 'verify' {
                Settings-Page 'Logs'
                $null = Ui 'Open audit log?' -Title 'Settings'
                $null = Ui 'Verify integrity' -Title 'Audit log' -TimeoutSec 30
                $a.verify = Box -Titles '*was changed after it was written*', '*entries*', 'Audit log changed', 'Audit log intact' -Button 'OK' -TimeoutSec 90
                Shot 'aud02'
                $null = Ui 'Close' -Title 'Audit log' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $null = Close-Rh
            Save 'aud02' @{ sql = $a.sql; verify = $a.verify }

            # C-AUD-03: entries written 3 days ago (new database, clock moved back), then AuditRetentionDays = 1
            Clear-RhData
            try {
                Set-GuestClock -Days -3
                Start-Client
                New-LabClientConnection -Name $VmName -Display 'OldEntry' -TargetHost 'oldentry.lab.test'
                $null = Close-Rh
            }
            finally { Set-GuestClock -Days 0 }
            $a.before3 = Dump
            Policy 'AuditRetentionDays' 1
            try {
                Start-Client
                New-LabClientConnection -Name $VmName -Display 'NewEntry' -TargetHost 'newentry.lab.test'
                Start-Sleep -Seconds 30
                Step $a 'retention' {
                    Settings-Page 'Logs'
                    $null = Ui 'Open audit log?' -Title 'Settings'
                    Start-Sleep -Seconds 3
                    $a.info = (Ui '*' -Title 'Audit log' -Value list).items -join "`n"
                    $null = Ui 'Close' -Title 'Audit log' -Optional
                    $null = Ui 'Cancel' -Title 'Settings' -Optional
                }
                $null = Close-Rh
                $a.after3 = Dump
            }
            finally { Policy 'AuditRetentionDays' $null }
            Save 'aud03' @{ info = $a.info; before = $a.before3.audit; after = $a.after3.audit; nowBefore = $a.before3.nowMs; nowAfter = $a.after3.nowMs }
        }

        It 'C-AUD-01 connect, disconnect, edit, delete, password copied, files, recording: entries without passwords, values or file content' -Tag 'C-AUD-01' {
            $events = @($a.dump1.audit | ForEach-Object event)
            foreach ($e in 'session.connected', 'session.ended', 'data.changed', 'data.deleted', 'password.copied', 'files.transferred', 'recording.started', 'recording.stopped') {
                $events | Should -Contain $e
            }
            (@($a.dump1.audit | Where-Object { $_.event -eq 'data.changed' -and $_.target -eq 'Device2' } | ForEach-Object detail) -join "`n") | Should -Match '(?m)^Connection: (.*, )?Host(,|$)'
            @($a.dump1.audit | Where-Object { $_.event -eq 'data.deleted' } | ForEach-Object target) | Should -Contain 'ToDelete'
            @($a.dump1.audit | Where-Object event -eq 'password.copied' | ForEach-Object detail) | Should -Contain 'For the Windows password prompt (cleared after 30 s)'
            foreach ($label in 'Connected', 'Disconnected', 'Changed', 'Deleted', 'Password copied', 'Files transferred', 'Recording started', 'Recording saved') {
                $a.csvAll | Should -Match ",$([regex]::Escape($label)),"
            }
            $everything = "$($a.csvAll)`n$(@($a.dump1.audit | ForEach-Object { "$($_.target) $($_.detail)" }) -join "`n")"
            $everything.Contains($password) | Should -BeFalse -Because 'no password in any entry'
            $everything.Contains($newHost) | Should -BeFalse -Because 'no field value in any entry'
            $everything | Should -Not -Match 'c-data audit file content marker'
        }

        It 'C-AUD-02 append-only: UPDATE and DELETE refused; Verify integrity finds the entry changed with the trigger dropped' -Tag 'C-AUD-02' {
            $a.sql.update.ok | Should -BeFalse
            $a.sql.update.error | Should -Match 'The audit log is append-only\.'
            $a.sql.delete.ok | Should -BeFalse
            $a.sql.delete.error | Should -Match 'Only audit entries past the retention period can be deleted\.'
            $a.sql.drop.ok | Should -BeTrue
            $a.sql.tamper.ok | Should -BeTrue
            $a.verify | Should -Match "Entry $($a.sql.id) \(.+\) was changed after it was written\."
        }

        It 'C-AUD-03 AuditRetentionDays=1: "kept for 1 days (set by your organization)", older entries pruned, newer ones stay' -Tag 'C-AUD-03' {
            $dayMs = 86400000
            @($a.before3.audit | Where-Object { $_.time_utc -lt ($a.before3.nowMs - 2 * $dayMs) }).Count | Should -BeGreaterThan 0 -Because 'entries older than 2 days were written'
            $a.info | Should -Match 'Entries are kept for 1 days \(set by your organi[sz]ation\)\.'
            @($a.after3.audit | Where-Object { $_.time_utc -lt ($a.after3.nowMs - $dayMs) }) | Should -BeNullOrEmpty
            @($a.after3.audit | Where-Object { $_.event -eq 'data.added' -and $_.target -eq 'NewEntry' }) | Should -Not -BeNullOrEmpty
        }

        It 'C-AUD-04 CSV export: header, formula-like names defused with an apostrophe, status line' -Tag 'C-AUD-04' {
            $lines = @("$($a.csv)" -split "`r?`n")
            $lines[0] | Should -Be 'Time (UTC),Event,Event code,Name,Details,Device,Account,Connection id'
            foreach ($n in $a.names) { $a.csv.Contains(",'$n,") | Should -BeTrue -Because "the name $n starts with an apostrophe" }
            $a.csvStatus | Should -Match '\d+ entries exported to C:\\lab\\audit\.csv'
        }
    }

    Context 'startup protection' {
        BeforeAll {
            $l = [ordered]@{}
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
            $null = Close-Rh
            Invoke-LabGuest -Name $VmName -ArgumentList $localDir -ScriptBlock { param($p) Copy-Item "$p\db.key" 'C:\lab\db.key.old' -Force }
            # C-LOCK-01: turned on
            Start-Client
            Settings-Page 'Security & storage'
            $null = Ui 'Startup protection?' -Title 'Settings'
            $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$lockPw"
            $null = Ui 'type:Edit#2' -Title 'Startup protection' -Value "set:$lockPw"
            $l.helloBox = Ui 'Also unlock with Windows Hello*' -Title 'Startup protection' -Value exists -Optional -TimeoutSec 5
            $null = Ui 'Turn on' -Title 'Startup protection'
            $deadline = (Get-Date).AddSeconds(60)
            do { Start-Sleep -Seconds 3; $on = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Test-Path "$r\lock.json" } } until ($on -or (Get-Date) -gt $deadline)
            Start-Sleep -Seconds 5
            $l.onText = (Ui '*' -Title 'Startup protection' -Value list -Optional).items -join "`n"
            $null = Ui 'Close' -Title 'Startup protection' -Optional
            $null = Ui 'Cancel' -Title 'Settings' -Optional
            $null = Close-Rh
            # restart: asked first
            Start-Rh
            $l.lockedText = (Ui '*' -Title 'RemoteHub*locked' -Value list -Optional -TimeoutSec 90).items -join "`n"
            $l.mainBefore = Ui 'SearchBox' -Value exists -Optional -TimeoutSec 3
            $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value 'set:Wrong-Password-1'
            $null = Ui 'Unlock' -Title 'RemoteHub*locked'
            Start-Sleep -Seconds 8
            $l.wrongText = (Ui '*' -Title 'RemoteHub*locked' -Value list -Optional).items -join "`n"
            $l.mainAfterWrong = Ui 'SearchBox' -Value exists -Optional -TimeoutSec 3
            $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw"
            $null = Ui 'Unlock' -Title 'RemoteHub*locked'
            $l.mainAfterRight = Wait-Main
            $l.connection = Ui 'Device' -Value exists -Optional -TimeoutSec 10
            Shot 'lock01'
            $null = Close-Rh
            $l.files1 = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
                param($lo, $r) [pscustomobject]@{ lock = (Test-Path "$r\lock.json"); keyHead = @([IO.File]::ReadAllBytes("$lo\db.key")[0..4] | ForEach-Object { [int]$_ }) }
            }
            $l.dump1 = Dump -Lock $lockPw
            $l.oldKey = InSession 'try { $k = Get-RhKeys "C:\lab\db.key.old"; Test-RhSql (Join-Path $env:LOCALAPPDATA "RemoteHub\remotehub.db") $k.Db "SELECT count(*) FROM nodes" } catch { [pscustomobject]@{ ok = $false; code = -2; error = "$_" } }'
            Save 'lock01' @{ l = $l; audit = $l.dump1.audit }

            # C-LOCK-02: lock after 1 minute, an RDP tab connected, no input until due
            Start-Rh
            $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw" -TimeoutSec 90
            $null = Ui 'Unlock' -Title 'RemoteHub*locked'
            $l.main2 = Wait-Main
            Step $l 'autolock' {
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$lockPw"
                # the shortest time offered: 1 minute
                $l.choose = Ui "Lock when you?re away" -Title 'Startup protection' -Value 'choose:After 1 minute'
                $l.lockMinutes = 1
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
                $l.sessionBefore = Connect-Device
                # nothing that is input on this PC from here on (UI Automation reads aren't input); RemoteHub checks
                # every 15 s, so the lock is due within the time + 15 s
                Start-Sleep -Seconds ($l.lockMinutes * 60 + 30)
                $l.lockedText2 = (Ui '*' -Title 'RemoteHub*locked' -Value list -Optional -TimeoutSec 30).items -join "`n"
                $l.mainHidden = Ui 'SearchBox' -Value exists -Optional -TimeoutSec 3
                $l.sessionLocked = Get-DeviceSession
                Shot 'lock02-locked'
                $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw"
                $null = Ui 'Unlock' -Title 'RemoteHub*locked'
                $l.mainBack = Wait-Main -TimeoutSec 60
                $l.tabBack = Ui 'Disconnect' -Value exists -Optional -TimeoutSec 10
                $l.sessionAfter = Get-DeviceSession
            }
            $null = Close-Rh
            $l.dump2 = Dump -Lock $lockPw
            Save 'lock02' @{ l = $l; audit = $l.dump2.audit }

            # C-LOCK-04: turned off; copies of the locked key and lock file kept to try them afterwards
            $l.before4 = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
                param($lo, $r)
                Copy-Item "$lo\db.key" 'C:\lab\db.key.locked' -Force
                Copy-Item "$r\lock.json" 'C:\lab\lock.json.locked' -Force
                @(Get-ChildItem "$lo\backup" -Filter 'remotehub-*.db' -ErrorAction SilentlyContinue | ForEach-Object Name)
            }
            Start-Rh
            $null = Ui 'type:Edit' -Title 'RemoteHub*locked' -Value "set:$lockPw" -TimeoutSec 90
            $null = Ui 'Unlock' -Title 'RemoteHub*locked'
            $l.main4 = Wait-Main
            Step $l 'off' {
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$lockPw"
                $null = Ui 'Turn off startup protection' -Title 'Startup protection'
                $l.offConfirm = Box -Titles 'RemoteHub will open without*', 'Turn off startup protection' -Button 'Turn off' -TimeoutSec 30
                $deadline = (Get-Date).AddSeconds(60)
                do { Start-Sleep -Seconds 3; $off = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) -not (Test-Path "$r\lock.json") } } until ($off -or (Get-Date) -gt $deadline)
                Start-Sleep -Seconds 5
                $null = Ui 'Close' -Title 'Startup protection' -Optional
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $null = Close-Rh
            Start-Rh
            $l.offMain = Wait-Main
            $l.offLocked = Ui '*' -Title 'RemoteHub*locked' -Value exists -Optional -TimeoutSec 3
            $null = Close-Rh
            $l.files4 = Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
                param($lo, $r)
                [pscustomobject]@{
                    lock     = (Test-Path "$r\lock.json")
                    keyHead  = @([IO.File]::ReadAllBytes("$lo\db.key")[0..4] | ForEach-Object { [int]$_ })
                    backups  = @(Get-ChildItem "$lo\backup" -Filter 'remotehub-*.db' -ErrorAction SilentlyContinue | ForEach-Object Name)
                    migrated = (Test-Path "$r\settings.dat.migrated")
                    bak      = (Test-Path "$r\settings.dat.bak")
                }
            }
            $l.lockedKey = InSession 'try { $app = Get-RhAppKey "C:\lab\lock.json.locked" $A.pw; $k = Get-RhKeys "C:\lab\db.key.locked" $app; Test-RhSql (Join-Path $env:LOCALAPPDATA "RemoteHub\remotehub.db") $k.Db "SELECT count(*) FROM nodes" } catch { [pscustomobject]@{ ok = $false; code = -2; error = "$_" } }' @{ pw = $lockPw }
            Save 'lock04' $l

            # C-LOCK-03: Windows Hello needs a virtual TPM, a Hello PIN for the console account and the PIN (lab.json HelloPin)
            $l.vtpm = [bool](Get-VMSecurity -VMName $vm.Name).TpmEnabled
            $pin = if ($cfg.PSObject.Properties['HelloPin']) { "$($cfg.HelloPin)" } else { '' }
            if (-not $l.vtpm) { $l.helloSkip = 'the VM has no virtual TPM - Windows Hello needs one' }
            elseif (-not $pin) { $l.helloSkip = 'no HelloPin in lab.json - the PIN of the console account''s Windows Hello is needed to answer the prompt' }
            else {
                Start-Client
                Settings-Page 'Security & storage'
                $null = Ui 'Startup protection?' -Title 'Settings'
                $hello = Ui 'Also unlock with Windows Hello*' -Title 'Startup protection' -Value exists -Optional -TimeoutSec 10
                if (-not $hello.enabled) {
                    $l.helloSkip = 'Windows Hello isn''t set up for the console account (RemoteHub offers no Hello)'
                    $null = Ui 'Close' -Title 'Startup protection' -Optional
                    $null = Ui 'Cancel' -Title 'Settings' -Optional
                }
                else {
                    Step $l 'hello' {
                        $null = Ui 'type:Edit#1' -Title 'Startup protection' -Value "set:$lockPw"
                        $null = Ui 'type:Edit#2' -Title 'Startup protection' -Value "set:$lockPw"
                        $null = Ui 'Also unlock with Windows Hello*' -Title 'Startup protection' -Value check
                        $null = Ui 'Turn on' -Title 'Startup protection'
                        Start-Sleep -Seconds 10
                        Send-LabText -Name $VmName -Text $pin; Send-LabKey -Name $VmName -Key Enter
                        Start-Sleep -Seconds 10
                        $null = Ui 'Close' -Title 'Startup protection' -Optional
                        $null = Ui 'Cancel' -Title 'Settings' -Optional
                        $null = Close-Rh
                        Start-Rh
                        Start-Sleep -Seconds 10
                        $l.helloText = (Ui '*' -Title 'RemoteHub*locked' -Value list -Optional -TimeoutSec 60).items -join "`n"
                        Shot 'lock03-prompt'
                        Send-LabText -Name $VmName -Text $pin; Send-LabKey -Name $VmName -Key Enter
                        $l.helloMain = Wait-Main -TimeoutSec 60
                        $null = Close-Rh
                        $l.dump3 = Dump -Lock $lockPw
                    }
                }
            }
            Save 'lock03' @{ vtpm = $l.vtpm; skip = $l.helloSkip; text = $l.helloText; main = $l.helloMain; audit = $l.dump3.audit }
        }

        It 'C-LOCK-01 startup password: asked before the main window, wrong one refused, right one opens; db re-encrypted (the old db.key opens nothing)' -Tag 'C-LOCK-01' {
            $l.onText | Should -Match 'On . startup password'
            $l.lockedText | Should -Match 'RemoteHub is locked'
            $l.lockedText | Should -Match 'Enter the startup password to open your connections\.'
            $l.mainBefore.found | Should -BeFalse
            $l.wrongText | Should -Match 'Wrong password\.'
            $l.mainAfterWrong.found | Should -BeFalse
            $l.mainAfterRight | Should -BeTrue
            $l.connection.found | Should -BeTrue
            @($l.dump1.audit | Where-Object event -eq 'app.unlock_failed') | Should -Not -BeNullOrEmpty
            @($l.dump1.audit | Where-Object event -eq 'app.unlocked' | ForEach-Object detail) | Should -Contain 'With the startup password'
            $l.files1.lock | Should -BeTrue
            (@($l.files1.keyHead) -join ',') | Should -Be '82,72,68,75,2'
            $l.oldKey.ok | Should -BeFalse -Because 'the database was encrypted with a new key'
            $l.oldKey.code | Should -BeIn 11, 26 -Because "the old key itself is readable and the database refuses it ($($l.oldKey.error))"
        }

        It 'C-LOCK-02 auto-lock after 1 min without input: windows hidden, password asked, the session keeps running' -Tag 'C-LOCK-02' {
            $l['autolock-error'] | Should -BeNullOrEmpty
            $l.choose.value | Should -Be 'After 1 minute'
            $l.sessionBefore | Should -BeTrue
            $l.lockedText2 | Should -Match 'Locked after a while without input . your sessions keep running\.'
            $l.lockedText2 | Should -Match 'Exit RemoteHub'
            $l.mainHidden.found | Should -BeFalse
            $l.sessionLocked | Should -BeTrue
            $l.mainBack | Should -BeTrue
            $l.tabBack.found | Should -BeTrue
            $l.sessionAfter | Should -BeTrue
            @($l.dump2.audit | Where-Object event -eq 'app.locked' | ForEach-Object detail) | Should -Contain "After $($l.lockMinutes) min without input"
        }

        It 'C-LOCK-03 Windows Hello PIN unlocks RemoteHub (vTPM)' -Tag 'C-LOCK-03' {
            if ($l.helloSkip) { Set-ItResult -Skipped -Because $l.helloSkip; return }
            $l['hello-error'] | Should -BeNullOrEmpty
            $l.helloText | Should -Match 'Unlock with Windows Hello \(PIN, fingerprint or face\) or the startup password\.'
            $l.helloMain | Should -BeTrue
            @($l.dump3.audit | Where-Object event -eq 'app.unlocked' | ForEach-Object detail) | Should -Contain 'With Windows Hello'
        }

        It 'C-LOCK-04 turned off: opens without asking, lock.json gone, db.key RHDK 0x01, old backups gone, the locked key opens nothing' -Tag 'C-LOCK-04' {
            $l['off-error'] | Should -BeNullOrEmpty
            $l.offConfirm | Should -Match 'Turn it off\?'
            $l.offMain | Should -BeTrue
            $l.offLocked.found | Should -BeFalse
            $l.files4.lock | Should -BeFalse
            (@($l.files4.keyHead) -join ',') | Should -Be '82,72,68,75,1'
            @($l.before4).Count | Should -BeGreaterThan 0 -Because 'there were backups before'
            @(@($l.files4.backups) | Where-Object { @($l.before4) -contains $_ }) | Should -BeNullOrEmpty
            @($l.files4.backups).Count | Should -BeLessOrEqual 1
            $l.files4.migrated | Should -BeFalse
            $l.files4.bak | Should -BeFalse
            $l.lockedKey.ok | Should -BeFalse
            $l.lockedKey.code | Should -BeIn 11, 26 -Because "the locked key itself is readable and the database refuses it ($($l.lockedKey.error))"
        }
    }

    Context 'shared settings folder on the device''s SMB share' {
        BeforeAll {
            $s = [ordered]@{}
            $share = "\\$deviceIp\rhsettings"
            Clear-RhData
            # Admin's access to the share (the device's PSAdmin)
            $s.access = InSession 'cmdkey "/add:$($A.ip)" "/user:$($A.user)" "/pass:$($A.pw)" | Out-Null; Test-Path $A.share' @{ ip = $deviceIp; user = "$deviceName\$($device.AdminAccount)"; pw = $password; share = $share }
            # C-SHARE-01
            Start-Client
            New-LabClientConnection -Name $VmName -Display 'ConnA' -TargetHost 'conna.lab.test'
            New-LabClientConnection -Name $VmName -Display 'ConnB' -TargetHost 'connb.lab.test'
            Step $s 'location' {
                Settings-Page 'Security & storage'
                $null = Ui 'Change location?' -Title 'Settings'
                $null = Ui 'Shared folder*' -Title 'Settings location' -Value check
                $null = Ui 'type:Edit' -Title 'Settings location' -Value "set:$share"
                $null = Ui 'Apply' -Title 'Settings location'
                $null = Ui 'type:Edit#1' -Title 'Protect shared settings' -Value "set:$sharePw"
                $null = Ui 'type:Edit#2' -Title 'Protect shared settings' -Value "set:$sharePw"
                $null = Ui 'Create' -Title 'Protect shared settings'
                $deadline = (Get-Date).AddSeconds(90)
                do { Start-Sleep -Seconds 3; $h = Get-SharedHash } until ($h -or (Get-Date) -gt $deadline)
            }
            New-LabClientConnection -Name $VmName -Display 'ShareConn1' -TargetHost 'shareconn1.lab.test'
            Start-Sleep -Seconds 3
            $null = Close-Rh
            Start-Rh
            $s.prompt = Ui '*' -Title 'Unlock shared settings' -Value exists -Optional -TimeoutSec 30
            if ($s.prompt.found) {
                $null = Ui 'type:Edit' -Title 'Unlock shared settings' -Value "set:$sharePw"
                $null = Ui 'Unlock' -Title 'Unlock shared settings'
            }
            $s.main1 = Wait-Main
            $s.conn1 = Ui 'ShareConn1' -Value exists -Optional -TimeoutSec 10
            $s.shareFiles = @(Invoke-LabGuest -Name $device.Name -ScriptBlock { Get-ChildItem 'C:\rhsettings' -Force | ForEach-Object Name })
            $s.keysDat = Invoke-LabGuest -Name $VmName -ArgumentList $roamDir -ScriptBlock { param($r) Test-Path "$r\keys.dat" }
            Shot 'share01'
            Save 'share01' $s

            # C-SHARE-02: a second RemoteHub on the device (its console account), the same file through its local path
            Step $s 'second' {
                $null = Install-LabClient -Name $device.Name
                Start-Client -On $device.Name
                Settings-Page 'Security & storage' -On $device.Name
                $null = Ui 'Change location?' -Title 'Settings' -On $device.Name
                $null = Ui 'Shared folder*' -Title 'Settings location' -Value check -On $device.Name
                $null = Ui 'type:Edit' -Title 'Settings location' -Value 'set:C:\rhsettings' -On $device.Name
                $null = Ui 'Apply' -Title 'Settings location' -On $device.Name
                $null = Ui 'type:Edit' -Title 'Open shared settings' -Value "set:$sharePw" -On $device.Name
                $null = Ui 'Open' -Title 'Open shared settings' -On $device.Name
                $s.bLoaded = Ui 'ConnA' -Value exists -Optional -TimeoutSec 90 -On $device.Name
            }
            # run 1: A edits ConnA (not saved), B renames ConnB and saves, A saves -> Keep mine
            Step $s 'keep' {
                $null = Ui 'ConnA' -Value 'menu:Edit?'
                $null = Ui 'Display name' -Title 'Edit connection' -Value 'setnext:ConnA-mine'
                $null = Ui 'ConnB' -Value 'menu:Edit?' -On $device.Name
                $null = Ui 'Display name' -Title 'Edit connection' -Value 'setnext:ConnB-other' -On $device.Name
                $s.k1Start = Get-SharedHash   # before B's save (B's save must change it)
                $s.k1BSave = Ui 'Save' -Title 'Edit connection' -On $device.Name
                $s.k1Before = Wait-SharedChange $s.k1Start
                # recorded only: a prompt A may show by itself when it sees the file change
                # A noticed B's save and asks to reload: declined, so A's own save meets the changed file (the conflict question)
                $s.k1Early = Box -Titles 'The settings file was updated*', 'Settings updated' -Button 'Cancel' -TimeoutSec 15
                $null = Ui 'Save' -Title 'Edit connection'
                Start-Sleep -Seconds 5
                $s.k1Dialog = Box -Titles 'The settings file was changed on another PC*', 'Settings changed on another device' -Button '' -TimeoutSec 30
                $s.k1BeforeChoice = Get-SharedHash
                $s.k1Title = Box-Title $s.k1Dialog
                Shot 'share02-keep'
                $null = Ui 'Keep mine' -Title '?*' -Optional -TimeoutSec 10
                Start-Sleep -Seconds 6
                $s.k1After = Get-SharedHash
                # B sees the file change: it offers to reload
                $s.k1BPrompt = Box -Titles 'The settings file was updated*', 'Settings updated' -Button 'Reload' -TimeoutSec 30 -On $device.Name
                Start-Sleep -Seconds 3
                $s.k1BA = Ui 'ConnA-mine' -Value exists -Optional -TimeoutSec 10 -On $device.Name
                $s.k1BB = Ui 'ConnB-other' -Value exists -Optional -TimeoutSec 3 -On $device.Name
            }
            $null = Box -Titles 'The settings file was updated*', 'Settings updated' -Button 'Cancel' -TimeoutSec 3
            # run 2: the same, A answers Reload
            Step $s 'reload' {
                $null = Ui 'ConnA-mine' -Value 'menu:Edit?'
                $null = Ui 'Display name' -Title 'Edit connection' -Value 'setnext:ConnA-mine2'
                $null = Ui 'ConnB' -Value 'menu:Edit?' -On $device.Name
                $null = Ui 'Display name' -Title 'Edit connection' -Value 'setnext:ConnB-other2' -On $device.Name
                $s.r2Start = Get-SharedHash   # before B's save (B's save must change it)
                $s.r2BSave = Ui 'Save' -Title 'Edit connection' -On $device.Name
                $s.r2Before = Wait-SharedChange $s.r2Start
                # A noticed B's save and asks to reload: declined, so A's own save meets the changed file (the conflict question)
                $s.r2Early = Box -Titles 'The settings file was updated*', 'Settings updated' -Button 'Cancel' -TimeoutSec 15
                $null = Ui 'Save' -Title 'Edit connection'
                Start-Sleep -Seconds 5
                $s.r2Dialog = Box -Titles 'The settings file was changed on another PC*', 'Settings changed on another device' -Button '' -TimeoutSec 30
                $s.r2BeforeChoice = Get-SharedHash
                $s.r2Title = Box-Title $s.r2Dialog
                Shot 'share02-reload'
                $null = Ui 'Reload' -Title '?*' -Optional -TimeoutSec 10
                Start-Sleep -Seconds 6
                $s.r2After = Get-SharedHash
                $s.r2BOther = Ui 'ConnB-other2' -Value exists -Optional -TimeoutSec 10
                $s.r2AMine2 = Ui 'ConnA-mine2' -Value exists -Optional -TimeoutSec 3
                $s.r2AMine = Ui 'ConnA-mine' -Value exists -Optional -TimeoutSec 5
            }
            $null = Box -Titles 'The settings file was updated*', 'Settings updated' -Button 'Cancel' -TimeoutSec 3
            try { $null = Close-Rh -On $device.Name } catch { }
            Save 'share02' $s

            # C-SHARE-03: the share unreachable at start (this VM's network cut)
            $null = Close-Rh
            $s.h0 = Get-SharedHash
            $net = Disconnect-LabNetwork -Name $VmName
            try {
                Start-Rh
                $s.offMsg = Box -Titles 'RemoteHub' -Button 'OK' -TimeoutSec 240
                $s.offMain = Wait-Main -TimeoutSec 120
                Shot 'share03'
                Step $s 'offline' { New-LabClientConnection -Name $VmName -Display 'OfflineConn' -TargetHost 'offline.lab.test' }
                $null = Close-Rh
            }
            finally { Connect-LabNetwork -Name $VmName -Switches $net }
            $s.h1 = Get-SharedHash
            Save 'share03' $s
        }

        It 'C-SHARE-01 settings on the SMB share: RemoteHub.settings there, no password prompt at the next start, keys.dat remembers it' -Tag 'C-SHARE-01' {
            $s.access | Should -BeTrue -Because 'Admin reaches the share'
            $s['location-error'] | Should -BeNullOrEmpty
            @($s.shareFiles) | Should -Contain 'RemoteHub.settings'
            $s.prompt.found | Should -BeFalse
            $s.main1 | Should -BeTrue
            $s.conn1.found | Should -BeTrue
            $s.keysDat | Should -BeTrue
        }

        It 'C-SHARE-02 sync conflict with a second PC: "Settings changed on another device" with Reload / Keep mine, nothing overwritten before the choice' -Tag 'C-SHARE-02' {
            $s['second-error'] | Should -BeNullOrEmpty
            $s.bLoaded.found | Should -BeTrue
            # Keep mine
            # B's save is the first edit after it opened the shared file: see the findings table (C-SHARE-02)
            "$($s.k1Before)" | Should -Not -Be "$($s.k1Start)" -Because 'B''s save changed the shared file (precondition)'
            $s.k1Title | Should -Be 'Settings changed on another device'
            $s.k1Dialog | Should -Match 'The settings file was changed on another PC \(or by another admin\) after RemoteHub loaded it\.'
            $s.k1Dialog | Should -Match 'Keep mine'
            $s.k1BeforeChoice | Should -Be $s.k1Before -Because 'the file is not written before the choice'
            $s.k1After | Should -Not -Be $s.k1Before -Because 'Keep mine writes this PC''s version'
            $s.k1BA.found | Should -BeTrue -Because 'the file holds A''s version'
            $s.k1BB.found | Should -BeFalse -Because 'B''s change is gone after Keep mine'
            # Reload
            "$($s.r2Before)" | Should -Not -Be "$($s.r2Start)" -Because 'B''s save changed the shared file (precondition)'
            $s.r2Title | Should -Be 'Settings changed on another device'
            $s.r2Dialog | Should -Match 'The settings file was changed on another PC \(or by another admin\) after RemoteHub loaded it\.'
            $s.r2BeforeChoice | Should -Be $s.r2Before
            $s.r2After | Should -Be $s.r2Before -Because 'Reload doesn''t write the file'
            $s.r2BOther.found | Should -BeTrue -Because 'B''s change is shown'
            $s.r2AMine2.found | Should -BeFalse -Because 'A''s edit is gone'
            $s.r2AMine.found | Should -BeTrue
        }

        It 'C-SHARE-03 share offline at start: message, RemoteHub starts with this PC''s settings, the shared file unchanged' -Tag 'C-SHARE-03' {
            $s.h0 | Should -Not -BeNullOrEmpty
            $s.offMsg | Should -Match 'The shared settings file could not be opened:'
            $s.offMsg | Should -Match 'RemoteHub starts with the settings stored on this PC\. The shared file is not changed\.\s*Restart the app to try again\.'
            $s.offMain | Should -BeTrue
            $s.h1 | Should -Be $s.h0
        }
    }

    Context 'backups and another Windows account' {
        BeforeAll {
            $b = [ordered]@{}
            # Admin: folders, connections and a saved credential with a password (a 0.13 settings.dat moved in)
            Clear-RhData
            $legacyB = New-Legacy 'B'
            $null = InSession 'New-RhLegacySettings $A.json' @{ json = $legacyB.json }
            Start-Client
            Step $b 'export' {
                Settings-Page 'Security & storage'
                $null = Ui 'Export backup?' -Title 'Settings'
                FileDialog 'C:\lab\rh.rhbackup'
                $null = Ui 'type:Edit#1' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'type:Edit#2' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'Export' -Title 'Protect backup'
                $b.exported = Box -Titles 'Backup saved to*', 'Export backup' -Button 'OK' -TimeoutSec 60
                $null = Ui 'Cancel' -Title 'Settings' -Optional
            }
            $null = Close-Rh
            $b.admin = Dump
            # the old format: RDP Manager's .rdpmbackup - format "rdpmanager-backup", version 1, sealed without associated
            # data. The harness writes it from this backup's settings: relabelling the current file isn't an old file -
            # since version 4 its sections are bound to the format and a relabelled one is refused on purpose (#63; run
            # 20261021-222745: no import question came)
            Step $b 'oldfile' {
                $hostDir = Join-Path $EvidenceDir 'c-data-backups'
                $null = New-Item -ItemType Directory -Path $hostDir -Force
                Get-LabFile -Name $VmName -Path 'C:\lab\rh.rhbackup' -Destination $hostDir
                $b.oldMade = Invoke-LabHarness @('old-backup', '--in', (Join-Path $hostDir 'rh.rhbackup'), '--out', (Join-Path $hostDir 'old.rdpmbackup'), '--password', $backupPw) -TimeoutSec 120
                Copy-LabFile -Name $VmName -Path (Join-Path $hostDir 'old.rdpmbackup') -Destination 'C:\lab'
            }
            $b.files = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\lab' -File | ForEach-Object Name) }
            Save 'bak-admin' @{ exported = $b.exported; files = $b.files; nodes = $b.admin.nodes; credentials = $b.admin.credentials; audit = $b.admin.audit; error = $b['export-error'] }

            # normaluser signed in from here on
            Set-LabAutoLogon -Name $VmName -Account User
            # C-DB-02: Admin's copied database in normaluser's session
            $b.harness = InSession 'try { $null = [RhLab.Db]::ReadKeyFile("C:\lab\dbcopy\db.key", $null); "read" } catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; $e.GetType().Name + ": " + $e.Message }' -Account User
            Start-Rh -Account User -DataDir 'C:\lab\dbcopy'
            $b.dbcopyMsg = Box -Titles 'RemoteHub' -Button 'Cancel' -TimeoutSec 90 -Account User
            Start-Sleep -Seconds 5
            $b.dbcopyMain = Ui 'SearchBox' -Value exists -Optional -TimeoutSec 5 -Account User
            $b.dbcopyFiles = @(Invoke-LabGuest -Name $VmName -ScriptBlock { Get-ChildItem 'C:\lab\dbcopy' -Force | ForEach-Object Name })
            Shot 'db02'
            $null = Close-Rh -Account User
            Save 'db02' @{ harness = $b.harness; msg = $b.dbcopyMsg; main = $b.dbcopyMain; files = $b.dbcopyFiles }

            Start-Client -Account User
            $b.before = Dump -Account User
            # C-BAK-02: the wrong password
            Step $b 'wrong' {
                Import-Backup 'C:\lab\rh.rhbackup' 'Wrong-Backup-Pw-1'
                # left open until its title is read (see Box-Title)
                $b.wrongMsg = Box -Titles 'Wrong password*', 'Import failed' -Button '' -TimeoutSec 90 -Account User
                $b.wrongTitle = Box-Title $b.wrongMsg -Account User
                $wrongWin = ("$($b.wrongMsg)" -split "`n" | Select-Object -First 1).TrimEnd("`r")
                if ($wrongWin) { $null = Ui 'OK' -Title ([WildcardPattern]::Escape($wrongWin) + '*') -Optional -TimeoutSec 10 -Account User }
            }
            Start-Sleep -Seconds 3
            $b.afterWrong = Dump -Account User
            # C-BAK-01: added as a folder, then replacing everything
            Step $b 'add' {
                Import-Backup 'C:\lab\rh.rhbackup' $backupPw
                $b.addAsk = Box -Titles 'Add the backup as a new folder*', 'Import backup' -Button 'Add as folder' -TimeoutSec 90 -Account User
                Start-Sleep -Seconds 5
                $b.folderShown = Ui 'Imported *' -Value exists -Optional -TimeoutSec 20 -Account User
            }
            $b.added = Dump -Account User
            Step $b 'replace' {
                Import-Backup 'C:\lab\rh.rhbackup' $backupPw
                $b.replaceAsk = Box -Titles 'Add the backup as a new folder*', 'Import backup' -Button 'Replace everything' -TimeoutSec 90 -Account User
                Start-Sleep -Seconds 5
            }
            $b.replaced = Dump -Account User
            # C-BAK-03: the old .rdpmbackup, added as a folder
            Step $b 'old' {
                Import-Backup 'C:\lab\old.rdpmbackup' $backupPw
                $b.oldAsk = Box -Titles 'Add the backup as a new folder*', 'Import backup' -Button 'Add as folder' -TimeoutSec 90 -Account User
                Start-Sleep -Seconds 5
            }
            Shot 'bak'
            $null = Close-Rh -Account User
            $b.old = Dump -Account User
            Clear-LabAutoLogon -Name $VmName
            Save 'bak-user' @{ steps = $b; before = $b.before.nodes; added = $b.added.nodes; replaced = $b.replaced.nodes; old = $b.old.nodes; audit = $b.old.audit }
        }

        It 'C-DB-02 another Windows user: normaluser can''t read Admin''s db.key; RemoteHub says so and shows nothing of Admin''s' -Tag 'C-DB-02' {
            @($b.dbcopyFiles) | Should -Contain 'db.key' -Because 'Admin''s database and key were copied for normaluser'
            $b.harness | Should -Not -Be 'read'
            $b.harness | Should -Match 'CryptographicException'
            $b.dbcopyMsg | Should -Match "RemoteHub's database can't be opened:\s*The database key can't be read with this Windows account\."
            $b.dbcopyMain.found | Should -BeFalse -Because 'no main window with Admin''s connections'
        }

        It 'C-BAK-01 .rhbackup round trip to normaluser: added as a folder and replacing everything, identical incl. credentials' -Tag 'C-BAK-01' {
            $b['export-error'] | Should -BeNullOrEmpty
            $b.exported | Should -Match 'Backup saved to\s+C:\\lab\\rh\.rhbackup'
            @($b.admin.audit | Where-Object event -eq 'backup.exported' | ForEach-Object target) | Should -Contain 'rh.rhbackup'
            $adminItems = Proj $b.admin
            $adminCreds = Creds $b.admin
            @($adminItems).Count | Should -Be 3
            # added as a folder "Imported yyyy-MM-dd HH:mm"
            $b['add-error'] | Should -BeNullOrEmpty
            $folder = @($b.added.nodes | Where-Object { -not $_.parent -and $_.kind -eq 'folder' -and $_.name -match '^Imported \d{4}-\d{2}-\d{2} \d{2}:\d{2}$' })
            $folder.Count | Should -Be 1
            $b.folderShown.found | Should -BeTrue
            ((Proj $b.added -Under $folder[0].name) -join "`n") | Should -Be ($adminItems -join "`n")
            foreach ($c in $adminCreds) { Creds $b.added | Should -Contain $c }
            # replacing everything
            $b['replace-error'] | Should -BeNullOrEmpty
            ((Proj $b.replaced) -join "`n") | Should -Be ($adminItems -join "`n")
            ((Creds $b.replaced) -join "`n") | Should -Be ($adminCreds -join "`n")
            $imported = @($b.replaced.audit | Where-Object event -eq 'backup.imported' | ForEach-Object detail)
            $imported | Should -Contain 'Added as a folder'
            $imported | Should -Contain 'Replaced all settings'
        }

        It 'C-BAK-02 wrong backup password: "Import failed", nothing imported, no "Backup imported" entry' -Tag 'C-BAK-02' {
            $b['wrong-error'] | Should -BeNullOrEmpty
            $b.wrongTitle | Should -Be 'Import failed'
            $b.wrongMsg | Should -Match 'Wrong password or the backup file is damaged\.'
            (@($b.afterWrong.nodes | ForEach-Object id) -join ',') | Should -Be (@($b.before.nodes | ForEach-Object id) -join ',')
            @($b.afterWrong.audit | Where-Object event -eq 'backup.imported') | Should -BeNullOrEmpty
        }

        It 'C-BAK-03 an old .rdpmbackup still imports: the folder holds the connections and credentials' -Tag 'C-BAK-03' {
            $b['old-error'] | Should -BeNullOrEmpty
            $b.files | Should -Contain 'old.rdpmbackup'
            $folder = @($b.old.nodes | Where-Object { -not $_.parent -and $_.kind -eq 'folder' -and $_.name -match '^Imported \d{4}-\d{2}-\d{2} \d{2}:\d{2}$' })
            $folder.Count | Should -Be 1
            ((Proj $b.old -Under $folder[0].name) -join "`n") | Should -Be ((Proj $b.admin) -join "`n")
            foreach ($c in (Creds $b.admin)) { Creds $b.old | Should -Contain $c }
        }
    }
}
