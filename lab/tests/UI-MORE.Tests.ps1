# UI-AG / UI-AD / UI-CP / UI-TL / UI-NET (more cases) - RemoteHub's own window on the admin's PC (this client VM, the
# console account's session, UI Automation through RhPerson.ps1 -Do uia) against the device (the Server VM).
#
# Covers (docs/test-lab-cases.md section 10.5):
#   UI-TL-09 Copy script, UI-TL-08 untrusted HTTPS from the remoting test, UI-TL-07 WinRM account refused,
#   UI-AG-14 install dialog checks its fields, UI-AG-15 installed but this PC can't reach it, UI-AG-13 agent test fails,
#   UI-CP-13 DesktopClipboard=2 (known product bug: tested for the correct text, fails until fixed), UI-CP-14 copies from
#   before connecting are quiet, UI-CP-15 cancel a copy to the device, UI-NET-03 agent reconnect gives up,
#   UI-AD-10 consent choices per session, UI-AD-09 no usable session, UI-AG-09 connect page offers automatic sign-in,
#   UI-AG-10 sign out asks first, UI-AG-11 2-minute reconnect after automatic sign-in, UI-AG-12 password checks (skipped:
#   not reachable through the UI, see the It), UI-AG-16 throttled sign-in.
# Needs: 2VM (this Windows 11 client + the Server VM as the device), the agent MSI under test built with the credential
# provider (build-installer.ps1 -CredentialProvider) and signed for the lab; Hyper-V on the host (network cut,
# bandwidth limit of the device's adapter).
#
# The order matters - each context builds on the one before:
#  1. PowerShell remoting: the failed test offers RemoteHub's setup script (Copy script), run on the device; the HTTPS
#     certificate is first declined, then trusted from the test; a second connection as normaluser is refused.
#  2. The agent installed silently over that WinRM from RemoteHub's install dialog (its field checks first), with a
#     firewall scope that leaves this PC out ("Installed, but this PC can't reach ..."), automatic sign-in ticked (the
#     credential provider). The scope is then opened on the device (the agent's firewall rule) - RemoteHub only trusts a
#     device certificate it made itself, so the agent has to come from RemoteHub's dialog.
#  3. The agent stopped: Test RemoteHub Agent fails and offers the install.
#  4. Agent desktop to Admin's console session (signed in at the checkpoint; consent None by policy): clipboard and
#     network cases. Notices over the picture last 6 s: a watcher in the client session logs every text RemoteHub shows.
#  5. The connect page: consent choices for the own, a locked and the sign-in session; no SecureDesktop right; the
#     automatic sign-in checkbox.
#  6. Automatic sign-in (policy NoUserBehavior = AutoSignIn, right AutoSignIn; nobody on the console): end action Sign
#     out / Lock, reconnects after a network cut. The agent is restarted before each sign-in (3 per account in 15 min).
#  7. Wrong passwords until the agent throttles this PC.
# Client VMs only. See docs/test-lab-cases.md section 10.5.
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
    $R = [ordered]@{}
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-more-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-more-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of a connection's context menu (right-click and the entry in one process; "A>B" = submenu, a parent that
    # isn't there is skipped)
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) {
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" }
    }
    # the entries the connection's menu shows (asked for one that isn't there: the answer lists them)
    function MenuEntries([string]$Node = 'Device', [string]$Parent = 'Tools') { @((Ui $Node -Value "menu:$Parent>zz-no-such-entry" -Optional -TimeoutSec 20).entries) }
    # a message box of RemoteHub (Dialogs.Confirm / Info, owned by the main window): its window name is the message text
    # (or its title) - -Titles the start of the message and the box's title; answers the window name and its texts, then
    # clicks -Button
    function Box([string[]]$Titles, [string]$Button = '', [int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            foreach ($t in $Titles) {
                $m = Ui '*' -Title $t -Value list -Optional -TimeoutSec 2
                if ($m.found) {
                    if ($Button) { $null = Ui $Button -Title ([WildcardPattern]::Escape($m.window)) -TimeoutSec 10; Start-Sleep -Milliseconds 1500 }
                    return "$($m.window)`n$(@($m.items) -join "`n")"
                }
            }
        } while ((Get-Date) -lt $until)
        throw "no dialog like '$($Titles -join "' / '")' within $TimeoutSec s"
    }
    function BoxShown([string[]]$Titles, [int]$TimeoutSec = 8) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { foreach ($t in $Titles) { if ((Ui '*' -Title $t -Value exists -Optional -TimeoutSec 1).found) { return $true } } } while ((Get-Date) -lt $until)
        $false
    }
    function Esc { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B; Start-Sleep -Milliseconds 400 }
    # one independent UI section: a failure is written down (error, screenshot, open dialogs), the next section runs
    function Step([string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            $R["$Name-dialogs"] = @((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60)
            foreach ($i in 1..2) { Esc }
        }
    }
    function NoError([string]$Name) { $R["$Name-error"] | Should -BeNullOrEmpty -Because "the UI steps of '$Name' ran" }
    function InSession([string]$Vm, [string]$Account, [string]$Code, [int]$TimeoutSec = 120) {
        Invoke-LabInSession -Name $Vm -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($Code))
    }
    function Lines($text) { @("$text" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    function WaitFor([scriptblock]$Cond, [int]$TimeoutSec = 60, [int]$Every = 3) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { if (& $Cond) { return $true }; Start-Sleep -Seconds $Every } while ((Get-Date) -lt $until)
        $false
    }
    # the device's explorer.exe owners, and the sessions PSAdmin has an explorer in
    function ExplorerOwners { @(Invoke-LabGuest -Name $dn -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User }) }) }
    function AdminSessions { @(Invoke-LabGuest -Name $dn -ArgumentList $device.AdminAccount -ScriptBlock { param($u) @(Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue | Where-Object { "$($_.UserName)" -like "*\$u" } | ForEach-Object SessionId | Sort-Object -Unique) }) }
    # every text RemoteHub's window shows, as the watcher logged it ("HH:mm:ss.fff|text"), from line -From on
    function Notices([int]$From = 0) { @(Invoke-LabGuest -Name $VmName -ArgumentList $watchFile -ScriptBlock { param($f) Get-Content -LiteralPath $f -Encoding UTF8 -ErrorAction SilentlyContinue }) | Select-Object -Skip $From }
    function NoticeMark { @(Notices).Count }
    # the agent service restarted (its sign-in throttle and automatic sign-in limit start again)
    function RestartAgent {
        Invoke-LabGuest -Name $dn -ScriptBlock { Restart-Service RemoteHubAgent -Force }
        $null = WaitFor -TimeoutSec 120 { "$(Invoke-LabGuest -Name $dn -ScriptBlock { (Get-Service RemoteHubAgent).Status.ToString() })" -eq 'Running' }
        Start-Sleep -Seconds 8
    }

    # ---- code run in a session
    # the texts RemoteHub shows, logged when they appear (notices last only 6 s)
    $watchCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]
$isText = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::Text)
$seen = @{}
while ($true) {
    try {
        $now = @{}
        foreach ($w in @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition))) {
            $n = $w.Current.Name
            if ($n -notlike 'RemoteHub*' -and $n -notlike '* RemoteHub') { continue }
            foreach ($t in @($w.FindAll([Windows.Automation.TreeScope]::Descendants, $isText))) { $x = $t.Current.Name; if ($x -and -not $t.Current.IsOffscreen) { $now[$x] = 1 } }
        }
        $new = @($now.Keys | Where-Object { -not $seen.ContainsKey($_) })
        if ($new.Count) { Add-Content -LiteralPath '@@FILE@@' -Encoding UTF8 -Value @($new | ForEach-Object { (Get-Date).ToString('HH:mm:ss.fff') + '|' + ($_ -replace '\s+', ' ') }) }
        $seen = $now
    }
    catch { }
    Start-Sleep -Milliseconds 400
}
'@
    # the clipboard: length and hash of the text, its formats, the "keep it out of history / cloud" values
    $clipInfoCode = @'
Add-Type -AssemblyName System.Windows.Forms
$o = [System.Windows.Forms.Clipboard]::GetDataObject()
$t = [string][System.Windows.Forms.Clipboard]::GetText()
function Dw([string]$n) { try { $d = $o.GetData($n) } catch { return $null }; if ($d -is [IO.MemoryStream]) { $a = $d.ToArray(); if ($a.Length -ge 4) { return [BitConverter]::ToInt32($a, 0) } }; $null }
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ length = $t.Length; formats = @(if ($o) { $o.GetFormats() }); history = (Dw 'CanIncludeInClipboardHistory'); cloud = (Dw 'CanUploadToCloudClipboard') })
'@
    function ClipInfo {
        $raw = "$(InSession $VmName 'Console' $clipInfoCode)"
        if ($raw -match '(?m)^JSON:(.*)$') { $Matches[1].Trim() | ConvertFrom-Json } else { [pscustomobject]@{ length = -1; raw = $raw } }
    }
    # a 200x200 test picture (pattern @@K@@) on the clipboard, or the clipboard's picture compared with it ("0" = equal)
    $patCode = @'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
function Pat([int]$k) { $b = New-Object Drawing.Bitmap 200, 200; for ($y = 0; $y -lt 200; $y++) { for ($x = 0; $x -lt 200; $x++) { $b.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, ($x * $k) % 256, ($y * 3 + $k * 40) % 256, (($x + $y) * 5) % 256)) } }; $b }
function Diff($a, $b) { if (-not $a -or -not $b) { return 'no image' }; if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { return "size $($b.Width)x$($b.Height)" }; $d = 0; for ($y = 0; $y -lt $a.Height; $y++) { for ($x = 0; $x -lt $a.Width; $x++) { $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y); if ($p.R -ne $q.R -or $p.G -ne $q.G -or $p.B -ne $q.B) { $d++ } } }; "$d" }
'@
    $setImageCode = $patCode + "`n[Windows.Forms.Clipboard]::SetImage((Pat @@K@@)); 'set'"
    $getImageCode = $patCode + "`n`$img = [Windows.Forms.Clipboard]::GetImage(); if (`$img) { `$img = New-Object Drawing.Bitmap `$img }; 'DIFF=' + (Diff (Pat @@K@@) `$img)"
    function SetFileList([string]$Vm, [string]$Account, [string[]]$Paths) {
        $add = ($Paths | ForEach-Object { "`$c.Add('" + ($_ -replace "'", "''") + "') | Out-Null" }) -join '; '
        InSession $Vm $Account "Add-Type -AssemblyName System.Windows.Forms; `$c = New-Object System.Collections.Specialized.StringCollection; $add; [Windows.Forms.Clipboard]::SetFileDropList(`$c); 'set'"
    }
    # Explorer's Paste into a folder of the session's user, started and left running (the copy is cancelled meanwhile)
    $pasteCode = @'
$f = Join-Path $env:USERPROFILE '@@DIR@@'
$null = New-Item -ItemType Directory -Path $f -Force
(New-Object -ComObject Shell.Application).NameSpace($f).Self.InvokeVerb('paste')
Start-Sleep -Seconds 600
'@

    # ---- the agent desktop tab
    function CloseTabs { foreach ($i in 1..8) { $c = Ui 'Close this tab' -Optional -TimeoutSec 3; if (-not $c.found) { break }; Start-Sleep -Seconds 2 } }
    # a new agent tab at its connect page ("Signed in as ... Choose what to show.")
    function OpenChoose { CloseTabs; Menu 'Connect'; Ui 'Signed in as *' -Value exists -TimeoutSec 90 }
    function ConnectChosen([string]$Session = 'Console*', [string]$Consent = 'Ask them to allow it', [switch]$ViewOnly) {
        $null = Ui $Session -Value check
        if ($Consent) { $null = Ui 'The signed-in user' -Value "choose:$Consent" }
        if ($ViewOnly) { $null = Ui 'View only*' -Value check }
        $null = Ui 'Connect'
    }
    function Live([int]$TimeoutSec = 60) { Ui 'Send Ctrl+Alt+Del' -Value exists -TimeoutSec $TimeoutSec -Optional }
    function TabTexts { "$((Ui '*' -Value list -Optional -TimeoutSec 10).items -join "`n")" }
    function EditConnection([scriptblock]$Do, [string]$Node = 'Device') {
        Menu 'Edit*' -Node $Node
        $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
        & $Do
        $null = Ui 'Save' -Title 'Edit connection'
        Start-Sleep -Seconds 2
    }
    function SetCredential([string]$User, [string]$Pass) {
        EditConnection {
            $null = Ui 'Credentials' -Title 'Edit connection'
            $null = Ui 'Username' -Title 'Edit connection' -Value "setnext:$User"
            $null = Ui 'Password' -Title 'Edit connection' -Value "setnext:$Pass"
        }
    }
    function PsOption([string]$Row, [string]$Choice, [string]$Node = 'Device') {
        EditConnection -Node $Node {
            $null = Ui 'PowerShell remoting' -Title 'Edit connection'
            $null = Ui $Row -Title 'Edit connection' -Value "choose:$Choice"
        }
    }
    # Automatic sign-in from the connect page: nobody on the console, a restarted agent, the console's sign-in screen with
    # "Sign in automatically as ..." ticked; signed in = PSAdmin has a desktop on the device
    function AutoSignIn([string]$Key) {
        RestartAgent
        Disconnect-LabConsole -Name $dn
        $since = Get-LabGuestTime -Name $dn
        $mark = NoticeMark
        $null = OpenChoose
        $null = Ui 'Console*sign-in screen*' -Value check
        $R["$Key-box"] = Ui 'Sign in automatically as *' -Value check -Optional -TimeoutSec 10
        $null = Ui 'Connect'
        $R["$Key-signed"] = WaitFor -TimeoutSec 180 -Every 5 { (ExplorerOwners) -contains $device.AdminAccount }
        $R["$Key-live"] = (Live 60).found
        Start-Sleep -Seconds 4
        $R["$Key-notices"] = @(Notices $mark)
        $R["$Key-sessions"] = AdminSessions
        $R["$Key-since"] = $since
    }

    $device = @(Get-LabVm | Where-Object Os -eq 'Server')[0]
    $dn = $device.Name
    $admins = 'S-1-5-32-544'
    $guests = 'S-1-5-32-546'

    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $dn
        $deviceIp = Get-LabVmAddress -Name $dn
        Reset-LabVm -Name $VmName
        $clientIp = Get-LabVmAddress -Name $VmName
        $null = Install-LabClient -Name $VmName
        $task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
        $watchFile = 'C:\ProgramData\RemoteHubLab\session\ui-more-texts.log'
        $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $d = 'C:\ProgramData\RemoteHubLab\session'; $null = New-Item -ItemType Directory -Path $d -Force; icacls $d /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null }
        $watch = Start-LabInSession -Name $VmName -Account Console -ScriptBlock ([scriptblock]::Create($watchCode.Replace('@@FILE@@', $watchFile)))
        # test files on the admin's PC: a small one, 101 MB (over the limit), 99 MB (under it, with progress)
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $null = New-Item -ItemType Directory -Path 'C:\lab\clip', 'C:\lab\big' -Force
            $b = New-Object byte[] (1MB); (New-Object Random 7).NextBytes($b); [IO.File]::WriteAllBytes('C:\lab\clip\f1.bin', $b)
            foreach ($f in @(@('C:\lab\big\big101.bin', 101MB), @('C:\lab\big\big99.bin', 99MB))) { $fs = [IO.File]::Create($f[0]); $fs.SetLength($f[1]); $fs.Dispose() }
        }
    }
}

AfterAll {
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        try { Set-VMNetworkAdapter -VMName $dn -MaximumBandwidth 0 } catch { }
        try { [IO.File]::WriteAllLines((Join-Path $EvidenceDir 'ui-more-texts.log'), [string[]]@(Notices)) } catch { }
        if ($watch) { try { Stop-LabInSession -Name $VmName -Task $watch } catch { } }
        Save 'results' $R
        Stop-LabClient -Name $VmName -Task $task
        try { Save-LabAgentEvidence -Name $dn -Destination (Join-Path $EvidenceDir 'ui-more-device-logs') } catch { }
        Stop-LabVm -Name $dn
    }
}

Describe 'UI-MORE <VmName>' -Tag Client, UI, 'UI-AG', 'UI-AD', 'UI-CP', 'UI-TL', 'UI-NET' -Skip:(-not $isClient) {

    Context 'PowerShell remoting: Copy script, the HTTPS certificate, a refused account' {
        BeforeAll {
            Step 'tl09' {
                # the test fails (no HTTPS yet) and offers the setup script
                Menu 'Test PowerShell remoting*'
                $R.tl09Test = Box @('PowerShell remoting is not available for Device*', 'PowerShell remoting') -Button 'Set up on the target*'
                $R.tl09Before = Ui 'Test again' -Title 'Set up PowerShell remoting' -Value exists -TimeoutSec 60
                $null = Ui 'Copy script' -Title 'Set up PowerShell remoting'
                $copied = Get-Date
                Start-Sleep -Seconds 2
                $R.tl09After = Ui 'Test again' -Title 'Set up PowerShell remoting' -Value exists
                $R.tl09Dialog = @((Ui '*' -Title 'Set up PowerShell remoting' -Value list).items)
                $R.tl09Clip = ClipInfo
                $script = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { Get-Clipboard -Raw }
                # the admin pastes it into an elevated PowerShell on the device - here over PowerShell Direct
                $null = New-Item -ItemType Directory -Path (Join-Path $EvidenceDir 'ui-more-script') -Force
                $scriptFile = Join-Path $EvidenceDir 'ui-more-script\remotehub-setup.ps1'
                [IO.File]::WriteAllText($scriptFile, "$script", (New-Object Text.UTF8Encoding $true))
                Copy-LabFile -Name $dn -Path $scriptFile -Destination 'C:\LabSetup'
                $R.tl09Run = Invoke-LabGuest -Name $dn -ScriptBlock { Set-ExecutionPolicy -Scope Process Bypass -Force; & 'C:\LabSetup\remotehub-setup.ps1' *>&1 | ForEach-Object { "$_" } | Select-Object -Last 15 }
                # closed without "Test again": the connection uses HTTPS from now on, the certificate is checked by the
                # next test (UI-TL-08)
                $null = Ui 'Close' -Title 'Set up PowerShell remoting'
                # 120 s after the copy the clipboard is empty again
                $left = 130 - ((Get-Date) - $copied).TotalSeconds
                if ($left -gt 0) { Start-Sleep -Seconds ([int][math]::Ceiling($left)) }
                $R.tl09ClipLater = ClipInfo
                $R.tl09ClipLaterSeconds = [int]((Get-Date) - $copied).TotalSeconds
            }
            Step 'tl08' {
                # declined: nothing changes on this PC
                Menu 'Test PowerShell remoting*'
                $R.tl08Ask = Box @('Device answers over HTTPS*', 'PowerShell remoting') -Button 'Check the certificate*'
                $R.tl08Trust = Box @("$deviceIp presents this certificate*", 'Trust this certificate?') -Button 'Cancel'
                $R.tl08Declined = Box @('Not trusted*', 'PowerShell remoting') -Button 'OK'
                $R.tl08RootAfterDecline = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { @(Get-ChildItem Cert:\CurrentUser\Root | Where-Object FriendlyName -like 'RemoteHub*WinRM*' | ForEach-Object FriendlyName) -join ';' }
                # trusted: Windows asks too; then the test runs again and works
                Menu 'Test PowerShell remoting*'
                $null = Box @('Device answers over HTTPS*', 'PowerShell remoting') -Button 'Check the certificate*'
                $null = Box @("$deviceIp presents this certificate*", 'Trust this certificate?') -Button 'Same*trust it'
                $R.tl08Windows = Ui 'Yes' -Title 'Security Warning' -TimeoutSec 30
                $R.tl08Works = Box @('PowerShell remoting works*', 'PowerShell remoting') -Button 'OK' -TimeoutSec 120
                $R.tl08Root = Invoke-LabInSession -Name $VmName -Account Console -ScriptBlock { @(Get-ChildItem Cert:\CurrentUser\Root | Where-Object FriendlyName -like 'RemoteHub*WinRM*' | ForEach-Object FriendlyName) -join ';' }
            }
            Step 'tl07' {
                # normaluser over HTTPS (the certificate is trusted now): WinRM answers, the account is refused
                New-LabClientConnection -Name $VmName -Display 'Refused' -TargetHost $deviceIp -User $device.UserAccount -Password $password
                PsOption 'Use HTTPS' 'On*' -Node 'Refused'
                Menu 'Test PowerShell remoting*' -Node 'Refused'
                $R.tl07 = Box @('PowerShell remoting answers on Refused*', 'PowerShell remoting') -Button 'OK' -TimeoutSec 120
            }
            Shot 'remoting'
            Save 'remoting' $R
        }

        It 'UI-TL-09 Copy script: "Test again" only after it; "Copied. Open an elevated PowerShell on <host>, paste ..."; kept out of the clipboard history, cleared after 120 s' -Tag 'UI-TL-09' {
            NoError 'tl09'
            $R.tl09Before.found | Should -BeTrue
            $R.tl09Before.enabled | Should -BeFalse -Because 'nothing was copied or saved yet'
            $R.tl09After.enabled | Should -BeTrue
            ($R.tl09Dialog -join "`n") | Should -Match ("Copied\. Open an elevated PowerShell on " + [regex]::Escape($deviceIp) + ", paste .*Then click .Test again.")
            $R.tl09Clip.length | Should -BeGreaterThan 0
            @($R.tl09Clip.formats) | Should -Contain 'ExcludeClipboardContentFromMonitorProcessing'
            $R.tl09Clip.history | Should -Be 0
            $R.tl09Clip.cloud | Should -Be 0
            $R.tl09ClipLater.length | Should -Be 0 -Because "the script is cleared 120 s after the copy (read after $($R.tl09ClipLaterSeconds) s)"
        }

        It 'UI-TL-08 the test over untrusted HTTPS: "<name> answers over HTTPS, but this PC doesn''t trust its certificate yet", Check the certificate, declined: "Not trusted. Nothing was changed on this PC."; trusted: works' -Tag 'UI-TL-08' {
            NoError 'tl09'
            NoError 'tl08'
            ($R.tl09Run -join "`n") | Should -Match 'Done\. PowerShell remoting is enabled over HTTPS'
            $R.tl08Ask | Should -Match 'Device answers over HTTPS, but this PC doesn.t trust its certificate yet\.'
            $R.tl08Trust | Should -Match 'Is it the same thumbprint the setup script printed'
            $R.tl08Declined | Should -Match 'Not trusted\. Nothing was changed on this PC\.'
            "$($R.tl08RootAfterDecline)" | Should -BeNullOrEmpty -Because 'declined: no certificate trusted'
            $R.tl08Windows.found | Should -BeTrue
            $R.tl08Works | Should -Match 'PowerShell remoting works'
            "$($R.tl08Root)" | Should -Match 'RemoteHub.*WinRM'
        }

        It 'UI-TL-07 WinRM refuses the account: "PowerShell remoting answers on <name>, but the account was refused. <msg>", no "Set up on the target..."' -Tag 'UI-TL-07' {
            NoError 'tl07'
            $R.tl07 | Should -Match 'PowerShell remoting answers on Refused, but the account was refused\.'
            $R.tl07 | Should -Match 'Access denied\.'
            $R.tl07 | Should -Not -Match 'Set up on the target'
        }
    }

    Context 'the agent installed from the dialog over WinRM' {
        BeforeAll {
            Step 'ag14' {
                Install-LabSigningCertificate -Name $dn
                Menu 'RemoteHub Agent>Install RemoteHub Agent*'
                $t = 'Install RemoteHub Agent'
                # (the dialog may reach below the screen: its top is checked, its buttons are Invoked)
                $null = Ui 'Device label' -Title $t -Value exists -TimeoutSec 60
                function Try-Install([string]$Key) {
                    $null = Ui 'Install' -Title $t
                    Start-Sleep -Seconds 1
                    $R["ag14$Key"] = ((Ui '*' -Title $t -Value list).items -join "`n")
                    $R["ag14$Key-open"] = (Ui 'Device label' -Title $t -Value exists -Optional -TimeoutSec 5).found
                }
                $null = Ui 'Port' -Title $t -Value 'setnext:0'
                Try-Install 'Port0'
                $null = Ui 'Port' -Title $t -Value 'setnext:x'
                Try-Install 'PortX'
                $null = Ui 'Port' -Title $t -Value 'setnext:58732'
                $null = Ui 'Allow connections from' -Title $t -Value 'setnext:abc'
                Try-Install 'Scope'
                $null = Ui 'Allow connections from' -Title $t -Value 'setnext:'
                $null = Ui 'Device label' -Title $t -Value 'setnext:'
                Try-Install 'Label'
            }
            Step 'ag15' {
                $t = 'Install RemoteHub Agent'
                # silently over WinRM, the scope without this PC (TEST-NET address), automatic sign-in allowed
                $null = Ui 'Device label' -Title $t -Value 'setnext:Device'
                $null = Ui 'Allow connections from' -Title $t -Value 'setnext:192.0.2.10'
                $null = Ui 'Install silently over PowerShell remoting*' -Title $t -Value check
                $R.ag15AutoSignIn = Ui 'Allow automatic sign-in*' -Title $t -Value check -Optional -TimeoutSec 5
                $null = Ui 'Use the agent for this connection afterwards' -Title $t -Value check -Optional -TimeoutSec 5
                $null = Ui 'Install' -Title $t
                $R.ag15Done = Ui 'Done' -Title $t -TimeoutSec 600
                $R.ag15Rows = @((Ui '*' -Title $t -Value list -Optional).items)
                Shot 'ag15'
                $null = Ui 'Done' -Title $t -Optional -TimeoutSec 5
                $R.ag15State = Get-LabAgentState -Name $dn
                # still marked installed: the menu offers the update
                $m = Ui 'Device' -Value 'menu:RemoteHub Agent>Update RemoteHub Agent*' -Optional -TimeoutSec 15
                $R.ag15Update = -not ($m.PSObject.Properties['error'] -and $m.error)
                if ($R.ag15Update) { $null = Ui 'Close' -Title 'Update RemoteHub Agent' -TimeoutSec 60 } else { $R.ag15Entries = @($m.entries); Esc }
                # the admin opens the scope on the device: the agent's firewall rule for any address
                Invoke-LabGuest -Name $dn -ScriptBlock { Set-NetFirewallRule -DisplayName 'RemoteHub Agent' -RemoteAddress Any }
            }
            Save 'install' $R
        }

        It 'UI-AG-14 the install dialog checks its fields: port 0 / x, scope "abc", empty label - each with its message, the dialog stays open' -Tag 'UI-AG-14' {
            NoError 'ag14'
            $R.ag14Port0 | Should -Match 'The port must be a number from 1 to 65535\.'
            $R.ag14PortX | Should -Match 'The port must be a number from 1 to 65535\.'
            $R.ag14Scope | Should -Match '"Allow connections from" must be IP addresses or ranges'
            $R.ag14Label | Should -Match 'Enter a device label\.'
            foreach ($k in 'Port0', 'PortX', 'Scope', 'Label') { $R["ag14$k-open"] | Should -BeTrue -Because "the dialog stays open ($k)" }
        }

        It 'UI-AG-15 a scope without this PC: after 5 tries "Installed, but this PC can''t reach the agent on <host>:<port> (<error>). Check firewalls ..."; still marked installed' -Tag 'UI-AG-15' {
            NoError 'ag15'
            $R.ag15Done.found | Should -BeTrue
            $rows = $R.ag15Rows -join "`n"
            $rows | Should -Match ("Installed, but this PC can.t reach the agent on " + [regex]::Escape($deviceIp) + ':58732 \(')
            $rows | Should -Match 'Check firewalls between this PC and the device'
            $R.ag15State.Service.State | Should -Be 'Running'
            $R.ag15Update | Should -BeTrue -Because 'the connection is marked installed (the menu offers Update RemoteHub Agent...)'
        }
    }

    Context 'the agent stopped: Test RemoteHub Agent' {
        BeforeAll {
            Step 'ag13' {
                $R.ag13EntriesBefore = MenuEntries
                Esc
                Invoke-LabGuest -Name $dn -ScriptBlock { Stop-Service RemoteHubAgent -Force }
                Menu 'Tools>Test RemoteHub Agent*'
                $R.ag13 = Box @('The RemoteHub Agent on Device could not be used*', 'RemoteHub Agent') -Button 'Cancel' -TimeoutSec 120
                $R.ag13EntriesAfter = MenuEntries
                Esc
                # started again: the test works (and the menu offers the tools)
                Invoke-LabGuest -Name $dn -ScriptBlock { Start-Service RemoteHubAgent }
                Start-Sleep -Seconds 10
                Menu 'Tools>Test RemoteHub Agent*'
                $R.ag13Works = Box @('The RemoteHub Agent works*', 'RemoteHub Agent') -Button 'OK' -TimeoutSec 120
            }
            Save 'agent-test' $R
        }

        It 'UI-AG-13 agent stopped: "The RemoteHub Agent on <name> could not be used. <reason>" with "Install / update the agent..."; the entry then reads "Test RemoteHub Agent again"' -Tag 'UI-AG-13' {
            NoError 'ag13'
            $R.ag13 | Should -Match 'The RemoteHub Agent on Device could not be used\.'
            $R.ag13 | Should -Match 'Install / update the agent'
            @($R.ag13EntriesAfter) | Should -Contain 'Test RemoteHub Agent again'
            # the entry's tooltip "Last result: <reason>" can't be read: RhPerson's menu step answers the tooltip only
            # for a disabled entry
            $R.ag13Works | Should -Match 'The RemoteHub Agent works'
        }
    }

    Context 'agent desktop: clipboard policy, quiet copies, cancelled copy, reconnect gives up' {
        BeforeAll {
            # connecting to Admin's console without asking (consent None needs the policy and the ConsentBypass right)
            Set-LabAgentPolicy -Name $dn -Clear -Values @{ AllowedConsentModes = @('Ask', 'Pin', 'None') } -Rights @{ ConsentBypass = @($admins) }
            Step 'cp13' {
                Set-LabAgentPolicy -Name $dn -Values @{ DesktopClipboard = 2 }
                $null = OpenChoose
                ConnectChosen -Consent 'Connect without asking*'
                $R.cp13Live = (Live 90).found
                Start-Sleep -Seconds 3
                $null = InSession $VmName 'Console' $setImageCode.Replace('@@K@@', '7') -TimeoutSec 240
                Start-Sleep -Seconds 4
                $R.cp13Image = "$(InSession $dn 'Console' $getImageCode.Replace('@@K@@', '7') -TimeoutSec 300)" -replace '(?s).*DIFF=([^\r\n]*).*', '$1'
                $mark = NoticeMark
                $null = SetFileList $VmName 'Console' @('C:\lab\clip\f1.bin')
                Start-Sleep -Seconds 6
                $R.cp13Notices = @(Notices $mark)
                Shot 'cp13'
                Set-LabAgentPolicy -Name $dn -Clear -Values @{ AllowedConsentModes = @('Ask', 'Pin', 'None') } -Rights @{ ConsentBypass = @($admins) }
            }
            Step 'cp14' {
                CloseTabs
                # 101 MB copied before the tab goes live: offered quietly, no notice
                $null = SetFileList $VmName 'Console' @('C:\lab\big\big101.bin')
                Start-Sleep -Seconds 2
                $mark = NoticeMark
                $null = OpenChoose
                ConnectChosen -Consent 'Connect without asking*'
                $R.cp14Live = (Live 90).found
                Start-Sleep -Seconds 10
                $R.cp14Before = @(Notices $mark)
                # the same copy while connected: the notice
                $mark = NoticeMark
                $null = SetFileList $VmName 'Console' @('C:\lab\big\big101.bin')
                Start-Sleep -Seconds 8
                $R.cp14While = @(Notices $mark)
            }
            Step 'cp15' {
                # 99 MB through a slow link (10 Mbit/s on the device's adapter), pasted on the device, cancelled here
                Set-VMNetworkAdapter -VMName $dn -MaximumBandwidth 10000000
                $paste = $null
                try {
                    $null = Invoke-LabGuest -Name $dn -ScriptBlock { Get-ChildItem 'C:\Users\*\cp15' -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
                    $mark = NoticeMark
                    $null = SetFileList $VmName 'Console' @('C:\lab\big\big99.bin')
                    Start-Sleep -Seconds 4
                    $paste = Start-LabInSession -Name $dn -Account Console -ScriptBlock ([scriptblock]::Create($pasteCode.Replace('@@DIR@@', 'cp15')))
                    $R.cp15Progress = WaitFor -TimeoutSec 90 -Every 1 { @(Notices $mark | Where-Object { $_ -match 'Copying 1 file to the device' }).Count -gt 0 }
                    Start-Sleep -Seconds 3
                    Shot 'cp15-progress'
                    $R.cp15Cancel = Ui 'Cancel' -TimeoutSec 10
                    Start-Sleep -Seconds 4
                    $R.cp15Notices = @(Notices $mark)
                    Start-Sleep -Seconds 10
                    $R.cp15Arrived = @(Invoke-LabGuest -Name $dn -ScriptBlock { @(Get-ChildItem 'C:\Users\*\cp15' -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)=$($_.Length)" }) })
                }
                finally {
                    Set-VMNetworkAdapter -VMName $dn -MaximumBandwidth 0
                    if ($paste) { try { Stop-LabInSession -Name $dn -Task $paste } catch { } }
                }
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value 'cp15 done'
            }
            Step 'net03' {
                # a cut longer than the 30 s a normal session keeps trying (the drop is noticed first, then 30 s of tries)
                if (-not (Live 3).found) { $null = OpenChoose; ConnectChosen -Consent 'Connect without asking*'; $null = Live 90 }
                $mark = NoticeMark
                $switches = Disconnect-LabNetwork -Name $dn
                try {
                    $R.net03Ended = WaitFor -TimeoutSec 150 -Every 2 { (Ui 'The connection dropped and couldn*' -Value exists -Optional -TimeoutSec 1).found }
                    Shot 'net03-ended'
                }
                finally { Connect-LabNetwork -Name $dn -Switches $switches }
                Start-Sleep -Seconds 2
                $R.net03Notices = @(Notices $mark)
                # Stop reconnecting ends it at once
                Start-Sleep -Seconds 10
                $null = OpenChoose
                ConnectChosen -Consent 'Connect without asking*'
                $R.net03Live2 = (Live 90).found
                $switches = Disconnect-LabNetwork -Name $dn
                try {
                    $R.net03Reconnecting = WaitFor -TimeoutSec 120 -Every 1 { (Ui 'Stop reconnecting' -Value exists -Optional -TimeoutSec 1).found }
                    $t0 = Get-Date
                    $null = Ui 'Stop reconnecting'
                    $null = WaitFor -TimeoutSec 30 -Every 1 { (Ui 'Use RDP instead' -Value exists -Optional -TimeoutSec 1).found }
                    $R.net03StopSeconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                    $R.net03StopText = TabTexts
                    Shot 'net03-stopped'
                }
                finally { Connect-LabNetwork -Name $dn -Switches $switches }
                Start-Sleep -Seconds 5
                CloseTabs
            }
            Save 'desktop' $R
        }

        # Known product bug (docs/test-lab-test-document.md, findings: UI-CP-13): with DesktopClipboard=2 RemoteHub refuses
        # a file copy with "Copying images and files isn't allowed on this device - only text." (AgentDesktopSession.cs
        # 402-403); tested for the agent's correct text - fails until fixed.
        It 'UI-CP-13 DesktopClipboard=2: an image reaches the device; a file copy is refused with "Copying files isn''t allowed on this device."' -Tag 'UI-CP-13' {
            NoError 'cp13'
            $R.cp13Live | Should -BeTrue
            $R.cp13Image | Should -Be '0'
            ($R.cp13Notices -join "`n") | Should -Match 'Copying files isn.t allowed on this device\.'
            ($R.cp13Notices -join "`n") | Should -Not -Match 'Copying images and files isn.t allowed'
        }

        It 'UI-CP-14 a too-big copy already on the clipboard when the tab goes live: no notice; the same copy while connected: "Too much to copy ..."' -Tag 'UI-CP-14' {
            NoError 'cp14'
            $R.cp14Live | Should -BeTrue
            ($R.cp14Before -join "`n") | Should -Not -Match 'Too much to copy'
            ($R.cp14While -join "`n") | Should -Match 'Too much to copy through the remote desktop \(at most 100 MB and 1000 files\) . use the file transfer instead\.'
        }

        It 'UI-CP-15 1 MB or more to the device: "Copying 1 file to the device", cancelled: "Cancelled - nothing was copied to the device."; nothing complete on the device' -Tag 'UI-CP-15' {
            NoError 'cp15'
            $R.cp15Progress | Should -BeTrue
            $R.cp15Cancel.found | Should -BeTrue
            ($R.cp15Notices -join "`n") | Should -Match 'Cancelled . nothing was copied to the device\.'
            @($R.cp15Arrived | Where-Object { $_ -eq "big99.bin=$(99MB)" }) | Should -BeNullOrEmpty
        }

        It 'UI-CP-15 no receipt from the device within 30 s: "Failed - the device didn''t confirm the copy of <what>; it may not have arrived."' -Tag 'UI-CP-15' {
            # needs a device that gets the whole copy but never confirms it (an agent build that drops the receipt): a
            # network cut or a suspended helper in the lab either stops the copy before the end or races the receipt
            Set-ItResult -Skipped -Because 'needs an agent that receives a copy without confirming it (fault-injection build)'
        }

        It 'UI-NET-03 a cut longer than 30 s: "Connection lost - reconnecting...", "Attempt n - trying for up to 30 s.", then "... couldn''t be restored within 30 s: <error>"; Stop reconnecting ends it at once' -Tag 'UI-NET-03' {
            NoError 'net03'
            $n = $R.net03Notices -join "`n"
            $n | Should -Match 'Connection lost . reconnecting'
            $n | Should -Match 'Attempt \d+ . trying for up to 30 s\.'
            $n | Should -Match 'The connection dropped and couldn.t be restored within 30 s: '
            $R.net03Ended | Should -BeTrue
            $R.net03Reconnecting | Should -BeTrue
            $R.net03StopSeconds | Should -BeLessOrEqual 5
        }
    }

    Context 'the connect page: consent choices, no usable session, automatic sign-in offered' {
        BeforeAll {
            # the device allows connecting without asking (for the "don't ask" choice of a locked / sign-in session) and
            # automatic sign-in for administrators
            $policy = @{ AllowedConsentModes = @('Ask', 'Pin', 'None'); NoUserBehavior = 'AutoSignIn' }
            $rights = @{ ConsentBypass = @($admins); AutoSignIn = @($admins) }
            $later = @('When someone unlocks or signs in: ask them', 'When someone unlocks or signs in: show them a code', 'When someone unlocks or signs in: don''t ask (they see a banner)')
            function Choices([string[]]$Items) { @(foreach ($i in $Items) { $c = Ui 'The signed-in user' -Value "choose:$i" -Optional -TimeoutSec 10; [pscustomobject]@{ Item = $i; Ok = -not ($c.PSObject.Properties['error'] -and $c.error) } }) }
            Step 'ad10-own' {
                # the connection signs in as Admin, who is signed in at the console: the own session; the policy
                # doesn't allow None (no policy at all)
                Set-LabAgentPolicy -Name $dn -Clear
                SetCredential $device.ConsoleAccount $password
                $null = OpenChoose
                $R.ad10OwnSessions = @((Ui '*' -Value list).items | Where-Object { $_ -like 'RadioButton|*' })
                $null = Ui 'Console*(signed in)*' -Value check
                $R.ad10Own = Choices @('Connect without asking (they see a banner)', 'Ask them to allow it')
                Shot 'ad10-own'
                CloseTabs
                SetCredential $device.AdminAccount $password
            }
            Step 'ad10-locked' {
                Set-LabAgentPolicy -Name $dn -Clear -Values $policy -Rights $rights
                Lock-LabSession -Name $dn -Account Console
                Start-Sleep -Seconds 4
                $null = OpenChoose
                $null = Ui 'Console*(locked)*' -Value check
                $R.ad10Locked = Choices $later
                # UI-AG-09: not for a locked session
                $R.ag09Locked = Ui 'Sign in automatically as *' -Value exists -Optional -TimeoutSec 5
                Shot 'ad10-locked'
                CloseTabs
            }
            Step 'ad10-signin' {
                Disconnect-LabConsole -Name $dn
                $null = OpenChoose
                $null = Ui 'Console*sign-in screen*' -Value check
                $R.ad10SignIn = Choices $later
                CloseTabs
            }
            Step 'ag09' {
                PsOption 'Sign in automatically when nobody is signed in' 'On'
                $null = OpenChoose
                $null = Ui 'Console*sign-in screen*' -Value check
                $R.ag09On = Ui 'Sign in automatically as *' -Value read -Optional -TimeoutSec 10
                $null = Ui 'View only*' -Value check
                $R.ag09ViewOnly = Ui 'Sign in automatically as *' -Value exists -Optional -TimeoutSec 5
                $null = Ui 'View only*' -Value uncheck
                $R.ag09ViewOnlyOff = Ui 'Sign in automatically as *' -Value exists -Optional -TimeoutSec 5
                Shot 'ag09'
                CloseTabs
                # the connection's option Off: shown, not ticked
                PsOption 'Sign in automatically when nobody is signed in' 'Off'
                $null = OpenChoose
                $null = Ui 'Console*sign-in screen*' -Value check
                $R.ag09Off = Ui 'Sign in automatically as *' -Value read -Optional -TimeoutSec 10
                CloseTabs
                PsOption 'Sign in automatically when nobody is signed in' 'On'
                # this PC's policy DisableAgentAutoSignIn
                Invoke-LabGuest -Name $VmName -ScriptBlock { Set-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub' -Name DisableAgentAutoSignIn -Value 1 -Type DWord }
                try {
                    $null = OpenChoose
                    $null = Ui 'Console*sign-in screen*' -Value check
                    $R.ag09Disabled = Ui 'Sign in automatically as *' -Value exists -Optional -TimeoutSec 5
                    CloseTabs
                }
                finally { Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub' -Name DisableAgentAutoSignIn -ErrorAction SilentlyContinue } }
                # the device: no AutoSignIn right for administrators
                Set-LabAgentPolicy -Name $dn -Clear -Values $policy -Rights @{ ConsentBypass = @($admins); AutoSignIn = @($guests) }
                $null = OpenChoose
                $null = Ui 'Console*sign-in screen*' -Value check
                $R.ag09NoRight = Ui 'Sign in automatically as *' -Value exists -Optional -TimeoutSec 5
                CloseTabs
            }
            Step 'ad09' {
                # nobody signed in, no SecureDesktop right for administrators
                Set-LabAgentPolicy -Name $dn -Clear -Rights @{ SecureDesktop = @($guests) }
                $null = OpenChoose
                $R.ad09Sessions = @((Ui '*' -Value list).items | Where-Object { $_ -like 'RadioButton|*' })
                $R.ad09Radio = Ui 'Console*your account may not see it' -Value exists -Optional -TimeoutSec 10
                $R.ad09Text = Ui 'Nobody is using a session right now*' -Value read -Optional -TimeoutSec 10
                $R.ad09Connect = Ui 'Connect' -Value exists -Optional -TimeoutSec 10
                Shot 'ad09'
                CloseTabs
            }
            Set-LabAgentPolicy -Name $dn -Clear -Values $policy -Rights $rights
            Save 'connect-page' $R
        }

        It 'UI-AD-10 own signed-in session: "Connect without asking (they see a banner)" offered though the policy lacks None; locked / sign-in session: "When someone unlocks or signs in: ask them / show them a code / don''t ask (they see a banner)"' -Tag 'UI-AD-10' {
            NoError 'ad10-own'
            NoError 'ad10-locked'
            NoError 'ad10-signin'
            ($R.ad10OwnSessions -join "`n") | Should -Match ([regex]::Escape($device.ConsoleAccount) + ' \(signed in\)')
            foreach ($c in @($R.ad10Own) + @($R.ad10Locked) + @($R.ad10SignIn)) { $c.Ok | Should -BeTrue -Because "the choice '$($c.Item)' is offered" }
        }

        It 'UI-AD-09 nobody signed in, no SecureDesktop right: the entries end " - your account may not see it" and are off, "Nobody is using a session right now ...", Connect off' -Tag 'UI-AD-09' {
            NoError 'ad09'
            $R.ad09Radio.found | Should -BeTrue
            $R.ad09Radio.enabled | Should -BeFalse
            "$($R.ad09Text.value)" | Should -Match 'Nobody is using a session right now, and your account may not see the sign-in or lock screen on this device'
            $R.ad09Connect.found | Should -BeTrue
            $R.ad09Connect.enabled | Should -BeFalse
        }

        It 'UI-AG-09 "Sign in automatically as <account>" only for the console''s sign-in screen with the device''s right and without DisableAgentAutoSignIn; ticked when the option is On; View only switches it off' -Tag 'UI-AG-09' {
            NoError 'ad10-locked'
            NoError 'ag09'
            $R.ag09On.found | Should -BeTrue
            $R.ag09On.name | Should -BeLike "Sign in automatically as *$($device.AdminAccount)"
            $R.ag09On.value | Should -Be 'On'
            $R.ag09ViewOnly.enabled | Should -BeFalse
            $R.ag09ViewOnlyOff.enabled | Should -BeTrue
            $R.ag09Off.found | Should -BeTrue
            $R.ag09Off.value | Should -Be 'Off'
            $R.ag09Disabled.found | Should -BeFalse -Because 'this PC''s policy DisableAgentAutoSignIn'
            $R.ag09NoRight.found | Should -BeFalse -Because 'the device doesn''t give the right AutoSignIn'
            $R.ag09Locked.found | Should -BeFalse -Because 'a locked session isn''t the sign-in screen'
        }
    }

    Context 'automatic sign-in: end action and reconnect' {
        BeforeAll {
            $signOutBox = @('RemoteHub signed in on Device automatically*', 'Sign out on the device?')
            Step 'ag10-signout' {
                PsOption 'Sign in automatically when nobody is signed in' 'On'
                PsOption 'When an automatic sign-in*' 'Sign out*'
                AutoSignIn 'so'
                # each way of ending asks; Cancel keeps the session
                $null = Ui 'Disconnect'
                $R.soAskDisconnect = Box $signOutBox -Button 'Cancel' -TimeoutSec 20
                $R.soLive1 = (Live 5).found
                $null = Ui 'Close this tab'
                $R.soAskTab = Box $signOutBox -Button 'Cancel' -TimeoutSec 20
                # Ctrl+W with the focus outside the picture (in the tab, the keys go to the device)
                $null = Ui 'Device' -Value realclick
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x57
                $R.soAskCtrlW = Box $signOutBox -Button 'Cancel' -TimeoutSec 20
                $null = Ui 'Close all tabs of this connection'
                $R.soAskGroup = Box $signOutBox -Button 'Cancel' -TimeoutSec 20
                $R.soLive2 = (Live 5).found
                $since = Get-LabGuestTime -Name $dn
                $null = Ui 'Disconnect'
                $null = Box $signOutBox -Button 'Disconnect' -TimeoutSec 20
                $R.soGone = WaitFor -TimeoutSec 120 -Every 5 { (ExplorerOwners) -notcontains $device.AdminAccount }
                $R.soEvents = @(Get-LabAgentEvent -Name $dn -Since $since -Id 1107)
                CloseTabs
            }
            Step 'ag10-lock' {
                PsOption 'When an automatic sign-in*' 'Lock the session'
                AutoSignIn 'lk'
                $null = Ui 'Disconnect'
                $R.lkAsked = BoxShown $signOutBox
                Start-Sleep -Seconds 10
                $R.lkKept = (ExplorerOwners) -contains $device.AdminAccount
                CloseTabs
            }
            Step 'ag10-policy' {
                # the device's AutoSignInEndAction = Lock wins over the connection's Sign out
                PsOption 'When an automatic sign-in*' 'Sign out*'
                Set-LabAgentPolicy -Name $dn -Values @{ AutoSignInEndAction = 'Lock' }
                AutoSignIn 'pl'
                $null = Ui 'Disconnect'
                $R.plAsked = BoxShown $signOutBox
                Start-Sleep -Seconds 10
                $R.plKept = (ExplorerOwners) -contains $device.AdminAccount
                CloseTabs
                Invoke-LabGuest -Name $dn -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -Name AutoSignInEndAction -ErrorAction SilentlyContinue }
                PsOption 'When an automatic sign-in*' 'Lock the session'
            }
            Step 'ag11-resume' {
                AutoSignIn 'rs'
                $mark = NoticeMark
                $switches = Disconnect-LabNetwork -Name $dn
                try { Start-Sleep -Seconds 60 } finally { Connect-LabNetwork -Name $dn -Switches $switches }
                $R.rsBack = WaitFor -TimeoutSec 120 -Every 2 { (Live 1).found -and -not (Ui 'Stop reconnecting' -Value exists -Optional -TimeoutSec 1).found }
                Start-Sleep -Seconds 3
                $R.rsNotices = @(Notices $mark)
                $R.rsAfter = AdminSessions
                CloseTabs
            }
            Step 'ag11-giveup' {
                AutoSignIn 'gu'
                $mark = NoticeMark
                $switches = Disconnect-LabNetwork -Name $dn
                try {
                    $R.guEnded = WaitFor -TimeoutSec 240 -Every 3 { (Ui 'The connection dropped and couldn*' -Value exists -Optional -TimeoutSec 1).found }
                    Shot 'ag11-giveup'
                }
                finally { Connect-LabNetwork -Name $dn -Switches $switches }
                Start-Sleep -Seconds 2
                $R.guNotices = @(Notices $mark)
                CloseTabs
            }
            Save 'auto-sign-in' $R
        }

        It 'UI-AG-10 end action Sign out: "... When you disconnect, the session is signed out."; Disconnect, x, Ctrl+W and the group ask "Sign out on the device?" (Cancel keeps it); with Lock - or the device''s AutoSignInEndAction = Lock - "... is locked." and nothing asked' -Tag 'UI-AG-10' {
            NoError 'ag10-signout'
            NoError 'ag10-lock'
            NoError 'ag10-policy'
            $R['so-signed'] | Should -BeTrue
            ($R['so-notices'] -join "`n") | Should -Match 'When you disconnect, the session is signed out\.'
            foreach ($k in 'soAskDisconnect', 'soAskTab', 'soAskCtrlW', 'soAskGroup') {
                $R[$k] | Should -Match 'When you disconnect, that session is signed out and unsaved work in it is lost\.' -Because "$k asks"
            }
            $R.soLive1 | Should -BeTrue
            $R.soLive2 | Should -BeTrue -Because 'Cancel keeps the session'
            $R.soGone | Should -BeTrue -Because 'Disconnect signs the session out'
            # the middle click on the tab isn't sent (the lab's console mouse has no middle button)
            $R['lk-signed'] | Should -BeTrue
            ($R['lk-notices'] -join "`n") | Should -Match 'When you disconnect, the session is locked\.'
            $R.lkAsked | Should -BeFalse
            $R.lkKept | Should -BeTrue
            $R['pl-signed'] | Should -BeTrue
            ($R['pl-notices'] -join "`n") | Should -Match 'When you disconnect, the session is locked\.'
            $R.plAsked | Should -BeFalse
            $R.plKept | Should -BeTrue
        }

        It 'UI-AG-11 after an automatic sign-in a 60 s cut: "Attempt n - trying for up to 120 s." and the same session goes on; past 120 s: "... couldn''t be restored within 120 s: ..."' -Tag 'UI-AG-11' {
            NoError 'ag11-resume'
            NoError 'ag11-giveup'
            $R['rs-signed'] | Should -BeTrue
            ($R.rsNotices -join "`n") | Should -Match 'Attempt \d+ . trying for up to 120 s\.'
            $R.rsBack | Should -BeTrue
            @($R.rsAfter) | Should -Be @($R['rs-sessions']) -Because 'the same Windows session goes on'
            $R['gu-signed'] | Should -BeTrue
            $R.guEnded | Should -BeTrue
            ($R.guNotices -join "`n") | Should -Match 'The connection dropped and couldn.t be restored within 120 s: '
        }

        It 'UI-AG-12 no stored password / a password over 256 characters: "Automatic sign-in needs a stored password ..." / "The password is too long ..."; nothing sent' -Tag 'UI-AG-12' {
            # Not reachable through RemoteHub's window: without a stored password RemoteHub asks for one before it signs in
            # to the agent (MainWindow.BuildTarget, PasswordDialog refuses an empty one) and keeps it for the session, so
            # AgentDesktopSession.SendSignIn always has a password; and a password over 256 characters can't sign in to
            # the agent with a Windows account in the first place (the same password is used for both).
            Set-ItResult -Skipped -Because 'both checks sit behind a successful agent sign-in with the same password - only a harness (Start-LabDesktop) can send them'
        }
    }

    Context 'wrong passwords until the agent throttles this PC' {
        BeforeAll {
            Step 'ag16' {
                RestartAgent
                SetCredential $device.AdminAccount "$password-wrong"
                CloseTabs
                $R.ag16Tries = @()
                Menu 'Connect'
                foreach ($i in 1..7) {
                    Start-Sleep -Seconds 6
                    $null = Ui 'Use RDP instead' -Value exists -Optional -TimeoutSec 30
                    $R.ag16Tries += TabTexts
                    if ($R.ag16Tries[-1] -match 'refuses sign-ins from this PC') { break }
                    $null = Ui 'Connect' -Optional -TimeoutSec 10
                }
                Shot 'ag16'
                CloseTabs
            }
            # the right password again, the throttle cleared
            try { SetCredential $device.AdminAccount $password } catch { }
            try { RestartAgent } catch { }
            Save 'throttle' $R
        }

        It 'UI-AG-16 after repeated wrong passwords: "The RemoteHub Agent on <host>:58732 refuses sign-ins from this PC for a few minutes after too many failed attempts."' -Tag 'UI-AG-16' {
            NoError 'ag16'
            ($R.ag16Tries | Select-Object -First 1) | Should -Match 'rejected the sign-in: wrong user name or password'
            ($R.ag16Tries -join "`n") | Should -Match ('The RemoteHub Agent on ' + [regex]::Escape($deviceIp) + ':58732 refuses sign-ins from this PC for a few minutes after too many failed attempts\.')
        }
    }
}
