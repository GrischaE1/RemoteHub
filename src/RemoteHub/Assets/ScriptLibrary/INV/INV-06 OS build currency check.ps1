<#
.SYNOPSIS
    Compares this device's OS build and UBR with an administrator-maintained minimum build table.
.DESCRIPTION
    Reads DisplayVersion, CurrentBuild and UBR from the registry and looks up the minimum UBR for
    the build in a JSON table supplied by -PolicyJson or the file at -PolicyPath. Reports compliant,
    non-compliant with the UBR gap, or "Data unavailable" when the build is not in the table.
    No lifecycle dates or builds are hard-coded; keep the table under change control, with its own
    rows for LTSC and Insider builds. Table format: [{"Build":26100,"MinimumUbr":4061,"Label":"24H2"}]
.NOTES
    RemoteHub: id=INV-06; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string]$PolicyPath = (Join-Path $env:ProgramData 'RemoteHub\Policy\os-build-minimums.json'),
    [string]$PolicyJson = ''
)

$ErrorActionPreference = 'Stop'

try {
    $cv = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $build = 0
    [void][int]::TryParse([string]$cv.CurrentBuild, [ref]$build)
    $ubr = -1
    if ($null -ne $cv.UBR) { $ubr = [int]$cv.UBR }
    $displayVersion = [string]$cv.DisplayVersion
    if (-not $displayVersion) { $displayVersion = [string]$cv.ReleaseId }
    $edition = [string]$cv.EditionID

    $raw = $PolicyJson
    if (-not $raw) {
        if (-not (Test-Path -LiteralPath $PolicyPath)) {
            [PSCustomObject]@{
                Status  = 'Data unavailable'
                Detail  = 'No minimum build table: pass -PolicyJson or create the policy file.'
                Build   = $build
                Ubr     = $ubr
            }
            exit 1
        }
        $raw = Get-Content -LiteralPath $PolicyPath -Raw -Encoding UTF8
    }
    $table = @($raw | ConvertFrom-Json)

    # An edition-specific row (for example LTSC) wins over a generic row for the same build.
    $rows = @($table | Where-Object { [int]$_.Build -eq $build })
    $row = $rows | Where-Object { $_.Edition -and $_.Edition -eq $edition } | Select-Object -First 1
    if (-not $row) { $row = $rows | Where-Object { -not $_.Edition } | Select-Object -First 1 }

    if (-not $row -or $build -eq 0 -or $ubr -lt 0) {
        [PSCustomObject]@{
            Status         = 'Data unavailable'
            Detail         = 'This OS build is not in the minimum build table.'
            DisplayVersion = $displayVersion
            Build          = $build
            Ubr            = $ubr
            Edition        = $edition
        }
        exit 1
    }

    $required = [int]$row.MinimumUbr
    $gap = $required - $ubr
    if ($gap -lt 0) { $gap = 0 }
    $compliant = ($ubr -ge $required)

    [PSCustomObject]@{
        Status         = if ($compliant) { 'Compliant' } else { 'Non-compliant' }
        Compliant      = $compliant
        DisplayVersion = $displayVersion
        Build          = $build
        Ubr            = $ubr
        RequiredUbr    = $required
        UbrGap         = $gap
        Edition        = $edition
        TableRow       = [string]$row.Label
    }
    if ($compliant) { exit 0 }
    exit 1
} catch {
    Write-Error "Build currency check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
