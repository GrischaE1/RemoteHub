<#
.SYNOPSIS
    Resets the Microsoft 365 and Teams identity caches for the signed-in user to end repeated sign-in prompts.
.DESCRIPTION
    Reports the current identity state first, then renames (not deletes) the identity cache, OneAuth, the Entra
    broker plugin and Teams caches, backs up and clears the Office identity registry key, and removes only stored
    credentials whose target matches known Office/Teams patterns - never VPN, file share or unrelated entries.
    The user will see sign-in and MFA prompts afterwards. Token contents are never read or logged, and account
    names in credential targets are shown as [REDACTED]. Paths differ between Office and Teams generations.
.NOTES
    RemoteHub: id=FIX-08; context=User; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$CredentialPattern = @('MicrosoftOffice1*_Data:*', 'msteams_*'),
    [switch]$CloseApps
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]
function Add-Row([string]$Item, [string]$State, [string]$Action) { $results.Add([pscustomobject]@{ Item = $Item; State = $State; Action = $Action }) }
function Hide-Identity([string]$Text) { return ($Text -replace '[^\s:@/]+@[^\s:/]+', '[REDACTED]') }

function Invoke-Native([string]$FilePath, [string]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $FilePath, $Arguments
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $null = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(60000)) { try { $p.Kill() } catch { }; throw "$FilePath timed out." }
    [pscustomobject]@{ ExitCode = $p.ExitCode; StdOut = $out.Result }
}

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { Write-Output 'Data unavailable: run this script in the signed-in user context.'; exit 1 }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logDir = Join-Path $env:LOCALAPPDATA 'RemoteHub\ScriptLogs'

    # State first, as INV-10 would report it, so that the reset is not done blindly.
    $joined = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo'
    Add-Row 'Device Entra joined' "$joined" 'None'
    $idKey = 'HKCU:\Software\Microsoft\Office\16.0\Common\Identity'
    $identities = @(Get-ChildItem -LiteralPath (Join-Path $idKey 'Identities') -ErrorAction SilentlyContinue).Count
    Add-Row 'Office identities cached' "$identities" 'None'

    $apps = @(Get-Process -Name WINWORD, EXCEL, POWERPNT, OUTLOOK, ONENOTE, Teams, ms-teams -ErrorAction SilentlyContinue)
    if ($apps.Count -gt 0) {
        if (-not $CloseApps) {
            $results
            Write-Output ('Close these apps first or run with -CloseApps: ' + (($apps | Select-Object -ExpandProperty Name -Unique) -join ', '))
            exit 1
        }
        if ($PSCmdlet.ShouldProcess((($apps | Select-Object -ExpandProperty Name -Unique) -join ', '), 'Close Office and Teams')) {
            $apps | Stop-Process -Force
            $apps | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
        }
    }

    $folders = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\IdentityCache'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\OneAuth'),
        (Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy\AC\TokenBroker'),
        (Join-Path $env:APPDATA 'Microsoft\Teams'),
        (Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams')
    )
    foreach ($f in $folders) {
        if (-not (Test-Path -LiteralPath $f)) { continue }
        $short = $f.Replace($env:LOCALAPPDATA, '%LOCALAPPDATA%').Replace($env:APPDATA, '%APPDATA%')
        if ($PSCmdlet.ShouldProcess($short, "Rename to *.bak-$stamp")) {
            # Renaming keeps a way back; the caches are rebuilt on the next sign-in.
            Rename-Item -LiteralPath $f -NewName ((Split-Path -Leaf $f) + ".bak-$stamp")
            Add-Row $short 'Present' "Renamed to .bak-$stamp"
        }
        else { Add-Row $short 'Present' 'Would rename' }
    }

    if (Test-Path -LiteralPath (Join-Path $idKey 'Identities')) {
        if ($PSCmdlet.ShouldProcess('HKCU Office Identity\Identities', 'Export to a .reg backup and remove')) {
            if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
            $backup = Join-Path $logDir "FIX-08-office-identity-$stamp.reg"
            $e = Invoke-Native (Join-Path $env:windir 'System32\reg.exe') ('export "HKCU\Software\Microsoft\Office\16.0\Common\Identity" "{0}" /y' -f $backup)
            if ($e.ExitCode -ne 0) { throw "Registry backup failed (reg.exe exit $($e.ExitCode)); key not removed." }
            Remove-Item -LiteralPath (Join-Path $idKey 'Identities') -Recurse -Force
            Add-Row 'Office identity registry' 'Present' "Removed, backup at $backup"
        }
        else { Add-Row 'Office identity registry' 'Present' 'Would remove' }
    }

    # cmdkey labels are localised, but the "target=" part of each identifier is not.
    $cmdkey = Join-Path $env:windir 'System32\cmdkey.exe'
    $list = Invoke-Native $cmdkey '/list'
    $targets = foreach ($line in ($list.StdOut -split "`r?`n")) { if ($line -match 'target=(.+?)\s*$') { $Matches[1] } }
    foreach ($t in @($targets | Select-Object -Unique)) {
        $match = @($CredentialPattern | Where-Object { $t -like $_ }).Count -gt 0
        if (-not $match) { continue }
        $shown = Hide-Identity $t
        if ($PSCmdlet.ShouldProcess($shown, 'Delete stored credential')) {
            $d = Invoke-Native $cmdkey ('/delete:"{0}"' -f $t)
            if ($d.ExitCode -ne 0) { throw "cmdkey could not delete $shown (exit $($d.ExitCode))." }
            Add-Row "Credential $shown" 'Present' 'Deleted'
        }
        else { Add-Row "Credential $shown" 'Present' 'Would delete' }
    }

    $results
    $changed = @($results | Where-Object { $_.Action -like 'Renamed*' -or $_.Action -like 'Removed*' -or $_.Action -eq 'Deleted' }).Count
    if ($changed -gt 0) { Write-Output 'Caches cleared. Open Outlook or Teams and sign in again; expect an MFA prompt.' }
    elseif (@($results | Where-Object { $_.Action -like 'Would*' }).Count -gt 0) { Write-Output 'WhatIf: the changes above were not made.' }
    else { Write-Output 'No change: no identity caches or matching credentials were found.' }
    exit 0
}
catch {
    $results
    Write-Output "FIX-08 failed: $($_.Exception.Message)"
    exit 1
}
