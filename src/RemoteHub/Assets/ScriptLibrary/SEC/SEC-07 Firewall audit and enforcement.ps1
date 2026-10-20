<#
.SYNOPSIS
    Audits Windows Firewall profiles and inbound rules, or enforces profile defaults with a timed rollback.
.DESCRIPTION
    Audit mode (default) reports profile state, default actions, logging, enabled inbound allow rules open
    to any address or any port, rules in sensitive groups (file and printer sharing, Remote Desktop) and
    the policy store each rule comes from. Enforce mode exports the configuration, schedules a rollback
    task that re-imports it after -RollbackMinutes unless confirmed with -ConfirmChange, then turns the
    profiles on, sets default actions and logging and disables the rules in -DisableRuleName. Rules that
    match -ProtectedRule are never touched, so the management path is kept. Group Policy rules override.
.NOTES
    RemoteHub: id=SEC-07; context=System; os=Both; risk=R1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Audit','Enforce')]
    [string]$Mode = 'Audit',
    [ValidateSet('Block','Allow')]
    [string]$DefaultInbound = 'Block',
    [ValidateSet('Block','Allow')]
    [string]$DefaultOutbound = 'Allow',
    [int]$LogMaxSizeKB = 16384,
    [string[]]$DisableRuleName = @(),
    [string[]]$ProtectedRule = @('RemoteDesktop-*', 'WINRM-HTTP-In-TCP*', 'RemoteHub*', 'CoreNet-*'),
    [int]$RollbackMinutes = 15,
    [switch]$ConfirmChange,
    [string]$ExportFolder = "$env:ProgramData\RemoteHub\ScriptLogs"
)

$ErrorActionPreference = 'Stop'
$taskName = 'RemoteHub SEC-07 firewall rollback'

function Test-Protected([string]$name) {
    foreach ($p in $ProtectedRule) { if ($name -like $p) { return $true } }
    return $false
}

try {
    if ($ConfirmChange) {
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($taskName, 'Remove pending rollback')) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
            [PSCustomObject]@{ Result = 'Change confirmed; rollback task removed' }
        } else { [PSCustomObject]@{ Result = 'No change: no pending rollback task' } }
        exit 0
    }

    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
    $findings = New-Object System.Collections.Generic.List[object]
    foreach ($p in $profiles) {
        if (-not [bool]$p.Enabled) { $findings.Add([PSCustomObject]@{ Severity = 'High'; Item = "Profile $($p.Name)"; Finding = 'Profile is off'; Store = '' }) }
        if ([string]$p.DefaultInboundAction -eq 'Allow') { $findings.Add([PSCustomObject]@{ Severity = 'High'; Item = "Profile $($p.Name)"; Finding = 'Default inbound action is Allow'; Store = '' }) }
        if ([string]$p.LogBlocked -ne 'True') { $findings.Add([PSCustomObject]@{ Severity = 'Low'; Item = "Profile $($p.Name)"; Finding = 'Dropped packets are not logged'; Store = '' }) }
    }

    # Filters share the rule's InstanceID, so one query per filter type is enough.
    $addr = @{}; Get-NetFirewallAddressFilter -PolicyStore ActiveStore | ForEach-Object { $addr[$_.InstanceID] = $_ }
    $port = @{}; Get-NetFirewallPortFilter -PolicyStore ActiveStore | ForEach-Object { $port[$_.InstanceID] = $_ }
    $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Action Allow -Enabled True)
    foreach ($r in $rules) {
        $a = $addr[$r.InstanceID]; $pf = $port[$r.InstanceID]
        $anyAddr = (-not $a) -or (@($a.RemoteAddress) -contains 'Any')
        $anyPort = (-not $pf) -or (@($pf.LocalPort) -contains 'Any')
        $store = [string]$r.PolicyStoreSourceType
        $sensitive = ($r.Name -like 'FPS-*' -or $r.Name -like 'RemoteDesktop-*')
        if ($anyAddr -and $anyPort -and -not $r.Program -and -not $r.Service) {
            $findings.Add([PSCustomObject]@{ Severity = 'High'; Item = $r.DisplayName; Finding = 'Allows any remote address on any port'; Store = $store })
        } elseif ($sensitive -and $anyAddr) {
            $findings.Add([PSCustomObject]@{ Severity = 'Medium'; Item = $r.DisplayName; Finding = 'Sensitive group open to any remote address'; Store = $store })
        } elseif ($anyAddr -and $anyPort) {
            $findings.Add([PSCustomObject]@{ Severity = 'Low'; Item = $r.DisplayName; Finding = 'Any address and port, limited to a program or service'; Store = $store })
        }
    }

    if ($Mode -eq 'Audit') {
        $findings | Sort-Object @{ Expression = { @{ High = 0; Medium = 1; Low = 2 }[$_.Severity] } }, Item
        [PSCustomObject]@{ Profiles = $profiles.Count; InboundAllowRules = $rules.Count; Findings = $findings.Count
            High = @($findings | Where-Object { $_.Severity -eq 'High' }).Count }
        if (@($findings | Where-Object { $_.Severity -eq 'High' }).Count -gt 0) { exit 1 }
        exit 0
    }

    # Enforce: work out the plan from the persistent (local) store, which is what this script can change.
    $plan = New-Object System.Collections.Generic.List[object]
    foreach ($p in @(Get-NetFirewallProfile -PolicyStore PersistentStore)) {
        $want = @{}
        if ([string]$p.Enabled -ne 'True') { $want.Enabled = 'True' }
        if ([string]$p.DefaultInboundAction -ne $DefaultInbound) { $want.DefaultInboundAction = $DefaultInbound }
        if ([string]$p.DefaultOutboundAction -ne $DefaultOutbound) { $want.DefaultOutboundAction = $DefaultOutbound }
        if ([string]$p.LogBlocked -ne 'True') { $want.LogBlocked = 'True' }
        if ($p.LogMaxSizeKilobytes -lt $LogMaxSizeKB) { $want.LogMaxSizeKilobytes = $LogMaxSizeKB }
        if ($want.Count -gt 0) { $plan.Add([PSCustomObject]@{ Kind = 'Profile'; Name = $p.Name; Settings = $want }) }
    }
    foreach ($n in $DisableRuleName) {
        if (Test-Protected $n) { Write-Warning "Rule '$n' is protected and is left alone."; continue }
        $rule = Get-NetFirewallRule -PolicyStore PersistentStore -Name $n -ErrorAction SilentlyContinue
        if (-not $rule) { Write-Verbose "Rule '$n' not found in the local store."; continue }
        if ([string]$rule.Enabled -eq 'True') { $plan.Add([PSCustomObject]@{ Kind = 'Rule'; Name = $n; Settings = @{ Enabled = 'False' } }) }
    }
    if ($plan.Count -eq 0) { [PSCustomObject]@{ Result = 'No change: firewall already matches the enforced settings' }; exit 0 }

    $export = $null
    if ($PSCmdlet.ShouldProcess('Windows Firewall', "Export configuration and apply $($plan.Count) change(s)")) {
        if (-not (Test-Path -LiteralPath $ExportFolder)) { $null = New-Item -ItemType Directory -Path $ExportFolder -Force }
        $export = Join-Path $ExportFolder ("SEC-07-firewall-{0:yyyyMMdd-HHmmss}.wfw" -f (Get-Date))
        $null = & netsh.exe advfirewall export "$export"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $export)) { throw 'Firewall export failed; nothing was changed.' }
        if ($RollbackMinutes -gt 0) {
            $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\netsh.exe" -Argument "advfirewall import `"$export`""
            $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes($RollbackMinutes)
            $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
            $null = Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Force
        }
    }
    $results = foreach ($item in $plan) {
        $target = "$($item.Kind) $($item.Name)"
        if (-not $PSCmdlet.ShouldProcess($target, ('Set ' + (($item.Settings.Keys | ForEach-Object { "$_=$($item.Settings[$_])" }) -join ', ')))) {
            [PSCustomObject]@{ Target = $target; Result = 'Planned' }; continue
        }
        $s = $item.Settings
        if ($item.Kind -eq 'Profile') { Set-NetFirewallProfile -PolicyStore PersistentStore -Name $item.Name @s }
        else { Disable-NetFirewallRule -PolicyStore PersistentStore -Name $item.Name }
        [PSCustomObject]@{ Target = $target; Result = 'Changed' }
    }
    $results
    if ($export) {
        $note = 'No automatic rollback was scheduled.'
        if ($RollbackMinutes -gt 0) { $note = "Rollback runs in $RollbackMinutes minutes unless this script is run with -ConfirmChange." }
        [PSCustomObject]@{ Export = $export; Rollback = $note }
    }
    exit 0
} catch {
    Write-Error "Firewall script failed: $($_.Exception.Message)" -ErrorAction Continue
    exit 1
}
