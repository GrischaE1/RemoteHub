<#
.SYNOPSIS
    Applies the first-boot server baseline (name, network, time zone, RDP, firewall, dumps, logs, power).
.DESCRIPTION
    Compares each baseline item with the current state and changes only what differs: computer name, static
    IP and DNS, time zone, Remote Desktop with NLA, firewall profiles, WSUS source, crash dump type, page file,
    event log sizes, power plan and unneeded services. Optional offline domain join from a djoin blob.
    Changing the IP over a remote session drops it; run that from the console. The djoin blob carries the
    computer account password: it is never printed. Defender settings are left to SEC-05.
.NOTES
    RemoteHub: id=SRV-06; context=System; os=Server; risk=R2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$NewComputerName = '',
    [string]$InterfaceAlias = '',
    [string]$IPAddress = '',
    [int]$PrefixLength = 24,
    [string]$DefaultGateway = '',
    [string[]]$DnsServer = @(),
    [string]$TimeZoneId = '',
    [bool]$EnableRemoteDesktop = $true,
    [bool]$EnableFirewallProfiles = $true,
    [string]$WsusServer = '',
    [ValidateSet('Unchanged', 'None', 'Complete', 'Kernel', 'Small', 'Automatic', 'Active')]
    [string]$CrashDumpType = 'Automatic',
    [bool]$AutomaticPageFile = $true,
    [int]$SystemLogSizeMB = 64,
    [int]$SecurityLogSizeMB = 256,
    [ValidateSet('Unchanged', 'HighPerformance', 'Balanced')]
    [string]$PowerPlan = 'HighPerformance',
    [string[]]$DisableService = @(),
    # Offline domain join provisioning file from djoin /provision (pre-created computer object).
    [string]$DjoinBlobPath = '',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if (-not $Apply) {
    $WhatIfPreference = $true
    Write-Output 'Dry run: nothing changed. Run with -Apply to make the changes.'
}

$log = @()
$restart = $false
$failed = $false

function Invoke-Step([string]$Item, [string]$Current, [string]$Desired, [scriptblock]$Change) {
    if ($Current -eq $Desired) {
        $script:log += [pscustomobject]@{ Item = $Item; Before = $Current; After = $Desired; Result = 'No change' }
        return
    }
    if ($PSCmdlet.ShouldProcess($Item, "Change '$Current' to '$Desired'")) {
        try {
            & $Change
            $script:log += [pscustomobject]@{ Item = $Item; Before = $Current; After = $Desired; Result = 'Changed' }
        } catch {
            $script:failed = $true
            $script:log += [pscustomobject]@{ Item = $Item; Before = $Current; After = $Desired; Result = "Failed: $($_.Exception.Message)" }
        }
    } else {
        $script:log += [pscustomobject]@{ Item = $Item; Before = $Current; After = $Desired; Result = 'Planned' }
    }
}

function Get-Snapshot {
    $tsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
    [ordered]@{
        ComputerName  = $env:COMPUTERNAME
        TimeZone      = (Get-TimeZone).Id
        RdpDenied     = (Get-ItemProperty -LiteralPath $tsKey).fDenyTSConnections
        RdpNla        = (Get-ItemProperty -LiteralPath "$tsKey\WinStations\RDP-Tcp").UserAuthentication
        Firewall      = @(Get-NetFirewallProfile | ForEach-Object { "$($_.Name)=$($_.Enabled)" }) -join ';'
        CrashDump     = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl').CrashDumpEnabled
        AutoPageFile  = (Get-CimInstance -ClassName Win32_ComputerSystem).AutomaticManagedPagefile
        IPv4          = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '127.0.0.1' } | ForEach-Object { "$($_.InterfaceAlias)=$($_.IPAddress)/$($_.PrefixLength)" }) -join ';'
        PartOfDomain  = (Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain
    }
}

try {
    if ((Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -eq 1) { throw 'This baseline is for Windows Server.' }
    $logDir = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    if ($Apply) {
        if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -WhatIf:$false | Out-Null }
        Get-Snapshot | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logDir "SRV-06-$stamp-before.json") -Encoding UTF8 -WhatIf:$false
    }

    if ($NewComputerName) {
        Invoke-Step 'Computer name' $env:COMPUTERNAME $NewComputerName.ToUpperInvariant() {
            Rename-Computer -NewName $NewComputerName -Force -WhatIf:$false; $script:restart = $true }
    }

    if ($IPAddress) {
        if (-not $InterfaceAlias) { throw '-InterfaceAlias is required with -IPAddress.' }
        $cur = @(Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength)" }) -join ','
        Write-Warning 'Changing the IP address drops remote sessions to this server; run it from the console.'
        Invoke-Step "IPv4 on $InterfaceAlias" $cur "$IPAddress/$PrefixLength" {
            Set-NetIPInterface -InterfaceAlias $InterfaceAlias -Dhcp Disabled -WhatIf:$false
            Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -WhatIf:$false
            $p = @{ InterfaceAlias = $InterfaceAlias; IPAddress = $IPAddress; PrefixLength = $PrefixLength }
            if ($DefaultGateway) { $p.DefaultGateway = $DefaultGateway }
            New-NetIPAddress @p -WhatIf:$false | Out-Null }
    }
    if ($DnsServer.Count -gt 0) {
        if (-not $InterfaceAlias) { throw '-InterfaceAlias is required with -DnsServer.' }
        $cur = (Get-DnsClientServerAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4).ServerAddresses -join ','
        Invoke-Step "DNS servers on $InterfaceAlias" $cur ($DnsServer -join ',') {
            Set-DnsClientServerAddress -InterfaceAlias $InterfaceAlias -ServerAddresses $DnsServer -WhatIf:$false }
    }

    if ($TimeZoneId) {
        Invoke-Step 'Time zone' (Get-TimeZone).Id $TimeZoneId { Set-TimeZone -Id $TimeZoneId -WhatIf:$false }
    }

    if ($EnableRemoteDesktop) {
        $tsKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
        Invoke-Step 'Remote Desktop allowed' ([string](Get-ItemProperty -LiteralPath $tsKey).fDenyTSConnections) '0' {
            Set-ItemProperty -LiteralPath $tsKey -Name fDenyTSConnections -Value 0 -WhatIf:$false
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -WhatIf:$false }
        Invoke-Step 'Remote Desktop NLA' ([string](Get-ItemProperty -LiteralPath "$tsKey\WinStations\RDP-Tcp").UserAuthentication) '1' {
            Set-ItemProperty -LiteralPath "$tsKey\WinStations\RDP-Tcp" -Name UserAuthentication -Value 1 -WhatIf:$false }
    }

    if ($EnableFirewallProfiles) {
        foreach ($fp in @(Get-NetFirewallProfile)) {
            Invoke-Step "Firewall profile $($fp.Name)" ([string]$fp.Enabled) 'True' {
                Set-NetFirewallProfile -Name $fp.Name -Enabled True -DefaultInboundAction Block -DefaultOutboundAction Allow -WhatIf:$false }
        }
    }

    if ($WsusServer) {
        $wuKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
        $cur = ''
        if (Test-Path -LiteralPath $wuKey) { $cur = [string](Get-ItemProperty -LiteralPath $wuKey).WUServer }
        Invoke-Step 'Windows Update source' $cur $WsusServer {
            if (-not (Test-Path -LiteralPath "$wuKey\AU")) { New-Item -Path "$wuKey\AU" -Force -WhatIf:$false | Out-Null }
            Set-ItemProperty -LiteralPath $wuKey -Name WUServer -Value $WsusServer -WhatIf:$false
            Set-ItemProperty -LiteralPath $wuKey -Name WUStatusServer -Value $WsusServer -WhatIf:$false
            Set-ItemProperty -LiteralPath "$wuKey\AU" -Name UseWUServer -Value 1 -Type DWord -WhatIf:$false }
    }

    if ($CrashDumpType -ne 'Unchanged') {
        # An active memory dump is a complete dump (1) with FilterPages set; Automatic is 7.
        $map = @{ None = 0; Complete = 1; Kernel = 2; Small = 3; Automatic = 7; Active = 1 }
        $ccKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl'
        $cc = Get-ItemProperty -LiteralPath $ccKey
        $cur = [string]$cc.CrashDumpEnabled
        if ($cc.PSObject.Properties['FilterPages'] -and $cc.FilterPages -eq 1 -and $cc.CrashDumpEnabled -eq 1) { $cur = 'Active' }
        $want = [string]$map[$CrashDumpType]
        if ($CrashDumpType -eq 'Active') { $want = 'Active' }
        Invoke-Step 'Crash dump type' $cur $want {
            Set-ItemProperty -LiteralPath $ccKey -Name CrashDumpEnabled -Value $map[$CrashDumpType] -WhatIf:$false
            if ($CrashDumpType -eq 'Active') { Set-ItemProperty -LiteralPath $ccKey -Name FilterPages -Value 1 -Type DWord -WhatIf:$false }
            elseif ($cc.PSObject.Properties['FilterPages']) { Remove-ItemProperty -LiteralPath $ccKey -Name FilterPages -WhatIf:$false }
            $script:restart = $true }
    }

    if ($AutomaticPageFile) {
        $cs = Get-CimInstance -ClassName Win32_ComputerSystem
        Invoke-Step 'Automatic page file' ([string]$cs.AutomaticManagedPagefile) 'True' {
            Set-CimInstance -InputObject $cs -Property @{ AutomaticManagedPagefile = $true } -WhatIf:$false
            $script:restart = $true }
    }

    foreach ($pair in @(@('System', $SystemLogSizeMB), @('Application', $SystemLogSizeMB), @('Security', $SecurityLogSizeMB))) {
        if ($pair[1] -le 0) { continue }
        $cur = [string]([int]((Get-WinEvent -ListLog $pair[0]).MaximumSizeInBytes / 1MB))
        Invoke-Step "Event log $($pair[0]) size MB" $cur ([string]$pair[1]) {
            Limit-EventLog -LogName $pair[0] -MaximumSize ([int64]$pair[1] * 1MB) -WhatIf:$false }
    }

    if ($PowerPlan -ne 'Unchanged') {
        $guid = @{ HighPerformance = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'; Balanced = '381b4222-f694-41f0-9685-ff5bb260df2e' }[$PowerPlan]
        $active = Get-CimInstance -Namespace 'root/cimv2/power' -ClassName Win32_PowerPlan -Filter 'IsActive=True'
        $curGuid = ([string]$active.InstanceID) -replace '^.*\{(.*)\}$', '$1'
        Invoke-Step 'Power plan' $curGuid $guid {
            & powercfg.exe /setactive $guid
            if ($LASTEXITCODE -ne 0) { throw "powercfg failed with exit code $LASTEXITCODE" } }
    }

    foreach ($svcName in $DisableService) {
        $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($svcName -replace "'", "''")'"
        if ($null -eq $svc) { $log += [pscustomobject]@{ Item = "Service $svcName"; Before = 'Not found'; After = 'Disabled'; Result = 'No change' }; continue }
        Invoke-Step "Service $svcName start mode" ([string]$svc.StartMode) 'Disabled' {
            Stop-Service -Name $svcName -Force -WhatIf:$false
            Set-Service -Name $svcName -StartupType Disabled -WhatIf:$false }
    }

    if ($DjoinBlobPath) {
        if (-not (Test-Path -LiteralPath $DjoinBlobPath)) { throw 'The djoin provisioning file was not found.' }
        $joined = [string](Get-CimInstance -ClassName Win32_ComputerSystem).PartOfDomain
        Write-Warning 'Domain join is an identity change: it needs approval and a pre-created computer object.'
        Invoke-Step 'Domain join (offline)' $joined 'True' {
            $null = & djoin.exe /requestODJ /loadfile $DjoinBlobPath /windowspath $env:SystemRoot /localos
            if ($LASTEXITCODE -ne 0) { throw "djoin failed with exit code $LASTEXITCODE" }
            $script:restart = $true }
    }

    if ($Apply) {
        Get-Snapshot | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logDir "SRV-06-$stamp-after.json") -Encoding UTF8 -WhatIf:$false
        Write-Verbose "Before and after exports written to $logDir."
    }
} catch {
    $failed = $true
    $log += [pscustomobject]@{ Item = 'Run'; Before = ''; After = ''; Result = "Failed: $($_.Exception.Message)" }
}

$log | Format-Table -AutoSize -Wrap | Out-String -Width 220
if ($failed) { exit 1 }
if ($restart -and $Apply) { Write-Output 'Restart required to finish. The script does not restart the server.'; exit 3010 }
exit 0
