# R-RDP / UI-CP (RDP) / UI-SAS (RDP) / UI-NET-02 / A-CLIP / J-11 / J-12 / UI-JUMP-01 - RemoteHub's RDP tabs on the
# admin's PC (this client VM, the console account's session, driven by UI Automation) to the device (the Server VM):
# saved credentials (with and without the NTLM delegation policy), the password prompt helper, redirection (clipboard text / files / image,
# drives), dynamic resolution, Ctrl+Alt+Del and the lock screen, full screen, detach / dock, .rdp export / import (also
# as normaluser), single instance, auto-reconnect after a network cut, the agent installed through the clipboard into
# the RDP session, and the jump host (connections moved under it, an RDP tab through it, an older RemoteHub opening the
# database). The RDP control is an ActiveX host without UI Automation inside: what happens in the session is checked on
# the device (quser, LogonUI / explorer per session, code in PSAdmin's RDP session) and by OCR of the client's screen.
# Client VMs only (2VM + the jump host VM). See docs/test-lab-cases.md sections 10.4, 10.5, 9.13.
# LabRole: console
# TimeLimitMin: 120   (many UI steps with RemoteHub and the device, sign-ins and reconnects: > 60 min)
# LabNeeds: lane, jumphost
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
    # "Allow delegating saved / fresh credentials with NTLM-only server authentication" for TERMSRV/* on this PC (the lab
    # isn't a domain). RemoteHub's tabs sign in without it too (R-RDP-03); the later contexts keep it so that a change in
    # Windows' CredSSP defaults fails R-RDP-03 alone rather than every RDP case.
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
    # What the client shows, written to ui-rdp-<name>.json / .png: a screenshot, whether the editor is open, the top-level
    # windows and RemoteHub's elements (its owned dialogs are listed inside the main window), RemoteHub's process (id and
    # start time - a restart shows), its crash files, errors in the Application log and the end of its diagnostic log.
    # The setup once failed with the editor gone after its page click and nothing recorded to say why.
    function ClientState([string]$Name) {
        Shot $Name
        $s = [ordered]@{ at = (Get-Date).ToString('HH:mm:ss') }
        try { $s.editorOpen = [bool](Ui 'Save' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 1).found } catch { $s.editorOpen = "$_" }
        try { $s.topWindows = TopWindows } catch { $s.topWindows = "$_" }
        try { $s.mainItems = @((Ui '*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 120) } catch { $s.mainItems = "$_" }
        try {
            $s.client = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($u)
                [ordered]@{
                    process = @(Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id) started $($_.StartTime.ToString('HH:mm:ss')) responding $($_.Responding): $($_.MainWindowTitle)" })
                    crash   = @(Get-ChildItem "C:\Users\$u\AppData\Local\RemoteHub\crash" -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) $($_.LastWriteTime.ToString('HH:mm:ss'))" })
                    events  = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-15) } -ErrorAction SilentlyContinue |
                            Where-Object { $_.ProviderName -in 'Application Error', 'Application Hang', '.NET Runtime', 'Windows Error Reporting' -and $_.Message -match 'RemoteHub' } |
                            Select-Object -First 10 | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($_.ProviderName) $($_.Id): $(($_.Message -split "`n" | Select-Object -First 15) -join ' ')" })
                    diagLog = @(Get-Content "C:\Users\$u\AppData\Roaming\RemoteHub\logs\remotehub.log" -Tail 40 -ErrorAction SilentlyContinue)
                }
            }
        }
        catch { $s.client = "$_" }
        Save $Name $s
    }
    # the connection's Edit dialog: -Do picks tabs and values, then Save (as UI-RDP2 / C-REC2). With -Trace the client's
    # state is recorded once the editor is open (ClientState "edit-<trace>-open"); a failing step always records it
    # ("edit-<trace>-fail") before the error goes on.
    function EditConnection([scriptblock]$Do, [string]$Node = 'Device', [string]$Trace = '') {
        $tag = if ($Trace) { "edit-$Trace" } else { 'edit' }
        try {
            Menu 'Edit*' -Node $Node
            $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
            if ($Trace) { ClientState "$tag-open" }
            & $Do
            $null = Ui 'Save' -Title 'Edit connection'
        }
        catch {
            $err = $_
            ClientState "$tag-fail"
            throw $err
        }
        Start-Sleep -Seconds 2
    }
    # Windows' credential dialog of the RDP client, shown within -TimeoutSec (Wait-LabCredentialDialog: UI Automation
    # lists it inside RemoteHub's window with nothing in it, so an element search in it never matches)
    function CredDialog([int]$TimeoutSec = 60, [string]$Account = 'Console') { Wait-LabCredentialDialog -Name $VmName -Account $Account -TimeoutSec $TimeoutSec }
    # RemoteHub running in the console account's session. A context before may have ended with it stopped or with the
    # console still signing in (lab run 20261017-054704: the switch back from normaluser timed out, and every later
    # context found no RemoteHub window).
    function Ensure-Client {
        $up = try { @(TopWindows | Where-Object { $_ -like 'RemoteHub*' -or $_ -like '* RemoteHub' }).Count -gt 0 } catch { $false }
        if ($up) { return }
        Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 300
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        $script:task = Start-LabClient -Name $VmName
    }
    # A connected RDP tab of Device, whatever the contexts before left: the NTLM policy (see Allow-NtlmDelegation), the
    # RDP tab selected if there is one, else a new one - "Remote desktop via RDP" once the agent is the connection's
    # way, "Connect" before.
    function Ensure-RdpTab([int]$TimeoutSec = 90) {
        Allow-NtlmDelegation
        $null = Ui 'Remote desktop' -Value realclick -Optional -TimeoutSec 5
        if (Wait-RdpTab 10) { return $true }
        $r = Ui 'Device' -Value 'menu:Remote desktop via RDP' -Optional
        if (-not $r.found -or ($r.PSObject.Properties['error'] -and $r.error)) { Key 1B; Menu 'Connect' }
        Wait-RdpTab $TimeoutSec
    }
    # The agent on the device, paired with Device. Installed through the RDP clipboard by the A-CLIP context; when that
    # didn't happen (a tag-filtered run, or that install failed) by hand as Install-AgentByHand does. Needs the device on
    # its own network (before the jump host context moves it).
    function Ensure-Agent {
        if ("$((Get-LabAgentState -Name $device.Name).Service.State)" -eq 'Running') { return 'already installed' }
        $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
        Install-AgentByHand -Node 'Device' -TargetVm $device.Name
    }
    # The person at the device ready to answer the agent. Their first call after the device's reset starts the person
    # server, compiles its helper and starts the OCR (slow): done before Connect, not against the consent dialog's 30 s
    # countdown (as in D-CLIP and D-MORE). Lab run 20261021-120638: the agent asked in session 1 and counted it as
    # denied 30 s later, while the first click call found no agent window - A-CLIP-03 had failed that way since
    # 20261016. Quick once the server runs.
    function Ensure-DevicePerson {
        $null = Invoke-LabPerson -Name $device.Name -Account Console -Do windows
        $null = Invoke-LabPerson -Name $device.Name -Account Console -Do screen-read -TimeoutSec 30
    }
    # An agent desktop tab of Device, allowed by the person at the device
    function Ensure-AgentDesktop {
        $null = Ensure-Agent
        # (10 s: in lab run 20261019-193627 3 s missed the open, connected tab after a failed step, so Connect was used
        # and the person waited 60 s for an "Allow" that never came - the device's session was the one already allowed)
        if ((Ui 'Desktop (agent)' -Value exists -Optional -TimeoutSec 10).found) { return $true }
        Ensure-DevicePerson
        Menu 'Connect'
        $null = Ui 'Connect' -Optional -TimeoutSec 15
        $null = Invoke-LabPerson -Name $device.Name -Account Console -Do click -Text 'Allow' -TimeoutSec 60
        (Ui 'Send Ctrl+Alt+Del' -Value exists -Optional -TimeoutSec 60).found
    }
    # the connection's audit log window (Audit log...), its rows as text
    function AuditText([string]$Node = 'Device') {
        Menu 'More>Audit log*' -Node $Node
        $l = Ui '*' -Title 'Audit log*' -Value list -TimeoutSec 30
        $null = Ui 'Close' -Title 'Audit log*' -Optional -TimeoutSec 5
        (@($l.items) -join "`n")
    }
    # RemoteHub's staging folders for agent installs (%TEMP%\RemoteHub-Agent\<12 hex>) of the console account, always as
    # an array: an empty answer unrolled to $null, and @($null).Count is 1 (lab run 20261017-054704: A-CLIP-02 read "1
    # folder left" when there was none)
    function Staged {
        $f = @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($u) @(Get-ChildItem "C:\Users\$u\AppData\Local\Temp\RemoteHub-Agent" -Directory -ErrorAction SilentlyContinue | Where-Object Name -match '^[0-9a-f]{12}$' | ForEach-Object Name)
            } | Where-Object { $_ } | ForEach-Object { [string]$_ })
        , [string[]]$f
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
        # the .rhagent holds RemoteHub's enrollment secret: kept out of the evidence folder
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
    # (Explorer's "Paste" of the RDP clipboard's files: Invoke-LabPaste, which pumps messages while the shell copies -
    # in lab run 20261017-054704 not one file arrived either way with a plain Start-Sleep (UI-CP-10, A-CLIP-01) while
    # text and pictures did)
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
    # a test picture (96 x 64, pattern by __SEED__) and the hash of a picture's pixels (RGB). The picture goes on the
    # clipboard as a plain 24-bit DIB (BI_RGB), as Paint puts one, and is read back from the DIB with its header honoured.
    # Lab runs 20261021-222745 and 20261022-061254: put there with Clipboard.SetImage and read with GetImage, it arrived
    # moved 3 pixels to the right both ways (shiftedEqual "-3=5952/5952") - 12 bytes, the size of the three colour masks
    # of a 32-bit BI_BITFIELDS DIB. SetImage leaves Windows to make the DIB from a 32-bit screen bitmap (BI_BITFIELDS),
    # and somewhere between the RDP clipboard and the bitmap Windows made back from it on the other side the masks were
    # taken for pixels. RemoteHub has no part in that: the RDP tab's clipboard is the RDP control's and rdpclip's.
    $imgLib = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
function New-TestImage([int]$Seed) {
    $b = New-Object System.Drawing.Bitmap 96, 64, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    for ($y = 0; $y -lt 64; $y++) { for ($x = 0; $x -lt 96; $x++) { $b.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(($x * 7 + $y * 3 + $Seed) % 256, ($x * 2 + $y * 11 + $Seed) % 256, ($x * 13 + $y * 5 + $Seed) % 256)) } }
    $b
}
# pixels as 0xRRGGBB, top row first
function Get-Pixels($img) {
    $b = New-Object System.Drawing.Bitmap $img
    $px = New-Object int[] ($b.Width * $b.Height)
    for ($y = 0; $y -lt $b.Height; $y++) { for ($x = 0; $x -lt $b.Width; $x++) { $px[$y * $b.Width + $x] = $b.GetPixel($x, $y).ToArgb() -band 0xFFFFFF } }
    @{ w = $b.Width; h = $b.Height; px = $px }
}
function Get-Hash($p) {
    if (-not $p -or -not $p.px) { return '' }
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $p.px) { [void]$sb.Append(($c -shr 16) -band 0xFF).Append(',').Append(($c -shr 8) -band 0xFF).Append(',').Append($c -band 0xFF).Append(';') }
    "$($p.w)x$($p.h)-" + [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::ASCII.GetBytes($sb.ToString()))).Replace('-', '')
}
function Get-PixelHash($img) { if (-not $img) { return '' }; Get-Hash (Get-Pixels $img) }
# a 24-bit bottom-up DIB (BITMAPINFOHEADER, BI_RGB, rows padded to 4 bytes)
function ConvertTo-Dib($img) {
    $p = Get-Pixels $img
    $stride = [int]([Math]::Floor(($p.w * 3 + 3) / 4) * 4)
    $d = New-Object byte[] (40 + $stride * $p.h)
    [BitConverter]::GetBytes([int]40).CopyTo($d, 0); [BitConverter]::GetBytes([int]$p.w).CopyTo($d, 4); [BitConverter]::GetBytes([int]$p.h).CopyTo($d, 8)
    [BitConverter]::GetBytes([uint16]1).CopyTo($d, 12); [BitConverter]::GetBytes([uint16]24).CopyTo($d, 14); [BitConverter]::GetBytes([int]($stride * $p.h)).CopyTo($d, 20)
    for ($y = 0; $y -lt $p.h; $y++) {
        $o = 40 + ($p.h - 1 - $y) * $stride
        for ($x = 0; $x -lt $p.w; $x++) { $c = $p.px[$y * $p.w + $x]; $d[$o + $x * 3] = $c -band 0xFF; $d[$o + $x * 3 + 1] = ($c -shr 8) -band 0xFF; $d[$o + $x * 3 + 2] = ($c -shr 16) -band 0xFF }
    }
    , $d
}
# a DIB's pixels (24 or 32 bit; after a BITMAPINFOHEADER with BI_BITFIELDS come the three masks) and its header
function Read-Dib([byte[]]$d) {
    if (-not $d -or $d.Length -lt 40) { return @{ header = "$(if ($d) { $d.Length } else { 0 }) bytes"; px = $null } }
    $hs = [BitConverter]::ToInt32($d, 0); $w = [BitConverter]::ToInt32($d, 4); $h = [BitConverter]::ToInt32($d, 8)
    $bits = [int][BitConverter]::ToUInt16($d, 14); $comp = [BitConverter]::ToInt32($d, 16)
    $off = $hs
    if ($hs -eq 40 -and $comp -eq 3) { $off += 12 }
    $top = $h -lt 0; $h = [Math]::Abs($h)
    $stride = [int]([Math]::Floor(($w * $bits + 31) / 32) * 4)
    $r = @{ w = $w; h = $h; px = $null; header = "size $hs, ${w}x$(if ($top) { -$h } else { $h }), $bits bit, compression $comp, $($d.Length) bytes (pixels from $off, $($stride * $h) wanted)" }
    if (($bits -ne 24 -and $bits -ne 32) -or $d.Length -lt $off + $stride * $h) { return $r }
    $bpp = $bits / 8
    $px = New-Object int[] ($w * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $o = $off + $(if ($top) { $y } else { $h - 1 - $y }) * $stride
        for ($x = 0; $x -lt $w; $x++) { $i = $o + $x * $bpp; $px[$y * $w + $x] = ([int]$d[$i + 2] -shl 16) -bor ([int]$d[$i + 1] -shl 8) -bor [int]$d[$i] }
    }
    $r.px = $px
    $r
}
function Get-ClipboardDib {
    $s = [System.Windows.Forms.Clipboard]::GetData([System.Windows.Forms.DataFormats]::Dib)
    if ($s -is [IO.MemoryStream]) { return , $s.ToArray() }
    $null
}
'@
    $code.imageSet = $imgLib + @'
$i = New-TestImage __SEED__
$o = New-Object System.Windows.Forms.DataObject
$o.SetData([System.Windows.Forms.DataFormats]::Dib, $false, (New-Object IO.MemoryStream (, (ConvertTo-Dib $i))))
[System.Windows.Forms.Clipboard]::SetDataObject($o, $true)
$back = Read-Dib (Get-ClipboardDib)
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ hash = (Get-PixelHash $i); onClipboard = (Get-Hash $back); header = $back.header })
'@
    # waits until the clipboard holds the picture with the wanted pixels (or 30 s). When another one is there, also how it
    # differs from the picture sent (made again from __SEED__): the clipboard's formats, the pixels changed, the largest
    # difference in one colour, and whether it is the picture upside down or moved sideways (lab run 20261017-054704:
    # 96 x 64 arrived both ways with other pixels, nothing recorded to tell why)
    $code.imageGet = $imgLib + @'
$want = '__WANT__'; $got = ''; $dib = $null; $err = $null
$until = (Get-Date).AddSeconds(30)
do {
    try { $dib = Read-Dib (Get-ClipboardDib); $got = Get-Hash $dib } catch { $err = "$_" }
    if ($got -ne $want) { Start-Sleep -Milliseconds 700 }
} while ($got -ne $want -and (Get-Date) -lt $until)
$info = [ordered]@{ formats = @(try { [System.Windows.Forms.Clipboard]::GetDataObject().GetFormats() } catch { "error: $_" }) }
if ($got -ne $want) {
    $info.dib = $(if ($dib) { $dib.header } else { 'none' })
    if ($err) { $info.error = $err }
    # the bitmap Windows makes from the DIB (Clipboard.GetImage), for comparison
    $info.imageHash = try { Get-PixelHash ([System.Windows.Forms.Clipboard]::GetImage()) } catch { "error: $_" }
}
if ($got -ne $want -and $dib -and $dib.px) {
    $a = Get-Pixels (New-TestImage __SEED__)
    $info.size = "$($dib.w)x$($dib.h)"
    if ($a.w -eq $dib.w -and $a.h -eq $dib.h) {
        $w = $a.w; $h = $a.h
        $pa = $a.px; $pb = $dib.px
        $changed = 0; $max = 0; $flipped = 0
        for ($i = 0; $i -lt $pa.Length; $i++) {
            $p = $pa[$i]; $q = $pb[$i]
            if ($p -ne $q) {
                $changed++
                foreach ($s in 0, 8, 16) { $dd = [Math]::Abs((($p -shr $s) -band 0xFF) - (($q -shr $s) -band 0xFF)); if ($dd -gt $max) { $max = $dd } }
            }
            if ($pa[($h - 1 - [int][Math]::Floor($i / $w)) * $w + ($i % $w)] -eq $q) { $flipped++ }
        }
        $shifts = @(foreach ($s in -4..4) {
                if ($s -eq 0) { continue }
                $same = 0; $n = 0
                for ($y = 0; $y -lt $h; $y++) { for ($x = [Math]::Max(0, -$s); $x -lt [Math]::Min($w, $w - $s); $x++) { $n++; if ($pa[$y * $w + $x + $s] -eq $pb[$y * $w + $x]) { $same++ } } }
                "$s=$same/$n"
            })
        $info.changed = "$changed of $($pa.Length)"; $info.maxDelta = $max; $info.upsideDownEqual = "$flipped of $($pa.Length)"; $info.shiftedEqual = $shifts
    }
}
'JSON:' + (ConvertTo-Json -Compress -Depth 4 -InputObject @{ hash = $got; info = $info })
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

    if ($isClient) {
        Reset-LabVm -Name $device.Name
        $computer = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            # Remote Desktop on (the firewall group by its resource name - display names are localized)
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            $env:COMPUTERNAME
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # RemoteHub installed, and no NTLM delegation policy (R-RDP-03's precondition). Always from the checkpoint (the
        # run's or the VM's own), never a VM an earlier file used: UI-RDP2, UI-JUMP, C-REC and C-REC2 set that policy
        # and take it out of the registry afterwards, but Windows' CredSSP needn't read it again before a restart - in
        # lab run 20261017-054704 (right after UI-RDP2 on the same VM) the saved credentials went through without it.
        Set-LabVmDirty -Name $VmName
        $null = Initialize-LabVm -Name $VmName -Client
        $script:task = Start-LabClient -Name $VmName
        # credentials for this item, "Local account on the target computer (.\user)" ticked: .\PSAdmin
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
        # no question about the device's self-signed RDP certificate: with "Warn" (the default, AuthenticationLevel 2)
        # the RDP control stops at "The identity of the remote computer cannot be verified" and nothing here answers it
        # (Esc on it ends the tab with "Disconnected (code 263)"). Also goes into the exported .rdp (R-RDP-08).
        # (Each step recorded: in the 2026-10-17 run the editor was gone after the page click, cause unknown.)
        EditConnection -Trace 'setup' {
            $page = Ui 'Security & session' -Title 'Edit connection'
            ClientState 'edit-setup-page'
            Save 'edit-setup-pageclick' $page
            $null = Ui 'Server authentication' -Title 'Edit connection' -Value 'choose:Connect without warning'
        }
    }
}

AfterAll {
    if ($isClient) {
        # the auto logon changed, an older RemoteHub installed, the NTLM policy, .rdp files: the next file starts from
        # the checkpoint; the device: Remote Desktop on, an agent, moved behind the jump host
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        try { Stop-LabJumpHost } catch { }
        try { Restore-LabVmNetwork -Name $device.Name } catch { }
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'ui-rdp-device-logs') } catch { }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-RDP <VmName>' -Tag Client, UI, 'R-RDP', 'UI-CP', 'UI-SAS', 'UI-NET', 'A-CLIP', 'J', 'UI-JUMP' -Skip:(-not $isClient) {

    # R-RDP-03 used to expect Windows' refusal of saved credentials here. Lab runs 20261017-160133 and 20261019-193627
    # (Windows 11 26H2 client from its checkpoint, no CredentialsDelegation key, Server 2025 device outside a domain)
    # both signed in: RemoteHub hands the password to the RDP control itself (IMsRdpClientNonScriptable.ClearTextPassword),
    # and Windows doesn't treat that as a password saved in Credential Manager. So the case checks that the stored
    # password works without the policy - and records CredSSP's built-in defaults on this PC for the evidence.
    Context 'saved credentials without the NTLM delegation policy' {
        BeforeAll {
            $n = [ordered]@{}
            Step $n connect {
                # the precondition on this PC: the policy's values, if any (none expected)
                $n.policy = @(Invoke-LabGuest -Name $VmName -ScriptBlock {
                        $k = Get-Item 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation' -ErrorAction SilentlyContinue
                        if ($k) { @($k.Property | ForEach-Object { "$_=$($k.GetValue($_))" }) }
                    } | Where-Object { $_ } | ForEach-Object { [string]$_ })
                # CredSSP's built-in lists (used when no policy is set): "<list>: <servers>"
                $n.credsspDefaults = @(Invoke-LabGuest -Name $VmName -ScriptBlock {
                        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa\Credssp\PolicyDefaults' -ErrorAction SilentlyContinue |
                            Where-Object { $_.PSChildName -like '*Saved*' -or $_.PSChildName -like '*Fresh*' } |
                            ForEach-Object { $k = $_; "$($k.PSChildName): $(@($k.GetValueNames() | ForEach-Object { $k.GetValue($_) }) -join ', ')" }
                    } | ForEach-Object { [string]$_ })
                Clear-DeviceSessions
                Menu 'Connect'
                $n.connected = Wait-RdpTab 90
                # the tab's overlay (Windows' own text, RdpSession.OnDisconnected) and Windows' credential dialog, if any
                $n.overlay = @((Ui '*' -Value list).items)
                $n.promptShown = CredDialog 5
                $n.prompt = @((Ui '*' -Title 'Windows Security' -Value list -Optional -TimeoutSec 5).items)
                $n.screen = Screen
                Shot 'ntlm'
                $n.device = DeviceState
            }
            foreach ($i in 1..2) { Key 1B; Start-Sleep -Seconds 1 }
            $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            Save 'ntlm' $n
        }

        It 'R-RDP-03 non-domain device, no "Allow delegating saved credentials with NTLM-only" policy: the stored password still signs in - no Windows prompt, no message about saved credentials' -Tag 'R-RDP-03' {
            $n['connect-error'] | Should -BeNullOrEmpty
            @($n.policy) | Should -BeNullOrEmpty -Because 'the precondition: no NTLM delegation policy on this PC'
            "$(@($n.overlay) -join "`n")`n$(@($n.prompt) -join "`n")`n$($n.screen)" | Should -Not -Match 'saved credentials'
            $n.promptShown | Should -BeFalse -Because 'Windows doesn''t ask for the password'
            $n.connected | Should -BeTrue -Because "the tab connects (CredSSP defaults: $(@($n.credsspDefaults) -join ' / '))"
            @($n.device.Rdp).Count | Should -BeGreaterThan 0 -Because 'the device has an active RDP session for PSAdmin'
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
                # asked again once in a new task before the step fails: in lab run 20261022-061254 the first query's task
                # in PSAdmin's RDP session started and gave no answer in 120 s ("task Running"), while whoami just before
                # and the clipboard queries later in the same session answered - the resize was never tried
                function Resolution([string]$Name) {
                    try { InSession $device.Name Admin $code.resolution -TimeoutSec 60 }
                    catch { $c["$Name-first"] = "$_"; InSession $device.Name Admin $code.resolution -TimeoutSec 90 }
                }
                $c.res1 = Resolution res1
                $c.resize = InSession $VmName Console $code.resize @{ W = 860; H = 640 }
                Start-Sleep -Seconds 8
                $c.res2 = Resolution res2
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
                # (lab run 20261017-054704: right after full screen the button's Invoke opened no window, and nothing
                # said why - the click's answer and RemoteHub's state are kept, then a real click is tried)
                $c.detClick = Ui 'Detach into its own window / dock back'
                Start-Sleep -Seconds 3
                $c.detWindows = TopWindows
                if (@($c.detWindows | Where-Object { $_ -eq 'Device' }).Count -eq 0) {
                    ClientState 'detach-invoke'
                    $c.detRealClick = Ui 'Detach into its own window / dock back' -Value realclick
                    Start-Sleep -Seconds 3
                    $c.detWindows = TopWindows
                }
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

        It 'R-RDP-02 local account .\PSAdmin: signed in as [DEVICE]\PSAdmin' -Tag 'R-RDP-02' {
            "$($c.whoami)" | Should -Be "$computer\$($device.AdminAccount)"
        }

        It 'R-RDP-06 dynamic resolution: the device session follows the resized window (even width, not larger than the window)' -Tag 'R-RDP-06' {
            $c['resolution-error'] | Should -BeNullOrEmpty
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
            # as after the context before (a tag-filtered run starts here)
            Allow-NtlmDelegation
            $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            Clear-DeviceSessions
            # "Always prompt for password upon connection" on the device (read when Remote Desktop Services start)
            Invoke-LabGuest -Name $device.Name -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
                $null = New-Item $k -Force
                Set-ItemProperty $k -Name fPromptForPassword -Value 1 -Type DWord
            }
            Restart-LabRdpService -Name $device.Name
            Step $p prompt {
                Menu 'Connect'
                $p.dialog = CredDialog 60
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
                $p.dialog2 = CredDialog 60
                Start-Sleep -Seconds 3
                Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x56
                Start-Sleep -Milliseconds 500
                Send-LabKey -Name $VmName -Key Enter
                $p.connected = Wait-RdpTab 60
                $p.clip3 = InSession $VmName Console $code.clipInfo
                Shot 'prompt-pasted'
            }
            Step $p audit {
                # a dialog still open is modal: RemoteHub's context menu stays empty behind it
                if (CredDialog 2) { $p.dialogLeft = $true; Key 1B; Start-Sleep -Seconds 2 }
                $p.audit = AuditText
            }
            # the policy off again and Remote Desktop Services restarted, so the next contexts connect without a prompt;
            # the restart ends the session, so the tab is closed first and opened again afterwards (the next context
            # expects a connected tab)
            $null = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5
            Invoke-LabGuest -Name $device.Name -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services' -Name fPromptForPassword -ErrorAction SilentlyContinue }
            Restart-LabRdpService -Name $device.Name
            # a session left at the device's password prompt would take the next connection (run 20261025-132336)
            Clear-DeviceSessions
            Step $p reconnect {
                Menu 'Connect'
                $p.reconnected = Wait-RdpTab 60
            }
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
            $x.tab = Ensure-RdpTab
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
                $x.pasteIn = Invoke-LabPaste -Name $device.Name -Account Admin -Folder $cpIn -Count 3 -TimeoutSec 240 -Fresh
                $x.filesIn = @($x.pasteIn.files)
                $x.filesBack = @(InSession $device.Name Admin $code.filesCopy @{ DIR = $cpOut })
                $x.pasteBack = Invoke-LabPaste -Name $VmName -Account Console -Folder $cpIn -Count 3 -TimeoutSec 240 -Fresh
                $x.filesBackIn = @($x.pasteBack.files)
            }
            Step $x image {
                # (each side's own DIB read back too: onClipboard equals hash when the picture went on as meant)
                $x.imgSet = InSession $VmName Console $code.imageSet @{ SEED = 1 }
                $x.imgOut = $x.imgSet.hash
                # (not $r: that is Step's $R - names ignore case, and it only takes a dictionary; lab run 20261019-193627:
                # "Cannot convert ... to type System.Collections.IDictionary", the pictures were never compared)
                $got = InSession $device.Name Admin $code.imageGet @{ WANT = $x.imgOut; SEED = 1 }
                $x.imgIn = $got.hash; $x.imgInInfo = $got.info
                $x.imgBackSet = InSession $device.Name Admin $code.imageSet @{ SEED = 77 }
                $x.imgBack = $x.imgBackSet.hash
                $got = InSession $VmName Console $code.imageGet @{ WANT = $x.imgBack; SEED = 77 }
                $x.imgBackIn = $got.hash; $x.imgBackInfo = $got.info
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
            @($x.filesIn | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) | Should -Be @($x.filesOut | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) -Because "the device's clipboard offered: $(@($x.pasteIn.formats) -join ', ') ($($x.pasteIn.apartment))"
            @($x.filesBackIn | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) | Should -Be @($x.filesBack | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Hash)" }) -Because "this PC's clipboard offered: $(@($x.pasteBack.formats) -join ', ') ($($x.pasteBack.apartment))"
            @($x.filesIn).Count | Should -Be 3
        }

        It 'UI-CP-11 a picture both ways through the RDP tab: pixels equal' -Tag 'UI-CP-11' {
            $x.imgOut | Should -Not -BeNullOrEmpty
            $x.imgIn | Should -Be $x.imgOut -Because "the device's picture should be the one sent ($(($x.imgInInfo | ConvertTo-Json -Compress -Depth 4)))"
            $x.imgBackIn | Should -Be $x.imgBack -Because "this PC's picture should be the one sent ($(($x.imgBackInfo | ConvertTo-Json -Compress -Depth 4)))"
        }

        It 'R-RDP-05 clipboard both ways; \\tsclient\C only with "Local drives" on' -Tag 'R-RDP-05' {
            $x.atDevice | Should -Be $x.want
            $x.atClient | Should -Be $x.back
            $x.drivesOff | Should -BeFalse
            $x.reconnected | Should -BeTrue
            $x.drivesOn | Should -BeTrue
        }
    }

    # also tagged R-RDP-08: the context 'the exported .rdp opened as normaluser' opens the file this one writes
    Context '.rdp export and a second start with the file' -Tag 'R-RDP-08' {
        BeforeAll {
            $e = [ordered]@{}
            Invoke-LabGuest -Name $VmName -ScriptBlock { $null = New-Item 'C:\lab' -ItemType Directory -Force; Remove-Item 'C:\lab\device.rdp' -ErrorAction SilentlyContinue }
            # as after the contexts before (a tag-filtered run starts here): the NTLM policy for the saved credentials, and
            # "Local drives" on, which R-RDP-08 expects in the file (the clipboard context switches it on)
            Allow-NtlmDelegation
            Step $e drives {
                EditConnection {
                    $null = Ui 'Local resources & keyboard' -Title 'Edit connection'
                    $null = Ui 'Local drives' -Title 'Edit connection' -Value 'choose:On'
                }
            }
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
            # the audit entry of the claim (MainWindow.AgentClaim.cs Pin)
            $claimed = 'Claimed\W+installed by hand, certificate SHA-256'
            $a.tab = Ensure-RdpTab
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
                $a.paste = Invoke-LabPaste -Name $device.Name -Account Admin -Folder 'C:\ProgramData\RemoteHubLab\session\agent-clip' -Count 2 -TimeoutSec 240 -Fresh
                $a.pasted = @($a.paste.files)
                $a.msi = InSession $device.Name Admin $code.msiInSession @{ DIR = 'C:\ProgramData\RemoteHubLab\session\agent-clip' } -Elevated -TimeoutSec 600
                $a.state = Get-LabAgentState -Name $device.Name
                # RemoteHub claims the agent every 10 s (MainWindow.AgentClaim.cs WaitForClaim) and writes "Agent installed"
                # with "Claimed - installed by hand, certificate SHA-256 ..." when it has (the "answered as RemoteHub Agent"
                # entry went with enrollment without a shared device key - lab run 20261022-061254 found the new entry and
                # waited 150 s for the old one)
                $until = (Get-Date).AddSeconds(150)
                do { Start-Sleep -Seconds 15; $a.audit = AuditText } while ($a.audit -notmatch $claimed -and (Get-Date) -lt $until)
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
            @($a.pasted).Count | Should -Be 2 -Because "the device's clipboard offered: $(@($a.paste.formats) -join ', ') ($($a.paste.apartment))"
            "$($a.msi.exit)" | Should -BeIn '0', '3010'
            $a.state.Service.State | Should -Be 'Running'
            $a.audit | Should -Match $claimed
        }

        It 'A-CLIP-02 the staging folder stays while RemoteHub runs, is gone after a normal exit and after the start that follows a crash' -Tag 'A-CLIP-02' {
            @($a.staged).Count | Should -BeGreaterThan 0
            @($a.afterExit).Count | Should -Be 0
            @($a.beforeKill).Count | Should -BeGreaterThan 0
            @($a.afterKill).Count | Should -BeGreaterThan 0 -Because 'a killed RemoteHub cleans nothing'
            @($a.afterRestart).Count | Should -Be 0
        }
    }

    Context 'asked before the enrollment secret goes through a desktop tab' {
        BeforeAll {
            $k = [ordered]@{}
            # the agent from the context before - installed by hand when that didn't happen (lab run 20261017-054704: the
            # clipboard install's paste failed, so Connect opened RDP again and nobody was asked)
            Step $k agent { $k.agent = Ensure-Agent }
            Step $k desktop {
                # with the agent installed, Connect opens the agent desktop; the person at the device allows it
                Ensure-DevicePerson
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
                # Dialogs.Confirm "Copy the enrollment secret?" - its window name is the message
                $k.question = Message -Title 'The remote desktop tab of*' -Button 'Cancel' -TimeoutSec 60
                Start-Sleep -Seconds 2
                $k.report = (@((Ui '*' -Title 'Install RemoteHub Agent' -Value list -TimeoutSec 20).items) -join "`n")
                $null = Ui 'Close' -Title 'Install RemoteHub Agent' -Optional -TimeoutSec 5
                $k.clipboard = @(InSession $VmName Console $code.clipFiles)
            }
            Shot 'ask-key'
            Save 'ask-key' $k
        }

        It 'A-CLIP-03 an agent desktop tab to Device is open: "Copy the enrollment secret?" names it and DeviceA.rhagent; Cancel copies nothing' -Tag 'A-CLIP-03' {
            $k.desktop | Should -BeTrue
            $k.question | Should -Match 'The remote desktop tab of Device sends everything copied on this PC'
            $k.question | Should -Match 'DeviceA\.rhagent'
            $k.question | Should -Match "which holds RemoteHub's enrollment secret"
            $k.question | Should -Match 'Close that tab first, or copy anyway\?'
            $k.report | Should -Match 'Cancelled\W+close the remote desktop tab, then try again\.'
            # DeviceA's file only: Device.rhagent may still be on the clipboard from Device's own install (Ensure-Agent
            # by hand, or A-CLIP-02's crash step) - lab run 20261021-135850
            @($k.clipboard | Where-Object { (Split-Path $_ -Leaf) -eq 'DeviceA.rhagent' }) | Should -BeNullOrEmpty
        }
    }

    Context 'copied in an RDP tab, pasted through an agent tab' {
        BeforeAll {
            $u = [ordered]@{}
            $u.text = $text + ' across'
            # the agent desktop tab of the context before (a tag-filtered run, or one that failed there, has none)
            Step $u desktop { $u.desktop = Ensure-AgentDesktop }
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
                if ($u.atB.sha256 -ne (Sha $u.text)) {
                    # Lab run 20261019-193627: the console session's clipboard stayed empty while the agent tab was live.
                    # Which half failed: did the copy reach this PC's clipboard through the RDP tab (read by another
                    # program, as Windows' own paste would), and did RemoteHub send it ("text copied on this PC sent" or
                    # "clipboard couldn't be read" in its log)?
                    $u.atPc = ((InSession $VmName Console $code.textGet @{ B64 = (B64 $u.text) }).b64 -eq (B64 $u.text))
                    $u.diag = @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                            param($a) @(Get-Content "C:\Users\$a\AppData\Roaming\RemoteHub\logs\remotehub.log" -Tail 60 -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Agent desktop|clipboard' } | ForEach-Object { [string]$_ })
                        } | ForEach-Object { [string]$_ })
                }
            }
            Save 'across' $u
        }

        It 'UI-CP-12 a text copied in the RDP session arrives through the agent tab in the other session' -Tag 'UI-CP-12' {
            $u.connected | Should -BeTrue
            $u.atB.sha256 | Should -Be (Sha $u.text) -Because "the text should come through the agent tab (on this PC's clipboard: $(if ($u.Contains('atPc')) { $u.atPc } else { 'not checked' }); RemoteHub's log: $(@($u['diag']) -join ' / '))"
        }
    }

    Context 'RDP auto-reconnect after a network cut' {
        BeforeAll {
            $w = [ordered]@{}
            $logFile = "C:\Users\$($vm.ConsoleAccount)\AppData\Roaming\RemoteHub\logs\remotehub.log"
            function DiagLines { @(Invoke-LabGuest -Name $VmName -ArgumentList $logFile -ScriptBlock { param($f) if (Test-Path $f) { @(Get-Content $f) } }) }
            Step $w cut {
                $w.before = Ensure-RdpTab
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
                $o.prompt = CredDialog 60 User
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
            # Admin back at the console for the rest of the file. A slow sign-in isn't this context's failure: written
            # down, then waited for once more (lab run 20261017-054704: "no desktop for PSAdmin after 300 s", the
            # desktop was there a few minutes later, and every later context found no RemoteHub)
            try { Set-LabAutoLogon -Name $VmName -Account Console }
            catch { $o.backToConsole = "$_" }
            Save 'rdp-file' $o
            $script:task = $null
            Ensure-Client
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

    # also tagged J-12: the context 'an older RemoteHub opens the database' reads the jump host this one sets up
    Context 'a jump host in the tree' -Tag 'J-12' {
        BeforeAll {
            $j = [ordered]@{}
            # what the contexts before leave (a tag-filtered run starts here): RemoteHub running, the NTLM policy for the
            # saved credentials (RDP to Behind), and Device's agent - "Move under > Device" (J-11) offers only a
            # connection with an agent; installed before the device moves behind the jump host
            Step $j client { Ensure-Client }
            Allow-NtlmDelegation
            Step $j agent { $j.agent = Ensure-Agent }
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
                # Ctrl+N puts a new connection where the selection is: select a top-level item first (not Behind)
                $null = Ui 'Device' -Value realclick
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

        It 'J-11 moved under a jump host, under another one and out again: the route follows; Behind (no agent, no children) isn''t offered, moving a jump host under another is switched off with the reason' -Tag 'J-11' {
            $j.h1.Jump | Should -Be ($j.h0.Jump + 1)
            $j.offer.error | Should -Match "no menu entry 'Behind'"
            @($j.offer.entries) | Should -Contain 'Device'
            $j.h2.Jump | Should -Be $j.h0.Jump
            $j.h2.Device | Should -Be ($j.h0.Device + 1)
            # the entry is offered but switched off, its tooltip says why (TreeLayout: one jump host only)
            "$($j.jumpUnder.error)" | Should -Match 'is disabled'
            "$($j.jumpUnder.help)" | Should -Match 'jump host itself|Only one jump host'
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
