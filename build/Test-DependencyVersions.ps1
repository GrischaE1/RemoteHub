# Fails (exit 1) when a project references a NuGet package below the version minimum-versions.json names for it - a
# version with a known security fix. Reads every PackageReference (Version attribute or element) in the .csproj,
# .vcxproj and .wixproj files under -Root, and Directory.Packages.props if there is one.
#   build\Test-DependencyVersions.ps1 [-Root <repository>] [-Minimums <file>]
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$Minimums = (Join-Path $PSScriptRoot 'minimum-versions.json')
)
$ErrorActionPreference = 'Stop'

# 55.1.0, 1.5.8.1, 2026.0.0 - and a pre-release suffix, which sorts below its release
function ConvertTo-Version([string]$Text) {
    $core = ($Text -split '[-+]')[0]
    $parts = @($core.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    [pscustomobject]@{ Version = [version]::new($parts[0], $parts[1], $parts[2], $parts[3]); Pre = $Text.Contains('-') }
}
function Test-Below([string]$Have, [string]$Need) {
    $h = ConvertTo-Version $Have; $n = ConvertTo-Version $Need
    if ($h.Version -ne $n.Version) { return $h.Version -lt $n.Version }
    $h.Pre -and -not $n.Pre
}

$table = (Get-Content $Minimums -Raw | ConvertFrom-Json).packages
$files = @(Get-ChildItem $Root -Recurse -File -Include '*.csproj', '*.vcxproj', '*.wixproj', 'Directory.Packages.props' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' })
$found = 0
$failures = @()
foreach ($file in $files) {
    $xml = [xml](Get-Content $file.FullName -Raw)
    foreach ($ref in @($xml.SelectNodes('//*[local-name()="PackageReference" or local-name()="PackageVersion"]'))) {
        $id = $ref.GetAttribute('Include')
        if (-not $id) { $id = $ref.GetAttribute('Update') }
        $version = $ref.GetAttribute('Version')
        if (-not $version) { $node = $ref.SelectSingleNode('*[local-name()="Version"]'); if ($node) { $version = $node.InnerText } }
        $rule = $table | Where-Object { $_.id -eq $id }
        if (-not $rule) { continue }
        $found++
        $relative = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
        if (-not $version -or $version -match '[\$\*\[\(]') {
            $failures += "${relative}: $id has no fixed version ('$version') - the minimum $($rule.minimum) can't be checked"
        }
        elseif (Test-Below $version $rule.minimum) {
            $failures += "${relative}: $id $version is below $($rule.minimum) - $($rule.reason)"
        }
    }
}
if ($failures) {
    $failures | ForEach-Object { "::error::$_"; Write-Host $_ }
    exit 1
}
"Dependency versions: $found reference(s) to $(@($table).Count) package(s) with a minimum version, all at or above it."
