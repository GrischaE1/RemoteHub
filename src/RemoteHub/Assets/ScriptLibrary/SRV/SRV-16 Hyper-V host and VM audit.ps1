<#
.SYNOPSIS
    Audits a Hyper-V host and its VMs; optionally provisions VMs or removes old checkpoints with -Apply.
.DESCRIPTION
    Reports host settings, VM inventory (state, generation, configuration version, memory demand, integration
    services, heartbeat), checkpoints with age, replication health, virtual disk types and overcommit risk,
    virtual switches and time synchronization on domain controller guests. Provision mode creates VMs from a
    JSON definition file; RemoveCheckpoints removes checkpoints older than the threshold. Checkpoint merges
    are I/O heavy and restoring snapshots of directory servers breaks replication, so removal runs only with
    -Apply in a window. Dynamically expanding disks can overcommit host storage.
.NOTES
    RemoteHub: id=SRV-16; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit', 'Provision', 'RemoveCheckpoints')]
    [string]$Mode = 'Audit',
    [int]$CheckpointAgeDays = 7,
    # JSON array: [{ "Name": "", "Generation": 2, "MemoryStartupMB": 4096, "VhdPath": "", "VhdSizeGB": 80, "SwitchName": "" }]
    [string]$DefinitionPath = '',
    # VM names that run a domain controller; the host cannot tell this itself.
    [string[]]$DomainControllerVm = @(),
    # Sensor mode writes a single integer: checkpoints older than the threshold.
    [switch]$Sensor,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    # A sensor must write exactly one value, so the dry-run notice is left out there.
    if (-not $Sensor) { Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.' }
}

$exitCode = 0
try {
    if (-not (Get-Module -ListAvailable -Name Hyper-V)) { Write-Output 'Data unavailable: the Hyper-V module is not installed.'; exit 1 }
    Import-Module Hyper-V -Verbose:$false
    $vmHost = Get-VMHost
    $now = Get-Date
    $vms = @(Get-VM)
    $checkpoints = @(Get-VMSnapshot -VM $vms -ErrorAction SilentlyContinue)
    $old = @($checkpoints | Where-Object { $_.CreationTime -lt $now.AddDays(-$CheckpointAgeDays) })

    if ($Sensor) { Write-Output $old.Count; exit 0 }

    $overcommitGB = 0
    $rows = foreach ($vm in $vms) {
        $disks = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path } | ForEach-Object { try { Get-VHD -Path $_.Path } catch { $null } } | Where-Object { $_ })
        foreach ($d in $disks) { if ([string]$d.VhdType -eq 'Dynamic') { $overcommitGB += ($d.Size - $d.FileSize) / 1GB } }
        $ic = @(Get-VMIntegrationService -VM $vm)
        $ts = $ic | Where-Object { $_.Name -eq 'Time Synchronization' -or $_.Id -like '*2497F4DE-E9FA-4204-80E4-4B75C46419C0' }
        $hb = $ic | Where-Object { $_.Name -eq 'Heartbeat' -or $_.Id -like '*84EAAE65-2F2E-45F5-9BB5-0E857DC8EB47' }
        $issues = @()
        if ($DomainControllerVm -contains $vm.Name -and $ts -and $ts.Enabled) { $issues += 'DC guest with host time sync enabled (check guidance)' }
        if ($vm.State -eq 'Running' -and $hb -and [string]$hb.PrimaryStatusDescription -ne 'OK') { $issues += 'heartbeat not OK' }
        $vmOld = @($old | Where-Object { $_.VMId -eq $vm.VMId }).Count
        if ($vmOld -gt 0) { $issues += "$vmOld old checkpoint(s)" }
        $repl = 'None'
        if ($vm.ReplicationState -ne 'Disabled') {
            try { $repl = [string](Measure-VMReplication -VMName $vm.Name).Health } catch { $repl = 'Data unavailable' }
            if ($repl -ne 'Normal' -and $repl -ne 'None') { $issues += "replication $repl" }
        }
        [pscustomobject]@{ VM = $vm.Name; State = [string]$vm.State; Gen = $vm.Generation; Version = $vm.Version
            MemDemandMB = [int]($vm.MemoryDemand / 1MB); MemAssignedMB = [int]($vm.MemoryAssigned / 1MB)
            Heartbeat = $(if ($hb) { [string]$hb.PrimaryStatusDescription } else { 'Data unavailable' })
            IntegrationOff = (@($ic | Where-Object { -not $_.Enabled } | ForEach-Object { $_.Name }) -join ',')
            Checkpoints = @($checkpoints | Where-Object { $_.VMId -eq $vm.VMId }).Count
            Disks = (@($disks | ForEach-Object { "$($_.VhdType)/$([int]($_.FileSize / 1GB))of$([int]($_.Size / 1GB))GB" }) -join ' ')
            Replication = $repl; Issues = ($issues -join '; ') }
    }

    $freeGB = 'Data unavailable'
    try {
        $drive = (Split-Path -Qualifier $vmHost.VirtualHardDiskPath).TrimEnd(':')
        $freeGB = [int]((Get-Volume -DriveLetter $drive).SizeRemaining / 1GB)
    } catch { Write-Verbose 'Free space of the default VHD path is not readable.' }

    [pscustomobject]@{
        Host = $env:COMPUTERNAME; VirtualHardDiskPath = $vmHost.VirtualHardDiskPath; VirtualMachinePath = $vmHost.VirtualMachinePath
        LiveMigration = $vmHost.VirtualMachineMigrationEnabled; EnhancedSessionMode = $vmHost.EnableEnhancedSessionMode
        Switches = (@(Get-VMSwitch | ForEach-Object { "$($_.Name) ($($_.SwitchType))" }) -join ', ')
        VMs = $vms.Count; OldCheckpoints = $old.Count
        DynamicDiskGrowthGB = [int]$overcommitGB; FreeGBOnDefaultPath = $freeGB
    } | Format-List | Out-String -Width 200
    $rows | Format-Table -AutoSize -Wrap | Out-String -Width 300
    if ($freeGB -is [int] -and $overcommitGB -gt $freeGB) { Write-Warning 'Dynamic disks can grow beyond the free space on the default VHD volume.' }
    if (@($rows | Where-Object { $_.Issues }).Count -gt 0) { $exitCode = 1 }

    if ($Mode -eq 'RemoveCheckpoints') {
        foreach ($cp in $old) {
            if ($DomainControllerVm -contains $cp.VMName) { Write-Warning "$($cp.VMName) is a DC guest: never restore its old checkpoints; removal merges only." }
            $age = [int]($now - $cp.CreationTime).TotalDays
            if ($PSCmdlet.ShouldProcess("$($cp.VMName)\$($cp.Name)", "Remove checkpoint ($age days old, merge is I/O heavy)")) {
                Remove-VMSnapshot -VMSnapshot $cp -Confirm:$false
                Write-Output "Removed checkpoint $($cp.Name) of $($cp.VMName)."
            }
        }
        if ($old.Count -eq 0) { Write-Output 'No change: no checkpoints older than the threshold.' }
    }

    if ($Mode -eq 'Provision') {
        if (-not $DefinitionPath -or -not (Test-Path -LiteralPath $DefinitionPath)) { throw 'Provision mode needs -DefinitionPath with a JSON definition file.' }
        foreach ($d in @(Get-Content -LiteralPath $DefinitionPath -Raw -Encoding UTF8 | ConvertFrom-Json)) {
            if (@($vms | Where-Object { $_.Name -eq $d.Name }).Count -gt 0) { Write-Output "No change: VM $($d.Name) exists."; continue }
            $vhd = [string]$d.VhdPath
            if (-not $vhd) { $vhd = Join-Path $vmHost.VirtualHardDiskPath "$($d.Name).vhdx" }
            $p = @{ Name = $d.Name; Generation = [int]$d.Generation; MemoryStartupBytes = [int64]$d.MemoryStartupMB * 1MB }
            if ($d.SwitchName) { $p.SwitchName = [string]$d.SwitchName }
            if (Test-Path -LiteralPath $vhd) { $p.VHDPath = $vhd } else { $p.NewVHDPath = $vhd; $p.NewVHDSizeBytes = [int64]$d.VhdSizeGB * 1GB }
            if ($PSCmdlet.ShouldProcess($d.Name, 'New-VM')) {
                New-VM @p | Out-Null
                Write-Output "Created VM $($d.Name)."
            } else { New-VM @p -WhatIf | Out-Null }
        }
    }
} catch {
    Write-Error "Hyper-V audit failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
