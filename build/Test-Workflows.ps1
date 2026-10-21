# The project's rules for GitHub Actions workflows (.github/copilot-instructions.md, docs/security-test-lab-design.md
# section 8), checked in CI next to actionlint; exit 1 with a line per finding:
# - SEC-CI-02: no ${{ github.* }} or ${{ inputs.* }} written into a run: script - a tag, branch name or input is text
#   someone chose and would run as code; pass it through env: instead.
# - Every action pinned to a full commit SHA, with its version in a comment (uses: owner/repo@<40 hex> # v1.2.3).
# - SEC-CI-03: a pull_request_target workflow never checks out code (it runs with write rights on main's definition).
#   build\Test-Workflows.ps1 [-Path <folder with .yml files>]
param([string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.github\workflows'))
$ErrorActionPreference = 'Stop'

$findings = @()
foreach ($file in @(Get-ChildItem $Path -File | Where-Object { $_.Extension -in '.yml', '.yaml' })) {
    $lines = @(Get-Content $file.FullName)
    $name = $file.Name
    $runIndent = -1   # inside a run: block when >= 0: the indentation of the "run:" key
    $prTarget = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $n = $i + 1
        if ($line -match '^\s*#') { continue }
        $indent = $line.Length - $line.TrimStart().Length
        if ($runIndent -ge 0) {
            if ($line.Trim().Length -gt 0 -and $indent -le $runIndent) { $runIndent = -1 }
            elseif ($line -match '\$\{\{\s*(github|inputs)\.') { $findings += "${name}:${n}: `${{ $($Matches[1]).* }} in a run: script - pass it through env:" }
        }
        if ($line -match '^(\s*)(-\s+)?run:\s*(.*)$') {
            # the key's own indentation (after "- " in a step written as "- run:"); taken before the next -match replaces $Matches
            $keyIndent = $Matches[1].Length + "$($Matches[2])".Length
            $rest = $Matches[3]
            if ($rest -match '^[|>]') { $runIndent = $keyIndent }
            elseif ($rest -match '\$\{\{\s*(github|inputs)\.') { $findings += "${name}:${n}: `${{ $($Matches[1]).* }} in a run: script - pass it through env:" }
        }
        if ($line -match '^\s*(-\s+)?uses:\s*([^\s#]+)\s*(#.*)?$') {
            $uses = $Matches[2]; $comment = $Matches[3]
            if ($uses -notmatch '^\./' -and $uses -notmatch '^docker://') {
                if ($uses -notmatch '@[0-9a-f]{40}$') { $findings += "${name}:${n}: $uses is not pinned to a full commit SHA" }
                elseif (-not $comment -or $comment -notmatch 'v?\d+(\.\d+)*') { $findings += "${name}:${n}: $uses has no version comment (# vX.Y.Z)" }
            }
        }
        # "pull_request_target:" as a key, or "on: pull_request_target" / "on: [push, pull_request_target]"
        if ($line -match '^\s*pull_request_target\s*:' -or $line -match '^on:.*\bpull_request_target\b') { $prTarget = $true }
    }
    if ($prTarget -and ($lines -match 'uses:\s*actions/checkout@')) { $findings += "${name}: pull_request_target with actions/checkout - such a workflow must never check out code" }
}
if ($findings) {
    $findings | ForEach-Object { "::error::$_"; Write-Host $_ }
    exit 1
}
"Workflows: no github.* or inputs.* in scripts, every action pinned to a commit SHA, no checkout under pull_request_target."
