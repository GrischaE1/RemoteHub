<#
.SYNOPSIS
    Reports whether this machine is ready for DSC 1.1 and whether the DSC v3 executable is present.
.DESCRIPTION
    Checks the PowerShell version, LCM state and modes, the WinRM service, installed DSC resource modules,
    the DSC v3 executable, the execution policy and a pending reboot. Local push with Start-DscConfiguration
    normally needs WinRM, so a stopped WinRM service makes the node not ready for push. LCM states such as
    Busy and PendingReboot are reported, not only errors. Use -Sensor for a single True/False value.
.NOTES
    RemoteHub: id=DSC-01; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$ResourceModules = @('PSDscResources', 'ComputerManagementDsc', 'NetworkingDsc', 'StorageDsc',
        'SecurityPolicyDsc', 'AuditPolicyDsc', 'ActiveDirectoryDsc', 'CertificateDsc', 'WebAdministrationDsc'),
    [string]$DscV3Path = '',
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'
$na = 'Data unavailable'

function Invoke-NativeCapture {
    param([string]$FilePath, [string]$Arguments)
    # Process is used directly because redirected stderr under ErrorActionPreference Stop terminates in 5.1.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "$FilePath did not finish within 30 seconds." }
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out; StdErr = $errTask.Result }
}

function Test-PendingReboot {
    $reasons = @()
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $reasons += 'CBS' }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reasons += 'WindowsUpdate' }
    $sm = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue
    if ($sm -and $sm.PSObject.Properties['PendingFileRenameOperations'] -and $sm.PendingFileRenameOperations) { $reasons += 'FileRename' }
    $active = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' -ErrorAction SilentlyContinue).ComputerName
    $pending = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' -ErrorAction SilentlyContinue).ComputerName
    if ($active -and $pending -and ($active -ne $pending)) { $reasons += 'ComputerRename' }
    $reasons
}

try {
    $psVersion = $PSVersionTable.PSVersion
    $psOk = ($PSVersionTable.PSEdition -ne 'Core') -and ($psVersion -ge [version]'5.1')

    # The LCM is read through local CIM, which works even when WinRM is stopped.
    $lcm = $null
    try {
        $r = Invoke-CimMethod -Namespace 'root/Microsoft/Windows/DesiredStateConfiguration' `
            -ClassName 'MSFT_DSCLocalConfigurationManager' -MethodName 'GetMetaConfiguration'
        $lcm = $r.MetaConfiguration
    } catch {
        Write-Verbose "LCM query failed: $($_.Exception.Message)"
    }

    $winrm = Get-Service -Name 'WinRM' -ErrorAction SilentlyContinue
    $winrmState = $na
    $winrmStart = $na
    if ($winrm) { $winrmState = [string]$winrm.Status; $winrmStart = [string]$winrm.StartType }

    $modules = foreach ($name in $ResourceModules) {
        $found = @(Get-Module -ListAvailable -Name $name | Sort-Object Version -Descending)
        $versions = 'Not installed'
        if ($found.Count -gt 0) { $versions = ($found | ForEach-Object { $_.Version.ToString() }) -join ', ' }
        [pscustomobject]@{ Name = $name; Versions = $versions }
    }

    $dscV3 = [ordered]@{ Present = $false; Path = $na; Version = $na }
    $exe = $DscV3Path
    if (-not $exe) {
        $cmd = Get-Command -Name 'dsc.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { $exe = $cmd.Source }
    }
    if ($exe -and (Test-Path -LiteralPath $exe)) {
        $dscV3.Present = $true
        $dscV3.Path = $exe
        try {
            $v = Invoke-NativeCapture -FilePath $exe -Arguments '--version'
            if ($v.StdOut -match '(\d+\.\d+\.\d+[\w\.\-]*)') { $dscV3.Version = $Matches[1] }
        } catch { Write-Verbose "dsc --version failed: $($_.Exception.Message)" }
    }

    $reboot = @(Test-PendingReboot)

    $lcmState = $na; $cfgMode = $na; $refreshMode = $na; $rebootNode = $na
    if ($lcm) {
        $lcmState = [string]$lcm.LCMState
        $cfgMode = [string]$lcm.ConfigurationMode
        $refreshMode = [string]$lcm.RefreshMode
        $rebootNode = [string]$lcm.RebootNodeIfNeeded
    }

    $blockers = @()
    if (-not $psOk) { $blockers += 'Windows PowerShell 5.1 is needed for DSC 1.1' }
    if (-not $lcm) { $blockers += 'LCM state unavailable' }
    elseif ($lcmState -eq 'Busy') { $blockers += 'LCM is busy applying a configuration' }
    elseif ($lcmState -eq 'PendingReboot') { $blockers += 'LCM waits for a reboot' }
    if ($winrmState -ne 'Running') { $blockers += 'WinRM is not running, so local push is not possible (use DSC v3 or pull)' }
    if ($reboot.Count -gt 0) { $blockers += 'A reboot is pending' }
    $ready = ($blockers.Count -eq 0)

    if ($Sensor) {
        Write-Output $ready
    } else {
        $report = [ordered]@{
            Id                 = 'DSC-01'
            Computer           = $env:COMPUTERNAME
            Ready              = $ready
            Blockers           = $blockers
            PowerShellVersion  = $psVersion.ToString()
            LcmState           = $lcmState
            ConfigurationMode  = $cfgMode
            RefreshMode        = $refreshMode
            RebootNodeIfNeeded = $rebootNode
            WinRMStatus        = $winrmState
            WinRMStartType     = $winrmStart
            ExecutionPolicy    = [string](Get-ExecutionPolicy)
            PendingReboot      = ($reboot -join ', ')
            ResourceModules    = $modules
            DscV3              = [pscustomobject]$dscV3
        }
        Write-Output ([pscustomobject]$report | ConvertTo-Json -Depth 5)
    }

    if ($ready) { exit 0 } else { exit 1 }
} catch {
    Write-Error "DSC-01 failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
