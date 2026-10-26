#Requires -Version 5.1
<#
.SYNOPSIS
    The lab's unreliable cases over the last runs: per case how often it failed (in its file's last attempt), how often
    it was flaky (failed, then passed on the file's second attempt), in how many runs it ran, and the last time with its
    message. The list to work through so a run is green without second attempts - the most frequent first.
.PARAMETER Root
    The folder holding the runs. Default: artifacts\lab of this checkout, where Start-LabRun.ps1 copies them back.
    D:\RemoteHubLab\results works too (the lab's own copy).
.PARAMETER Runs
    How many of the newest finished runs are read.
.PARAMETER Top
    How many cases are listed.
.PARAMETER Width
    How many characters of each last message are shown.
.PARAMETER PassThru
    Return the rows (Id, Ran, Failed, Flaky, Last, LastResult, Message) instead of the text.
.EXAMPLE
    .\lab\Get-LabFlaky.ps1
    .\lab\Get-LabFlaky.ps1 -Root D:\RemoteHubLab\results -Runs 10 -Top 50
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path (Split-Path $PSScriptRoot) 'artifacts\lab'),
    [int]$Runs = 30,
    [int]$Top = 40,
    [int]$Width = 160,
    [switch]$PassThru
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LabRetry.ps1')

if (-not (Test-Path $Root)) { throw "No lab results in $Root - pass -Root (e.g. D:\RemoteHubLab\results)." }
$rows = @(Get-LabFlakyHistory -Root $Root -Runs $Runs)
if ($PassThru) { return $rows }
if (-not $rows.Count) { "No failed or flaky case in the last $Runs runs under $Root."; return }
"Cases that failed or were flaky in the last $Runs runs under $Root ($($rows.Count) cases; failed = in the last attempt, flaky = passed on the second):"
'  {0,-14} {1,4} {2,7} {3,6}  {4,-16} {5}' -f 'Case', 'Ran', 'Failed', 'Flaky', 'Last', 'Last message'
foreach ($r in @($rows | Select-Object -First $Top)) {
    $msg = ($r.Message -replace '\s+', ' ').Trim()
    if ($msg.Length -gt $Width) { $msg = $msg.Substring(0, $Width) + ' ...' }
    '  {0,-14} {1,4} {2,7} {3,6}  {4,-16} {5}' -f $r.Id, $r.Ran, $r.Failed, $r.Flaky, "$($r.Last) $($r.LastResult)", $msg
}
if ($rows.Count -gt $Top) { "  ... and $($rows.Count - $Top) more (-Top)" }
