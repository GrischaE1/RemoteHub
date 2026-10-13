Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Config = $null
$script:Ns = 'root\virtualization\v2'

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
    foreach ($n in $Name) {
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
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $ips = @(Get-VMNetworkAdapter -VMName $Name | ForEach-Object { $_.IPAddresses } |
            Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' -and $_ -notlike '169.254.*' })
        if ($ips.Count -gt 0) { return $ips[0] }
        Start-Sleep -Seconds 3
    }
    throw "${Name}: no IPv4 address reported by Hyper-V after $TimeoutSec s."
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
# The account must be signed in. Returns the code's output as text.
function Invoke-LabInSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock,
        [string]$Account = 'User',
        [int]$TimeoutSec = 120
    )
    $user = Resolve-LabAccount (Get-LabVm $Name) $Account
    Invoke-LabGuest -Name $Name -ArgumentList $user, $ScriptBlock.ToString(), $TimeoutSec -ScriptBlock {
        param($u, $code, $timeout)
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
        $principal = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive
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
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutSec = 900,
        [string]$VmName
    )
    $exe = Join-Path (Get-LabConfig).LabRoot 'harness\RemoteHub.LabHarness.exe'
    if (-not (Test-Path $exe)) { throw "Lab harness not found at $exe - Start-LabRun.ps1 builds it." }
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
        [bool]$VirtualDisplay = $true
    )
    $vm = Get-LabVm $Name
    $cfg = Get-LabConfig
    if (-not $Msi) { $Msi = Get-LabAgentMsi }
    if (-not $PSBoundParameters.ContainsKey('Scope')) { $Scope = Get-LabHostAddress -Name $Name }
    if (-not $Label) { $Label = $vm.Id }
    $harnessArgs = @('install', '--vm', $vm.Name, '--user', $vm.AdminAccount, '--password', $cfg.Password,
        '--msi', $Msi, '--label', $Label, '--port', "$Port", '--virtual-display', $(if ($VirtualDisplay) { '1' } else { '0' }))
    if ($Scope) { $harnessArgs += '--scope', $Scope }
    if ($KeepIdentity) { $harnessArgs += '--keep-identity', '1' }
    Invoke-LabHarness $harnessArgs -TimeoutSec 600 -VmName $Name
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
        [switch]$HoldSession
    )
    $vm = Get-LabVm $Name
    $cfg = Get-LabConfig
    if (-not $Msi) { $Msi = Get-LabAgentMsi }
    $harnessArgs = @('update', '--host', (Get-LabVmAddress -Name $Name), '--port', "$Port", '--pin', $Pin,
        '--user', $vm.AdminAccount, '--password', $cfg.Password, '--msi', $Msi,
        '--virtual-display', $(if ($VirtualDisplay) { '1' } else { '0' }))
    if ($NewIdentity) { $harnessArgs += '--new-identity', $vm.Id, '--scope', (Get-LabHostAddress -Name $Name) }
    if ($EnrollmentFile) { $harnessArgs += '--enrollment-file', $EnrollmentFile }
    if ($HoldSession) { $harnessArgs += '--hold-session', '1' }
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
        [Parameter(Mandatory)][ValidateSet('health', 'http', 'tls', 'ws', 'hold')][string]$Kind,
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
        [int]$TimeoutSec = 600
    )
    if (-not $Allow) { $Allow = @(Get-LabHostAddress -Name $Name) }
    $local = Join-Path $env:TEMP "RemoteHub-setup-$([guid]::NewGuid().ToString('N').Substring(0, 8)).ps1"
    $null = Invoke-LabHarness @('tool', '--op', 'script', '--host', (Get-LabVmAddress -Name $Name), '--allow', ($Allow -join '|'),
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
#   Truncated       - only the first 64 KB: not a package at all (msiexec 1620, version unreadable).
function New-LabBrokenMsi {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet('LaunchCondition', 'Truncated')][string]$Kind
    )
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
    Invoke-LabGuest -Name $Name -ArgumentList $Values, $Rights, [bool]$Clear -ScriptBlock {
        param($values, $rights, $clear)
        $key = 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent'
        if ($clear) { Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue }
        foreach ($set in @(@{ Path = $key; Items = $values }, @{ Path = "$key\Rights"; Items = $rights })) {
            if ($set.Items.Count -eq 0) { continue }
            $null = New-Item $set.Path -Force
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

# Copies the agent's logs to the evidence folder.
function Save-LabAgentEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Destination)
    Get-LabFile -Name $Name -Path 'C:\ProgramData\RemoteHub\Agent\logs' -Destination $Destination
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
