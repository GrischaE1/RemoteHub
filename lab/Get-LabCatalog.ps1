#Requires -Version 5.1
<#
.SYNOPSIS
    The lab's test cases as they are in lab\tests\ now: one object per test file with Name, File, About (its first
    comment line), Role (LabRole), Groups (the bare tags of its Describe blocks - the first is its group) and Cases
    (Id and Title of each It that carries a case ID tag).
.DESCRIPTION
    Read from the files each time, so a new file, case or group needs no change here. Used by LabConsole.ps1 (the
    test tree) and Update-LabCaseStatus.ps1 (which cases have a lab test).
#>
[CmdletBinding()]
param([string]$TestsDir = (Join-Path $PSScriptRoot 'tests'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($f in @(Get-ChildItem $TestsDir -Filter '*.Tests.ps1' | Sort-Object Name)) {
    $lines = [IO.File]::ReadAllLines($f.FullName)
    $about = if ($lines.Count -and $lines[0] -match '^#\s*(.+)$') { $Matches[1] } else { '' }
    $role = ''
    $groups = New-Object System.Collections.Generic.List[string]
    $cases = New-Object System.Collections.Generic.List[object]
    foreach ($l in $lines) {
        if ($l -match '^#\s*LabRole:\s*(\S+)') { $role = $Matches[1]; continue }
        if ($l -match "^\s*Describe\s+(['""]).*?\1\s+-Tag\s+([^{]+)") {
            foreach ($t in ($Matches[2] -split ',')) {
                $t = $t.Trim()
                if ($t -match '^[A-Za-z]+$' -and -not $groups.Contains($t)) { $groups.Add($t) }
            }
            continue
        }
        $id = if ($l -match "-Tag\s+'([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})'") { $Matches[1] } else { $null }
        if (-not $id) { continue }
        if ($l -match "^\s*It\s+'((?:[^']|'')*)'") { $title = $Matches[1] -replace "''", "'" }
        elseif ($l -match '^\s*It\s+"([^"]*)"') { $title = $Matches[1] }
        else { continue }
        $cases.Add([pscustomobject]@{ Id = $id; Title = $title })
    }
    # arrays, not the lists: Windows PowerShell 5.1 fails on a List[string] indexed inside the literal below
    $groupList = [string[]]$groups.ToArray()
    $main = if ($groupList.Count) { $groupList[0] } else { 'Other' }
    [pscustomobject]@{
        Name = $f.Name -replace '\.Tests\.ps1$', ''; File = $f.Name; About = $about; Role = $role
        Groups = $groupList; Group = $main; Cases = [object[]]$cases.ToArray()
    }
}
