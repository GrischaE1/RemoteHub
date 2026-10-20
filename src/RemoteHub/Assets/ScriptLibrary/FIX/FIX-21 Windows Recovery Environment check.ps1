<#
.SYNOPSIS
    Reports whether the Windows Recovery Environment is enabled and has enough free space, and can enable it.
.DESCRIPTION
    Reports whether WinRE is enabled and where it lives, the recovery partition type, size and free space, and
    whether free space is enough for servicing updates that need it (check current vendor guidance for the
    figure). With -Enable it runs reagentc /enable when WinRE is disabled and a valid Winre.wim exists. Resizing
    partitions is risky and deliberately out of scope: the script only reports. State is read from ReAgent.xml
    and the partition table because reagentc output is localised.
.NOTES
    RemoteHub: id=FIX-21; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$MinFreeMB = 250,
    [switch]$Enable,
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'
$recoveryGpt = '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}'

function Get-WinReState {
    $xmlPath = Join-Path $env:windir 'System32\Recovery\ReAgent.xml'
    if (-not (Test-Path -LiteralPath $xmlPath)) { return $null }
    $x = ([xml](Get-Content -LiteralPath $xmlPath -Raw)).WindowsRE
    $offset = $null
    if ($x.WinreLocation -and $x.WinreLocation.offset) { $offset = [uint64]$x.WinreLocation.offset }
    [pscustomobject]@{ Enabled = ($x.InstallState.state -eq '1'); Path = "$($x.WinreLocation.path)"; Offset = $offset }
}

try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell (System32\Recovery is redirected for 32-bit hosts).' }
    $state = Get-WinReState
    if (-not $state) { if ($Sensor) { Write-Output $false } else { Write-Output 'Data unavailable: ReAgent.xml not found.' }; exit 1 }

    $localWim = Join-Path $env:windir 'System32\Recovery\Winre.wim'
    $enableResult = 'Not requested'
    if (-not $state.Enabled) {
        if (-not $Enable) { $enableResult = 'Disabled; run with -Enable to enable' }
        elseif (-not (Test-Path -LiteralPath $localWim)) { $enableResult = 'Cannot enable: no Winre.wim image found' }
        elseif ($PSCmdlet.ShouldProcess('Windows Recovery Environment', 'reagentc /enable')) {
            $p = Start-Process -FilePath (Join-Path $env:windir 'System32\reagentc.exe') -ArgumentList '/enable' -PassThru -WindowStyle Hidden
            if (-not $p.WaitForExit(300000)) { throw 'reagentc /enable timed out.' }
            $state = Get-WinReState
            if ($p.ExitCode -ne 0 -or -not $state.Enabled) { throw "reagentc /enable failed (exit $($p.ExitCode))." }
            $enableResult = 'Enabled'
        }
        else { $enableResult = 'WhatIf: would enable' }
    }
    elseif ($Enable) { $enableResult = 'Already enabled, no change' }

    # WinRE lives on the partition whose byte offset matches ReAgent.xml; fall back to the recovery partition type.
    $partitions = @(Get-Partition -ErrorAction SilentlyContinue)
    $part = $null
    if ($state.Enabled -and $null -ne $state.Offset) { $part = $partitions | Where-Object { $_.Offset -eq $state.Offset } | Select-Object -First 1 }
    if (-not $part) { $part = $partitions | Where-Object { $_.GptType -eq $recoveryGpt -or $_.MbrType -eq 39 } | Select-Object -First 1 }

    $sizeMB = 'Data unavailable'; $freeMB = 'Data unavailable'; $type = 'Data unavailable'; $location = 'Data unavailable'
    if ($part) {
        $sizeMB = [math]::Round($part.Size / 1MB)
        $location = "Disk $($part.DiskNumber) partition $($part.PartitionNumber)"
        if ($part.GptType) { if ($part.GptType -eq $recoveryGpt) { $type = 'GPT recovery' } else { $type = "GPT $($part.GptType)" } }
        else { $type = 'MBR 0x{0:X2}' -f [int]$part.MbrType }
        $vol = Get-Volume -Partition $part -ErrorAction SilentlyContinue
        if ($vol) { $freeMB = [math]::Round($vol.SizeRemaining / 1MB) }
        if ($part.IsBoot -or $part.DriveLetter -eq $env:SystemDrive.TrimEnd(':')) { $type = "$type (on the OS partition)" }
    }
    $enough = ($freeMB -is [double] -or $freeMB -is [int] -or $freeMB -is [long]) -and $freeMB -ge $MinFreeMB
    $ok = [bool]($state.Enabled -and $enough)

    if ($Sensor) { Write-Output $ok }
    else {
        [pscustomobject]@{
            EnabledAndHasSpace = $ok
            Enabled = $state.Enabled
            WinRePath = $state.Path
            Location = $location
            PartitionType = $type
            SizeMB = $sizeMB
            FreeMB = $freeMB
            RequiredFreeMB = $MinFreeMB
            EnableAction = $enableResult
            Note = 'Partition resizing is out of scope for automatic runs.'
        }
    }
    if ($ok) { exit 0 }
    exit 1
}
catch {
    if ($Sensor) { Write-Output $false } else { Write-Output "FIX-21 failed: $($_.Exception.Message)" }
    exit 1
}
