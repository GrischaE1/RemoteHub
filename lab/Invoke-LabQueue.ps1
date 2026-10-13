#Requires -Version 5.1
<#
.SYNOPSIS
    The action of the scheduled task 'RemoteHub Lab' (runs as labop, starts at boot): watches LabRoot\queue and works
    through the requests that Start-LabRun.ps1 / Invoke-AsLabop.ps1 drop there. Each request runs in its own
    PowerShell process, so changes to the lab code apply to the next request without restarting the watcher (changes
    to this file need a restart: Install-LabTask.ps1). A running request is stopped by creating LabRoot\queue\cancel
    (Stop-LabRun.ps1), or after 12 h (tests) / 2 h (script).
.PARAMETER Once
    Work through the queue once and exit instead of watching (for trying it by hand).
#>
[CmdletBinding()]
param([switch]$Once)
$ErrorActionPreference = 'Stop'

$cfg = Get-Content (Join-Path $PSScriptRoot 'lab.json') -Raw | ConvertFrom-Json
$labRoot = $cfg.LabRoot
$queue = Join-Path $labRoot 'queue'
$null = New-Item -ItemType Directory -Path $queue -Force

# Runs a script in its own PowerShell process with all output in $Log. Ends it (with everything it started) when
# queue\cancel appears - anyone can create that file, labop's processes can't be stopped from other accounts without
# elevation - or after $TimeoutHours. Returns the exit code (-2 cancelled, -3 timed out).
function Invoke-Child {
    param([string]$Script, [string]$Arguments, [string]$Log, [double]$TimeoutHours)
    $cancel = Join-Path $queue 'cancel'
    Remove-Item $cancel -ErrorAction SilentlyContinue
    $command = "& '$Script' $Arguments *> '$Log'; exit `$LASTEXITCODE"
    $p = Start-Process powershell.exe -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "`"$command`"" `
        -PassThru -WindowStyle Hidden
    $null = $p.Handle   # Windows PowerShell 5.1 loses the exit code unless the handle is opened early
    $until = (Get-Date).AddHours($TimeoutHours)
    while (-not $p.WaitForExit(2000)) {
        $reason = if (Test-Path $cancel) { 'cancelled (queue\cancel)'; $code = -2 } elseif ((Get-Date) -gt $until) { "timed out after $TimeoutHours h"; $code = -3 }
        if ($reason) {
            & taskkill.exe /T /F /PID $p.Id 2>&1 | Out-Null
            Remove-Item $cancel -ErrorAction SilentlyContinue
            Add-Content $Log "`r`nLab queue: request $reason - stopped." -ErrorAction SilentlyContinue
            return $code
        }
    }
    $p.ExitCode
}

function Invoke-Request {
    param($Req)
    switch ($Req.Type) {
        'tests' {
            # Invoke-LabTests.ps1 in its own process; console.log + done.json land in results\<run>\.
            $runDir = Join-Path $labRoot "results\$($Req.Id)"
            $null = New-Item -ItemType Directory -Path $runDir -Force
            $arguments = "-RunId '$($Req.Id)'"
            if (@($Req.Vm | Where-Object { $_ }).Count -gt 0) { $arguments += " -Vm '$(@($Req.Vm) -join ',')'" }
            if (@($Req.Tag | Where-Object { $_ }).Count -gt 0) { $arguments += " -Tag '$(@($Req.Tag) -join ',')'" }
            $code = Invoke-Child -Script (Join-Path $labRoot 'lab\Invoke-LabTests.ps1') -Arguments $arguments `
                -Log (Join-Path $runDir 'console.log') -TimeoutHours 12
            @{ ExitCode = $code } | ConvertTo-Json | Set-Content (Join-Path $runDir 'done.json') -Encoding UTF8
        }
        'script' {
            # Invoke-AsLabop.ps1: the script file is next to the request; output and exit code go back the same way.
            $base = Join-Path $queue $Req.Id
            $code = Invoke-Child -Script "$base.ps1" -Arguments '' -Log "$base.out.txt" -TimeoutHours 2
            Set-Content "$base.done" -Value $code
        }
        default { throw "unknown request type '$($Req.Type)'" }
    }
}

do {
    foreach ($file in @(Get-ChildItem $queue -Filter '*.json' | Sort-Object LastWriteTime)) {
        $req = $null
        try {
            $req = Get-Content $file.FullName -Raw | ConvertFrom-Json
            Remove-Item $file.FullName
            Invoke-Request $req
        }
        catch {
            # never let one bad request stop the watcher; tell the waiting caller instead
            $msg = "Lab queue: $($_.Exception.Message)"
            Remove-Item $file.FullName -ErrorAction SilentlyContinue
            if ($req -and $req.Type -eq 'tests') {
                $runDir = Join-Path $labRoot "results\$($req.Id)"
                $null = New-Item -ItemType Directory -Path $runDir -Force
                Set-Content (Join-Path $runDir 'console.log') $msg
                @{ ExitCode = -1 } | ConvertTo-Json | Set-Content (Join-Path $runDir 'done.json') -Encoding UTF8
            }
            elseif ($req) {
                Set-Content (Join-Path $queue "$($req.Id).out.txt") $msg
                Set-Content (Join-Path $queue "$($req.Id).done") -Value -1
            }
        }
    }
    if (-not $Once) { Start-Sleep -Seconds 2 }
} until ($Once)
