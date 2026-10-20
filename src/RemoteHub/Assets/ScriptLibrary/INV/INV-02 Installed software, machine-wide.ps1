<#
.SYNOPSIS
    Lists software installed for all users from the 64-bit and 32-bit uninstall registry views.
.DESCRIPTION
    Reports name, version, publisher, install date, scope and architecture per product, removes
    duplicates and hides system components and updates unless -IncludeSystemComponents is set.
    The WMI product class is never used because it triggers MSI consistency checks and repairs.
    Per-user installs and Store apps are not included (see INV-03). DisplayVersion can be blank or
    free text, so the optional -Name/-MinimumVersion check tolerates parse failures.
.NOTES
    RemoteHub: id=INV-02; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeSystemComponents,
    [string]$Name = '',
    [string]$MinimumVersion = '',
    [ValidateSet('Table', 'Json', 'Csv')]
    [string]$OutputFormat = 'Table'
)

$ErrorActionPreference = 'Stop'

function ConvertTo-VersionOrNull([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $m = [regex]::Match($Text, '\d+(\.\d+){0,3}')
    if (-not $m.Success) { return $null }
    $v = $null
    $s = $m.Value
    if ($s -notmatch '\.') { $s = "$s.0" }
    if ([version]::TryParse($s, [ref]$v)) { return $v }
    return $null
}

try {
    # Opening both views explicitly avoids the Wow6432Node redirection of a 32-bit host.
    $views = @(
        @{ View = [Microsoft.Win32.RegistryView]::Registry64; Arch = 'x64' },
        @{ View = [Microsoft.Win32.RegistryView]::Registry32; Arch = 'x86' }
    )
    if (-not [Environment]::Is64BitOperatingSystem) { $views = @(@{ View = [Microsoft.Win32.RegistryView]::Default; Arch = 'x86' }) }

    $items = New-Object System.Collections.Generic.List[object]
    foreach ($v in $views) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $v.View)
        try {
            $root = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
            if ($null -eq $root) { continue }
            foreach ($sub in $root.GetSubKeyNames()) {
                $k = $root.OpenSubKey($sub)
                if ($null -eq $k) { continue }
                try {
                    $display = [string]$k.GetValue('DisplayName')
                    if ([string]::IsNullOrWhiteSpace($display)) { continue }
                    $isSystem = ($k.GetValue('SystemComponent') -eq 1) -or [bool]$k.GetValue('ParentKeyName') -or
                                ([string]$k.GetValue('ReleaseType') -match 'Update|Hotfix')
                    if ($isSystem -and -not $IncludeSystemComponents) { continue }
                    $installDate = [string]$k.GetValue('InstallDate')
                    if ($installDate -match '^\d{8}$') {
                        $installDate = $installDate.Substring(0, 4) + '-' + $installDate.Substring(4, 2) + '-' + $installDate.Substring(6, 2)
                    } elseif (-not $installDate) { $installDate = 'Data unavailable' }
                    $items.Add([PSCustomObject]@{
                        Name         = $display.Trim()
                        Version      = [string]$k.GetValue('DisplayVersion')
                        Publisher    = [string]$k.GetValue('Publisher')
                        InstallDate  = $installDate
                        Scope        = 'Machine'
                        Architecture = $v.Arch
                    })
                } finally { $k.Close() }
            }
            $root.Close()
        } finally { $base.Close() }
    }

    $unique = $items | Sort-Object Name, Version, Architecture -Unique | Sort-Object Name

    if ($Name) {
        # Sensor mode: one Boolean answering "application X at or above version Y is present".
        $found = @($unique | Where-Object { $_.Name -like $Name })
        $present = $false
        foreach ($m in $found) {
            if (-not $MinimumVersion) { $present = $true; break }
            $have = ConvertTo-VersionOrNull $m.Version
            $want = ConvertTo-VersionOrNull $MinimumVersion
            if ($null -ne $have -and $null -ne $want -and $have -ge $want) { $present = $true; break }
            if ($null -eq $have) { Write-Verbose "Version '$($m.Version)' of '$($m.Name)' could not be parsed." }
        }
        $present
        if ($present) { exit 0 } else { exit 1 }
    }

    switch ($OutputFormat) {
        'Json'  { ConvertTo-Json -InputObject @($unique) -Depth 5 }
        'Csv'   { $unique | ConvertTo-Csv -NoTypeInformation }
        default { $unique | Format-Table -AutoSize | Out-String -Width 4096 }
    }
    Write-Verbose "$(@($unique).Count) products listed."
    exit 0
} catch {
    Write-Error "Software inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
