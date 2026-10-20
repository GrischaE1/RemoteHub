<#
.SYNOPSIS
    Checks this device against a JSON security baseline and reports pass or fail per control.
.DESCRIPTION
    Supported control types: Registry, SecurityPolicy, AuditPolicy (by subcategory GUID), UserRight,
    Service, OptionalFeature and FirewallProfile. Reports expected and actual value, a remediation note
    and a compliance percentage. The baseline file comes from your chosen authority; none is embedded.
    Settings delivered by MDM may live only under the PolicyManager keys: such controls are reported as
    "Data unavailable", not as failed. The secedit and auditpol exports are temporary and deleted.
.NOTES
    RemoteHub: id=SEC-01; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string]$BaselinePath = "$env:ProgramData\RemoteHub\Baselines\SEC-01-baseline.json",
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'
$unavailable = 'Data unavailable'

# Baseline format: { "controls": [ { "id", "type", "expected", "remediation", ...type fields } ]
#   Registry:        path (HKLM:\...), name, optional mdmPath (PolicyManager key that may hold the value)
#   SecurityPolicy:  section ("System Access"), key
#   AuditPolicy:     guid; expected is the numeric setting (0 none, 1 success, 2 failure, 3 both)
#   UserRight:       right (SeDenyNetworkLogonRight); expected is an array of SIDs (*S-1-5-32-546)
#   Service:         name; expected is a start type (Disabled, Manual, Automatic)
#   OptionalFeature: name; expected Enabled or Disabled
#   FirewallProfile: name (Domain, Private, Public); expected true or false

function Read-SeceditExport {
    $tmp = Join-Path $env:TEMP ("sec01-" + [guid]::NewGuid().ToString('N') + '.inf')
    try {
        $null = & secedit.exe /export /cfg $tmp /areas SECURITYPOLICY USER_RIGHTS /quiet
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmp)) { return $null }
        $data = @{}; $section = ''
        foreach ($line in (Get-Content -LiteralPath $tmp)) {
            if ($line -match '^\[(.+)\]$') { $section = $Matches[1]; $data[$section] = @{}; continue }
            if ($section -and $line -match '^\s*([^=]+?)\s*=\s*(.*)$') { $data[$section][$Matches[1]] = $Matches[2].Trim() }
        }
        return $data
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Read-AuditPolicy {
    # The backup CSV carries a numeric setting value, so nothing localized is parsed.
    $tmp = Join-Path $env:TEMP ("sec01-" + [guid]::NewGuid().ToString('N') + '.csv')
    try {
        $null = & auditpol.exe /backup /file:$tmp
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmp)) { return $null }
        $map = @{}
        $rows = Get-Content -LiteralPath $tmp | Select-Object -Skip 1 |
            ConvertFrom-Csv -Header 'Machine','Target','Subcategory','Guid','Inclusion','Exclusion','Value'
        foreach ($r in $rows) { if ($r.Guid) { $map[$r.Guid.ToUpperInvariant()] = $r.Value } }
        return $map
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Get-RegistryActual($c) {
    try { return (Get-ItemProperty -LiteralPath $c.path -Name $c.name -ErrorAction Stop).($c.name) } catch { }
    if ($c.mdmPath -and (Test-Path -LiteralPath $c.mdmPath)) { return $unavailable }
    return $null
}

try {
    if (-not (Test-Path -LiteralPath $BaselinePath)) {
        if ($Sensor) { $unavailable } else { [PSCustomObject]@{ Status = $unavailable; Reason = "Baseline file not found: $BaselinePath" } }
        exit 1
    }
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    $types = @($baseline.controls | ForEach-Object { $_.type })
    $secedit = $null; $audit = $null
    if ($types -contains 'SecurityPolicy' -or $types -contains 'UserRight') { $secedit = Read-SeceditExport }
    if ($types -contains 'AuditPolicy') { $audit = Read-AuditPolicy }

    $results = foreach ($c in $baseline.controls) {
        $actual = $null; $pass = $false
        try {
            switch ($c.type) {
                'Registry' {
                    $actual = Get-RegistryActual $c
                    if ($null -ne $actual -and $actual -ne $unavailable) { $pass = ([string]$actual -eq [string]$c.expected) }
                }
                'SecurityPolicy' {
                    if ($null -eq $secedit) { $actual = $unavailable }
                    elseif ($secedit.ContainsKey($c.section)) { $actual = $secedit[$c.section][$c.key] }
                    if ($null -ne $actual -and $actual -ne $unavailable) { $pass = ([string]$actual -eq [string]$c.expected) }
                }
                'AuditPolicy' {
                    if ($null -eq $audit) { $actual = $unavailable }
                    else {
                        $g = ([string]$c.guid).Trim('{}').ToUpperInvariant()
                        $actual = $audit['{' + $g + '}']
                        if ($null -ne $actual) { $pass = ([int]$actual -eq [int]$c.expected) }
                    }
                }
                'UserRight' {
                    if ($null -eq $secedit) { $actual = $unavailable }
                    else {
                        $raw = $secedit['Privilege Rights'][$c.right]
                        $have = @(); if ($raw) { $have = @($raw -split ',' | ForEach-Object { $_.Trim() } | Sort-Object) }
                        $want = @($c.expected | ForEach-Object { [string]$_ } | Sort-Object)
                        $actual = $have -join ','
                        $pass = ($actual -eq ($want -join ','))
                    }
                }
                'Service' {
                    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($c.name)'"
                    if ($svc) { $actual = $svc.StartMode } else { $actual = 'NotInstalled' }
                    $exp = [string]$c.expected; if ($exp -eq 'Automatic') { $exp = 'Auto' }
                    $pass = ($actual -eq $exp) -or ($actual -eq 'NotInstalled' -and $exp -eq 'Disabled')
                }
                'OptionalFeature' {
                    $f = Get-WindowsOptionalFeature -Online -FeatureName $c.name -ErrorAction SilentlyContinue
                    if ($f) { $actual = [string]$f.State } else { $actual = 'NotPresent' }
                    $pass = ($actual -eq [string]$c.expected) -or ($actual -eq 'NotPresent' -and [string]$c.expected -eq 'Disabled')
                }
                'FirewallProfile' {
                    $actual = [string](Get-NetFirewallProfile -Name $c.name).Enabled
                    $pass = ($actual -eq [string]$c.expected)
                }
                default { $actual = $unavailable; Write-Verbose "Unknown control type '$($c.type)' in control $($c.id)" }
            }
        } catch {
            Write-Verbose "Control $($c.id) could not be evaluated: $($_.Exception.Message)"
            $actual = $unavailable
        }
        if ($null -eq $actual) { $actual = 'Not set' }
        $status = 'Fail'
        if ($actual -eq $unavailable) { $status = $unavailable } elseif ($pass) { $status = 'Pass' }
        [PSCustomObject]@{
            Id = $c.id; Type = $c.type; Status = $status
            Expected = ($c.expected -join ','); Actual = [string]$actual
            Remediation = $(if ($status -eq 'Fail') { $c.remediation } else { '' })
        }
    }
    $results = @($results)
    $evaluated = @($results | Where-Object { $_.Status -ne $unavailable })
    $passed = @($evaluated | Where-Object { $_.Status -eq 'Pass' }).Count
    $percent = 0; if ($evaluated.Count -gt 0) { $percent = [math]::Round(100 * $passed / $evaluated.Count, 1) }
    $compliant = ($evaluated.Count -gt 0 -and $passed -eq $evaluated.Count)

    if ($Sensor) { $percent }
    else {
        [PSCustomObject]@{
            Baseline = Split-Path -Leaf $BaselinePath
            Controls = $results.Count; Passed = $passed
            Failed = @($evaluated | Where-Object { $_.Status -eq 'Fail' }).Count
            Unavailable = $results.Count - $evaluated.Count
            CompliancePercent = $percent; Compliant = $compliant
            Results = $results
        } | ConvertTo-Json -Depth 5
    }
    if ($compliant) { exit 0 }
    exit 1
} catch {
    Write-Error "Baseline drift check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
