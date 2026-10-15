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
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = (Get-LabVm $VmName).Os -ne 'Server'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    $device = @(Get-LabVm | Where-Object Os -eq 'Server')[0]
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
    # tabs of the selected connection (the tab bar shows each one's kind)
    function RdpTabCount { @(@((Ui '*' -Value list -Optional -TimeoutSec 10).items) | Where-Object { $_ -match '^Text\|Remote desktop\|' }).Count }
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
    # Windows' credential dialog of the RDP client ("Windows Security"; the newer one is a "Credential Dialog Xaml Host")
    function CredDialog([int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            foreach ($t in 'Windows Security', 'Credential Dialog Xaml Host') {
                if ((Ui '*' -Title $t -Value exists -Optional -TimeoutSec 2).found) { return $true }
            }
        } while ((Get-Date) -lt $until)
        $false
    }
    # RemoteHub's password notice (ClipboardToast): a window of its own without a title
    function Toast([int]$TimeoutSec = 3) { Ui 'Stored password for*' -Title '*' -Value exists -Optional -TimeoutSec $TimeoutSec }
    function Clip { Invoke-LabPerson -Name $VmName -Account Console -Do clip-get }

    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $device.Name
        Invoke-LabGuest -Name $device.Name -ScriptBlock {
            $ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
            Set-ItemProperty $ts -Name fDenyTSConnections -Value 0
            # two sessions of the same account at once (an additional session must not take over the first one)
            Set-ItemProperty $ts -Name fSingleSessionPerUser -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        Allow-NtlmDelegation
        $null = Install-LabClient -Name $VmName
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
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
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
            Save 'prompt' $p
        }

        It 'R-RDP-11 "When Windows asks for the password" = Do nothing: the clipboard untouched, no toast, no "Password copied" audit entry' -Tag 'R-RDP-11' {
            $p['nothing-error'] | Should -BeNullOrEmpty
            $p.nothingDialog | Should -BeTrue -Because 'Windows asks for the password (fPromptForPassword)'
            $p.nothingClip.sha256 | Should -Be (Sha $p.marker)
            $p.nothingToast.found | Should -BeFalse
            $p.nothingAuditAfter | Should -Be $p.nothingAuditBefore
        }

        It 'R-RDP-10 the toast ''Stored password for "<name>" copied'' / "Press Ctrl+V in the password field, then OK." with Clear now (clipboard emptied at once); it closes when Windows'' dialog closes or the clipboard changes' -Tag 'R-RDP-10' {
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
                $s.sessions = DeviceRdp
            }
            Step $s next {
                # back on Device's tab, a click into its session, then Ctrl+Alt+< (left Alt; VK_OEM_102 = the "<" key)
                Menu 'Connect'
                $null = Wait-RdpTab 30
                $s.before = SelectedTab
                ClickIntoSession
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x12, 0xE2
                Start-Sleep -Seconds 3
                $s.afterNext = SelectedTab
                Shot 'switch-next'
            }
            Step $s previous {
                # in Device2's session: Ctrl+Alt+Shift+< goes back to Device (next would be Ghost)
                ClickIntoSession
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x12, 0x10, 0xE2
                Start-Sleep -Seconds 3
                $s.afterPrevious = SelectedTab
                Shot 'switch-previous'
            }
            Step $s fullscreen {
                # from full screen: it is left first, the next RDP tab goes full screen
                $null = Ui 'Full screen'
                Start-Sleep -Seconds 3
                $s.fsBefore = TopWindows
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x12, 0xE2
                Start-Sleep -Seconds 4
                $s.fsAfter = TopWindows
                $s.fsSelected = SelectedTab
                Shot 'switch-fullscreen'
                # Ctrl+Alt+Break leaves full screen (Break = VK_CANCEL with Ctrl held)
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x12, 0x03
                Start-Sleep -Seconds 3
            }
            Save 'switch' $s
        }

        It 'R-RDP-12 inside the RDP tab Ctrl+Alt+< selects the next tab, Ctrl+Alt+Shift+< the previous one; from full screen the next RDP tab goes full screen' -Tag 'R-RDP-12' {
            foreach ($k in 'setup', 'next', 'previous', 'fullscreen') { $s["$k-error"] | Should -BeNullOrEmpty -Because "the steps of '$k' ran" }
            $s.device | Should -BeTrue
            $s.device2 | Should -BeTrue
            @($s.sessions).Count | Should -Be 2 -Because 'Device and Device2 each have their own session'
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
                Start-Sleep -Seconds 3
                $a.tabs1 = RdpTabCount
                $a.sessions1 = DeviceRdp
                # with an RDP tab open the tree offers "Open additional session"
                Menu 'Open additional session'
                Start-Sleep -Seconds 5
                $a.second = Wait-RdpTab 90
                Start-Sleep -Seconds 5
                $a.tabs2 = RdpTabCount
                $a.sessions2 = DeviceRdp
                Shot 'additional-tree'
            }
            Step $a tab {
                CloseAll
                Clear-DeviceSessions
                Menu 'Connect'
                $null = Wait-RdpTab 90
                Start-Sleep -Seconds 3
                $a.tabTabs1 = RdpTabCount
                # the tab bar's "+" (Open another tab for this connection): "Additional remote desktop session"
                $null = Ui 'Open another tab for this connection' -Value 'pick:Additional remote desktop session'
                Start-Sleep -Seconds 5
                $a.tabSecond = Wait-RdpTab 90
                Start-Sleep -Seconds 5
                $a.tabTabs2 = RdpTabCount
                $a.tabSessions2 = DeviceRdp
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
            $a.tabs2 | Should -Be ($a.tabs1 + 1)
            @($a.sessions2).Count | Should -Be 2
            $a.tabTabs2 | Should -Be ($a.tabTabs1 + 1)
            @($a.tabSessions2).Count | Should -Be 2
        }
    }
}
