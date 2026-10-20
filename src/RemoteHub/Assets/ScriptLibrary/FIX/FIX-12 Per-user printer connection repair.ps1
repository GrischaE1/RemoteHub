<#
.SYNOPSIS
    Repairs the signed-in user's printer connections: removes stale ones, adds approved ones, sets the default.
.DESCRIPTION
    Lists the user's printer connections, removes connections to retired print servers, adds connections from the
    approved list and sets the default printer. Connections are per user, so this must run as the user, not SYSTEM.
    Adding a connection needs the driver to be installable under the Point and Print policy and line of sight to
    the print server (Kerberos for Entra-joined devices). When Windows manages the default printer, that setting
    is honoured and the default is not changed. The before list is saved so connections can be restored.
.NOTES
    RemoteHub: id=FIX-12; context=User; os=Client; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$RetiredServer = @(),
    [string[]]$AddConnection = @(),
    [string]$DefaultPrinter = ''
)

$ErrorActionPreference = 'Stop'
$actions = New-Object System.Collections.Generic.List[object]
$failed = $false
function Add-Action([string]$Printer, [string]$Action, [string]$Result) { $actions.Add([pscustomobject]@{ Phase = 'Action'; Printer = $Printer; Default = ''; Detail = "$Action - $Result" }) }

function Get-Connection {
    $default = Get-CimInstance -ClassName Win32_Printer -Filter 'Default=TRUE' -ErrorAction SilentlyContinue
    @(Get-Printer | Where-Object { $_.Type -eq 'Connection' } | ForEach-Object {
        [pscustomobject]@{ Phase = ''; Printer = $_.Name; Default = ($default -and $default.Name -eq $_.Name); Detail = "$($_.PrinterStatus)" }
    })
}

try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { Write-Output 'Data unavailable: printer connections are per user; run this script in the user context.'; exit 1 }
    $before = Get-Connection
    $logDir = Join-Path $env:LOCALAPPDATA 'RemoteHub\ScriptLogs'

    foreach ($c in $before) {
        $server = ($c.Printer.TrimStart('\') -split '\\')[0]
        if ($RetiredServer -notcontains $server) { continue }
        if ($PSCmdlet.ShouldProcess($c.Printer, 'Remove connection to retired print server')) {
            if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
            # Saved once before the first removal so the user's connections can be added back.
            $rollback = Join-Path $logDir 'FIX-12-connections-before.json'
            if (-not (Test-Path -LiteralPath $rollback)) { ConvertTo-Json -InputObject @($before) | Set-Content -LiteralPath $rollback -Encoding UTF8 }
            try { Remove-Printer -Name $c.Printer; Add-Action $c.Printer 'Remove' 'Done' }
            catch { $failed = $true; Add-Action $c.Printer 'Remove' "Failed: $($_.Exception.Message)" }
        }
    }

    $existing = @(Get-Printer | Select-Object -ExpandProperty Name)
    foreach ($conn in $AddConnection) {
        if ($existing -contains $conn) { continue }
        $server = ($conn.TrimStart('\') -split '\\')[0]
        if ($PSCmdlet.ShouldProcess($conn, 'Add printer connection')) {
            $reach = New-Object System.Net.Sockets.TcpClient
            try { $ok = $reach.ConnectAsync($server, 445).Wait(3000) } catch { $ok = $false } finally { $reach.Dispose() }
            if (-not $ok) { $failed = $true; Add-Action $conn 'Add' "Failed: print server $server not reachable on port 445"; continue }
            try { Add-Printer -ConnectionName $conn; Add-Action $conn 'Add' 'Done' }
            catch { $failed = $true; Add-Action $conn 'Add' "Failed (Point and Print policy or driver?): $($_.Exception.Message)" }
        }
    }

    if ($DefaultPrinter) {
        $winKey = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' -ErrorAction SilentlyContinue
        $legacy = $winKey -and $winKey.PSObject.Properties['LegacyDefaultPrinterMode'] -and $winKey.LegacyDefaultPrinterMode -eq 1
        $target = Get-CimInstance -ClassName Win32_Printer -Filter ("Name='{0}'" -f $DefaultPrinter.Replace('\', '\\').Replace("'", "\'"))
        if (-not $legacy) { Add-Action $DefaultPrinter 'Set default' 'Skipped: Windows manages the default printer for this user' }
        elseif (-not $target) { $failed = $true; Add-Action $DefaultPrinter 'Set default' 'Failed: printer not found' }
        elseif ($target.Default) { Add-Action $DefaultPrinter 'Set default' 'Already default' }
        elseif ($PSCmdlet.ShouldProcess($DefaultPrinter, 'Set as default printer')) {
            $rv = (Invoke-CimMethod -InputObject $target -MethodName SetDefaultPrinter).ReturnValue
            if ($rv -eq 0) { Add-Action $DefaultPrinter 'Set default' 'Done' } else { $failed = $true; Add-Action $DefaultPrinter 'Set default' "Failed: return $rv" }
        }
    }

    $after = Get-Connection
    foreach ($b in $before) { $b.Phase = 'Before' }
    foreach ($a in $after) { $a.Phase = 'After' }
    @($before) + @($actions) + @($after)
    if ($actions.Count -eq 0) { Write-Output 'No change.' }
    if ($failed) { exit 1 }
    exit 0
}
catch {
    Write-Output "FIX-12 failed: $($_.Exception.Message)"
    exit 1
}
