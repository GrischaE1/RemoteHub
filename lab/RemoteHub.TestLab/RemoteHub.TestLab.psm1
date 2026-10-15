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
            $parts = $hv.Name -split '\s+-\s+'
            if ($parts.Count -ne 4) { continue }
            $vm = [ordered]@{
                Name = $hv.Name; Id = ($parts[1..3] -join '-'); Os = $parts[1]; Version = $parts[2]; Language = $parts[3]
            }
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
        # always the newest checkpoint - a refreshed image is just a new checkpoint
        $snap = Get-VMSnapshot -VMName $vm.Name | Sort-Object CreationTime -Descending | Select-Object -First 1
        if (-not $snap) { throw "$($vm.Name): no checkpoint to revert to." }
        Write-Verbose "$($vm.Name): reverting to checkpoint '$($snap.Name)' ($($snap.CreationTime))"
        Restore-VMSnapshot -VMSnapshot $snap -Confirm:$false
        # a VM a jump host test left behind the jump host: back on its own switch
        Restore-LabVmNetwork -Name $vm.Name
        if ((Get-VM -Name $vm.Name).State -ne 'Running') { Start-VM -Name $vm.Name }
        Wait-LabVm -Name $vm.Name -TimeoutSec $TimeoutSec
    }
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
# and answers adapter -> switch, for Connect-LabNetwork. A revert to the checkpoint reconnects them as well.
# -ExceptSwitch: adapters on that switch stay (the jump host keeps its side towards the targets).
function Disconnect-LabNetwork {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$ExceptSwitch)
    $Name = (Get-LabVm $Name).Name
    $map = @{}
    foreach ($a in Get-VMNetworkAdapter -VMName $Name | Where-Object { -not $ExceptSwitch -or $_.SwitchName -ne $ExceptSwitch }) {
        if ($a.SwitchName) { $map[$a.Name] = $a.SwitchName }
        Disconnect-VMNetworkAdapter -VMNetworkAdapter $a
    }
    $map
}

function Connect-LabNetwork {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][hashtable]$Switches)
    $Name = (Get-LabVm $Name).Name
    foreach ($a in Get-VMNetworkAdapter -VMName $Name) {
        if ($Switches.ContainsKey($a.Name)) { Connect-VMNetworkAdapter -VMNetworkAdapter $a -SwitchName $Switches[$a.Name] }
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
    foreach ($r in @(Get-Content $record -Raw | ConvertFrom-Json)) {
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

function Invoke-LabGuest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)][string]$Name,
        [Parameter(Mandatory, Position = 1)][scriptblock]$ScriptBlock,
        [object[]]$ArgumentList = @(),
        [string]$Account = 'Admin'
    )
    $vm = Get-LabVm $Name
    $session = New-LabSession -Name $Name -Credential (Get-LabCredential (Resolve-LabAccount $vm $Account))
    try { Invoke-Command -Session $session -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList }
    finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
}

# Host -> guest. Destination is a guest folder; it is created if missing.
function Copy-LabFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Path,
        [Parameter(Mandatory)][string]$Destination
    )
    $vm = Get-LabVm $Name
    $session = New-LabSession -Name $Name -Credential (Get-LabCredential $vm.AdminAccount)
    try {
        Invoke-Command -Session $session -ArgumentList $Destination -ScriptBlock {
            param($d) $null = New-Item -ItemType Directory -Path $d -Force
        }
        Copy-Item -Path $Path -Destination $Destination -ToSession $session -Recurse -Force
    }
    finally { Remove-PSSession $session }
}

# Guest -> host. Destination is a host folder; it is created if missing. Missing guest files are skipped.
function Get-LabFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$Path,
        [Parameter(Mandatory)][string]$Destination
    )
    $vm = Get-LabVm $Name
    $null = New-Item -ItemType Directory -Path $Destination -Force
    $session = New-LabSession -Name $Name -Credential (Get-LabCredential $vm.AdminAccount)
    try {
        $existing = @(Invoke-Command -Session $session -ArgumentList (, $Path) -ScriptBlock {
                param($p) $p | Where-Object { Test-Path $_ }
            })
        if ($existing.Count -gt 0) {
            Copy-Item -Path $existing -Destination $Destination -FromSession $session -Recurse -Force
        }
    }
    finally { Remove-PSSession $session }
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

function Get-LabKeyboard {
    param([Parameter(Mandatory)][string]$Name)
    $kb = @(Get-CimAssociatedInstance -InputObject (Get-LabCimVm $Name) -ResultClassName Msvm_Keyboard)
    if ($kb.Count -eq 0) { throw "${Name}: no keyboard (is the VM running?)." }
    $kb[0]
}

# Types ASCII text on the console, like a person at the keyboard.
function Send-LabText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Text)
    $r = Invoke-CimMethod -InputObject (Get-LabKeyboard $Name) -MethodName TypeText -Arguments @{ asciiText = $Text }
    Assert-CimResult $r "${Name}: TypeText"
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
    foreach ($c in $Codes) { Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = $c }) "${Name}: press $c" }
    Start-Sleep -Milliseconds 100
    foreach ($c in $Codes[($Codes.Count - 1)..0]) { Assert-CimResult (Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = $c }) "${Name}: release $c" }
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
    }
    Restart-LabVm -Name $Name -TimeoutSec $TimeoutSec
    Wait-LabDesktop -Name $Name -Account $user -TimeoutSec $TimeoutSec
}

function Clear-LabAutoLogon {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
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
            $unlocked = Invoke-LabGuest -Name $Name -ArgumentList $user -ScriptBlock {
                param($u)
                $lockedSessions = @(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId)
                @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Where-Object {
                        (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $u -and
                        $lockedSessions -notcontains $_.SessionId
                    }).Count -gt 0
            }
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
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $enc"
        $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel $(if ($elevated) { 'Highest' } else { 'Limited' })
        $null = Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force
        try {
            Start-ScheduledTask -TaskName $task
            $deadline = (Get-Date).AddSeconds($timeout)
            while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
            if (-not (Test-Path $done)) { throw "no result from the session task after $timeout s (is $u signed in?)" }
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
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $enc"
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
        Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
    }
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
            else { Write-Warning "${Name}: '$Text' not found on the agent's window; OCR read: $(@($found.lines) -join ' | ')" }
            return $found
        }
        $w = @($found | Where-Object { $_.Title -like $Title -or $_.Title -like "RemoteHub*$Title" })[0]
        if ($w) { Send-LabMouse -Name $Name -X ($w.X + [int]($w.Width / 2)) -Y ($w.Y + [int]($w.Height / 2)) -Action Move; Start-Sleep -Seconds 2 }
        return [pscustomobject]@{ before = $w; after = @(Invoke-LabPerson -Name $Name -Do windows -Account $Account | Where-Object Title -eq $w.Title)[0] }
    }
    if ($Do -eq 'locate') { $Do = 'click'; $Value = 'locate' }
    Copy-LabPerson -Name $Name
    $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
    $answer = "C:\ProgramData\RemoteHubLab\session\person-$([guid]::NewGuid().ToString('N').Substring(0, 8)).json"
    $cmd = "& 'C:\ProgramData\RemoteHubLab\RhPerson.ps1' -Do $Do -Title $(& $q $Title) -TimeoutSec $TimeoutSec -Out '$answer'"
    if ($Text) { $cmd += " -Text $(& $q $Text)" }
    if ($PSBoundParameters.ContainsKey('Value')) { $cmd += " -Value $(& $q $Value)" }
    $raw = Invoke-LabInSession -Name $Name -Account $Account -TimeoutSec ($TimeoutSec + 60) -ScriptBlock ([scriptblock]::Create($cmd))
    $json = Invoke-LabGuest -Name $Name -ArgumentList $answer -ScriptBlock { param($f) if (Test-Path $f) { Get-Content $f -Raw; Remove-Item $f } }
    if (-not $json) { throw "RhPerson -Do $Do gave no answer: $raw" }
    $json | ConvertFrom-Json
}

# Windows' OCR of a picture file on this PC (a decoded frame, a console screenshot): text and lines.
function Get-LabOcr {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $script = Join-Path $PSScriptRoot '..\guest\RhPerson.ps1'
    $out = Join-Path ([IO.Path]::GetTempPath()) "rhlab-ocr-$([guid]::NewGuid().ToString('N').Substring(0, 8)).json"
    $null = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Do ocr-file -Value $Path -Out $out
    try { Get-Content $out -Raw | ConvertFrom-Json } finally { Remove-Item $out -ErrorAction SilentlyContinue }
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

# The marker window (RhPerson -Do marker) in the account's session: full screen, magenta, records keys, mouse and
# clipboard to a guest file. Returns the handle for Get-LabMarker / Stop-LabInSession.
function Start-LabMarker {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'User')
    Copy-LabPerson -Name $Name
    $file = "C:\ProgramData\RemoteHubLab\session\marker-$([guid]::NewGuid().ToString('N').Substring(0, 8)).jsonl"
    $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create("& 'C:\ProgramData\RemoteHubLab\RhPerson.ps1' -Do marker -Out '$file'"))
    $until = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $until) {
        if (Invoke-LabGuest -Name $Name -ArgumentList $file -ScriptBlock { param($f) (Test-Path $f) -and (Get-Content $f -Raw) -match '"shown"' }) { break }
        Start-Sleep -Seconds 1
    }
    [pscustomobject]@{ Task = $task; File = $file }
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
        [IO.File]::WriteAllText("$d\RhPerson.ps1", $text)
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
    $toJump = $hostAt -ge 0 -and $hostAt + 1 -lt @($Arguments).Count -and $Arguments[$hostAt + 1] -eq $script:JumpAddress
    if ($script:JumpArgs -and -not $toJump -and $Arguments[0] -notin 'install', 'enrollment', 'seed', 'share-conflict', 'probe') { $Arguments = @($Arguments) + $script:JumpArgs }
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

# A .rhagent enrollment file as RemoteHub makes it (harness "enrollment"), written on the host. Scope defaults to the
# host's address towards the VM. Returns file, pin and deviceId.
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
    if (-not (Test-Path $cer)) { return }
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
    $stepsFile = Join-Path $Out "steps-$([guid]::NewGuid().ToString('N').Substring(0, 6)).json"
    [IO.File]::WriteAllText($stepsFile, (ConvertTo-Json @($Steps) -Depth 6))
    $a = @('desktop', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', (Resolve-LabAccount $vm $Account), '--password', $(if ($PSBoundParameters.ContainsKey('Password')) { $Password } else { (Get-LabConfig).Password }),
        '--steps', $stepsFile, '--out', $Out, '--timeout', "$TimeoutSec")
    Invoke-LabHarness $a -TimeoutSec ($TimeoutSec + 60) -VmName $Name
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
        # automatic sign-in: the credential provider (DLL, both registrations) and the policy values the agent set
        $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'   # Package.wxs
        $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue
        $rights = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue
        $autoSignIn = [pscustomobject]@{
            Dll          = Test-Path 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
            Provider     = Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
            InprocServer = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
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
# policy unless -Network (a client in the lab must not call update.remotehub.dev).
function Install-LabClient {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Msi, [switch]$Network)
    if (-not $Msi) { $Msi = Get-LabClientMsi }
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
# the task to stop it with Stop-LabClient. -Arguments: .rdp paths in the guest.
function Start-LabClient {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [string]$Account = 'Console', [string[]]$Arguments = @(), [int]$TimeoutSec = 120)
    $argText = ($Arguments | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ' '
    $task = Start-LabInSession -Name $Name -Account $Account -ScriptBlock ([scriptblock]::Create("& 'C:\Program Files\RemoteHub\RemoteHub.exe' $argText"))
    $main = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'New' -Value exists -TimeoutSec $TimeoutSec
    if (-not $main.found) { throw "${Name}: RemoteHub's main window didn't appear (windows: $($main.windows -join ', '))." }
    # it may open partly off the screen (seen at x < 0): moved into it, so real clicks reach every part
    $null = Invoke-LabPerson -Name $Name -Account $Account -Do uia -Title 'RemoteHub*' -Text 'NewButton' -Value fit -TimeoutSec 10
    $task
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
        throw "UI step failed: '$Text' in '$Title' ($Value): $(if ($r.PSObject.Properties['error']) { $r.error + $(if ($r.PSObject.Properties['entries']) { ' - entries: ' + (@($r.entries) -join ', ') }) } else { 'not found; windows: ' + ($r.windows -join ', ') })"
    }
    $r
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
        Add-Content "$env:WINDIR\System32\drivers\etc\hosts" "`r`n127.0.0.1 update.remotehub.dev"
        $cert = New-SelfSignedCertificate -DnsName 'update.remotehub.dev' -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddDays(30)
        $root2 = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine'); $root2.Open('ReadWrite'); $root2.Add($cert); $root2.Close()
        $null = netsh.exe http add sslcert ipport=0.0.0.0:443 certhash=$($cert.Thumbprint) appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}'
        $null = netsh.exe http add sslcert hostnameport=update.remotehub.dev:443 certhash=$($cert.Thumbprint) appid='{4dc3e181-e14b-4a21-b022-59fc669b0914}' certstorename=MY
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
