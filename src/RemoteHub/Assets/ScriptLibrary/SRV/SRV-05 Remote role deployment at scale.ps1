<#
.SYNOPSIS
    Installs server features on a list of servers through PowerShell remoting, in batches with a failure cap.
.DESCRIPTION
    Runs the SRV-03 install logic on each target server with a throttle limit, collects one result record per
    server (status, restart needed, error) and stops after a batch when failures exceed the threshold.
    Kerberos double-hop blocks network shares inside the remote session, so -Source must be a path that is
    local on each target. CredSSP is not used. Prefer a JEA endpoint (SRV-22) via -ConfigurationName, and
    deploy in pilot batches; never target the whole fleet at once. Use -WhatIf for the plan.
.NOTES
    RemoteHub: id=SRV-05; context=AsMe; os=Server; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$ComputerName = @(),
    # Text file with one server name per line; lines starting with # are ignored.
    [string]$ComputerListPath = '',
    [string[]]$Feature = @(),
    [bool]$IncludeManagementTools = $false,
    # Path that exists locally on every target (staged install source), never a UNC path.
    [string]$Source = '',
    [string]$ConfigurationName = 'Microsoft.PowerShell',
    [int]$ThrottleLimit = 5,
    [int]$BatchSize = 10,
    [int]$MaxFailures = 1
)

$ErrorActionPreference = 'Stop'

$remoteBlock = {
    param([string[]]$Features, [bool]$Tools, [string]$Src, [bool]$DoInstall)
    $ErrorActionPreference = 'Stop'
    $missing = @($Features | Where-Object { -not (Get-WindowsFeature -Name $_).Installed })
    $out = [ordered]@{ Planned = ($missing -join ','); Status = 'NoChange'; RestartNeeded = $false; Error = '' }
    if ($missing.Count -eq 0) { return [pscustomobject]$out }
    if (-not $DoInstall) { $out.Status = 'Planned'; return [pscustomobject]$out }
    if ($Src -and -not (Test-Path -LiteralPath $Src)) { throw "Source '$Src' not found on this server." }
    $p = @{ Name = $missing; IncludeManagementTools = $Tools }
    if ($Src) { $p.Source = $Src }
    $r = Install-WindowsFeature @p
    $out.RestartNeeded = ([string]$r.RestartNeeded -eq 'Yes')
    if ($r.Success) { $out.Status = 'Installed' } else { $out.Status = 'Failed'; $out.Error = 'Install-WindowsFeature reported failure.' }
    [pscustomobject]$out
}

$records = @()
$exitCode = 0
try {
    $targets = @($ComputerName)
    if ($ComputerListPath) {
        $targets += @(Get-Content -LiteralPath $ComputerListPath -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    }
    $targets = @($targets | Where-Object { $_ } | Sort-Object -Unique)
    if ($targets.Count -eq 0 -or $Feature.Count -eq 0) {
        Write-Output 'Nothing to do: give -ComputerName or -ComputerListPath and -Feature.'
        exit 0
    }
    if ($Source -match '^\\\\') { throw 'Source must be local on each target (Kerberos double-hop blocks UNC paths).' }
    if ($BatchSize -lt 1) { $BatchSize = 1 }

    $failures = 0
    for ($i = 0; $i -lt $targets.Count; $i += $BatchSize) {
        $batch = @($targets[$i..([Math]::Min($i + $BatchSize, $targets.Count) - 1)])
        $doInstall = $PSCmdlet.ShouldProcess(($batch -join ', '), "Install features: $($Feature -join ', ')")
        Write-Verbose "Batch starting at $i with $($batch.Count) servers."
        $errs = $null
        $res = @(Invoke-Command -ComputerName $batch -ConfigurationName $ConfigurationName -ThrottleLimit $ThrottleLimit `
            -ScriptBlock $remoteBlock -ArgumentList $Feature, $IncludeManagementTools, $Source, $doInstall `
            -ErrorAction SilentlyContinue -ErrorVariable errs)
        foreach ($server in $batch) {
            $r = $res | Where-Object { $_.PSComputerName -eq $server } | Select-Object -First 1
            if ($null -ne $r) {
                $records += [pscustomobject]@{ Server = $server; Status = $r.Status; Planned = $r.Planned; RestartNeeded = $r.RestartNeeded; Error = $r.Error }
                if ($r.Status -eq 'Failed') { $failures++ }
            } else {
                $msg = @($errs | Where-Object { $_.TargetObject -eq $server -or "$($_.OriginInfo.PSComputerName)" -eq $server } | Select-Object -First 1)
                $text = 'No result returned'
                if ($msg.Count -gt 0) { $text = $msg[0].Exception.Message }
                $records += [pscustomobject]@{ Server = $server; Status = 'Failed'; Planned = ''; RestartNeeded = $false; Error = $text }
                $failures++
            }
        }
        if ($failures -gt $MaxFailures) {
            Write-Warning "Stopping: $failures failures exceed the threshold of $MaxFailures."
            foreach ($rest in @($targets | Select-Object -Skip ($i + $BatchSize))) {
                $records += [pscustomobject]@{ Server = $rest; Status = 'NotRun'; Planned = ''; RestartNeeded = $false; Error = 'Run stopped after failure threshold' }
            }
            break
        }
    }

    $records | Format-Table -AutoSize -Wrap | Out-String -Width 220
    [pscustomobject]@{
        Servers       = $records.Count
        Installed     = @($records | Where-Object { $_.Status -eq 'Installed' }).Count
        NoChange      = @($records | Where-Object { $_.Status -eq 'NoChange' }).Count
        Planned       = @($records | Where-Object { $_.Status -eq 'Planned' }).Count
        Failed        = @($records | Where-Object { $_.Status -eq 'Failed' }).Count
        NotRun        = @($records | Where-Object { $_.Status -eq 'NotRun' }).Count
        RestartNeeded = @($records | Where-Object { $_.RestartNeeded -eq $true }).Count
    }
    if (@($records | Where-Object { $_.Status -eq 'Failed' -or $_.Status -eq 'NotRun' }).Count -gt 0) { $exitCode = 1 }
    elseif (@($records | Where-Object { $_.RestartNeeded -eq $true }).Count -gt 0) { $exitCode = 3010 }
} catch {
    Write-Error "Remote deployment failed: $($_.Exception.Message)" -ErrorAction Continue
    $exitCode = 1
}
exit $exitCode
