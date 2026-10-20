<#
.SYNOPSIS
    Runs get, test, set or export of a DSC v3 configuration document on this machine with the dsc executable.
.DESCRIPTION
    Uses the DSC v3 executable (dsc config get/test/set/export) with a YAML or JSON document. Without
    -DocumentPath a built-in document is used that keeps SMBv1 server disabled through the Microsoft.Windows/Registry
    resource. Set runs only after a test shows drift, and -WhatIf runs dsc's own what-if. There is no LCM and no
    background correction: schedule the test and approve sets yourself. Pin the executable version you validated
    with -RequiredVersion, because the project releases often. Not every DSC 1.1 resource works through the adapter.
.NOTES
    RemoteHub: id=DSC-13; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Test', 'Get', 'Set', 'Export')]
    [string]$Operation = 'Test',
    [string]$DocumentPath = '',
    [string]$DscPath = '',
    [string]$RequiredVersion = ''
)

$ErrorActionPreference = 'Stop'
$id = 'DSC-13'

function Invoke-Dsc {
    param([string]$Exe, [string]$Arguments)
    # Process is used directly because redirected stderr under ErrorActionPreference Stop terminates in 5.1.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    if (-not $p.WaitForExit(600000)) { $p.Kill(); throw 'dsc did not finish within 10 minutes.' }
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out; StdErr = $errTask.Result }
}

function Get-DriftList {
    param($TestJson)
    foreach ($r in @($TestJson.results)) {
        $res = $r.result
        $in = $true
        if ($res.PSObject.Properties['inDesiredState']) { $in = [bool]$res.inDesiredState }
        $diff = ''
        if ($res.PSObject.Properties['differingProperties']) { $diff = (@($res.differingProperties) -join ', ') }
        [pscustomobject]@{ Resource = [string]$r.name; Type = [string]$r.type; InDesiredState = $in; Differing = $diff }
    }
}

$sampleDocument = @'
$schema: https://aka.ms/dsc/schemas/v3/bundled/config/document.json
resources:
  - name: SMBv1 server disabled
    type: Microsoft.Windows/Registry
    properties:
      keyPath: HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters
      valueName: SMB1
      valueData:
        DWord: 0
'@

try {
    $exe = $DscPath
    if (-not $exe) {
        $cmd = Get-Command -Name 'dsc.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd) { $exe = $cmd.Source }
    }
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { throw 'Data unavailable: the DSC v3 executable (dsc.exe) was not found; pass -DscPath.' }

    $ver = Invoke-Dsc -Exe $exe -Arguments '--version'
    $version = 'Data unavailable'
    if ($ver.StdOut -match '(\d+\.\d+\.\d+[\w\.\-]*)') { $version = $Matches[1] }
    if ($RequiredVersion -and $version -ne $RequiredVersion) {
        throw "dsc version $version does not match the pinned version $RequiredVersion."
    }
    if (-not $RequiredVersion) { Write-Warning "No -RequiredVersion pinned; running dsc $version." }

    $doc = $DocumentPath
    if (-not $doc) {
        $workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"
        $doc = Join-Path $workDir 'sample.dsc.yaml'
        $savedWhatIf = $WhatIfPreference
        $WhatIfPreference = $false
        try {
            if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
            # Written without a BOM so the YAML parser sees the document as plain UTF-8.
            [IO.File]::WriteAllText($doc, $sampleDocument, (New-Object System.Text.UTF8Encoding $false))
        } finally { $WhatIfPreference = $savedWhatIf }
    }
    if (-not (Test-Path -LiteralPath $doc)) { throw "Data unavailable: document not found: $doc" }
    $fileArg = '--file "' + $doc + '"'

    if ($Operation -in @('Get', 'Export')) {
        $r = Invoke-Dsc -Exe $exe -Arguments ("config {0} {1} --output-format json" -f $Operation.ToLowerInvariant(), $fileArg)
        if ($r.ExitCode -ne 0) { throw "dsc config $Operation failed (exit $($r.ExitCode)): $($r.StdErr.Trim())" }
        Write-Output (($r.StdOut | ConvertFrom-Json) | ConvertTo-Json -Depth 8)
        exit 0
    }

    $t = Invoke-Dsc -Exe $exe -Arguments ("config test {0} --output-format json" -f $fileArg)
    if ($t.ExitCode -ne 0) { throw "dsc config test failed (exit $($t.ExitCode)): $($t.StdErr.Trim())" }
    $rows = @(Get-DriftList ($t.StdOut | ConvertFrom-Json))
    $drift = @($rows | Where-Object { -not $_.InDesiredState })
    $rows

    $summary = [ordered]@{ Id = $id; DscVersion = $version; Document = $doc; Operation = $Operation; InDesiredState = ($drift.Count -eq 0); Result = '' }
    if ($rows.Count -eq 0) { $summary.Result = 'Data unavailable: no test results returned'; [pscustomobject]$summary; exit 1 }
    if ($Operation -eq 'Test' -or $drift.Count -eq 0) {
        if ($drift.Count -eq 0) { $summary.Result = 'No change: in desired state' } else { $summary.Result = 'Not in desired state (test only)' }
        [pscustomobject]$summary
        if ($drift.Count -eq 0) { exit 0 } else { exit 1 }
    }

    if (-not $PSCmdlet.ShouldProcess("$env:COMPUTERNAME", "dsc config set ($($drift.Count) resource(s) to change)")) {
        $w = Invoke-Dsc -Exe $exe -Arguments ("config set {0} --what-if --output-format json" -f $fileArg)
        if ($w.ExitCode -eq 0) { Write-Output (($w.StdOut | ConvertFrom-Json) | ConvertTo-Json -Depth 8) }
        $summary.Result = 'WhatIf: nothing changed'
        [pscustomobject]$summary
        exit 0
    }
    # The test output holds the actual state before the change, which is the rollback reference.
    $workDir = Join-Path $env:ProgramData "RemoteHub\DSC\$id"
    if (-not (Test-Path $workDir)) { New-Item -ItemType Directory -Path $workDir -Force | Out-Null }
    $rollback = Join-Path $workDir ('before-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    Set-Content -Path $rollback -Value $t.StdOut -Encoding UTF8

    $s = Invoke-Dsc -Exe $exe -Arguments ("config set {0} --output-format json" -f $fileArg)
    if ($s.ExitCode -ne 0) { throw "dsc config set failed (exit $($s.ExitCode)): $($s.StdErr.Trim())" }
    $after = Invoke-Dsc -Exe $exe -Arguments ("config test {0} --output-format json" -f $fileArg)
    if ($after.ExitCode -ne 0) { throw "dsc config test after set failed (exit $($after.ExitCode))." }
    $left = @(Get-DriftList ($after.StdOut | ConvertFrom-Json) | Where-Object { -not $_.InDesiredState })
    $summary.InDesiredState = ($left.Count -eq 0)
    if ($left.Count -gt 0) { $summary.Result = 'Set ran, but still not in desired state: ' + (($left | ForEach-Object { $_.Resource }) -join '; ') }
    else { $summary.Result = 'Changed; prior state saved to ' + $rollback }
    [pscustomobject]$summary
    if ($left.Count -gt 0) { exit 1 }
    exit 0
} catch {
    Write-Error "$id failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
