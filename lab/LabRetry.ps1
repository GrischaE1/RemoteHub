# The second attempt of a failed file and what it tells (docs/test-lab-spec.md, "Retry and flaky cases"). Dot-sourced by
# Invoke-LabTests.ps1, Get-LabFailures.ps1 and Get-LabFlaky.ps1; needs neither Hyper-V nor the lab's config, so the
# tests in tests\RemoteHub.Tests (LabRetryTests) run it as it is.
#
# A file that fails (or runs past its time limit) runs once more, in full, from its VM's checkpoint. Its first results go
# to <kind>\attempt1\ as attempt1-<file>.xml (+ .summary.json), with attempt1-console-<file>.log next to them - names
# the readers of pester-*.xml don't take, so a case is counted once, by its last attempt. A case that failed in the first
# attempt and passed in the second is flaky: listed in the run's flaky.json, the run can still pass. One that failed in
# both is a failure as before.

# The cases of one Pester XML: Id (the case ID at the start of the It's name), Result (Success, Failure, Skipped, ...),
# Message. An XML that can't be read answers nothing.
function Get-LabCaseResults {
    param([Parameter(Mandatory)][string]$Xml)
    if (-not (Test-Path -LiteralPath $Xml)) { return }
    $x = $null
    try { [xml]$x = Get-Content -LiteralPath $Xml -Raw } catch { return }
    foreach ($c in @($x.SelectNodes('//test-case'))) {
        $it = $c.GetAttribute('description')
        if (-not $it) { $it = ($c.GetAttribute('name') -split '\.')[-1] }
        if ($it -notmatch '\b([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,}[a-z]?)\b') { continue }
        $msg = ''
        foreach ($n in 'failure', 'reason') {
            $node = $c.SelectSingleNode("$n/message")
            if ($node -and $node.InnerText) { $msg = ($node.InnerText -replace '\s+', ' ').Trim(); break }
        }
        [pscustomobject]@{ Id = $Matches[1]; Result = $c.GetAttribute('result'); Message = $msg }
    }
}

# Moves a file's first results out of the way before its second attempt. Answers the moved XML's path (or $null when the
# first attempt left none - a file ended at its time limit).
function Move-LabAttempt {
    param([Parameter(Mandatory)][string]$KindDir, [Parameter(Mandatory)][string]$File)
    $dir = Join-Path $KindDir 'attempt1'
    $null = New-Item -ItemType Directory -Path $dir -Force
    $moved = $null
    $pairs = @(
        @("pester-$File.xml", "attempt1-$File.xml"),
        @("pester-$File.xml.summary.json", "attempt1-$File.xml.summary.json"),
        @("pester-$File.xml.timing.json", "attempt1-$File.xml.timing.json"),
        @("console-$File.log", "attempt1-console-$File.log"))
    foreach ($p in $pairs) {
        $from = Join-Path $KindDir $p[0]
        if (-not (Test-Path -LiteralPath $from)) { continue }
        $to = Join-Path $dir $p[1]
        Move-Item -LiteralPath $from -Destination $to -Force
        if ($p[0] -like '*.xml') { $moved = $to }
    }
    $moved
}

# Puts a first attempt's results back when its second attempt won't run (the run drained or ended): its failures count.
function Restore-LabAttempt {
    param([Parameter(Mandatory)][string]$KindDir, [Parameter(Mandatory)][string]$File)
    $dir = Join-Path $KindDir 'attempt1'
    foreach ($n in "$File.xml", "$File.xml.summary.json", "$File.xml.timing.json") {
        $from = Join-Path $dir "attempt1-$n"
        if (Test-Path -LiteralPath $from) { Move-Item -LiteralPath $from -Destination (Join-Path $KindDir "pester-$n") -Force }
    }
    $log = Join-Path $dir "attempt1-console-$File.log"
    if (Test-Path -LiteralPath $log) { Move-Item -LiteralPath $log -Destination (Join-Path $KindDir "console-$File.log") -Force }
}

# The cases of a retried file that failed first and passed then: one row each (Id, File, Kind, Vm, Message = the first
# failure). -TimedOut: the first attempt ended at its time limit, so every case that passed the second time counts.
function Get-LabFlakyCases {
    param(
        [string]$FirstXml,
        [Parameter(Mandatory)][string]$SecondXml,
        [Parameter(Mandatory)][string]$File,
        [Parameter(Mandatory)][string]$Kind,
        [string]$Vm,
        [switch]$TimedOut
    )
    $first = @{}
    if ($FirstXml) { foreach ($c in @(Get-LabCaseResults -Xml $FirstXml)) { $first[$c.Id] = $c } }
    foreach ($c in @(Get-LabCaseResults -Xml $SecondXml)) {
        if ($c.Result -ne 'Success') { continue }
        $before = $first[$c.Id]
        if ($before -and $before.Result -eq 'Failure') {
            [pscustomobject]@{ Id = $c.Id; File = $File; Kind = $Kind; Vm = $Vm; Message = $before.Message }
        }
        elseif ($TimedOut -and -not $before) {
            [pscustomobject]@{ Id = $c.Id; File = $File; Kind = $Kind; Vm = $Vm; Message = 'the first attempt ended at its time limit' }
        }
    }
}

# A run's flaky.json, one object per case. Windows PowerShell 5.1 hands a JSON array on as one object (piped straight
# on, every case became one row: "FLAKY System.Object[]" in run 20261026-144106), so it is assigned first.
function Read-LabFlaky {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $list = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($f in @($list)) { if ($f -and $f.PSObject.Properties['Id'] -and $f.Id) { $f } }
}

# Every finished run under -Root, newest last: per case how often it failed (in its last attempt) and how often it was
# flaky (failed, then passed on the retry), the runs it ran in, and the last time and message. Runs with tags or -Quick
# count too - they ran the case - but runs that never finished (no done.json) don't.
function Get-LabFlakyHistory {
    param([Parameter(Mandatory)][string]$Root, [int]$Runs = 30)
    $dirs = @(Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and (Test-Path (Join-Path $_.FullName 'done.json')) } |
            Sort-Object Name | Select-Object -Last $Runs)
    $cases = @{}
    function Entry([string]$id) {
        if (-not $cases.ContainsKey($id)) { $cases[$id] = [pscustomobject]@{ Id = $id; Ran = 0; Failed = 0; Flaky = 0; Last = ''; LastResult = ''; Message = '' } }
        $cases[$id]
    }
    foreach ($d in $dirs) {
        $seen = @{}
        foreach ($xml in @(Get-ChildItem -LiteralPath $d.FullName -Recurse -Filter 'pester-*.xml' -ErrorAction SilentlyContinue)) {
            foreach ($c in @(Get-LabCaseResults -Xml $xml.FullName)) {
                if ($c.Result -notin 'Success', 'Failure') { continue }
                $e = Entry $c.Id
                if (-not $seen.ContainsKey($c.Id)) { $e.Ran++; $seen[$c.Id] = $true }
                if ($c.Result -eq 'Failure') { $e.Failed++; $e.Last = $d.Name; $e.LastResult = 'failed'; $e.Message = $c.Message }
            }
        }
        foreach ($f in @(Read-LabFlaky -Path (Join-Path $d.FullName 'flaky.json'))) {
            $e = Entry $f.Id
            $e.Flaky++
            if ($e.Last -ne $d.Name) { $e.Last = $d.Name; $e.LastResult = 'flaky'; $e.Message = $f.Message }
        }
    }
    $cases.Values | Where-Object { $_.Failed -or $_.Flaky } | Sort-Object @{ Expression = { $_.Failed + $_.Flaky }; Descending = $true }, Id
}
