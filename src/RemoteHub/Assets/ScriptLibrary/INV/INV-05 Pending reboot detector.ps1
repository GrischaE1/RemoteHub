<#
.SYNOPSIS
    Reports whether a restart is pending on this device and why.
.DESCRIPTION
    Checks the CBS RebootPending key, the Windows Update RebootRequired key, a pending computer
    rename, pending domain join markers and, if the client exists, the Configuration Manager
    reboot-pending method. PendingFileRenameOperations is reported separately and counts only with
    -CountFileRenames, because many installers set it. This script never restarts the machine.
    Exit code 3010 means a restart is pending, 0 means none.
.NOTES
    RemoteHub: id=INV-05; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$CountFileRenames
)

$ErrorActionPreference = 'Stop'

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

try {
    $reasons = New-Object System.Collections.Generic.List[string]

    if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
        $reasons.Add('Component Based Servicing')
    }
    if (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
        $reasons.Add('Windows Update')
    }

    $active  = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' 'ComputerName'
    $pendingName = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' 'ComputerName'
    if ($active -and $pendingName -and $active -ne $pendingName) { $reasons.Add('Computer rename') }

    $netlogon = 'HKLM:\SYSTEM\CurrentControlSet\Services\Netlogon'
    if ((Test-Path -LiteralPath "$netlogon\JoinDomain") -or $null -ne (Get-RegValue $netlogon 'AvoidSpnSet')) {
        $reasons.Add('Domain join')
    }

    $ccm = 'Not installed'
    try {
        $ns = Get-CimInstance -Namespace 'root' -ClassName '__Namespace' -Filter "Name='ccm'" -ErrorAction Stop
        if ($ns) {
            $r = Invoke-CimMethod -Namespace 'root\ccm\ClientSDK' -ClassName 'CCM_ClientUtilities' -MethodName 'DetermineIfRebootPending' -ErrorAction Stop
            $ccm = 'No'
            if ($r.RebootPending -or $r.IsHardRebootPending) { $ccm = 'Yes'; $reasons.Add('Configuration Manager') }
        }
    } catch {
        Write-Verbose "Configuration Manager query failed: $($_.Exception.Message)"
        $ccm = 'Data unavailable'
    }

    $renames = Get-RegValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' 'PendingFileRenameOperations'
    $renameCount = 0
    if ($renames) { $renameCount = @($renames | Where-Object { $_ }).Count / 2 }
    if ($renameCount -gt 0 -and $CountFileRenames) { $reasons.Add('Pending file rename operations') }

    $pending = $reasons.Count -gt 0
    [PSCustomObject]@{
        RebootPending                = $pending
        Reasons                      = if ($pending) { $reasons -join ', ' } else { 'None' }
        ConfigurationManager         = $ccm
        PendingFileRenameOperations  = [int]$renameCount
        FileRenamesCounted           = [bool]$CountFileRenames
    }
    Write-Verbose ("Reasons: " + ($reasons -join '; '))

    if ($pending) { exit 3010 }
    exit 0
} catch {
    Write-Error "Pending reboot check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
