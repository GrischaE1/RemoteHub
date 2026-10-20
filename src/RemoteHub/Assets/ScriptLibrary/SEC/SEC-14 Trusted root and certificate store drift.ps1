<#
.SYNOPSIS
    Compares the trusted root, intermediate, third-party root, trusted publisher and disallowed stores against a thumbprint baseline.
.DESCRIPTION
    Flags certificates that are not in the approved baseline (interception or test roots, for example)
    and approved Disallowed entries that are missing. Windows updates the third-party root store
    (AuthRoot) automatically; with -AcceptAuthRoot those additions are reported as vendor-managed instead
    of unknown. Enterprise roots from MDM or Group Policy land in the same stores, so add them to the
    baseline. Run with -StoreLocation CurrentUser in the user context to cover CurrentUser\Root.
.NOTES
    RemoteHub: id=SEC-14; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [string]$BaselinePath = "$env:ProgramData\RemoteHub\Baselines\SEC-14-thumbprints.json",
    [ValidateSet('LocalMachine','CurrentUser')]
    [string]$StoreLocation = 'LocalMachine',
    [bool]$AcceptAuthRoot = $true
)

$ErrorActionPreference = 'Stop'

# Baseline format: { "Root": [thumbprints], "CA": [...], "AuthRoot": [...], "TrustedPublisher": [...], "Disallowed": [...] }
# Every thumbprint in "Disallowed" must be present in the Disallowed store.
$stores = 'Root', 'CA', 'AuthRoot', 'TrustedPublisher', 'Disallowed'

try {
    if (-not (Test-Path -LiteralPath $BaselinePath)) {
        'Store,Subject,Thumbprint,Reason'
        "All,Data unavailable,,Baseline file not found: $BaselinePath"
        exit 1
    }
    $baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json

    $findings = New-Object System.Collections.Generic.List[object]
    $checked = 0
    foreach ($store in $stores) {
        $approved = @()
        if ($baseline.PSObject.Properties[$store]) { $approved = @($baseline.$store | ForEach-Object { ([string]$_).Replace(' ', '').ToUpperInvariant() }) }
        $path = "Cert:\$StoreLocation\$store"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $certs = @(Get-ChildItem -LiteralPath $path)
        $checked += $certs.Count
        $present = @($certs | ForEach-Object { $_.Thumbprint.ToUpperInvariant() })

        if ($store -eq 'Disallowed') {
            foreach ($t in $approved) {
                if ($present -notcontains $t) {
                    $findings.Add([PSCustomObject]@{ Store = "$StoreLocation\$store"; Subject = ''; Thumbprint = $t; Reason = 'Required Disallowed entry is missing' })
                }
            }
            continue
        }
        foreach ($c in $certs) {
            if ($approved -contains $c.Thumbprint.ToUpperInvariant()) { continue }
            $reason = 'Not in baseline'
            if ($store -eq 'AuthRoot' -and $AcceptAuthRoot) { $reason = 'Vendor-managed (Windows root update); not in baseline' }
            elseif ($c.Subject -eq $c.Issuer -and $store -eq 'CA') { $reason = 'Self-signed certificate in intermediate store' }
            elseif ($c.Subject -match '(?i)test|fiddler|charles|mitm|burp|proxy|inspection|decrypt') { $reason = 'Not in baseline; looks like a test or interception root' }
            if ($c.NotAfter -lt (Get-Date)) { $reason = "$reason; expired" }
            $findings.Add([PSCustomObject]@{ Store = "$StoreLocation\$store"; Subject = $c.Subject; Thumbprint = $c.Thumbprint; Reason = $reason })
        }
    }

    $actionable = @($findings | Where-Object { $_.Reason -notlike 'Vendor-managed*' })
    if ($findings.Count -eq 0) { 'Store,Subject,Thumbprint,Reason' }
    else { $findings | Sort-Object Store, Subject | ConvertTo-Csv -NoTypeInformation }
    Write-Output ''
    Write-Output ("Certificates checked: {0}; findings: {1}; vendor-managed: {2}" -f $checked, $actionable.Count, ($findings.Count - $actionable.Count))
    if ($actionable.Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Certificate store drift check failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
