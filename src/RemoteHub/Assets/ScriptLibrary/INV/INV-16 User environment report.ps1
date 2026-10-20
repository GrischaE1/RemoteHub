<#
.SYNOPSIS
    Reports the signed-in user's environment: proxy, mapped drives, printers, OneDrive and more.
.DESCRIPTION
    Covers WinINET proxy and PAC settings, mapped drives, connected and default printers, OneDrive
    account state and known folder redirection, the default browser, the language and keyboard
    list and user environment variables. Mapped drives and printer connections exist only in the
    user session, so the script returns "Data unavailable" when run as SYSTEM. Account emails and
    user names inside paths are redacted unless -IncludeIdentity is set.
.NOTES
    RemoteHub: id=INV-16; context=User; os=Client; risk=R0
#>
[CmdletBinding()]
param(
    [switch]$IncludeIdentity,
    [switch]$Sensor
)

$ErrorActionPreference = 'Stop'

function Hide-Identity([string]$Text) {
    if ($IncludeIdentity -or [string]::IsNullOrEmpty($Text)) { return $Text }
    $out = $Text
    if ($env:USERPROFILE) { $out = $out.Replace($env:USERPROFILE, '%USERPROFILE%') }
    $out = [regex]::Replace($out, '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '[REDACTED]')
    if ($env:USERNAME) { $out = [regex]::Replace($out, [regex]::Escape($env:USERNAME), '[REDACTED]', 'IgnoreCase') }
    return $out
}

function Get-RegValue([string]$Path, [string]$Name) {
    try { return (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop).$Name } catch { return $null }
}

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) {
        'Data unavailable: no user is signed in to this context (running as SYSTEM).'
        exit 1
    }

    $inet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $proxy = [PSCustomObject]@{
        ProxyEnabled  = ((Get-RegValue $inet 'ProxyEnable') -eq 1)
        ProxyServer   = [string](Get-RegValue $inet 'ProxyServer')
        ProxyOverride = [string](Get-RegValue $inet 'ProxyOverride')
        AutoConfigUrl = [string](Get-RegValue $inet 'AutoConfigURL')
    }

    $drives = 'Data unavailable'
    try {
        $drives = @(Get-SmbMapping | ForEach-Object {
            [PSCustomObject]@{ Drive = $_.LocalPath; Target = Hide-Identity $_.RemotePath; Status = [string]$_.Status }
        })
    } catch { Write-Verbose "Get-SmbMapping failed: $($_.Exception.Message)" }

    $printers = 'Data unavailable'
    $defaultPrinter = 'Data unavailable'
    try {
        $printers = @(Get-Printer | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Type = [string]$_.Type; Shared = $_.Shared } })
        $dp = Get-CimInstance -ClassName Win32_Printer -Filter 'Default = TRUE' | Select-Object -First 1
        if ($dp) { $defaultPrinter = $dp.Name } else { $defaultPrinter = 'None' }
    } catch { Write-Verbose "Printer query failed: $($_.Exception.Message)" }

    $oneDrive = @()
    $odRoot = 'HKCU:\Software\Microsoft\OneDrive\Accounts'
    if (Test-Path -LiteralPath $odRoot) {
        foreach ($k in Get-ChildItem -LiteralPath $odRoot) {
            $folder = Get-RegValue $k.PSPath 'UserFolder'
            if (-not $folder) { continue }
            $oneDrive += [PSCustomObject]@{
                Account  = $k.PSChildName
                Email    = Hide-Identity ([string](Get-RegValue $k.PSPath 'UserEmail'))
                Tenant   = [string](Get-RegValue $k.PSPath 'DisplayName')
                Folder   = Hide-Identity $folder
            }
        }
    }
    $odFolders = @($oneDrive | ForEach-Object { (Get-RegValue "$odRoot\$($_.Account)" 'UserFolder') })

    $usf = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
    $known = [ordered]@{ Desktop = 'Desktop'; Documents = 'Personal'; Pictures = 'My Pictures' }
    $kfm = @()
    $allRedirected = ($odFolders.Count -gt 0)
    foreach ($label in $known.Keys) {
        $path = [Environment]::ExpandEnvironmentVariables([string](Get-RegValue $usf $known[$label]))
        $redirected = $false
        foreach ($f in $odFolders) { if ($f -and $path -like "$f\*") { $redirected = $true } }
        if (-not $redirected) { $allRedirected = $false }
        $kfm += [PSCustomObject]@{ Folder = $label; Path = Hide-Identity $path; InOneDrive = $redirected }
    }

    if ($Sensor) {
        $allRedirected
        exit 0
    }

    $browser = [string](Get-RegValue 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' 'ProgId')
    if (-not $browser) { $browser = 'Data unavailable' }

    $languages = 'Data unavailable'
    try {
        $languages = @(Get-WinUserLanguageList | ForEach-Object {
            [PSCustomObject]@{ Language = $_.LanguageTag; Keyboards = ($_.InputMethodTips -join ', ') }
        })
    } catch { Write-Verbose "Language list unavailable: $($_.Exception.Message)" }

    $envVars = [ordered]@{}
    $envKey = Get-Item -LiteralPath 'HKCU:\Environment'
    foreach ($n in $envKey.GetValueNames()) { $envVars[$n] = Hide-Identity ([string]$envKey.GetValue($n, '', 'DoNotExpandEnvironmentNames')) }

    [PSCustomObject]@{
        WinInetProxy       = $proxy
        MappedDrives       = $drives
        Printers           = $printers
        DefaultPrinter     = $defaultPrinter
        OneDriveAccounts   = $oneDrive
        KnownFolders       = $kfm
        KnownFoldersInOneDrive = $allRedirected
        DefaultBrowser     = $browser
        Languages          = $languages
        EnvironmentVariables = [PSCustomObject]$envVars
    } | ConvertTo-Json -Depth 5
    exit 0
} catch {
    Write-Error "User environment report failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
