<#
.SYNOPSIS
    Checks the effective local password, lockout, cached logon, interactive sign-in and UAC settings against a baseline.
.DESCRIPTION
    Reads account policy from a secedit export (temporary file, deleted after parsing) and the Winlogon
    and Policies\System registry values, and reports pass or fail per setting. On domain-joined devices
    domain accounts follow the domain password policy, so the account policy result describes local
    accounts only; the output says which policy it applies to. Report only - nothing is changed.
.NOTES
    RemoteHub: id=SEC-16; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$MinPasswordLength = 14,
    [int]$MaxPasswordAgeDays = 365,
    [int]$MinPasswordAgeDays = 1,
    [int]$PasswordHistory = 24,
    [int]$MaxLockoutThreshold = 10,
    [int]$MinLockoutDurationMinutes = 15,
    [int]$MaxCachedLogons = 4,
    [int]$MaxInactivitySeconds = 900,
    [bool]$RequireLegalNotice = $false,
    [bool]$RequireHideLastUser = $false
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'
$results = New-Object System.Collections.Generic.List[object]

function Add-Check($area, $setting, $actual, $expected, [scriptblock]$test) {
    $status = $unavailable
    if ($null -ne $actual -and [string]$actual -ne '') { if (& $test $actual) { $status = 'Pass' } else { $status = 'Fail' } }
    else { $actual = $unavailable }
    $results.Add([PSCustomObject]@{ Area = $area; Setting = $setting; Actual = [string]$actual; Expected = $expected; Status = $status })
}

function Get-Reg($path, $name) {
    try { return (Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop).$name } catch { return $null }
}

try {
    $acct = @{}
    $tmp = Join-Path $env:TEMP ("sec16-" + [guid]::NewGuid().ToString('N') + '.inf')
    try {
        $null = & secedit.exe /export /cfg $tmp /areas SECURITYPOLICY /quiet
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $tmp)) {
            $section = ''
            foreach ($line in (Get-Content -LiteralPath $tmp)) {
                if ($line -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
                if ($section -eq 'System Access' -and $line -match '^\s*(\w+)\s*=\s*(-?\d+)') { $acct[$Matches[1]] = [int]$Matches[2] }
            }
        } else { Write-Verbose 'secedit export failed; account policy is unavailable.' }
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

    # -1 in MaximumPasswordAge means "never expires".
    Add-Check 'Password' 'Minimum length' $acct['MinimumPasswordLength'] ">= $MinPasswordLength" { param($v) $v -ge $MinPasswordLength }
    Add-Check 'Password' 'Maximum age (days)' $acct['MaximumPasswordAge'] "1..$MaxPasswordAgeDays" { param($v) $v -ge 1 -and $v -le $MaxPasswordAgeDays }
    Add-Check 'Password' 'Minimum age (days)' $acct['MinimumPasswordAge'] ">= $MinPasswordAgeDays" { param($v) $v -ge $MinPasswordAgeDays }
    Add-Check 'Password' 'History' $acct['PasswordHistorySize'] ">= $PasswordHistory" { param($v) $v -ge $PasswordHistory }
    Add-Check 'Password' 'Complexity' $acct['PasswordComplexity'] '1' { param($v) $v -eq 1 }
    Add-Check 'Password' 'Reversible encryption' $acct['ClearTextPassword'] '0' { param($v) $v -eq 0 }
    Add-Check 'Lockout' 'Threshold' $acct['LockoutBadCount'] "1..$MaxLockoutThreshold" { param($v) $v -ge 1 -and $v -le $MaxLockoutThreshold }
    # Duration and reset only exist in the export once a lockout threshold is set.
    Add-Check 'Lockout' 'Duration (minutes)' $acct['LockoutDuration'] ">= $MinLockoutDurationMinutes or -1" { param($v) $v -eq -1 -or $v -ge $MinLockoutDurationMinutes }
    Add-Check 'Lockout' 'Reset counter after (minutes)' $acct['ResetLockoutCount'] ">= $MinLockoutDurationMinutes" { param($v) $v -ge $MinLockoutDurationMinutes }

    $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    $sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    $cached = Get-Reg $winlogon 'CachedLogonsCount'
    if ($null -eq $cached) { $cached = 10 }
    Add-Check 'Sign-in' 'Cached logons' ([int]$cached) "<= $MaxCachedLogons" { param($v) $v -le $MaxCachedLogons }
    $hide = Get-Reg $sys 'DontDisplayLastUserName'; if ($null -eq $hide) { $hide = 0 }
    Add-Check 'Sign-in' 'Hide last signed-in user' $hide $(if ($RequireHideLastUser) { '1' } else { 'any' }) { param($v) (-not $RequireHideLastUser) -or $v -eq 1 }
    $idle = Get-Reg $sys 'InactivityTimeoutSecs'
    Add-Check 'Sign-in' 'Machine inactivity limit (seconds)' $idle "1..$MaxInactivitySeconds" { param($v) $v -ge 1 -and $v -le $MaxInactivitySeconds }
    $caption = Get-Reg $sys 'LegalNoticeCaption'; $text = Get-Reg $sys 'LegalNoticeText'
    $notice = [bool]($caption -or $text)
    Add-Check 'Sign-in' 'Legal notice present' $notice $(if ($RequireLegalNotice) { 'True' } else { 'any' }) { param($v) (-not $RequireLegalNotice) -or $v }

    $lua = Get-Reg $sys 'EnableLUA'; if ($null -eq $lua) { $lua = 1 }
    Add-Check 'UAC' 'Enabled (EnableLUA)' $lua '1' { param($v) $v -eq 1 }
    $admin = Get-Reg $sys 'ConsentPromptBehaviorAdmin'; if ($null -eq $admin) { $admin = 5 }
    Add-Check 'UAC' 'Admin prompt behaviour' $admin '1, 2 or 5 (not 0)' { param($v) $v -ne 0 }
    $secure = Get-Reg $sys 'PromptOnSecureDesktop'; if ($null -eq $secure) { $secure = 1 }
    Add-Check 'UAC' 'Prompt on secure desktop' $secure '1' { param($v) $v -eq 1 }
    $filter = Get-Reg $sys 'FilterAdministratorToken'; if ($null -eq $filter) { $filter = 0 }
    Add-Check 'UAC' 'Admin approval mode for built-in Administrator' $filter '1' { param($v) $v -eq 1 }

    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $appliesTo = 'Local accounts (workgroup device)'
    if ($cs.PartOfDomain) { $appliesTo = 'Local accounts only; domain accounts follow the domain password policy' }
    if ($cs.DomainRole -ge 4) { $appliesTo = 'Domain controller: the account policy shown is the domain policy' }

    $results
    $failed = @($results | Where-Object { $_.Status -eq 'Fail' }).Count
    [PSCustomObject]@{ AppliesTo = $appliesTo; Checks = $results.Count; Failed = $failed
        Unavailable = @($results | Where-Object { $_.Status -eq $unavailable }).Count; Compliant = ($failed -eq 0) }
    if ($failed -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Local policy check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
