<#
.SYNOPSIS
    Repairs Microsoft Defender Antivirus: services, signature updates, corrupt definitions and a follow-up quick scan.
.DESCRIPTION
    Verifies the Defender and Security Center services, updates signatures from the configured source order, and
    when the update fails or definitions look corrupt, removes and reinstalls them with MpCmdRun (taken from the
    newest versioned platform folder). Runs a quick scan afterwards and reports conflicting antivirus products and
    recent Defender error events, with status before and after. Tamper protection blocks some actions; real-time
    protection is never disabled. Isolated networks must have an internal update source configured.
.NOTES
    RemoteHub: id=FIX-19; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$MaxSignatureAgeDays = 2,
    [bool]$QuickScan = $true,
    [int]$EventLookbackDays = 7
)

$ErrorActionPreference = 'Stop'
$actions = New-Object System.Collections.Generic.List[string]

function Get-Status {
    $s = Get-MpComputerStatus
    [pscustomobject]@{
        Mode = $s.AMRunningMode; AntivirusEnabled = $s.AntivirusEnabled; RealTimeProtection = $s.RealTimeProtectionEnabled
        TamperProtected = $s.IsTamperProtected; EngineVersion = $s.AMEngineVersion; SignatureVersion = $s.AntivirusSignatureVersion
        SignatureAgeDays = $s.AntivirusSignatureAge; LastQuickScan = $s.QuickScanEndTime
    }
}

function Get-MpCmdRun {
    # The platform folder name carries the version (for example 4.18.24090.11-0); the newest one is the live platform.
    $root = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform'
    $best = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $v = $null
        if ([version]::TryParse(($_.Name -split '-')[0], [ref]$v) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'MpCmdRun.exe'))) { [pscustomobject]@{ Version = $v; Path = Join-Path $_.FullName 'MpCmdRun.exe' } }
    } | Sort-Object Version -Descending | Select-Object -First 1
    if ($best) { return $best.Path }
    $fallback = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw 'MpCmdRun.exe not found.'
}

function Invoke-MpCmdRun([string]$Arguments) {
    $p = Start-Process -FilePath (Get-MpCmdRun) -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(1800000)) { throw "MpCmdRun $Arguments timed out." }
    return $p.ExitCode
}

try {
    if (-not (Get-Command -Name Get-MpComputerStatus -ErrorAction SilentlyContinue)) { Write-Output 'Data unavailable: the Defender module is not present (Defender feature removed?).'; exit 1 }
    $isClient = (Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -eq 1
    $before = Get-Status

    $services = @('WinDefend'); if ($isClient) { $services += 'SecurityHealthService', 'wscsvc' }
    foreach ($n in $services) {
        $svc = Get-Service -Name $n -ErrorAction SilentlyContinue
        if (-not $svc) { $actions.Add("Service ${n}: not present"); continue }
        if ($svc.Status -eq 'Running') { continue }
        if ($PSCmdlet.ShouldProcess($n, 'Start service')) {
            try { Start-Service -Name $n; $actions.Add("Service ${n}: started") }
            catch { $actions.Add("Service ${n}: could not start (protected service, tamper protection?): $($_.Exception.Message)") }
        }
    }

    $conflicts = 'Data unavailable (Security Center is client-only)'
    if ($isClient) {
        $conflicts = @(Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | Where-Object { $_.displayName -notmatch 'Defender' } | ForEach-Object { $_.displayName })
        if ($conflicts.Count -eq 0) { $conflicts = 'None' } else { $conflicts = $conflicts -join ', ' }
    }

    $needsUpdate = $before.SignatureAgeDays -gt $MaxSignatureAgeDays -or $before.SignatureVersion -eq '0.0.0.0' -or -not $before.SignatureVersion
    if ($needsUpdate -and $PSCmdlet.ShouldProcess('Defender signatures', 'Update from the configured source order')) {
        $updated = $false
        try { Update-MpSignature; $updated = $true; $actions.Add('Signatures updated') }
        catch { $actions.Add("Signature update failed: $($_.Exception.Message)") }
        if (-not $updated -or (Get-MpComputerStatus).AntivirusSignatureAge -gt $MaxSignatureAgeDays) {
            # Treated as corrupt definitions: remove them and pull a fresh set (definitions come back on update).
            $rc = Invoke-MpCmdRun '-RemoveDefinitions -All'
            $actions.Add("Definitions removed (MpCmdRun exit $rc)")
            $rc = Invoke-MpCmdRun '-SignatureUpdate'
            $actions.Add("Definitions reinstalled (MpCmdRun exit $rc)")
            if ($rc -ne 0) { throw 'Signature reinstall failed; check the update source (internal source on isolated networks).' }
        }
    }
    elseif (-not $needsUpdate) { $actions.Add('Signatures current, no update needed') }

    if ($QuickScan -and $PSCmdlet.ShouldProcess('Defender', 'Quick scan')) {
        Start-MpScan -ScanType QuickScan
        $actions.Add('Quick scan finished')
    }

    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Level = 1, 2; StartTime = (Get-Date).AddDays(-$EventLookbackDays) } -ErrorAction SilentlyContinue)
    $eventSummary = ($events | Group-Object Id | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '
    if (-not $eventSummary) { $eventSummary = 'None' }

    $after = Get-Status
    $before | Add-Member -NotePropertyName When -NotePropertyValue 'Before' -PassThru
    $after | Add-Member -NotePropertyName When -NotePropertyValue 'After' -PassThru
    [pscustomobject]@{ ConflictingAntivirus = $conflicts; ErrorEvents = $eventSummary; Actions = ($actions -join '; ') }

    $healthy = $after.AntivirusEnabled -and $after.SignatureAgeDays -le $MaxSignatureAgeDays
    if (-not $healthy) { exit 1 }
    exit 0
}
catch {
    if ($actions.Count) { Write-Output ($actions -join '; ') }
    Write-Output "FIX-19 failed: $($_.Exception.Message)"
    exit 1
}
