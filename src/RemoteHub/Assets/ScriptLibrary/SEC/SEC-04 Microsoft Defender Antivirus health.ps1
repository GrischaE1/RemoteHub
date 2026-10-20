<#
.SYNOPSIS
    Reports Microsoft Defender Antivirus health, versions, signature and scan age, and recent detections.
.DESCRIPTION
    Reports the service, real-time, behaviour, IOAV and network inspection protection, tamper protection,
    running mode, engine and platform versions, signature age and last quick and full scan age, plus
    detections in the last -DetectionDays days. On clients it lists other registered antivirus products
    (SecurityCenter2 does not exist on Server). Passive mode is expected when another antivirus or EDR is
    in charge. User names in detection paths are redacted. On Server 2016 Defender is an optional feature.
.NOTES
    RemoteHub: id=SEC-04; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [int]$MaxSignatureAgeDays = 3,
    [int]$MaxQuickScanAgeDays = 14,
    [int]$DetectionDays = 30,
    [bool]$AcceptPassiveMode = $true,
    [ValidateSet('None','SignatureAgeDays','Healthy')]
    [string]$Sensor = 'None'
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'

function Protect-Path([string]$text) {
    if (-not $text) { return $text }
    # Profile folder names are user names, so they are removed from paths.
    return ($text -replace '(?i)(\\Users\\)[^\\]+', '$1[REDACTED]')
}

function Get-AgeDays($date) {
    if (-not $date -or $date.Year -lt 2000) { return $null }
    return [math]::Round(((Get-Date) - $date).TotalDays, 1)
}

try {
    if (-not (Get-Command -Name Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
        if ($Sensor -ne 'None') { $unavailable } else { [PSCustomObject]@{ Status = $unavailable; Reason = 'Defender module not present' } }
        exit 1
    }
    $svc = Get-Service -Name WinDefend -ErrorAction SilentlyContinue
    $st = $null
    try { $st = Get-MpComputerStatus } catch { Write-Verbose "Get-MpComputerStatus failed: $($_.Exception.Message)" }
    if (-not $st) {
        $state = 'NotInstalled'; if ($svc) { $state = [string]$svc.Status }
        if ($Sensor -ne 'None') { $unavailable } else { [PSCustomObject]@{ Status = $unavailable; Service = $state } }
        exit 1
    }

    $mode = $unavailable
    if ($st.PSObject.Properties['AMRunningMode']) { $mode = [string]$st.AMRunningMode }
    $sigAge = Get-AgeDays $st.AntivirusSignatureLastUpdated
    $quickAge = Get-AgeDays $st.QuickScanEndTime
    $fullAge = Get-AgeDays $st.FullScanEndTime

    $detections = @()
    try {
        $threats = @{}
        foreach ($t in @(Get-MpThreat -ErrorAction SilentlyContinue)) { $threats[[string]$t.ThreatID] = $t.ThreatName }
        $since = (Get-Date).AddDays(-$DetectionDays)
        $detections = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -ge $since } |
            Sort-Object InitialDetectionTime -Descending | Select-Object -First 25 | ForEach-Object {
                [PSCustomObject]@{
                    Time = $_.InitialDetectionTime.ToString('s')
                    Threat = $threats[[string]$_.ThreatID]
                    Status = [string]$_.ThreatStatusID
                    ActionSuccess = $_.ActionSuccess
                    Resources = @($_.Resources | ForEach-Object { Protect-Path $_ })
                }
            })
    } catch { Write-Verbose "Detections unavailable: $($_.Exception.Message)" }

    $otherAv = $unavailable
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    if ($os.ProductType -eq 1) {
        try {
            $otherAv = @(Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName AntiVirusProduct |
                Where-Object { $_.displayName -notmatch 'Defender' } | ForEach-Object { $_.displayName })
        } catch { $otherAv = $unavailable }
    } else { $otherAv = 'Not applicable on Server' }

    $issues = New-Object System.Collections.Generic.List[string]
    if (-not $svc -or $svc.Status -ne 'Running') { $issues.Add('Defender service not running') }
    $passive = ($mode -match 'Passive|EDR')
    if ($passive -and -not $AcceptPassiveMode) { $issues.Add("Running mode is $mode") }
    if (-not $passive) {
        if (-not $st.RealTimeProtectionEnabled) { $issues.Add('Real-time protection off') }
        if (-not $st.BehaviorMonitorEnabled) { $issues.Add('Behaviour monitoring off') }
        if (-not $st.IoavProtectionEnabled) { $issues.Add('IOAV protection off') }
    }
    if ($null -eq $sigAge -or $sigAge -gt $MaxSignatureAgeDays) { $issues.Add("Signatures older than $MaxSignatureAgeDays days") }
    if ($null -eq $quickAge -or $quickAge -gt $MaxQuickScanAgeDays) { $issues.Add("No quick scan in $MaxQuickScanAgeDays days") }
    $healthy = ($issues.Count -eq 0)

    if ($Sensor -eq 'SignatureAgeDays') { if ($null -eq $sigAge) { $unavailable } else { [int][math]::Floor($sigAge) } }
    elseif ($Sensor -eq 'Healthy') { $healthy }
    else {
        [PSCustomObject]@{
            Healthy = $healthy; Issues = @($issues)
            Service = $(if ($svc) { [string]$svc.Status } else { 'NotInstalled' })
            RunningMode = $mode
            RealTimeProtection = $st.RealTimeProtectionEnabled; BehaviorMonitoring = $st.BehaviorMonitorEnabled
            IoavProtection = $st.IoavProtectionEnabled; NetworkInspection = $st.NISEnabled
            TamperProtection = $(if ($st.PSObject.Properties['IsTamperProtected']) { $st.IsTamperProtected } else { $unavailable })
            EngineVersion = $st.AMEngineVersion; PlatformVersion = $st.AMProductVersion
            SignatureVersion = $st.AntivirusSignatureVersion; SignatureAgeDays = $sigAge
            QuickScanAgeDays = $quickAge; FullScanAgeDays = $fullAge
            Detections = $detections; OtherAntivirus = $otherAv
        } | ConvertTo-Json -Depth 5
    }
    if ($healthy) { exit 0 }
    exit 1
} catch {
    Write-Error "Defender health check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
