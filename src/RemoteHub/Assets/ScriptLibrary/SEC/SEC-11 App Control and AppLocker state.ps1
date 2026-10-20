<#
.SYNOPSIS
    Reports App Control for Business (WDAC) and AppLocker enforcement, policies and recent audit and block events.
.DESCRIPTION
    Reads code integrity enforcement from Win32_DeviceGuard, active App Control policies from CiTool
    (Windows 11 22H2 and later; JSON output, so nothing localized is parsed), the effective AppLocker policy
    and its rule collections, the Application Identity service, and audit and block event counts from the
    Code Integrity and AppLocker logs. Event IDs and tool availability vary by build; verify on your
    targets. User names in blocked-file paths are redacted.
.NOTES
    RemoteHub: id=SEC-11; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$EventDays = 7,
    [int]$SampleCount = 10
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'

function Protect-Path([string]$text) {
    if (-not $text) { return $text }
    return ($text -replace '(?i)(\\Users\\)[^\\]+', '$1[REDACTED]')
}

function Get-EventSummary([string]$log, [int[]]$auditIds, [int[]]$blockIds, [datetime]$since) {
    # An absent log means "unavailable"; an existing log with no matches means zero events.
    if (-not (Get-WinEvent -ListLog $log -ErrorAction SilentlyContinue)) { return $null }
    $ev = @(Get-WinEvent -FilterHashtable @{ LogName = $log; Id = ($auditIds + $blockIds); StartTime = $since } -ErrorAction SilentlyContinue)
    $blocks = @($ev | Where-Object { $blockIds -contains $_.Id })
    return [PSCustomObject]@{
        Log = $log
        Audit = @($ev | Where-Object { $auditIds -contains $_.Id }).Count
        Block = $blocks.Count
        RecentBlocks = @($blocks | Select-Object -First $SampleCount | ForEach-Object {
            $file = ''
            if ($_.Message -match '([A-Za-z]:\\[^\r\n"]+?\.(exe|dll|sys|ps1|msi|js|vbs|bat|cmd))') { $file = Protect-Path $Matches[1] }
            [PSCustomObject]@{ Time = $_.TimeCreated.ToString('s'); Id = $_.Id; File = $file }
        })
    }
}

try {
    $since = (Get-Date).AddDays(-$EventDays)
    $ciMap = @{ 0 = 'Off'; 1 = 'Audit'; 2 = 'Enforced' }
    $kmci = $unavailable; $umci = $unavailable
    try {
        $dg = Get-CimInstance -Namespace 'root\Microsoft\Windows\DeviceGuard' -ClassName Win32_DeviceGuard
        $kmci = $ciMap[[int]$dg.CodeIntegrityPolicyEnforcementStatus]
        $umci = $ciMap[[int]$dg.UsermodeCodeIntegrityPolicyEnforcementStatus]
    } catch { Write-Verbose "Win32_DeviceGuard unavailable: $($_.Exception.Message)" }

    $policies = $unavailable
    $ciTool = Join-Path $env:SystemRoot 'System32\CiTool.exe'
    if (Test-Path -LiteralPath $ciTool) {
        try {
            $json = (& $ciTool --list-policies --json) -join "`n"
            $parsed = $json | ConvertFrom-Json
            $policies = @($parsed.Policies | Where-Object { $_.IsEnforced } | ForEach-Object {
                [PSCustomObject]@{ Name = $_.FriendlyName; PolicyId = $_.PolicyID; System = $_.IsSystemPolicy; Version = $_.Version }
            })
        } catch { Write-Verbose "CiTool failed: $($_.Exception.Message)" }
    }

    $collections = $unavailable
    try {
        [xml]$xml = Get-AppLockerPolicy -Effective -Xml
        $collections = @($xml.AppLockerPolicy.RuleCollection | ForEach-Object {
            $count = @($_.ChildNodes | Where-Object { $_.NodeType -eq 'Element' }).Count
            [PSCustomObject]@{ Collection = $_.Type; EnforcementMode = $_.EnforcementMode; Rules = $count }
        })
    } catch { Write-Verbose "AppLocker policy unavailable: $($_.Exception.Message)" }

    $svc = Get-Service -Name AppIDSvc -ErrorAction SilentlyContinue
    $appId = 'NotInstalled'; if ($svc) { $appId = "$($svc.Status) ($($svc.StartType))" }

    $ciEvents = Get-EventSummary 'Microsoft-Windows-CodeIntegrity/Operational' @(3076) @(3077) $since
    $alExe = Get-EventSummary 'Microsoft-Windows-AppLocker/EXE and DLL' @(8003) @(8004) $since
    $alScript = Get-EventSummary 'Microsoft-Windows-AppLocker/MSI and Script' @(8006) @(8007) $since

    $enforced = @($collections | Where-Object { $_ -isnot [string] -and $_.EnforcementMode -eq 'Enabled' -and $_.Rules -gt 0 })
    $policyCount = $unavailable
    if ($policies -isnot [string]) { $policyCount = @($policies).Count }
    $blocks = 0
    foreach ($s in @($ciEvents, $alExe, $alScript)) { if ($s) { $blocks += $s.Block } }

    [PSCustomObject]@{
        AppControlKernelMode = $kmci; AppControlUserMode = $umci
        AppControlActivePolicies = $policyCount
        AppLockerEnforcedCollections = $enforced.Count
        ApplicationIdentityService = $appId
        BlocksInWindow = $blocks; WindowDays = $EventDays
    }
    if ($policies -isnot [string]) { $policies }
    if ($collections -isnot [string]) { $collections }
    foreach ($s in @($ciEvents, $alExe, $alScript)) {
        if ($s) { $s | Select-Object Log, Audit, Block; $s.RecentBlocks }
    }
    if ($enforced.Count -gt 0 -and (-not $svc -or $svc.Status -ne 'Running')) {
        Write-Warning 'AppLocker rules are enforced but the Application Identity service is not running, so they do not apply.'
        exit 1
    }
    exit 0
} catch {
    Write-Error "App Control state check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
