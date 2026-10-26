#Requires -Version 5.1
<#
.SYNOPSIS
    A short summary of a lab run: one line per VM, then one line per failed case (ID, VM, the start of the message)
    and where its evidence is. Meant to be read instead of the pester XMLs and console logs - about 1 KB for a run
    with a few failures. A file that failed ran a second time (Invoke-LabTests.ps1): its failures are the second
    attempt's, and the cases that failed only the first time are listed apart as FLAKY (flaky.json, LabRetry.ps1).
.PARAMETER Run
    The run ID (20261019-130557). Default: the newest run under -Root.
.PARAMETER Root
    The folder holding the runs. Default: artifacts\lab of this checkout, where Start-LabRun.ps1 copies them back.
    D:\RemoteHubLab\results works too (the lab's own copy).
.PARAMETER Width
    How many characters of each failure message are shown (whitespace collapsed).
.PARAMETER Skipped
    Also list skipped cases with their reason.
.PARAMETER PassThru
    Return the failed (and with -Skipped the skipped) cases as objects (Result, Id, Vm, File, Message, Evidence)
    instead of the text - for LabConsole.ps1's "failed in the last run".
.EXAMPLE
    .\lab\Get-LabFailures.ps1
    .\lab\Get-LabFailures.ps1 -Run 20261019-130557 -Width 400 -Skipped
#>
[CmdletBinding()]
param(
    [string]$Run,
    [string]$Root = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\lab'),
    [int]$Width = 240,
    [switch]$Skipped,
    [switch]$PassThru
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabRetry.ps1')

if (-not (Test-Path $Root)) { throw "No lab results in $Root - pass -Root (e.g. D:\RemoteHubLab\results)." }
$runs = @(Get-ChildItem $Root -Directory | Where-Object Name -match '^\d{8}-\d{6}$' | Sort-Object Name)
if ($Run) { $dir = Join-Path $Root $Run; if (-not (Test-Path $dir)) { throw "Run $Run not found in $Root." } }
elseif ($runs.Count) { $dir = $runs[-1].FullName; $Run = $runs[-1].Name }
else { throw "No runs in $Root." }

function Short([string]$Text, [int]$Max) {
    $t = ($Text -replace '\s+', ' ').Trim()
    if ($t.Length -gt $Max) { $t.Substring(0, $Max) + ' ...' } else { $t }
}

# the run: agent, tags, and one line per VM
$head = "Run $Run"
$runJson = Join-Path $dir 'run.json'
if (Test-Path $runJson) {
    $meta = Get-Content $runJson -Raw | ConvertFrom-Json
    if ($meta.PSObject.Properties['Agent'] -and $meta.Agent) { $head += " - agent $($meta.Agent.Name -replace '^RemoteHub-Agent-|-x64\.msi$', '')" }
    if ($meta.PSObject.Properties['Tags'] -and @($meta.Tags).Count) { $head += " - tags $(@($meta.Tags) -join ',')" }
}
if (-not (Test-Path (Join-Path $dir 'done.json'))) { $head += ' - NOT FINISHED (no done.json)' }
if (-not $PassThru) { $head }
$summary = Join-Path $dir 'summary.json'
if ((Test-Path $summary) -and -not $PassThru) {
    # not @(... | ConvertFrom-Json): Windows PowerShell 5.1 passes the array on as one object
    $list = Get-Content $summary -Raw | ConvertFrom-Json
    foreach ($s in $list) {
        $line = '  {0,-22} {1,-7} passed {2}, failed {3}, skipped {4}' -f $s.Vm, $s.Result, $s.Passed, $s.Failed, $s.Skipped
        if ($s.PSObject.Properties['Flaky'] -and $s.Flaky) { $line += ", flaky $($s.Flaky)" }
        if ("$($s.TimedOut)") { $line += ", timed out: $($s.TimedOut)" }
        $line
    }
}

# the failed (and with -Skipped the skipped) cases from every pester XML; the same message for several cases of one
# file and VM (a BeforeAll that failed) is shown once with the count
$rows = foreach ($xml in @(Get-ChildItem $dir -Recurse -Filter 'pester-*.xml')) {
    # an XML that can't be read (cut off - run 20261022-061254, UI-RDP) is one row, not the end of the list
    $x = $null
    try { [xml]$x = Get-Content $xml.FullName -Raw }
    catch {
        [pscustomobject]@{ Result = 'FAIL'; Id = '(xml)'; Vm = $xml.Directory.Name; File = $xml.Name -replace '^pester-|\.xml$', ''
            Message = Short "pester XML unreadable: $($_.Exception.InnerException.Message)" $Width; Xml = $xml.FullName; Evidence = $xml.DirectoryName }
        continue
    }
    foreach ($c in @($x.SelectNodes('//test-case'))) {
        $result = $c.GetAttribute('result')
        if ($result -eq 'Success' -or ($result -ne 'Failure' -and -not $Skipped)) { continue }
        # name is "<FILE> <VM name>.<context>....<It name>" and the It's name may hold dots (run-time.json): the VM
        # from the Describe before the first dot, the It's name from description. A file whose setup failed leaves
        # "<VmName>" unexpanded - then the result folder names the VM's kind.
        $describe = ($c.GetAttribute('name') -split '\.')[0]
        $vm = if ($describe -match '^\S+ ([^<]+)$') { $Matches[1] -replace '^TestAutomation - ', '' } else { $xml.Directory.Name }
        $it = $c.GetAttribute('description')
        if (-not $it) { $it = ($c.GetAttribute('name') -split '\.')[-1] }
        $id = if ($it -match '\b([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})\b') { $Matches[1] } else { Short $it 40 }
        $msg = ''
        foreach ($n in 'failure', 'reason') {
            $node = $c.SelectSingleNode("$n/message")
            if ($node -and $node.InnerText) { $msg = $node.InnerText; break }
        }
        [pscustomobject]@{
            Result = if ($result -eq 'Failure') { 'FAIL' } else { 'SKIP' }
            Id = $id; Vm = $vm; File = $xml.Name -replace '^pester-|\.xml$', ''; Message = Short $msg $Width; Xml = $xml.FullName
            # a second lane's XML sits with its kind, its evidence in the lane's folder (<kind>-2)
            Evidence = if ($vm -match ' - (\d+)$' -and (Test-Path "$($xml.DirectoryName)-$($Matches[1])")) { "$($xml.DirectoryName)-$($Matches[1])" } else { $xml.DirectoryName }
        }
    }
}
$rows = @($rows)
if ($PassThru) { return $rows }
# failed the first time, passed the second: not failures of the run, but each one is a case to make reliable
$flakyFile = Join-Path $dir 'flaky.json'
$flaky = @(Read-LabFlaky -Path $flakyFile)
function Write-Flaky {
    if (-not $flaky.Count) { return }
    foreach ($f in $flaky) { 'FLAKY {0} [{1}, {2}]: passed the second time; first: {3}' -f $f.Id, $f.File, $f.Vm, (Short $f.Message $Width) }
}
if (-not $rows.Count) {
    if ($Skipped) { 'No failed or skipped cases.' } else { 'No failed cases.' }
    Write-Flaky
    return
}

# Cases a failed BeforeAll never ran only say so in the XML; the error itself is in the file's console log (UTF-16,
# with colour codes): its first exception line.
function Get-SetupError([string]$Folder, [string]$File) {
    $log = Join-Path $Folder "console-$File.log"
    if (-not (Test-Path $log)) { return $null }
    $lines = @(Get-Content $log -Encoding Unicode | ForEach-Object { $_ -replace "$([char]27)\[[0-9;]*m", '' })
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '\w+Exception: ') { continue }
        # the console wraps a long message: its following lines up to the stack ("at ...") or a blank line
        $text = $lines[$i]
        for ($j = $i + 1; $j -lt $lines.Count -and $j -le $i + 4; $j++) {
            if (-not $lines[$j].Trim() -or $lines[$j] -match '^\s*at ') { break }
            $text += ' ' + $lines[$j].Trim()
        }
        return $text
    }
    $null
}

foreach ($g in @($rows | Group-Object Result, File, Vm, Message)) {
    $r = $g.Group[0]
    $ids = if ($g.Count -gt 3) { "$($g.Group[0].Id) and $($g.Count - 1) more" } else { ($g.Group | ForEach-Object Id) -join ', ' }
    $msg = $r.Message
    if ($msg -like 'This test should run but it did not*') {
        $why = Get-SetupError (Split-Path $r.Xml) $r.File
        if ($why) { $msg = 'setup failed: ' + (Short $why $Width) }
    }
    '{0} {1} [{2}, {3}]: {4}' -f $r.Result, $ids, $r.File, $r.Vm, $msg
}
Write-Flaky
''
'Evidence:'
$rows | Select-Object -ExpandProperty Evidence -Unique | ForEach-Object { "  $_" }
