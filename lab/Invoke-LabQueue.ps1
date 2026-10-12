#Requires -Version 5.1
<#
.SYNOPSIS
    The action of the scheduled task 'RemoteHub Lab' (runs as labop, starts at boot): watches LabRoot\queue and works
    through the requests that Start-LabRun.ps1 / Invoke-AsLabop.ps1 drop there. Each request runs in its own
    PowerShell process, so changes to the lab code apply to the next request without restarting the watcher.
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

function Invoke-Request {
    param($Req)
    switch ($Req.Type) {
        'tests' {
            # Invoke-LabTests.ps1 in its own process; console.log + done.json land in results\<run>\.
            $runDir = Join-Path $labRoot "results\$($Req.Id)"
            $null = New-Item -ItemType Directory -Path $runDir -Force
            $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $labRoot 'lab\Invoke-LabTests.ps1'), '-RunId', $Req.Id)
            if (@($Req.Vm | Where-Object { $_ }).Count -gt 0) { $arguments += '-Vm', (@($Req.Vm) -join ',') }
            if (@($Req.Tag | Where-Object { $_ }).Count -gt 0) { $arguments += '-Tag', (@($Req.Tag) -join ',') }
            & powershell.exe @arguments *> (Join-Path $runDir 'console.log')
            @{ ExitCode = $LASTEXITCODE } | ConvertTo-Json | Set-Content (Join-Path $runDir 'done.json') -Encoding UTF8
        }
        'script' {
            # Invoke-AsLabop.ps1: the script file is next to the request; output and exit code go back the same way.
            $base = Join-Path $queue $Req.Id
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$base.ps1" *> "$base.out.txt"
            Set-Content "$base.done" -Value $LASTEXITCODE
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
