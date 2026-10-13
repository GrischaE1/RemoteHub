# Start-time baseline: RemoteHub has to show its main window in less than 3 seconds.
#   powershell -ExecutionPolicy Bypass -File build\Test-StartTime.ps1 [-Publish artifacts\publish] [-Connections 500] [-Shares 3]
#
# Fills a test settings folder with many connections in folders, each with network shares, plus a share on its own in
# each folder (lab harness "seed"), starts the published RemoteHub.exe with it (REMOTEHUB_DATA_DIR) several times and
# reads RemoteHub's own "Started in ... ms" log line: from the process start until the main window drew its first
# frame. The median has to stay under -LimitMs.
# The exe is copied to a local folder first, so a slow network share does not count. Runs in any user session (also a
# disconnected RDP session); in the service session (0) the check is skipped.
param(
    [string]$Publish,
    [int]$Connections = 500,
    [int]$Folders = 25,
    [int]$Shares = 3,
    [int]$Runs = 3,
    [int]$LimitMs = 3000
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if (-not $Publish) { $Publish = Join-Path $root "artifacts\publish" }
if (-not (Test-Path "$Publish\RemoteHub.exe")) { throw "RemoteHub.exe not found in $Publish." }

$session = (Get-Process -Id $PID).SessionId
if ($session -eq 0) { Write-Warning "Start time not measured: no desktop in the service session."; return }

$work = Join-Path $env:TEMP "rh-starttime-$PID"
$app = Join-Path $work "app"
$data = Join-Path $work "data"
$harness = Join-Path $work "harness"
try {
    New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
    Copy-Item "$Publish\*" $app -Recurse

    dotnet build "$root\tests\RemoteHub.LabHarness\RemoteHub.LabHarness.csproj" -c Release -o $harness --nologo -v q | Out-Null
    if ($LASTEXITCODE) { throw "lab harness build failed" }
    $env:REMOTEHUB_DATA_DIR = $data
    $seed = & "$harness\RemoteHub.LabHarness.exe" seed --connections $Connections --folders $Folders --shares $Shares
    if ($LASTEXITCODE) { throw "seed failed: $seed" }

    $log = Join-Path $data "logs\remotehub.log"
    $times = @()
    for ($run = 1; $run -le $Runs; $run++) {
        if (Test-Path $log) { Remove-Item $log }
        $psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $app "RemoteHub.exe")
        $psi.UseShellExecute = $false
        $psi.EnvironmentVariables["REMOTEHUB_DATA_DIR"] = $data
        $p = [Diagnostics.Process]::Start($psi)
        $line = $null
        $deadline = (Get-Date).AddSeconds(30)
        while (-not $line -and (Get-Date) -lt $deadline -and -not $p.HasExited) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $log) { $line = Select-String -Path $log -Pattern "Started in (\d+) ms" | Select-Object -Last 1 }
        }
        if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
        if (-not $line) { throw "Run ${run}: RemoteHub didn't report its start within 30 s." }
        $ms = [int]$line.Matches[0].Groups[1].Value
        $times += $ms
        "Run ${run}: $($line.Line.Substring($line.Line.IndexOf('Started')))"
    }
    $median = ($times | Sort-Object)[[int][math]::Floor($times.Count / 2)]
    if ($median -ge $LimitMs) { throw "Start time $median ms (median of $Runs) - the limit is $LimitMs ms." }
    "Start time OK: $median ms (median of $Runs, limit $LimitMs ms, $Connections connections with $Shares shares each)"
}
finally {
    Remove-Item Env:REMOTEHUB_DATA_DIR -ErrorAction SilentlyContinue
    for ($try = 1; (Test-Path $work) -and $try -le 5; $try++) {
        try { [IO.Directory]::Delete($work, $true) } catch { Start-Sleep -Milliseconds 500 }
    }
}
