<#
.SYNOPSIS
    Collects a redacted support bundle (logs, policy, join, network, software and driver lists) into one archive.
.DESCRIPTION
    Gathers system information, exported event logs (System, Application, Setup, update client, Group Policy,
    device management, Defender; Security only with -IncludeSecurityLog), the decoded update log, servicing logs,
    join and management state, computer policy results, network configuration, software and driver lists and a
    manifest with SHA256 hashes into a protected folder under ProgramData, redacts user names, UPNs, emails, SIDs
    (and optionally IPv4 addresses) in text files as [REDACTED], and compresses the result within a size limit.
    Exported .evtx logs are binary and cannot be redacted. Review before sharing, upload only to an approved
    location, and delete the local copy on a schedule. User-scope data comes from FIX-16 and INV-10.
.NOTES
    RemoteHub: id=FIX-23; context=System; os=Both; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeSecurityLog,
    [bool]$RedactIpAddresses = $false,
    [int]$MaxSizeMB = 500
)

$ErrorActionPreference = 'Stop'
$root = Join-Path $env:ProgramData 'RemoteHub\ScriptLogs\FIX-23'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $root "bundle-$stamp"
$notes = New-Object System.Collections.Generic.List[string]

function Invoke-ToFile([string]$FilePath, [string]$Arguments, [string]$OutFile, [int]$TimeoutSec = 300) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $FilePath, $Arguments
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync(); $null = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { try { $p.Kill() } catch { }; $notes.Add("$(Split-Path -Leaf $FilePath) $Arguments timed out"); return }
    if ($OutFile) { [IO.File]::WriteAllText($OutFile, $out.Result, (New-Object Text.UTF8Encoding $false)) }
    if ($p.ExitCode -ne 0) { $notes.Add("$(Split-Path -Leaf $FilePath) $Arguments exit $($p.ExitCode)") }
}

function Step([string]$Name, [scriptblock]$Action) {
    # One failed collector must not lose the rest of the bundle; the failure is recorded in the notes.
    try { & $Action } catch { $notes.Add("${Name}: $($_.Exception.Message)") }
}

try {
    if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) { throw 'Run this script in 64-bit PowerShell.' }
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    # Bundles hold personal and tenant data: only SYSTEM and Administrators may read the folder.
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $sys32 = Join-Path $env:windir 'System32'
    Step 'systeminfo' { Invoke-ToFile (Join-Path $sys32 'systeminfo.exe') '/fo list' (Join-Path $stage 'systeminfo.txt') }
    $logs = @('System', 'Application', 'Setup', 'Microsoft-Windows-WindowsUpdateClient/Operational', 'Microsoft-Windows-GroupPolicy/Operational',
        'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin', 'Microsoft-Windows-Windows Defender/Operational')
    if ($IncludeSecurityLog) { $logs += 'Security' }
    New-Item -ItemType Directory -Path (Join-Path $stage 'events') -Force | Out-Null
    foreach ($l in $logs) {
        $file = Join-Path $stage ('events\' + ($l -replace '[\\/ ]', '_') + '.evtx')
        Step "export $l" { if (Get-WinEvent -ListLog $l -ErrorAction SilentlyContinue) { Invoke-ToFile (Join-Path $sys32 'wevtutil.exe') ('epl "{0}" "{1}"' -f $l, $file) '' } }
    }
    Step 'WindowsUpdate.log' { $null = Get-WindowsUpdateLog -LogPath (Join-Path $stage 'WindowsUpdate.log') }
    Step 'servicing logs' {
        foreach ($src in (Join-Path $env:windir 'Logs\CBS\CBS.log'), (Join-Path $env:windir 'Logs\DISM\dism.log')) {
            if (-not (Test-Path -LiteralPath $src)) { continue }
            $in = New-Object IO.FileStream($src, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try { $outStream = [IO.File]::Create((Join-Path $stage (Split-Path -Leaf $src))); try { $in.CopyTo($outStream) } finally { $outStream.Dispose() } } finally { $in.Dispose() }
        }
    }
    Step 'dsregcmd' { Invoke-ToFile (Join-Path $sys32 'dsregcmd.exe') '/status' (Join-Path $stage 'dsregcmd.txt') }
    Step 'gpresult' { Invoke-ToFile (Join-Path $sys32 'gpresult.exe') ('/scope computer /x "{0}" /f' -f (Join-Path $stage 'gpresult-computer.xml')) '' }
    Step 'ipconfig' { Invoke-ToFile (Join-Path $sys32 'ipconfig.exe') '/all' (Join-Path $stage 'ipconfig.txt') }
    Step 'routes' { Invoke-ToFile (Join-Path $sys32 'route.exe') 'print' (Join-Path $stage 'routes.txt') }
    Step 'software' {
        @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*') |
            ForEach-Object { Get-ItemProperty -Path $_ -ErrorAction SilentlyContinue } | Where-Object { $_.DisplayName } |
            Select-Object DisplayName, DisplayVersion, Publisher, InstallDate | Sort-Object DisplayName |
            Export-Csv -LiteralPath (Join-Path $stage 'software.csv') -NoTypeInformation -Encoding UTF8
    }
    Step 'drivers' {
        if (Get-Command -Name Get-WindowsDriver -ErrorAction SilentlyContinue) {
            Get-WindowsDriver -Online | Select-Object ProviderName, ClassName, Version, Date, OriginalFileName, BootCritical |
                Export-Csv -LiteralPath (Join-Path $stage 'drivers.csv') -NoTypeInformation -Encoding UTF8
        }
        else { $notes.Add('drivers: Data unavailable (DISM module missing)') }
    }

    # Redaction pass over text files: profile folder names, UPNs and emails, user SIDs, optionally IPv4 addresses.
    $names = @(Get-CimInstance -ClassName Win32_UserProfile | Where-Object { -not $_.Special -and $_.LocalPath } | ForEach-Object { Split-Path -Leaf $_.LocalPath } | Where-Object { $_.Length -ge 3 } | Select-Object -Unique)
    foreach ($f in @(Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in '.txt', '.csv', '.log', '.xml' })) {
        $text = [IO.File]::ReadAllText($f.FullName)
        foreach ($n in $names) { $text = [regex]::Replace($text, '(?i)\b' + [regex]::Escape($n) + '\b', '[REDACTED]') }
        $text = [regex]::Replace($text, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[REDACTED]')
        $text = [regex]::Replace($text, 'S-1-(5-21|12-1)(-\d+)+', '[REDACTED]')
        if ($RedactIpAddresses) { $text = [regex]::Replace($text, '\b(\d{1,3}\.){3}\d{1,3}\b', '[REDACTED]') }
        [IO.File]::WriteAllText($f.FullName, $text, (New-Object Text.UTF8Encoding $false))
    }
    $notes.Add('Exported .evtx event logs are binary and not redacted.')

    # Large servicing logs are dropped first if the bundle would exceed the size limit.
    foreach ($big in 'CBS.log', 'dism.log', 'WindowsUpdate.log') {
        $total = (Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
        if ($total -le ($MaxSizeMB * 1MB * 4)) { break }
        $path = Join-Path $stage $big
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force; $notes.Add("$big left out to stay within $MaxSizeMB MB") }
    }
    $manifest = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        [pscustomobject]@{ File = $_.FullName.Substring($stage.Length + 1); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [pscustomobject]@{ Computer = $env:COMPUTERNAME; Created = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); Files = $manifest; Notes = $notes } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8

    $archive = Join-Path $root "bundle-$env:COMPUTERNAME-$stamp.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
    Remove-Item -LiteralPath $stage -Recurse -Force
    $sizeMB = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1)
    if ($sizeMB -gt $MaxSizeMB) { Remove-Item -LiteralPath $archive -Force; throw "Archive is $sizeMB MB, over the $MaxSizeMB MB limit; it was deleted." }

    [pscustomobject]@{ Archive = $archive; SizeMB = $sizeMB; Files = $manifest.Count; Notes = ($notes -join '; '); Reminder = 'Review before sharing; upload only to an approved location and delete the local copy on a schedule.' }
    exit 0
}
catch {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Output "FIX-23 failed: $($_.Exception.Message)"
    exit 1
}
