<#
.SYNOPSIS
    Applies selected hardening modules and writes the prior value of every setting to a snapshot first.
.DESCRIPTION
    Modules: Smb1, SmbSigning, LlmnrMdns, Netbios, WDigest, LsaProtection, LmCompatibility,
    AnonymousRestriction, AutoRun, PowerShell2, LegacyTls, RemoteRegistry, PrintSpooler. Run with -WhatIf
    for the plan. Settings applied by Group Policy are skipped unless -Force, because the next refresh
    would overwrite them. Removing SMBv1 breaks old NAS devices and scanners; disabling legacy TLS breaks
    older clients and drivers and needs a restart; the spooler is left alone where printers are shared.
    Pilot first. SEC-03 restores the snapshot. Exit 3010 means a restart is needed.
.NOTES
    RemoteHub: id=SEC-02; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Smb1','SmbSigning','LlmnrMdns','Netbios','WDigest','LsaProtection','LmCompatibility',
        'AnonymousRestriction','AutoRun','PowerShell2','LegacyTls','RemoteRegistry','PrintSpooler')]
    [string[]]$Module = @('WDigest','AutoRun','AnonymousRestriction'),
    [ValidateRange(3, 5)]
    [int]$LmCompatibilityLevel = 5,
    [switch]$Force,
    [string]$SnapshotFolder = "$env:ProgramData\RemoteHub\ScriptLogs"
)

$ErrorActionPreference = 'Stop'
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$snapshot = New-Object System.Collections.Generic.List[object]
$log = New-Object System.Collections.Generic.List[object]
$script:restart = $false
$script:failed = $false
$snapshotPath = Join-Path $SnapshotFolder ("SEC-02-snapshot-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))

function Add-Log($mod, $setting, $before, $after, $result) {
    $log.Add([PSCustomObject]@{ Module = $mod; Setting = $setting; Before = [string]$before; After = [string]$after; Result = $result })
}

# The snapshot is saved before each change, so a failure part way still leaves a complete rollback file.
function Save-Snapshot($entry) {
    $snapshot.Add($entry)
    if (-not (Test-Path -LiteralPath $SnapshotFolder)) { $null = New-Item -ItemType Directory -Path $SnapshotFolder -Force }
    $json = ConvertTo-Json -InputObject @($snapshot) -Depth 5
    [IO.File]::WriteAllText($snapshotPath, $json, (New-Object Text.UTF8Encoding $false))
    # The hash lets SEC-03 refuse an altered snapshot.
    (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash | Set-Content -LiteralPath "$snapshotPath.sha256" -Encoding ASCII
}

function Get-Reg($sub, $name) {
    $k = $hklm.OpenSubKey($sub)
    if (-not $k) { return @{ Existed = $false; Kind = $null; Value = $null } }
    try {
        if ($k.GetValueNames() -notcontains $name) { return @{ Existed = $false; Kind = $null; Value = $null } }
        return @{ Existed = $true; Kind = $k.GetValueKind($name).ToString(); Value = $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames') }
    } finally { $k.Close() }
}

$gpoValues = @{}
try {
    Get-CimInstance -Namespace 'root\rsop\computer' -ClassName RSOP_RegistryPolicySetting -Filter 'precedence=1' |
        ForEach-Object { $gpoValues[("{0}\{1}" -f $_.registryKey, $_.valueName).ToLowerInvariant()] = $true }
} catch { Write-Verbose "RSoP data not available; Group Policy detection limited: $($_.Exception.Message)" }

function Set-Reg($mod, $sub, $name, $value) {
    $cur = Get-Reg $sub $name
    $label = "HKLM\$sub\$name"
    if ($cur.Existed -and [string]$cur.Value -eq [string]$value) { Add-Log $mod $label $cur.Value $value 'No change'; return }
    if ($gpoValues.ContainsKey("$sub\$name".ToLowerInvariant()) -and -not $Force) { Add-Log $mod $label $cur.Value $value 'Skipped: managed by Group Policy'; return }
    if (-not $PSCmdlet.ShouldProcess($label, "Set to $value")) { Add-Log $mod $label $cur.Value $value 'Planned'; return }
    Save-Snapshot ([PSCustomObject]@{ Kind = 'Registry'; Path = $sub; Name = $name; Existed = $cur.Existed; ValueKind = $cur.Kind; Value = $cur.Value })
    $k = $hklm.CreateSubKey($sub)
    try { $k.SetValue($name, [int]$value, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $k.Close() }
    Add-Log $mod $label $cur.Value $value 'Changed'
}

function Disable-Feature($mod, $name) {
    $f = Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction SilentlyContinue
    if (-not $f) { Add-Log $mod "Feature $name" 'NotPresent' 'Disabled' 'No change'; return }
    if ([string]$f.State -ne 'Enabled') { Add-Log $mod "Feature $name" $f.State 'Disabled' 'No change'; return }
    if (-not $PSCmdlet.ShouldProcess("Feature $name", 'Disable')) { Add-Log $mod "Feature $name" 'Enabled' 'Disabled' 'Planned'; return }
    Save-Snapshot ([PSCustomObject]@{ Kind = 'Feature'; Name = $name; State = 'Enabled' })
    $r = Disable-WindowsOptionalFeature -Online -FeatureName $name -NoRestart
    if ($r.RestartNeeded) { $script:restart = $true }
    Add-Log $mod "Feature $name" 'Enabled' 'Disabled' 'Changed'
}

function Disable-Svc($mod, $name) {
    $s = Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'"
    if (-not $s) { Add-Log $mod "Service $name" 'NotInstalled' 'Disabled' 'No change'; return }
    if ($s.StartMode -eq 'Disabled' -and $s.State -ne 'Running') { Add-Log $mod "Service $name" 'Disabled' 'Disabled' 'No change'; return }
    if (-not $PSCmdlet.ShouldProcess("Service $name", 'Stop and disable')) { Add-Log $mod "Service $name" $s.StartMode 'Disabled' 'Planned'; return }
    $start = $s.StartMode; if ($start -eq 'Auto') { $start = 'Automatic' }
    Save-Snapshot ([PSCustomObject]@{ Kind = 'Service'; Name = $name; StartType = $start; Running = ($s.State -eq 'Running') })
    if ($s.State -eq 'Running') { Stop-Service -Name $name -Force }
    Set-Service -Name $name -StartupType Disabled
    Add-Log $mod "Service $name" $s.StartMode 'Disabled' 'Changed'
}

function Set-Smb($mod, $scope, $name, $value) {
    if ($scope -eq 'Server') { $cur = (Get-SmbServerConfiguration).$name } else { $cur = (Get-SmbClientConfiguration).$name }
    if ($cur -eq $value) { Add-Log $mod "SMB $scope $name" $cur $value 'No change'; return }
    if (-not $PSCmdlet.ShouldProcess("SMB $scope $name", "Set to $value")) { Add-Log $mod "SMB $scope $name" $cur $value 'Planned'; return }
    Save-Snapshot ([PSCustomObject]@{ Kind = 'Smb'; Scope = $scope; Name = $name; Value = $cur })
    $p = @{ $name = $value; Force = $true; Confirm = $false }
    if ($scope -eq 'Server') { Set-SmbServerConfiguration @p } else { Set-SmbClientConfiguration @p }
    Add-Log $mod "SMB $scope $name" $cur $value 'Changed'
}

$lsa = 'SYSTEM\CurrentControlSet\Control\Lsa'
$schannel = 'SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL'
foreach ($m in $Module) {
    $changedBefore = @($log | Where-Object { $_.Result -eq 'Changed' }).Count
    try {
        switch ($m) {
            'Smb1' { Set-Smb $m 'Server' 'EnableSMB1Protocol' $false; Disable-Feature $m 'SMB1Protocol' }
            'SmbSigning' { Set-Smb $m 'Server' 'RequireSecuritySignature' $true; Set-Smb $m 'Client' 'RequireSecuritySignature' $true }
            'LlmnrMdns' {
                Set-Reg $m 'SOFTWARE\Policies\Microsoft\Windows NT\DNSClient' 'EnableMulticast' 0
                Set-Reg $m 'SYSTEM\CurrentControlSet\Services\Dnscache\Parameters' 'EnableMDNS' 0
            }
            'Netbios' {
                foreach ($a in @(Get-CimInstance -ClassName Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=True')) {
                    $label = "NetBIOS adapter $($a.Index)"
                    if ($a.TcpipNetbiosOptions -eq 2) { Add-Log $m $label 2 2 'No change'; continue }
                    if (-not $PSCmdlet.ShouldProcess($label, 'Disable NetBIOS over TCP/IP')) { Add-Log $m $label $a.TcpipNetbiosOptions 2 'Planned'; continue }
                    Save-Snapshot ([PSCustomObject]@{ Kind = 'Netbios'; SettingID = $a.SettingID; Value = $a.TcpipNetbiosOptions })
                    $r = Invoke-CimMethod -InputObject $a -MethodName SetTcpipNetbios -Arguments @{ TcpipNetbiosOptions = [uint32]2 }
                    if ($r.ReturnValue -gt 1) { throw "SetTcpipNetbios returned $($r.ReturnValue) for adapter $($a.Index)" }
                    Add-Log $m $label $a.TcpipNetbiosOptions 2 'Changed'
                }
            }
            'WDigest' { Set-Reg $m 'SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest' 'UseLogonCredential' 0 }
            'LsaProtection' { Set-Reg $m $lsa 'RunAsPPL' 1 }
            'LmCompatibility' { Set-Reg $m $lsa 'LmCompatibilityLevel' $LmCompatibilityLevel }
            'AnonymousRestriction' {
                Set-Reg $m $lsa 'RestrictAnonymous' 1
                Set-Reg $m $lsa 'RestrictAnonymousSAM' 1
                Set-Reg $m $lsa 'EveryoneIncludesAnonymous' 0
            }
            'AutoRun' {
                Set-Reg $m 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoDriveTypeAutoRun' 255
                Set-Reg $m 'SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer' 'NoAutorun' 1
            }
            'PowerShell2' { Disable-Feature $m 'MicrosoftWindowsPowerShellV2'; Disable-Feature $m 'MicrosoftWindowsPowerShellV2Root' }
            'LegacyTls' {
                foreach ($p in 'SSL 2.0','SSL 3.0','TLS 1.0','TLS 1.1') {
                    foreach ($side in 'Server','Client') {
                        Set-Reg $m "$schannel\Protocols\$p\$side" 'Enabled' 0
                        Set-Reg $m "$schannel\Protocols\$p\$side" 'DisabledByDefault' 1
                    }
                }
                foreach ($c in 'RC4 128/128','RC4 56/128','RC4 40/128','DES 56/56','Triple DES 168','NULL') {
                    Set-Reg $m "$schannel\Ciphers\$c" 'Enabled' 0
                }
            }
            'RemoteRegistry' { Disable-Svc $m 'RemoteRegistry' }
            'PrintSpooler' {
                $shared = $null
                try { $shared = @(Get-CimInstance -ClassName Win32_Printer -Filter 'Shared=True').Count } catch { $shared = $null }
                if ($null -eq $shared) { Add-Log $m 'Service Spooler' 'Data unavailable' 'Disabled' 'Skipped: shared printers could not be checked' }
                elseif ($shared -gt 0) { Add-Log $m 'Service Spooler' 'Running' 'Disabled' 'Skipped: device shares printers' }
                else { Disable-Svc $m 'Spooler' }
            }
        }
        $changedNow = @($log | Where-Object { $_.Result -eq 'Changed' }).Count
        if ($changedNow -gt $changedBefore -and $m -in 'LsaProtection','LegacyTls','Smb1','Netbios') { $script:restart = $true }
    } catch {
        $script:failed = $true
        Add-Log $m 'Module' '' '' ("Failed: " + $_.Exception.Message)
    }
}

$log
if ($snapshot.Count -gt 0) { [PSCustomObject]@{ Snapshot = $snapshotPath; Entries = $snapshot.Count } }
if (@($log | Where-Object { $_.Result -eq 'Changed' -or $_.Result -eq 'Planned' }).Count -eq 0 -and -not $script:failed) {
    Write-Output 'No change: every selected setting is already in place or skipped.'
}
if ($script:failed) { Write-Error 'One or more hardening modules failed; see the Failed rows above.' -ErrorAction Continue; exit 1 }
if ($script:restart -and -not $WhatIfPreference) { exit 3010 }
exit 0
