<#
.SYNOPSIS
    Builds one hardware and OS baseline record for this device.
.DESCRIPTION
    Collects manufacturer, model, serial number, BIOS, firmware type, Secure Boot state, CPU, RAM,
    disks, OS edition, version, build and UBR, install date and domain or workgroup membership,
    and flags virtual machines. The UBR is read from the registry because Win32_OperatingSystem
    does not include it. Confirm-SecureBootUEFI throws on legacy BIOS, which is reported as
    "Not supported". The serial number is an asset identifier; use -HideSerial to leave it out.
.NOTES
    RemoteHub: id=INV-01; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$HideSerial,
    [ValidateSet('Json', 'Object')]
    [string]$OutputFormat = 'Json'
)

$ErrorActionPreference = 'Stop'

function Get-SecureBootState {
    try {
        if (Confirm-SecureBootUEFI) { return 'On' }
        return 'Off'
    } catch [System.PlatformNotSupportedException] {
        return 'Not supported'
    } catch {
        # Legacy BIOS raises a "not supported" error; anything else (access denied) is unknown.
        if ($_.Exception.Message -match 'not supported') { return 'Not supported' }
        Write-Verbose "Secure Boot query failed: $($_.Exception.Message)"
        return 'Data unavailable'
    }
}

try {
    $cs   = Get-CimInstance -ClassName Win32_ComputerSystem
    $os   = Get-CimInstance -ClassName Win32_OperatingSystem
    $bios = Get-CimInstance -ClassName Win32_BIOS
    $cpus = @(Get-CimInstance -ClassName Win32_Processor)
    $cv   = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

    $firmware = 'Data unavailable'
    try {
        $fw = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name 'PEFirmwareType' -ErrorAction Stop
        if ($fw.PEFirmwareType -eq 2) { $firmware = 'UEFI' } elseif ($fw.PEFirmwareType -eq 1) { $firmware = 'Legacy BIOS' }
    } catch {
        Write-Verbose 'PEFirmwareType not present.'
    }

    $disks = @()
    try {
        $disks = @(Get-PhysicalDisk | ForEach-Object {
            [PSCustomObject]@{
                FriendlyName = $_.FriendlyName
                MediaType    = [string]$_.MediaType
                BusType      = [string]$_.BusType
                SizeGB       = [math]::Round($_.Size / 1GB, 1)
            }
        })
    } catch {
        Write-Verbose "Get-PhysicalDisk failed: $($_.Exception.Message)"
        $disks = 'Data unavailable'
    }

    $vmPattern = 'Virtual|VMware|VirtualBox|KVM|QEMU|Xen|HVM domU|Parallels|Amazon EC2|Google Compute'
    $isVm = ("$($cs.Manufacturer) $($cs.Model)" -match $vmPattern)

    $ubr = 'Data unavailable'
    if ($null -ne $cv.UBR) { $ubr = [int]$cv.UBR }
    $displayVersion = 'Data unavailable'
    if ($cv.DisplayVersion) { $displayVersion = $cv.DisplayVersion } elseif ($cv.ReleaseId) { $displayVersion = $cv.ReleaseId }

    $serial = $bios.SerialNumber
    if ($HideSerial) { $serial = '[REDACTED]' }

    $membership = 'Workgroup'
    if ($cs.PartOfDomain) { $membership = 'Domain' }

    $record = [PSCustomObject]@{
        ComputerName     = $env:COMPUTERNAME
        Manufacturer     = $cs.Manufacturer
        Model            = $cs.Model
        SerialNumber     = $serial
        IsVirtualMachine = $isVm
        BiosVersion      = $bios.SMBIOSBIOSVersion
        BiosDate         = if ($bios.ReleaseDate) { $bios.ReleaseDate.ToString('yyyy-MM-dd') } else { 'Data unavailable' }
        FirmwareType     = $firmware
        SecureBoot       = Get-SecureBootState
        Cpu              = (($cpus | ForEach-Object { $_.Name.Trim() }) -join '; ')
        CpuCores         = ($cpus | Measure-Object -Property NumberOfCores -Sum).Sum
        CpuLogical       = ($cpus | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
        RamGB            = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
        Disks            = $disks
        OsEdition        = $os.Caption
        OsDisplayVersion = $displayVersion
        OsBuild          = $cv.CurrentBuild
        OsUbr            = $ubr
        OsArchitecture   = $os.OSArchitecture
        OsInstallDate    = $os.InstallDate.ToString('yyyy-MM-dd')
        Membership       = $membership
        DomainOrWorkgroup = $cs.Domain
    }

    if ($OutputFormat -eq 'Json') {
        $record | ConvertTo-Json -Depth 5
    } else {
        $record
    }
    exit 0
} catch {
    Write-Error "Baseline collection failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
