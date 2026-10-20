<#
.SYNOPSIS
    Sets the Windows Time source for this machine's role and verifies source, offset and sync state.
.DESCRIPTION
    The PDC emulator syncs from external NTP peers, other domain members follow the domain hierarchy and
    workgroup machines use manual peers. Reads the configuration from the W32Time registry keys, changes it
    only when it differs, then verifies the source, the offset and the last sync event. Kerberos needs tight
    skew; a large offset is reported instead of forcing a jump, which can break authentication. On a
    virtualized domain controller the Hyper-V time provider is reported; change it per current guidance.
.NOTES
    RemoteHub: id=SRV-23; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Configure', 'Verify')]
    [string]$Mode = 'Configure',
    [string[]]$NtpPeer = @('time.windows.com'),
    [int]$MaxOffsetSeconds = 5,
    # Above this offset no resync is forced; correct it in steps in a window.
    [int]$MaxAutoCorrectSeconds = 300,
    # Sensor mode writes a single integer: the absolute offset in seconds.
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Get-Offset([string]$Target) {
    # The /dataonly sample line is numeric ("hh:mm:ss, +00.0012345s"); only the offset is taken from it.
    $out = & w32tm.exe /stripchart "/computer:$Target" /samples:1 /dataonly 2>$null
    foreach ($line in @($out)) { if ($line -match ',\s*([+-]\d+[\.,]\d+)s') { return [double]($Matches[1] -replace ',', '.') } }
    return $null
}

$exitCode = 0
try {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem
    $paramKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\Parameters'
    $p = Get-ItemProperty -LiteralPath $paramKey
    $role = 'Workgroup'
    if ($cs.DomainRole -eq 5) { $role = 'PDCEmulator' } elseif ($cs.PartOfDomain) { $role = 'DomainMember' }

    $peerList = (@($NtpPeer | ForEach-Object { if ($_ -match ',0x') { $_ } else { "$_,0x8" } }) -join ' ')
    $wantType = 'NTP'
    if ($role -eq 'DomainMember') { $wantType = 'NT5DS' }
    $inCompliance = ([string]$p.Type -eq $wantType) -and ($wantType -eq 'NT5DS' -or [string]$p.NtpServer -eq $peerList)

    $vmic = 'Not present'
    $vmicKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\VMICTimeProvider'
    if (Test-Path -LiteralPath $vmicKey) { $vmic = "Enabled=$((Get-ItemProperty -LiteralPath $vmicKey).Enabled)" }
    if ($cs.DomainRole -ge 4 -and $cs.Model -match 'Virtual' -and $vmic -eq 'Enabled=1') {
        Write-Verbose 'Virtual DC with the Hyper-V time provider enabled; verify against current vendor guidance.'
    }

    $changed = $false
    if ($Mode -eq 'Configure' -and -not $inCompliance) {
        $args1 = @('/config', '/update')
        if ($wantType -eq 'NT5DS') { $args1 += '/syncfromflags:domhier' }
        else {
            $args1 += @("/manualpeerlist:$peerList", '/syncfromflags:manual')
            if ($role -eq 'PDCEmulator') { $args1 += '/reliable:yes' }
        }
        if ($PSCmdlet.ShouldProcess("W32Time ($role)", "Set source: $wantType $(if ($wantType -eq 'NTP') { $peerList })")) {
            $null = & w32tm.exe @args1
            if ($LASTEXITCODE -ne 0) { throw "w32tm /config failed with exit code $LASTEXITCODE" }
            if ((Get-Service -Name W32Time).Status -ne 'Running') { Start-Service -Name W32Time }
            $changed = $true
        }
    }

    $p = Get-ItemProperty -LiteralPath $paramKey
    $source = [string]$p.NtpServer
    if ([string]$p.Type -eq 'NT5DS') {
        $source = 'Domain hierarchy'
        try { $source = "Domain hierarchy (DC $(([System.DirectoryServices.ActiveDirectory.Domain]::GetComputerDomain()).FindDomainController().Name))" } catch { Write-Verbose 'No DC located.' }
    }
    $target = ''
    if ([string]$p.Type -eq 'NT5DS') {
        try { $target = ([System.DirectoryServices.ActiveDirectory.Domain]::GetComputerDomain()).PdcRoleOwner.Name } catch { $target = '' }
    } else { $target = (($NtpPeer | Select-Object -First 1) -split ',')[0] }
    $offset = $null
    if ($target) { $offset = Get-Offset $target }

    if ($changed -and $null -ne $offset) {
        if ([Math]::Abs($offset) -le $MaxAutoCorrectSeconds) {
            $null = & w32tm.exe /resync /nowait
        } else {
            Write-Warning "Offset $offset s exceeds $MaxAutoCorrectSeconds s; no resync was forced. Correct it in steps in a window."
        }
    }

    $lastSync = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Time-Service'; Id = 35, 37 } -MaxEvents 1 -ErrorAction SilentlyContinue

    if ($Sensor) {
        if ($null -eq $offset) { Write-Output 'Data unavailable' } else { Write-Output ([int][Math]::Round([Math]::Abs($offset))) }
    } else {
        [pscustomobject]@{
            Role = $role; Type = [string]$p.Type; Source = $source; ExpectedType = $wantType
            OffsetSeconds = $(if ($null -ne $offset) { [Math]::Round($offset, 3) } else { 'Data unavailable' })
            OffsetMeasuredAgainst = $(if ($target) { $target } else { 'Data unavailable' })
            LastSyncEvent = $(if ($lastSync) { $lastSync.TimeCreated } else { 'Data unavailable' })
            HyperVTimeProvider = $vmic; Changed = $changed; ConfigurationCompliant = ($inCompliance -or $changed)
        } | Format-List | Out-String -Width 200
    }

    if ((-not $inCompliance -and -not $changed) -or $null -eq $offset -or [Math]::Abs($offset) -gt $MaxOffsetSeconds) { $exitCode = 1 }
} catch {
    Write-Error "Time service configuration failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
