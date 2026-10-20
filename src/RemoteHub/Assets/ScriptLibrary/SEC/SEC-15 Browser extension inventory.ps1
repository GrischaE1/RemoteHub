<#
.SYNOPSIS
    Lists the Edge and Chrome extensions in the signed-in user's profiles and checks them against allow and block lists.
.DESCRIPTION
    Reads only extension manifests and the browser preferences files: extension ID, name, version,
    requested permissions, install source and enabled state. Never reads cookies, history, saved logins
    or the login data store. Names that are localization tokens are resolved from the extension's
    messages file. Profile folder names are reported, never the profile display names (they can hold
    email addresses). Also reports machine policy forced-install and block lists. Firefox is not covered.
.NOTES
    RemoteHub: id=SEC-15; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [string[]]$AllowedId = @(),
    [string[]]$BlockedId = @(),
    [switch]$IncludeComponents
)

$ErrorActionPreference = 'Stop'

$browsers = [ordered]@{
    Edge = @{ Data = "$env:LOCALAPPDATA\Microsoft\Edge\User Data"; Policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge' }
    Chrome = @{ Data = "$env:LOCALAPPDATA\Google\Chrome\User Data"; Policy = 'HKLM:\SOFTWARE\Policies\Google\Chrome' }
}
# Chromium "location" values; 5 and 10 are built-in components.
$sources = @{ 1 = 'Store'; 2 = 'External preferences'; 3 = 'External registry'; 4 = 'Unpacked (developer)'; 5 = 'Component'
    6 = 'External preferences download'; 7 = 'Policy download'; 8 = 'Command line'; 9 = 'Policy'; 10 = 'External component' }

function Read-Json([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { Write-Verbose "Could not parse $(Split-Path -Leaf $path): $($_.Exception.Message)"; return $null }
}

function Resolve-Name($manifest, [string]$dir) {
    $name = [string]$manifest.name
    if ($name -notmatch '^__MSG_(.+)__$') { return $name }
    $key = $Matches[1]
    foreach ($loc in @($manifest.default_locale, 'en', 'en_US') | Where-Object { $_ }) {
        $msgs = Read-Json (Join-Path $dir "_locales\$loc\messages.json")
        if ($msgs) {
            $prop = $msgs.PSObject.Properties | Where-Object { $_.Name -ieq $key } | Select-Object -First 1
            if ($prop) { return [string]$prop.Value.message }
        }
    }
    return $name
}

try {
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($b in $browsers.Keys) {
        $root = $browsers[$b].Data
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $profiles = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile *' })
        foreach ($p in $profiles) {
            # Recent builds keep extension settings in Secure Preferences; older ones in Preferences.
            $settings = @{}
            foreach ($file in 'Preferences', 'Secure Preferences') {
                $json = Read-Json (Join-Path $p.FullName $file)
                if ($json -and $json.extensions -and $json.extensions.settings) {
                    foreach ($prop in $json.extensions.settings.PSObject.Properties) { $settings[$prop.Name] = $prop.Value }
                }
            }
            $extRoot = Join-Path $p.FullName 'Extensions'
            $ids = @($settings.Keys)
            if (Test-Path -LiteralPath $extRoot) { $ids += @(Get-ChildItem -LiteralPath $extRoot -Directory | ForEach-Object { $_.Name }) }
            foreach ($id in ($ids | Sort-Object -Unique)) {
                $s = $settings[$id]
                $location = $null; if ($s -and $s.PSObject.Properties['location']) { $location = [int]$s.location }
                if (($location -eq 5 -or $location -eq 10) -and -not $IncludeComponents) { continue }
                $verDir = $null
                if (Test-Path -LiteralPath (Join-Path $extRoot $id)) {
                    $verDir = Get-ChildItem -LiteralPath (Join-Path $extRoot $id) -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                }
                $manifest = $null
                if ($verDir) { $manifest = Read-Json (Join-Path $verDir.FullName 'manifest.json') }
                if (-not $manifest -and $s -and $s.PSObject.Properties['manifest']) { $manifest = $s.manifest }
                if (-not $manifest) { continue }
                $name = Resolve-Name $manifest $(if ($verDir) { $verDir.FullName } else { '' })
                $perms = @($manifest.permissions) + @($manifest.host_permissions) | Where-Object { $_ -is [string] }
                $enabled = 'Data unavailable'
                if ($s -and $s.PSObject.Properties['state']) { $enabled = ([int]$s.state -eq 1) }
                elseif ($s -and $s.PSObject.Properties['disable_reasons']) { $enabled = (@($s.disable_reasons).Count -eq 0) }
                $finding = 'OK'
                if ($BlockedId -contains $id) { $finding = 'Blocked' }
                elseif ($AllowedId.Count -gt 0 -and $AllowedId -notcontains $id) { $finding = 'Not in allow list' }
                elseif ($location -eq 4) { $finding = 'Unpacked developer extension' }
                $src = 'Data unavailable'; if ($null -ne $location -and $sources.ContainsKey($location)) { $src = $sources[$location] }
                $rows.Add([PSCustomObject]@{
                    Browser = $b; Profile = $p.Name; Id = $id; Name = $name; Version = [string]$manifest.version
                    Enabled = $enabled; Source = $src; Permissions = (($perms | Sort-Object -Unique) -join ' '); Finding = $finding
                })
            }
        }
    }

    if ($rows.Count -eq 0) { 'Browser,Profile,Id,Name,Version,Enabled,Source,Permissions,Finding' }
    else { $rows | Sort-Object Browser, Profile, Name | ConvertTo-Csv -NoTypeInformation }

    Write-Output ''
    foreach ($b in $browsers.Keys) {
        $pol = $browsers[$b].Policy
        $force = 0; $block = 0
        $k = Get-Item -LiteralPath "$pol\ExtensionInstallForcelist" -ErrorAction SilentlyContinue
        if ($k) { $force = $k.ValueCount }
        $k = Get-Item -LiteralPath "$pol\ExtensionInstallBlocklist" -ErrorAction SilentlyContinue
        if ($k) { $block = $k.ValueCount }
        $present = Test-Path -LiteralPath $pol
        Write-Output ("{0} machine policy: {1}; forced installs: {2}; block list entries: {3}" -f $b, $(if ($present) { 'present' } else { 'none' }), $force, $block)
    }
    Write-Output 'Firefox: not covered by this script.'

    if (@($rows | Where-Object { $_.Finding -eq 'Blocked' }).Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "Browser extension inventory failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
