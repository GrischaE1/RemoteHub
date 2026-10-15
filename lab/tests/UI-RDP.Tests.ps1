# R-RDP / UI-CP (RDP) / UI-SAS (RDP) / UI-NET-02 / A-CLIP / J-11 / J-12 / UI-JUMP-01 - RemoteHub's RDP tabs on the
# admin's PC (this client VM, the console account's session, driven by UI Automation) to the device (the Server VM):
# saved credentials, the NTLM policy message, the password prompt helper, redirection (clipboard text / files / image,
# drives), dynamic resolution, Ctrl+Alt+Del and the lock screen, full screen, detach / dock, .rdp export / import (also
# as normaluser), single instance, auto-reconnect after a network cut, the agent installed through the clipboard into
# the RDP session, and the jump host (connections moved under it, an RDP tab through it, an older RemoteHub opening the
# database). The RDP control is an ActiveX host without UI Automation inside: what happens in the session is checked on
# the device (quser, LogonUI / explorer per session, code in PSAdmin's RDP session) and by OCR of the client's screen.
# Client VMs only (2VM + the jump host VM). See docs/test-lab-cases.md sections 10.4, 10.5, 9.13.
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
    $script:jump = $null
    $script:rdpFile = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-rdp-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-rdp-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional, [string]$Account = 'Console') {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional -Account $Account
    }
    # an entry of a connection's context menu (right-click and the entry in one step; "A>B" = submenu: the tree menu
    # groups its entries in "Tools", "RemoteHub Agent" and "More" - MainWindow.xaml CmTools / CmManage / CmMore)
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) { $null = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec }
    # the text of a message box (its window name is the message text), closed with -Button
    function Message([string]$Title = '?*', [string]$Button = 'OK', [int]$TimeoutSec = 120) {
        $m = Ui '*' -Title $Title -Value list -TimeoutSec $TimeoutSec
        $null = Ui $Button -Title $Title -Optional
        ($m.items -join "`n")
    }
    # a key at the client's console keyboard (hex virtual-key code: 1B Esc, 0D Enter)
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    # OCR of the client's whole screen (in the account's session)
    function Screen([string]$Account = 'Console') { try { "$((Invoke-LabPerson -Name $VmName -Account $Account -Do screen-read -TimeoutSec 30).text)" } catch { "OCR failed: $_" } }
    # the names of the top-level windows in the account's session (a search for a window that isn't there answers them)
    function TopWindows([string]$Account = 'Console') { @((Invoke-LabPerson -Name $VmName -Account $Account -Do uia -Title 'zz-no-such-window' -Text 'zz' -Value exists -TimeoutSec 1).windows) }
    # each section on its own: a failing step is written down (error, screenshot, open dialogs) and the next one still
    # runs; dot-sourced, so the result dictionary of the context is filled
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
        }
    }
    # code in an interactive session (Invoke-LabInSession); placeholders __NAME__ filled from -Values; a line "JSON:..."
    # in the output is the answer
    function InSession([string]$Vm, [string]$Account, [string]$Code, [hashtable]$Values = @{}, [switch]$Elevated, [int]$TimeoutSec = 120) {
        foreach ($k in $Values.Keys) { $Code = $Code.Replace("__$($k)__", [string]$Values[$k]) }
        $raw = "$(Invoke-LabInSession -Name $Vm -Account $Account -Elevated:$Elevated -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($Code)))"
        if ($raw -match '(?m)^JSON:(.*)$') { return ($Matches[1].Trim() | ConvertFrom-Json) }
        if ($raw.Trim() -like 'ERROR:*') { throw "$Vm / ${Account}: $($raw.Trim())" }
        $raw.Trim()
    }
    function B64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
    function Sha([string]$s) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s))).Replace('-', '') }

    # The RDP tab is connected: its header says "Click into the session to type" or "Keyboard -> remote session"
    # (SessionView.UpdateFrame) - the status text itself is only in a tooltip.
    function Wait-RdpTab([int]$TimeoutSec = 90, [string]$Account = 'Console') {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = Invoke-LabUi -Name $VmName -Account $Account -Text '*' -Value list -Optional -TimeoutSec 5
            if (@(@($l.items) | Where-Object { $_ -match '\|(Click into the session to type|Keyboard\W+remote session)\|' }).Count -gt 0) { return $true }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $false
    }
    # the device: sessions (quser), PSAdmin's active ones, the sessions showing LogonUI (lock / security screen) and
    # those with PSAdmin's explorer
    function DeviceState {
        Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
            param($u)
            $s = @(quser 2>$null | Select-Object -Skip 1 | ForEach-Object {
                    if ($_ -match '^\s*>?(?<u>\S+)\s+(?:(?<n>\S+)\s+)?(?<id>\d+)\s+(?<s>Active|Disc)') { [pscustomobject]@{ User = $Matches.u; Name = $Matches.n; Id = [int]$Matches.id; State = $Matches.s } }
                })
            $mine = @($s | Where-Object { $_.User -eq $u })
            [pscustomobject]@{
                Sessions = $s
                Active   = @($mine | Where-Object State -eq 'Active' | ForEach-Object Id)
                Rdp      = @($mine | Where-Object { $_.State -eq 'Active' -and "$($_.Name)" -like 'rdp-tcp*' } | ForEach-Object Id)
                LogonUi  = @(Get-Process LogonUI -ErrorAction SilentlyContinue | ForEach-Object SessionId)
                Explorer = @(Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { "$($_.UserName)" -like "*\$u" } | ForEach-Object SessionId)
            }
        }
    }
    # PSAdmin signed out on the device, so a session afterwards is a new one
    function Clear-DeviceSessions {
        $null = Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
            param($u)
            foreach ($l in @(quser 2>$null | Select-Object -Skip 1)) {
                if ($l -match '^\s*>?(?<u>\S+)\s+(?:(?<n>\S+)\s+)?(?<id>\d+)\s+(?:Active|Disc)' -and $Matches.u -eq $u) { logoff $Matches.id }
            }
        }
        Start-Sleep -Seconds 5
    }
    # "Allow delegating saved / fresh credentials with NTLM-only server authentication" for TERMSRV/* on this PC: the
    # lab isn't a domain, so the RDP client sends saved credentials only with it (R-RDP-01 precondition)
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
    # the connection's audit log window (Audit log...), its rows as text
    function AuditText([string]$Node = 'Device') {
        Menu 'More>Audit log*' -Node $Node
        $l = Ui '*' -Title 'Audit log*' -Value list -TimeoutSec 30
        $null = Ui 'Close' -Title 'Audit log*' -Optional -TimeoutSec 5
        (@($l.items) -join "`n")
    }
    # RemoteHub's staging folders for agent installs (%TEMP%\RemoteHub-Agent\<12 hex>) of the console account
    function Staged {
        @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($u) @(Get-ChildItem "C:\Users\$u\AppData\Local\Temp\RemoteHub-Agent" -Directory -ErrorAction SilentlyContinue | Where-Object Name -match '^[0-9a-f]{12}$' | ForEach-Object Name)
            })
    }
    # Install RemoteHub Agent... with "Copy the installer and the enrollment file to the clipboard" (Update... once
    # installed): the dialog's own install path that needs no WinRM
    function Copy-AgentToClipboard([string]$Node) {
        Menu 'RemoteHub Agent>[IU]* RemoteHub Agent*' -Node $Node
        $t = '*RemoteHub Agent'
        $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title $t -Value check -TimeoutSec 60
        $b = Ui 'Install' -Title $t -Optional -TimeoutSec 3
        if (-not $b.found) { $null = Ui 'Update' -Title $t }
    }
    # The agent paired with RemoteHub on a VM without WinRM: RemoteHub's clipboard install, the two staged files taken
    # from this PC's clipboard to the VM over PowerShell Direct and msiexec run there (what the admin does by hand).
    # RemoteHub's WaitForManualInstall then marks the connection installed.
    function Install-AgentByHand([string]$Node, [string]$TargetVm) {
        $o = [ordered]@{}
        Copy-AgentToClipboard $Node
        $null = Ui 'Done' -Title '*RemoteHub Agent' -Value exists -TimeoutSec 60
        $o.report = (@((Ui '*' -Title '*RemoteHub Agent' -Value list).items) -join "`n")
        $null = Ui 'Done' -Title '*RemoteHub Agent' -Optional
        $files = @(InSession $VmName Console $code.clipFiles)
        $o.files = @($files | ForEach-Object { Split-Path $_ -Leaf })
        # the .rhagent holds the device key: kept out of the evidence folder
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "rhlab-agent-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        try {
            Get-LabFile -Name $VmName -Path $files -Destination $tmp
            Install-LabSigningCertificate -Name $TargetVm
            Copy-LabFile -Name $TargetVm -Path @(Get-ChildItem $tmp -File | ForEach-Object FullName) -Destination 'C:\LabAgentByHand'
        }
        finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
        $o.exit = Invoke-LabGuest -Name $TargetVm -ScriptBlock {
            $msi = Get-ChildItem 'C:\LabAgentByHand' -Filter 'RemoteHub-Agent*.msi' | Select-Object -First 1
            (Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/norestart', '/l*v', 'C:\LabAgentByHand\install.log' -Wait -PassThru).ExitCode
        }
        # RemoteHub asks the agent every 10 s
        Start-Sleep -Seconds 25
        $o
    }

    $code = @{}
    $code.textSet = @'
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Clipboard]::SetText([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__B64__')))
'JSON:{"set":true}'
'@
    # waits until the clipboard holds the wanted text (or 30 s): what arrived, as base64
    $code.textGet = @'
Add-Type -AssemblyName System.Windows.Forms
$want = '__B64__'; $got = ''
$until = (Get-Date).AddSeconds(30)
do {
    try { $got = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string][System.Windows.Forms.Clipboard]::GetText())) } catch { }
    if ($got -ne $want) { Start-Sleep -Milliseconds 700 }
} while ($got -ne $want -and (Get-Date) -lt $until)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ b64 = $got })
'@
    # the files of a folder onto the clipboard (Explorer's "Copy"), with their hashes
    $code.filesCopy = @'
Add-Type -AssemblyName System.Windows.Forms
$c = New-Object System.Collections.Specialized.StringCollection
$f = @(Get-ChildItem '__DIR__' -File)
foreach ($x in $f) { [void]$c.Add($x.FullName) }
[System.Windows.Forms.Clipboard]::SetFileDropList($c)
'JSON:' + (ConvertTo-Json -Compress -InputObject @($f | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }))
'@
    # Explorer's "Paste" into an empty folder (the shell handles the RDP clipboard's file streams), until __COUNT__
    # files are there and their size stays the same
    $code.paste = @'
$d = '__DIR__'
if (Test-Path $d) { Remove-Item $d -Recurse -Force }
$null = New-Item -ItemType Directory -Path $d -Force
$sh = New-Object -ComObject Shell.Application
$sh.NameSpace($d).Self.InvokeVerb('Paste')
$until = (Get-Date).AddSeconds(240); $last = -1; $stable = $false
do {
    Start-Sleep -Seconds 2
    $files = @(Get-ChildItem $d -File -ErrorAction SilentlyContinue)
    $size = [long]($files | Measure-Object Length -Sum).Sum
    $stable = $files.Count -ge __COUNT__ -and $size -eq $last
    $last = $size
} while (-not $stable -and (Get-Date) -lt $until)
'JSON:' + (ConvertTo-Json -Compress -InputObject @($files | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }))
'@
    $code.clipFiles = @'
Add-Type -AssemblyName System.Windows.Forms
'JSON:' + (ConvertTo-Json -Compress -InputObject @([System.Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { [string]$_ }))
'@
    # the password prompt helper's clipboard: hash of the text, formats, the two "keep it out of history / cloud" values
    $code.clipInfo = @'
Add-Type -AssemblyName System.Windows.Forms
$o = [System.Windows.Forms.Clipboard]::GetDataObject()
$t = [string][System.Windows.Forms.Clipboard]::GetText()
function Dw([string]$n) { try { $d = $o.GetData($n) } catch { return $null }; if ($d -is [IO.MemoryStream]) { $a = $d.ToArray(); if ($a.Length -ge 4) { return [BitConverter]::ToInt32($a, 0) } }; $null }
$sha = if ($t) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($t))).Replace('-', '') } else { '' }
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ length = $t.Length; sha256 = $sha; formats = @(if ($o) { $o.GetFormats() }); history = (Dw 'CanIncludeInClipboardHistory'); cloud = (Dw 'CanUploadToCloudClipboard') })
'@
    # a test picture (96 x 64, pattern by __SEED__) and the hash of a picture's pixels (RGB)
    $imgLib = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function New-TestImage([int]$Seed) {
    $b = New-Object System.Drawing.Bitmap 96, 64, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    for ($y = 0; $y -lt 64; $y++) { for ($x = 0; $x -lt 96; $x++) { $b.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(($x * 7 + $y * 3 + $Seed) % 256, ($x * 2 + $y * 11 + $Seed) % 256, ($x * 13 + $y * 5 + $Seed) % 256)) } }
    $b
}
function Get-PixelHash($img) {
    if (-not $img) { return '' }
    $b = New-Object System.Drawing.Bitmap $img
    $sb = New-Object System.Text.StringBuilder
    for ($y = 0; $y -lt $b.Height; $y++) { for ($x = 0; $x -lt $b.Width; $x++) { $c = $b.GetPixel($x, $y); [void]$sb.Append($c.R).Append(',').Append($c.G).Append(',').Append($c.B).Append(';') } }
    "$($b.Width)x$($b.Height)-" + [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($sb.ToString()))).Replace('-', '')
}
'@
    $code.imageSet = $imgLib + @'
$i = New-TestImage __SEED__
[System.Windows.Forms.Clipboard]::SetImage($i)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ hash = (Get-PixelHash $i) })
'@
    $code.imageGet = $imgLib + @'
$want = '__WANT__'; $got = ''
$until = (Get-Date).AddSeconds(30)
do {
    try { $got = Get-PixelHash ([System.Windows.Forms.Clipboard]::GetImage()) } catch { }
    if ($got -ne $want) { Start-Sleep -Milliseconds 700 }
} while ($got -ne $want -and (Get-Date) -lt $until)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ hash = $got })
'@
    # \\tsclient\C in the RDP session (it shows up a little after the sign-in when drives are redirected)
    $code.tsclient = @'
$seen = $false
$until = (Get-Date).AddSeconds(__WAIT__)
do { $seen = Test-Path '\\tsclient\C'; if (-not $seen) { Start-Sleep -Seconds 2 } } while (-not $seen -and (Get-Date) -lt $until)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ present = $seen })
'@
    $code.resolution = @'
Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ w = $b.Width; h = $b.Height })
'@
    # RemoteHub's main window to a size (as a person drags its border)
    $code.resize = @'
Add-Type -Namespace RhLab -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
$h = (Get-Process RemoteHub | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1).MainWindowHandle
[void][RhLab.Win]::ShowWindow($h, 9)
$ok = [RhLab.Win]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, __W__, __H__, 0x14)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ resized = $ok })
'@
    $code.msiInSession = @'
$d = '__DIR__'
$msi = Get-ChildItem $d -Filter 'RemoteHub-Agent*.msi' | Select-Object -First 1
$p = Start-Process msiexec.exe -ArgumentList '/i', ('"' + $msi.FullName + '"'), '/qn', '/norestart', '/l*v', ('"' + (Join-Path $d 'install.log') + '"') -PassThru
$null = $p.Handle; $p.WaitForExit()
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ exit = $p.ExitCode; left = @(Get-ChildItem $d -Filter *.rhagent | ForEach-Object Name) })
'@
    $code.closeMain = @'
$p = @(Get-Process RemoteHub -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0)
foreach ($x in $p) { [void]$x.CloseMainWindow() }
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ closed = $p.Count })
'@

    # Unicode, multi-line (as UI-CP-01): "Zeile 1 aou-umlauts / Line 2 check mark, two CJK characters"
    $text = 'Zeile 1 ' + [char]0xE4 + [char]0xF6 + [char]0xFC + "`r`nLine 2 " + [char]0x2713 + ' ' + [char]0x65E5 + [char]0x672C
    $cpOut = 'C:\ProgramData\RemoteHubLab\cp-out'
    $cpIn = 'C:\ProgramData\RemoteHubLab\session\cp-in'

    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $device.Name
        $computer = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            # Remote Desktop on (the firewall group by its resource name - display names are localized)
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            $env:COMPUTERNAME
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        $null = Install-LabClient -Name $VmName
        $script:task = Start-LabClient -Name $VmName
        # credentials for this item, "Local account on the target computer (.\user)" ticked: .\PSAdmin
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
    }
}

AfterAll {
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        try { Stop-LabJumpHost } catch { }
        try { Restore-LabVmNetwork -Name $device.Name } catch { }
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'ui-rdp-device-logs') } catch { }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-RDP <VmName>' -Tag Client, UI, 'R-RDP', 'UI-CP', 'UI-SAS', 'UI-NET', 'A-CLIP', 'J', 'UI-JUMP' -Skip:(-not $isClient) {

    Context 'saved credentials without the NTLM delegation policy' {
        BeforeAll {
            $n = [ordered]@{}
            Step $n connect {
                Clear-DeviceSessions
                Menu 'Connect'
                Start-Sleep -Seconds 25
                # the tab's overlay (Windows' own text, RdpSession.OnDisconnected) and Windows' credential dialog, if any
                $n.overlay = @((Ui '*' -Value list).items)
                $n.prompt = @((Ui '*' -Title 'Windows Security' -Value list -Optional -TimeoutSec 5).items)
                $n.screen = Screen
                Shot 'ntlm'
                $n.device = DeviceState
            }
            foreach ($i in 1..2) { Key 1B; Start-Sleep -Seconds 1 }
            $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            Save 'ntlm' $n
        }

        It 'R-RDP-03 non-domain device, no "Allow delegating saved credentials with NTLM-only" policy: Windows'' message about saved credentials' -Tag 'R-RDP-03' {
            "$(@($n.overlay) -join "`n")`n$(@($n.prompt) -join "`n")`n$($n.screen)" | Should -Match 'saved credentials'
        }
    }

    Context 'the RDP tab with saved credentials' {
        BeforeAll {
            $c = [ordered]@{}
            Allow-NtlmDelegation
            Clear-DeviceSessions
            Step $c connect {
                Menu 'Connect'
                $c.connected = Wait-RdpTab 90
                Start-Sleep -Seconds 5
                $c.device = DeviceState
                $c.session = @($c.device.Rdp)[0]
                $c.whoami = InSession $device.Name Admin 'whoami'
                Shot 'connected'
            }
            Step $c resolution {
                $c.res1 = InSession $device.Name Admin $code.resolution
                $c.resize = InSession $VmName Console $code.resize @{ W = 860; H = 640 }
                Start-Sleep -Seconds 8
                $c.res2 = InSession $device.Name Admin $code.resolution
                Shot 'resized'
            }
            Step $c focus {
                # back into the screen (and its size), then a click into the session with the console mouse
                $fit = Ui 'Reconnect' -Value fit
                $rect = @("$($fit.value)" -split '[,;]' | ForEach-Object { [double]$_ })
                Start-Sleep -Seconds 4
                Send-LabMouse -Name $VmName -X ([int]($rect[0] + $rect[2] * 0.65)) -Y ([int]($rect[1] + $rect[3] * 0.6))
                Start-Sleep -Seconds 2
                $c.focus = @(@((Ui '*' -Value list).items) | Where-Object { $_ -match 'Keyboard\W+remote session|Click into the session to type' })
                Shot 'focus'
            }
            Step $c sas {
                $null = Ui 'Send Ctrl+Alt+Del to the session'
                Start-Sleep -Seconds 4
                $c.sasState = DeviceState
                $c.sasScreen = Screen
                Shot 'sas'
                Key 1B
                Start-Sleep -Seconds 3
                $c.sasAfter = DeviceState
            }
            Step $c lock {
                Lock-LabSession -Name $device.Name -Account Admin
                Start-Sleep -Seconds 5
                $c.locked = DeviceState
                $null = Ui 'Send Ctrl+Alt+Del to the session'
                Start-Sleep -Seconds 4
                Shot 'lock-password'
                # typed at the client's keyboard: the RDP control has the focus after Ctrl+Alt+Del
                Send-LabText -Name $VmName -Text $password
                Send-LabKey -Name $VmName -Key Enter
                Start-Sleep -Seconds 10
                $c.unlocked = DeviceState
                Shot 'unlocked'
            }
            Step $c fullscreen {
                $null = Ui 'Full screen'
                Start-Sleep -Seconds 2
                $c.fsWindows = TopWindows
                $c.fsScreen = Screen
                Shot 'fullscreen'
                # Ctrl+Alt+Break (Break = VK_CANCEL with Ctrl held)
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x12, 0x03
                Start-Sleep -Seconds 3
                $c.fsAfter = TopWindows
                $c.fsConnected = Wait-RdpTab 20
            }
            Step $c detach {
                $null = Ui 'Detach into its own window / dock back'
                Start-Sleep -Seconds 3
                $c.detWindows = TopWindows
                $c.detHint = (Ui 'This session runs in its own window.' -Value exists -Optional -TimeoutSec 5).found
                $c.detTools = @((Ui '*' -Title 'Device' -Value list -Optional -TimeoutSec 10).items)
                Shot 'detached'
                $null = Ui 'Dock' -Title 'Device'
                Start-Sleep -Seconds 3
                $c.dockWindows = TopWindows
                $c.dockHint = (Ui 'This session runs in its own window.' -Value exists -Optional -TimeoutSec 3).found
                $c.dockConnected = Wait-RdpTab 20
            }
            Save 'tab' $c
        }

        It 'R-RDP-01 Connect on Device (saved .\PSAdmin + password): the tab connects, the device has a new active RDP session for PSAdmin' -Tag 'R-RDP-01' {
            $c.connected | Should -BeTrue
            @($c.device.Rdp).Count | Should -BeGreaterThan 0
        }

        It 'R-RDP-02 local account .\PSAdmin: signed in as <DEVICE>\PSAdmin' -Tag 'R-RDP-02' {
            "$($c.whoami)" | Should -Be "$computer\$($device.AdminAccount)"
        }

        It 'R-RDP-06 dynamic resolution: the device session follows the resized window (even width, not larger than the window)' -Tag 'R-RDP-06' {
            $c.resize.resized | Should -BeTrue
            "$($c.res2.w)x$($c.res2.h)" | Should -Not -Be "$($c.res1.w)x$($c.res1.h)"
            $c.res2.w % 2 | Should -Be 0
            $c.res2.w | Should -BeLessOrEqual 860
            $c.res2.h | Should -BeLessOrEqual 640
        }

        It 'UI-SAS-06 "Send Ctrl+Alt+Del to the session": the RDP session shows its security screen in the tab' -Tag 'UI-SAS-06' {
            @($c.sasState.LogonUi) | Should -Contain $c.session
            $c.sasScreen | Should -Match 'Task Manager|Sign out|Lock|Change a password|Switch user'
            @($c.sasAfter.LogonUi) | Should -Not -Contain $c.session -Because 'Esc closes it again'
        }

        It 'UI-SAS-07 the RDP session locked: Ctrl+Alt+Del from the tab, the password typed through it unlocks the session' -Tag 'UI-SAS-07' {
            @($c.locked.LogonUi) | Should -Contain $c.session
            @($c.unlocked.LogonUi) | Should -Not -Contain $c.session
            @($c.unlocked.Explorer) | Should -Contain $c.session
        }

        It 'R-RDP-07 Ctrl+Alt+Del, full screen (the control''s own window, left with Ctrl+Alt+Break), detach into a window and dock back, the keyboard frame' -Tag 'R-RDP-07' {
            @($c.sasState.LogonUi) | Should -Contain $c.session
            # the full-screen window carries the connection's name (RdpSession.Configure: FullScreenTitle)
            @($c.fsWindows | Where-Object { $_ -eq 'Device' }).Count | Should -BeGreaterThan 0
            @($c.fsAfter | Where-Object { $_ -eq 'Device' }).Count | Should -Be 0
            $c.fsConnected | Should -BeTrue
            @($c.detWindows | Where-Object { $_ -eq 'Device' }).Count | Should -BeGreaterThan 0
            $c.detHint | Should -BeTrue
            $c.dockHint | Should -BeFalse
            @($c.dockWindows | Where-Object { $_ -eq 'Device' }).Count | Should -Be 0
            $c.dockConnected | Should -BeTrue
            (@($c.focus) -join "`n") | Should -Match 'Keyboard\W+remote session'
        }
    }

    Context 'the password prompt helper' {
        BeforeAll {
            $p = [ordered]@{}
            $p.passwordSha = Sha $password
            $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            Clear-DeviceSessions
            # "Always prompt for password upon connection" on the device
            Invoke-LabGuest -Name $device.Name -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
                $null = New-Item $k -Force
                Set-ItemProperty $k -Name fPromptForPassword -Value 1 -Type DWord
            }
            Step $p prompt {
                Menu 'Connect'
                $p.dialog = (Ui '*' -Title 'Windows Security' -Value exists -Optional -TimeoutSec 60).found
                Start-Sleep -Seconds 2
                # read first: the helper clears it after 30 s
                $p.clip1 = InSession $VmName Console $code.clipInfo
                # the notice at the bottom of RemoteHub's window (ClipboardToast: 'Stored password for "Device" copied')
                $p.toast = (Ui 'Stored password for*' -Title '*' -Value exists -Optional -TimeoutSec 10).found
                Shot 'prompt'
                Start-Sleep -Seconds 35
                $p.clip2 = InSession $VmName Console $code.clipInfo
            }
            Step $p paste {
                # cancel this prompt, connect again, paste into Windows' dialog, OK
                Key 1B
                Start-Sleep -Seconds 3
                $null = Ui 'Reconnect'
                $p.dialog2 = (Ui '*' -Title 'Windows Security' -Value exists -Optional -TimeoutSec 60).found
                Start-Sleep -Seconds 3
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x56
                Start-Sleep -Milliseconds 500
                Send-LabKey -Name $VmName -Key Enter
                $p.connected = Wait-RdpTab 60
                $p.clip3 = InSession $VmName Console $code.clipInfo
                Shot 'prompt-pasted'
            }
            Step $p audit { $p.audit = AuditText }
            Invoke-LabGuest -Name $device.Name -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -Name fPromptForPassword -ErrorAction SilentlyContinue }
            Save 'prompt' $p
        }

        It 'R-RDP-04 "Always prompt": the password on the clipboard (out of history and cloud), cleared after 30 s and after the login; audit "Password copied"' -Tag 'R-RDP-04' {
            $p.dialog | Should -BeTrue
            $p.clip1.sha256 | Should -Be $p.passwordSha
            $p.clip1.history | Should -Be 0
            $p.clip1.cloud | Should -Be 0
            $p.toast | Should -BeTrue
            $p.clip2.length | Should -Be 0
            $p.connected | Should -BeTrue
            $p.clip3.length | Should -Be 0
            $p.audit | Should -Match 'Password copied'
            $p.audit | Should -Match 'cleared after 30 s'
        }
    }

    Context 'clipboard and drive redirection' {
        BeforeAll {
            $x = [ordered]@{}
            if (-not (Wait-RdpTab 5)) { $null = Ui 'Reconnect' -Optional; $null = Wait-RdpTab 60 }
            $x.want = B64 $text
            $x.back = B64 ($text + ' back')
            foreach ($v in $VmName, $device.Name) {
                Invoke-LabGuest -Name $v -ArgumentList $cpOut -ScriptBlock {
                    param($d)
                    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
                    $null = New-Item -ItemType Directory -Path $d -Force
                    foreach ($i in 1..3) { $b = New-Object byte[] (40000 * $i); (New-Object Random).NextBytes($b); [IO.File]::WriteAllBytes("$d\$env:COMPUTERNAME-$i.bin", $b) }
                }
            }
            Step $x text {
                $null = InSession $VmName Console $code.textSet @{ B64 = $x.want }
                $x.atDevice = (InSession $device.Name Admin $code.textGet @{ B64 = $x.want }).b64
                $null = InSession $device.Name Admin $code.textSet @{ B64 = $x.back }
                $x.atClient = (InSession $VmName Console $code.textGet @{ B64 = $x.back }).b64
            }
            Step $x files {
                $x.filesOut = @(InSession $VmName Console $code.filesCopy @{ DIR = $cpOut })
                $x.filesIn = @(InSession $device.Name Admin $code.paste @{ DIR = $cpIn; COUNT = 3 } -TimeoutSec 300)
                $x.filesBack = @(InSession $device.Name Admin $code.filesCopy @{ DIR = $cpOut })
                $x.filesBackIn = @(InSession $VmName Console $code.paste @{ DIR = $cpIn; COUNT = 3 } -TimeoutSec 300)
            }
            Step $x image {
                $x.imgOut = (InSession $VmName Console $code.imageSet @{ SEED = 1 }).hash
                $x.imgIn = (InSession $device.Name Admin $code.imageGet @{ WANT = $x.imgOut }).hash
                $x.imgBack = (InSession $device.Name Admin $code.imageSet @{ SEED = 77 }).hash
                $x.imgBackIn = (InSession $VmName Console $code.imageGet @{ WANT = $x.imgBack }).hash
            }
            Step $x drives {
                $x.drivesOff = (InSession $device.Name Admin $code.tsclient @{ WAIT = 5 }).present
                Menu 'Edit*'
                $null = Ui 'Local resources & keyboard' -Title 'Edit connection'
                $null = Ui 'Local drives' -Title 'Edit connection' -Value 'choose:On'
                $null = Ui 'Save' -Title 'Edit connection'
                $null = Ui 'Reconnect'
                Start-Sleep -Seconds 8
                $x.reconnected = Wait-RdpTab 60
                $x.drivesOn = (InSession $device.Name Admin $code.tsclient @{ WAIT = 30 }).present
            }
            Shot 'redirection'
            Save 'redirection' $x
        }

        It 'UI-CP-09 text both ways through the RDP tab (Unicode, multi-line)' -Tag 'UI-CP-09' {
            $x.atDevice | Should -Be $x.want
            $x.atClient | Should -Be $x.back
        }

        It 'UI-CP-10 three files both ways through the RDP tab: hashes equal' -Tag 'UI-CP-10' {
            @($x.filesIn | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) | Should -Be @($x.filesOut | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" })
            @($x.filesBackIn | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) | Should -Be @($x.filesBack | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" })
            @($x.filesIn).Count | Should -Be 3
        }

        It 'UI-CP-11 a picture both ways through the RDP tab: pixels equal' -Tag 'UI-CP-11' {
            $x.imgOut | Should -Not -BeNullOrEmpty
            $x.imgIn | Should -Be $x.imgOut
            $x.imgBackIn | Should -Be $x.imgBack
        }

        It 'R-RDP-05 clipboard both ways; \\tsclient\C only with "Local drives" on' -Tag 'R-RDP-05' {
            $x.atDevice | Should -Be $x.want
            $x.atClient | Should -Be $x.back
            $x.drivesOff | Should -BeFalse
            $x.reconnected | Should -BeTrue
            $x.drivesOn | Should -BeTrue
        }
    }

    Context '.rdp export and a second start with the file' {
        BeforeAll {
            $e = [ordered]@{}
            Invoke-LabGuest -Name $VmName -ScriptBlock { $null = New-Item 'C:\lab' -ItemType Directory -Force; Remove-Item 'C:\lab\device.rdp' -ErrorAction SilentlyContinue }
            Step $e export {
                Menu 'More>Save as .rdp file*'
                Start-Sleep -Seconds 4
                Shot 'save-as'
                # Windows' Save As dialog has no usable UIA patterns here: its file name box has the focus with the
                # proposed name selected - typed over at the console keyboard
                Send-LabText -Name $VmName -Text 'C:\lab\device.rdp'
                Send-LabKey -Name $VmName -Key Enter
                $e.question = Message -Title 'Include saved passwords*' -Button 'Include passwords' -TimeoutSec 30
                Start-Sleep -Seconds 3
                $e.file = Invoke-LabGuest -Name $VmName -ScriptBlock { if (Test-Path 'C:\lab\device.rdp') { Get-Content 'C:\lab\device.rdp' -Raw } }
                # for R-RDP-08 (the file opened as normaluser, further down)
                $script:rdpFile = $e.file
            }
            Step $e single {
                $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
                Start-Sleep -Seconds 3
                Clear-DeviceSessions
                $second = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' 'C:\lab\device.rdp' }
                Start-Sleep -Seconds 15
                $e.processes = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count }
                $e.windows = @(TopWindows | Where-Object { $_ -like 'RemoteHub*' -or $_ -like '* RemoteHub' })
                $e.tree = @((Ui '*' -Value list).items)
                $e.connected = Wait-RdpTab 60
                Start-Sleep -Seconds 3
                $e.device = DeviceState
                Shot 'single-instance'
                Stop-LabInSession -Name $VmName -Task $second
            }
            Save 'export' $e
        }

        It 'R-RDP-09 RemoteHub.exe C:\lab\device.rdp while running: one process and window, no new entry, Device connects' -Tag 'R-RDP-09' {
            $e.processes | Should -Be 1
            @($e.windows).Count | Should -Be 1
            # an entry made from the file would be named after it: "device" (RdpFileParser.Parse)
            @($e.tree | Where-Object { $_ -cmatch '\|device\|' }).Count | Should -Be 0
            $e.connected | Should -BeTrue
            @($e.device.Rdp).Count | Should -BeGreaterThan 0
        }
    }

    Context 'the agent installed through the clipboard into the RDP session' {
        BeforeAll {
            $a = [ordered]@{}
            if (-not (Wait-RdpTab 5)) { Menu 'Connect'; $null = Wait-RdpTab 60 }
            Install-LabSigningCertificate -Name $device.Name
            Step $a copy {
                Menu 'RemoteHub Agent>Install RemoteHub Agent*'
                $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
                $null = Ui 'Install' -Title 'Install RemoteHub Agent'
                $null = Ui 'Done' -Title 'Install RemoteHub Agent' -Value exists -TimeoutSec 60
                $a.report = (@((Ui '*' -Title 'Install RemoteHub Agent' -Value list).items) -join "`n")
                $null = Ui 'Done' -Title 'Install RemoteHub Agent'
                $a.clipboard = @(InSession $VmName Console $code.clipFiles)
                $a.staged = Staged
            }
            Step $a paste {
                # in PSAdmin's RDP session: both files pasted into one folder, the MSI run as administrator there
                $a.pasted = @(InSession $device.Name Admin $code.paste @{ DIR = 'C:\ProgramData\RemoteHubLab\session\agent-clip'; COUNT = 2 } -TimeoutSec 400)
                $a.msi = InSession $device.Name Admin $code.msiInSession @{ DIR = 'C:\ProgramData\RemoteHubLab\session\agent-clip' } -Elevated -TimeoutSec 600
                $a.state = Get-LabAgentState -Name $device.Name
                # RemoteHub asks every 10 s (WaitForManualInstall) and writes the audit entry when the agent answers
                $until = (Get-Date).AddSeconds(150)
                do { Start-Sleep -Seconds 15; $a.audit = AuditText } while ($a.audit -notmatch 'answered as RemoteHub Agent' -and (Get-Date) -lt $until)
            }
            Step $a exit {
                $a.beforeExit = Staged
                $null = InSession $VmName Console $code.closeMain
                # sessions are open: "Disconnect and exit?"
                $null = Ui 'Exit' -Title '?*' -Optional -TimeoutSec 20
                $until = (Get-Date).AddSeconds(60)
                while ((Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count }) -gt 0 -and (Get-Date) -lt $until) { Start-Sleep -Seconds 2 }
                $a.afterExit = Staged
                Stop-LabInSession -Name $VmName -Task $script:task
                $script:task = Start-LabClient -Name $VmName
            }
            Step $a crash {
                Copy-AgentToClipboard 'Device'
                $null = Ui 'Done' -Title '*RemoteHub Agent' -Value exists -Optional -TimeoutSec 60
                $null = Ui 'Done' -Title '*RemoteHub Agent' -Optional -TimeoutSec 5
                $a.beforeKill = Staged
                Stop-LabClient -Name $VmName -Task $script:task
                $a.afterKill = Staged
                $script:task = Start-LabClient -Name $VmName
                # the start cleans in the background (App.OnStartup: AgentInstaller.CleanTemp(TimeSpan.Zero))
                Start-Sleep -Seconds 15
                $a.afterRestart = Staged
            }
            Shot 'agent-clip'
            Save 'agent-clip' $a
        }

        It 'A-CLIP-01 Install RemoteHub Agent, clipboard: "Copied ... to the clipboard", pasted into the RDP session and run there: installed on the first answer' -Tag 'A-CLIP-01' {
            $a.report | Should -Match 'Copied RemoteHub-Agent-x64\.msi and Device\.rhagent to the clipboard\.'
            @($a.clipboard | ForEach-Object { Split-Path $_ -Leaf }) | Should -Contain 'RemoteHub-Agent-x64.msi'
            @($a.pasted).Count | Should -Be 2
            "$($a.msi.exit)" | Should -BeIn '0', '3010'
            $a.state.Service.State | Should -Be 'Running'
            $a.audit | Should -Match 'Installed\W+answered as RemoteHub Agent'
        }

        It 'A-CLIP-02 the staging folder stays while RemoteHub runs, is gone after a normal exit and after the start that follows a crash' -Tag 'A-CLIP-02' {
            @($a.staged).Count | Should -BeGreaterThan 0
            @($a.afterExit).Count | Should -Be 0
            @($a.beforeKill).Count | Should -BeGreaterThan 0
            @($a.afterKill).Count | Should -BeGreaterThan 0 -Because 'a killed RemoteHub cleans nothing'
            @($a.afterRestart).Count | Should -Be 0
        }
    }

    Context 'asked before the device key goes through a desktop tab' {
        BeforeAll {
            $k = [ordered]@{}
            Step $k desktop {
                # with the agent installed, Connect opens the agent desktop; the person at the device allows it
                Menu 'Connect'
                $null = Ui 'Connect' -Optional -TimeoutSec 15
                $k.allow = Invoke-LabPerson -Name $device.Name -Account Console -Do click -Text 'Allow' -TimeoutSec 60
                $k.desktop = (Ui 'Send Ctrl+Alt+Del' -Value exists -TimeoutSec 60).found
            }
            Step $k ask {
                New-LabClientConnection -Name $VmName -Display 'DeviceA' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
                Menu 'RemoteHub Agent>Install RemoteHub Agent*' -Node 'DeviceA'
                $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
                $null = Ui 'Install' -Title 'Install RemoteHub Agent'
                # Dialogs.Confirm "Copy the device key?" - its window name is the message
                $k.question = Message -Title 'The remote desktop tab of*' -Button 'Cancel' -TimeoutSec 60
                Start-Sleep -Seconds 2
                $k.report = (@((Ui '*' -Title 'Install RemoteHub Agent' -Value list -TimeoutSec 20).items) -join "`n")
                $null = Ui 'Close' -Title 'Install RemoteHub Agent' -Optional -TimeoutSec 5
                $k.clipboard = @(InSession $VmName Console $code.clipFiles)
            }
            Shot 'ask-key'
            Save 'ask-key' $k
        }

        It 'A-CLIP-03 an agent desktop tab to Device is open: "Copy the device key?" names it and DeviceA.rhagent; Cancel copies nothing' -Tag 'A-CLIP-03' {
            $k.desktop | Should -BeTrue
            $k.question | Should -Match 'The remote desktop tab of Device sends everything copied on this PC'
            $k.question | Should -Match 'DeviceA\.rhagent'
            $k.question | Should -Match 'Close that tab first, or copy anyway\?'
            $k.report | Should -Match 'Cancelled\W+close the remote desktop tab, then try again\.'
            @($k.clipboard | Where-Object { $_ -like '*.rhagent' }) | Should -BeNullOrEmpty
        }
    }

    Context 'copied in an RDP tab, pasted through an agent tab' {
        BeforeAll {
            $u = [ordered]@{}
            $u.text = $text + ' across'
            Step $u across {
                # the RDP session next to the agent desktop of the same connection: PSAdmin's RDP session is "device A",
                # the console session (Admin) behind the agent desktop is "device B"
                Menu 'Remote desktop via RDP'
                $u.connected = Wait-RdpTab 90
                $null = InSession $device.Name Admin $code.textSet @{ B64 = (B64 $u.text) }
                Start-Sleep -Seconds 3
                $null = Ui 'Desktop (agent)' -Value realclick -Optional -TimeoutSec 10
                $until = (Get-Date).AddSeconds(40)
                do {
                    Start-Sleep -Seconds 3
                    $u.atB = Invoke-LabPerson -Name $device.Name -Account Console -Do clip-get
                } while ($u.atB.sha256 -ne (Sha $u.text) -and (Get-Date) -lt $until)
                Shot 'across'
            }
            Save 'across' $u
        }

        It 'UI-CP-12 a text copied in the RDP session arrives through the agent tab in the other session' -Tag 'UI-CP-12' {
            $u.connected | Should -BeTrue
            $u.atB.sha256 | Should -Be (Sha $u.text)
        }
    }

    Context 'RDP auto-reconnect after a network cut' {
        BeforeAll {
            $w = [ordered]@{}
            $logFile = "C:\Users\$($vm.ConsoleAccount)\AppData\Roaming\RemoteHub\logs\remotehub.log"
            function DiagLines { @(Invoke-LabGuest -Name $VmName -ArgumentList $logFile -ScriptBlock { param($f) if (Test-Path $f) { @(Get-Content $f) } }) }
            Step $w cut {
                $null = Ui 'Remote desktop' -Value realclick -Optional -TimeoutSec 10
                $w.before = Wait-RdpTab 30
                $w.stateBefore = DeviceState
                $skip = @(DiagLines).Count
                $map = Disconnect-LabNetwork -Name $device.Name
                try {
                    Start-Sleep -Seconds 5
                    $w.screenDuring = Screen
                    Shot 'net-cut'
                    Start-Sleep -Seconds 5
                }
                finally { Connect-LabNetwork -Name $device.Name -Switches $map }
                $restored = Get-Date
                $until = $restored.AddSeconds(60)
                do {
                    Start-Sleep -Seconds 3
                    $all = @(DiagLines)
                    $w.log = @(if ($all.Count -ge $skip) { $all | Select-Object -Skip $skip } else { $all })
                } while (-not ($w.log -match 'Connecting -> Connected') -and (Get-Date) -lt $until)
                $w.seconds = [int]((Get-Date) - $restored).TotalSeconds
                $w.connected = Wait-RdpTab 10
                $w.stateAfter = DeviceState
                Shot 'net-back'
            }
            Save 'net' $w
        }

        It 'UI-NET-02 the device''s network cut for 10 s: the tab goes to reconnecting and is connected again within 60 s in the same session' -Tag 'UI-NET-02' {
            $w.before | Should -BeTrue
            # RdpSession: AutoReconnecting -> Connecting "Connection lost - reconnecting (n/max)...", AutoReconnected ->
            # Connected (the state changes are in RemoteHub's diagnostic log; the text only in the tab's tooltip)
            ($w.log -join "`n") | Should -Match 'Connected -> Connecting'
            ($w.log -join "`n") | Should -Match 'Connecting -> Connected'
            $w.connected | Should -BeTrue
            @($w.stateAfter.Rdp) | Should -Be @($w.stateBefore.Rdp)
        }
    }

    Context 'the exported .rdp opened as normaluser' {
        BeforeAll {
            $o = [ordered]@{}
            Step $o user {
                Stop-LabClient -Name $VmName -Task $script:task
                $script:task = $null
                Clear-DeviceSessions
                # normaluser at the console (the client allows one interactive session)
                Set-LabAutoLogon -Name $VmName -Account User
                # double-clicking the file with RemoteHub as its handler = RemoteHub.exe <file>
                $userTask = Start-LabClient -Name $VmName -Account User -Arguments 'C:\lab\device.rdp'
                $o.question = (@((Invoke-LabUi -Name $VmName -Account User -Text '*' -Title 'Connect to*' -Value list -TimeoutSec 60).items) -join "`n")
                $null = Invoke-LabUi -Name $VmName -Account User -Text 'Connect' -Title 'Connect to*'
                # the password in the file is DPAPI-protected for Admin: Windows asks normaluser for it
                $o.prompt = (Invoke-LabUi -Name $VmName -Account User -Text '*' -Title 'Windows Security' -Value exists -Optional -TimeoutSec 60).found
                Start-Sleep -Seconds 3
                Shot 'user-prompt'
                Send-LabText -Name $VmName -Text $password
                Send-LabKey -Name $VmName -Key Enter
                $o.connected = Wait-RdpTab 90 -Account User
                Start-Sleep -Seconds 3
                $o.device = DeviceState
                Shot 'user-connected'
                Stop-LabClient -Name $VmName -Task $userTask
            }
            # Admin back at the console for the rest of the file
            Set-LabAutoLogon -Name $VmName -Account Console
            $script:task = Start-LabClient -Name $VmName
            Save 'rdp-file' $o
        }

        It 'R-RDP-08 Save as .rdp with the password; opened by normaluser: "Open .rdp file" lists drives and clipboard, Connect, Windows asks for the password, connected' -Tag 'R-RDP-08' {
            "$script:rdpFile" | Should -Match 'password 51:b:[0-9A-F]+'
            "$script:rdpFile" | Should -Match 'drivestoredirect:s:\*'
            "$script:rdpFile" | Should -Match 'redirectclipboard:i:1'
            $o.question | Should -Match ([regex]::Escape("Connect to $deviceIp?"))
            $o.question | Should -Match 'The connection was added from a \.rdp file'
            $o.question | Should -Match 'Shares this PC''s drives with the server\.'
            $o.question | Should -Match 'Shares the clipboard with the server\.'
            $o.prompt | Should -BeTrue
            $o.connected | Should -BeTrue
            @($o.device.Rdp).Count | Should -BeGreaterThan 0
        }
    }

    Context 'a jump host in the tree' {
        BeforeAll {
            $j = [ordered]@{}
            # the route hints ("via JumpHost") beside the addresses in the tree
            function Hints {
                $i = @((Ui '*' -Value list).items)
                [pscustomobject]@{ Jump = @($i | Where-Object { $_ -like 'Text|via JumpHost|*' }).Count; Device = @($i | Where-Object { $_ -like 'Text|via Device|*' }).Count }
            }
            Step $j setup {
                $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
                $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
                $script:jump = Start-LabJumpHost
                # the jump VM comes with an agent paired with the host: replaced by one RemoteHub pairs itself
                $null = Uninstall-LabAgent -Name 'JumpHost'
                New-LabClientConnection -Name $VmName -Display 'JumpHost' -TargetHost $script:jump.Address -User $script:jump.User -Password $password
                $j.pair = Install-AgentByHand -Node 'JumpHost' -TargetVm 'JumpHost'
                $j.target = Move-LabVmBehindJump -Name $device.Name
                New-LabClientConnection -Name $VmName -Display 'Behind' -TargetHost $j.target -User $device.AdminAccount -Password $password
                $null = Ui 'Behind' -Value 'menu:Move under>JumpHost'
                Start-Sleep -Seconds 2
            }
            Step $j rdp {
                Clear-DeviceSessions
                $since = Get-LabGuestTime -Name $device.Name
                Menu 'Connect' -Node 'Behind'
                # RemoteHub checks the certificate itself (RdpRouteAsync): "Trust this RDP certificate?"
                $j.trust = Message -Title 'Behind is reached through*' -Button 'Trust and connect' -TimeoutSec 120
                $j.connected = Wait-RdpTab 120
                Start-Sleep -Seconds 3
                $j.device = DeviceState
                $j.logons = @(Invoke-LabGuest -Name $device.Name -ArgumentList $since, $device.AdminAccount -ScriptBlock {
                        param($since, $u)
                        $ev = try { @(Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4624; StartTime = $since } -ErrorAction Stop) } catch { @() }
                        foreach ($e in $ev) {
                            $d = @{}; foreach ($x in ([xml]$e.ToXml()).Event.EventData.Data) { $d[$x.Name] = $x.'#text' }
                            if ($d.TargetUserName -eq $u) { [pscustomobject]@{ Type = $d.LogonType; Package = $d.AuthenticationPackageName; Ip = $d.IpAddress } }
                        }
                    })
                Shot 'jump-rdp'
                $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            }
            Step $j move {
                New-LabClientConnection -Name $VmName -Display 'Movable' -TargetHost 'movable.lab.test'
                $j.h0 = Hints
                $null = Ui 'Movable' -Value 'menu:Move under>JumpHost'
                Start-Sleep -Seconds 2
                $j.h1 = Hints
                # below a jump host nothing can be a jump host: Behind isn't offered as a target
                $j.offer = Ui 'Movable' -Value 'menu:Move under>Behind' -Optional
                Key 1B
                $null = Ui 'Movable' -Value 'menu:Move under>Device'
                Start-Sleep -Seconds 2
                $j.h2 = Hints
                # a jump host can't go below another one: its "Move under" is switched off
                try { $j.jumpUnder = Ui 'JumpHost' -Value 'menu:Move under>Device' -Optional -TimeoutSec 10 }
                catch { $j.jumpUnder = [pscustomobject]@{ error = "$_" } }
                Key 1B
                Start-Sleep -Seconds 2
                $j.h3 = Hints
                $null = Ui 'Movable' -Value 'menu:Move under>Out of Device*'
                Start-Sleep -Seconds 2
                $j.h4 = Hints
                Shot 'jump-tree'
            }
            Save 'jump' $j
        }

        It 'UI-JUMP-01 Behind under JumpHost: the certificate question names the device (not 127.0.0.1), RDP signs in with NTLM through the forwarder' -Tag 'UI-JUMP-01' {
            $j.trust | Should -Match "Issued to: $([regex]::Escape($computer))"
            $j.trust | Should -Not -Match 'Issued to: 127\.0\.0\.1'
            $j.connected | Should -BeTrue
            @($j.device.Rdp).Count | Should -BeGreaterThan 0
            @($j.logons | Where-Object Package -eq 'NTLM').Count | Should -BeGreaterThan 0
        }

        It 'J-11 moved under a jump host, under another one and out again: the route follows; Behind isn''t offered, a jump host can''t go under another' -Tag 'J-11' {
            $j.h1.Jump | Should -Be ($j.h0.Jump + 1)
            $j.offer.error | Should -Match "no menu entry 'Behind'"
            @($j.offer.entries) | Should -Contain 'Device'
            $j.h2.Jump | Should -Be $j.h0.Jump
            $j.h2.Device | Should -Be ($j.h0.Device + 1)
            "$($j.jumpUnder.error)" | Should -Not -BeNullOrEmpty
            $j.h3.Jump | Should -Be $j.h2.Jump
            $j.h3.Device | Should -Be $j.h2.Device
            $j.h4.Device | Should -Be $j.h0.Device
        }
    }

    Context 'an older RemoteHub opens the database' {
        BeforeAll {
            $z = [ordered]@{}
            Step $z older {
                Stop-LabClient -Name $VmName -Task $script:task
                $script:task = $null
                $current = Split-Path (Get-LabClientMsi) -Leaf
                $z.uninstall = Invoke-LabGuest -Name $VmName -ArgumentList $current -ScriptBlock {
                    param($leaf) (Start-Process msiexec.exe -ArgumentList '/x', "`"C:\LabClient\$leaf`"", '/qn', '/norestart' -Wait -PassThru).ExitCode
                }
                $previous = Get-LabClientMsi -Previous
                $z.previous = Split-Path $previous -Leaf
                $z.install = Install-LabClient -Name $VmName -Msi $previous
                $script:task = Start-LabClient -Name $VmName
                $z.tree = @((Ui '*' -Value list).items)
                Shot 'older'
            }
            Save 'older' $z
        }

        It 'J-12 the previous RemoteHub opens the database written with the jump host: Device, JumpHost and Behind are there' -Tag 'J-12' {
            "$($z.install)" | Should -BeIn '0', '3010'
            @($z.tree | Where-Object { $_ -match '\|JumpHost\|' }).Count | Should -BeGreaterThan 0
            @($z.tree | Where-Object { $_ -match '\|Behind\|' }).Count | Should -BeGreaterThan 0
            @($z.tree | Where-Object { $_ -match '\|Device\|' }).Count | Should -BeGreaterThan 0
        }
    }
}
