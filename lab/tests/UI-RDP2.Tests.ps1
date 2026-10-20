# R-RDP (more cases) - RemoteHub's RDP tabs on the admin's PC (this client VM, the console account's session, driven
# by UI Automation through RhPerson.ps1 -Do uia) to the device (the Server VM).
#
# Covers (docs/test-lab-cases.md section 10.4):
#   R-RDP-11 password prompt "Do nothing", R-RDP-10 the password toast and "Clear now", R-RDP-12 the switch hotkey
#   inside an RDP tab (and from full screen), R-RDP-13 an additional RDP session from the tree and from the tab bar.
# Needs: 2VM (this Windows 11 client + the Server VM). The device: Remote Desktop on, "Restrict each user to a single
# session" off (fSingleSessionPerUser = 0: two RDP sessions of PSAdmin at once), "Always prompt for password upon
# connection" for the prompt cases. This PC: the NTLM delegation policy for TERMSRV/* (the lab isn't a domain - R-RDP-01).
# The RDP control is an ActiveX host without UI Automation inside: what happens in the session is checked on the device
# (quser), the selected tab by the main window's title ("<tab> . <kind> - RemoteHub").
# Client VMs only. See docs/test-lab-cases.md section 10.4.
# LabRole: console
# TimeLimitMin: 90   (the jump host / several RDP sign-ins and reconnects: > 60 min)
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $password = (Get-LabConfig).Password
    $device = Get-LabDevice -For $VmName
    $script:task = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-rdp2-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-rdp2-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of a connection's context menu (right-click and the entry in one process; "A>B" = submenu, a parent that
    # isn't there is skipped)
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) {
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" }
    }
    # a key at the client's console keyboard (hex virtual-key code: 1B Esc)
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
        }
    }
    function Sha([string]$s) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s))).Replace('-', '') }
    # the names of the top-level windows in the console session (a search for a window that isn't there answers them)
    function TopWindows { @((Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'zz-no-such-window' -Text 'zz' -Value exists -TimeoutSec 1).windows) }
    # the connection of the selected tab, from the main window's title "<name> . <kind> - RemoteHub"
    function SelectedTab {
        $t = @(TopWindows | Where-Object { $_ -like '* RemoteHub' }) | Select-Object -First 1
        if ("$t" -match '^(.+?) \S Remote desktop') { $Matches[1] } else { "$t" }
    }
    # The RDP tab is connected: its header says "Click into the session to type" or "Keyboard -> remote session"
    function Wait-RdpTab([int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = Ui '*' -Value list -Optional -TimeoutSec 5
            if (@(@($l.items) | Where-Object { $_ -match '\|(Click into the session to type|Keyboard\W+remote session)\|' }).Count -gt 0) { return $true }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $false
    }
    # RDP tabs of the selected connection. The second tab row (each tab's kind) shows only when the connection has more
    # than one tab (MainWindow.xaml), so a single RDP tab has no row - then the main window's title says the kind (run
    # 20261016-212514 counted 0 for one tab and 2 for two).
    function RdpTabCount {
        $n = @(@((Ui '*' -Value list -Optional -TimeoutSec 10).items) | Where-Object { $_ -match '^Text\|Remote desktop\|' }).Count
        if ($n -eq 0 -and @(TopWindows | Where-Object { $_ -match ' \S Remote desktop \S RemoteHub$' }).Count) { $n = 1 }
        $n
    }
    # waits until the selected connection has $Count RDP tabs; answers the last count
    function Wait-RdpTabCount([int]$Count, [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $n = RdpTabCount
            if ($n -eq $Count) { return $n }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $until)
        $n
    }
    # a click into the RDP session of the selected tab with the console mouse (the control gets the keyboard)
    function ClickIntoSession {
        $fit = Ui 'Reconnect' -Value fit
        $rect = @("$($fit.value)" -split '[,;]' | ForEach-Object { [double]$_ })
        Start-Sleep -Seconds 2
        Send-LabMouse -Name $VmName -X ([int]($rect[0] + $rect[2] * 0.65)) -Y ([int]($rect[1] + $rect[3] * 0.6))
        Start-Sleep -Seconds 2
    }
    # PSAdmin's RDP sessions on the device (quser)
    function DeviceRdp {
        @(Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
                param($u)
                @(quser 2>$null | Select-Object -Skip 1 | ForEach-Object {
                        if ($_ -match '^\s*>?(?<u>\S+)\s+(?:(?<n>\S+)\s+)?(?<id>\d+)\s+(?<s>Active|Disc)' -and $Matches.u -eq $u -and $Matches.s -eq 'Active' -and "$($Matches.n)" -like 'rdp-tcp*') { [int]$Matches.id }
                    })
            })
    }
    # waits until PSAdmin has at least $Count active RDP sessions on the device (a sign-in takes a while); answers them
    # When fewer come, $script:quser holds what the device listed then and its single-session values, for the evidence
    # (lab run 20261019-193627: one session where two were expected, nothing recorded to tell why).
    function Wait-DeviceRdp([int]$Count, [int]$TimeoutSec = 90) {
        $script:quser = $null
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $ids = @(DeviceRdp)
            if ($ids.Count -ge $Count) { return $ids }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $script:quser = @(Invoke-LabGuest -Name $device.Name -ScriptBlock {
                $p = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -ErrorAction SilentlyContinue
                $t = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -ErrorAction SilentlyContinue
                @(quser 2>$null) + @("fSingleSessionPerUser: policy $(if ($p) { $p.fSingleSessionPerUser }), registry $(if ($t) { $t.fSingleSessionPerUser })")
            } | ForEach-Object { "$_".Trim() })
        $ids
    }
    function Clear-DeviceSessions {
        $null = Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
            param($u)
            foreach ($l in @(quser 2>$null | Select-Object -Skip 1)) {
                if ($l -match '^\s*>?(?<u>\S+)\s+(?:(?<n>\S+)\s+)?(?<id>\d+)\s+(?:Active|Disc)' -and $Matches.u -eq $u) { logoff $Matches.id }
            }
        }
        Start-Sleep -Seconds 5
    }
    function CloseAll { foreach ($i in 1..6) { $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 3; if (-not $c.found) { break }; Start-Sleep -Seconds 2 } }
    function Allow-NtlmDelegation {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation'
            $null = New-Item $k -Force
            foreach ($kind in 'Saved', 'Fresh') {
                Set-ItemProperty $k -Name "Allow$($kind)CredentialsWhenNTLMOnly" -Value 1 -Type DWord
                Set-ItemProperty $k -Name "ConcatenateDefaults_Allow$($kind)NTLMOnly" -Value 1 -Type DWord
                $l = New-Item "$k\Allow$($kind)CredentialsWhenNTLMOnly" -Force
                Set-ItemProperty $l.PSPath -Name '1' -Value 'TERMSRV/*'
            }
        }
    }
    # what this file leaves on the admin's PC, undone for the next file there: RemoteHub's data of the console account
    # (connections, settings, audit log) and the NTLM delegation policy. Throws when something stays.
    function Clear-ClientLeftovers {
        $left = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
            param($user)
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            $paths = "C:\Users\$user\AppData\Local\RemoteHub", "C:\Users\$user\AppData\Roaming\RemoteHub",
                "C:\Users\$user\Videos\RemoteHub", 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation'
            foreach ($p in $paths) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
            @($paths | Where-Object { Test-Path $_ })
        }
        if (@($left).Count) { throw "${VmName}: still there: $(@($left) -join ', ')" }
    }
    function AuditText([string]$Node = 'Device') {
        Menu 'More>Audit log*' -Node $Node
        $l = Ui '*' -Title 'Audit log*' -Value list -TimeoutSec 30
        $null = Ui 'Close' -Title 'Audit log*' -Optional -TimeoutSec 5
        (@($l.items) -join "`n")
    }
    function PasswordCopiedCount { ([regex]::Matches((AuditText), 'Password copied')).Count }
    function EditConnection([scriptblock]$Do, [string]$Node = 'Device') {
        Menu 'Edit*' -Node $Node
        $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
        & $Do
        $null = Ui 'Save' -Title 'Edit connection'
        Start-Sleep -Seconds 2
    }
    function PasswordPrompt([string]$Choice) {
        EditConnection {
            $null = Ui 'Security & session' -Title 'Edit connection'
            $null = Ui 'When Windows asks for the password' -Title 'Edit connection' -Value "choose:$Choice"
        }
    }
    # Windows' credential dialog of the RDP client, shown within -TimeoutSec (Wait-LabCredentialDialog: UI Automation
    # lists it inside RemoteHub's window with nothing in it, so a -Title 'Windows Security' element search never matched)
    function CredDialog([int]$TimeoutSec = 60) { Wait-LabCredentialDialog -Name $VmName -Account Console -TimeoutSec $TimeoutSec }
    # RemoteHub's password notice (ClipboardToast): a window of its own without a title
    function Toast([int]$TimeoutSec = 3) { Ui 'Stored password for*' -Title '*' -Value exists -Optional -TimeoutSec $TimeoutSec }
    # the clipboard's hash and length only: clip-get's "start" (the text's beginning) would be the stored password here,
    # and this goes into the evidence
    function Clip { Invoke-LabPerson -Name $VmName -Account Console -Do clip-get | Select-Object -Property * -ExcludeProperty start }

    if ($isClient) {
        Reset-LabVm -Name $device.Name
        Invoke-LabGuest -Name $device.Name -ScriptBlock {
            $ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
            Set-ItemProperty $ts -Name fDenyTSConnections -Value 0
            # two sessions of the same account at once (an additional session must not take over the first one)
            Set-ItemProperty $ts -Name fSingleSessionPerUser -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        }
        Restart-LabRdpService -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # RemoteHub installed (kept from an earlier file when it left the VM so; AfterAll empties RemoteHub's data)
        $null = Initialize-LabVm -Name $VmName -Client
        Allow-NtlmDelegation
        $script:task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
        # no question about the device's self-signed RDP certificate
        EditConnection {
            $null = Ui 'Security & session' -Title 'Edit connection'
            $null = Ui 'Server authentication' -Title 'Edit connection' -Value 'choose:Connect without warning'
        }
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        # undone for the next file on this VM; anything that fails: the next file starts from the checkpoint
        try { Clear-ClientLeftovers } catch { Set-LabVmDirty -Name $VmName }
        # the device: Remote Desktop on, two sessions per user
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-RDP2 <VmName>' -Tag Client, UI, 'R-RDP' -Skip:(-not $isClient) {

    Context 'the password prompt' {
        BeforeAll {
            $p = [ordered]@{}
            # "Always prompt for password upon connection" on the device: Windows asks although RemoteHub sends the password
            Invoke-LabGuest -Name $device.Name -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
                $null = New-Item $k -Force
                Set-ItemProperty $k -Name fPromptForPassword -Value 1 -Type DWord
            }
            Restart-LabRdpService -Name $device.Name
            Step $p nothing {
                PasswordPrompt 'Do nothing'
                $p.nothingAuditBefore = PasswordCopiedCount
                $p.marker = "rdp11 $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $p.marker
                Menu 'Connect'
                $p.nothingDialog = CredDialog
                Start-Sleep -Seconds 6
                $p.nothingToast = Toast 5
                $p.nothingClip = Clip
                Shot 'nothing'
                Key 1B
                Start-Sleep -Seconds 3
                CloseAll
                $p.nothingAuditAfter = PasswordCopiedCount
            }
            Step $p clearnow {
                PasswordPrompt 'Copy to clipboard*'
                Menu 'Connect'
                $p.clearDialog = CredDialog
                Start-Sleep -Seconds 2
                $p.clearToast = Toast 10
                $p.clearTitle = (Ui 'Stored password for*' -Title '*' -Value read -Optional -TimeoutSec 5).name
                $p.clearHint = Ui 'Press Ctrl+V in the password field, then OK.' -Title '*' -Value exists -Optional -TimeoutSec 5
                $p.clearButton = Ui 'Clear now' -Title '*' -Value exists -Optional -TimeoutSec 5
                $p.clipBefore = Clip
                Shot 'toast'
                $null = Ui 'Clear now' -Title '*' -TimeoutSec 10
                Start-Sleep -Seconds 1
                $p.clipAfter = Clip
                $p.toastAfterClear = Toast 2
            }
            Step $p dialogcloses {
                # the toast goes when Windows' dialog closes
                Key 1B
                Start-Sleep -Seconds 3
                $null = Ui 'Reconnect'
                $p.dlg2 = CredDialog
                $p.toast2 = Toast 10
                Key 1B
                Start-Sleep -Seconds 4
                $p.toast2After = Toast 2
            }
            Step $p clipchanges {
                # ... and when the clipboard changes
                $null = Ui 'Reconnect'
                $p.dlg3 = CredDialog
                $p.toast3 = Toast 10
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value 'something else'
                Start-Sleep -Seconds 4
                $p.toast3After = Toast 2
                Key 1B
                Start-Sleep -Seconds 3
                CloseAll
            }
            Invoke-LabGuest -Name $device.Name -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -Name fPromptForPassword -ErrorAction SilentlyContinue }
            # the next contexts connect without a prompt
            Restart-LabRdpService -Name $device.Name
            Save 'prompt' $p
        }

        It 'R-RDP-11 "When Windows asks for the password" = Do nothing: the clipboard untouched, no toast, no "Password copied" audit entry' -Tag 'R-RDP-11' {
            $p['nothing-error'] | Should -BeNullOrEmpty
            $p.nothingDialog | Should -BeTrue -Because 'Windows asks for the password (fPromptForPassword)'
            $p.nothingClip.sha256 | Should -Be (Sha $p.marker)
            $p.nothingToast.found | Should -BeFalse
            $p.nothingAuditAfter | Should -Be $p.nothingAuditBefore
        }

        It 'R-RDP-10 the toast ''Stored password for "[name]" copied'' / "Press Ctrl+V in the password field, then OK." with Clear now (clipboard emptied at once); it closes when Windows'' dialog closes or the clipboard changes' -Tag 'R-RDP-10' {
            $p['clearnow-error'] | Should -BeNullOrEmpty
            $p['dialogcloses-error'] | Should -BeNullOrEmpty
            $p['clipchanges-error'] | Should -BeNullOrEmpty
            $p.clearDialog | Should -BeTrue
            $p.clearToast.found | Should -BeTrue
            $p.clearTitle | Should -Be 'Stored password for "Device" copied'
            $p.clearHint.found | Should -BeTrue
            $p.clearButton.found | Should -BeTrue
            $p.clipBefore.sha256 | Should -Be (Sha $password)
            $p.clipAfter.length | Should -Be 0 -Because 'Clear now empties the clipboard at once'
            $p.toastAfterClear.found | Should -BeFalse
            $p.toast2.found | Should -BeTrue
            $p.toast2After.found | Should -BeFalse -Because 'Windows'' dialog was closed'
            $p.toast3.found | Should -BeTrue
            $p.toast3After.found | Should -BeFalse -Because 'the clipboard changed'
        }
    }

    Context 'the switch hotkey inside the RDP tab' {
        BeforeAll {
            $s = [ordered]@{}
            Step $s setup {
                CloseAll
                Clear-DeviceSessions
                New-LabClientConnection -Name $VmName -Display 'Device2' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
                EditConnection -Node 'Device2' {
                    $null = Ui 'Security & session' -Title 'Edit connection'
                    $null = Ui 'Server authentication' -Title 'Edit connection' -Value 'choose:Connect without warning'
                }
                # a third tab that never connects (nothing answers on that TEST-NET address): next and previous differ
                New-LabClientConnection -Name $VmName -Display 'Ghost' -TargetHost '192.0.2.1'
                # tabs in this order: Device, Device2, Ghost
                Menu 'Connect'
                $s.device = Wait-RdpTab 90
                Menu 'Connect' -Node 'Device2'
                $s.device2 = Wait-RdpTab 90
                Menu 'Connect' -Node 'Ghost'
                Start-Sleep -Seconds 3
                $s.sessions = Wait-DeviceRdp 2; $s.quser = $script:quser
            }
            Step $s next {
                # back on Device's tab, a click into its session, then Ctrl+Alt+< (left Alt; VK_OEM_102 = the "<" key).
                # As scan codes (Ctrl 1D, left Alt 38, "<" 56): Hyper-V's PressKey rejects VK_OEM_102 (226) with 32773
                # "Invalid parameter" (run 20261016-212514) - its key table is the US 101-key one without that key.
                Menu 'Connect'
                $null = Wait-RdpTab 30
                $s.before = SelectedTab
                ClickIntoSession
                Send-LabScancodes -Name $VmName -Codes 0x1D, 0x38, 0x56
                Start-Sleep -Seconds 3
                $s.afterNext = SelectedTab
                Shot 'switch-next'
            }
            Step $s previous {
                # in Device2's session: Ctrl+Alt+Shift+< goes back to Device (next would be Ghost)
                ClickIntoSession
                Send-LabScancodes -Name $VmName -Codes 0x1D, 0x38, 0x2A, 0x56
                Start-Sleep -Seconds 3
                $s.afterPrevious = SelectedTab
                Shot 'switch-previous'
            }
            Step $s fullscreen {
                # from full screen: it is left first, the next RDP tab goes full screen
                $null = Ui 'Full screen'
                Start-Sleep -Seconds 3
                $s.fsBefore = TopWindows
                Send-LabScancodes -Name $VmName -Codes 0x1D, 0x38, 0x56
                Start-Sleep -Seconds 4
                $s.fsAfter = TopWindows
                $s.fsSelected = SelectedTab
                Shot 'switch-fullscreen'
                # Ctrl+Alt+Break leaves full screen (Pause with Ctrl held sends the extended scan code E0 46 = Break)
                Send-LabScancodes -Name $VmName -Codes 0x1D, 0x38, 0xE046
                Start-Sleep -Seconds 3
            }
            Save 'switch' $s
        }

        It 'R-RDP-12 inside the RDP tab Ctrl+Alt+< selects the next tab, Ctrl+Alt+Shift+< the previous one; from full screen the next RDP tab goes full screen' -Tag 'R-RDP-12' {
            foreach ($k in 'setup', 'next', 'previous', 'fullscreen') { $s["$k-error"] | Should -BeNullOrEmpty -Because "the steps of '$k' ran" }
            $s.device | Should -BeTrue
            $s.device2 | Should -BeTrue
            @($s.sessions).Count | Should -Be 2 -Because "Device and Device2 each have their own session (device: $(@($s.quser) -join ' / '))"
            $s.before | Should -Be 'Device'
            $s.afterNext | Should -Be 'Device2'
            $s.afterPrevious | Should -Be 'Device'
            @($s.fsBefore | Where-Object { $_ -eq 'Device' }).Count | Should -BeGreaterThan 0 -Because 'Device was in full screen'
            @($s.fsAfter | Where-Object { $_ -eq 'Device' }).Count | Should -Be 0 -Because 'full screen is left first'
            @($s.fsAfter | Where-Object { $_ -eq 'Device2' }).Count | Should -BeGreaterThan 0 -Because 'the next RDP tab goes full screen'
            $s.fsSelected | Should -Be 'Device2'
            # AltGr+< typing "|" in the session needs a de-DE layout - the lab's VMs are en-US
        }
    }

    Context 'an additional RDP session' {
        BeforeAll {
            $a = [ordered]@{}
            Step $a tree {
                CloseAll
                Clear-DeviceSessions
                Menu 'Connect'
                $a.first = Wait-RdpTab 90
                $a.tabs1 = Wait-RdpTabCount 1 30
                $a.sessions1 = Wait-DeviceRdp 1
                # with an RDP tab open the tree offers "Open additional session"
                Menu 'Open additional session'
                $a.tabs2 = Wait-RdpTabCount 2 60
                $a.second = Wait-RdpTab 90
                $a.sessions2 = Wait-DeviceRdp 2; $a.quser2 = $script:quser
                Shot 'additional-tree'
            }
            Step $a tab {
                CloseAll
                Clear-DeviceSessions
                Menu 'Connect'
                $null = Wait-RdpTab 90
                $a.tabTabs1 = Wait-RdpTabCount 1 30
                $null = Wait-DeviceRdp 1
                # the tab bar's "+" (Open another tab for this connection): "Additional remote desktop session"
                $null = Ui 'Open another tab for this connection' -Value 'pick:Additional remote desktop session'
                $a.tabTabs2 = Wait-RdpTabCount 2 60
                $a.tabSecond = Wait-RdpTab 90
                $a.tabSessions2 = Wait-DeviceRdp 2; $a.tabQuser2 = $script:quser
                Shot 'additional-tab'
                CloseAll
            }
            Save 'additional' $a
        }

        It 'R-RDP-13 with an RDP tab open: "Open additional session" (tree) and "Additional remote desktop session" (tab bar) - a second tab and a second session on the device' -Tag 'R-RDP-13' {
            $a['tree-error'] | Should -BeNullOrEmpty
            $a['tab-error'] | Should -BeNullOrEmpty
            $a.first | Should -BeTrue
            @($a.sessions1).Count | Should -Be 1
            $a.tabs1 | Should -Be 1
            $a.tabs2 | Should -Be 2
            @($a.sessions2).Count | Should -Be 2 -Because "the device: $(@($a.quser2) -join ' / ')"
            $a.tabTabs1 | Should -Be 1
            $a.tabTabs2 | Should -Be 2
            @($a.tabSessions2).Count | Should -Be 2 -Because "the device: $(@($a.tabQuser2) -join ' / ')"
        }
    }
}
