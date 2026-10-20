<#
.SYNOPSIS
    Reports computer-scope Group Policy and MDM state as summary JSON, with optional MDM sync and diagnostics package.
.DESCRIPTION
    Collects applied and filtered computer GPOs with reasons, the last Group Policy refresh, Group Policy
    operational errors, MDM enrollment state and last sync, failing MDM policy events, the MDM-wins-over-GP setting
    and areas configured by both Group Policy and MDM. Optionally triggers an MDM sync (-ForceMdmSync, the only
    change) and creates the MDM diagnostics package. As SYSTEM, gpresult only returns computer scope; user scope
    is FIX-16. Reports and packages contain user, device and tenant details: UPNs are redacted here, and the
    package must be protected and redacted before sharing.
.NOTES
    RemoteHub: id=FIX-15; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$Days = 7,
    [switch]$ForceMdmSync,
    [switch]$CollectMdmDiagnostics
)

$ErrorActionPreference = 'Stop'
$logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'

function Invoke-Native([string]$FilePath, [string]$Arguments, [int]$TimeoutSec = 300) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $FilePath, $Arguments
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $null = $p.StandardOutput.ReadToEndAsync(); $null = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; throw "$FilePath timed out." }
    return $p.ExitCode
}

function Get-EventSummary([string]$Log) {
    $ev = @(Get-WinEvent -FilterHashtable @{ LogName = $Log; Level = 1, 2; StartTime = (Get-Date).AddDays(-$Days) } -ErrorAction SilentlyContinue)
    if (-not (Get-WinEvent -ListLog $Log -ErrorAction SilentlyContinue)) { return 'Data unavailable' }
    [pscustomobject]@{ Errors = $ev.Count; ByEventId = @($ev | Group-Object Id | Sort-Object Count -Descending | ForEach-Object { "$($_.Name) x$($_.Count)" }); Latest = $(if ($ev.Count) { $ev[0].TimeCreated.ToString('yyyy-MM-dd HH:mm') } else { $null }) }
}

try {
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force -WhatIf:$false | Out-Null }

    # gpresult's XML element names and booleans are not localised, unlike its text report.
    $xmlPath = Join-Path $logDir ('FIX-15-gpresult-{0}.xml' -f [guid]::NewGuid().ToString('N'))
    $gpos = 'Data unavailable'
    try {
        $rc = Invoke-Native (Join-Path $env:windir 'System32\gpresult.exe') ('/scope computer /x "{0}" /f' -f $xmlPath)
        if ($rc -eq 0 -and (Test-Path -LiteralPath $xmlPath)) {
            $rsop = [xml](Get-Content -LiteralPath $xmlPath -Raw)
            $gpos = @(foreach ($g in @($rsop.Rsop.ComputerResults.GPO)) {
                $reason = 'Applied'
                if ($g.AccessDenied -eq 'true') { $reason = 'Denied (security filtering)' }
                elseif ($g.FilterAllowed -eq 'false') { $reason = 'Denied (WMI filter)' }
                elseif ($g.IsValid -eq 'false') { $reason = 'Not valid' }
                elseif ($g.Enabled -eq 'false') { $reason = 'Disabled' }
                [pscustomobject]@{ Name = $g.Name; Result = $reason }
            })
        }
    }
    finally { Remove-Item -LiteralPath $xmlPath -Force -ErrorAction SilentlyContinue -WhatIf:$false }

    $lastRefresh = 'Data unavailable'
    $st = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}' -ErrorAction SilentlyContinue
    if ($st -and $st.PSObject.Properties['EndTimeHi']) {
        $ft = ([int64][uint32]$st.EndTimeHi -shl 32) -bor [int64][uint32]$st.EndTimeLo
        $lastRefresh = [datetime]::FromFileTime($ft).ToString('yyyy-MM-dd HH:mm:ss')
    }

    $enrollments = @(Get-ChildItem -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Enrollments' -ErrorAction SilentlyContinue | ForEach-Object {
        $e = Get-ItemProperty -LiteralPath $_.PSPath
        if ($e.PSObject.Properties['ProviderID'] -and $e.ProviderID) {
            $sync = 'Data unavailable'
            $conn = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Provisioning\OMADM\Accounts\$($_.PSChildName)\Protected\ConnInfo" -ErrorAction SilentlyContinue
            if ($conn -and $conn.PSObject.Properties['ServerLastSuccessTime']) { $sync = $conn.ServerLastSuccessTime }
            [pscustomobject]@{ Id = $_.PSChildName; Provider = $e.ProviderID; State = $e.EnrollmentState; Type = $e.EnrollmentType; User = '[REDACTED]'; LastSuccessfulSync = $sync }
        }
    })

    $pm = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device'
    $mdmWins = 'Not set'
    $cp = Get-ItemProperty -LiteralPath "$pm\ControlPolicyConflict" -ErrorAction SilentlyContinue
    if ($cp -and $cp.PSObject.Properties['MDMWinsOverGP']) { $mdmWins = $cp.MDMWinsOverGP }
    $pairs = @(
        @{ Area = 'Update'; Gp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' },
        @{ Area = 'Defender'; Gp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' },
        @{ Area = 'BitLocker'; Gp = 'HKLM:\SOFTWARE\Policies\Microsoft\FVE' },
        @{ Area = 'DeviceLock'; Gp = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' },
        @{ Area = 'Firewall'; Gp = 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsFirewall' }
    )
    $conflicts = @(foreach ($pair in $pairs) {
        $m = Get-Item -LiteralPath "$pm\$($pair.Area)" -ErrorAction SilentlyContinue
        $g = Get-Item -LiteralPath $pair.Gp -ErrorAction SilentlyContinue
        if ($m -and $m.ValueCount -gt 0 -and $g -and ($g.ValueCount -gt 0 -or $g.SubKeyCount -gt 0)) { $pair.Area }
    })

    $syncResult = 'Not requested'
    if ($ForceMdmSync) {
        $tasks = @(Get-ScheduledTask -TaskPath '\Microsoft\Windows\EnterpriseMgmt\*' -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like 'Schedule #3*' })
        if ($tasks.Count -eq 0) { $syncResult = 'Data unavailable: no enrollment sync task' }
        elseif ($PSCmdlet.ShouldProcess('MDM enrollment', 'Trigger sync')) {
            foreach ($t in $tasks) { Start-ScheduledTask -InputObject $t }
            $syncResult = "Triggered ($($tasks.Count) task(s))"
        }
        else { $syncResult = 'WhatIf: not triggered' }
    }

    $package = 'Not requested'
    if ($CollectMdmDiagnostics) {
        $tool = Join-Path $env:windir 'System32\MdmDiagnosticsTool.exe'
        if (-not (Test-Path -LiteralPath $tool)) { $package = 'Data unavailable: MdmDiagnosticsTool not present' }
        else {
            $package = Join-Path $logDir ('FIX-15-mdmdiag-{0}.zip' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
            $rc = Invoke-Native $tool ('-area "DeviceEnrollment;DeviceProvisioning;Autopilot" -zip "{0}"' -f $package) 600
            if ($rc -ne 0 -or -not (Test-Path -LiteralPath $package)) { throw "MdmDiagnosticsTool failed with exit $rc." }
        }
    }

    [pscustomobject]@{
        Computer = $env:COMPUTERNAME
        Collected = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        GroupPolicy = [pscustomobject]@{ LastRefresh = $lastRefresh; ComputerGpos = $gpos; OperationalErrors = (Get-EventSummary 'Microsoft-Windows-GroupPolicy/Operational') }
        Mdm = [pscustomobject]@{
            Enrollments = $enrollments
            Errors = (Get-EventSummary 'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin')
            MdmWinsOverGP = $mdmWins
            AreasSetByBoth = $conflicts
            Sync = $syncResult
        }
        DiagnosticsPackage = $package
    } | ConvertTo-Json -Depth 5
    exit 0
}
catch {
    Write-Output "FIX-15 failed: $($_.Exception.Message)"
    exit 1
}
