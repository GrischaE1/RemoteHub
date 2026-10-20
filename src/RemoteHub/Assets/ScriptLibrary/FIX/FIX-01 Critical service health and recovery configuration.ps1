<#
.SYNOPSIS
    Checks critical Windows services against a baseline and repairs start type, state and failure recovery actions.
.DESCRIPTION
    Compares start type and state of critical services (Windows Update, BITS, Cryptographic Services, Defender,
    Firewall, DNS Client, Netlogon, Windows Time, Event Log, WMI, WinRM and any extra role services) with a baseline,
    restores baseline start types, starts stopped services and sets restart-on-failure recovery actions.
    Stopped trigger-start services are normal and are not flagged. Excluded services are never touched, disabled
    services are only re-enabled with -IncludeDisabled, and protected services (Defender with tamper protection)
    are reported rather than forced. Prior state is saved to a rollback file before any change.
.NOTES
    RemoteHub: id=FIX-01; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$AdditionalService = @(),
    [string[]]$ExcludeService = @(),
    [bool]$ConfigureRecovery = $true,
    [switch]$IncludeDisabled,
    [int]$StartTimeoutSec = 60,
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
$rollbackFile = Join-Path $logDir ('FIX-01-rollback-{0}.json' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$rollback = New-Object System.Collections.Generic.List[object]

function Save-Rollback {
    # Written before each change so that a run which fails half way still leaves the prior state on disk.
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force -WhatIf:$false | Out-Null }
    ConvertTo-Json -InputObject $rollback.ToArray() -Depth 5 | Set-Content -LiteralPath $rollbackFile -Encoding UTF8 -WhatIf:$false
}

function Invoke-Native([string]$FilePath, [string]$Arguments, [int]$TimeoutSec = 120) {
    # Runs a native tool without letting its stderr turn into terminating errors under 'Stop' in non-console hosts.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath; $psi.Arguments = $Arguments; $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "$FilePath timed out after $TimeoutSec seconds." }
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out.Result; StdErr = $err.Result }
}

function Test-RestartRecovery([string]$Name) {
    # FailureActions is a SERVICE_FAILURE_ACTIONS blob: a 20-byte header, then (type, delay) pairs; type 1 is restart.
    $item = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -ErrorAction SilentlyContinue
    if ($null -eq $item -or $null -eq $item.PSObject.Properties['FailureActions']) { return $false }
    $blob = [byte[]]$item.FailureActions
    if ($blob.Length -lt 28) { return $false }
    return ([BitConverter]::ToInt32($blob, 20) -eq 1)
}

try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $isServer = $os.ProductType -ne 1
    $baseline = [ordered]@{
        'wuauserv' = 'Manual'; 'BITS' = 'Manual'; 'CryptSvc' = 'Auto'; 'WinDefend' = 'Auto'; 'mpssvc' = 'Auto'
        'Dnscache' = 'Auto'; 'EventLog' = 'Auto'; 'Winmgmt' = 'Auto'
    }
    if ($isServer) { $baseline['WinRM'] = 'Auto' } else { $baseline['WinRM'] = 'Manual' }
    if ($cs.PartOfDomain) { $baseline['Netlogon'] = 'Auto'; $baseline['W32Time'] = 'Auto' } else { $baseline['W32Time'] = 'Manual' }
    foreach ($extra in $AdditionalService) { if ($extra) { $baseline[$extra] = 'Auto' } }

    $results = foreach ($name in @($baseline.Keys)) {
        $expected = $baseline[$name]
        $findings = New-Object System.Collections.Generic.List[string]
        $actions = New-Object System.Collections.Generic.List[string]
        $healthy = $true
        $svc = Get-CimInstance -ClassName Win32_Service -Filter ("Name='{0}'" -f $name.Replace("'", ''))
        if (-not $svc) {
            [pscustomobject]@{ Service = $name; Expected = $expected; StartMode = 'Not installed'; State = '-'; Finding = 'Not installed'; Action = 'None'; Healthy = $true }
            continue
        }
        if ($ExcludeService -contains $name) {
            [pscustomobject]@{ Service = $name; Expected = $expected; StartMode = $svc.StartMode; State = $svc.State; Finding = 'Excluded'; Action = 'None'; Healthy = $true }
            continue
        }
        $triggerStart = Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$name\TriggerInfo"

        if ($svc.StartMode -ne $expected) {
            $findings.Add("Start type $($svc.StartMode), baseline $expected")
            if ($svc.StartMode -eq 'Disabled' -and -not $IncludeDisabled) {
                $healthy = $false
                $actions.Add('Left disabled (use -IncludeDisabled or add to the exclusion list)')
            }
            elseif ($PSCmdlet.ShouldProcess($name, "Set start type to $expected")) {
                try {
                    $rollback.Add([pscustomobject]@{ Service = $name; Setting = 'StartMode'; Prior = $svc.StartMode }); Save-Rollback
                    $type = 'Manual'; if ($expected -eq 'Auto') { $type = 'Automatic' }
                    Set-Service -Name $name -StartupType $type
                    $actions.Add("Start type set to $type")
                }
                catch { $healthy = $false; $actions.Add("Start type change failed (protected service?): $($_.Exception.Message)") }
            }
            else { $healthy = $false }
        }

        if ($expected -eq 'Auto' -and $svc.State -ne 'Running' -and $healthy) {
            if ($triggerStart) { $findings.Add('Stopped, trigger-start (normal)') }
            else {
                $findings.Add("State $($svc.State)")
                if ($PSCmdlet.ShouldProcess($name, 'Start service and its dependencies')) {
                    try {
                        Start-Service -Name $name
                        (Get-Service -Name $name).WaitForStatus('Running', [TimeSpan]::FromSeconds($StartTimeoutSec))
                        $actions.Add('Started')
                    }
                    catch { $healthy = $false; $actions.Add("Start failed: $($_.Exception.Message)") }
                }
                else { $healthy = $false }
            }
        }

        if ($ConfigureRecovery -and $expected -eq 'Auto' -and -not (Test-RestartRecovery $name)) {
            $findings.Add('No restart-on-failure recovery action')
            if ($PSCmdlet.ShouldProcess($name, 'Set failure recovery to restart after 60 seconds (twice)')) {
                $prior = (Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -ErrorAction SilentlyContinue).FailureActions
                $priorText = 'none'; if ($prior) { $priorText = [Convert]::ToBase64String([byte[]]$prior) }
                $rollback.Add([pscustomobject]@{ Service = $name; Setting = 'FailureActions'; Prior = $priorText }); Save-Rollback
                $sc = Invoke-Native -FilePath (Join-Path $env:windir 'System32\sc.exe') -Arguments "failure `"$name`" reset= 86400 actions= restart/60000/restart/60000//0"
                if ($sc.ExitCode -eq 0) { $actions.Add('Recovery actions set') }
                else { $actions.Add("Recovery change failed (sc.exe exit $($sc.ExitCode), protected service?)"); $healthy = $false }
            }
            else { $healthy = $false }
        }

        if ($findings.Count -eq 0) { $findings.Add('Matches baseline') }
        if ($actions.Count -eq 0) { $actions.Add('None') }
        [pscustomobject]@{
            Service = $name; Expected = $expected; StartMode = $svc.StartMode; State = $svc.State
            Finding = ($findings -join '; '); Action = ($actions -join '; '); Healthy = $healthy
        }
    }

    $unhealthy = @($results | Where-Object { -not $_.Healthy }).Count
    if ($Sensor) { Write-Output $unhealthy }
    else {
        $results
        if ($rollback.Count -gt 0) { Write-Output "Rollback information: $rollbackFile" }
    }
    if ($unhealthy -gt 0) { exit 1 }
    exit 0
}
catch {
    Write-Output "FIX-01 failed: $($_.Exception.Message)"
    exit 1
}
