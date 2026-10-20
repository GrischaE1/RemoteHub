<#
.SYNOPSIS
    Reports the signed-in user's policy results (GPOs, folder redirection, drives, printers, roaming, MDM) as JSON.
.DESCRIPTION
    Collects applied and filtered user GPOs, folder redirection state, mapped drives (including Group Policy
    Preferences drive maps), printer connections, roaming profile state and user-scope MDM policy areas visible in
    the session. Works without elevation for the user scope and returns "Data unavailable" when no user is signed
    in (for example when run as SYSTEM). User names and SIDs are shown as [REDACTED] unless -IncludeIdentity is
    given; redact before sharing. Read-only.
.NOTES
    RemoteHub: id=FIX-16; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeIdentity
)

$ErrorActionPreference = 'Stop'

function Protect-Text([string]$Text) {
    if ($IncludeIdentity -or -not $Text) { return $Text }
    $t = $Text
    if ($env:USERNAME) { $t = $t -replace [regex]::Escape($env:USERNAME), '[REDACTED]' }
    $t = $t -replace 'S-1-5-21-[\d-]+', '[REDACTED]'
    return ($t -replace '[^\s\\/:@]+@[^\s\\/:]+', '[REDACTED]')
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ($identity.IsSystem) { [pscustomobject]@{ Result = 'Data unavailable'; Reason = 'No signed-in user in this context; run in the user context.' } | ConvertTo-Json; exit 0 }
    $sid = $identity.User.Value

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('FIX-16-{0}.xml' -f [guid]::NewGuid().ToString('N'))
    $gpos = 'Data unavailable'; $extensions = @()
    try {
        $p = Start-Process -FilePath (Join-Path $env:windir 'System32\gpresult.exe') -ArgumentList ('/scope user /x "{0}" /f' -f $tmp) -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(300000)) { try { $p.Kill() } catch { } }
        elseif ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $tmp)) {
            # Element names and booleans in the RSoP XML are not localised.
            $rsop = [xml](Get-Content -LiteralPath $tmp -Raw)
            $gpos = @(foreach ($g in @($rsop.Rsop.UserResults.GPO)) {
                $reason = 'Applied'
                if ($g.AccessDenied -eq 'true') { $reason = 'Denied (security filtering)' }
                elseif ($g.FilterAllowed -eq 'false') { $reason = 'Denied (WMI filter)' }
                elseif ($g.Enabled -eq 'false') { $reason = 'Disabled' }
                [pscustomobject]@{ Name = $g.Name; Result = $reason }
            })
            $extensions = @($rsop.Rsop.UserResults.ExtensionData | ForEach-Object { $_.Name.'#text' } | Where-Object { $_ })
        }
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }

    $usf = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
    $redirection = @(foreach ($n in 'Desktop', 'Personal', 'My Pictures', 'My Music', 'My Video', 'Favorites', 'AppData', '{374DE290-123F-4565-9164-39C4925E467B}') {
        if ($usf.PSObject.Properties[$n]) {
            $path = [string]$usf.$n
            [pscustomobject]@{ Folder = $n; Redirected = ($path.StartsWith('\\') -or $path -match 'OneDrive'); Path = (Protect-Text $path) }
        }
    })

    $drives = @(Get-ChildItem -LiteralPath 'HKCU:\Network' -ErrorAction SilentlyContinue | ForEach-Object {
        $v = Get-ItemProperty -LiteralPath $_.PSPath
        [pscustomobject]@{ Drive = $_.PSChildName.ToUpper(); RemotePath = (Protect-Text ([string]$v.RemotePath)) }
    })
    $printers = 'Data unavailable'
    if (Get-Command -Name Get-Printer -ErrorAction SilentlyContinue) {
        $printers = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.Type -eq 'Connection' } | ForEach-Object { $_.Name })
    }

    $profileEntry = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue
    $roaming = 'Data unavailable'
    if ($profileEntry) {
        $central = ''; if ($profileEntry.PSObject.Properties['CentralProfile']) { $central = [string]$profileEntry.CentralProfile }
        $roaming = [pscustomobject]@{ Roaming = [bool]$central; CentralProfile = (Protect-Text $central); State = $profileEntry.State }
    }

    $mdmAreas = 'Data unavailable'
    $mdmKey = "HKLM:\SOFTWARE\Microsoft\PolicyManager\current\$sid"
    if (Test-Path -LiteralPath $mdmKey) { $mdmAreas = @(Get-ChildItem -LiteralPath $mdmKey -ErrorAction SilentlyContinue | Where-Object { $_.ValueCount -gt 0 } | ForEach-Object { $_.PSChildName }) }
    elseif (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current') { $mdmAreas = @() }

    $userName = '[REDACTED]'; $sidOut = '[REDACTED]'
    if ($IncludeIdentity) { $userName = $identity.Name; $sidOut = $sid }
    [pscustomobject]@{
        User = $userName
        Sid = $sidOut
        Collected = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        UserGpos = $gpos
        ExtensionsApplied = $extensions
        FolderRedirection = $redirection
        MappedDrives = $drives
        PrinterConnections = $printers
        RoamingProfile = $roaming
        MdmUserPolicyAreas = $mdmAreas
    } | ConvertTo-Json -Depth 5
    exit 0
}
catch {
    Write-Output "FIX-16 failed: $($_.Exception.Message)"
    exit 1
}
