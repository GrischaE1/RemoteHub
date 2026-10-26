#Requires -Version 5.1
<#
.SYNOPSIS
    Direct call from any shell, no elevation: builds the lab harness (RemoteHub's client code) into LabRoot\harness,
    copies lab\ and the newest agent MSI to LabRoot, has the scheduled task 'RemoteHub Lab' run the scenarios as
    labop, waits, then copies the results back to artifacts\lab\<run>\.
.EXAMPLE
    .\lab\Start-LabRun.ps1
    .\lab\Start-LabRun.ps1 -Vm W11-26H2-en-US -Tag Smoke
    .\lab\Start-LabRun.ps1 -Tag A-INST -NoBuild
    .\lab\Start-LabRun.ps1 -Quick -FailFast              # the build check: device files on one kind, stop early
    .\lab\Start-LabRun.ps1 -NoRetry                       # a failed file isn't run a second time
#>
[CmdletBinding()]
param(
    [string[]]$Vm,
    [string[]]$Tag,
    [switch]$NoBuild,
    # Invoke-LabTests.ps1 -Quick / -FailFast / -NoRetry (passed in results\<run>\options.json: the queue takes only -Vm and -Tag)
    [switch]$Quick,
    [switch]$FailFast,
    [switch]$NoRetry
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabQueue.ps1')

$repo = Split-Path $PSScriptRoot
Publish-LabFiles

if (-not $NoBuild) {
    Write-Host 'Building the lab harness...'
    $build = dotnet build (Join-Path $repo 'tests\RemoteHub.LabHarness') -c Release -o (Join-Path $LabRoot 'harness') -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-Object -Last 30 | Write-Host; throw 'Building the lab harness failed.' }
}

# The agent under test: newest RemoteHub-Agent-<version>-x64.msi in artifacts\. The one before it goes to msi\previous
# (the update scenarios install it first, then update to the one under test).
$agentMsis = @(Get-ChildItem (Join-Path $repo 'artifacts') -Filter 'RemoteHub-Agent-*-x64.msi' |
        Where-Object { $_.Name -match '^RemoteHub-Agent-\d+\.\d+\.\d+-x64\.msi$' } |
        Sort-Object { [version]($_.Name -replace '^RemoteHub-Agent-(.+)-x64\.msi$', '$1') } -Descending)
function Publish-Msi($file, $dir) {
    $null = New-Item -ItemType Directory -Path $dir -Force
    Get-ChildItem $dir -Filter *.msi | Where-Object { -not $file -or $_.Name -ne $file.Name } | Remove-Item
    if ($file) {
        robocopy $file.DirectoryName $dir $file.Name /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy of $($file.Name) failed ($LASTEXITCODE)." }
    }
}
$msi = $agentMsis | Select-Object -First 1
$previousMsi = $agentMsis | Select-Object -Skip 1 -First 1
Publish-Msi $msi (Join-Path $LabRoot 'msi')
Publish-Msi $previousMsi (Join-Path $LabRoot 'msi\previous')

# RemoteHub itself (the C / R / UI cases): the package of the same version as the agent under test, the one of the
# previous agent release, and the newest RDP Manager package (same upgrade code - C-MSI-05). By version, not by name
# order: an old "RemoteHub 2.1.0" package would sort above 0.x.
function Find-ClientMsi($agent) {
    if (-not $agent) { return $null }
    $v = $agent.Name -replace '^RemoteHub-Agent-(.+)-x64\.msi$', '$1'
    Get-Item (Join-Path $repo "artifacts\RemoteHub-$v-x64.msi") -ErrorAction SilentlyContinue
}
Publish-Msi (Find-ClientMsi $msi) (Join-Path $LabRoot 'client')
Publish-Msi (Find-ClientMsi $previousMsi) (Join-Path $LabRoot 'client\previous')
Publish-Msi (Get-ChildItem (Join-Path $repo 'artifacts') -Filter 'RdpManager-*-x64.msi' | Sort-Object { [version]($_.Name -replace '^RdpManager-(.+)-x64\.msi$', '$1') } -Descending | Select-Object -First 1) (Join-Path $LabRoot 'client\legacy')

# The credential provider's lab build and its test server (build\build-credential-provider.ps1 -Configuration Lab),
# for the C-PROV cases.
$cpLab = Join-Path $repo 'artifacts\credential-provider-lab'
if (Test-Path $cpLab) {
    robocopy $cpLab (Join-Path $LabRoot 'credential-provider') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy of the credential provider lab build failed ($LASTEXITCODE)." }
}

# The Group Policy template of this build (policy\RemoteHub.admx, en-US\RemoteHub.adml), for POL-01.
robocopy (Join-Path $repo 'policy') (Join-Path $LabRoot 'policy') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy of the policy templates failed ($LASTEXITCODE)." }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $LabRoot "results\$runId"
# the run's options, and the commit it ran from (RemoteHub Lab selects what changed since a run by it)
$null = New-Item -ItemType Directory -Path $runDir -Force
# (the checkout's .git may be on a share, which git refuses as owned by someone else unless named as safe)
$commit = try { $c = git -c "safe.directory=$($repo -replace '\\', '/')" -C $repo rev-parse HEAD; if ($LASTEXITCODE -eq 0) { $c } } catch { $null }
@{ Quick = [bool]$Quick; FailFast = [bool]$FailFast; NoRetry = [bool]$NoRetry; Commit = $commit } | ConvertTo-Json | Set-Content (Join-Path $runDir 'options.json') -Encoding UTF8
Write-Host "Run $runId$(if ($msi) { ", agent $($msi.Name)" }) - queued for '$LabTask'"
Submit-LabRequest -Request @{ Type = 'tests'; Id = $runId; Vm = @($Vm); Tag = @($Tag) } -DoneFile (Join-Path $runDir 'done.json')

$back = Join-Path $repo "artifacts\lab\$runId"
robocopy $runDir $back /E /NFL /NDL /NJH /NJS /NP | Out-Null
Get-Content (Join-Path $runDir 'console.log') -Tail 60
Write-Host "Results: $back"
# The PR report (case x VM, gate) - its verdict is printed; the exit code stays the number of failed tests.
try { & (Join-Path $PSScriptRoot 'New-LabReport.ps1') -Run $runId -ResultsRoot (Join-Path $repo 'artifacts\lab') }
catch { Write-Warning "No report: $($_.Exception.Message)" }
# The short summary (one line per VM and per failed case) as failures.txt next to the results: what LabConsole.ps1
# shows and what an AI session reads instead of the XMLs and logs.
try {
    $failures = @(& (Join-Path $PSScriptRoot 'Get-LabFailures.ps1') -Run $runId -Root (Join-Path $repo 'artifacts\lab'))
    $failures | Set-Content (Join-Path $back 'failures.txt') -Encoding UTF8
    Write-Host ''
    $failures | ForEach-Object { Write-Host $_ }
}
catch { Write-Warning "No failure summary: $($_.Exception.Message)" }
exit [int](Get-Content (Join-Path $runDir 'done.json') -Raw | ConvertFrom-Json).ExitCode
