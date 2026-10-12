# Dot-sourced by Start-LabRun.ps1, Invoke-AsLabop.ps1 and Install-LabTask.ps1: copy lab\ to LabRoot, drop a request
# for the watcher (scheduled task 'RemoteHub Lab', runs as labop) and wait for its answer.

$script:LabCfg = Get-Content (Join-Path $PSScriptRoot 'lab.json') -Raw | ConvertFrom-Json
$script:LabRoot = $script:LabCfg.LabRoot
$script:LabTask = 'RemoteHub Lab'

function Publish-LabFiles {
    if (-not (Test-Path (Join-Path $PSScriptRoot 'lab.local.json'))) {
        throw "Missing $PSScriptRoot\lab.local.json - copy lab.local.example.json and set the lab password."
    }
    $target = Join-Path $script:LabRoot 'lab'
    if ((Resolve-Path $PSScriptRoot).Path.TrimEnd('\') -eq $target.TrimEnd('\')) { return }   # already running from LabRoot
    robocopy $PSScriptRoot $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy to $script:LabRoot\lab failed ($LASTEXITCODE)." }
}

# True while the watcher task runs (it runs from boot until shutdown).
function Test-LabWatcher {
    $task = Get-ScheduledTask -TaskName $script:LabTask -ErrorAction SilentlyContinue
    [bool]($task -and $task.State -eq 'Running')
}

# Drops the request into the queue and waits until $DoneFile exists.
function Submit-LabRequest {
    param(
        [Parameter(Mandatory)][hashtable]$Request,
        [Parameter(Mandatory)][string]$DoneFile,
        [int]$TimeoutMin = 240
    )
    if (-not (Test-LabWatcher)) {
        throw "The lab watcher (scheduled task '$script:LabTask') isn't running. It starts at boot; to start it now " +
            "run lab\Install-LabTask.ps1 again in an elevated shell."
    }
    $queue = Join-Path $script:LabRoot 'queue'
    # write under a temporary name first so the watcher never reads a half-written request
    $tmp = Join-Path $queue "$($Request.Id).tmp"
    $Request | ConvertTo-Json | Set-Content $tmp -Encoding UTF8
    Move-Item $tmp (Join-Path $queue "$($Request.Id).json")

    $deadline = (Get-Date).AddMinutes($TimeoutMin)
    while (-not (Test-Path $DoneFile)) {
        if ((Get-Date) -gt $deadline) { throw "No answer from the lab watcher after $TimeoutMin min." }
        if (-not (Test-LabWatcher)) { throw "The lab watcher stopped while working on request $($Request.Id)." }
        Start-Sleep -Seconds 2
    }
}
