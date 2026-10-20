<#
.SYNOPSIS
    Lists what the signed-in user has installed that machine-wide inventory cannot see.
.DESCRIPTION
    Reports per-user uninstall entries (HKCU, both views), Store and MSIX apps for the current user,
    and folders under the user's local Programs folder, each with a Source column.
    Run in the user's context: from SYSTEM these items are invisible because HKCU is the SYSTEM
    account's own hive. The user name is redacted from any paths in the report.
.NOTES
    RemoteHub: id=INV-03; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeFrameworks,
    [switch]$SkipAppx,
    [ValidateSet('Table', 'Json', 'Csv')]
    [string]$OutputFormat = 'Table'
)

$ErrorActionPreference = 'Stop'

function Hide-UserPath([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    if ($env:USERPROFILE) { $out = $out.Replace($env:USERPROFILE, '%USERPROFILE%') }
    if ($env:USERNAME) { $out = [regex]::Replace($out, [regex]::Escape($env:USERNAME), '[REDACTED]', 'IgnoreCase') }
    return $out
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.IsSystem) {
        'Data unavailable: this script must run in the signed-in user''s context, not as SYSTEM.'
        exit 1
    }

    $items = New-Object System.Collections.Generic.List[object]

    $views = @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)
    foreach ($view in $views) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $root = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall')
            if ($null -eq $root) { continue }
            foreach ($sub in $root.GetSubKeyNames()) {
                $k = $root.OpenSubKey($sub)
                if ($null -eq $k) { continue }
                try {
                    $name = [string]$k.GetValue('DisplayName')
                    if ([string]::IsNullOrWhiteSpace($name) -or $k.GetValue('SystemComponent') -eq 1) { continue }
                    $items.Add([PSCustomObject]@{
                        Source    = 'Registry'
                        Name      = $name.Trim()
                        Version   = [string]$k.GetValue('DisplayVersion')
                        Publisher = [string]$k.GetValue('Publisher')
                        Location  = Hide-UserPath ([string]$k.GetValue('InstallLocation'))
                    })
                } finally { $k.Close() }
            }
            $root.Close()
        } finally { $base.Close() }
    }

    if (-not $SkipAppx) {
        if (Get-Command -Name Get-AppxPackage -ErrorAction SilentlyContinue) {
            try {
                foreach ($p in Get-AppxPackage) {
                    if ($p.IsFramework -and -not $IncludeFrameworks) { continue }
                    $items.Add([PSCustomObject]@{
                        Source    = 'Appx'
                        Name      = $p.Name
                        Version   = [string]$p.Version
                        Publisher = $p.Publisher
                        Location  = ''
                    })
                }
            } catch {
                # Appx can be unavailable in some sessions (for example a non-interactive logon).
                Write-Verbose "Get-AppxPackage failed: $($_.Exception.Message)"
                $items.Add([PSCustomObject]@{ Source = 'Appx'; Name = 'Data unavailable'; Version = ''; Publisher = ''; Location = '' })
            }
        }
    }

    $programs = Join-Path $env:LOCALAPPDATA 'Programs'
    if (Test-Path -LiteralPath $programs) {
        foreach ($d in Get-ChildItem -LiteralPath $programs -Directory -Force -ErrorAction SilentlyContinue) {
            $items.Add([PSCustomObject]@{
                Source    = 'Folder'
                Name      = $d.Name
                Version   = ''
                Publisher = ''
                Location  = Hide-UserPath $d.FullName
            })
        }
    }

    $result = $items | Sort-Object Source, Name, Version -Unique
    switch ($OutputFormat) {
        'Json'  { ConvertTo-Json -InputObject @($result) -Depth 5 }
        'Csv'   { $result | ConvertTo-Csv -NoTypeInformation }
        default { $result | Format-Table -AutoSize | Out-String -Width 4096 }
    }
    exit 0
} catch {
    Write-Error "Per-user inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
