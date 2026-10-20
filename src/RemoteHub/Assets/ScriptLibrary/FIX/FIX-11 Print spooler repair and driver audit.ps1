<#
.SYNOPSIS
    Repairs the print spooler and audits printer drivers, ports and Point and Print restrictions.
.DESCRIPTION
    Removes stuck print jobs, optionally stops the spooler to clear its queue folder and starts it again, and
    audits printer drivers (version, type, signature, file date), ports (including WSD) and the Point and Print
    policy. Removes printers and unused drivers only when they are named in the approved lists. Clearing the spool
    deletes queued jobs for every user, so notify users first. Print hardening can block user driver installs and
    break per-user connections (FIX-12). The spooler is never disabled.
.NOTES
    RemoteHub: id=FIX-11; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [bool]$RemoveStuckJobs = $true,
    [int]$StuckJobMinutes = 60,
    [switch]$ClearSpoolFolder,
    [string[]]$RemovePrinter = @(),
    [string[]]$RemoveDriver = @()
)

$ErrorActionPreference = 'Stop'
$rows = New-Object System.Collections.Generic.List[object]
$failed = $false
function Add-Row([string]$Section, [string]$Item, [string]$Detail, [string]$Status) {
    $rows.Add([pscustomobject]@{ Section = $Section; Item = $Item; Detail = $Detail; Status = $Status })
}

try {
    if (-not (Get-Module -ListAvailable -Name PrintManagement)) { Write-Output 'Data unavailable: the PrintManagement module is not installed.'; exit 1 }
    $spooler = Get-Service -Name Spooler
    if ($spooler.StartType -eq 'Disabled') { Add-Row 'Spooler' 'Spooler' 'Disabled (left as is; it may be disabled on purpose)' 'Finding' }
    elseif ($spooler.Status -ne 'Running' -and $PSCmdlet.ShouldProcess('Spooler', 'Start service')) {
        Start-Service -Name Spooler; (Get-Service Spooler).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        Add-Row 'Spooler' 'Spooler' 'Was stopped' 'Started'
    }

    $printers = @()
    if ((Get-Service -Name Spooler).Status -eq 'Running') { $printers = @(Get-Printer) }
    if (@($printers | Where-Object { $_.Shared }).Count -gt 0) { Add-Row 'Spooler' 'Role' 'Shared printers found: this is a print server' 'Info' }

    if ($RemoveStuckJobs) {
        $cutoff = (Get-Date).AddMinutes(-$StuckJobMinutes)
        foreach ($p in $printers) {
            foreach ($j in @(Get-PrintJob -PrinterObject $p -ErrorAction SilentlyContinue | Where-Object { $_.SubmittedTime -lt $cutoff -or "$($_.JobStatus)" -match 'Error' })) {
                # The document name and owner are user data, so only the job id and age are shown.
                $item = '{0} job {1}' -f $p.Name, $j.Id
                if ($PSCmdlet.ShouldProcess($item, 'Remove stuck print job')) {
                    try { Remove-PrintJob -InputObject $j; Add-Row 'Jobs' $item ("Submitted {0:yyyy-MM-dd HH:mm}, {1}" -f $j.SubmittedTime, $j.JobStatus) 'Removed' }
                    catch { $failed = $true; Add-Row 'Jobs' $item $_.Exception.Message 'Failed' }
                }
            }
        }
    }

    if ($ClearSpoolFolder) {
        $spoolDir = Join-Path $env:windir 'System32\spool\PRINTERS'
        $files = @(Get-ChildItem -LiteralPath $spoolDir -File -Force -ErrorAction SilentlyContinue)
        if ($files.Count -eq 0) { Add-Row 'Spooler' 'Queue folder' 'Empty' 'No change' }
        elseif ($PSCmdlet.ShouldProcess($spoolDir, "Stop spooler, delete $($files.Count) queue file(s) for all users, start spooler")) {
            Stop-Service -Name Spooler -Force
            (Get-Service Spooler).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            try { foreach ($f in $files) { Remove-Item -LiteralPath $f.FullName -Force } }
            finally { Start-Service -Name Spooler; (Get-Service Spooler).WaitForStatus('Running', [TimeSpan]::FromSeconds(60)) }
            Add-Row 'Spooler' 'Queue folder' "$($files.Count) file(s)" 'Cleared, spooler restarted'
        }
    }

    foreach ($name in $RemovePrinter) {
        $p = $printers | Where-Object { $_.Name -eq $name }
        if (-not $p) { Add-Row 'Cleanup' $name 'Printer not present' 'No change'; continue }
        if ($PSCmdlet.ShouldProcess($name, 'Remove printer (approved list)')) {
            try { Remove-Printer -Name $name; Add-Row 'Cleanup' $name 'Printer' 'Removed' } catch { $failed = $true; Add-Row 'Cleanup' $name $_.Exception.Message 'Failed' }
        }
    }
    $inUse = @(Get-Printer -ErrorAction SilentlyContinue | Select-Object -ExpandProperty DriverName)
    foreach ($name in $RemoveDriver) {
        if ($inUse -contains $name) { Add-Row 'Cleanup' $name 'Driver still used by a printer' 'Skipped'; continue }
        if (-not (Get-PrinterDriver -Name $name -ErrorAction SilentlyContinue)) { Add-Row 'Cleanup' $name 'Driver not present' 'No change'; continue }
        if ($PSCmdlet.ShouldProcess($name, 'Remove printer driver (approved list)')) {
            try { Remove-PrinterDriver -Name $name; Add-Row 'Cleanup' $name 'Driver' 'Removed' } catch { $failed = $true; Add-Row 'Cleanup' $name $_.Exception.Message 'Failed' }
        }
    }

    foreach ($d in @(Get-PrinterDriver -ErrorAction SilentlyContinue)) {
        $v = [uint64]$d.DriverVersion
        $ver = '{0}.{1}.{2}.{3}' -f (($v -shr 48) -band 0xFFFF), (($v -shr 32) -band 0xFFFF), (($v -shr 16) -band 0xFFFF), ($v -band 0xFFFF)
        $sig = 'Data unavailable'; $date = 'Data unavailable'
        if ($d.ConfigFile -and (Test-Path -LiteralPath $d.ConfigFile)) {
            $sig = "$((Get-AuthenticodeSignature -FilePath $d.ConfigFile).Status)"
            $date = '{0:yyyy-MM-dd}' -f (Get-Item -LiteralPath $d.ConfigFile).LastWriteTime
        }
        $status = 'OK'; if ($sig -ne 'Valid') { $status = 'Review' }
        Add-Row 'Drivers' $d.Name ("Type {0}, {1}, version {2}, signature {3}, file date {4}" -f $d.MajorVersion, $d.PrinterEnvironment, $ver, $sig, $date) $status
    }
    foreach ($port in @(Get-PrinterPort -ErrorAction SilentlyContinue)) {
        $status = 'OK'; if ($port.Name -like 'WSD*') { $status = 'Review (WSD port)' }
        Add-Row 'Ports' $port.Name "$($port.Description)" $status
    }

    $pp = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint' -ErrorAction SilentlyContinue
    if (-not $pp) { Add-Row 'Point and Print' 'Policy' 'Not configured (Windows default: admins only install drivers)' 'OK' }
    else {
        foreach ($n in 'RestrictDriverInstallationToAdministrators', 'NoWarningNoElevationOnInstall', 'UpdatePromptSettings', 'TrustedServers', 'ServerList') {
            if ($pp.PSObject.Properties[$n]) {
                $val = "$($pp.$n)"; $status = 'OK'
                if (($n -eq 'RestrictDriverInstallationToAdministrators' -and $val -eq '0') -or ($n -eq 'NoWarningNoElevationOnInstall' -and $val -eq '1')) { $status = 'Insecure' }
                Add-Row 'Point and Print' $n $val $status
            }
        }
    }

    $rows
    if ($failed) { exit 1 }
    exit 0
}
catch {
    $rows
    Write-Output "FIX-11 failed: $($_.Exception.Message)"
    exit 1
}
