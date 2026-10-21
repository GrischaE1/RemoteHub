# Merges the SARIF files MSVC Code Analysis writes (one per source file) into one file for code scanning. The compiler
# names files by absolute, lower-cased paths; code scanning needs paths relative to the repository in their real case,
# or it can't show the alert on the source. Results in files outside the repository (Windows SDK headers) are dropped.
#   build\Merge-CodeAnalysisSarif.ps1 -Path <folder with *.nativecodeanalysis.sarif> -Root <repository> -OutFile <file>
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$OutFile
)
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path $Root).Path.TrimEnd('\')

$files = @(Get-ChildItem $Path -Filter '*.nativecodeanalysis.sarif')
if (-not $files) { throw "No *.nativecodeanalysis.sarif files in $Path - did the build run with code analysis?" }

$relative = @{}
# The repository-relative path of a file:// URI in its real case, or $null when the file is outside the repository.
function Get-RepoPath([string]$Uri) {
    if ($relative.ContainsKey($Uri)) { return $relative[$Uri] }
    $local = ([Uri]$Uri).LocalPath
    $result = $null
    if ($local.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $dir = $Root
        $parts = @()
        foreach ($segment in $local.Substring($Root.Length + 1).Split('\')) {
            $item = Get-ChildItem -LiteralPath $dir -Force | Where-Object { $_.Name -eq $segment } | Select-Object -First 1
            if (-not $item) { $parts = $null; break }
            $parts += $item.Name
            $dir = $item.FullName
        }
        if ($parts) { $result = $parts -join '/' }
    }
    $relative[$Uri] = $result
    return $result
}

# Replaces every artifactLocation under $Node (locations, code flows, related locations) with a relative uri. The
# artifacts table it indexes into is left out of the merged file, so no index may survive.
function Update-Locations($Node, $Artifacts) {
    if ($Node -is [array]) { foreach ($n in $Node) { Update-Locations $n $Artifacts }; return }
    if ($Node -isnot [pscustomobject]) { return }
    foreach ($p in @($Node.PSObject.Properties)) {
        if ($p.Name -eq 'artifactLocation') {
            $uri = $p.Value.uri
            if ($null -ne $p.Value.index) { $uri = $Artifacts[$p.Value.index].location.uri }
            $repoPath = Get-RepoPath $uri
            if ($repoPath) { $Node.artifactLocation = [pscustomobject]@{ uri = $repoPath } }
            else { $Node.artifactLocation = [pscustomobject]@{ uri = $uri } }
        }
        else { Update-Locations $p.Value $Artifacts }
    }
}

$tool = $null
$results = New-Object System.Collections.Generic.List[object]
$seen = @{}
$outside = 0
foreach ($file in $files) {
    $sarif = Get-Content $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($run in @($sarif.runs)) {
        if (-not $tool) { $tool = $run.tool }
        $artifacts = @($run.artifacts)
        foreach ($r in @($run.results)) {
            if ($null -eq $r) { continue }
            $r.PSObject.Properties.Remove('analysisTarget')
            Update-Locations $r $artifacts
            $first = @($r.locations)[0].physicalLocation
            if (-not $first -or $first.artifactLocation.uri -like 'file:*') { $outside++; continue }
            # A header included by several sources is analysed, and reported, once per source.
            $key = '{0}|{1}|{2}|{3}' -f $r.ruleId, $first.artifactLocation.uri, $first.region.startLine, $r.message.text
            if ($seen.ContainsKey($key)) { continue }
            $seen[$key] = $true
            $results.Add($r)
        }
    }
}

$merged = [pscustomobject]@{
    version = '2.1.0'
    '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'
    runs = @([pscustomobject]@{ tool = $tool; results = $results.ToArray() })
}
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$merged | ConvertTo-Json -Depth 100 | Set-Content -Path $OutFile -Encoding UTF8
"Code analysis: $($results.Count) result(s) from $($files.Count) file(s) in $OutFile ($outside outside the repository left out)."
