Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Config = $null
$script:Ns = 'root\virtualization\v2'
# the jump host route for the harness (Use-LabJump)
$script:JumpArgs = $null
$script:JumpAddress = $null

#region Config

# Reads lab.json and the shared lab password from lab.local.json next to it. Cached per session.
function Get-LabConfig {
    [CmdletBinding()]
    param(
        [string]$Path = (Join-Path $PSScriptRoot '..\lab.json'),
        [switch]$Force
    )
    if ($script:Config -and -not $Force) { return $script:Config }

    $Path = (Resolve-Path $Path).Path
    $cfg = Get-Content $Path -Raw | ConvertFrom-Json
    $localPath = Join-Path (Split-Path $Path) 'lab.local.json'
    if (-not (Test-Path $localPath)) {
        throw "Missing $localPath - copy lab.local.example.json to lab.local.json and set the lab password."
    }
    $local = Get-Content $localPath -Raw | ConvertFrom-Json
    $cfg | Add-Member -NotePropertyName Password -NotePropertyValue $local.Password -Force
    $script:Config = $cfg
    $cfg
}

# Lab VMs are found by name: "TestAutomation - <OS> - <version> - <language>", e.g.
# "TestAutomation - W11 - 26H2 - en-US" -> Id "W11-26H2-en-US". Accounts come from lab.json "Accounts"; an entry in
# lab.json "Vms" with the same Name overrides any value for that VM. -Name takes the Hyper-V name or the Id
# (wildcards allowed).
function Get-LabVm {
    [CmdletBinding()]
    param([string[]]$Name)
    $cfg = Get-LabConfig
    $overrides = @(if ($cfg.PSObject.Properties['Vms']) { $cfg.Vms })
    $all = @(foreach ($hv in Get-VM -Name $cfg.VmNamePattern | Sort-Object Name) {
            # "TestAutomation - W11 - 26H2 - en-US" (lane 1) or "... - en-US - 2" (a second VM of the same kind: lane 2).
            # BaseId is the kind (results are kept per kind), Id the VM itself.
            $parts = $hv.Name -split '\s+-\s+'
            if ($parts.Count -notin 4, 5) { continue }
            $base = $parts[1..3] -join '-'
            $lane = if ($parts.Count -eq 5) { $parts[4] } else { '1' }
            $vm = [ordered]@{
                Name = $hv.Name; Id = $(if ($lane -eq '1') { $base } else { "$base-$lane" }); BaseId = $base; Lane = $lane
                Os = $parts[1]; Version = $parts[2]; Language = $parts[3]
            }
            # Role: "Console" VMs are the admin PC (RemoteHub, the client); W11 / Server VMs are devices (the agent)
            $vm.Role = if ($parts[1] -eq 'Console') { 'Console' } else { 'Device' }
            foreach ($p in $cfg.Accounts.PSObject.Properties) { $vm[$p.Name] = $p.Value }
            foreach ($o in $overrides | Where-Object Name -eq $hv.Name) {
                foreach ($p in $o.PSObject.Properties) { $vm[$p.Name] = $p.Value }
            }
            [pscustomobject]$vm
        })
    if (-not $Name) { return $all }
    # the jump host (lab.json "JumpHost") only by its name or Id "JumpHost" - never part of a run's VM list
    $jumpCfg = if ($cfg.PSObject.Properties['JumpHost']) { $cfg.JumpHost } else { $null }
    foreach ($n in $Name) {
        if ($jumpCfg -and ($n -eq $jumpCfg.Name -or $n -eq 'JumpHost')) {
            $j = [ordered]@{ Name = $jumpCfg.Name; Id = 'JumpHost'; Os = ''; Version = ''; Language = '' }
            foreach ($p in $cfg.Accounts.PSObject.Properties) { $j[$p.Name] = $p.Value }
            # its own admin account when PSAdmin isn't an administrator there (lab.json JumpHost.AdminAccount)
            if ($jumpCfg.PSObject.Properties['AdminAccount']) { $j.AdminAccount = $jumpCfg.AdminAccount }
            [pscustomobject]$j
            continue
        }
        $found = @($all | Where-Object { $_.Name -like $n -or $_.Id -like $n })
        if ($found.Count -eq 0) { throw "No lab VM matches '$n' (pattern '$($cfg.VmNamePattern)')." }
        $found
    }
}

# The other VM a test needs next to its own: by default the device for a test on the admin PC - a Server VM. -Os W11 /
# Console for another kind. Inside a run it is the VM the scheduler kept for this file (RHLAB_DEVICES, "Server=<name>;
# W11=<name>" - a free one of any lane); outside a run the one of the same lane (lane 1: "... - en-US", lane 2:
# "... - en-US - 2"), else any. $null when the lab has none.
function Get-LabDevice {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$For, [string]$Os = 'Server')
    foreach ($pair in @("$env:RHLAB_DEVICES" -split ';' | Where-Object { $_ })) {
        $os1, $name = $pair -split '=', 2
        if ($os1 -eq $Os -and $name) { return Get-LabVm $name }
    }
    $me = Get-LabVm $For
    # only VMs ready for the lab (a checkpoint): one still being set up isn't a device yet
    $all = @(Get-LabVm | Where-Object { $_.Os -eq $Os -and $_.Name -ne $me.Name -and (Test-LabVmReady $_.Name) })
    @(@($all | Where-Object Lane -eq $me.Lane) + $all) | Select-Object -First 1   # (strict mode: [0] of an empty list throws)
}

# Whether a VM is ready for the lab: it has a checkpoint of its own to revert to (a VM still being set up has none; a
# run checkpoint doesn't count).
function Test-LabVmReady {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    [bool](Get-LabBaseCheckpoint -Name $Name)
}

# What a scenario file needs, from its header (see Invoke-LabTests.ps1): its role, whether it uses other VMs (LabNeeds:
# lane - found by itself when it calls Get-LabDevice) and of which kinds, the jump host, its time limit, whether a
# quick run still takes it on every kind (LabQuick: every kind), and whether it runs only after every other file of its
# kind (LabNeeds: last - D-ZAUDIT reads what the others left).
function Get-LabFileNeeds {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$File, [int]$DefaultLimitMin = 60)
    $text = [IO.File]::ReadAllText($File)
    $head = ($text -split "`n" | Select-Object -First 40) -join "`n"
    $role = if ($head -match '(?m)^#\s*LabRole:\s*(console|device|any)') { $Matches[1] }
            elseif ($text -match 'Install-LabClient|Start-LabClient') { 'console' } else { 'device' }
    $limit = $DefaultLimitMin
    if ($head -match '(?m)^#\s*TimeLimitMin:\s*(\d+)' -and [int]$Matches[1] -gt $limit) { $limit = [int]$Matches[1] }
    [pscustomobject]@{
        Role      = $role
        Lane      = ($head -match '(?m)^#\s*LabNeeds:.*\blane\b') -or ($text -match 'Get-LabDevice|Where-Object Os -eq .Server.')
        # the kinds of the other VMs it uses: Get-LabDevice -Os <kind> (Server when not given). None found (the kind is
        # worked out at run time): the rest of its own lane
        LaneOs    = @(@([regex]::Matches($text, 'Get-LabDevice[^\r\n]*?-Os\s+''?(\w+)') | ForEach-Object { $_.Groups[1].Value }) + @(if ($text -match 'Get-LabDevice(?![^\r\n]*-Os)' -or $text -match 'Where-Object Os -eq .Server.') { 'Server' }) | Select-Object -Unique)
        JumpHost  = ($head -match '(?m)^#\s*LabNeeds:.*\bjumphost\b') -or ($text -match 'Move-LabVmBehindJump|Use-LabJump|Start-LabJumpHost')
        Limit     = $limit
        EveryKind = $head -match '(?m)^#\s*LabQuick:\s*every kind'
        Last      = $head -match '(?m)^#\s*LabNeeds:.*\blast\b'
    }
}

# How long each file took on each kind in the last -Runs finished runs (the "done" lines of results\<run>\console.log):
# "<file>|<kind>" -> Median, Max (minutes) and Count. Runs where a file was ended at its time limit don't count. Only
# finished runs (done.json): the run going on writes its console.log, which can't be opened then - and a log that can't
# be read is left out, never a reason to fail the run. Only runs of whole files: a run with -Tag runs some of a file's
# cases, and its shorter times made the time limit too short for the whole file (run 20261021-222745: UI-RDP ended
# after 34 min, from single-case runs of 7-17 min).
function Get-LabFileTimes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ResultsRoot, [int]$Runs = 20)
    $all = @{}
    $whole = {
        param($dir)
        $json = Join-Path $dir 'run.json'
        if (-not (Test-Path $json)) { return $false }
        try { $r = Get-Content $json -Raw | ConvertFrom-Json } catch { return $false }
        -not ($r.PSObject.Properties['Tags'] -and @($r.Tags | Where-Object { $_ }).Count)
    }
    $dirs = @(Get-ChildItem $ResultsRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -match '^\d{8}-\d{6}$' |
            Where-Object { (Test-Path (Join-Path $_.FullName 'done.json')) -and (& $whole $_.FullName) } | Sort-Object Name -Descending | Select-Object -First $Runs)
    foreach ($d in $dirs) {
        $log = Join-Path $d.FullName 'console.log'
        if (-not (Test-Path $log)) { continue }
        # The run's own console.log is open for writing (Invoke-LabQueue's "*>"): ReadAllText's FileShare.Read refused it
        # and the run ended there with exit 1 and nothing in its log (runs 20261020-185111 and -185422).
        $text = try {
            $fs = [IO.FileStream]::new($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try { [IO.StreamReader]::new($fs).ReadToEnd() } finally { $fs.Dispose() }
        }
        catch { continue }
        foreach ($m in [regex]::Matches($text,'(?m)^\d\d:\d\d:\d\d done\s+(\S+) on (\S+?): .*\((\d+) min\)')) {
            # the kind: the VM's Id without its lane ("W11-26H2-en-US-2" -> "W11-26H2-en-US")
            $key = "$($m.Groups[1].Value)|$($m.Groups[2].Value -replace '-\d+$', '')"
            if (-not $all.ContainsKey($key)) { $all[$key] = [Collections.Generic.List[int]]::new() }
            $all[$key].Add([int]$m.Groups[3].Value)
        }
    }
    $times = @{}
    foreach ($k in $all.Keys) {
        $s = @($all[$k] | Sort-Object)
        $times[$k] = [pscustomobject]@{ Median = $s[[int][math]::Floor(($s.Count - 1) / 2)]; Max = $s[-1]; Count = $s.Count }
    }
    $times
}

# A file's time limit in this run: twice the longest it took before (at least 20 min), so a file that hangs is ended
# long before -FileTimeoutMin; never above its own limit, and that limit until it has run 3 times.
function Get-LabFileLimit {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Needs, $Time)
    if (-not $Time -or $Time.Count -lt 3) { return $Needs.Limit }
    [math]::Min($Needs.Limit, [math]::Max(20, 2 * $Time.Max))
}

# The scheduler's next start: of the file runs in -Queue that can start now on a free VM of -Vms, the one expected to
# take longest (-Times; a file never timed counts 10 min), so the long files don't end up in the tail of the run. A file
# that uses other VMs takes free ones of the kinds it names from -LabVms - of its own lane first, else of another, so
# one Console VM's file doesn't keep both Console VMs waiting for the same Server VM; with no kinds named, the rest of
# its own lane. -Busy: the names of the VMs that run or are held for a file. $null when nothing can start.
function Select-LabNextRun {
    [CmdletBinding()]
    param($Queue, $Vms, $LabVms, [string[]]$Busy = @(), [bool]$JumpBusy, [hashtable]$Times = @{})
    $free = @($LabVms | Where-Object { $Busy -notcontains $_.Name })
    $best = $null
    foreach ($item in @($Queue)) {
        if ($item.Needs.JumpHost -and $JumpBusy) { continue }
        # a "last" file waits until no other file of its kind is queued and no VM of its kind runs or is held for one
        # (run 20261021-222745: D-ZAUDIT started while the kind's other lane still ran files whose audit it checks)
        if ($item.PSObject.Properties['Needs'] -and $item.Needs.PSObject.Properties['Last'] -and $item.Needs.Last) {
            $others = @($Queue | Where-Object { $_.Kind -eq $item.Kind -and -not $_.Needs.Last })
            $busyOfKind = @($LabVms | Where-Object { $_.BaseId -eq $item.Kind -and $Busy -contains $_.Name })
            if ($others.Count -or $busyOfKind.Count) { continue }
        }
        $t = $Times["$($item.Name)|$($item.Kind)"]
        $min = if ($t) { $t.Median } else { 10 }
        if ($best -and $min -le $best.Minutes) { continue }
        foreach ($v in @($Vms | Where-Object { $_.BaseId -eq $item.Kind -and $Busy -notcontains $_.Name } | Sort-Object Lane)) {
            $partners = @()
            if ($item.Needs.Lane) {
                if (@($item.Needs.LaneOs).Count) {
                    foreach ($os in $item.Needs.LaneOs) {
                        $taken = @($partners | ForEach-Object Name)
                        $p = @($free | Where-Object { $_.Role -and $_.Os -eq $os -and $_.Name -ne $v.Name -and $taken -notcontains $_.Name } |
                                Sort-Object @{ e = { $_.Lane -ne $v.Lane } }, Lane) | Select-Object -First 1
                        if (-not $p) { $partners = $null; break }
                        $partners += $p
                    }
                }
                else {
                    $partners = @($LabVms | Where-Object { $_.Role -and $_.Lane -eq $v.Lane -and $_.Name -ne $v.Name })
                    if (@($partners | Where-Object { $Busy -contains $_.Name }).Count) { $partners = $null }
                }
                if ($null -eq $partners) { continue }
            }
            $best = [pscustomobject]@{ Item = $item; Vm = $v; Partners = @($partners); Minutes = $min }
            break
        }
    }
    $best
}

# Run checkpoints are named "RHLAB <run id> <state key>" (Initialize-LabVm takes them, Remove-LabRunCheckpoints removes
# them). They are never a base to revert to: install and upgrade tests must start from the user's own checkpoint.
function Test-LabRunCheckpointName([string]$Name) { $Name -like 'RHLAB *' }

# The VM's own checkpoint, the one Reset-LabVm reverts to: the newest that isn't a run checkpoint - a refreshed image is
# just a new checkpoint. $null when it has none.
function Get-LabBaseCheckpoint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Get-VMSnapshot -VMName (Get-LabVm $Name).Name -ErrorAction SilentlyContinue |
        Where-Object { -not (Test-LabRunCheckpointName $_.Name) } |
        Sort-Object CreationTime -Descending | Select-Object -First 1
}

# Whether a VM is the admin PC for RemoteHub (client) tests: a Console VM, or - in a lab without one - any client
# Windows (not Server), as before.
function Test-LabConsoleVm {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $vm = Get-LabVm $Name
    if ($vm.Role -eq 'Console') { return $true }
    $vm.Os -ne 'Server' -and -not @(Get-LabVm | Where-Object { $_.Role -eq 'Console' -and (Test-LabVmReady $_.Name) }).Count
}

# Every lab account uses the same password.
function Get-LabCredential {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$UserName)
    $secure = ConvertTo-SecureString (Get-LabConfig).Password -AsPlainText -Force
    New-Object System.Management.Automation.PSCredential ($UserName, $secure)
}

# 'Admin' (configuration, PS Direct), 'User' (standard user) and 'Console' (signed in at the checkpoint) map to the
# VM's accounts from lab.json; anything else is taken as a user name.
function Resolve-LabAccount {
    param([Parameter(Mandatory)]$Vm, [Parameter(Mandatory)][string]$Account)
    switch ($Account) {
        'Admin' { $Vm.AdminAccount }
        'User' { $Vm.UserAccount }
        'Console' { $Vm.ConsoleAccount }
        default { $Account }
    }
}

#endregion

#region VM

function Reset-LabVm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Name,
        [int]$TimeoutSec = 300
    )
    foreach ($vm in Get-LabVm $Name) {
        # always the VM's own newest checkpoint, never a run checkpoint (Get-LabBaseCheckpoint)
        $snap = Get-LabBaseCheckpoint -Name $vm.Name
        if (-not $snap) { throw "$($vm.Name): no checkpoint to revert to." }
        Write-Verbose "$($vm.Name): reverting to checkpoint '$($snap.Name)' ($($snap.CreationTime))"
        # closed while the guest can still end them; the revert would leave them broken
        Clear-LabSessionCache -Name $vm.Name
        Restore-VMSnapshot -VMSnapshot $snap -Confirm:$false
        Clear-LabVmState -Name $vm.Name
        # a VM a jump host test left behind the jump host: back on its own switch
        Restore-LabVmNetwork -Name $vm.Name
        if ((Get-VM -Name $vm.Name).State -ne 'Running') { Start-VM -Name $vm.Name }
        Wait-LabVm -Name $vm.Name -TimeoutSec $TimeoutSec
    }
}

# ------------------------------------------------------------------ reusing a VM between scenario files
# A scenario file that needs "the agent (and/or RemoteHub) installed, an account signed in" calls Initialize-LabVm
# instead of Reset-LabVm + Install-LabAgent + Set-LabAutoLogon. With reuse on (the runner sets RHLAB_REUSE=1) the VM is
# only reverted when it isn't in that state already: the state of the last Initialize-LabVm is kept on the host
# (LabRoot\state\<Id>.json, with the agent's pin) and as a token in the guest (a revert loses it). Anything that changes
# the VM outside it - Reset-LabVm, a direct Install-LabAgent / Install-LabClient / Set-LabAutoLogon, a failed file (the
# runner) - drops the state, so the next file starts from the checkpoint. Before reuse the VM is checked: agent service
# running and answering with the pin, the policies as they were, the signed-in account's desktop up (no lock / sign-in
# screen), no RemoteHub left running.
#
# Run checkpoints (the runner sets RHLAB_SNAPSHOTS=1 and RHLAB_RUN_ID; off with -NoSnapshots, -NoReuse or
# RHLAB_SNAPSHOTS=0): the first time in a run that Initialize-LabVm builds a state on a VM, it takes a Hyper-V checkpoint
# of the running VM right after building it, before the file changes anything - "RHLAB <run id> <state key>". The key is
# the options plus the MSIs (name, size, time) plus the VM's own checkpoint, so a different build, option set or image
# never matches. LabRoot\state\run-checkpoints\<Id>\<key>.json keeps the state record of that moment (pin, install
# answer, guest token, policies); the token itself is inside the checkpoint. When a later file needs that state and the
# VM isn't in it (a failed file, Set-LabVmDirty, an install test, another state in between), the VM is reverted to the
# run checkpoint instead of installing again: the record becomes the VM's state, the guest clock is brought up to date,
# and the same check as for a reused VM decides. A checkpoint that fails the check is deleted and the state is built the
# full way (and checkpointed again; after two failures not any more in this run). Every state gets one, -Clean files
# included (they only never start from one): there are few per VM, and a file with an unusual combination of options
# pays for one checkpoint at most. Checkpoints are per VM, and a VM runs one file at a time, so lanes need no locking.
# Reset-LabVm and Test-LabVmReady ignore run checkpoints; the runner removes them at the end of the run and at the start
# of the next (a cancelled run leaves them behind) - Remove-LabRunCheckpoints.

function Get-LabVmStatePath([string]$Name) { Join-Path (Get-LabConfig).LabRoot "state\$((Get-LabVm $Name).Id).json" }

# Forgets what Initialize-LabVm set up on the VM: the next Initialize-LabVm reverts it.
function Clear-LabVmState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Remove-Item (Get-LabVmStatePath $Name) -ErrorAction SilentlyContinue
}

# A file that changed the VM in a way the next one must not see (a policy, an account, a driver): its state is dropped.
function Set-LabVmDirty {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Clear-LabVmState -Name $Name
}

$script:LabInitializing = $false

# Reverts and installs as asked, or - reuse on and the VM already so - keeps it. Returns Pin (of the agent), Install
# (Install-LabAgent's answer, from the first install when reused), Reused, Why (why it reverted).
# The person's first call in a session starts its server, compiles its helper and loads the OCR - 20-40 s. Done when a
# file's VM is handed over, so the first real step (often a consent dialog with a 30 s countdown) isn't the slow one.
# Best effort: a failure here shows up again, with its reason, at the file's own first call.
function Start-LabPersonWarm {
    param([Parameter(Mandatory)][string]$Name, [string]$AutoLogon)
    $account = $(if ($AutoLogon) { $AutoLogon } else { 'Console' })
    try {
        $null = Invoke-LabPerson -Name $Name -Do windows -Account $account -TimeoutSec 120
        $null = Invoke-LabPerson -Name $Name -Do screen-read -Account $account -TimeoutSec 120
    }
    catch { Write-Verbose "${Name}: warming up the person in $account's session failed: $($_.Exception.Message)" }
}

function Initialize-LabVm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        # the agent: installed as Install-LabAgent does by default (plus -AutoSignIn / -VirtualDisplay)
        [switch]$Agent,
        [Nullable[bool]]$AutoSignIn,
        [bool]$VirtualDisplay = $true,
        # RemoteHub (the client): Install-LabClient, -ClientNetwork = its -Network
        [switch]$Client,
        [switch]$ClientNetwork,
        # the account signed in on the console: User (normaluser), Console (Admin) or '' (none set)
        [string]$AutoLogon = '',
        # always from the checkpoint (the file changes the installation itself)
        [switch]$Clean
    )
    $vm = Get-LabVm $Name
    $agentMsi = if ($Agent) { Get-Item (Get-LabAgentMsi) } else { $null }
    $clientMsi = if ($Client) { Get-Item (Get-LabClientMsi) } else { $null }
    $want = [ordered]@{
        Agent      = $(if ($Agent) { "$($agentMsi.Name)|$($agentMsi.Length)|$($agentMsi.LastWriteTimeUtc.Ticks)|$AutoSignIn|$VirtualDisplay" } else { '' })
        Client     = $(if ($Client) { "$($clientMsi.Name)|$($clientMsi.Length)|$($clientMsi.LastWriteTimeUtc.Ticks)|$($ClientNetwork.IsPresent)" } else { '' })
        AutoLogon  = $AutoLogon
    }
    # the run checkpoint of this state (see "Run checkpoints" above): only inside a runner's run with reuse on
    $snap = $null
    if ($env:RHLAB_REUSE -eq '1' -and $env:RHLAB_SNAPSHOTS -eq '1' -and $env:RHLAB_RUN_ID) {
        $base = Get-LabBaseCheckpoint -Name $Name
        if ($base) {
            $key = Get-LabRunCheckpointKey -Want $want -BaseId "$($base.Id)"
            $snap = [pscustomobject]@{
                Name = "RHLAB $($env:RHLAB_RUN_ID) $key"
                Record = Join-Path (Get-LabRunCheckpointDir $Name) "$key.json"
                Failed = Join-Path (Get-LabRunCheckpointDir $Name) "$key.failed"
                Bad = $false
            }
        }
    }
    $why = $null
    if ($Clean) { $why = 'the file needs a clean VM' }
    elseif ($env:RHLAB_REUSE -ne '1') { $why = 'reuse is off' }
    else {
        $path = Get-LabVmStatePath $Name
        $have = if (Test-Path $path) { try { Get-Content $path -Raw | ConvertFrom-Json } catch { $null } } else { $null }
        if (-not $have) { $why = 'no state from an earlier file' }
        elseif ($have.Agent -ne $want.Agent -or $have.Client -ne $want.Client -or $have.AutoLogon -ne $want.AutoLogon) { $why = 'set up differently' }
        else { $why = Test-LabVmState -Name $Name -State $have }
        if (-not $why) {
            Write-Verbose "${Name}: reused (set up by an earlier file)"
            Start-LabPersonWarm -Name $Name -AutoLogon $AutoLogon
            return [pscustomobject]@{ Pin = $have.Pin; Install = $have.Install; Reused = $true; Why = $null }
        }
        if ($snap) {
            $r = Restore-LabRunCheckpoint -Name $Name -Snapshot $snap
            if ($r.Tried -and -not $r.Why) {
                Write-Verbose "${Name}: reverted to the run checkpoint '$($snap.Name)' ($why)"
                Start-LabPersonWarm -Name $Name -AutoLogon $AutoLogon
                return [pscustomobject]@{ Pin = $r.State.Pin; Install = $r.State.Install; Reused = $true; Why = "$why - from the run checkpoint" }
            }
            if ($r.Tried) {
                Write-Warning "${Name}: the run checkpoint '$($snap.Name)' didn't pass the check ($($r.Why)) - installing again, the checkpoint is replaced"
                $why = "$why; the run checkpoint failed ($($r.Why))"
                $snap.Bad = $true
            }
        }
    }
    Write-Verbose "${Name}: from the checkpoint - $why"
    $script:LabInitializing = $true
    try {
        Reset-LabVm -Name $Name
        # only now: the VM no longer derives from the failed run checkpoint, so removing it is quick (nothing to merge)
        if ($snap -and $snap.Bad) { Remove-LabRunCheckpointOne -Name $Name -Snapshot $snap }
        $install = $null; $pin = $null
        if ($Agent) {
            $a = @{ Name = $Name; VirtualDisplay = $VirtualDisplay }
            if ($null -ne $AutoSignIn) { $a.AutoSignIn = $AutoSignIn }
            $install = Install-LabAgent @a
            $pin = $install.pin
        }
        if ($Client) { $null = Install-LabClient -Name $Name -Network:$ClientNetwork }
        if ($AutoLogon) { Set-LabAutoLogon -Name $Name -Account $AutoLogon }
        if ($Agent) { $null = Wait-LabAgent -Name $Name -Pin $pin }
        $token = [guid]::NewGuid().ToString('N')
        $policies = Invoke-LabGuest -Name $Name -ArgumentList $token -ScriptBlock {
            param($t)
            $null = New-Item -ItemType Directory -Path 'C:\ProgramData\RemoteHubLab' -Force
            Set-Content 'C:\ProgramData\RemoteHubLab\state-token.txt' $t
            (Get-ChildItem 'HKLM:\SOFTWARE\Policies\RemoteHub' -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $k = $_; $k.Name + '=' + (($k.Property | Sort-Object | ForEach-Object { "$_=$($k.GetValue($_))" }) -join ';') }) -join '|'
        }
        $state = [ordered]@{ Vm = $vm.Name }
        foreach ($k in $want.Keys) { $state[$k] = $want[$k] }
        $state.Pin = $pin; $state.Install = $install; $state.Token = $token; $state.Policies = "$policies"; $state.Since = (Get-Date).ToString('s')
        $path = Get-LabVmStatePath $Name
        $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
        $state | ConvertTo-Json -Depth 6 | Set-Content $path -Encoding UTF8
        # before the checkpoint: a revert to it then has the person's server running already
        Start-LabPersonWarm -Name $Name -AutoLogon $AutoLogon
        # taken before the file changes anything: the next file that needs this state reverts to it instead of installing
        if ($snap) { New-LabRunCheckpoint -Name $Name -Snapshot $snap -State $state }
        [pscustomobject]@{ Pin = $pin; Install = $install; Reused = $false; Why = $why }
    }
    finally { $script:LabInitializing = $false }
}

# Where the run checkpoints' records of a VM are kept (one folder per VM: a lane-2 Id starts with the lane-1 Id).
function Get-LabRunCheckpointDir([string]$Name) { Join-Path (Get-LabConfig).LabRoot "state\run-checkpoints\$((Get-LabVm $Name).Id)" }

# The state key in a run checkpoint's name: readable parts plus a hash of everything that makes the state - the options
# and MSIs (Initialize-LabVm's $want) and the VM's own checkpoint it was built from.
function Get-LabRunCheckpointKey {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Want, [string]$BaseId)
    $text = (@($Want.Keys | ForEach-Object { "$_=$($Want[$_])" }) + "Base=$BaseId") -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = -join @($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | Select-Object -First 5 | ForEach-Object { $_.ToString('x2') }) }
    finally { $sha.Dispose() }
    $parts = @(if ($Want['Agent']) { 'agent' }; if ($Want['Client']) { 'client' }; if ($Want['AutoLogon']) { "$($Want['AutoLogon'])".ToLowerInvariant() })
    if (-not $parts.Count) { $parts = @('none') }
    ($parts -join '_') + '-' + $hash
}

# Takes the run checkpoint of the state the VM is in now and writes its record. A checkpoint that can't be taken (disk
# space, a merge still running) only costs the time saving: warned, the run goes on.
function New-LabRunCheckpoint {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)]$State)
    $hv = (Get-LabVm $Name).Name
    if (@(Get-VMSnapshot -VMName $hv -Name $Snapshot.Name -ErrorAction SilentlyContinue).Count) { return }
    # failed twice in this run (reverted, then the check failed): this state is installed the full way from now on
    if ((Test-Path $Snapshot.Failed) -and @(Get-Content $Snapshot.Failed).Count -ge 2) { return }
    try {
        Write-Verbose "${Name}: taking the run checkpoint '$($Snapshot.Name)'"
        # no kept PowerShell Direct session inside the checkpoint: after a revert its guest end would wait for a
        # connection that is gone
        Clear-LabSessionCache -Name $hv
        Checkpoint-VM -Name $hv -SnapshotName $Snapshot.Name
        $cp = Get-VMSnapshot -VMName $hv -Name $Snapshot.Name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $cp) { throw 'it is not there after taking it' }
        $null = New-Item -ItemType Directory -Path (Split-Path $Snapshot.Record) -Force
        [ordered]@{
            RunId = $env:RHLAB_RUN_ID; Checkpoint = $Snapshot.Name; CheckpointId = "$($cp.Id)"; Type = "$($cp.SnapshotType)"
            Created = (Get-Date).ToString('s'); State = $State
        } | ConvertTo-Json -Depth 8 | Set-Content $Snapshot.Record -Encoding UTF8
    }
    catch { Write-Warning "${Name}: no run checkpoint '$($Snapshot.Name)' ($($_.Exception.Message)) - the next file that needs this state installs again" }
}

# Removes one run checkpoint and its record, and counts the failure (New-LabRunCheckpoint stops after two).
function Remove-LabRunCheckpointOne {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Snapshot)
    Add-Content -Path $Snapshot.Failed -Value (Get-Date).ToString('s') -Encoding ASCII
    Remove-Item $Snapshot.Record -ErrorAction SilentlyContinue
    try { Get-VMSnapshot -VMName (Get-LabVm $Name).Name -Name $Snapshot.Name -ErrorAction SilentlyContinue | Remove-VMSnapshot -Confirm:$false }
    catch { Write-Warning "${Name}: removing the run checkpoint '$($Snapshot.Name)' failed ($($_.Exception.Message)) - Remove-LabRunCheckpoints takes it at the end of the run" }
}

# Reverts the VM to the run checkpoint of a state and makes it count as that state again. Answers Tried (there was a
# checkpoint with a record of this run), Why ($null: the VM passed the same check as a reused VM) and State (the record).
function Restore-LabRunCheckpoint {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Snapshot)
    $no = [pscustomobject]@{ Tried = $false; Why = $null; State = $null }
    if (-not (Test-Path $Snapshot.Record)) { return $no }
    $rec = try { Get-Content $Snapshot.Record -Raw | ConvertFrom-Json } catch { $null }
    if (-not $rec -or $rec.RunId -ne $env:RHLAB_RUN_ID -or $rec.Checkpoint -ne $Snapshot.Name) { return $no }
    $hv = (Get-LabVm $Name).Name
    $cp = Get-VMSnapshot -VMName $hv -Name $Snapshot.Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cp) { Remove-Item $Snapshot.Record -ErrorAction SilentlyContinue; return $no }
    $state = $rec.State
    $why = $null
    try {
        Write-Verbose "${Name}: reverting to the run checkpoint '$($Snapshot.Name)'"
        Clear-LabSessionCache -Name $hv
        Restore-VMSnapshot -VMSnapshot $cp -Confirm:$false
        Clear-LabVmState -Name $Name
        Restore-LabVmNetwork -Name $Name
        # a checkpoint with memory comes back saved (Start-VM resumes it); a production checkpoint comes back off and
        # boots, so the sign-in and the agent need longer - the waits below cover both
        if ((Get-VM -Name $hv).State -ne 'Running') { Start-VM -Name $hv }
        Wait-LabVm -Name $Name -TimeoutSec 300
        if ($state.AutoLogon) { Wait-LabDesktop -Name $Name -Account $state.AutoLogon -TimeoutSec 300 }
        if ($state.Agent) { $null = Wait-LabAgent -Name $Name -Pin $state.Pin -TimeoutSec 240 }
        Sync-LabGuestClock -Name $Name
        $state.Since = (Get-Date).ToString('s')
        $path = Get-LabVmStatePath $Name
        $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
        $state | ConvertTo-Json -Depth 6 | Set-Content $path -Encoding UTF8
        $why = Test-LabVmState -Name $Name -State $state
    }
    catch { $why = $_.Exception.Message }
    if ($why) { Clear-LabVmState -Name $Name }
    [pscustomobject]@{ Tried = $true; Why = $why; State = $state }
}

# After a revert to a checkpoint with memory the guest's clock stands where it was when the checkpoint was taken (tickets,
# certificates and "since" times compare it with the host's). Hyper-V's time sync corrects it shortly after the resume;
# when it doesn't within -TimeoutSec the clock is set from the host's.
function Sync-LabGuestClock {
    param([Parameter(Mandatory)][string]$Name, [int]$TimeoutSec = 60, [int]$ToleranceSec = 5)
    $skew = { [math]::Abs(([long](Invoke-LabGuest -Name $Name -ScriptBlock { [DateTime]::UtcNow.Ticks }) - [DateTime]::UtcNow.Ticks) / 1e7) }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $off = & $skew
    while ($off -gt $ToleranceSec -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3; $off = & $skew }
    if ($off -le $ToleranceSec) { return }
    Write-Verbose "${Name}: guest clock $([int]$off) s off after the revert - setting it from the host"
    Invoke-LabGuest -Name $Name -ArgumentList ([DateTime]::UtcNow.Ticks) -ScriptBlock {
        param($t) $null = Set-Date -Date ([DateTime]::new([long]$t, [DateTimeKind]::Utc).ToLocalTime())
    }
    $off = & $skew
    if ($off -gt $ToleranceSec * 2) { throw "${Name}: the guest clock is still $([int]$off) s off the host's" }
}

# Removes every run checkpoint ("RHLAB ...") of the lab VMs (all of them without -Name) and their records. The runner
# calls it at the end of a run and at the start of the next one. A VM that derived from a removed checkpoint keeps
# running as it is (Hyper-V merges it in the background); its reuse state is dropped, so its next file starts from the
# VM's own checkpoint. Waits at most -MergeTimeoutSec for the merges (0: not at all) - a revert during a merge fails.
function Remove-LabRunCheckpoints {
    [CmdletBinding()]
    param([string[]]$Name, [int]$MergeTimeoutSec = 120)
    $vms = @(if ($Name) { Get-LabVm $Name } else { Get-LabVm })
    $jobs = @()
    foreach ($vm in $vms) {
        # newest first and one at a time per VM: two run checkpoints of one VM are parent and child, and removing the
        # second while the first one's merge runs fails with "cannot be performed while the object is in its current
        # state" (run 20261017-054704 left 6 of 18 behind) - such a removal is retried once the merge is done
        $cps = @(Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue | Where-Object { Test-LabRunCheckpointName $_.Name } |
                Sort-Object CreationTime -Descending)
        foreach ($cp in $cps) {
            Write-Verbose "$($vm.Name): removing the run checkpoint '$($cp.Name)'"
            $until = (Get-Date).AddSeconds([Math]::Max(60, $MergeTimeoutSec))
            while ($true) {
                try { Remove-VMSnapshot -VMSnapshot $cp -Confirm:$false -ErrorAction Stop; break }
                catch {
                    if ("$($_.Exception.Message)" -like '*current state*' -and (Get-Date) -lt $until) { Start-Sleep -Seconds 10; continue }
                    Write-Warning "$($vm.Name): removing '$($cp.Name)' failed: $($_.Exception.Message)"
                    break
                }
            }
        }
        $dir = Get-LabRunCheckpointDir $vm.Name
        $records = Test-Path $dir
        if ($records) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
        if ($cps.Count -or $records) { Clear-LabVmState -Name $vm.Name }
        [pscustomobject]@{ Vm = $vm.Id; Removed = $cps.Count }
    }
    $jobs = @($jobs | Where-Object { $_ })
    if ($MergeTimeoutSec -le 0) { return }
    $deadline = (Get-Date).AddSeconds($MergeTimeoutSec)
    if ($jobs.Count) {
        $null = Wait-Job -Job $jobs -Timeout $MergeTimeoutSec
        foreach ($j in $jobs | Where-Object { $_.State -ne 'Running' }) {
            try { $null = Receive-Job -Job $j -ErrorAction Stop } catch { Write-Warning "Removing a run checkpoint: $($_.Exception.Message)" }
            Remove-Job -Job $j -Force
        }
    }
    # Hyper-V may still merge after the removal is done (and an earlier run's removal may still be merging)
    $merging = @()
    do {
        $merging = @($vms | Where-Object {
                $v = Get-VM -Name $_.Name -ErrorAction SilentlyContinue
                $v -and ((@($v.OperationalStatus) | ForEach-Object { "$_" }) -contains 'MergingDisks' -or "$($v.Status)" -like '*erg*')
            })
        if (-not $merging.Count) { return }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    Write-Warning "Still merging after $MergeTimeoutSec s: $(($merging | ForEach-Object Id) -join ', ') - Hyper-V finishes it in the background"
}

# Why the VM can't be reused as Initialize-LabVm left it ($null: it can). Also closes a RemoteHub left running.
function Test-LabVmState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$State)
    if ((Get-VM -Name (Get-LabVm $Name).Name).State -ne 'Running') { return 'the VM is off' }
    $user = if ($State.AutoLogon) { Resolve-LabAccount (Get-LabVm $Name) $State.AutoLogon } else { '' }
    $g = try {
        Invoke-LabGuest -Name $Name -ArgumentList $State.Token, $user, [bool]$State.Agent -ScriptBlock {
            param($token, $user, $agent)
            $r = [ordered]@{}
            $r.token = (Get-Content 'C:\ProgramData\RemoteHubLab\state-token.txt' -ErrorAction SilentlyContinue | Select-Object -First 1) -eq $token
            $r.service = if ($agent) { [string](Get-Service RemoteHubAgent -ErrorAction SilentlyContinue).Status } else { 'n/a' }
            $r.policies = (Get-ChildItem 'HKLM:\SOFTWARE\Policies\RemoteHub' -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $k = $_; $k.Name + '=' + (($k.Property | Sort-Object | ForEach-Object { "$_=$($k.GetValue($_))" }) -join ';') }) -join '|'
            $locked = @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count
            $desk = if ($user) {
                @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $user }).Count -gt 0
            } else { $true }
            $r.desktop = $desk -and $locked -eq 0
            # a RemoteHub (client) a file left open: closed, it would answer the next file's first UI step
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            [pscustomobject]$r
        }
    }
    catch { return "the guest doesn't answer ($($_.Exception.Message))" }
    if (-not $g.token) { return 'reverted since (the token is gone)' }
    if ($State.Agent -and $g.service -ne 'Running') { return "the agent service is $($g.service)" }
    if ("$($g.policies)" -ne "$($State.Policies)") { return 'the RemoteHub policies changed' }
    if (-not $g.desktop) { return "no unlocked desktop for $user" }
    if ($State.Agent) {
        try { $null = Wait-LabAgent -Name $Name -Pin $State.Pin -TimeoutSec 30 } catch { return "the agent doesn't answer ($($_.Exception.Message))" }
    }
    $null
}

# Ready = heartbeat OK and PowerShell Direct answers as the admin account twice in a row - right after a revert a
# busy guest may answer once and then drop the next connections.
function Wait-LabVm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSec = 300
    )
    $vm = Get-LabVm $Name
    # called after the VM changed (started, reverted, restarted - also by tests directly): kept sessions are stale
    $vm | ForEach-Object { Clear-LabSessionCache -Name $_.Name }
    $cred = Get-LabCredential $vm.AdminAccount
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = 'no heartbeat'
    $ok = 0
    while ((Get-Date) -lt $deadline) {
        $hb = [string](Get-VM -Name $Name).Heartbeat
        if ($hb -like 'Ok*') {
            try {
                $null = Invoke-Command -VMName $Name -Credential $cred -ScriptBlock { 1 } -ErrorAction Stop
                if (++$ok -ge 2) { return }
                continue
            }
            catch { $last = $_.Exception.Message; $ok = 0 }
        }
        else { $last = "heartbeat $hb"; $ok = 0 }
        Start-Sleep -Seconds 3
    }
    throw "${Name}: guest not ready after $TimeoutSec s ($last)."
}

# Opens a PowerShell Direct session, retrying while the guest drops connections (busy after a revert). Only the
# connection is retried, never a command - nothing has run in the guest when New-PSSession fails.
function New-LabSession {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][pscredential]$Credential, [int]$Attempts = 5)
    # an Id ('W11-26H2-en-US', 'JumpHost') to the Hyper-V name
    $Name = @(Get-LabVm $Name)[0].Name
    for ($i = 1; ; $i++) {
        try { return New-PSSession -VMName $Name -Credential $Credential -ErrorAction Stop }
        catch {
            if ($i -ge $Attempts) { throw "${Name}: PowerShell Direct failed $Attempts times: $($_.Exception.Message)" }
            Write-Verbose "${Name}: PowerShell Direct attempt $i failed ($($_.Exception.Message)) - retrying"
            Start-Sleep -Seconds 5
        }
    }
}

# Switches lab VMs off (all of them without -Name). Nothing is lost: every scenario reverts to the checkpoint first.
# Only VMs matching the lab name pattern are touched.
function Stop-LabVm {
    [CmdletBinding()]
    param([string[]]$Name)
    foreach ($vm in @(if ($Name) { Get-LabVm $Name } else { Get-LabVm })) {
        Clear-LabSessionCache -Name $vm.Name
        if ((Get-VM -Name $vm.Name).State -ne 'Off') {
            Write-Verbose "$($vm.Name): turning off"
            Stop-VM -Name $vm.Name -TurnOff -Force
        }
    }
    # all of them: the jump host too (only the jump host tests start it)
    if (-not $Name) { Stop-LabJumpHost }
}

# Restarts the guest OS and returns once it is back with a new boot time.
function Restart-LabVm {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSec = 300
    )
    $bootTime = { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o') }
    $before = Invoke-LabGuest -Name $Name -ScriptBlock $bootTime
    # the session may break while Windows goes down - the new boot time below is the real check
    try { Invoke-LabGuest -Name $Name -ScriptBlock { shutdown.exe /r /t 0 /f | Out-Null } } catch { Write-Verbose $_.Exception.Message }
    Clear-LabSessionCache -Name $Name

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = 'still the old boot'
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        try {
            $left = [int][math]::Max(5, ($deadline - (Get-Date)).TotalSeconds)
            Wait-LabVm -Name $Name -TimeoutSec $left
            if ((Invoke-LabGuest -Name $Name -ScriptBlock $bootTime) -ne $before) { return }
        }
        catch { $last = $_.Exception.Message }
    }
    throw "${Name}: guest did not come back after restart within $TimeoutSec s ($last)."
}

function Get-LabVmAddress {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSec = 120
    )
    $Name = (Get-LabVm $Name).Name
    # an address in the jump host's network only when the VM has no other (it sits behind the jump host then)
    $inner = Get-LabJumpPrefix
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $ips = @(Get-VMNetworkAdapter -VMName $Name | ForEach-Object { $_.IPAddresses } |
            Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' -and $_ -notlike '169.254.*' } |
            Sort-Object { if ($inner -and $_.StartsWith($inner)) { 1 } else { 0 } })
        if ($ips.Count -gt 0) { return $ips[0] }
        Start-Sleep -Seconds 3
    }
    throw "${Name}: no IPv4 address reported by Hyper-V after $TimeoutSec s."
}

# Cuts the VM's network adapters from their switches (the guest sees the cable pulled; PowerShell Direct still works)
# and answers adapter (MAC address) -> switch, for Connect-LabNetwork. A revert to the checkpoint reconnects them as well.
# -ExceptSwitch: adapters on that switch stay (the jump host keeps its side towards the targets).
# Keyed by MAC, not by name: Hyper-V names every adapter "Network Adapter", so by name the jump host's inner adapter
# (left connected) was moved onto the outer switch by Connect-LabNetwork and the targets behind it were cut off.
function Disconnect-LabNetwork {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$ExceptSwitch)
    $Name = (Get-LabVm $Name).Name
    $map = @{}
    foreach ($a in Get-VMNetworkAdapter -VMName $Name | Where-Object { -not $ExceptSwitch -or $_.SwitchName -ne $ExceptSwitch }) {
        if ($a.SwitchName) { $map[$a.MacAddress] = $a.SwitchName }
        Disconnect-VMNetworkAdapter -VMNetworkAdapter $a
    }
    $map
}

function Connect-LabNetwork {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][hashtable]$Switches)
    $Name = (Get-LabVm $Name).Name
    foreach ($a in Get-VMNetworkAdapter -VMName $Name) {
        if ($Switches.ContainsKey($a.MacAddress)) { Connect-VMNetworkAdapter -VMNetworkAdapter $a -SwitchName $Switches[$a.MacAddress] }
    }
}

#endregion

#region Jump host

# The jump host topology (lab.json "JumpHost"): the jump VM has one adapter where the host reaches it and one on a
# private switch; a test VM is moved onto that switch with a static address, so the host can reach it only through the
# jump host's RemoteHub Agent. Built at run time on the provided VMs - a revert undoes it, and Reset-LabVm puts back a
# switch the checkpoint doesn't know.

function Get-LabJumpPrefix {
    $cfg = Get-LabConfig
    if (-not $cfg.PSObject.Properties['JumpHost']) { return $null }
    # "192.168.231." for a /24
    (($cfg.JumpHost.Address -split '\.')[0..2] -join '.') + '.'
}

# Reverts the jump VM, waits for it, installs the agent on it (scope: the host) and answers what the harness needs to go
# through it: Name, Address (where the host reaches it), Inner (its address on the jump switch), Pin, User.
function Start-LabJumpHost {
    [CmdletBinding()]
    param([int]$TimeoutSec = 300)
    $cfg = (Get-LabConfig).JumpHost
    if (-not $cfg) { throw 'lab.json has no "JumpHost".' }
    Reset-LabVm -Name $cfg.Name -TimeoutSec $TimeoutSec
    $inner = Invoke-LabGuest -Name $cfg.Name -ArgumentList $cfg.Address -ScriptBlock {
        param($a) @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object IPAddress -eq $a).Count
    }
    if (-not $inner) { throw "$($cfg.Name): has no address $($cfg.Address) on the jump switch - check the checkpoint." }
    $install = Install-LabAgent -Name $cfg.Name -Label 'JumpHost'
    $vm = Get-LabVm $cfg.Name
    [pscustomobject]@{
        Name = $cfg.Name; Address = (Get-LabVmAddress -Name $cfg.Name); Inner = $cfg.Address
        Pin = $install.pin; DeviceId = $install.deviceId; User = $vm.AdminAccount
    }
}

function Stop-LabJumpHost {
    [CmdletBinding()]
    param()
    $cfg = (Get-LabConfig).JumpHost
    if ($cfg) { Clear-LabSessionCache -Name $cfg.Name }
    if ($cfg -and (Get-VM -Name $cfg.Name -ErrorAction SilentlyContinue).State -ne 'Off') { Stop-VM -Name $cfg.Name -TurnOff -Force }
}

# Moves a test VM behind the jump host: its adapters onto the jump switch (the old switches are written down for
# Restore-LabVmNetwork), a static address <prefix>.<FirstTarget + index of the VM>, no gateway, no DNS. Answers the
# address.
function Move-LabVmBehindJump {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $cfg = (Get-LabConfig).JumpHost
    $vm = Get-LabVm $Name
    $index = [array]::IndexOf(@(Get-LabVm | ForEach-Object Name), $vm.Name)
    $address = "$(Get-LabJumpPrefix)$([int]$cfg.FirstTarget + [math]::Max(0, $index))"
    $record = Join-Path (Get-LabConfig).LabRoot "network\$($vm.Id).json"
    $null = New-Item -ItemType Directory -Path (Split-Path $record) -Force
    $adapters = @(Get-VMNetworkAdapter -VMName $vm.Name)
    if (-not (Test-Path $record)) {
        @($adapters | ForEach-Object {
                $v = Get-VMNetworkAdapterVlan -VMNetworkAdapter $_
                [pscustomobject]@{ Name = $_.Name; Mac = $_.MacAddress; Switch = $_.SwitchName; VlanMode = [string]$v.OperationMode; Vlan = [int]$v.AccessVlanId } }) |
            ConvertTo-Json | Set-Content $record -Encoding UTF8
    }
    # PowerShell Direct doesn't use the network, but a guest whose network is rebuilt is no place for a kept session
    # to rely on: the calls below start with a new one
    Clear-LabSessionCache -Name $vm.Name
    # untagged on the jump switch: an access VLAN from the VM's own network would keep it apart from the jump host
    foreach ($a in $adapters) { Connect-VMNetworkAdapter -VMNetworkAdapter $a -SwitchName $cfg.Switch; Set-VMNetworkAdapterVlan -VMNetworkAdapter $a -Untagged }
    $macs = @($adapters | ForEach-Object { ($_.MacAddress -replace '(..)(?!$)', '$1-') })
    Invoke-LabGuest -Name $vm.Name -ArgumentList $macs, $address, ([int]$cfg.PrefixLength) -ScriptBlock {
        param($macs, $address, $prefix)
        $nics = @(Get-NetAdapter | Where-Object { $macs -contains $_.MacAddress })
        foreach ($n in $nics) {
            Set-NetIPInterface -InterfaceIndex $n.ifIndex -Dhcp Disabled -ErrorAction SilentlyContinue
            Get-NetIPAddress -InterfaceIndex $n.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
            Get-NetRoute -InterfaceIndex $n.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
            Set-DnsClientServerAddress -InterfaceIndex $n.ifIndex -ResetServerAddresses
        }
        # one address on the first adapter is enough
        $null = New-NetIPAddress -InterfaceIndex $nics[0].ifIndex -IPAddress $address -PrefixLength $prefix
    }
    # until Windows has the address up (duplicate detection)
    $until = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $until) {
        $state = Invoke-LabGuest -Name $vm.Name -ArgumentList $address -ScriptBlock { param($a) (Get-NetIPAddress -IPAddress $a -ErrorAction SilentlyContinue).AddressState }
        if ("$state" -eq 'Preferred') { break }
        Start-Sleep -Seconds 2
    }
    $address
}

# Puts a VM's adapters back on the switches Move-LabVmBehindJump wrote down (nothing to do when it wasn't moved).
# Called by Reset-LabVm, so a run never leaves a VM behind the jump host.
function Restore-LabVmNetwork {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $vm = Get-LabVm $Name
    $record = Join-Path (Get-LabConfig).LabRoot "network\$($vm.Id).json"
    if (-not (Test-Path $record)) { return }
    Clear-LabSessionCache -Name $vm.Name
    # (..): 5.1's ConvertFrom-Json writes an array (two or more adapters) as one object - the parentheses unroll it
    foreach ($r in @((Get-Content $record -Raw | ConvertFrom-Json))) {
        $a = Get-VMNetworkAdapter -VMName $vm.Name | Where-Object MacAddress -eq $r.Mac | Select-Object -First 1
        if ($a -and $r.Switch -and $a.SwitchName -ne $r.Switch) { Connect-VMNetworkAdapter -VMNetworkAdapter $a -SwitchName $r.Switch }
        if ($a -and $r.PSObject.Properties['VlanMode'] -and $r.VlanMode -eq 'Access' -and $r.Vlan -gt 0) { Set-VMNetworkAdapterVlan -VMNetworkAdapter $a -Access -VlanId $r.Vlan }
    }
    Remove-Item $record
}

# From now on the harness goes through the jump host (every command that reaches a device): -Jump is what
# Start-LabJumpHost answered. Use-LabJump -Off goes direct again.
function Use-LabJump {
    [CmdletBinding()]
    param($Jump, [switch]$Off)
    if ($Off -or -not $Jump) { $script:JumpArgs = $null; $script:JumpAddress = $null; return }
    $script:JumpAddress = $Jump.Address
    $script:JumpArgs =@('--jump', $Jump.Address, '--jump-pin', $Jump.Pin, '--jump-user', $Jump.User, '--jump-password', (Get-LabConfig).Password)
}

# A check of the jump host tunnel itself (harness "jump", see LabJump.cs) - through the route set with Use-LabJump.
function Invoke-LabJump {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('open', 'local', 'rdp', 'forward', 'streams', 'direct', 'raw')][string]$Kind,
        [Parameter(Mandatory)][string]$TargetHost,
        [int]$Port = 3389,
        [hashtable]$Params = @{},
        [int]$TimeoutSec = 180
    )
    $a = @('jump', '--kind', $Kind, '--host', $TargetHost, '--port', "$Port")
    foreach ($k in $Params.Keys) { $a += "--$k", "$($Params[$k])" }
    Invoke-LabHarness $a -TimeoutSec $TimeoutSec
}

#endregion

#region Guest

# ------------------------------------------------------------------ kept PowerShell Direct sessions
# Opening a PowerShell Direct session takes 0.5-2 s, a command in an open one 0.01-0.06 s, and the tests call
# Invoke-LabGuest hundreds of times, many in polling loops. So Invoke-LabGuest, Copy-LabFile and Get-LabFile keep one
# session per VM and account for as long as the module is loaded - one test file, each runs in its own process. The
# sessions are closed when the module is removed or PowerShell exits, and dropped whenever the module changes the VM
# underneath them (revert, restart, stop, run checkpoint, network move, Wait-LabVm): Clear-LabSessionCache.
# RHLAB_SESSION_CACHE=0 opens a session per call as before; Invoke-LabGuest -NewSession does so for one call.
#
# Script blocks run in the session's global scope, so whatever one leaves behind would be seen by the next. Before a
# kept session is used again one call (Reset) puts back what a new session has: variables, functions and aliases that
# weren't there when it opened are removed, the preference variables, $PSDefaultParameterValues, the environment
# variables and the current location are restored, strict mode is off, $Error, jobs and event subscriptions are
# cleared. Not undone: modules a block imported, types it added (Add-Type again with the same source is fine) and
# whatever else lives in the session's process - anything that would have ended with a per-call session (a process
# it started, if PowerShell Direct ends those) now ends with the file.
#
# The reset is also the check that the session still works. When it fails - the guest restarted or was reverted
# outside the module, the connection broke - nothing of the caller's block has run yet, so the block goes to a new
# session instead. Once a block has been sent it is never sent again: a failure then (a block that restarts the guest,
# a connection lost half-way) drops a broken session and is thrown as before, so the next call opens a new one.

$script:LabSessions = @{}         # "<Hyper-V name>|<user>" -> Session, Vm
$script:LabSessionTargets = @{}   # "<-Name>|<-Account>" as given -> Key, Vm, User (saves Get-LabVm, ~0.1 s, per call)

# What a new session has, recorded in it before the first block runs (see above), and the reset kept as a function
# there: compiled once, while a script block sent each time is compiled again (measured 35-55 ms a call against 20-35).
#
# RhLabReset runs dot-sourced, in the session's global scope like the blocks do (its own variables are removed at the
# end); foreach and .NET calls rather than pipelines keep it short. No errors of its own: one would count as a broken
# session.
$script:LabSessionBaseline = {
    function global:RhLabReset {
        Set-StrictMode -Off
        $rhB = $global:RhLabBaseline
        if ((Get-Location).Path -ne $rhB.Location) { Set-Location -LiteralPath $rhB.Location -ErrorAction SilentlyContinue }
        foreach ($rhK in $rhB.Prefs.Keys) { $ExecutionContext.SessionState.PSVariable.Set($rhK, $rhB.Prefs[$rhK]) }
        $PSDefaultParameterValues.Clear()
        $rhE = [Environment]::GetEnvironmentVariables()
        foreach ($rhK in $rhE.Keys) { if (-not $rhB.Env.ContainsKey($rhK)) { [Environment]::SetEnvironmentVariable($rhK, $null) } }
        foreach ($rhK in $rhB.Env.Keys) { if ($rhE[$rhK] -cne $rhB.Env[$rhK]) { [Environment]::SetEnvironmentVariable($rhK, $rhB.Env[$rhK]) } }
        foreach ($rhK in Get-ChildItem function:) { if (-not $rhB.Functions.Contains($rhK.Name)) { Remove-Item -LiteralPath "function:\$($rhK.Name)" -Force -ErrorAction SilentlyContinue } }
        foreach ($rhK in Get-ChildItem alias:) { if (-not $rhB.Aliases.Contains($rhK.Name)) { Remove-Item -LiteralPath "alias:\$($rhK.Name)" -Force -ErrorAction SilentlyContinue } }
        if (@(Get-Job).Count) { Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue }
        if (@(Get-EventSubscriber).Count) { Get-EventSubscriber | Unregister-Event -Force -ErrorAction SilentlyContinue }
        if (@(Get-Event).Count) { Get-Event | Remove-Event -ErrorAction SilentlyContinue }
        $Error.Clear()
        # (through $global:RhLabBaseline: $rhB itself goes in this loop)
        foreach ($rhK in Get-Variable -Scope Global) { if (-not $global:RhLabBaseline.Vars.Contains($rhK.Name)) { Remove-Variable -Name $rhK.Name -Scope Global -Force -ErrorAction SilentlyContinue } }
        Remove-Variable -Name rhB, rhE, rhK -Scope Global -Force -ErrorAction SilentlyContinue
    }
    $global:RhLabBaseline = & {
        $set = { param($items) $s = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase); foreach ($i in $items) { $null = $s.Add($i.Name) }; , $s }
        $vars = & $set (Get-Variable -Scope Global)
        $null = $vars.Add('RhLabBaseline')
        $prefs = @{}
        foreach ($v in Get-Variable -Scope Global) { if ($v.Name -like '*Preference') { $prefs[$v.Name] = $v.Value } }
        @{
            Vars = $vars; Prefs = $prefs; Env = [Environment]::GetEnvironmentVariables(); Location = (Get-Location).Path
            Functions = & $set (Get-ChildItem function:); Aliases = & $set (Get-ChildItem alias:)
        }
    }
}

# Puts a kept session back to its baseline (see above). Should a block have removed RhLabReset, the call fails, which
# only costs a new session.
$script:LabSessionReset = { . RhLabReset }

function Test-LabSessionCache { $env:RHLAB_SESSION_CACHE -ne '0' }

function Remove-LabCachedSession([string]$Key) {
    $e = $script:LabSessions[$Key]
    $script:LabSessions.Remove($Key)
    if ($e) {
        try { Remove-PSSession $e.Session -ErrorAction Stop }
        catch { Write-Verbose "$($e.Vm): closing the kept PowerShell Direct session: $($_.Exception.Message)" }
    }
}

# Drops the kept sessions of a VM (-Name: its Hyper-V name or what callers pass, e.g. the Id; all VMs without it): the
# module calls it whenever it changes the VM's state, so the next call opens a new session.
function Clear-LabSessionCache {
    param([string]$Name)
    $keys = @(foreach ($a in @($script:LabSessionTargets.Keys)) {
            if ($a.StartsWith("$Name|", [StringComparison]::OrdinalIgnoreCase)) { $script:LabSessionTargets[$a].Key }
        })
    foreach ($k in @($script:LabSessions.Keys)) {
        if (-not $Name -or $script:LabSessions[$k].Vm -eq $Name -or $keys -contains $k) { Remove-LabCachedSession $k }
    }
}

# A session for one call to the guest: the kept one, reset (see above), or a new one - kept unless -NewSession or
# RHLAB_SESSION_CACHE=0, then Close-LabGuestSession removes it after the call.
function Open-LabGuestSession {
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'Admin', [switch]$NewSession)
    $alias = "$Name|$Account"
    $t = $script:LabSessionTargets[$alias]
    if (-not $t) {
        $vm = Get-LabVm $Name | Select-Object -First 1
        $user = Resolve-LabAccount $vm $Account
        $t = [pscustomobject]@{ Key = "$($vm.Name)|$user"; Vm = $vm.Name; User = $user }
        $script:LabSessionTargets[$alias] = $t
    }
    if ($NewSession -or -not (Test-LabSessionCache)) {
        return [pscustomobject]@{ Session = (New-LabSession -Name $t.Vm -Credential (Get-LabCredential $t.User)); Kept = $false }
    }
    $e = $script:LabSessions[$t.Key]
    if ($e) {
        $s = $e.Session
        if ($s.State -eq 'Opened' -and $s.Availability -eq 'Available') {
            try {
                $null = Invoke-Command -Session $s -ScriptBlock $script:LabSessionReset -ErrorAction Stop
                return [pscustomobject]@{ Session = $s; Kept = $true }
            }
            catch { Write-Verbose "$($t.Vm): the kept PowerShell Direct session doesn't answer ($($_.Exception.Message)) - opening a new one" }
        }
        Remove-LabCachedSession $t.Key
    }
    $s = New-LabSession -Name $t.Vm -Credential (Get-LabCredential $t.User)
    try { $null = Invoke-Command -Session $s -ScriptBlock $script:LabSessionBaseline -ErrorAction Stop }
    catch { Remove-PSSession $s -ErrorAction SilentlyContinue; throw }
    $script:LabSessions[$t.Key] = [pscustomobject]@{ Session = $s; Vm = $t.Vm }
    [pscustomobject]@{ Session = $s; Kept = $true }
}

# After a failed call: a kept session is dropped when the connection failed (the next call opens a new one) and kept
# when the error was the script block's own. The call itself is not repeated - the block may have run (see above).
function Remove-LabBrokenSession {
    param([Parameter(Mandatory)]$Guest, [Parameter(Mandatory)]$ErrorRecord)
    if (-not $Guest.Kept) { return }
    $s = $Guest.Session
    $broken = $s.State -ne 'Opened' -or $s.Availability -ne 'Available'
    for ($x = $ErrorRecord.Exception; $x -and -not $broken; $x = $x.InnerException) {
        $broken = $x -is [System.Management.Automation.Remoting.PSRemotingTransportException] -or
            $x -is [System.Management.Automation.Remoting.PSRemotingDataStructureException] -or
            $x.Message -like '*Hyper-V socket target process has ended*'
    }
    if (-not $broken) { return }
    foreach ($k in @($script:LabSessions.Keys)) {
        if ($script:LabSessions[$k].Session.InstanceId -eq $s.InstanceId) { Remove-LabCachedSession $k }
    }
}

function Close-LabGuestSession($Guest) {
    if ($Guest -and -not $Guest.Kept) { Remove-PSSession $Guest.Session -ErrorAction SilentlyContinue }
}

# The kept sessions are closed with the module (Remove-Module, Import-Module -Force) and when PowerShell exits, so the
# guest ends their processes at once rather than when it notices the connection is gone (PowerShell Direct allows only
# so many sessions per VM). The exit handler gets the table itself - an engine event's action doesn't run in the
# module's scope.
$script:LabSessionExit = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -MessageData $script:LabSessions -Action {
    foreach ($e in @($Event.MessageData.Values)) { Remove-PSSession $e.Session -ErrorAction SilentlyContinue }
}
$ExecutionContext.SessionState.Module.OnRemove = {
    Clear-LabSessionCache
    Get-EventSubscriber -ErrorAction SilentlyContinue | Where-Object { $_.Action -and $_.Action.Id -eq $script:LabSessionExit.Id } |
        Unregister-Event -ErrorAction SilentlyContinue
    Remove-Job -Job $script:LabSessionExit -Force -ErrorAction SilentlyContinue
}

# -NewSession: a session of its own for this call, closed after it (see "kept PowerShell Direct sessions" above).
function Invoke-LabGuest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [Parameter(Mandatory, Position = 1)][scriptblock]$ScriptBlock,
        [object[]]$ArgumentList = @(),
        [string]$Account = 'Admin',
        [switch]$NewSession
    )
    $g = Open-LabGuestSession -Name $Name -Account $Account -NewSession:$NewSession
    try { Invoke-Command -Session $g.Session -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList }
    catch { Remove-LabBrokenSession -Guest $g -ErrorRecord $_; throw }
    finally { Close-LabGuestSession $g }
}

# Host -> guest. Destination is a guest folder; it is created if missing.
function Copy-LabFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Path,
        [Parameter(Mandatory)][string]$Destination
    )
    $g = Open-LabGuestSession -Name $Name
    try {
        Invoke-Command -Session $g.Session -ArgumentList $Destination -ScriptBlock {
            param($d) $null = New-Item -ItemType Directory -Path $d -Force
        }
        Copy-Item -Path $Path -Destination $Destination -ToSession $g.Session -Recurse -Force
    }
    catch { Remove-LabBrokenSession -Guest $g -ErrorRecord $_; throw }
    finally { Close-LabGuestSession $g }
}

# Guest -> host. Destination is a host folder; it is created if missing. Missing guest files are skipped.
function Get-LabFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Path,
        [Parameter(Mandatory)][string]$Destination
    )
    $null = New-Item -ItemType Directory -Path $Destination -Force
    $g = Open-LabGuestSession -Name $Name
    try {
        $existing = @(Invoke-Command -Session $g.Session -ArgumentList (, $Path) -ScriptBlock {
                param($p) $p | Where-Object { Test-Path $_ }
            })
        if ($existing.Count -gt 0) {
            Copy-Item -Path $existing -Destination $Destination -FromSession $g.Session -Recurse -Force
        }
    }
    catch { Remove-LabBrokenSession -Guest $g -ErrorRecord $_; throw }
    finally { Close-LabGuestSession $g }
}

#endregion

#region Console (Hyper-V WMI)

function Get-LabCimVm {
    param([Parameter(Mandatory)][string]$Name)
    $cs = @(Get-CimInstance -Namespace $script:Ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$Name'")
    if ($cs.Count -ne 1) { throw "${Name}: expected one Hyper-V VM with this name, found $($cs.Count)." }
    $cs[0]
}

# Msvm methods return uint32 0 on success; keyboard methods on some builds return a boolean.
function Assert-CimResult {
    param($Result, [string]$What)
    $ok = if ($Result.ReturnValue -is [bool]) { $Result.ReturnValue } else { $Result.ReturnValue -eq 0 }
    if (-not $ok) { throw "${What} failed (return value $($Result.ReturnValue))." }
}

# Saves the console as PNG at the guest's current resolution. IsBlank = every sampled pixel has the same colour.
function Get-LabScreenshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path
    )
    $cs = Get-LabCimVm $Name
    $heads = @(Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VideoHead)
    $head = @($heads | Where-Object EnabledState -eq 2) + $heads | Select-Object -First 1
    if (-not $head) { throw "${Name}: no video head (is the VM running?)." }
    $w = [int]@($head.CurrentHorizontalResolution)[0]
    $h = [int]@($head.CurrentVerticalResolution)[0]

    $vssd = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VirtualSystemSettingData |
        Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'
    $mgmt = Get-CimInstance -Namespace $script:Ns -ClassName Msvm_VirtualSystemManagementService
    $r = Invoke-CimMethod -InputObject $mgmt -MethodName GetVirtualSystemThumbnailImage -Arguments @{
        TargetSystem = $vssd; WidthPixels = [uint16]$w; HeightPixels = [uint16]$h
    }
    Assert-CimResult $r "${Name}: GetVirtualSystemThumbnailImage"
    $data = [byte[]]$r.ImageData

    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap ($w, $h, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    try {
        $bits = $bmp.LockBits((New-Object System.Drawing.Rectangle (0, 0, $w, $h)),
            [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
        try {
            for ($y = 0; $y -lt $h; $y++) {
                [System.Runtime.InteropServices.Marshal]::Copy($data, $y * $w * 2,
                    [IntPtr]($bits.Scan0.ToInt64() + $y * $bits.Stride), $w * 2)
            }
        }
        finally { $bmp.UnlockBits($bits) }
        $null = New-Item -ItemType Directory -Path (Split-Path $Path) -Force
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bmp.Dispose() }

    $colours = @{}
    for ($i = 0; $i -lt $data.Length - 1; $i += 2 * 997) { $colours[[BitConverter]::ToUInt16($data, $i)] = $true }
    [pscustomobject]@{ Path = $Path; Width = $w; Height = $h; IsBlank = ($colours.Count -le 1) }
}

# The console's resolution as "WxH", from Hyper-V's video head (the enabled head, else the first) - what the device's
# monitor shows, whoever is signed in. Read it here, not in the guest: over PowerShell Direct Windows.Forms.Screen and
# the like see that non-interactive session's own screen, always 1024x768. With the agent's virtual monitor on, the
# head still shows only the Hyper-V display, not the virtual monitor (which is then the primary one) - read that
# from the agent's Layout instead.
function Get-LabResolution {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $heads = @(Get-CimAssociatedInstance -InputObject (Get-LabCimVm $Name) -ResultClassName Msvm_VideoHead)
    $head = @($heads | Where-Object EnabledState -eq 2) + $heads | Select-Object -First 1
    if (-not $head) { throw "${Name}: no video head (is the VM running?)." }
    "$(@($head.CurrentHorizontalResolution)[0])x$(@($head.CurrentVerticalResolution)[0])"
}

# Polls Get-LabResolution until it is -Is (and/or is no longer -Not); returns the value read. On timeout it throws,
# naming what was seen; -NoThrow returns the last value instead, for a test that records it and checks it in an It.
function Wait-LabResolution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Is,
        [string]$Not,
        [int]$TimeoutSec = 30,
        [switch]$NoThrow
    )
    if (-not $Is -and -not $Not) { throw 'Wait-LabResolution: give -Is, -Not or both.' }
    $until = (Get-Date).AddSeconds($TimeoutSec)
    $seen = New-Object System.Collections.Generic.List[string]
    while ($true) {
        $r = Get-LabResolution -Name $Name
        if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $r) { $seen.Add($r) }
        if ((-not $Is -or $r -eq $Is) -and (-not $Not -or $r -ne $Not)) { return $r }
        if ((Get-Date) -ge $until) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($NoThrow) { return $r }
    $want = @($Is, $(if ($Not) { "not $Not" }) | Where-Object { $_ }) -join ' and '
    throw "${Name}: the console's resolution wasn't $want within $TimeoutSec s (seen: $($seen -join ', '))."
}

function Get-LabKeyboard {
    param([Parameter(Mandatory)][string]$Name)
    $kb = @(Get-CimAssociatedInstance -InputObject (Get-LabCimVm $Name) -ResultClassName Msvm_Keyboard)
    if ($kb.Count -eq 0) { throw "${Name}: no keyboard (is the VM running?)." }
    $kb[0]
}

# Types ASCII text on the console, like a person at the keyboard. Letters, digits and unshifted symbols go through
# Hyper-V's TypeText; shifted symbols are Shift and the key's virtual-key code pressed and released one by one
# (PressKey / ReleaseKey). TypeText typed SMOKE-10's "!@#$%" as "%%%%%" on the Windows 11 VMs, PressKey(Shift) + TypeKey
# as "1234", and Shift sent as scan codes in one TypeScancodes call as "12345" on the 26H2 VMs (lab run
# 20261019-115442). Hyper-V maps virtual keys through its US table, so this needs the US keyboard the lab's VMs have
# (docs/test-lab-spec.md 5.1; SMOKE-10 checks it).
$script:LabShifted = @{
    '!' = 0x31; '@' = 0x32; '#' = 0x33; '$' = 0x34; '%' = 0x35; '^' = 0x36; '&' = 0x37; '*' = 0x38; '(' = 0x39; ')' = 0x30
    '_' = 0xBD; '+' = 0xBB; '{' = 0xDB; '}' = 0xDD; '|' = 0xDC; ':' = 0xBA; '"' = 0xDE; '<' = 0xBC; '>' = 0xBE; '?' = 0xBF
    '~' = 0xC0
}
function Send-LabText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Text)
    $kb = Get-LabKeyboard $Name
    $run = New-Object Text.StringBuilder
    $flush = {
        if ($run.Length) {
            Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName TypeText -Arguments @{ asciiText = $run.ToString() }) "${Name}: TypeText"
            $null = $run.Clear()
        }
    }
    foreach ($c in $Text.ToCharArray()) {
        $vk = $script:LabShifted[[string]$c]
        if ($null -eq $vk) { $null = $run.Append($c); continue }
        if ($run.Length) {
            # TypeText returns before the guest has taken its keys: Shift pressed at once was lost on the 26H2 VMs
            # for the first symbol after text ("1@#$%" after 17 characters, lab runs 20261019-121308 and, with a
            # fixed 300 ms, -125300). The wait grows with the text typed.
            $wait = 500 + 40 * $run.Length
            & $flush
            Start-Sleep -Milliseconds $wait
        }
        Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = 0x10 }) "${Name}: press Shift"
        try {
            Start-Sleep -Milliseconds 50
            Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = $vk }) "${Name}: press $vk"
            Start-Sleep -Milliseconds 50
            Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = $vk }) "${Name}: release $vk"
        }
        # Shift left down would change every later key on the console
        finally { $null = Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = 0x10 } }
        Start-Sleep -Milliseconds 50
    }
    & $flush
}

# Key is a System.Windows.Forms.Keys name (Enter, Escape, Tab, Space, LWin, ControlKey, ...) - the values are
# Windows virtual-key codes.
function Send-LabKey {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Key)
    Add-Type -AssemblyName System.Windows.Forms
    $code = [int][System.Windows.Forms.Keys]$Key
    $r = Invoke-CimMethod -InputObject (Get-LabKeyboard $Name) -MethodName TypeKey -Arguments @{ keyCode = $code }
    Assert-CimResult $r "${Name}: TypeKey $Key"
}

# The console mouse (Hyper-V synthetic mouse): real input, so it also reaches windows of higher integrity (the agent's
# SYSTEM dialogs and banner) that input sent from inside a user's session can't (UIPI). X / Y in guest screen pixels.
function Get-LabMouse {
    param([Parameter(Mandatory)][string]$Name)
    $m = @(Get-CimAssociatedInstance -InputObject (Get-LabCimVm $Name) -ResultClassName Msvm_SyntheticMouse)
    if ($m.Count -eq 0) { throw "${Name}: no synthetic mouse (is the VM running?)." }
    $m[0]
}

function Send-LabMouse {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$X,
        [Parameter(Mandatory)][int]$Y,
        [ValidateSet('Move', 'Left', 'Right', 'Middle', 'Double')][string]$Action = 'Left'
    )
    $mouse = Get-LabMouse $Name
    Assert-CimResult (Invoke-CimMethod -InputObject $mouse -MethodName SetAbsolutePosition -Arguments @{ HorizontalPosition = [int32]$X; VerticalPosition = [int32]$Y }) "${Name}: mouse position"
    if ($Action -eq 'Move') { return }
    Start-Sleep -Milliseconds 150
    # Msvm_SyntheticMouse buttons: 1 left, 2 right, 3 middle
    $button = switch ($Action) { 'Right' { 2 } 'Middle' { 3 } default { 1 } }
    foreach ($n in 1..$(if ($Action -eq 'Double') { 2 } else { 1 })) {
        Assert-CimResult (Invoke-CimMethod -InputObject $mouse -MethodName SetButtonState -Arguments @{ ButtonIndex = [uint32]$button; IsDown = $true }) "${Name}: mouse down"
        Start-Sleep -Milliseconds 80
        Assert-CimResult (Invoke-CimMethod -InputObject $mouse -MethodName SetButtonState -Arguments @{ ButtonIndex = [uint32]$button; IsDown = $false }) "${Name}: mouse up"
        Start-Sleep -Milliseconds 80
    }
}

# A key combination on the console keyboard: virtual-key codes pressed in order, released in reverse (Alt+F4 = 0x12, 0x73).
function Send-LabKeyCombo {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][int[]]$Codes)
    $kb = Get-LabKeyboard $Name
    $down = New-Object System.Collections.Generic.List[int]
    try {
        foreach ($c in $Codes) {
            Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = $c }) "${Name}: press $c"
            $down.Add($c)
        }
        Start-Sleep -Milliseconds 100
    }
    catch {
        # keys already down are released: Ctrl/Alt left pressed would change every later key on the console (lab run
        # 20261016-212514: PressKey 226 failed after Ctrl and Alt were down)
        for ($i = $down.Count - 1; $i -ge 0; $i--) { $null = Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = $down[$i] } }
        throw
    }
    foreach ($c in $Codes[($Codes.Count - 1)..0]) { Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = $c }) "${Name}: release $c" }
}

# A key combination as keyboard scan codes (set 1), for keys PressKey rejects: Hyper-V's virtual-key table is the US
# 101-key one, so VK_OEM_102 (226, the ISO "<" key) fails with 32773 "Invalid parameter". Make codes pressed in order,
# break codes (make + 0x80) in reverse, in one TypeScancodes call. Extended keys as 0xE0nn (0xE046 = Break with Ctrl).
# Ctrl 0x1D, left Alt 0x38, left Shift 0x2A, "<" 0x56: Ctrl+Alt+< = 0x1D, 0x38, 0x56. -HoldMs: the keys are held that
# long before they are let go (two calls), as a person holds them - in one call all of them are down and up again at once.
function Send-LabScancodes {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][int[]]$Codes, [int]$HoldMs = 0)
    $down = New-Object System.Collections.Generic.List[byte]
    foreach ($c in $Codes) {
        if ($c -gt 0xFF) { $down.Add(0xE0) }
        $down.Add([byte]($c -band 0x7F))
    }
    $up = New-Object System.Collections.Generic.List[byte]
    foreach ($c in $Codes[($Codes.Count - 1)..0]) {
        if ($c -gt 0xFF) { $up.Add(0xE0) }
        $up.Add([byte](($c -band 0x7F) -bor 0x80))
    }
    $calls = New-Object System.Collections.Generic.List[object]
    if ($HoldMs -gt 0) { $calls.Add([byte[]]$down.ToArray()); $calls.Add([byte[]]$up.ToArray()) }
    else { $down.AddRange($up); $calls.Add([byte[]]$down.ToArray()) }
    for ($i = 0; $i -lt $calls.Count; $i++) {
        if ($i -gt 0) { Start-Sleep -Milliseconds $HoldMs }
        [byte[]]$bytes = $calls[$i]
        $r = Invoke-CimMethod -InputObject (Get-LabKeyboard $Name) -MethodName TypeScancodes -Arguments @{ Scancodes = $bytes }
        Assert-CimResult $r "${Name}: TypeScancodes $(($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')"
    }
}

function Send-LabCtrlAltDel {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $r = Invoke-CimMethod -InputObject (Get-LabKeyboard $Name) -MethodName TypeCtrlAltDel
    Assert-CimResult $r "${Name}: TypeCtrlAltDel"
}

#endregion

#region Sessions

# Signs the account in automatically (built-in Winlogon values), restarts the guest and waits for its desktop.
function Set-LabAutoLogon {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Account = 'User',
        [int]$TimeoutSec = 300
    )
    # set up outside Initialize-LabVm: the VM is no longer what Initialize-LabVm left
    if (-not $script:LabInitializing) { Clear-LabVmState -Name $Name }
    $vm = Get-LabVm $Name
    $user = Resolve-LabAccount $vm $Account
    Invoke-LabGuest -Name $Name -ArgumentList $user, (Get-LabConfig).Password -ScriptBlock {
        param($u, $p)
        $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty $k -Name AutoAdminLogon -Value '1'
        Set-ItemProperty $k -Name DefaultUserName -Value $u
        Set-ItemProperty $k -Name DefaultPassword -Value $p
        Set-ItemProperty $k -Name DefaultDomainName -Value $env:COMPUTERNAME
        Remove-ItemProperty $k -Name AutoLogonCount -ErrorAction SilentlyContinue
        # long scenarios: the signed-in session must not lock or go dark on its own (the person at the device needs its
        # desktop on screen); a test locks it on purpose when it needs to (Lock-LabSession)
        powercfg /change monitor-timeout-ac 0 | Out-Null
        powercfg /change standby-timeout-ac 0 | Out-Null
        $sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
        Set-ItemProperty $sys -Name InactivityTimeoutSecs -Value 0 -Type DWord
        $ss = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop'
        $null = New-Item $ss -Force
        Set-ItemProperty $ss -Name ScreenSaveActive -Value '0'
        Set-ItemProperty $ss -Name ScreenSaverIsSecure -Value '0'
    }
    Restart-LabVm -Name $Name -TimeoutSec $TimeoutSec
    Wait-LabDesktop -Name $Name -Account $user -TimeoutSec $TimeoutSec
}

function Clear-LabAutoLogon {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    # set up outside Initialize-LabVm: the VM is no longer what Initialize-LabVm left
    if (-not $script:LabInitializing) { Clear-LabVmState -Name $Name }
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty $k -Name AutoAdminLogon -Value '0'
        Remove-ItemProperty $k -Name DefaultPassword -ErrorAction SilentlyContinue
    }
}

# Desktop = the account owns an explorer.exe in a session without a lock/sign-in screen (LogonUI) and the console
# isn't blank.
function Wait-LabDesktop {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Account = 'User',
        [int]$TimeoutSec = 180
    )
    $user = Resolve-LabAccount (Get-LabVm $Name) $Account
    $shot = Join-Path ([IO.Path]::GetTempPath()) "rhlab-$([guid]::NewGuid().ToString('N')).png"
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    try {
        while ((Get-Date) -lt $deadline) {
            # unlocked = the user's explorer runs in a session without LogonUI (other sessions may sit at a lock screen)
            # right after a restart the guest's PowerShell Direct endpoint may still go away ("The Hyper-V socket target
            # process has ended"): that is "not yet", not a failure
            $unlocked = try {
                Invoke-LabGuest -Name $Name -ArgumentList $user -ScriptBlock {
                    param($u)
                    $lockedSessions = @(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId)
                    @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object {
                            (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $u -and
                            $lockedSessions -notcontains $_.SessionId
                        }).Count -gt 0
                }
            }
            catch [System.Management.Automation.Remoting.PSRemotingTransportException] { $false }
            if ($unlocked -and -not (Get-LabScreenshot -Name $Name -Path $shot).IsBlank) { return }
            Start-Sleep -Seconds 3
        }
    }
    finally { Remove-Item $shot -ErrorAction SilentlyContinue }
    throw "${Name}: no desktop for $user after $TimeoutSec s."
}

# Runs code in the account's interactive session (not session 0) through a one-off scheduled task in the guest.
# The account must be signed in. Returns the code's output as text. -Elevated: with the full token ("Run as
# administrator"), for an account in Administrators.
function Invoke-LabInSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [string]$Account = 'User',
        [int]$TimeoutSec = 120,
        [switch]$Elevated
    )
    $user = Resolve-LabAccount (Get-LabVm $Name) $Account
    Invoke-LabGuest -Name $Name -ArgumentList $user, $ScriptBlock.ToString(), $TimeoutSec, $Elevated.IsPresent -ScriptBlock {
        param($u, $code, $timeout, $elevated)
        $id = [guid]::NewGuid().ToString('N')
        $dir = 'C:\ProgramData\RemoteHubLab\session'
        $null = New-Item -ItemType Directory -Path $dir -Force
        icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $out = Join-Path $dir "$id.txt"
        $done = Join-Path $dir "$id.done"
        $wrapped = "try { & { $code } *>&1 | Out-String -Width 4096 | Set-Content -Path '$out' -Encoding UTF8 } " +
            "catch { `"ERROR: `$_`" | Set-Content -Path '$out' -Encoding UTF8 } finally { New-Item '$done' | Out-Null }"
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
        $task = "RHLab-$id"
        # through conhost --headless: on Windows 11 a "hidden" powershell.exe still gets a visible Windows Terminal window,
        # which covers the desktop and takes keys meant for it
        $action = New-ScheduledTaskAction -Execute 'conhost.exe' `
            -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $enc"
        $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel $(if ($elevated) { 'Highest' } else { 'Limited' })
        $null = Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force
        try {
            Start-ScheduledTask -TaskName $task
            $deadline = (Get-Date).AddSeconds($timeout)
            while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
            if (-not (Test-Path $done)) {
                # what the next run needs to tell "not signed in / locked / disconnected" from "started but slow" (lab
                # run 20261019-193627, C-REC-01: only "no result" was known): the task's state and last result, the
                # sessions (quser: Active / Disc) and the sessions that show a lock or sign-in screen (LogonUI)
                $why = try {
                    $ti = Get-ScheduledTaskInfo -TaskName $task -ErrorAction Stop
                    $st = (Get-ScheduledTask -TaskName $task -ErrorAction Stop).State
                    $sessions = (@(quser.exe 2>&1 | ForEach-Object { ("$_" -replace '\s+', ' ').Trim() }) -join '; ')
                    $logonUi = (@(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId) -join ', ')
                    # whether the task's PowerShell started at all and is busy or stuck (run 20261022-061254, Server: the
                    # task ran, no result, the person server's PowerShell in the same session too)
                    $now = Get-Date
                    $procs = (@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='conhost.exe'" | ForEach-Object {
                                $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                                $cpu = if ($p -and $null -ne $p.CPU) { [int]$p.CPU } else { '?' }
                                "$($_.Name)#$($_.ProcessId) s$($_.SessionId) $([int]($now - $_.CreationDate).TotalSeconds)s old cpu ${cpu}s$(if ($_.CommandLine -like "*$enc*") { ' (this task)' })"
                            }) -join ', ')
                    "task $st, last run $($ti.LastRunTime), last result 0x$('{0:X8}' -f $ti.LastTaskResult); sessions: $sessions; LogonUI in session(s): $(if ($logonUi) { $logonUi } else { 'none' }); processes: $procs"
                }
                catch { "no task details: $($_.Exception.Message)" }
                throw "no result from the session task after $timeout s (is $u signed in?) - $why"
            }
            Get-Content $out -Raw
        }
        finally {
            Unregister-ScheduledTask -TaskName $task -Confirm:$false
            Remove-Item $out, $done -ErrorAction SilentlyContinue
        }
    }
}

# Starts code in the account's interactive session and returns at once (a marker app, a prompt that must stay open);
# Stop-LabInSession ends it. The task's name is the handle.
function Start-LabInSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [string]$Account = 'User',
        [switch]$Elevated
    )
    $user = Resolve-LabAccount (Get-LabVm $Name) $Account
    Invoke-LabGuest -Name $Name -ArgumentList $user, $ScriptBlock.ToString(), $Elevated.IsPresent -ScriptBlock {
        param($u, $code, $elevated)
        $task = "RHLab-bg-$([guid]::NewGuid().ToString('N').Substring(0, 12))"
        # its process id, for Stop-LabInSession (stopping the task ends conhost, not always the powershell under it)
        $dir = 'C:\ProgramData\RemoteHubLab\session'
        $null = New-Item -ItemType Directory -Path $dir -Force
        icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $code = "Set-Content '$dir\$task.pid' `$PID`r`n" + $code
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
        # (conhost --headless: no visible console window - see Invoke-LabInSession)
        $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $enc"
        $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel $(if ($elevated) { 'Highest' } else { 'Limited' })
        $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
        $null = Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force
        Start-ScheduledTask -TaskName $task
        $task
    }
}

function Stop-LabInSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Task)
    Invoke-LabGuest -Name $Name -ArgumentList $Task -ScriptBlock {
        param($t)
        Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
        $pidFile = "C:\ProgramData\RemoteHubLab\session\$t.pid"
        if (Test-Path $pidFile) {
            $id = [int](Get-Content $pidFile | Select-Object -First 1)
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
            Remove-Item $pidFile -ErrorAction SilentlyContinue
        }
        Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
    }
}

# A window in the account's session (the console by default) that changes its picture on every tick of a 15 ms timer:
# a new colour, or with -Noise one of 8 pictures of random pixels. The agent sends a picture only when the screen
# changes (the mouse pointer isn't part of it), so frame counts need this. It writes "<ticks> <ms>" to a guest file
# every 30 ticks; Stop-LabChangingWindow ends it and answers how often it changed.
function Start-LabChangingWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'Console', [switch]$Noise, [int]$TimeoutSec = 60)
    $code = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$global:n = 0
$global:noise = ('__NOISE__' -eq '1')
$global:clock = [Diagnostics.Stopwatch]::StartNew()
$w = 480; $h = 360
$global:form = New-Object Windows.Forms.Form
$form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.TopMost = $true; $form.ShowInTaskbar = $false
$form.Location = New-Object Drawing.Point (160, 120); $form.ClientSize = New-Object Drawing.Size ($w, $h)
$global:box = New-Object Windows.Forms.PictureBox; $box.Dock = 'Fill'; $form.Controls.Add($box)
$global:pics = @()
if ($noise) {
    $rnd = New-Object Random 7
    for ($i = 0; $i -lt 8; $i++) {
        $b = New-Object Drawing.Bitmap ($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        $bits = $b.LockBits((New-Object Drawing.Rectangle (0, 0, $w, $h)), [Drawing.Imaging.ImageLockMode]::WriteOnly, $b.PixelFormat)
        $bytes = New-Object byte[] ($bits.Stride * $h); $rnd.NextBytes($bytes)
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $bits.Scan0, $bytes.Length); $b.UnlockBits($bits)
        $global:pics += $b
    }
}
$global:timer = New-Object Windows.Forms.Timer; $timer.Interval = 15
$timer.add_Tick({
    $global:n++
    if ($global:noise) { $global:box.Image = $global:pics[$global:n % 8] }
    else { $global:box.BackColor = [Drawing.Color]::FromArgb(($global:n * 7) % 256, ($global:n * 13) % 256, ($global:n * 29) % 256) }
    if ($global:n % 30 -eq 0) { [IO.File]::WriteAllText('__FILE__', "$($global:n) $($global:clock.ElapsedMilliseconds)") }
})
$form.add_Shown({ $global:clock.Restart(); [IO.File]::WriteAllText('__FILE__', '0 0'); $global:timer.Start() })
[Windows.Forms.Application]::Run($form)
'@
    $file = "C:\ProgramData\RemoteHubLab\session\change-$([guid]::NewGuid().ToString('N').Substring(0, 8)).txt"
    $code = $code.Replace('__FILE__', $file).Replace('__NOISE__', $(if ($Noise) { '1' } else { '0' }))
    $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create($code))
    $until = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Invoke-LabGuest -Name $Name -ArgumentList $file -ScriptBlock { param($f) Test-Path $f })) {
        if ((Get-Date) -gt $until) {
            # whether the task ran at all, for the next look (run 20261021-062735, J-08: no file, no telling why)
            $why = try {
                Invoke-LabGuest -Name $Name -ArgumentList $task -ScriptBlock {
                    param($t)
                    $i = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
                    $ps = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | ForEach-Object { "$($_.ProcessId)@$($_.SessionId)" })
                    "task $(if ($i) { "last run $($i.LastRunTime), result $($i.LastTaskResult)" } else { 'not found' }); powershell.exe: $($ps -join ', ')"
                }
            } catch { "state not read: $($_.Exception.Message)" }
            Stop-LabInSession -Name $Name -Task $task
            throw "The changing window didn't come up in the $Account session within $TimeoutSec s (no $file) - $why."
        }
        Start-Sleep -Seconds 1
    }
    [pscustomobject]@{ Name = $Name; Task = $task; File = $file }
}

# Ends Start-LabChangingWindow's window; answers its ticks, ms and changes per second.
function Stop-LabChangingWindow {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Change)
    $t = "$(Invoke-LabGuest -Name $Change.Name -ArgumentList $Change.File -ScriptBlock { param($f) Get-Content $f -ErrorAction SilentlyContinue | Select-Object -Last 1; Remove-Item $f -ErrorAction SilentlyContinue })"
    Stop-LabInSession -Name $Change.Name -Task $Change.Task
    $ticks = 0; $ms = 0
    if ($t -match '^(\d+) (\d+)$') { $ticks = [int]$Matches[1]; $ms = [int]$Matches[2] }
    [ordered]@{ ticks = $ticks; ms = $ms; perSecond = $(if ($ms -gt 0) { [math]::Round($ticks * 1000.0 / $ms, 1) } else { 0 }) }
}

# The shell's own Paste (Explorer's "Paste") into a folder, as script text for the account's session
# (Invoke-LabInSession / Start-LabInSession; Invoke-LabPaste runs it and reads the answer). Files offered on demand
# (RemoteHub's virtual files, the RDP clipboard's file lists) are copied by the shell on a thread of its own that calls
# back into the clipboard's data object - and that object lives on the pasting (STA) thread, which therefore has to keep
# pumping window messages until the copy is done. A plain Start-Sleep pumps none, and then no file arrives (lab run
# 20261017-054704: D-CLIP-05, UI-CP-04, UI-CP-10, A-CLIP-01 all without a pasted file while text and pictures passed).
# Pumps (DoEvents) until -Count files are in the folder (subfolders included) and their total size stayed the same over
# one 2 s check, or -TimeoutSec passes; -Hold pumps for all of -TimeoutSec (a paste that is cancelled meanwhile).
# -Fresh empties the folder first. A folder that isn't rooted is under the session user's profile. The answer is one
# line "JSON:{...}": folder, files (Path relative to the folder, Name, Length, SHA-256 Hash), formats (the clipboard's
# formats before the paste - names only, which fetches nothing), apartment (STA expected), complete, seconds.
function Get-LabPasteCode {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Folder,
        [int]$Count = 1,
        [int]$TimeoutSec = 120,
        [switch]$Fresh,
        [switch]$Hold
    )
    $code = @'
Add-Type -AssemblyName System.Windows.Forms
$d = '__DIR__'
if (-not [IO.Path]::IsPathRooted($d)) { $d = Join-Path $env:USERPROFILE $d }
if (__FRESH__ -and (Test-Path -LiteralPath $d)) { Remove-Item -LiteralPath $d -Recurse -Force }
$null = New-Item -ItemType Directory -Path $d -Force
$formats = @(try { [System.Windows.Forms.Clipboard]::GetDataObject().GetFormats() } catch { "error: $($_.Exception.Message)" })
$start = Get-Date
(New-Object -ComObject Shell.Application).NameSpace($d).Self.InvokeVerb('paste')
$until = $start.AddSeconds(__TIMEOUT__); $last = -1; $complete = $false; $files = @()
do {
    $tick = (Get-Date).AddSeconds(2)
    while ((Get-Date) -lt $tick) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 50 }
    $files = @(Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue)
    $size = [long]($files | Measure-Object Length -Sum).Sum
    $complete = $files.Count -ge __COUNT__ -and $size -eq $last
    $last = $size
} while ((__HOLD__ -or -not $complete) -and (Get-Date) -lt $until)
$list = @($files | ForEach-Object {
        $h = try { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } catch { '' }
        [pscustomobject]@{ Path = $_.FullName.Substring($d.Length).TrimStart('\'); Name = $_.Name; Length = $_.Length; Hash = $h }
    })
'JSON:' + (ConvertTo-Json -Compress -Depth 4 -InputObject @{ folder = $d; files = $list; formats = $formats; apartment = "$([Threading.Thread]::CurrentThread.ApartmentState)"; complete = $complete; seconds = [int]((Get-Date) - $start).TotalSeconds })
'@
    $fill = @{
        '__DIR__'     = $Folder -replace "'", "''"
        '__FRESH__'   = $(if ($Fresh) { '$true' } else { '$false' })
        '__HOLD__'    = $(if ($Hold) { '$true' } else { '$false' })
        '__TIMEOUT__' = "$TimeoutSec"
        '__COUNT__'   = "$Count"
    }
    foreach ($k in $fill.Keys) { $code = $code.Replace($k, $fill[$k]) }
    $code
}

# Get-LabPasteCode run in the account's session (Invoke-LabInSession), its answer as an object: folder, files (always
# an array), formats, apartment, complete, seconds. Throws when the session code answered no result.
function Invoke-LabPaste {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Folder,
        [string]$Account = 'User',
        [int]$Count = 1,
        [int]$TimeoutSec = 120,
        [switch]$Fresh,
        [switch]$Hold,
        [switch]$Elevated
    )
    $code = Get-LabPasteCode -Folder $Folder -Count $Count -TimeoutSec $TimeoutSec -Fresh:$Fresh -Hold:$Hold
    $raw = "$(Invoke-LabInSession -Name $Name -Account $Account -Elevated:$Elevated -TimeoutSec ($TimeoutSec + 120) -ScriptBlock ([scriptblock]::Create($code)))"
    if ($raw -notmatch '(?m)^JSON:(.*)$') { throw "${Name} / ${Account}: the paste into '$Folder' answered no result: $($raw.Trim())" }
    $r = $Matches[1].Trim() | ConvertFrom-Json
    $r.files = @($r.files | Where-Object { $_ })
    $r.formats = @($r.formats | Where-Object { $_ })
    $r
}

# The person at the device (lab\guest\RhPerson.ps1) in the account's session: reads the agent's dialogs and banner by
# OCR, clicks their buttons by label, reads the PIN, gets / sets the clipboard. Answers its JSON as an object.
# -Do marker runs until stopped: use Start-LabMarker.
function Invoke-LabPerson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('windows', 'read', 'click', 'locate', 'pin', 'clip-get', 'clip-set', 'screen-read', 'key', 'close-window', 'hover', 'uia')][string]$Do,
        [string]$Account = 'User',
        [string]$Title = '*',
        [string]$Text,
        [string]$Value,
        [int]$TimeoutSec = 30
    )
    # The agent's dialogs and banner belong to SYSTEM: input from the user's session doesn't reach them (UIPI). So the
    # session only finds the place (OCR) and the console mouse / keyboard does the click, the hover and the key.
    if ($Do -eq 'key') {
        Assert-CimResult (Invoke-CimMethod -InputObject (Get-LabKeyboard $Name) -MethodName TypeKey -Arguments @{ keyCode = [Convert]::ToInt32($Value, 16) }) "${Name}: TypeKey $Value"
        return [pscustomobject]@{ key = $Value }
    }
    if ($Do -in 'click', 'hover') {
        $found = Invoke-LabPerson -Name $Name -Do $(if ($Do -eq 'click') { 'locate' } else { 'windows' }) -Account $Account -Title $Title -Text $Text -TimeoutSec $TimeoutSec
        if ($Do -eq 'click') {
            if ($found.PSObject.Properties['x']) { Send-LabMouse -Name $Name -X $found.x -Y $found.y; $found.clicked = $true }
            else { Write-Warning "${Name}: '$Text' not found on the agent's window; OCR read: $(@($found.lines) -join ' | ')$(if ($found.PSObject.Properties['seen']) { '; windows seen: ' + (@($found.seen) -join ' || ') })" }
            return $found
        }
        $w = @($found | Where-Object { $_.Title -like $Title -or $_.Title -like "RemoteHub*$Title" }) | Select-Object -First 1
        if ($w) { Send-LabMouse -Name $Name -X ($w.X + [int]($w.Width / 2)) -Y ($w.Y + [int]($w.Height / 2)) -Action Move; Start-Sleep -Seconds 2 }
        return [pscustomobject]@{ before = $w; after = @(Invoke-LabPerson -Name $Name -Do windows -Account $Account | Where-Object Title -eq $w.Title) | Select-Object -First 1 }
    }
    if ($Do -eq 'locate') { $Do = 'click'; $Value = 'locate' }
    $answer = "C:\ProgramData\RemoteHubLab\session\person-$([guid]::NewGuid().ToString('N').Substring(0, 8)).json"
    $json = $null
    $noServer = ''
    # through the person server in the session (RhPerson -Do serve, one PowerShell Direct call); RHLAB_PERSON_SERVER=0
    # or a server that doesn't come up: a scheduled task per action, as before
    if ($env:RHLAB_PERSON_SERVER -ne '0') {
        $req = @{ do = $Do; title = $Title; timeout = $TimeoutSec; out = $answer }
        if ($Text) { $req.text = $Text }
        if ($PSBoundParameters.ContainsKey('Value')) { $req.value = $Value }
        # an error on the way (PowerShell Direct dropped, ...) may come after the server ran the action: no second run of
        # it the old way (a click done twice opens two dialogs) - the step fails with the error instead
        try { $json = Invoke-LabPersonServer -Name $Name -Account $Account -Request $req -TimeoutSec $TimeoutSec }
        catch { throw "RhPerson -Do $Do through the person server in $Account's session failed: $($_.Exception.Message)" }
        if ("$json" -like 'RHLAB-NOANSWER*') { throw "RhPerson -Do $Do gave no answer in $Account's session: $(("$json" -split '\|', 2)[1])" }
        # no server: the reason is kept for the error below, should the scheduled task give no answer either
        if ("$json" -like 'RHLAB-NOSERVER*') { $noServer = ("$json" -split '\|', 2)[1]; $json = $null }
        if ($json) {
            $s = $json | ConvertFrom-Json
            if ($s -isnot [array] -and $s.PSObject.Properties['serveError']) { throw "RhPerson -Do $Do gave no answer: ERROR: $($s.serveError)" }
        }
    }
    $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $cmd = "& 'C:\ProgramData\RemoteHubLab\RhPerson.ps1' -Do $Do -Title $(& $q $Title) -TimeoutSec $TimeoutSec -Out '$answer'"
    if ($Text) { $cmd += " -Text $(& $q $Text)" }
    if ($PSBoundParameters.ContainsKey('Value')) { $cmd += " -Value $(& $q $Value)" }
    if (-not $json) { Copy-LabPerson -Name $Name }
    foreach ($try in 1..2) {
        if ($json) { break }
        $raw = try { Invoke-LabInSession -Name $Name -Account $Account -TimeoutSec ($TimeoutSec + 60) -ScriptBlock ([scriptblock]::Create($cmd)) }
        catch { if ($noServer) { throw "$($_.Exception.Message) (before that: $noServer)" } else { throw } }
        # RhPerson writes UTF-8 without a BOM: read as UTF-8 (Get-Content would take it as ANSI: a middle dot or an arrow comes out as two or three wrong characters)
        $json = Invoke-LabGuest -Name $Name -ArgumentList $answer -ScriptBlock { param($f) if (Test-Path $f) { [IO.File]::ReadAllText($f, [Text.Encoding]::UTF8); Remove-Item $f } }
        # an empty answer (the session's task didn't run) is tried once more; an ERROR from the script is not
        if ($json -or "$raw".Trim()) { break }
        Start-Sleep -Seconds 3
    }
    if (-not $json) { throw "RhPerson -Do $Do gave no answer: $raw" }
    $r = $json | ConvertFrom-Json
    # The banner and the privacy screen are kept out of screen captures (display affinity): the session's OCR sees what
    # is below them - nothing, or a desktop icon's label once the banner is taller (lab run 20261019-121308, "cle Bin").
    # The console picture shows them - read the window there when it is excluded, or the session read nothing.
    $excluded = $r.PSObject.Properties['excluded'] -and $r.excluded
    if ($Do -eq 'read' -and $r.found -and ($excluded -or -not "$($r.text)".Trim())) {
        $c = Read-LabConsoleWindow -Name $Name -Window $r.window
        $r.text = $c.text; $r.lines = $c.lines
        $r | Add-Member -NotePropertyName source -NotePropertyValue 'console' -Force
    }
    elseif ($Do -eq 'click' -and $Value -eq 'locate' -and -not $r.clicked) {
        # every agent window, dialogs first (as the session's search); the console's lines are kept apart from the
        # session's, which say what the dialog read while it was there
        $consoleLines = @()
        foreach ($w in @(Invoke-LabPerson -Name $Name -Do windows -Account $Account -Title $Title | Sort-Object { if ($_.Title -eq 'RemoteHub') { 1 } else { 0 } })) {
            $c = Read-LabConsoleWindow -Name $Name -Window $w
            $word = $c.words | Where-Object { $_.text -eq $Text -or ($_.text -replace '[^\p{L}\p{Nd}]+.*$', '') -eq $Text -or ($_.text -replace '^[^\p{L}\p{Nd}]+', '') -eq $Text } |
                Sort-Object { if (($_.line -replace '[^\p{L}\p{Nd} ]', '').Trim() -eq $Text) { 0 } else { 1 } }, { -$_.y } | Select-Object -First 1
            if ($word) {
                $r = [pscustomobject]@{ clicked = $true; text = $Text; x = [int]($word.x + $word.w / 2); y = [int]($word.y + $word.h / 2); title = $w.Title; lines = $c.lines; source = 'console' }
                break
            }
            $consoleLines += @("[$($w.Title)] $(@($c.lines) -join ' / ')")
        }
        if (-not $r.clicked) {
            if (-not @($r.lines).Count -and $consoleLines.Count) { $r.lines = $consoleLines }
            $r | Add-Member -NotePropertyName consoleLines -NotePropertyValue $consoleLines -Force
        }
    }
    $r
}

# OCR of one window's area in the console picture (Hyper-V): words with screen coordinates, lines, text.
function Read-LabConsoleWindow {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Window)
    $p = Join-Path ([IO.Path]::GetTempPath()) "rhlab-console-$([guid]::NewGuid().ToString('N').Substring(0, 8)).png"
    try { $null = Get-LabScreenshot -Name $Name -Path $p; $ocr = Get-LabOcr -Path $p } finally { Remove-Item $p -ErrorAction SilentlyContinue }
    $in = @($ocr.words | Where-Object {
            $cx = $_.x + $_.w / 2; $cy = $_.y + $_.h / 2
            $cx -ge $Window.X -and $cx -lt $Window.X + $Window.Width -and $cy -ge $Window.Y -and $cy -lt $Window.Y + $Window.Height })
    # the OCR line of each word (an older RhPerson gives none: then the words of one row, top to bottom)
    $lines = if (@($in | Where-Object { $_.PSObject.Properties['line'] -and $_.line }).Count) { @($in | ForEach-Object { $_.line } | Select-Object -Unique) }
    else { @($in | Group-Object { [int]($_.y / 10) } | Sort-Object { [int]$_.Name } | ForEach-Object { (@($_.Group | Sort-Object x) | ForEach-Object text) -join ' ' }) }
    [pscustomobject]@{ words = $in; lines = $lines; text = $lines -join "`n" }
}

# A button on the agent's banner clicked by the person at the device (console mouse), found by its WHOLE label in the
# console picture: the banner is kept out of screen captures, so the session's OCR doesn't see it. The whole label is
# needed - "Hide" alone is in "Hide screen" and "Hide apps...", and Invoke-LabPerson -Do click took "Hide apps..." in
# runs 20261016-184035 and -212514. A word counts when the label's next word follows it on the same row.
# The banner is opened first (hover; not the pill) and looked for up to -TimeoutSec; the console picture is kept at
# -EvidencePath when given. Answers clicked / label / x / y / lines / window, or clicked = $false with why.
function Invoke-LabBannerClick {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Label,
        [string]$EvidencePath,
        [string]$Account = 'User',
        [int]$TimeoutSec = 10
    )
    $null = Invoke-LabPerson -Name $Name -Account $Account -Do hover -Title 'RemoteHub'
    # (asked again: right after a change the banner may be missing from the list for a moment)
    $w = $null
    $until = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        $w = @(Invoke-LabPerson -Name $Name -Account $Account -Do windows | Where-Object Title -eq 'RemoteHub') | Select-Object -First 1
        if ($w -or (Get-Date) -gt $until) { break }
        Start-Sleep -Seconds 2
    }
    if (-not $w) { return [pscustomobject]@{ clicked = $false; label = $Label; why = 'no banner'; lines = @(); window = $null } }
    $keep = [bool]$EvidencePath
    $p = if ($keep) { $EvidencePath } else { Join-Path ([IO.Path]::GetTempPath()) "rhlab-banner-$([guid]::NewGuid().ToString('N').Substring(0, 8)).png" }
    try { $null = Get-LabScreenshot -Name $Name -Path $p; $ocr = Get-LabOcr -Path $p }
    finally { if (-not $keep) { Remove-Item $p -ErrorAction SilentlyContinue } }
    $in = @($ocr.words | Where-Object {
            $cx = $_.x + $_.w / 2; $cy = $_.y + $_.h / 2
            $cx -ge $w.X -and $cx -lt $w.X + $w.Width -and $cy -ge $w.Y -and $cy -lt $w.Y + $w.Height })
    $first, $rest = $Label -split ' ', 2
    $next = if ($rest) { ($rest -split ' ')[0] } else { $null }
    $word = @($in | Where-Object { $_.text -eq $first } | Where-Object {
            $c = $_
            -not $next -or @($in | Where-Object { $_.text -like "$next*" -and [math]::Abs($_.y - $c.y) -le $c.h -and $_.x -gt $c.x -and ($_.x - $c.x - $c.w) -le 3 * $c.h }).Count -gt 0
        } | Sort-Object x) | Select-Object -First 1
    # the OCR line of each word (an older RhPerson gives none: then the words of one row, top to bottom)
    $lines = if (@($in | Where-Object { $_.PSObject.Properties['line'] -and $_.line }).Count) { @($in | ForEach-Object { $_.line } | Select-Object -Unique) }
    else { @($in | Group-Object { [int]($_.y / 10) } | Sort-Object { [int]$_.Name } | ForEach-Object { (@($_.Group | Sort-Object x) | ForEach-Object text) -join ' ' }) }
    if (-not $word) { return [pscustomobject]@{ clicked = $false; label = $Label; why = 'not on the banner'; lines = $lines; window = $w } }
    $x = [int]($word.x + $word.w / 2); $y = [int]($word.y + $word.h / 2)
    Send-LabMouse -Name $Name -X $x -Y $y
    [pscustomobject]@{ clicked = $true; label = $Label; why = $null; x = $x; y = $y; lines = $lines; window = $w }
}

# Windows' OCR of a picture file on this PC (a decoded frame, a console screenshot): text and lines.
function Get-LabOcr {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [ValidateRange(1, 4)][int]$Scale = 1)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Get-LabOcr: no picture at $Path (the step that should have written it failed or didn't run - see its result.json)." }
    $script = Join-Path $PSScriptRoot '..\guest\RhPerson.ps1'
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $out = Join-Path ([IO.Path]::GetTempPath()) "rhlab-ocr-$tag.json"
    # -Scale: the picture enlarged first. Windows' OCR drops short words in a small picture - in run 20261022-061254 it
    # read every option of the agent tab's 537x402 Ctrl+Alt+Del screen but the one-word "Lock", twice; twice as large
    # it reads them all. Coordinates in the answer are then in the enlarged picture's pixels.
    $read = $Path
    if ($Scale -gt 1) {
        Add-Type -AssemblyName System.Drawing
        $read = Join-Path ([IO.Path]::GetTempPath()) "rhlab-ocr-$tag.png"
        $img = [Drawing.Image]::FromFile($Path)
        try {
            $big = New-Object Drawing.Bitmap ($img.Width * $Scale), ($img.Height * $Scale)
            try {
                $g = [Drawing.Graphics]::FromImage($big)
                try { $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic; $g.DrawImage($img, 0, 0, $big.Width, $big.Height) } finally { $g.Dispose() }
                $big.Save($read, [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $big.Dispose() }
        }
        finally { $img.Dispose() }
    }
    try {
        $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Do ocr-file -Value $read -Out $out
        # RhPerson writes UTF-8 without a BOM: 5.1 would read it as ANSI and turn an en dash into three characters
        Get-Content $out -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    finally {
        Remove-Item $out -ErrorAction SilentlyContinue
        if ($read -ne $Path) { Remove-Item $read -ErrorAction SilentlyContinue }
    }
}

# Where a word is in a picture (OCR, exact match, case-insensitive; -Last for the last one): "x,y" of its middle,
# for the desktop harness' click step - or $null when it isn't there.
function Find-LabPictureText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text, [switch]$Last)
    $words = @((Get-LabOcr -Path $Path).words | Where-Object { $_.text -eq $Text })
    if (-not $words) { return $null }
    $w = if ($Last) { $words[-1] } else { $words[0] }
    "$([int]($w.x + $w.w / 2)),$([int]($w.y + $w.h / 2))"
}

# Edit distance of two names (OCR reads the small tile names on the sign-in screen only in parts: "min" for Admin).
function Get-LabTextDistance([string]$a, [string]$b) {
    $prev = 0..$b.Length
    for ($i = 1; $i -le $a.Length; $i++) {
        $cur = @($i) + @(0) * $b.Length
        for ($j = 1; $j -le $b.Length; $j++) {
            $c = if ($a[$i - 1] -ceq $b[$j - 1]) { 0 } else { 1 }
            $cur[$j] = [Math]::Min([Math]::Min($prev[$j] + 1, $cur[$j - 1] + 1), $prev[$j - 1] + $c)
        }
        $prev = $cur
    }
    $prev[$b.Length]
}

# The lines of text OCR reads in a part of a picture (fractions of its width and height), top to bottom: text (lower
# case, letters and digits only) and the middle of its first word.
function Get-LabPictureRows([string]$Path, [double]$X0, [double]$X1, [double]$Y0, [double]$Y1) {
    Add-Type -AssemblyName System.Drawing
    $img = [Drawing.Image]::FromFile($Path)
    try { $w = $img.Width; $h = $img.Height } finally { $img.Dispose() }
    $words = @((Get-LabOcr -Path $Path).words | Where-Object { $_.x -ge $w * $X0 -and $_.x -lt $w * $X1 -and $_.y -ge $h * $Y0 -and $_.y -lt $h * $Y1 } | Sort-Object y, x)
    $rows = @(); $row = $null
    foreach ($word in $words) {
        if (-not $row -or $word.y - $row.y -gt 12) { $row = [pscustomobject]@{ y = $word.y; words = @() }; $rows += $row }
        $row.words += $word
    }
    foreach ($r in $rows) {
        $first = @($r.words | Sort-Object x)[0]
        [pscustomobject]@{
            text = (@($r.words | Sort-Object x | ForEach-Object text) -join '').ToLowerInvariant() -replace '[^a-z0-9]', ''
            x = [int]($first.x + $first.w / 2); y = [int]($first.y + $first.h / 2)
        }
    }
}

# A part of a picture (fractions of its width and height) as black text on white: pixels that are near-white in the
# picture (white text over a photo) become black, everything else white - OCR then reads text it misses on a busy
# background. Written to -Out as PNG with a white margin.
function Save-LabWhiteText([string]$Path, [string]$Out, [double]$X0, [double]$X1, [double]$Y0, [double]$Y1, [int]$Min = 225) {
    Add-Type -AssemblyName System.Drawing
    $src = [Drawing.Bitmap]::FromFile($Path)
    try {
        $x = [int]($src.Width * $X0); $y = [int]($src.Height * $Y0)
        $w = [int]($src.Width * ($X1 - $X0)); $h = [int]($src.Height * ($Y1 - $Y0))
        $m = 20
        $dst = New-Object Drawing.Bitmap ($w + 2 * $m), ($h + 2 * $m)
        try {
            $g = [Drawing.Graphics]::FromImage($dst); $g.Clear([Drawing.Color]::White); $g.Dispose()
            for ($j = 0; $j -lt $h; $j++) {
                for ($i = 0; $i -lt $w; $i++) {
                    $c = $src.GetPixel($x + $i, $y + $j)
                    if ($c.R -ge $Min -and $c.G -ge $Min -and $c.B -ge $Min) { $dst.SetPixel($m + $i, $m + $j, [Drawing.Color]::Black) }
                }
            }
            $dst.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $dst.Dispose() }
    }
    finally { $src.Dispose() }
}

# An account's tile on the sign-in screen in a decoded frame (the harness' "frame" step). The tile names are small on the
# wallpaper and OCR reads them in parts ("or aluser", "min", "PSAdmin,.."), so an exact word doesn't do: each line of
# the list at the bottom left goes to the closest of the device's enabled local accounts, and the closest line of
# -Account wins. Answers XY ("x,y" for the harness' click step, or $null), X, Y, Line, Distance, Lines (all lines read)
# and Accounts. -Selected reads the chosen tile instead - its name stands large in the middle once clicked - and
# answers Selected (the name is -Account) and Lines, so a test can check the tile before it types a password.
function Find-LabSignInTile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Picture,
        [Parameter(Mandatory)][string]$Account,
        [switch]$Selected
    )
    $accounts = @(@(Invoke-LabGuest -Name $Name -ScriptBlock { @(Get-LocalUser | Where-Object Enabled | ForEach-Object Name) }) + $Account | Select-Object -Unique)
    if ($Selected) {
        # On a busy wallpaper (a lit temple behind the name) OCR of the whole frame can miss the large name and read
        # only "Password" below it: then the middle band is read again with only its near-white pixels kept (white
        # text on the picture becomes black on white). A line counts when it is the account or one letter off it and
        # no other account is as close.
        $lines = @(Get-LabPictureRows $Picture 0.25 0.75 (1 / 3) 0.6 | ForEach-Object text)
        $isAccount = {
            param($t)
            if (-not $t) { return $false }
            $d = Get-LabTextDistance $t $Account.ToLowerInvariant()
            $other = @($accounts | Where-Object { $_ -ne $Account } | Where-Object { (Get-LabTextDistance $t $_.ToLowerInvariant()) -le $d })
            $d -le 1 -and $other.Count -eq 0
        }
        $hit = @($lines | Where-Object { & $isAccount $_ }).Count -gt 0
        if (-not $hit) {
            $crop = Join-Path ([IO.Path]::GetTempPath()) "rhlab-tile-$([guid]::NewGuid().ToString('N').Substring(0, 8)).png"
            try {
                Save-LabWhiteText -Path $Picture -Out $crop -X0 0.25 -X1 0.75 -Y0 0.43 -Y1 0.56
                $more = @(Get-LabPictureRows $crop 0 1 0 1 | ForEach-Object text)
            }
            finally { Remove-Item $crop -ErrorAction SilentlyContinue }
            $lines += @($more | ForEach-Object { "crop:$_" })
            $hit = @($more | Where-Object { & $isAccount $_ }).Count -gt 0
        }
        return [pscustomobject]@{ Selected = $hit; Lines = $lines }
    }
    $rows = @(Get-LabPictureRows $Picture 0 (1 / 3) 0.5 1)
    $best = $null
    foreach ($r in $rows) {
        if (-not $r.text) { continue }
        $near = @($accounts | Sort-Object { Get-LabTextDistance $r.text $_.ToLowerInvariant() })[0]
        $dist = Get-LabTextDistance $r.text $Account.ToLowerInvariant()
        if ($near -eq $Account -and $dist -le [Math]::Max(1, $Account.Length - 2) -and (-not $best -or $dist -lt $best.dist)) {
            $best = [pscustomobject]@{ dist = $dist; row = $r }
        }
    }
    [pscustomobject]@{
        XY = $(if ($best) { "$($best.row.x),$($best.row.y)" } else { $null })
        X = $(if ($best) { $best.row.x } else { $null })
        Y = $(if ($best) { $best.row.y } else { $null })
        Line = $(if ($best) { $best.row.text } else { $null })
        Distance = $(if ($best) { $best.dist } else { $null })
        Lines = @($rows | ForEach-Object text)
        Accounts = $accounts
    }
}

# After Switch user Server 2025 shows the lock screen ("Press Ctrl+Alt+Delete to unlock") where Windows 11 shows the
# tiles. Answers 'cad' when a decoded frame asks for Ctrl+Alt+Delete, otherwise 'none' - written to the file of the
# harness' {do = 'cad'; ifFile = ...} step, so the tiles come up on both before the test looks for one.
function Get-LabCadAnswer {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Picture)
    if ((Get-LabOcr -Path $Picture).text -match 'Ctrl\s*\+\s*Alt\s*\+\s*Del') { 'cad' } else { 'none' }
}

# The marker window (RhPerson -Do marker) in the account's session: full screen, magenta, records keys, mouse and
# clipboard to a guest file. Returns the handle for Get-LabMarker / Stop-LabInSession.
function Start-LabMarker {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'User')
    Copy-LabPerson -Name $Name
    # In a session signed in moments before, the task sometimes stays Ready (last result 0x00041303) or its PowerShell
    # never starts (C-END-12 / C-END-13, runs 20261018-133341, 20261022-172139, 20261025-132336): then it is stopped
    # and started once more.
    $earlier = $null
    foreach ($try in 1..2) {
        $file = "C:\ProgramData\RemoteHubLab\session\marker-$([guid]::NewGuid().ToString('N').Substring(0, 8)).jsonl"
        $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create("& 'C:\ProgramData\RemoteHubLab\RhPerson.ps1' -Do marker -Out '$file'"))
        $until = (Get-Date).AddSeconds(60)
        $state = $null
        while ((Get-Date) -lt $until) {
            # shown and active (it has the keyboard), or shown and given up on the keyboard ('nofocus', after 30 s)
            $state = Invoke-LabGuest -Name $Name -ArgumentList $file -ScriptBlock {
                param($f)
                $ev = @(Get-Content $f -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
                if (-not @($ev | Where-Object e -eq 'shown')) { return $null }
                $no = @($ev | Where-Object e -eq 'nofocus') | Select-Object -First 1
                if ($no) { return "nofocus:$($no.fg)" }
                if (@($ev | Where-Object e -eq 'active')) { return 'active' }
                $null
            }
            if ($state) { break }
            Start-Sleep -Seconds 1
        }
        if ($state -or $try -eq 2) { break }
        $earlier = "try 1: task $task not shown within 60 s"
        Stop-LabInSession -Name $Name -Task $task
    }
    $shown = $state -eq 'active'
    # Not shown, or shown without the keyboard: no key can reach it, so "nothing arrived" would prove nothing. Callers
    # that count what arrived check Shown; Why says who kept the keyboard, or what the task did (state, last result,
    # its PowerShell still running).
    $why = $null
    if ($state -like 'nofocus:*') {
        $why = "shown, but the keyboard stayed with $($state.Substring(8))"
        Write-Warning "${Name}: the marker window didn't get the keyboard ($why)"
    }
    elseif (-not $shown) {
        $why = Invoke-LabGuest -Name $Name -ArgumentList $task, $file -ScriptBlock {
            param($t, $f)
            $st = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
            $info = Get-ScheduledTaskInfo -TaskName $t -ErrorAction SilentlyContinue
            $pidFile = "C:\ProgramData\RemoteHubLab\session\$t.pid"
            $p = if (Test-Path $pidFile) { Get-Process -Id ([int](Get-Content $pidFile | Select-Object -First 1)) -ErrorAction SilentlyContinue } else { $null }
            "task $(if ($st) { $st.State } else { 'missing' }), last result $(if ($info) { '0x{0:X8}' -f $info.LastTaskResult } else { '-' }), " +
            "pid file $(Test-Path $pidFile), PowerShell $(if ($p) { "running in session $($p.SessionId)" } else { 'not running' }), " +
            "marker file $(if (Test-Path $f) { "$((Get-Item $f).Length) bytes" } else { 'missing' })"
        }
        Write-Warning "${Name}: the marker window didn't show within 60 s ($why)"
    }
    if ($earlier) { $why = if ($why) { "$earlier | try 2: $why" } else { "$earlier | try 2 shown" } }
    [pscustomobject]@{ Task = $task; File = $file; Shown = $shown; Why = $why }
}

# What the marker recorded so far: one object per event (e = down/up/char/mdown/mup/move/wheel/clipboard/shown).
function Get-LabMarker {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Marker)
    Invoke-LabGuest -Name $Name -ArgumentList $Marker.File -ScriptBlock {
        param($f) @(Get-Content $f -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    }
}

function Copy-LabPerson {
    param([Parameter(Mandatory)][string]$Name)
    $src = Join-Path $PSScriptRoot '..\guest\RhPerson.ps1'
    Invoke-LabGuest -Name $Name -ArgumentList ([IO.File]::ReadAllText($src)) -ScriptBlock {
        param($text)
        $d = 'C:\ProgramData\RemoteHubLab'
        $null = New-Item -ItemType Directory -Path "$d\session" -Force
        icacls "$d\session" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        # only when it changed: a running RhPerson -Do serve ends when its file is replaced
        $f = "$d\RhPerson.ps1"
        if (-not (Test-Path $f) -or [IO.File]::ReadAllText($f) -ne $text) { [IO.File]::WriteAllText($f, $text) }
    }
}

# One person action through RhPerson -Do serve in the account's session (started when it isn't running): the script
# updated if it changed, the request written, the answer waited for - one PowerShell Direct call instead of a scheduled
# task and a new PowerShell per action. "RHLAB-NOSERVER|<why>" when the server didn't come up (the caller then runs the
# action the old way).
function Invoke-LabPersonServer {
    param([string]$Name, [string]$Account, [hashtable]$Request, [int]$TimeoutSec)
    $user = Resolve-LabAccount (Get-LabVm $Name) $Account
    $src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\guest\RhPerson.ps1'))
    Invoke-LabGuest -Name $Name -ArgumentList $src, $user, ($Request | ConvertTo-Json -Compress), $TimeoutSec -ScriptBlock {
        param($text, $u, $reqJson, $timeout)
        $d = 'C:\ProgramData\RemoteHubLab'
        $null = New-Item -ItemType Directory -Path "$d\session" -Force
        icacls "$d\session" /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        $f = "$d\RhPerson.ps1"
        $q = "$d\session\person-$($u -replace '[\\/:]', '_')"
        $null = New-Item -ItemType Directory -Path $q -Force
        # the server's process id while it runs (alive.txt is refreshed between requests, not during a long one)
        function ServerPid {
            $line = Get-Content (Join-Path $q 'alive.txt') -ErrorAction SilentlyContinue | Select-Object -First 1
            if (-not $line) { return 0 }
            $procId = ($line -split ' ', 2)[0]
            if (-not (Get-Process -Id ([int]$procId) -ErrorAction SilentlyContinue)) { return 0 }
            [int]$procId
        }
        function Fresh { $a = Join-Path $q 'alive.txt'; (Test-Path $a) -and ([datetime]::UtcNow - (Get-Item $a).LastWriteTimeUtc).TotalSeconds -lt 10 }
        # a new RhPerson: the running server is stopped first (it would quit at its next turn and leave a request behind)
        if (-not (Test-Path $f) -or [IO.File]::ReadAllText($f) -ne $text) {
            $old = ServerPid
            if ($old) {
                Set-Content (Join-Path $q 'stop.txt') 'new version'
                $until = (Get-Date).AddSeconds(15)
                while ((Get-Process -Id $old -ErrorAction SilentlyContinue) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200 }
                Stop-Process -Id $old -Force -ErrorAction SilentlyContinue
            }
            [IO.File]::WriteAllText($f, $text)
            Remove-Item (Join-Path $q 'alive.txt'), (Join-Path $q 'stop.txt') -ErrorAction SilentlyContinue
        }
        function Start-Server {
            Remove-Item (Join-Path $q 'stop.txt') -ErrorAction SilentlyContinue
            $task = "RHLab-person-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
            $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$f`" -Do serve -Value `"$q`""
            $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -MultipleInstances Parallel
            $null = Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings -Force
            Start-ScheduledTask -TaskName $task
            $started = Get-Date
            # The first start in a session compiles the helper and loads the OCR - 20-40 s, more on a VM that was just
            # reverted and is busy (lab run 20261019-193627, C-REC-01: not up within 60 s, and the fallback's own
            # PowerShell, compiling again next to it, gave no result within 120 s). Waited for up to 120 s while the
            # task still runs; a task that ended without a server is not waited for.
            $until = (Get-Date).AddSeconds(120)
            $state = ''
            while (-not ((ServerPid) -and (Fresh)) -and (Get-Date) -lt $until) {
                Start-Sleep -Milliseconds 300
                $state = "$((Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue).State)"
                if ($state -eq 'Ready' -and ((Get-Date) - $started).TotalSeconds -gt 5) { break }
            }
            $up = [bool]((ServerPid) -and (Fresh))
            if (-not $up) {
                $ti = Get-ScheduledTaskInfo -TaskName $task -ErrorAction SilentlyContinue
                $sessions = (@(quser.exe 2>&1 | ForEach-Object { ("$_" -replace '\s+', ' ').Trim() }) -join '; ')
                $logonUi = (@(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId) -join ', ')
                $script:noServer = "the person server didn't come up within $([int]((Get-Date) - $started).TotalSeconds) s (task $state, last result 0x$(if ($ti) { '{0:X8}' -f $ti.LastTaskResult } else { '?' }); sessions: $sessions; LogonUI in session(s): $(if ($logonUi) { $logonUi } else { 'none' }))"
            }
            Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
            $up
        }
        $script:noServer = ''
        $req = $reqJson | ConvertFrom-Json
        foreach ($try in 1, 2) {
            # no server: "RHLAB-NOSERVER|<why>" - the caller runs the action the old way and keeps the reason
            if (-not ((ServerPid) -and (Fresh)) -and -not (Start-Server)) { return "RHLAB-NOSERVER|$script:noServer" }
            $serverPid = ServerPid
            $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
            $reqFile = Join-Path $q "$id.req.json"
            [IO.File]::WriteAllText((Join-Path $q "$id.tmp"), $reqJson)
            Move-Item (Join-Path $q "$id.tmp") $reqFile
            # the server answers an action that doesn't finish at max(5, timeout) + 50 s (RhPerson -Do serve) and needs
            # up to about 15 s more for its list of windows that don't respond: waited for 30 s beyond its deadline
            $wait = [Math]::Max(5, $timeout) + 80
            $sent = Get-Date
            $until = $sent.AddSeconds($wait)
            $ended = $false
            # a handle held while waiting: an ended server's exit code can be read only through one opened before it ended
            $serverProc = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
            if ($serverProc) { try { $null = $serverProc.Handle } catch { } }
            while (-not (Test-Path $req.out) -and (Get-Date) -lt $until) {
                Start-Sleep -Milliseconds 150
                if (-not (Get-Process -Id $serverPid -ErrorAction SilentlyContinue)) { $ended = $true; break }
            }
            if (Test-Path $req.out) {
                Start-Sleep -Milliseconds 50
                # the renamed answer can still be held for a moment (lab run 20261019-193627, C-REC: "being used by
                # another process" - a scanner opening the new file): read again for up to 5 s
                $answer = $null
                $readUntil = (Get-Date).AddSeconds(5)
                while ($null -eq $answer) {
                    try { $answer = [IO.File]::ReadAllText($req.out, [Text.Encoding]::UTF8) }
                    catch { if ((Get-Date) -gt $readUntil) { throw }; Start-Sleep -Milliseconds 200 }
                }
                Remove-Item $req.out -ErrorAction SilentlyContinue
                return $answer
            }
            # never picked up (the server ended before reading it): safe to give to a new server once
            if ((Test-Path $reqFile) -and $try -eq 1) { Remove-Item $reqFile -ErrorAction SilentlyContinue; continue }
            Remove-Item $reqFile -ErrorAction SilentlyContinue
            $secs = [int]((Get-Date) - $sent).TotalSeconds
            if ($ended) {
                # the process ended in the middle of the action (run 20261017-054704, C-AUD-05: within seconds, not after
                # the deadline): Windows' own record of a crash of powershell.exe since the request, if there is one
                $crash = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = $sent.AddSeconds(-2) } -MaxEvents 10 -ErrorAction SilentlyContinue |
                        Where-Object { $_.Message -match 'powershell' } | Select-Object -First 1 |
                        ForEach-Object { ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(400, ($_.Message -replace '\s+', ' ').Length)) })
                # its exit code tells a native crash (0xC0000005), a stack overflow (0xC00000FD), Ctrl+C/Break
                # (0xC000013A), an exit of its own or a kill from outside apart - and which request it was doing
                $exit = try { if ($serverProc -and $serverProc.WaitForExit(2000)) { '0x{0:X8}' -f $serverProc.ExitCode } else { '?' } } catch { '?' }
                # the error it ended with, as RhPerson -Do serve writes it down
                $lastError = Get-Content (Join-Path $q 'last-error.txt') -Raw -ErrorAction SilentlyContinue
                if ($lastError -and (Get-Item (Join-Path $q 'last-error.txt')).LastWriteTime -ge $sent.AddSeconds(-2)) { $exit += "; its error: $($lastError.Trim())" }
                $why = "the person server (process $serverPid, exit code $exit) ended $secs s after taking the request ($($req.do) '$($req.text)' $(if ("$($req.value)" -match '^set') { 'set...' } else { $req.value })), without an answer$(if ($crash) { '; Application log: ' + $crash[0] } else { ' (no crash in the Application log)' })"
                # ended within seconds with no crash recorded (runs 20261022-092309 and -112607, both in
                # New-LabClientConnection): Defender's behaviour monitoring stops a process without one - its detections
                $defender = @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1116, 1117, 1121, 1122, 1123, 1126; StartTime = $sent.AddSeconds(-5) } -MaxEvents 5 -ErrorAction SilentlyContinue |
                        ForEach-Object { "$($_.Id): $((($_.Message -replace '\s+', ' ').Trim()).Substring(0, [Math]::Min(300, ($_.Message -replace '\s+', ' ').Trim().Length)))" })
                $why += "; Defender: $(if ($defender) { $defender -join ' | ' } else { 'nothing' })"
            }
            else {
                # a server stuck in a request (a UI Automation call that never returns) would hold every later one: ended,
                # the next call starts a new one
                $why = "the person server (process $serverPid) took the request but didn't answer within $wait s (it should have at its deadline of $([Math]::Max(5, $timeout) + 50) s)"
                if (Get-Process -Id $serverPid -ErrorAction SilentlyContinue) {
                    Stop-Process -Id $serverPid -Force -ErrorAction SilentlyContinue
                    Remove-Item (Join-Path $q 'alive.txt') -ErrorAction SilentlyContinue
                }
            }
            # the server took the request but gave no answer: said so (not $null - the caller must not run the action again)
            return "RHLAB-NOANSWER|$why"
        }
        'RHLAB-NOANSWER|the request was not picked up by a new person server either'
    }
}

function Lock-LabSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'User')
    $null = Invoke-LabInSession -Name $Name -Account $Account -ScriptBlock { rundll32.exe user32.dll, LockWorkStation }
}

# Unlocks like a person: Ctrl+Alt+Del, password, Enter - then waits for the desktop.
function Unlock-LabSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'User', [int]$TimeoutSec = 60)
    Send-LabCtrlAltDel -Name $Name
    Start-Sleep -Seconds 2   # let the credential screen take focus before typing
    Send-LabText -Name $Name -Text (Get-LabConfig).Password
    Send-LabKey -Name $Name -Key Enter
    Wait-LabDesktop -Name $Name -Account $Account -TimeoutSec $TimeoutSec
}

#endregion

#region RemoteHub Agent

# The host's source address towards the VM - what the VM sees as the caller (firewall scope, health checks), also
# when the VM sits in another subnet behind a router.
function Get-LabHostAddress {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $vmIp = Get-LabVmAddress -Name $Name
    $source = Find-NetRoute -RemoteIPAddress $vmIp | Where-Object { $_.PSObject.Properties['IPAddress'] -and $_.IPAddress } |
        Select-Object -First 1 -ExpandProperty IPAddress
    if (-not $source) { throw "${Name}: no route from the host to $vmIp." }
    $source
}

# Runs the C# lab harness (tests\RemoteHub.LabHarness, built to LabRoot\harness) and returns its JSON answer.
# Every call has a time limit: a hang (msiexec waiting in the guest, a session that never answers) must fail the test,
# not block the lab queue. With -VmName a timeout also tells what the guest was busy with.
function Invoke-LabHarness {
    param(
        # empty strings are real values (an empty password)
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Arguments,
        [int]$TimeoutSec = 900,
        [string]$VmName
    )
    $exe = Join-Path (Get-LabConfig).LabRoot 'harness\RemoteHub.LabHarness.exe'
    if (-not (Test-Path $exe)) { throw "Lab harness not found at $exe - Start-LabRun.ps1 builds it." }
    # through the jump host set with Use-LabJump - not for commands that don't go over the network to a device
    # and not for the jump host itself
    $hostAt = [array]::IndexOf(@($Arguments), '--host')
    # (the "jump" command always goes through it - also to the jump host's own address, J-27)
    $toJump = $Arguments[0] -ne 'jump' -and $hostAt -ge 0 -and $hostAt + 1 -lt @($Arguments).Count -and $Arguments[$hostAt + 1] -eq $script:JumpAddress
    if ($script:JumpArgs -and -not $toJump -and $Arguments[0] -notin 'install', 'enrollment', 'seed', 'old-backup', 'share-conflict', 'probe') { $Arguments = @($Arguments) + $script:JumpArgs }
    $psi = New-Object Diagnostics.ProcessStartInfo $exe
    # quote like the C runtime expects (arguments may contain spaces and quotes)
    $psi.Arguments = ($Arguments | ForEach-Object {
            if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' } else { $_ }
        }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        $busy = ''
        if ($VmName) { try { $busy = ' Guest: ' + (Get-LabGuestActivity -Name $VmName) } catch { $busy = " (guest not readable: $($_.Exception.Message))" } }
        # what it was doing (the share commands write each step to stderr)
        $last = try { (($stderr.Result -split "`r?`n") | Where-Object { $_ } | Select-Object -Last 3) -join ' | ' } catch { '' }
        throw "Lab harness '$($Arguments[0])' timed out after $TimeoutSec s.$busy$(if ($last) { " Last steps: $last" })"
    }
    $p.WaitForExit()
    $out = $stdout.Result
    $answer = try { $out | ConvertFrom-Json } catch { throw "Lab harness gave no JSON: $out $($stderr.Result)" }
    if ($p.ExitCode -ne 0 -or $answer.PSObject.Properties['error']) { throw "Lab harness: $($answer.error)" }
    $answer
}

# What a guest is busy with (for timeouts): installer-related processes and the tail of the newest msiexec log in a
# temp folder.
function Get-LabGuestActivity {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $procs = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(msiexec|TiWorker|TrustedInstaller|drvinst|pnputil|RemoteHubAgent|wusa|MoUsoCoreWorker)\.exe$' } |
                ForEach-Object { "$($_.Name)[$($_.ProcessId)] $(($_.CommandLine -replace '\s+', ' ').Trim())" })
        $log = Get-ChildItem 'C:\Users\*\AppData\Local\Temp', 'C:\Windows\Temp' -Recurse -Include '*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'install|update|RemoteHub' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $tail = if ($log) { "$($log.FullName): " + ((Get-Content $log.FullName -Tail 8 -ErrorAction SilentlyContinue) -join ' | ') } else { 'no msiexec log' }
        "processes: $($procs -join '; ') -- newest log $tail"
    }
}

# Installs the agent the way RemoteHub does (AgentInstaller.InstallOverRemoting, over PowerShell Direct): new device
# identity + pin, upload MSI and .rhagent, msiexec ENROLLMENT=..., read the fingerprint back. Scope defaults to the
# host's address. Returns the harness answer (deviceId, pin, port, installed.*).
# -KeepIdentity: no new enrollment - an update over remoting that keeps the device's certificate.
function Install-LabAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Msi,
        [string]$Scope,
        [int]$Port = 58732,
        [string]$Label,
        [switch]$KeepIdentity,
        [bool]$VirtualDisplay = $true,
        # MSI property AUTOSIGNIN (credential provider): $true / $false; not given = not passed
        [Nullable[bool]]$AutoSignIn,
        # the install dialog's "Allowed accounts" as typed (AgentInstaller.ParseGroups); not given = Administrators
        [string]$Groups,
        # a host file sent as the enrollment instead of the one made for this install (to break it on purpose)
        [string]$EnrollmentFile,
        # over WinRM HTTPS (pinned to -Cert, the listener's thumbprint) instead of PowerShell Direct - through the jump
        # host when Use-LabJump set one
        [switch]$ViaWinRm,
        [string]$Cert
    )
    # set up outside Initialize-LabVm: the VM is no longer what Initialize-LabVm left
    if (-not $script:LabInitializing) { Clear-LabVmState -Name $Name }
    $vm = Get-LabVm $Name
    $cfg = Get-LabConfig
    if (-not $Msi) { $Msi = Get-LabAgentMsi }
    Install-LabSigningCertificate -Name $Name
    if (-not $PSBoundParameters.ContainsKey('Scope')) { $Scope = Get-LabHostAddress -Name $Name }
    if (-not $Label) { $Label = $vm.Id }
    $harnessArgs = @('install', '--vm', $vm.Name, '--user', $vm.AdminAccount, '--password', $cfg.Password,
        '--msi', $Msi, '--label', $Label, '--port', "$Port", '--virtual-display', $(if ($VirtualDisplay) { '1' } else { '0' }))
    if ($Scope) { $harnessArgs += '--scope', $Scope }
    if ($KeepIdentity) { $harnessArgs += '--keep-identity', '1' }
    if ($null -ne $AutoSignIn) { $harnessArgs += '--auto-sign-in', $(if ($AutoSignIn) { '1' } else { '0' }) }
    if ($PSBoundParameters.ContainsKey('Groups')) { $harnessArgs += '--groups', $Groups }
    if ($EnrollmentFile) { $harnessArgs += '--enrollment-file', $EnrollmentFile }
    if ($ViaWinRm) {
        $harnessArgs += '--via', 'winrm', '--host', (Get-LabVmAddress -Name $Name), '--cert', $Cert
        if ($script:JumpArgs) { $harnessArgs += $script:JumpArgs }
    }
    Invoke-LabHarness $harnessArgs -TimeoutSec 600 -VmName $Name
}

# A .rhagent enrollment file as RemoteHub makes it (harness "enrollment"), written on the host: the lab's enrollment
# secret, no key. Scope defaults to the host's address towards the VM. Returns file and keyId - the device makes its own
# certificate, so pin and deviceId come from the claim afterwards (Get-LabAgentClaim).
function New-LabEnrollment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Path,
        [string]$Label,
        [int]$Port = 58732,
        [string]$Scope,
        [string]$Groups
    )
    $vm = Get-LabVm $Name
    if (-not $PSBoundParameters.ContainsKey('Scope')) { $Scope = Get-LabHostAddress -Name $Name }
    if (-not $Label) { $Label = $vm.Id }
    $null = New-Item -ItemType Directory -Path (Split-Path $Path) -Force
    $a = @('enrollment', '--label', $Label, '--out', $Path, '--port', "$Port")
    if ($Scope) { $a += '--scope', $Scope }
    if ($Groups) { $a += '--groups', $Groups }
    Invoke-LabHarness $a -TimeoutSec 60
}

# Makes the VM trust the lab's code-signing certificate (lab\lab-signing.cer from New-LabSigningCertificate.ps1) in
# LocalMachine Root + TrustedPublisher, so builds signed with -LabSigning pass the agent's signature checks there as a
# release would. Nothing to do when the file isn't there (an unsigned build). The VM's checkpoint undoes it.
function Install-LabSigningCertificate {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $cer = Join-Path $PSScriptRoot '..\lab-signing.cer'
    # git-ignored: a lab folder copied without it leaves a -LabSigning build untrusted on a VM whose checkpoint doesn't
    # trust it yet (the credential provider is then refused as not signed by a trusted publisher)
    if (-not (Test-Path $cer)) { Write-Warning "Install-LabSigningCertificate: $cer is missing - $Name won't trust builds made with -LabSigning"; return }
    $bytes = [IO.File]::ReadAllBytes($cer)
    Invoke-LabGuest -Name $Name -ArgumentList (, $bytes) -ScriptBlock {
        param([byte[]]$bytes)
        $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)
        foreach ($storeName in 'Root', 'TrustedPublisher') {
            $store = [Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'LocalMachine')
            $store.Open('ReadWrite')
            try { $store.Add($cert) } finally { $store.Close() }
        }
    }
}

# The agent MSI under test (LabRoot\msi), or with -Previous the release before it (LabRoot\msi\previous) - both
# published by Start-LabRun.ps1.
function Get-LabAgentMsi {
    [CmdletBinding()]
    param([switch]$Previous)
    $dir = Join-Path (Get-LabConfig).LabRoot $(if ($Previous) { 'msi\previous' } else { 'msi' })
    $msi = Get-ChildItem $dir -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $msi) { throw "No agent MSI in $dir - Start-LabRun.ps1 copies the newest one$(if ($Previous) { ' and the one before it' }) there." }
    $msi
}

# RemoteHub's own package (LabRoot\client): the one of the build under test, -Previous the release before it,
# -Legacy the newest RDP Manager package - all published by Start-LabRun.ps1.
function Get-LabClientMsi {
    [CmdletBinding()]
    param([switch]$Previous, [switch]$Legacy)
    $dir = Join-Path (Get-LabConfig).LabRoot $(if ($Previous) { 'client\previous' } elseif ($Legacy) { 'client\legacy' } else { 'client' })
    $m = Get-ChildItem $dir -Filter '*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $m) { throw "No RemoteHub package in $dir - Start-LabRun.ps1 copies it there." }
    $m
}

# The version an agent MSI file name carries (RemoteHub-Agent-0.16.5-x64.msi -> 0.16.5).
function Get-LabMsiVersion {
    param([Parameter(Mandatory)][string]$Path)
    (Split-Path $Path -Leaf) -replace '^RemoteHub-Agent-(.+)-x64\.msi$', '$1'
}

# Update through the running agent, like RemoteHub's "Install / update RemoteHub Agent" (harness "update"). Returns
# the verdict RemoteHub would show (updated / notInstalled / noAnswer / failed), the health afterwards, the pin to use
# from now on (expectedPin) and what the agent answers to the old pin (oldPinNow).
function Update-LabAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [string]$Msi,
        [int]$Port = 58732,
        [switch]$NewIdentity,
        [string]$EnrollmentFile,
        [bool]$VirtualDisplay = $true,
        [switch]$HoldSession,
        # MSI property AUTOSIGNIN, as RemoteHub's update passes it; not given = not passed (the device keeps its choice)
        [Nullable[bool]]$AutoSignIn
    )
    $vm = Get-LabVm $Name
    $cfg = Get-LabConfig
    if (-not $Msi) { $Msi = Get-LabAgentMsi }
    Install-LabSigningCertificate -Name $Name
    $harnessArgs = @('update', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', $vm.AdminAccount, '--password', $cfg.Password, '--msi', $Msi,
        '--virtual-display', $(if ($VirtualDisplay) { '1' } else { '0' }))
    if ($NewIdentity) { $harnessArgs += '--new-identity', $vm.Id, '--scope', (Get-LabHostAddress -Name $Name) }
    if ($EnrollmentFile) { $harnessArgs += '--enrollment-file', $EnrollmentFile }
    if ($HoldSession) { $harnessArgs += '--hold-session', '1' }
    if ($null -ne $AutoSignIn) { $harnessArgs += '--auto-sign-in', $(if ($AutoSignIn) { '1' } else { '0' }) }
    Invoke-LabHarness $harnessArgs -TimeoutSec 900 -VmName $Name
}

# Runs a script in a PowerShell session through the agent (harness "run"), as the admin account unless -Account says
# otherwise. Returns output (strings) and errors.
function Invoke-LabAgentCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [Parameter(Mandatory)][string]$Script,
        [ValidateSet('Admin', 'User', 'Console')][string]$Account = 'Admin',
        [int]$Port = 58732
    )
    $vm = Get-LabVm $Name
    Invoke-LabHarness @('run', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', (Resolve-LabAccount $vm $Account), '--password', (Get-LabConfig).Password, '--script', $Script) -TimeoutSec 300 -VmName $Name
}

# Signs out whoever is on the console and waits for the sign-in screen (LogonUI running, no explorer.exe), then a few
# seconds for LogonUI to load its credential providers.
function Disconnect-LabConsole {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        # Every interactive session with a user has an explorer.exe; sign each of them out.
        $sessions = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object SessionId | Sort-Object -Unique)
        foreach ($id in $sessions) { logoff.exe $id }
        $deadline = (Get-Date).AddSeconds(120)
        while ((Get-Date) -lt $deadline) {
            $logonUi = @(Get-Process LogonUI -ErrorAction SilentlyContinue)
            $users = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")
            if ($logonUi.Count -gt 0 -and $users.Count -eq 0) { return }
            Start-Sleep -Seconds 2
        }
        $state = "sessions signed out: $($sessions -join ', '); LogonUI in: $(@(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId) -join ', '); " +
            "explorer in: $(@(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object SessionId) -join ', ')"
        throw "the console didn't reach the sign-in screen ($state)"
    }
    Start-Sleep -Seconds 5
}

# Automatic sign-in through the agent desktop, as RemoteHub's desktop tab does it (harness "auto-sign-in"), signed in to
# the agent as the console account. Returns rights, features, the console session and every status ("State: message").
function Invoke-LabAutoSignIn {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [switch]$ViewOnly,
        [switch]$NoRequest,
        [string]$SignInPassword,
        [int]$Port = 58732
    )
    $vm = Get-LabVm $Name
    $harnessArgs = @('auto-sign-in', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', $vm.ConsoleAccount, '--password', (Get-LabConfig).Password)
    if ($ViewOnly) { $harnessArgs += '--view-only', '1' }
    if ($NoRequest) { $harnessArgs += '--no-request', '1' }
    if ($SignInPassword) { $harnessArgs += '--password-for-sign-in', $SignInPassword }
    Invoke-LabHarness $harnessArgs -TimeoutSec 240 -VmName $Name
}

# A remote desktop session as RemoteHub's desktop tab runs it, step by step (harness "desktop", see DesktopHarness.cs):
# -Steps is a list of hashtables ({do = 'connect'}, {do = 'start'; consent = 'Ask'}, {do = 'wait'; state = 'Connected'},
# {do = 'frame'; out = 'x.png'}, ...). Signed in to the agent as -Account. PNGs and result.json go to -Out. Answers the
# harness' record: hello, steps, statuses, layouts, stats, clipboard, transfers, frames, images, closes.
function Invoke-LabDesktop {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [Parameter(Mandatory)][object[]]$Steps,
        [string]$Account = 'Admin',
        [string]$Password,
        [string]$Out,
        [int]$Port = 58732,
        [int]$TimeoutSec = 600
    )
    $vm = Get-LabVm $Name
    if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) "rhlab-desktop-$([guid]::NewGuid().ToString('N').Substring(0, 8))" }
    $null = New-Item -ItemType Directory -Path $Out -Force
    # the steps may type the lab password (keys steps at a sign-in or UAC prompt): the harness reads them from a temp
    # file that is deleted afterwards; the evidence gets a copy with the password replaced
    $stepId = [guid]::NewGuid().ToString('N').Substring(0, 6)
    $stepsFile = Join-Path ([IO.Path]::GetTempPath()) "rhlab-steps-$stepId.json"
    $json = ConvertTo-Json @($Steps) -Depth 6
    [IO.File]::WriteAllText($stepsFile, $json)
    $labPw = (Get-LabConfig).Password
    $redacted = $json
    foreach ($s in @($labPw, $Password) | Where-Object { $_ }) {
        # ConvertTo-Json escapes some characters (', \", \\): the password as written there too
        $redacted = $redacted.Replace((ConvertTo-Json $s).Trim('"'), '***').Replace($s, '***')
    }
    [IO.File]::WriteAllText((Join-Path $Out "steps-$stepId.json"), $redacted)
    $a = @('desktop', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', (Resolve-LabAccount $vm $Account), '--password', $(if ($PSBoundParameters.ContainsKey('Password')) { $Password } else { $labPw }),
        '--steps', $stepsFile, '--out', $Out, '--timeout', "$TimeoutSec")
    try { Invoke-LabHarness $a -TimeoutSec ($TimeoutSec + 60) -VmName $Name }
    finally { Remove-Item $stepsFile -ErrorAction SilentlyContinue }
}

# Invoke-LabDesktop in the background, for steps that wait while the test acts at the device (markFile / waitFile on
# host paths). Receive-LabDesktop waits for it and answers the harness' record. The jump route set with Use-LabJump
# goes along.
function Start-LabDesktop {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [Parameter(Mandatory)][object[]]$Steps,
        [string]$Account = 'Admin',
        [string]$Out,
        [int]$TimeoutSec = 600
    )
    $mod = Join-Path $PSScriptRoot 'RemoteHub.TestLab.psd1'
    $jumpArgs = $script:JumpArgs; $jumpAddress = $script:JumpAddress
    Start-Job -ArgumentList $mod, $Name, $Pin, (ConvertTo-Json @($Steps) -Depth 6), $Account, $Out, $TimeoutSec, $jumpArgs, $jumpAddress -ScriptBlock {
        param($mod, $name, $pin, $stepsJson, $account, $out, $timeout, $jumpArgs, $jumpAddress)
        Import-Module $mod
        if ($jumpArgs) { & (Get-Module RemoteHub.TestLab) { param($a, $h) $script:JumpArgs = $a; $script:JumpAddress = $h } $jumpArgs $jumpAddress }
        # (..): 5.1's ConvertFrom-Json writes an array as one object - the parentheses unroll it
        $p = @{ Name = $name; Pin = $pin; Steps = @(($stepsJson | ConvertFrom-Json)); Account = $account; TimeoutSec = $timeout }
        if ($out) { $p.Out = $out }
        Invoke-LabDesktop @p
    }
}

function Receive-LabDesktop {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Job)
    $r = Receive-Job $Job -Wait -AutoRemoveJob -ErrorAction Stop
    @($r)[-1]
}

# Waits for a file on this PC (the desktop harness writes one at a markFile step).
function Wait-LabHostFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [int]$TimeoutSec = 120)
    $until = (Get-Date).AddSeconds($TimeoutSec)
    while (-not (Test-Path $Path)) {
        if ((Get-Date) -gt $until) { throw "No $Path after $TimeoutSec s." }
        Start-Sleep -Milliseconds 500
    }
}

# How alike two pictures are (0..1): both scaled to 64x36 grey, 1 - mean absolute difference. A decoded frame and a
# console screenshot of the same screen are above 0.95.
function Compare-LabImage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$A, [Parameter(Mandatory)][string]$B)
    Add-Type -AssemblyName System.Drawing
    function Grey([string]$path) {
        $src = [Drawing.Image]::FromFile($path)
        try {
            $small = New-Object Drawing.Bitmap 64, 36
            $g = [Drawing.Graphics]::FromImage($small); $g.InterpolationMode = 'HighQualityBilinear'; $g.DrawImage($src, 0, 0, 64, 36); $g.Dispose()
            $v = New-Object double[] (64 * 36)
            for ($y = 0; $y -lt 36; $y++) { for ($x = 0; $x -lt 64; $x++) { $c = $small.GetPixel($x, $y); $v[$y * 64 + $x] = (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B) / 255 } }
            $small.Dispose(); $v
        }
        finally { $src.Dispose() }
    }
    $ga = Grey $A; $gb = Grey $B
    $sum = 0.0; for ($i = 0; $i -lt $ga.Count; $i++) { $sum += [math]::Abs($ga[$i] - $gb[$i]) }
    [math]::Round(1 - $sum / $ga.Count, 4)
}

# The share (0..1) of a picture's pixels near a colour - e.g. the marker app's magenta (255,0,255): a user desktop in
# a picture that must not show it. Every 4th pixel each way.
function Get-LabColorShare {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [int]$R = 255, [int]$G = 0, [int]$B = 255, [int]$Tolerance = 40)
    Add-Type -AssemblyName System.Drawing
    $bmp = [Drawing.Bitmap]::FromFile($Path)
    try {
        $hit = 0; $all = 0
        for ($y = 0; $y -lt $bmp.Height; $y += 4) {
            for ($x = 0; $x -lt $bmp.Width; $x += 4) {
                $c = $bmp.GetPixel($x, $y); $all++
                if ([math]::Abs($c.R - $R) -le $Tolerance -and [math]::Abs($c.G - $G) -le $Tolerance -and [math]::Abs($c.B - $B) -le $Tolerance) { $hit++ }
            }
        }
        [math]::Round($hit / [math]::Max(1, $all), 4)
    }
    finally { $bmp.Dispose() }
}

# RemoteHub's "Install / Remove credential provider" (harness "credential-provider"): over the agent's PowerShell, with
# the MSI under test as RemoteHub's package. Returns ok, installed, message (RemoteHub's text) and progress.
function Set-LabCredentialProvider {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [Parameter(Mandatory)][bool]$Install,
        [string]$Msi,
        [int]$Port = 58732
    )
    $vm = Get-LabVm $Name
    if (-not $Msi) { $Msi = Get-LabAgentMsi }
    Invoke-LabHarness @('credential-provider', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', $vm.AdminAccount, '--password', (Get-LabConfig).Password, '--msi', $Msi, '--install', $(if ($Install) { '1' } else { '0' })) -TimeoutSec 600 -VmName $Name
}

# One operation of RemoteHub's PowerShell connection or remote tools (harness "tool", see LabTools.cs), over the agent
# (-Via Agent, needs -Pin) or WinRM HTTPS (-Via WinRM). -Params are the op's own options (name -> value; arrays are
# joined with '|'). -UserName overrides the account name as typed (e.g. 'PSAdmin@host'); -Password the password.
function Invoke-LabTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Op,
        [ValidateSet('Agent', 'WinRM')][string]$Via = 'Agent',
        [string]$Pin,
        [hashtable]$Params = @{},
        [ValidateSet('Admin', 'User', 'Console')][string]$Account = 'Admin',
        [string]$UserName,
        [string]$Password,
        [int]$TimeoutSec = 900
    )
    $vm = Get-LabVm $Name
    $cfg = Get-LabConfig
    $user = if ($UserName) { $UserName } else { Resolve-LabAccount $vm $Account }
    $a = @('tool', '--op', $Op, '--via', $Via.ToLowerInvariant(), '--host', (Get-LabVmAddress -Name $Name),
        '--user', $user, '--password', $(if ($PSBoundParameters.ContainsKey('Password')) { $Password } else { $cfg.Password }))
    # "name@host" or "HOST\name" as typed: no ".\" prefix
    if ($UserName -and $UserName -match '[\\@]') { $a += '--local', '0' }
    if ($Via -eq 'Agent') {
        if (-not $Pin) { throw 'Invoke-LabTool -Via Agent needs -Pin.' }
        $a += '--pin', $Pin
    }
    foreach ($k in $Params.Keys) { $a += "--$k", (@($Params[$k]) -join '|') }
    Invoke-LabHarness $a -TimeoutSec $TimeoutSec -VmName $Name
}

# One script run on the device through RemoteHub's ScriptRunner (harness "tool --op script-run", see LabTools.cs),
# over the agent (-Pin) or WinRM HTTPS (-Cert): System, User (the console user unless -RunUser) or AsMe. The script
# text goes to the harness as a file on the host, removed afterwards. Returns the harness answer (.result.state,
# .exitCode, .message, .output, .runId). docs/test-lab-cases.md section 7.2.
function Invoke-LabScriptRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [ValidateSet('Agent', 'WinRM')][string]$Via = 'Agent',
        [string]$Pin,
        [string]$Cert,
        [Parameter(Mandatory)][ValidateSet('System', 'User', 'AsMe')][string]$Context,
        [Parameter(Mandatory)][string]$Body,
        [string]$RunUser,
        [int]$ScriptTimeoutSec = 0,
        [int]$CancelAfterSec = 0,
        [string]$ParamsJson,
        [int]$TimeoutSec = 900
    )
    $file = Join-Path ([IO.Path]::GetTempPath()) ("rhlab-script-" + [guid]::NewGuid().ToString('N') + '.ps1')
    try {
        # UTF-8 with a BOM, as RemoteHub writes it on the device
        [IO.File]::WriteAllText($file, $Body, (New-Object Text.UTF8Encoding($true)))
        $p = @{ context = $Context; body = $file }
        if ($RunUser) { $p['run-user'] = $RunUser }
        if ($ScriptTimeoutSec -gt 0) { $p['timeout'] = "$ScriptTimeoutSec" }
        if ($CancelAfterSec -gt 0) { $p['cancel-after'] = "$CancelAfterSec" }
        if ($ParamsJson) { $p['params-json'] = $ParamsJson }
        if ($Via -eq 'WinRM' -and $Cert) { $p['cert'] = $Cert }
        Invoke-LabTool -Name $Name -Op 'script-run' -Via $Via -Pin $Pin -Params $p -TimeoutSec $TimeoutSec
    }
    finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
}

# What a script run leaves on the device: RemoteHub's scheduled tasks and run folders (expected: none).
function Get-LabScriptLeftovers {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $tasks = @(Get-ScheduledTask -TaskPath '\RemoteHub\' -ErrorAction SilentlyContinue | ForEach-Object TaskName)
        $root = Join-Path $env:ProgramData 'RemoteHub\Runs'
        $folders = @()
        if (Test-Path -LiteralPath $root) { $folders = @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | ForEach-Object Name) }
        [pscustomobject]@{ Tasks = $tasks; Folders = $folders }
    }
}

# A raw check of the agent's listener (harness "probe", see LabProbes.cs). -Params as for Invoke-LabTool.
function Invoke-LabProbe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('health', 'http', 'tls', 'ws', 'hold', 'big')][string]$Kind,
        [hashtable]$Params = @{},
        [int]$Port = 58732,
        [int]$TimeoutSec = 300
    )
    $a = @('probe', '--kind', $Kind, '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port")
    foreach ($k in $Params.Keys) { $a += "--$k", (@($Params[$k]) -join '|') }
    Invoke-LabHarness $a -TimeoutSec $TimeoutSec -VmName $Name
}

# RemoteHub's WinRM setup script (RemotingSetup.BuildScript, as "Set up on the target..." makes it) for the VM's
# address, allowing the host by default; run in the guest as the admin account (elevated, as the script requires).
# Returns the script's output and whether it threw.
function Invoke-LabSetupScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string[]]$Allow,
        [bool]$Https = $true,
        [bool]$LocalAdmins = $true,
        [switch]$DryRun,
        [int]$TimeoutSec = 600,
        # the host the script is made for (a computer name, W-NAME-01); default the VM's address
        [string]$TargetHost
    )
    if (-not $TargetHost) { $TargetHost = Get-LabVmAddress -Name $Name }
    if (-not $Allow) { $Allow = @(Get-LabHostAddress -Name $Name) }
    $local = Join-Path $env:TEMP "RemoteHub-setup-$([guid]::NewGuid().ToString('N').Substring(0, 8)).ps1"
    $null = Invoke-LabHarness @('tool', '--op', 'script', '--host', $TargetHost, '--allow', ($Allow -join '|'),
        '--https', $(if ($Https) { '1' } else { '0' }), '--local-admins', $(if ($LocalAdmins) { '1' } else { '0' }), '--out', $local)
    try {
        $text = [IO.File]::ReadAllText($local)
        if ($DryRun) { $text = $text -replace '(?m)^\$DryRun = \$false', '$DryRun = $true' }
        # in a job with a time limit: the script once hung on W11 for hours (2026-10-13); a hang must fail the test
        # and say which step it was in (the last "<step>..." line)
        Invoke-LabGuest -Name $Name -ArgumentList $text, $TimeoutSec -ScriptBlock {
            param($text, $TimeoutSec)
            $file = Join-Path $env:TEMP 'RemoteHub-setup.ps1'
            Set-Content -Path $file -Value $text -Encoding UTF8
            $job = Start-Job -ArgumentList $file -ScriptBlock {
                param($file)
                # a client's default policy (Restricted) blocks .ps1 files; an admin allows it for their window the same way
                Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
                try { & $file *>&1 | ForEach-Object { "$_" } } catch { "FAILED: $($_.Exception.Message)" }
            }
            $finished = Wait-Job $job -Timeout $TimeoutSec
            $out = @(Receive-Job $job -Keep 2>&1 | ForEach-Object { "$_" })
            $failed = $null
            if (-not $finished) {
                Stop-Job $job
                $failed = "The setup script didn't finish within $TimeoutSec s. Last output: $(@($out)[-1])"
            }
            elseif ($out -and $out[-1] -like 'FAILED: *') { $failed = $out[-1].Substring(8); $out = @($out | Select-Object -SkipLast 1) }
            Remove-Job $job -Force
            Remove-Item $file -ErrorAction SilentlyContinue
            [pscustomobject]@{ Output = @($out); Failed = $failed }
        }
    }
    finally { Remove-Item $local -ErrorAction SilentlyContinue }
}

# WinRM / firewall / certificate state in the guest, for the setup-script checks.
function Get-LabWinRmState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        # WSMan:\ asks "start the WinRM service?" when it is stopped (the client default) - over PowerShell Direct that
        # question goes to the lab process, which nobody answers: it hung W11 runs for hours (2026-10-13)
        $winrmUp = (Get-Service WinRM).Status -eq 'Running'
        $found = @(if ($winrmUp) { Get-ChildItem WSMan:\localhost\Listener -ErrorAction SilentlyContinue })
        $listeners = @($found | ForEach-Object {
                $keys = @($_.Keys)
                $thumb = (Get-ChildItem "WSMan:\localhost\Listener\$($_.Name)" | Where-Object Name -eq 'CertificateThumbprint').Value
                [pscustomobject]@{ Transport = ($keys | Where-Object { $_ -like 'Transport=*' }) -replace 'Transport=', ''; Thumbprint = $thumb }
            })
        $rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'RemoteHub-WinRM-*' -or $_.Name -like 'RdpManager-WinRM-*' -or $_.Name -like 'WINRM-HTTP-In-TCP*' } |
                ForEach-Object {
                    [pscustomobject]@{
                        Name = $_.Name; Enabled = [string]$_.Enabled
                        Port = @(($_ | Get-NetFirewallPortFilter).LocalPort); RemoteAddress = @(($_ | Get-NetFirewallAddressFilter).RemoteAddress)
                    }
                })
        $https = $listeners | Where-Object Transport -eq 'HTTPS' | Select-Object -First 1
        $cert = $null
        if ($https -and $https.Thumbprint) {
            $c = Get-Item "Cert:\LocalMachine\My\$($https.Thumbprint)" -ErrorAction SilentlyContinue
            if ($c) {
                $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($c)
                $exportable = $null
                if ($rsa -is [Security.Cryptography.RSACng]) { $exportable = ($rsa.Key.ExportPolicy -band [Security.Cryptography.CngExportPolicies]::AllowExport) -ne 0 }
                $bc = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
                $eku = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
                $san = $c.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
                $cert = [pscustomobject]@{
                    Thumbprint = $c.Thumbprint; Subject = $c.Subject; FriendlyName = $c.FriendlyName; KeySize = $rsa.KeySize
                    Exportable = $exportable; IsCa = $(if ($bc) { $bc.CertificateAuthority } else { $null })
                    Eku = @($(if ($eku) { $eku.EnhancedKeyUsages | ForEach-Object Value }))
                    San = $(if ($san) { $san.Format($false) } else { $null })
                    Years = [math]::Round(($c.NotAfter - $c.NotBefore).TotalDays / 365.25, 1)
                }
            }
        }
        $latfp = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
        [pscustomobject]@{
            Service = [string](Get-Service WinRM).Status; StartType = [string](Get-Service WinRM).StartType
            Listeners = $listeners; Rules = $rules; Certificate = $cert; LocalAccountTokenFilterPolicy = $latfp
            Certificates = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object FriendlyName -eq 'WinRM HTTPS (RemoteHub)' | ForEach-Object Thumbprint)
        }
    }
}

# A copy of an MSI that fails on purpose:
#   LaunchCondition - a valid package (same version) whose launch condition is false: msiexec ends with 1603 before
#                     anything changes;
#   Truncated       - only the first 64 KB: not a package at all (msiexec 1620, version unreadable);
#   AfterEnroll     - a deferred step right after AgentEnroll that fails (cmd /c exit 1): the install rolls back after
#                     the enrollment ran.
function New-LabBrokenMsi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet('LaunchCondition', 'Truncated', 'AfterEnroll')][string]$Kind
    )
    if ($Kind -eq 'AfterEnroll') {
        $null = New-Item -ItemType Directory -Path (Split-Path $Path) -Force
        Copy-Item $Source $Path -Force
        $wi = New-Object -ComObject WindowsInstaller.Installer
        function Call($o, [string]$m, [object[]]$a) { $o.GetType().InvokeMember($m, 'InvokeMethod', $null, $o, $a) }
        $db = Call $wi 'OpenDatabase' @($Path, 1)
        function Exec([string]$sql) { $v = Call $db 'OpenView' @($sql); [void](Call $v 'Execute' @()); [void](Call $v 'Close' @()) }
        $v = Call $db 'OpenView' @("SELECT ``Sequence`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'AgentEnroll'")
        [void](Call $v 'Execute' @()); $rec = Call $v 'Fetch' @()
        $seq = [int]$rec.GetType().InvokeMember('IntegerData', 'GetProperty', $null, $rec, @(1)); [void](Call $v 'Close' @())
        # 51: set a property (immediate); 50 + 1024: run the exe the property names, deferred - its exit code 1 fails the install
        Exec "INSERT INTO ``CustomAction`` (``Action``, ``Type``, ``Source``, ``Target``) VALUES ('LabFailSet', 51, 'LabFailExe', '[SystemFolder]cmd.exe')"
        Exec "INSERT INTO ``CustomAction`` (``Action``, ``Type``, ``Source``, ``Target``) VALUES ('LabFail', 1074, 'LabFailExe', '/c exit 1')"
        Exec "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('LabFailSet', 'NOT Installed', $($seq - 2))"
        Exec "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Condition``, ``Sequence``) VALUES ('LabFail', 'NOT Installed', $($seq + 1))"
        [void](Call $db 'Commit' @())
        $v = $null; $rec = $null; $db = $null; $wi = $null
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        return Get-Item $Path
    }
    $null = New-Item -ItemType Directory -Path (Split-Path $Path) -Force
    if ($Kind -eq 'Truncated') {
        $bytes = [IO.File]::ReadAllBytes($Source)
        [IO.File]::WriteAllBytes($Path, $bytes[0..65535])
        return Get-Item $Path
    }
    Copy-Item $Source $Path -Force
    $wi = New-Object -ComObject WindowsInstaller.Installer
    function Call($o, [string]$m, [object[]]$a) { $o.GetType().InvokeMember($m, 'InvokeMethod', $null, $o, $a) }
    function Prop($o, [string]$p, [object[]]$a) { $o.GetType().InvokeMember($p, 'GetProperty', $null, $o, $a) }
    $db = Call $wi 'OpenDatabase' @($Path, 1)   # msiOpenDatabaseModeTransact
    function Exec([string]$sql) { $v = Call $db 'OpenView' @($sql); [void](Call $v 'Execute' @()); [void](Call $v 'Close' @()) }
    function Count([string]$sql) {
        $v = Call $db 'OpenView' @($sql); [void](Call $v 'Execute' @())
        $n = 0; while (Call $v 'Fetch' @()) { $n++ }; [void](Call $v 'Close' @()); $n
    }
    if ((Prop $db 'TablePersistent' @('LaunchCondition')) -ne 1) {
        Exec 'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)'
    }
    Exec "INSERT INTO ``LaunchCondition`` (``Condition``, ``Description``) VALUES ('LabNeverTrue = 1', 'Lab: this package is broken on purpose.')"
    if ((Count "SELECT ``Action`` FROM ``InstallExecuteSequence`` WHERE ``Action`` = 'LaunchConditions'") -eq 0) {
        Exec "INSERT INTO ``InstallExecuteSequence`` (``Action``, ``Sequence``) VALUES ('LaunchConditions', 100)"
    }
    [void](Call $db 'Commit' @())
    # views and records keep the file open until they are collected
    $db = $null; $wi = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    Get-Item $Path
}

# A copy of an MSI as a rebuild of the same version would be: new ProductCode and PackageCode, everything else the
# same. Installing it over the original is a (same-version) major upgrade, not a maintenance run of the installed
# product - what an update with a rebuilt agent does. The edit breaks the copy's signature; the lab doesn't check it.
function New-LabRebuiltMsi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Path
    )
    $null = New-Item -ItemType Directory -Path (Split-Path $Path) -Force
    Copy-Item $Source $Path -Force
    $wi = New-Object -ComObject WindowsInstaller.Installer
    function Call($o, [string]$m, [object[]]$a) { $o.GetType().InvokeMember($m, 'InvokeMethod', $null, $o, $a) }
    function Prop($o, [string]$p, [object[]]$a) { $o.GetType().InvokeMember($p, 'GetProperty', $null, $o, $a) }
    function SetProp($o, [string]$p, [object[]]$a) { [void]$o.GetType().InvokeMember($p, 'SetProperty', $null, $o, $a) }
    $db = Call $wi 'OpenDatabase' @($Path, 1)   # msiOpenDatabaseModeTransact
    $v = Call $db 'OpenView' @("UPDATE ``Property`` SET ``Value`` = '{$([guid]::NewGuid().ToString().ToUpper())}' WHERE ``Property`` = 'ProductCode'")
    [void](Call $v 'Execute' @()); [void](Call $v 'Close' @())
    $info = Prop $db 'SummaryInformation' @(20)
    SetProp $info 'Property' @(9, "{$([guid]::NewGuid().ToString().ToUpper())}")   # PID_REVNUMBER = PackageCode
    [void](Call $info 'Persist' @())
    [void](Call $db 'Commit' @())
    $v = $null; $info = $null; $db = $null; $wi = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    Get-Item $Path
}

# Sets agent policy values (HKLM\SOFTWARE\Policies\RemoteHub\Agent) and rights (its Rights subkey) in the guest.
# DWORDs for integers, REG_MULTI_SZ for arrays, REG_SZ otherwise. -Clear removes the whole policy key first.
function Set-LabAgentPolicy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Values = @{},
        [hashtable]$Rights = @{},
        [switch]$Clear
    )
    # $Clear.IsPresent, not [bool]$Clear: in argument mode that is the string "[bool]False" - true, so every call cleared the key.
    Invoke-LabGuest -Name $Name -ArgumentList $Values, $Rights, $Clear.IsPresent -ScriptBlock {
        param($values, $rights, $clear)
        $key = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
        if ($clear) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
        foreach ($set in @(@{ Path = $key; Items = $values }, @{ Path = "$key\Rights"; Items = $rights })) {
            if ($set.Items.Count -eq 0) { continue }
            # Only when missing: New-Item -Force on an existing registry key recreates it, wiping its values and subkeys.
            if (-not (Test-Path $set.Path)) { $null = New-Item $set.Path -Force }
            foreach ($n in $set.Items.Keys) {
                $v = $set.Items[$n]
                # arrays arrive as ArrayList after PowerShell Direct serialization
                $type = if ($v -is [int]) { 'DWord' } elseif ($v -is [Collections.IList]) { 'MultiString' } else { 'String' }
                if ($type -eq 'MultiString') { $v = [string[]]@($v) }
                $null = New-ItemProperty -Path $set.Path -Name $n -Value $v -PropertyType $type -Force
            }
        }
    }
}

# Agent policy values as the device has them ($null when the key doesn't exist).
function Get-LabAgentPolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $key = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
        if (-not (Test-Path $key)) { return $null }
        $p = Get-ItemProperty $key
        $o = [ordered]@{}
        foreach ($prop in $p.PSObject.Properties) { if ($prop.Name -notlike 'PS*') { $o[$prop.Name] = $prop.Value } }
        [pscustomobject]$o
    }
}

# GET /v1/health over TLS from the host, pinned to the fingerprint (AgentInstaller.HealthAsync).
function Get-LabAgentHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [int]$Port = 58732
    )
    Invoke-LabHarness @('health', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin) -TimeoutSec 60
}

# The claim of the VM's agent as RemoteHub makes it (harness "claim", docs/agent-spec.md section 5.5): outcome
# (Claimed, NoEnrollment, OtherName, RevokedSecret, ...), pin and deviceId of the certificate the device made, its
# computer name. -HostName judges the device against that name instead of the address (a DNS name pointing elsewhere);
# -RevokeCurrent as RemoteHub would after "Replace and revoke". Without an answer the harness fails (Invoke-LabHarness throws).
function Get-LabAgentClaim {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$Port = 58732,
        [string]$HostName,
        [switch]$RevokeCurrent
    )
    $a = @('claim', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port")
    if ($HostName) { $a += '--name', $HostName }
    if ($RevokeCurrent) { $a += '--revoke-current', '1' }
    Invoke-LabHarness $a -TimeoutSec 90
}

# Everything the device says about the installed agent, read as the admin account: service, agent.json, certificate,
# folder ACL, firewall rule, leftovers, log lines. Missing parts come back as $null.
function Get-LabAgentState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $folder = Join-Path $env:ProgramData 'RemoteHub\Agent'
        $svc = Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'"
        $svcKey = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\RemoteHubAgent' -ErrorAction SilentlyContinue

        $config = $null; $cert = $null
        $configPath = Join-Path $folder 'agent.json'
        if (Test-Path $configPath) {
            $config = Get-Content $configPath -Raw | ConvertFrom-Json
            # the enrollment secret goes into evidence only as "set" (docs/agent-spec.md section 5.5)
            if ($config.PSObject.Properties['enrollmentSecret'] -and $config.enrollmentSecret) { $config.enrollmentSecret = 'set' }
            $c = Get-Item ('Cert:\LocalMachine\My\' + $config.certificateThumbprint) -ErrorAction SilentlyContinue
            if ($c) {
                $sha = [Security.Cryptography.SHA256]::Create()
                $exportable = $null
                $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($c)
                if ($rsa -is [Security.Cryptography.RSACng]) {
                    $policy = $rsa.Key.ExportPolicy
                    $exportable = (($policy -band [Security.Cryptography.CngExportPolicies]::AllowExport) -ne 0) -or
                        (($policy -band [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport) -ne 0)
                }
                elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) { $exportable = $rsa.CspKeyContainerInfo.Exportable }
                $cert = [pscustomobject]@{
                    Subject      = $c.Subject
                    FriendlyName = $c.FriendlyName
                    Sha256       = [BitConverter]::ToString($sha.ComputeHash($c.RawData)).Replace('-', '')
                    HasKey       = $c.HasPrivateKey
                    Exportable   = $exportable
                    NotAfter     = $c.NotAfter
                }
            }
        }

        $acl = $null
        if (Test-Path $folder) {
            $a = Get-Acl $folder
            $sidType = [Security.Principal.SecurityIdentifier]
            $acl = [pscustomobject]@{
                Owner     = $a.GetOwner($sidType).Value
                Protected = $a.AreAccessRulesProtected
                Rules     = @($a.GetAccessRules($true, $true, $sidType) | ForEach-Object {
                        [pscustomobject]@{ Sid = $_.IdentityReference.Value; Rights = [string]$_.FileSystemRights
                            Type = [string]$_.AccessControlType; Inherited = $_.IsInherited }
                    })
            }
        }

        $fw = $null
        $rule = @(Get-NetFirewallRule -DisplayName 'RemoteHub Agent' -ErrorAction SilentlyContinue)
        if ($rule.Count -gt 0) {
            $r = $rule[0]
            $portFilter = $r | Get-NetFirewallPortFilter
            $fw = [pscustomobject]@{
                Count         = $rule.Count
                Enabled       = [string]$r.Enabled
                Direction     = [string]$r.Direction
                Action        = [string]$r.Action
                Profile       = [string]$r.Profile
                Protocol      = [string]$portFilter.Protocol
                LocalPort     = @($portFilter.LocalPort)
                RemoteAddress = @(($r | Get-NetFirewallAddressFilter).RemoteAddress)
                Program       = ($r | Get-NetFirewallApplicationFilter).Program
            }
        }

        $service = $null
        if ($svc) {
            $service = [pscustomobject]@{
                State       = $svc.State
                StartMode   = $svc.StartMode
                Account     = $svc.StartName
                DisplayName = $svc.DisplayName
                Delayed     = [bool]($svcKey -and $svcKey.PSObject.Properties['DelayedAutostart'] -and $svcKey.DelayedAutostart -eq 1)
                Path        = $svc.PathName
            }
        }

        # leftovers of the install: staged folders in any temp folder, enrollment files in ProgramData or temp
        $staged = @(Get-ChildItem 'C:\Users\*\AppData\Local\Temp', 'C:\Windows\Temp' -Directory -Filter 'RemoteHub-Agent-*' -ErrorAction SilentlyContinue)
        $enrollments = @(Get-ChildItem $env:ProgramData, 'C:\Users\*\AppData\Local\Temp' -Recurse -Filter '*.rhagent' -ErrorAction SilentlyContinue)
        $logs = @(Get-ChildItem (Join-Path $folder 'logs') -Filter 'agent-*.log' -ErrorAction SilentlyContinue)
        $product = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
                Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'RemoteHub Agent' })
        $productInfo = $null
        if ($product.Count -gt 0) { $productInfo = [pscustomobject]@{ Code = $product[0].PSChildName; Version = $product[0].DisplayVersion } }
        $sasKey = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue
        $sas = $null
        if ($sasKey -and $sasKey.PSObject.Properties['SoftwareSASGeneration']) { $sas = $sasKey.SoftwareSASGeneration }
        # the virtual monitor driver the agent can install (Virtual Display Driver, hardware ID Root\MttVDD)
        $vdd = @(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
                Where-Object { $_.PSObject.Properties['HardwareID'] -and @($_.HardwareID) -contains 'Root\MttVDD' })
        # automatic sign-in: the credential provider (DLL, both registrations), the install's choice and the policy values
        # (docs/admx-design.md 11.3)
        $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'   # Package.wxs
        $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue
        $rights = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue
        $autoSignIn = [pscustomobject]@{
            Dll          = Test-Path 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
            Provider     = Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
            InprocServer = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
            # the install's choice (MSI AUTOSIGNIN, auto-sign-in on|off), kept in agent.json since 0.18.12
            ConfigBehavior = $(if ($config -and $config.PSObject.Properties['noUserBehavior']) { $config.noUserBehavior })
            ConfigGroups = @($(if ($config -and $config.PSObject.Properties['autoSignInGroups']) { $config.autoSignInGroups }) | Where-Object { $_ })
            # the policy values: set by IT - or written by an install before 0.18.12, which recorded them in the marker file
            Behavior     = $(if ($policy -and $policy.PSObject.Properties['NoUserBehavior']) { $policy.NoUserBehavior })
            Right        = @($(if ($rights -and $rights.PSObject.Properties['AutoSignIn']) { $rights.AutoSignIn }))
            Marker       = Test-Path (Join-Path $folder 'autosignin.policy')
        }
        $processes = @(Get-CimInstance Win32_Process -Filter "Name='RemoteHubAgent.exe'" | ForEach-Object {
                [pscustomobject]@{ Id = $_.ProcessId; CommandLine = $_.CommandLine } })
        [pscustomobject]@{
            Product         = $productInfo
            InstallFolder   = Test-Path 'C:\Program Files\RemoteHub Agent'
            Service         = $service
            Config          = $config
            Certificate     = $cert
            # every certificate the agent would call its own (also when agent.json is gone)
            OurCertificates = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -like '*O=RemoteHub Agent*' } | ForEach-Object Thumbprint)
            FolderAcl       = $acl
            Firewall        = $fw
            EventSource     = Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\RemoteHub Agent'
            SoftwareSas     = $sas
            AutoSignIn      = $autoSignIn
            VirtualDisplay  = $(if ($vdd.Count -gt 0) { [pscustomobject]@{ Count = $vdd.Count; Status = [string]$vdd[0].Status; ConfigManagerErrorCode = $vdd[0].ConfigManagerErrorCode } } else { $null })
            Processes       = $processes
            Leftovers       = @(($staged + $enrollments) | ForEach-Object FullName)
            LogFolder       = Test-Path (Join-Path $folder 'logs')
            LogLines        = @($logs | Get-Content -ErrorAction SilentlyContinue | ForEach-Object { [string]$_ })
        }
    }
}

# Uninstalls the agent the way Windows does (msiexec /x <product code> /qn) and returns the exit code and the guest
# path of the verbose MSI log. RemoteHub itself has no uninstall; this is "Apps > Uninstall" without the clicking.
function Uninstall-LabAgent {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        $product = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
                Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'RemoteHub Agent' })
        if ($product.Count -eq 0) { throw 'RemoteHub Agent is not installed.' }
        $log = Join-Path $env:SystemRoot 'Temp\RemoteHub-Agent-uninstall.log'
        $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList @('/x', $product[0].PSChildName, '/qn', '/norestart', '/l*v', "`"$log`"")
        [pscustomobject]@{ ExitCode = $p.ExitCode; ProductCode = $product[0].PSChildName; Log = $log }
    }
}

# Restarts the agent service in the guest (clears its sign-in throttle and session count) and waits until health answers
# with the pin again.
function Restart-LabAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [int]$Port = 58732,
        [int]$TimeoutSec = 120
    )
    Invoke-LabGuest -Name $Name -ScriptBlock { Restart-Service RemoteHubAgent -Force }
    Wait-LabAgent -Name $Name -Pin $Pin -Port $Port -TimeoutSec $TimeoutSec
}

# Sets one value in the agent's agent.json (e.g. virtualDisplay = always) and restarts the agent so it reads it.
function Set-LabAgentJsonValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [Parameter(Mandatory)][string]$Key,
        $Value
    )
    Invoke-LabGuest -Name $Name -ArgumentList $Key, $Value -ScriptBlock {
        param($k, $v)
        $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'
        $c = Get-Content $f -Raw | ConvertFrom-Json
        $c | Add-Member -NotePropertyName $k -NotePropertyValue $v -Force
        [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
    }
    $null = Restart-LabAgent -Name $Name -Pin $Pin
}

# The lines RemoteHub's diagnostic log (DiagLog) got on this PC since -Since: what the harness's client code wrote
# (%APPDATA%\RemoteHub\logs, or REMOTEHUB_DATA_DIR's logs; the rotated file too). For checking that nothing names a
# device, account or address there.
function Get-LabHostDiagLog {
    [CmdletBinding()]
    param([datetime]$Since = [datetime]::MinValue)
    $dir = if ($env:REMOTEHUB_DATA_DIR) { $env:REMOTEHUB_DATA_DIR } else { Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'RemoteHub' }
    $files = @('remotehub.log.old', 'remotehub.log') | ForEach-Object { Join-Path $dir "logs\$_" } | Where-Object { Test-Path $_ }
    $keep = $false
    foreach ($line in @($files | ForEach-Object { Get-Content $_ -Encoding UTF8 })) {
        # a line without a date (the rest of a message with a line break) goes with the line before it
        if ($line -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)') { $keep = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', $null) -ge $Since.AddSeconds(-1) }
        if ($keep) { $line }
    }
}

# Waits until health answers with the pin (after a restart or reboot - the service starts delayed) and returns it.
function Wait-LabAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Pin,
        [int]$Port = 58732,
        [int]$TimeoutSec = 240
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = ''
    while ((Get-Date) -lt $deadline) {
        try { return Get-LabAgentHealth -Name $Name -Pin $Pin -Port $Port } catch { $last = $_.Exception.Message }
        Start-Sleep -Seconds 2
    }
    throw "${Name}: the agent didn't answer within $TimeoutSec s ($last)."
}

# Restarts Remote Desktop Services in the guest and waits until port 3389 listens again. They read values such as
# fPromptForPassword (HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services) and fSingleSessionPerUser
# (HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server) only when they start, so a test that sets or removes one
# calls this afterwards (fDenyTSConnections takes effect without it) (runs 20261016-024318, 20261016-212514:
# set only in the registry, the device neither asked for the password nor opened a second session). It also makes them
# create a new self-signed RDP certificate when the old one was removed. Ends the guest's RDP sessions.
function Restart-LabRdpService {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSec = 60
    )
    # Stopping TermService sometimes fails ("Service ... stop failed", runs 20261025-052539 R-RDP-10/11), or failed
    # quietly and the old instance's listener counted as up (run 20261025-132336: the device kept asking for the password
    # in every later session). So: its process id first; stopped, or its own svchost ended when it won't stop; started
    # again; up only with a new process id listening.
    $up = Invoke-LabGuest -Name $Name -ArgumentList $TimeoutSec -ScriptBlock {
        param($t)
        function TermPid { [int](Get-CimInstance Win32_Service -Filter "Name='TermService'").ProcessId }
        $before = TermPid
        # -Force: UmRdpService depends on TermService
        try { Stop-Service TermService -Force -ErrorAction Stop } catch { }
        $until = (Get-Date).AddSeconds($t)
        while ((Get-Service TermService).Status -ne 'Stopped' -and (Get-Date) -lt $until) {
            # only a process that hosts TermService alone is ended
            $shared = @(Get-CimInstance Win32_Service -Filter "ProcessId=$before" | Where-Object Name -ne 'TermService')
            if ($before -gt 0 -and $shared.Count -eq 0) { Stop-Process -Id $before -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Seconds 2
        }
        Start-Service TermService -ErrorAction SilentlyContinue
        Start-Service UmRdpService -ErrorAction SilentlyContinue
        $until = (Get-Date).AddSeconds($t)
        do {
            $now = TermPid
            if ($now -gt 0 -and $now -ne $before -and @(Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue).Count) { return "up $before -> $now" }
            Start-Sleep -Seconds 1
        } while ((Get-Date) -lt $until)
        "not restarted: TermService $((Get-Service TermService).Status), process $before -> $(TermPid)"
    }
    if (-not (@($up) -like 'up *')) { throw "${Name}: Remote Desktop Services weren't restarted within $TimeoutSec s ($(@($up) -join '; '))." }
}

# A local account made by a test (the checkpoint removes it again): the lab password unless -NoPassword, member of
# Administrators with -Admin, of the local groups in -Group (created when missing).
function New-LabGuestUser {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$User,
        [switch]$Admin,
        [switch]$NoPassword,
        [string[]]$Group = @()
    )
    Invoke-LabGuest -Name $Name -ArgumentList $User, (Get-LabConfig).Password, $Admin.IsPresent, $NoPassword.IsPresent, ($Group -join "|") -ScriptBlock {
        param($user, $password, $admin, $noPassword, $groups)
        if (-not (Get-LocalUser -Name $user -ErrorAction SilentlyContinue)) {
            if ($noPassword) { $null = New-LocalUser -Name $user -NoPassword }
            else { $null = New-LocalUser -Name $user -Password (ConvertTo-SecureString $password -AsPlainText -Force) -PasswordNeverExpires }
        }
        # by SID: the group is "Administratoren" on de-DE
        if ($admin) { Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $user -ErrorAction SilentlyContinue }
        foreach ($g in @("$groups" -split '\|')) {
            if (-not $g) { continue }
            if (-not (Get-LocalGroup -Name $g -ErrorAction SilentlyContinue)) { $null = New-LocalGroup -Name $g }
            Add-LocalGroupMember -Group $g -Member $user -ErrorAction SilentlyContinue
        }
        (Get-LocalUser -Name $user).SID.Value
    }
}

# The guest's clock - the -Since of the event readers below (the host's clock may differ).
function Get-LabGuestTime {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock { Get-Date }
}

# The agent's audit events (Application log, source "RemoteHub Agent") since a guest time, as message strings. -Id
# filters (1000 PowerShell started, 1001 ended, 2000 sign-in refused, 2100 desktop refused, ...).
function Get-LabAgentEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][datetime]$Since,
        [int[]]$Id
    )
    Invoke-LabGuest -Name $Name -ArgumentList $Since, $Id -ScriptBlock {
        param($Since, $Id)
        $filter = @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent'; StartTime = $Since }
        # int[]: several ids arrive as a deserialized list, which Get-WinEvent refuses ("parameter is incorrect")
        if ($Id) { $filter.Id = [int[]]@($Id) }
        # no events, or the event source already gone (uninstalled): Get-WinEvent throws either way
        $ev = try { @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop) } catch { @() }
        @($ev | Sort-Object TimeCreated | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Time = $_.TimeCreated; Message = [string]$_.Message } })
    }
}

# Failed Windows sign-ins (Security 4625) since a guest time, optionally for one account: what reached Windows.
function Get-LabFailedLogon {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][datetime]$Since,
        [string]$Account
    )
    Invoke-LabGuest -Name $Name -ArgumentList $Since, $Account -ScriptBlock {
        param($Since, $Account)
        @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4625; StartTime = $Since } -ErrorAction SilentlyContinue | ForEach-Object {
                $x = [xml]$_.ToXml()
                $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
                [pscustomobject]@{ Time = $_.TimeCreated; User = $d.TargetUserName; LogonType = $d.LogonType; Status = $d.Status; SubStatus = $d.SubStatus }
            } | Where-Object { -not $Account -or $_.User -eq $Account })
    }
}

# The agent's log lines (logs\agent-*.log) written since a guest time - the file has no per-line filter otherwise.
function Get-LabAgentLog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [datetime]$Since = [datetime]::MinValue)
    Invoke-LabGuest -Name $Name -ArgumentList $Since -ScriptBlock {
        param($Since)
        $lines = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' -ErrorAction SilentlyContinue |
                Sort-Object Name | Get-Content -ErrorAction SilentlyContinue | ForEach-Object { [string]$_ })
        if ($Since -eq [datetime]::MinValue) { return $lines }
        # lines start with "yyyy-MM-dd HH:mm:ss"; continuation lines belong to the line before
        $keep = $false
        foreach ($l in $lines) {
            if ($l -match '^(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})') { $keep = ([datetime]$Matches[1]) -ge $Since.AddSeconds(-1) }
            if ($keep) { $l }
        }
    }
}

# Copies the agent's logs to the evidence folder.
function Save-LabAgentEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Destination)
    Get-LabFile -Name $Name -Path 'C:\ProgramData\RemoteHub\Agent\logs' -Destination $Destination
    # and its audit events (Application log) - D-AUD reads them across the scenarios of a run
    $events = Invoke-LabGuest -Name $Name -ScriptBlock {
        $ev = try { @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteHub Agent' } -ErrorAction Stop) } catch { @() }
        @($ev | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Time = $_.TimeCreated.ToString('o'); Message = [string]$_.Message } })
    }
    $null = New-Item -ItemType Directory -Path $Destination -Force
    ConvertTo-Json @($events) -Depth 3 | Set-Content (Join-Path $Destination 'events.json') -Encoding UTF8
}

#endregion

#region RemoteHub client

# Installs RemoteHub's package (LabRoot\client unless -Msi) silently. The update check and telemetry are turned off by
# policy unless -Network (a client in the lab must not call update.remotehub.dev). The VM trusts the lab's signing
# certificate, as a PC trusts a release's publisher: an update must be signed by the publisher of the running build.
function Install-LabClient {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Msi, [switch]$Network)
    # set up outside Initialize-LabVm: the VM is no longer what Initialize-LabVm left
    if (-not $script:LabInitializing) { Clear-LabVmState -Name $Name }
    if (-not $Msi) { $Msi = Get-LabClientMsi }
    Install-LabSigningCertificate -Name $Name
    Copy-LabFile -Name $Name -Path $Msi -Destination 'C:\LabClient'
    Invoke-LabGuest -Name $Name -ArgumentList (Split-Path $Msi -Leaf), $Network.IsPresent -ScriptBlock {
        param($leaf, $network)
        $p = Start-Process msiexec.exe -ArgumentList "/i `"C:\LabClient\$leaf`" /qn /norestart /l*v C:\LabClient\client-install.log" -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { throw "RemoteHub install failed: msiexec $($p.ExitCode)" }
        if (-not $network) {
            $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
            Set-ItemProperty $k -Name DisableUpdateCheck -Value 1 -Type DWord
            Set-ItemProperty $k -Name DisableTelemetry -Value 1 -Type DWord
        }
        $p.ExitCode
    }
}

# Starts RemoteHub in the account's session (the console account by default) and waits for its main window. Answers
# the task to stop it with Stop-LabClient. -Arguments: .rdp paths in the guest. The welcome tour of a new profile (and
# What's new after an upgrade) is closed, as a user would, unless -KeepFirstStart: every test file starts from the
# checkpoint with a new profile, and the tour is modal.
function Start-LabClient {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'Console', [string[]]$Arguments = @(), [int]$TimeoutSec = 120, [switch]$KeepFirstStart)
    # each argument in double quotes: Start-Process joins them with spaces as they are
    $argText = ($Arguments | ForEach-Object { "'" + (('"' + $_ + '"') -replace "'", "''") + "'" }) -join ', '
    # Standard error to a file of its own: a stack overflow prints its repeating frames only there (UI-AG-04, runs of
    # 16.10.2026, 20261025-192331 and 20261026-032740: exception 0xc00000fd, nothing in the event log). UI-DESKTOP's
    # EnsureClient reads the newest file.
    $stderr = "C:\ProgramData\RemoteHubLab\session\remotehub-stderr-$((Get-Date).ToString('HHmmss'))-$([guid]::NewGuid().ToString('N').Substring(0, 6)).txt"
    $start = "Start-Process -FilePath 'C:\Program Files\RemoteHub\RemoteHub.exe' -RedirectStandardError '$stderr'" + $(if ($argText) { " -ArgumentList @($argText)" })
    $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create($start))
    # The main window is up: its New button, or - when RemoteHub opens in the view used last, Notes or Scripts, which
    # have none (UI-NOTE-07, run 20261021-222745) - the rail's Remote Control, which every view has
    # A person server stuck in one search (run 20261022-092309, C-POLUPD2: right after an update it took the request and
    # never answered) is ended by Invoke-LabPerson and the next search starts a new one: once more, not the end.
    function Wait-Main([int]$Sec) {
        $until = (Get-Date).AddSeconds($Sec)
        $stuck = 0
        do {
            try {
                $m = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'New' -Value exists -TimeoutSec 5
                if ($m.found) { return $m }
                $m = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'Remote Control' -Value exists -TimeoutSec 2
                if ($m.found) { return $m }
            }
            catch {
                if ("$_" -notmatch 'took the request but didn''t answer' -or ++$stuck -gt 1) { throw }
                $m = [pscustomobject]@{ found = $false; error = "$_" }
                $until = $until.AddSeconds(60)   # the stuck wait isn't the main window's time
            }
        } while ((Get-Date) -lt $until)
        $m
    }
    $started = Get-Date
    $main = Wait-Main $TimeoutSec
    if (-not $main.found) {
        # No window (run 20261021-222745, C-FIRST on a fresh install: only "Program Manager" after 120 s): RemoteHub
        # not running is started once more, a RemoteHub still starting gets the time again
        $running = Invoke-LabGuest -Name $Name -ScriptBlock { [bool](Get-Process RemoteHub -ErrorAction SilentlyContinue) }
        if (-not $running) {
            Stop-LabInSession -Name $Name -Task $task
            $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create("& 'C:\Program Files\RemoteHub\RemoteHub.exe' $argText"))
        }
        $main = Wait-Main $TimeoutSec
        if (-not $main.found) {
            # what Windows recorded about RemoteHub since the start: a crash or a .NET runtime error
            $crash = @(Invoke-LabGuest -Name $Name -ArgumentList $started.AddMinutes(-1) -ScriptBlock {
                    param($since)
                    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = $since } -MaxEvents 5 -ErrorAction SilentlyContinue |
                        Where-Object { $_.Message -match 'RemoteHub' } | ForEach-Object { ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(300, ($_.Message -replace '\s+', ' ').Length)) }
                }) | Select-Object -First 1
            # (error: the person server's search didn't finish - it names the windows that don't respond)
            $searchError = if ($main.PSObject.Properties['error'] -and $main.error) { ' - ' + $main.error } else { '' }
            $state = if ($running) { 'RemoteHub was running' } else { 'RemoteHub was not running and was started again' }
            if ($crash) { $state += '; Application log: ' + $crash }
            throw "${Name}: RemoteHub's main window didn't appear$searchError (windows: $($main.windows -join ', '); $state)."
        }
    }
    if (-not $KeepFirstStart) { $null = Close-LabFirstStart -Name $Name -Account $Account }
    # it may open partly off the screen (seen at x < 0): moved into it, so real clicks reach every part
    $fit = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'Remote Control' -Value fit -TimeoutSec 10   # (the rail: in every view)
    if (-not $fit.found) { $null = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'NewButton' -Value fit -TimeoutSec 5 }   # an older build without the rail
    $task
}

# Closes the welcome tour ("Skip") or What's new ("Close") when one is open over the main window, and answers which it
# was ('' when none). Every start is checked: a test that resets the VM, wipes RemoteHub's data or upgrades it brings
# one back, modal, and then no real click or key reaches the main window (lab run 20261021-062735: remembering one
# check per file left them open in C-DATA2, C-POLUPD, C-POLUPD2 and C-NOTES). Call it once the main window is up:
# RemoteHub opens either right after its window shows (MainWindow.StartupChecks), so one that hasn't come within
# -TimeoutSec isn't coming.
function Close-LabFirstStart {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'Console', [int]$TimeoutSec = 6)

    $until = (Get-Date).AddSeconds($TimeoutSec)
    do {
        foreach ($d in @(@{ Title = 'Welcome to RemoteHub'; Button = 'Skip' }, @{ Title = "What's new"; Button = 'Close' })) {
            $r = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title $d.Title -Text $d.Button -Value click -TimeoutSec 1
            if ($r.found -and -not ($r.PSObject.Properties['error'] -and $r.error)) { return $d.Title }
        }
    } while ((Get-Date) -lt $until)
    ''
}

function Stop-LabClient {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Task)
    Invoke-LabGuest -Name $Name -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
    if ($Task) { Stop-LabInSession -Name $Name -Task $Task }
}

# One UI Automation step in RemoteHub (or any window) in the account's session - RhPerson.ps1 -Do uia. Throws when the
# element isn't there unless -Optional; answers the step's result (found, name, value, items, error).
function Invoke-LabUi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Text,
        [string]$Title = 'RemoteHub*',
        [string]$Value = 'click',
        [string]$Account = 'Console',
        [int]$TimeoutSec = 30,
        [switch]$Optional
    )
    $r = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title $Title -Text $Text -Value $Value -TimeoutSec $TimeoutSec
    if (-not $Optional -and (-not $r.found -or ($r.PSObject.Properties['error'] -and $r.error))) {
        # a password typed into a field never goes into the message (it ends up in the evidence and the console log -
        # lab run 20261019-193627 had the lab password there, UI-RDP's "setnext:..." for 'Password')
        $shown = if ($Value -match '^(setnext|keys):' -and $Text -match 'Password|PIN|Passwort|Kennwort') { "$(($Value -split ':', 2)[0]):<hidden>" } else { $Value }
        throw "UI step failed: '$Text' in '$Title' ($shown): $(if ($r.PSObject.Properties['error']) { $r.error + $(if ($r.PSObject.Properties['entries']) { ' - entries: ' + (@($r.entries) -join ', ') }) } else { 'not found; windows: ' + ($r.windows -join ', ') + $(if ($r.PSObject.Properties['notResponding'] -and @($r.notResponding).Count) { '; not responding: ' + (@($r.notResponding) -join ', ') }) })"
    }
    $r
}

# Waits for Windows' credential dialog of the RDP client ("Windows Security"; the newer one is a "Credential Dialog Xaml
# Host") in the account's session; answers whether it showed within -TimeoutSec. RemoteHub owns the dialog, so UI
# Automation lists it inside RemoteHub's window as a bare "Window|Windows Security|" with nothing inside it: a search
# for an element in it never finds one (lab run 20261017-054704 counted the prompt as not shown while RemoteHub's audit
# log had "Password copied" for it twice). So RemoteHub's element list is searched for that entry, and the session's
# top-level windows for the title (the dialog on its own, when nothing owns it).
function Wait-LabCredentialDialog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Account = 'Console',
        [int]$TimeoutSec = 60
    )
    $titles = 'Windows Security', 'Credential Dialog Xaml Host'
    $until = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $l = Invoke-LabUi -Name $Name -Account $Account -Text '*' -Value list -Optional -TimeoutSec 5
        $items = if ($l -and $l.PSObject.Properties['items']) { @($l.items) } else { @() }
        if (@($items | Where-Object { "$_" -match '^Window\|(Windows Security|Credential Dialog Xaml Host)\|' }).Count -gt 0) { return $true }
        # a search for a window that isn't there answers the session's top-level windows
        $w = try { Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'zz-no-such-window' -Text 'zz' -Value exists -TimeoutSec 1 } catch { $null }
        $tops = if ($w -and $w.PSObject.Properties['windows']) { @($w.windows) } else { @() }
        if (@($tops | Where-Object { $titles -contains "$_" }).Count -gt 0) { return $true }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $until)
    $false
}

# A connection made through RemoteHub's "New connection" dialog: name, host, credentials for this item (a local account
# on the device unless -Domain).
function New-LabClientConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Display,
        [Parameter(Mandatory)][string]$TargetHost,
        [string]$User,
        [string]$Password,
        [switch]$Domain,
        [string]$Account = 'Console'
    )
    $ui = @{ Name = $Name; Account = $Account }
    # New opens a context menu (a popup UIA doesn't keep); Ctrl+N is the same command
    $null = Invoke-LabUi @ui -Text 'SearchBox' -Value 'keys:^n'
    $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Display name' -Value "setnext:$Display"
    $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Computer name or IP address' -Value "setnext:$TargetHost"
    if ($User) {
        $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Credentials'
        $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Enter credentials for this item' -Value check
        $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Username' -Value "setnext:$User"
        if (-not $Domain) { $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Local account on the target computer*' -Value check }
        if ($Password) { $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Password' -Value "setnext:$Password" }
    }
    $null = Invoke-LabUi @ui -Title 'New connection' -Text 'Save'
    $null = Invoke-LabUi @ui -Text $Display -Value exists
}

# update.remotehub.dev inside the VM (lab\guest\RhMockServer.ps1): hosts entry, a certificate for the name in
# LocalMachine\Root, the HTTPS binding on 443, the server started outside the PowerShell Direct session. -Files go to
# its download folder (served at https://update.remotehub.dev/download/<name>). Set-LabMockLatest sets /latest.
function Start-LabMockServer {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string[]]$Files = @())
    $script = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\guest\RhMockServer.ps1'))
    Invoke-LabGuest -Name $Name -ArgumentList $script -ScriptBlock {
        param($script)
        $root = 'C:\ProgramData\RemoteHubLab\mock'
        $null = New-Item -ItemType Directory -Path "$root\download" -Force
        [IO.File]::WriteAllText("$root\RhMockServer.ps1", $script)
        # a second name, dl.untrusted.test: reachable with a valid certificate but not an allowed download host (the
        # redirect target of C-UPD-08)
        Add-Content "$env:WINDIR\System32\drivers\etc\hosts" "`r`n127.0.0.1 update.remotehub.dev`r`n127.0.0.1 dl.untrusted.test"
        $cert = New-SelfSignedCertificate -DnsName 'update.remotehub.dev', 'dl.untrusted.test' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(30)
        $root2 = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine'); $root2.Open('ReadWrite'); $root2.Add($cert); $root2.Close()
        $null = netsh.exe http add sslcert ipport=0.0.0.0:443 certhash=$($cert.Thumbprint) appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}'
        foreach ($h in 'update.remotehub.dev', 'dl.untrusted.test') {
            $null = netsh.exe http add sslcert hostnameport="${h}:443" certhash=$($cert.Thumbprint) appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}' certstorename=MY
        }
        $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$root\RhMockServer.ps1`" -Root `"$root`""
        $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        Start-Sleep -Seconds 3
    }
    if ($Files) { Copy-LabFile -Name $Name -Path $Files -Destination 'C:\ProgramData\RemoteHubLab\mock\download' }
}

# What /latest answers: -Version, the download (a file given to Start-LabMockServer, its SHA-256 and size are filled in
# unless -Sha256 / -Url override them), -Mandatory, -MinSupported. -None: 404 (no release).
function Set-LabMockLatest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Version, [string]$File, [string]$Url, [string]$Sha256,
        [switch]$Mandatory, [string]$MinSupported, [string]$Channel = 'stable', [switch]$None)
    Invoke-LabGuest -Name $Name -ArgumentList $Version, $File, $Url, $Sha256, $Mandatory.IsPresent, $MinSupported, $Channel, $None.IsPresent -ScriptBlock {
        param($version, $file, $url, $sha, $mandatory, $min, $channel, $none)
        $root = 'C:\ProgramData\RemoteHubLab\mock'
        if ($none) { Remove-Item "$root\latest.json" -ErrorAction SilentlyContinue; return }
        $path = if ($file) { Join-Path "$root\download" $file } else { $null }
        $download = [ordered]@{
            name   = $(if ($file) { $file } else { "RemoteHub-$version-x64.msi" })
            url    = $(if ($url) { $url } else { "https://update.remotehub.dev/download/$file" })
            sha256 = $(if ($sha) { $sha } elseif ($path) { (Get-FileHash $path -Algorithm SHA256).Hash } else { '0' * 64 })
            size   = $(if ($path) { (Get-Item $path).Length } else { 1 })
        }
        $latest = [ordered]@{ version = $version; channel = $channel; prerelease = $false; notes = 'Lab release.'; publishedAt = (Get-Date).ToUniversalTime().ToString('o')
            mandatory = $mandatory; minSupportedVersion = $(if ($min) { $min } else { $null }); download = $download }
        [IO.File]::WriteAllText("$root\latest.json", ($latest | ConvertTo-Json -Depth 4))
    }
}

# The requests the mock got so far (time, method, path, query, agent, body - telemetry bodies parsed into .json).
# The mock's behaviour beyond latest.json (RhMockServer.ps1 reads <Root>\control.json per request): -LatestStatus answers
# /latest with that code (500 ...), -LatestDelayMs stalls it (RemoteHub gives up after 15 s), -TelemetryStatus refuses
# /telemetry (e.g. 503), -Redirects @{ '/download/x.msi' = 'https://elsewhere/x.msi' } answers 302. -Clear removes it.
function Set-LabMockControl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [int]$LatestStatus, [int]$LatestDelayMs, [int]$TelemetryStatus, [hashtable]$Redirects, [switch]$Clear)
    $c = [ordered]@{}
    if ($LatestStatus) { $c.latestStatus = $LatestStatus }
    if ($LatestDelayMs) { $c.latestDelayMs = $LatestDelayMs }
    if ($TelemetryStatus) { $c.telemetryStatus = $TelemetryStatus }
    if ($Redirects) { $c.redirects = $Redirects }
    $json = if ($Clear) { $null } else { $c | ConvertTo-Json -Depth 4 }
    Invoke-LabGuest -Name $Name -ArgumentList $json -ScriptBlock {
        param($json)
        $p = 'C:\ProgramData\RemoteHubLab\mock\control.json'
        if ($json) { [IO.File]::WriteAllText($p, $json) } else { Remove-Item $p -ErrorAction SilentlyContinue }
    }
}

function Get-LabMockRequests {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Invoke-LabGuest -Name $Name -ScriptBlock {
        @(Get-Content 'C:\ProgramData\RemoteHubLab\mock\requests.jsonl' -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object {
                $r = $_ | ConvertFrom-Json
                if ($r.body) { try { $r | Add-Member -NotePropertyName json -NotePropertyValue ($r.body | ConvertFrom-Json) } catch { } }
                $r
            })
    }
}

# Code run in the account's session (Invoke-LabInSession) that answers an object: -Code's output goes through a JSON file
# in the guest (not the session's console text, which Out-String would wrap), $A in -Code is -Data (a JSON round trip).
# Throws with the session's text when the code failed or answered nothing.
function Invoke-LabInSessionJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Code,
        [hashtable]$Data = @{},
        [string]$Account = 'Console',
        [int]$TimeoutSec = 300
    )
    $out = "C:\ProgramData\RemoteHubLab\session\json-$([guid]::NewGuid().ToString('N')).json"
    # -Data as base64: no quoting of what it holds
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Data -Depth 8 -Compress)))
    $text = "`$ErrorActionPreference = 'Stop'`n`$A = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64')) | ConvertFrom-Json`n" +
        "`$r = & {`n$Code`n}`n" +
        "`$j = if (`$null -eq `$r) { 'null' } else { ConvertTo-Json -InputObject `$r -Depth 10 -Compress }`n" +
        "[IO.File]::WriteAllText('$out', `$j, (New-Object Text.UTF8Encoding `$false))"
    $raw = Invoke-LabInSession -Name $Name -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($text))
    $answer = Invoke-LabGuest -Name $Name -ArgumentList $out -ScriptBlock { param($f) if (Test-Path -LiteralPath $f) { [IO.File]::ReadAllText($f); Remove-Item -LiteralPath $f } }
    if (-not $answer) { throw "${Name} / ${Account}: the session code answered nothing: $("$raw".Trim())" }
    $answer | ConvertFrom-Json
}

# RemoteHub's local database as RemoteHub opens it (LocalDb.Connect): the installed sqlite3mc.dll, cipher chacha20,
# kdf_iter 1, the raw key from db.key (DPAPI of the account, entropy RemoteHub.Database.v1). Read in the account's own
# session, so DPAPI works; a db.key sealed by the startup password (version 2) is refused. -Query: name = SELECT; answers
# an object with one property per name, each an array of rows (BLOB columns as base64). Meant for SELECTs only.
$script:LabSqlCs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RhLabSql
{
    public static class Db
    {
        const string L = "sqlite3mc.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_key(IntPtr db, byte[] key, int n);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_name(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(L, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);

        static bool loaded;

        public static void Load(string dll)
        {
            if (loaded) return;
            if (LoadLibraryW(dll) == IntPtr.Zero) throw new Exception("LoadLibrary failed for " + dll + ": " + Marshal.GetLastWin32Error());
            loaded = true;
        }

        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        static string Utf8Z(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            List<byte> bytes = new List<byte>();
            for (int i = 0; ; i++) { byte b = Marshal.ReadByte(p, i); if (b == 0) break; bytes.Add(b); }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        static void Check(IntPtr db, int rc)
        {
            if (rc != 0 && rc != 100 && rc != 101) throw new Exception("SQLite " + rc + ": " + Utf8Z(sqlite3_errmsg(db)));
        }

        // db.key "RHDK" 1 + DPAPI: the first 32 of its 64 bytes are the database key
        public static byte[] ReadKey(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 6 || b[0] != (byte)'R' || b[1] != (byte)'H' || b[2] != (byte)'D' || b[3] != (byte)'K') throw new InvalidDataException("not a RemoteHub db.key");
            if (b[4] != 1) throw new InvalidOperationException("db.key is sealed with the startup protection (version " + b[4] + ")");
            byte[] inner = new byte[b.Length - 5];
            Array.Copy(b, 5, inner, 0, inner.Length);
            byte[] plain = ProtectedData.Unprotect(inner, Encoding.UTF8.GetBytes("RemoteHub.Database.v1"), DataProtectionScope.CurrentUser);
            if (plain.Length != 64) throw new InvalidDataException("db.key holds " + plain.Length + " bytes");
            byte[] key = new byte[32];
            Array.Copy(plain, key, 32);
            Array.Clear(plain, 0, plain.Length);
            return key;
        }

        public static IntPtr Open(string path, byte[] key)
        {
            IntPtr db;
            int rc = sqlite3_open_v2(Z(path), out db, 2, IntPtr.Zero);
            if (rc != 0) { string m = Utf8Z(sqlite3_errmsg(db)); sqlite3_close_v2(db); throw new Exception("SQLite " + rc + ": " + m); }
            try
            {
                Query(db, "PRAGMA cipher = 'chacha20'");
                Query(db, "PRAGMA kdf_iter = 1");
                Check(db, sqlite3_key(db, key, key.Length));
                Query(db, "SELECT count(*) FROM sqlite_master");
                return db;
            }
            catch { sqlite3_close_v2(db); throw; }
        }

        public static void Close(IntPtr db) { if (db != IntPtr.Zero) sqlite3_close_v2(db); }

        public static List<Dictionary<string, object>> Query(IntPtr db, string sql)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            byte[] s = Z(sql);
            IntPtr st;
            Check(db, sqlite3_prepare_v2(db, s, s.Length, out st, IntPtr.Zero));
            try
            {
                while (true)
                {
                    int rc = sqlite3_step(st);
                    if (rc == 101) break;
                    if (rc != 100) Check(db, rc);
                    Dictionary<string, object> row = new Dictionary<string, object>();
                    int n = sqlite3_column_count(st);
                    for (int i = 0; i < n; i++)
                    {
                        string name = Utf8Z(sqlite3_column_name(st, i));
                        int t = sqlite3_column_type(st, i);
                        object v = null;
                        if (t == 1) v = sqlite3_column_int64(st, i);
                        else if (t == 2) v = sqlite3_column_double(st, i);
                        else if (t == 3 || t == 4)
                        {
                            IntPtr p = t == 3 ? sqlite3_column_text(st, i) : sqlite3_column_blob(st, i);
                            int len = sqlite3_column_bytes(st, i);
                            byte[] b = new byte[len];
                            if (len > 0) Marshal.Copy(p, b, 0, len);
                            v = t == 3 ? (object)Encoding.UTF8.GetString(b) : Convert.ToBase64String(b);
                        }
                        row[name] = v;
                    }
                    rows.Add(row);
                }
            }
            finally { sqlite3_finalize(st); }
            return rows;
        }
    }
}
'@

function Invoke-LabClientSql {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$Query,
        [string]$Account = 'Console',
        [int]$TimeoutSec = 180
    )
    Invoke-LabGuest -Name $Name -ArgumentList $script:LabSqlCs -ScriptBlock {
        param($cs)
        $d = 'C:\ProgramData\RemoteHubLab'; $null = New-Item -ItemType Directory -Path $d -Force
        [IO.File]::WriteAllText("$d\rhsql.cs", $cs)
        icacls "$d\rhsql.cs" /grant '*S-1-5-32-545:RX' | Out-Null
    }
    $code = @'
Add-Type -Path 'C:\ProgramData\RemoteHubLab\rhsql.cs' -ReferencedAssemblies System.Security
$dll = Get-ChildItem 'C:\Program Files\RemoteHub' -Recurse -Filter 'sqlite3mc.dll' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $dll) { throw 'sqlite3mc.dll not found under C:\Program Files\RemoteHub' }
[RhLabSql.Db]::Load($dll)
$dir = Join-Path $env:LOCALAPPDATA 'RemoteHub'
$key = [RhLabSql.Db]::ReadKey((Join-Path $dir 'db.key'))
$db = [RhLabSql.Db]::Open((Join-Path $dir 'remotehub.db'), $key)
$sets = @{}
try {
    foreach ($p in $A.PSObject.Properties) {
        $rows = @(foreach ($row in [RhLabSql.Db]::Query($db, [string]$p.Value)) {
                $o = [ordered]@{}
                foreach ($kv in $row.GetEnumerator()) { $o[$kv.Key] = $kv.Value }
                [pscustomobject]$o
            })
        $sets[$p.Name] = $rows
    }
}
finally { [RhLabSql.Db]::Close($db); [Array]::Clear($key, 0, $key.Length) }
$sets
'@
    $r = Invoke-LabInSessionJson -Name $Name -Account $Account -Code $code -Data $Query -TimeoutSec $TimeoutSec
    # each answer as an array, also with one row or none
    $out = [ordered]@{}
    foreach ($k in $Query.Keys) { $out[$k] = @($r.$k | Where-Object { $null -ne $_ }) }
    [pscustomobject]$out
}

#endregion

#region Network shares

# Turns the guest into a file server for RemoteHub's network shares: two SMB shares (rhshare for the admin account,
# rhshare2 for the standard user too), OpenSSH (SFTP), IIS FTP with TLS required (self-signed certificate, passive
# ports 50000-50100) and IIS WebDAV on /dav over HTTP and HTTPS (same certificate, Windows authentication).
# Answers what was set up, with the certificate's SHA-256 fingerprint as RemoteHub shows it.
function Enable-LabShareServers {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    $vm = Get-LabVm $Name
    Invoke-LabGuest -Name $Name -ArgumentList $vm.UserAccount, $vm.AdminAccount -ScriptBlock {
        param($user, $admin)
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        # SMB
        foreach ($s in 'rhshare', 'rhshare2') {
            $null = New-Item "C:\$s" -ItemType Directory -Force
            if (-not (Get-SmbShare -Name $s -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name $s -Path "C:\$s" -FullAccess 'Administrators', $admin, $user }
            $null = icacls "C:\$s" /grant "${user}:(OI)(CI)M" "${admin}:(OI)(CI)F"
        }
        Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502'   # File and Printer Sharing
        # SFTP
        if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) { $null = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' }
        # Port 2222: the router between the lab host and the VMs throttles SSH on port 22 after a few quick connections
        # (it also tests a share with its own port). Windows' own sshd rule covers only the private profile.
        Set-Service sshd -StartupType Manual
        Start-Service sshd   # the first start writes sshd_config
        $cfgPath = Join-Path $env:ProgramData 'ssh\sshd_config'
        $lines = @(Get-Content $cfgPath | Where-Object { $_ -notmatch '^\s*#?\s*Port\s' })
        Set-Content $cfgPath -Value (@('Port 2222') + $lines) -Encoding ascii
        Restart-Service sshd
        $null = New-NetFirewallRule -DisplayName 'RemoteHub lab SSH' -Protocol TCP -LocalPort 2222 -Direction Inbound -Action Allow -Profile Any
        # IIS: FTP and WebDAV
        $null = Install-WindowsFeature Web-Server, Web-Ftp-Server, Web-DAV-Publishing, Web-Windows-Auth, Web-Basic-Auth
        Import-Module WebAdministration
        $cert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -CertStoreLocation Cert:\LocalMachine\My
        $null = New-Item C:\RHFtp -ItemType Directory -Force
        $null = icacls C:\RHFtp /grant 'Users:(OI)(CI)M'
        if (-not (Get-Website -Name RHFtp)) { $null = New-WebFtpSite -Name RHFtp -Port 21 -PhysicalPath C:\RHFtp -Force }
        $site = 'IIS:\Sites\RHFtp'
        Set-ItemProperty $site -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
        Set-ItemProperty $site -Name ftpServer.security.ssl.serverCertHash -Value $cert.Thumbprint
        Set-ItemProperty $site -Name ftpServer.security.ssl.controlChannelPolicy -Value 1   # SslRequire
        Set-ItemProperty $site -Name ftpServer.security.ssl.dataChannelPolicy -Value 1
        Add-WebConfiguration -PSPath IIS:\ -Location RHFtp -Filter system.ftpServer/security/authorization -Value @{ accessType = 'Allow'; users = '*'; permissions = 'Read,Write' }
        Set-WebConfigurationProperty -PSPath IIS:\ -Filter system.ftpServer/firewallSupport -Name lowDataChannelPort -Value 50000
        Set-WebConfigurationProperty -PSPath IIS:\ -Filter system.ftpServer/firewallSupport -Name highDataChannelPort -Value 50100
        $null = New-NetFirewallRule -DisplayName 'RemoteHub lab FTP' -Protocol TCP -LocalPort 21, '50000-50100' -Direction Inbound -Action Allow
        Restart-Service ftpsvc
        $null = New-Item C:\inetpub\wwwroot\dav -ItemType Directory -Force
        $null = icacls C:\inetpub\wwwroot\dav /grant 'Users:(OI)(CI)M'
        $w = 'Default Web Site'
        Set-WebConfigurationProperty -PSPath IIS:\ -Location $w -Filter system.webServer/webdav/authoring -Name enabled -Value $true
        Add-WebConfiguration -PSPath IIS:\ -Location $w -Filter system.webServer/webdav/authoringRules -Value @{ users = '*'; path = '*'; access = 'Read,Write,Source' }
        Set-WebConfigurationProperty -PSPath IIS:\ -Location $w -Filter system.webServer/security/authentication/windowsAuthentication -Name enabled -Value $true
        Set-WebConfigurationProperty -PSPath IIS:\ -Location $w -Filter system.webServer/security/authentication/anonymousAuthentication -Name enabled -Value $false
        if (-not (Get-WebBinding -Name $w -Protocol https)) { New-WebBinding -Name $w -Protocol https -Port 443 }
        (Get-WebBinding -Name $w -Protocol https).AddSslCertificate($cert.Thumbprint, 'My')
        $null = New-NetFirewallRule -DisplayName 'RemoteHub lab web' -Protocol TCP -LocalPort 80, 443 -Direction Inbound -Action Allow
        $sha = [Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)
        [pscustomobject]@{
            Smb = @('rhshare', 'rhshare2'); Sftp = (Get-Service sshd).Status.ToString(); Ftp = (Get-Website -Name RHFtp).State
            Certificate = ($sha | ForEach-Object { $_.ToString('X2') }) -join ':'
        }
    }
}

# Runs the harness' share scenario (ShareConnector + ShareTransfer) with the lab password; answers its JSON.
function Invoke-LabShare {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Conflict, [string]$WrongPassword)
    $cmd = if ($Conflict) { 'share-conflict' } else { 'share' }
    $password = if ($WrongPassword) { $WrongPassword } else { (Get-LabConfig).Password }
    Invoke-LabHarness (@($cmd) + $Arguments + @('--password', $password)) -TimeoutSec 600
}

#endregion

#region Timing

# Where a file's time goes (Invoke-LabTestFile.ps1 sets RHLAB_TIMING and writes <result>.timing.json): the slow helpers
# are wrapped so each outermost call adds its time to $global:RhLabTiming - a helper's calls to another count once, for
# the outer one. Global, as the test files import the module again (-Force) and the totals must survive that.
if ($env:RHLAB_TIMING -eq '1') {
    if (-not (Get-Variable RhLabTiming -Scope Global -ErrorAction SilentlyContinue)) {
        $global:RhLabTiming = @{ Depth = 0; Calls = @{} }
    }
    foreach ($name in @('Reset-LabVm', 'Initialize-LabVm', 'Restart-LabVm', 'Wait-LabVm', 'Wait-LabDesktop', 'Set-LabAutoLogon',
            'Invoke-LabGuest', 'Copy-LabFile', 'Get-LabScreenshot', 'Get-LabOcr', 'Invoke-LabInSession', 'Invoke-LabPerson',
            'Invoke-LabDesktop', 'Start-LabDesktop', 'Receive-LabDesktop', 'Invoke-LabHarness', 'Install-LabAgent',
            'Update-LabAgent', 'Uninstall-LabAgent', 'Restart-LabAgent', 'Wait-LabAgent', 'Install-LabClient', 'Start-LabClient',
            'Stop-LabClient', 'Invoke-LabUi', 'Invoke-LabAutoSignIn')) {
        $inner = (Get-Item "function:$name" -ErrorAction SilentlyContinue)
        if (-not $inner) { continue }
        # The wrapper has the helper's own parameters and passes on what was bound. "& $orig @args" lost the colon of
        # "-Optional:$false": the switch came through as set and $false bound to the next free parameter by position
        # (the tests' Ui wrappers then ran in "False's session" - lab run 20261020-185549). The closure's variables
        # have names no helper uses as a parameter (-Name would hide $name).
        $rhTimedOrig = $inner.ScriptBlock
        $rhTimedName = $name
        $meta = New-Object System.Management.Automation.CommandMetadata $inner
        $wrapper = [scriptblock]::Create([System.Management.Automation.ProxyCommand]::GetCmdletBindingAttribute($meta) +
            "`nparam(" + [System.Management.Automation.ProxyCommand]::GetParamBlock($meta) + ")`n" + {
                $rhTimed = $global:RhLabTiming
                $rhTimed.Depth++
                $rhTimedWatch = [Diagnostics.Stopwatch]::StartNew()
                try { & $rhTimedOrig @PSBoundParameters }
                finally {
                    $rhTimed.Depth--
                    if ($rhTimed.Depth -eq 0) {
                        if (-not $rhTimed.Calls.ContainsKey($rhTimedName)) { $rhTimed.Calls[$rhTimedName] = @{ Count = 0; Seconds = 0.0 } }
                        $rhTimed.Calls[$rhTimedName].Count++
                        $rhTimed.Calls[$rhTimedName].Seconds += $rhTimedWatch.Elapsed.TotalSeconds
                    }
                }
            }.ToString()).GetNewClosure()
        Set-Item "function:script:$name" -Value $wrapper
    }
}

#endregion