#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the lab scenarios (lab\tests\*.Tests.ps1) once per kind of VM (W11-26H2-en-US, Server-2025-en-US,
    Console-26H2-en-US, ...). Runs as labop - from the scheduled task or through Start-LabRun.ps1.
.DESCRIPTION
    Every VM of a selected kind works through the files in parallel with the others: lane 1 ("... - en-US") and lane 2
    ("... - en-US - 2") are VMs of the same kinds, a file runs on one of them. A file's header says what it needs:
      # LabRole: console | device | any  console = RemoteHub (client) tests, on the Console VMs (in a lab without one: on
                                        the client Windows VMs, as before); device = agent tests, on W11 / Server.
                                        any = on every kind, the file's parts decide (Test-LabConsoleVm, Os).
                                        Not given: console when the file installs or starts RemoteHub, else device.
      # LabNeeds: lane                  it uses another VM of its lane (Get-LabDevice): the whole lane is kept for it.
                                        Found by itself when the file calls Get-LabDevice.
      # LabNeeds: jumphost              it uses the jump host (one at a time in the lab). Found by itself too.
      # TimeLimitMin: 150               more time than -FileTimeoutMin.
    A VM is reused between files (RHLAB_REUSE=1, see Initialize-LabVm); a file that fails drops the state of the VMs it
    used, so the next file there starts from the checkpoint. The first time a VM is set up for a state in the run,
    Initialize-LabVm takes a run checkpoint ("RHLAB <run> <state>"); later files that need that state revert to it
    instead of installing again (off: -NoSnapshots). Run checkpoints are removed at the end of the run and, left by a
    cancelled run, at the start of the next.
.EXAMPLE
    .\Invoke-LabTests.ps1                                              # all kinds, all scenarios
    .\Invoke-LabTests.ps1 -Vm W11-26H2-en-US,Console-26H2-en-US -Tag Smoke
#>
[CmdletBinding()]
param(
    [string[]]$Vm,
    [string[]]$Tag,
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    # per scenario file and VM: a file that runs longer is ended and its cases are reported as failed
    [int]$FileTimeoutMin = 60,
    # every file from the checkpoint, as before (no reuse between files, no run checkpoints)
    [switch]$NoReuse,
    # no run checkpoints (Initialize-LabVm installs again whenever a VM isn't in the state a file needs); also
    # RHLAB_SNAPSHOTS=0
    [switch]$NoSnapshots
)
$ErrorActionPreference = 'Stop'
# powershell -File passes "-Vm a,b" as one string
$Vm = @($Vm | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Tag = @($Tag | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

Import-Module (Join-Path $PSScriptRoot 'RemoteHub.TestLab') -Force
$cfg = Get-LabConfig

# Run checkpoints a cancelled run left behind (Stop-LabRun ends the processes, so the finally block below didn't run),
# before anything looks at the VMs' checkpoints. Their merges are waited for (bounded): a revert during one fails.
try {
    Remove-LabRunCheckpoints -MergeTimeoutSec 900 | Where-Object Removed |
        ForEach-Object { Write-Host "$($_.Vm): $($_.Removed) run checkpoint(s) of an earlier run removed" -ForegroundColor Yellow }
}
catch { Write-Warning "Removing run checkpoints of an earlier run failed: $($_.Exception.Message)" }

# the kinds asked for (a VM's Id or its kind both select the kind), and every VM of those kinds (both lanes)
# Only VMs that are ready for the lab: a checkpoint to revert to (a VM still being set up has none yet - it is left out
# and named, so a half-made second lane doesn't take files and fail them)
$labVms = @(Get-LabVm | Where-Object {
        $v = $_
        if (-not (Get-LabBaseCheckpoint -Name $v.Name)) {
            Write-Host "$($v.Name): no checkpoint yet - not used in this run" -ForegroundColor Yellow; return $false
        }
        # the VM is asked for the lab's accounts (a typo in one fails every file there) - started for it when it is off
        $up = (Get-VM -Name $v.Name).State -eq 'Running'
        if (-not $up) { try { Start-VM -Name $v.Name; Wait-LabVm -Name $v.Name -TimeoutSec 240; $up = $true } catch { Write-Host "$($v.Name): doesn't start ($_)" -ForegroundColor Yellow } }
        if ($up) {
            $want = @($v.AdminAccount, $v.UserAccount, $v.ConsoleAccount) | Where-Object { $_ }
            $missing = try {
                @(Invoke-LabGuest -Name $v.Name -ArgumentList (, $want) -ScriptBlock { param($w) $have = @(Get-LocalUser | ForEach-Object Name); @($w | Where-Object { $have -notcontains $_ }) })
            } catch { @() }
            if ($missing.Count) { Write-Host "$($v.Name): missing account(s) $($missing -join ', ') - not used in this run" -ForegroundColor Yellow; return $false }
        }
        $true
    })
$kinds = @(if ($Vm) { Get-LabVm $Vm | ForEach-Object BaseId } else { $labVms | ForEach-Object BaseId }) | Select-Object -Unique
$hasConsole = @($labVms | Where-Object Role -eq 'Console').Count -gt 0
# the admin PC comes with the devices: with Console VMs in the lab, RemoteHub's tests run there even when only device
# kinds were asked for
if ($hasConsole -and -not @($labVms | Where-Object { $_.Role -eq 'Console' -and $kinds -contains $_.BaseId }).Count) {
    $kinds = @($kinds) + @($labVms | Where-Object Role -eq 'Console' | ForEach-Object BaseId | Select-Object -Unique)
}
$vms = @($labVms | Where-Object { $kinds -contains $_.BaseId })
$runDir = Join-Path $cfg.LabRoot "results\$RunId"
$null = New-Item -ItemType Directory -Path $runDir -Force
$testFiles = @(Get-ChildItem (Join-Path $PSScriptRoot 'tests') -Filter '*.Tests.ps1' | ForEach-Object FullName)
$msi = Get-ChildItem (Join-Path $cfg.LabRoot 'msi') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue |
    Select-Object -First 1

$previous = Get-ChildItem (Join-Path $cfg.LabRoot 'msi\previous') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue |
    Select-Object -First 1

Write-Host "Run $RunId - kinds: $($kinds -join ', ') - VMs: $(($vms | ForEach-Object Id) -join ', ') - agent: $(if ($msi) { $msi.Name } else { 'none' })$(if ($previous) { " (previous: $($previous.Name))" })"

# What was tested, for the PR report (New-LabReport.ps1): the packages with their checksums and each kind's checkpoint.
function Get-MsiInfo($file) {
    if (-not $file) { return $null }
    [pscustomobject]@{ Name = $file.Name; Sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash }
}
[pscustomobject]@{
    RunId         = $RunId
    Started       = (Get-Date).ToString('s')
    Host          = $env:COMPUTERNAME
    Tags          = @($Tag)
    Agent         = Get-MsiInfo $msi
    PreviousAgent = Get-MsiInfo $previous
    Vms           = @($kinds | ForEach-Object {
            $k = $_
            $first = @($vms | Where-Object BaseId -eq $k | Sort-Object Lane)[0]
            $cp = Get-LabBaseCheckpoint -Name $first.Name
            [pscustomobject]@{
                Id             = $k
                Name           = $first.Name
                Lanes          = @($vms | Where-Object BaseId -eq $k | ForEach-Object Name)
                Checkpoint     = $(if ($cp) { $cp.Name } else { $null })
                CheckpointDate = $(if ($cp) { $cp.CreationTime.ToString('yyyy-MM-dd HH:mm') } else { $null })
            }
        })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $runDir 'run.json') -Encoding UTF8

# ------------------------------------------------------------------ the work: one item per file and kind
function Get-FileNeeds([string]$File) {
    $text = [IO.File]::ReadAllText($File)
    $head = ($text -split "`n" | Select-Object -First 40) -join "`n"
    $role = if ($head -match '(?m)^#\s*LabRole:\s*(console|device|any)') { $Matches[1] }
            elseif ($text -match 'Install-LabClient|Start-LabClient') { 'console' } else { 'device' }
    $limit = $FileTimeoutMin
    if ($head -match '(?m)^#\s*TimeLimitMin:\s*(\d+)' -and [int]$Matches[1] -gt $limit) { $limit = [int]$Matches[1] }
    [pscustomobject]@{
        Role     = $role
        Lane     = ($head -match '(?m)^#\s*LabNeeds:.*\blane\b') -or ($text -match 'Get-LabDevice|Where-Object Os -eq .Server.')
        # the kinds of the lane's other VMs it uses: Get-LabDevice -Os <kind> (Server when not given)
        LaneOs   = @(@([regex]::Matches($text, 'Get-LabDevice[^\r\n]*?-Os\s+''?(\w+)') | ForEach-Object { $_.Groups[1].Value }) + @(if ($text -match 'Get-LabDevice(?![^\r\n]*-Os)' -or $text -match 'Where-Object Os -eq .Server.') { 'Server' }) | Select-Object -Unique)
        JumpHost = ($head -match '(?m)^#\s*LabNeeds:.*\bjumphost\b') -or ($text -match 'Move-LabVmBehindJump|Use-LabJump|Start-LabJumpHost')
        Limit    = $limit
    }
}
# which kinds a file runs on: console files on the Console kind (no Console VMs in the lab: on the client Windows kinds),
# device files on the W11 / Server kinds
function Test-KindRuns($needs, [string]$Kind) {
    $one = @($vms | Where-Object BaseId -eq $Kind)[0]
    if ($needs.Role -eq 'any') { $true }
    elseif ($needs.Role -eq 'console') { if ($hasConsole) { $one.Role -eq 'Console' } else { $one.Os -ne 'Server' } }
    else { $one.Role -ne 'Console' }
}
$queue = [Collections.Generic.List[object]]::new()
foreach ($file in $testFiles) {
    # with -Tag only the files that have one of the tags (Pester would find nothing to run in the others)
    if ($Tag) { $text = [IO.File]::ReadAllText($file); if (-not @($Tag | Where-Object { $text.Contains("'$_'") }).Count) { continue } }
    $needs = Get-FileNeeds $file
    foreach ($k in $kinds) {
        if (Test-KindRuns $needs $k) { $queue.Add([pscustomobject]@{ File = $file; Kind = $k; Needs = $needs }) }
    }
}
Write-Host "$($queue.Count) file runs on $($vms.Count) VMs"

if (-not $NoReuse) { $env:RHLAB_REUSE = '1' } else { $env:RHLAB_REUSE = '0'; foreach ($v in $vms) { Clear-LabVmState -Name $v.Name } }
# the file processes inherit these: Initialize-LabVm takes run checkpoints only inside a run, named with its id
$snapshots = (-not $NoReuse) -and (-not $NoSnapshots) -and $env:RHLAB_SNAPSHOTS -ne '0'
$env:RHLAB_SNAPSHOTS = $(if ($snapshots) { '1' } else { '0' })
$env:RHLAB_RUN_ID = $RunId
Write-Host "Reuse between files: $(if ($NoReuse) { 'off' } else { 'on' }) - run checkpoints: $(if ($snapshots) { 'on' } else { 'off' })"

# ------------------------------------------------------------------ the scheduler
# A VM is busy with its own file, or held for another VM's file of its lane (Get-LabDevice). Files that use the jump
# host run one at a time. Each file runs in its own process (Invoke-LabTestFile.ps1); one that runs past its time
# limit is ended and its cases are reported as failed.
$running = @{}      # VM name -> the item running there
$heldBy = @{}       # VM name -> the VM whose file holds it
$jumpBusy = $false
$timeouts = @{}     # kind -> list
$results = [Collections.Generic.List[object]]::new()

function Start-Item($item, $v) {
    $kindDir = Join-Path $runDir $item.Kind
    $null = New-Item -ItemType Directory -Path $kindDir -Force
    $base = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileNameWithoutExtension($item.File))
    $xml = Join-Path $kindDir "pester-$base.xml"
    $log = Join-Path $kindDir "console-$base.log"
    # evidence per VM: two lanes run files of the same kind at once, and their evidence names may meet. Lane 1's Id is
    # the kind, so its evidence stays next to the Pester results; lane 2 gets <run>\<kind>-2.
    $evidenceDir = Join-Path $runDir $v.Id
    $null = New-Item -ItemType Directory -Path $evidenceDir -Force
    $runner = Join-Path $PSScriptRoot 'Invoke-LabTestFile.ps1'
    $cmd = "& '$runner' -TestFile '$($item.File)' -VmName '$($v.Name)' -EvidenceDir '$evidenceDir' -OutputPath '$xml' -Tag '$($Tag -join ',')' *> '$log'"
    $p = Start-Process powershell.exe -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command', $cmd -WindowStyle Hidden -PassThru
    $held = @()
    if ($item.Needs.Lane) {
        # the lane's VMs of the kinds it uses (none found: the whole lane, to be safe)
        $held = @($labVms | Where-Object { $_.Lane -eq $v.Lane -and $_.Name -ne $v.Name -and $_.Role -and (-not $item.Needs.LaneOs -or $item.Needs.LaneOs -contains $_.Os) })
        foreach ($h in $held) { $script:heldBy[$h.Name] = $v.Name }
    }
    if ($item.Needs.JumpHost) { $script:jumpBusy = $true }
    $script:running[$v.Name] = [pscustomobject]@{ Item = $item; Vm = $v; Process = $p; Base = $base; Xml = $xml; Held = $held
        Deadline = (Get-Date).AddMinutes($item.Needs.Limit); Started = Get-Date }
    Write-Host "$((Get-Date).ToString('HH:mm:ss')) start  $base on $($v.Id)$(if ($held) { " (+ $(($held | ForEach-Object Id) -join ', '))" })"
}

function Complete-Item($r) {
    $timedOut = -not $r.Process.HasExited
    if ($timedOut) { & taskkill.exe /PID $r.Process.Id /T /F 2>&1 | Out-Null }
    $sum = "$($r.Xml).summary.json"
    $c = if (Test-Path $sum) { Get-Content $sum -Raw | ConvertFrom-Json } else { $null }
    $failed = if ($c) { [int]$c.Failed } else { 0 }
    $min = [int]((Get-Date) - $r.Started).TotalMinutes
    if ($timedOut) {
        Write-Host "$((Get-Date).ToString('HH:mm:ss')) $($r.Base) on $($r.Vm.Id): ended after $($r.Item.Needs.Limit) min (time limit)" -ForegroundColor Red
        if (-not $script:timeouts.ContainsKey($r.Item.Kind)) { $script:timeouts[$r.Item.Kind] = [Collections.Generic.List[object]]::new() }
        $script:timeouts[$r.Item.Kind].Add([pscustomobject]@{ File = $r.Base; Minutes = $r.Item.Needs.Limit; Ended = (Get-Date).ToString('s'); Vm = $r.Vm.Id })
    }
    else {
        Write-Host ("{0} done   {1} on {2}: {3} passed, {4} failed, {5} skipped ({6} min)" -f (Get-Date).ToString('HH:mm:ss'), $r.Base, $r.Vm.Id,
            $(if ($c) { $c.Passed } else { 0 }), $failed, $(if ($c) { $c.Skipped } else { 0 }), $min)
    }
    # a failed or ended file may have left its VMs changed: the next file there starts from the checkpoint
    if ($timedOut -or $failed -gt 0) { foreach ($n in @($r.Vm.Name) + @($r.Held | ForEach-Object Name)) { Clear-LabVmState -Name $n } }
    foreach ($h in $r.Held) { $script:heldBy.Remove($h.Name) }
    if ($r.Item.Needs.JumpHost) { $script:jumpBusy = $false }
    $script:running.Remove($r.Vm.Name)
}

try {
    $drainFile = Join-Path $cfg.LabRoot 'drain.txt'
    if (Test-Path $drainFile) { Remove-Item $drainFile -Force; Write-Host 'drain.txt from an earlier run removed' }
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($r in @($running.Values)) { if ($r.Process.HasExited -or (Get-Date) -gt $r.Deadline) { Complete-Item $r } }
        # LabRoot\drain.txt: no new files - the running ones finish, then the run ends (the rest is reported as not run)
        if ((Test-Path $drainFile) -and $queue.Count) {
            Write-Host "$((Get-Date).ToString('HH:mm:ss')) drain.txt: $($queue.Count) file runs not started" -ForegroundColor Yellow
            $queue.Clear()
        }
        foreach ($v in $vms | Sort-Object Lane, Role) {
            if ($running.ContainsKey($v.Name) -or $heldBy.ContainsKey($v.Name)) { continue }
            # the next file of this VM's kind it can start now: lane files need the lane's other VMs free
            $pick = $null
            foreach ($item in $queue) {
                if ($item.Kind -ne $v.BaseId) { continue }
                if ($item.Needs.JumpHost -and $jumpBusy) { continue }
                if ($item.Needs.Lane) {
                    $others = @($labVms | Where-Object { $_.Lane -eq $v.Lane -and $_.Name -ne $v.Name -and $_.Role -and (-not $item.Needs.LaneOs -or $item.Needs.LaneOs -contains $_.Os) })
                    if (@($others | Where-Object { $running.ContainsKey($_.Name) -or $heldBy.ContainsKey($_.Name) }).Count) { continue }
                }
                $pick = $item; break
            }
            if ($pick) { $null = $queue.Remove($pick); Start-Item $pick $v }
        }
        Start-Sleep -Seconds 3
    }
}
finally {
    foreach ($r in @($running.Values)) { Complete-Item $r }
    foreach ($v in $labVms | Where-Object { $_.Role }) { try { Stop-LabVm -Name $v.Name } catch { } }
    # after the last file (also when drained or ended early): the run checkpoints go. The VMs are off by now, so nothing
    # runs from them while they merge; the next run waits for merges still going on.
    try {
        Remove-LabRunCheckpoints -MergeTimeoutSec 120 | Where-Object Removed |
            ForEach-Object { Write-Host "$($_.Vm): $($_.Removed) run checkpoint(s) removed" }
    }
    catch { Write-Warning "Removing the run checkpoints failed: $($_.Exception.Message) - the next run removes them" }
}

$summary = foreach ($k in $kinds) {
    $kindDir = Join-Path $runDir $k
    $null = New-Item -ItemType Directory -Path $kindDir -Force
    $to = @(if ($timeouts.ContainsKey($k)) { $timeouts[$k] })
    if ($to) { ConvertTo-Json @($to) | Set-Content (Join-Path $kindDir 'timeouts.json') -Encoding UTF8 }
    $counts = @(Get-ChildItem $kindDir -Filter 'pester-*.xml.summary.json' | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
    $failed = [int]($counts | Measure-Object Failed -Sum).Sum + $to.Count
    [pscustomobject]@{
        Vm       = $k
        Name     = (@($vms | Where-Object BaseId -eq $k | Sort-Object Lane)[0]).Name
        Passed   = [int]($counts | Measure-Object Passed -Sum).Sum
        Failed   = $failed
        Skipped  = [int]($counts | Measure-Object Skipped -Sum).Sum
        TimedOut = @($to | ForEach-Object File) -join ', '
        Result   = $(if ($failed) { 'Failed' } else { 'Passed' })
    }
}
$summary | ConvertTo-Json | Set-Content (Join-Path $runDir 'summary.json') -Encoding UTF8
$summary | Format-Table -AutoSize | Out-String | Write-Host
exit [int](($summary | Measure-Object Failed -Sum).Sum)
