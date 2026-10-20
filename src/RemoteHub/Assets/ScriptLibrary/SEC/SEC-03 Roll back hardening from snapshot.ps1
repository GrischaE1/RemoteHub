<#
.SYNOPSIS
    Restores the settings recorded in a SEC-02 (or SEC-12) snapshot and verifies the end state.
.DESCRIPTION
    Reads the snapshot JSON and restores each setting in reverse order; a registry value that did not
    exist before is deleted. The snapshot's SHA-256 file must match: if the snapshot is missing or
    altered, the script stops rather than guessing. Features removed by SEC-02 may need -Source or
    Windows Update access to come back, and a restart may be required (exit 3010). Without
    -SnapshotPath the newest SEC-02 snapshot in the log folder is used.
.NOTES
    RemoteHub: id=SEC-03; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SnapshotPath = '',
    [string]$SnapshotFolder = "$env:ProgramData\RemoteHub\ScriptLogs",
    [string]$Source = ''
)

$ErrorActionPreference = 'Stop'
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$restart = $false

function Get-Reg($sub, $name) {
    $k = $hklm.OpenSubKey($sub)
    if (-not $k) { return @{ Existed = $false; Value = $null } }
    try {
        if ($k.GetValueNames() -notcontains $name) { return @{ Existed = $false; Value = $null } }
        return @{ Existed = $true; Value = $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames') }
    } finally { $k.Close() }
}

function Get-Current($e) {
    switch ($e.Kind) {
        'Registry' { $r = Get-Reg $e.Path $e.Name; if ($r.Existed) { return (@($r.Value) -join ',') } else { return '<absent>' } }
        'Feature'  { $f = Get-WindowsOptionalFeature -Online -FeatureName $e.Name -ErrorAction SilentlyContinue; if ($f) { return [string]$f.State } else { return 'NotPresent' } }
        'Service'  { $s = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($e.Name)'"; $m = $s.StartMode; if ($m -eq 'Auto') { $m = 'Automatic' }; return $m }
        'Smb'      { if ($e.Scope -eq 'Server') { return [string](Get-SmbServerConfiguration).($e.Name) } else { return [string](Get-SmbClientConfiguration).($e.Name) } }
        'Netbios'  { return [string](Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter "SettingID='$($e.SettingID)'").TcpipNetbiosOptions }
        'EventLog' { return [string](Get-WinEvent -ListLog $e.Name).MaximumSizeInBytes }
    }
    throw "Unsupported snapshot entry kind '$($e.Kind)'."
}

function Get-Wanted($e) {
    switch ($e.Kind) {
        'Registry' { if ($e.Existed) { return (@($e.Value) -join ',') } else { return '<absent>' } }
        'Feature'  { return [string]$e.State }
        'Service'  { return [string]$e.StartType }
        'EventLog' { return [string]$e.MaximumSizeInBytes }
        default    { return [string]$e.Value }
    }
}

function Restore-Entry($e) {
    switch ($e.Kind) {
        'Registry' {
            if (-not $e.Existed) {
                $k = $hklm.OpenSubKey($e.Path, $true)
                if ($k) { try { $k.DeleteValue($e.Name, $false) } finally { $k.Close() } }
                return
            }
            $kind = [Microsoft.Win32.RegistryValueKind]$e.ValueKind
            $v = $e.Value
            switch ($kind) {
                'DWord' { $v = [int]$v } 'QWord' { $v = [long]$v }
                'Binary' { $v = [byte[]]@($v) } 'MultiString' { $v = [string[]]@($v) }
                default { $v = [string]$v }
            }
            $k = $hklm.CreateSubKey($e.Path)
            try { $k.SetValue($e.Name, $v, $kind) } finally { $k.Close() }
        }
        'Feature' {
            $p = @{ Online = $true; FeatureName = $e.Name; NoRestart = $true }
            if ($Source) { $p.Source = $Source; $p.LimitAccess = $true }
            $r = Enable-WindowsOptionalFeature @p
            if ($r.RestartNeeded) { $script:restart = $true }
        }
        'Service' {
            Set-Service -Name $e.Name -StartupType $e.StartType
            if ($e.Running) { Start-Service -Name $e.Name }
        }
        'Smb' {
            $p = @{ $e.Name = [bool]$e.Value; Force = $true; Confirm = $false }
            if ($e.Scope -eq 'Server') { Set-SmbServerConfiguration @p } else { Set-SmbClientConfiguration @p }
        }
        'Netbios' {
            $a = Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter "SettingID='$($e.SettingID)'"
            if (-not $a) { throw "Adapter $($e.SettingID) no longer exists." }
            $r = Invoke-CimMethod -InputObject $a -MethodName SetTcpipNetbios -Arguments @{ TcpipNetbiosOptions = [uint32]$e.Value }
            if ($r.ReturnValue -gt 1) { throw "SetTcpipNetbios returned $($r.ReturnValue)." }
            $script:restart = $true
        }
        'EventLog' {
            $l = Get-WinEvent -ListLog $e.Name
            $l.MaximumSizeInBytes = [long]$e.MaximumSizeInBytes
            $l.LogMode = $e.LogMode
            $l.SaveChanges()
        }
        default { throw "Unsupported snapshot entry kind '$($e.Kind)'." }
    }
}

try {
    if (-not $SnapshotPath) {
        $latest = Get-ChildItem -LiteralPath $SnapshotFolder -Filter 'SEC-02-snapshot-*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { throw "No SEC-02 snapshot found in $SnapshotFolder; nothing to restore." }
        $SnapshotPath = $latest.FullName
    }
    if (-not (Test-Path -LiteralPath $SnapshotPath)) { throw "Snapshot not found: $SnapshotPath" }
    if (-not (Test-Path -LiteralPath "$SnapshotPath.sha256")) { throw 'Snapshot hash file is missing; the snapshot cannot be trusted.' }
    $expected = (Get-Content -LiteralPath "$SnapshotPath.sha256" -Raw).Trim()
    if ((Get-FileHash -LiteralPath $SnapshotPath -Algorithm SHA256).Hash -ne $expected) { throw 'Snapshot was altered after it was written; stopping.' }

    $entries = @(Get-Content -LiteralPath $SnapshotPath -Raw | ConvertFrom-Json)
    [array]::Reverse($entries)
    $failed = $false
    $results = foreach ($e in $entries) {
        $label = "$($e.Kind) $($e.Path)$($e.Scope) $($e.Name)$($e.SettingID)".Trim()
        $wanted = Get-Wanted $e
        $result = ''
        try {
            $before = Get-Current $e
            if ($before -eq $wanted) { $result = 'No change' }
            elseif ($PSCmdlet.ShouldProcess($label, "Restore to $wanted")) {
                Restore-Entry $e
                $after = Get-Current $e
                if ($after -eq $wanted) { $result = 'Restored' } else { $result = "Mismatch: now $after"; $failed = $true }
            } else { $result = 'Planned' }
        } catch {
            $failed = $true
            $result = "Failed: $($_.Exception.Message)"
        }
        [PSCustomObject]@{ Setting = $label; Wanted = $wanted; Result = $result }
    }
    $results
    [PSCustomObject]@{ Snapshot = $SnapshotPath; Entries = $entries.Count; Failed = $failed }
    if ($failed) { exit 1 }
    if ($restart -and -not $WhatIfPreference) { exit 3010 }
    exit 0
} catch {
    Write-Error "Rollback stopped: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
