# UI-JUMP - jump hosts in RemoteHub's own window (docs/test-lab-cases.md section 9.13 and 10.5), driven by UI
# Automation in the console account's session of this client VM.
# Covers: J-15, J-16, J-17, J-18, J-19, J-20, J-21, J-22, J-23, J-24, J-25, UI-JUMP-02.
# Topology as in UI-RDP's jump context and J-JUMP: the jump host VM (lab.json "JumpHost", Start-LabJumpHost) gets an
# agent paired by RemoteHub itself (its clipboard install, the files run there by hand), the Server VM (the device) is
# moved behind it (Move-LabVmBehindJump) and reached only through it. In the tree: JumpHost (its account without a
# stored password - J-18), Behind (the device) under it, plus folders, connections and shares made here.
# Needs: a client VM, the Server VM, the jump host VM. The jump VM is turned off and the device's network restored at
# the end (AfterAll). The tree's geometry comes from UI Automation rectangles (a local probe, $probeCode); drag and drop
# is real mouse input in the session (mouse_event), a dropped .rdp file comes from a small WinForms drag source there.
# LabRole: console
# TimeLimitMin: 120   (the jump host / several RDP sign-ins and reconnects: > 60 min)
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
    # the results of every context (filled by their BeforeAll, read by the Its of later contexts too)
    $R = [ordered]@{}

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "ui-jump-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-jump-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of an item's context menu (right-click and the entry in one step; "A>B" = submenu, a parent that isn't
    # there is skipped)
    function Menu([string]$Entry, [string]$Node, [int]$TimeoutSec = 30) {
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu ${Node}: $($m.error) - entries: $(@($m.entries) -join ', ')" }
        $m
    }
    # A message box, found by its text (-Like, wildcards): its window name is the message text; if not, the dialog that
    # shows that text. Answers the window name and everything in it, then clicks -Button.
    function Box([string]$Like, [string]$Button = 'OK', [int]$TimeoutSec = 60) {
        $title = $Like
        $m = Ui '*' -Title $Like -Value list -TimeoutSec $TimeoutSec -Optional
        if (-not $m.found) {
            $e = Ui $Like -Title '?*' -Value exists -TimeoutSec 5 -Optional
            $title = if ($e.found) { $e.window } else { $null }
            if ($title) { $m = Ui '*' -Title $title -Value list -TimeoutSec 5 -Optional }
        }
        if ($title -and $m.found) {
            $b = Ui $Button -Title $title -Optional -TimeoutSec 10
            # no such button (another dialog than expected - "Delete all" on a connection without items in the run of
            # 16.10.2026 13:55): closed anyway, so it doesn't block the next step's menus
            if (-not $b.found) { foreach ($x in 'Cancel', 'Close', 'OK') { if ((Ui $x -Title $title -Optional -TimeoutSec 2).found) { break } } }
        }
        "$($m.window)`n" + (@($m.items) -join "`n")
    }
    # RemoteHub's dialogs still open (owned windows of the main window), closed through UI Automation's WindowPattern.
    # A modal one left by an earlier step blocks every right-click menu (the run of 16.10.2026 13:55: the jump host's
    # password dialog of the first step stayed open, the tree was then built in the wrong place and most cases failed).
    $dialogCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
$TS = [Windows.Automation.TreeScope]
$isWin = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
$closed = @()
foreach ($m in @($AE::RootElement.FindAll($TS::Children, $isWin) | Where-Object { $_.Current.Name -like 'RemoteHub*' -or $_.Current.Name -like '* RemoteHub' })) {
    foreach ($w in @($m.FindAll($TS::Descendants, $isWin))) {
        $p = $null
        if ($w.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { $closed += "$($w.Current.Name)"; try { $p.Close() } catch { } }
    }
}
@{ closed = @($closed) }
'@
    function Clear-Dialogs { try { @((InSession $dialogCode -TimeoutSec 60).closed) } catch { @("check failed: $_") } }
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    # each section on its own: a failing step is written down (error, screenshot, open dialogs) and the next one runs;
    # dot-sourced, so the result dictionary is filled
    # Every step starts without dialogs left by an earlier one (written down as "<step>-leftover").
    function Step([System.Collections.IDictionary]$Into, [string]$Name, [scriptblock]$Do) {
        $left = @(Clear-Dialogs)
        if ($left.Count) { $Into["$Name-leftover"] = $left; Start-Sleep -Seconds 1 }
        try { . $Do }
        catch {
            $Into["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $Into["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
        }
    }
    # code in the console account's session; placeholders __NAME__ from -Values; the code's last value (a hashtable)
    # comes back through a file
    function InSession([string]$Code, [hashtable]$Values = @{}, [int]$TimeoutSec = 120, [string]$On = $VmName, [string]$Account = 'Console') {
        foreach ($k in $Values.Keys) { $Code = $Code.Replace("__$($k)__", [string]$Values[$k]) }
        $out = "C:\ProgramData\RemoteHubLab\session\ui-jump-$([guid]::NewGuid().ToString('N')).json"
        $text = "`$r = & {`n$Code`n}`n`$j = if (`$null -eq `$r) { 'null' } else { ConvertTo-Json -InputObject `$r -Depth 8 -Compress }`nSet-Content -LiteralPath '$out' -Encoding UTF8 -Value `$j"
        $raw = Invoke-LabInSession -Name $On -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($text))
        $answer = Invoke-LabGuest -Name $On -ArgumentList $out -ScriptBlock { param($f) if (Test-Path $f) { Get-Content -LiteralPath $f -Raw; Remove-Item -LiteralPath $f } }
        if (-not $answer) { throw "session code gave no answer: $raw" }
        $answer | ConvertFrom-Json
    }
    function B64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
    function Items { @((Ui '*' -Value list).items) }

    # UI Automation details RhPerson's uia doesn't answer: per pattern (name or tooltip, wildcards) every match in the
    # window (-Title like RhPerson) with type, tooltip (HelpText), state (selection / toggle, also of the parent - a radio
    # button's text), screen rectangle, and the rectangle and texts of the tree item it is in
    $probeCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$q = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__ARGS__')) | ConvertFrom-Json
$AE = [Windows.Automation.AutomationElement]
$TS = [Windows.Automation.TreeScope]
$all = [Windows.Automation.Condition]::TrueCondition
$walker = [Windows.Automation.TreeWalker]::ControlViewWalker
function Rect($e) { if (-not $e) { return $null }; $r = $e.Current.BoundingRectangle; if ($r.IsEmpty) { return $null }; @{ x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height } }
function Up($e, [string]$type) {
    $p = $walker.GetParent($e)
    while ($p) {
        if ([Windows.Automation.Automation]::Compare($p, $AE::RootElement)) { return $null }
        if ($p.Current.ControlType.ProgrammaticName -eq "ControlType.$type") { return $p }
        $p = $walker.GetParent($p)
    }
    $null
}
function State($e, $o) {
    $pt = $null; if ($e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pt)) { $o.selected = $pt.Current.IsSelected }
    $pt = $null; if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pt)) { $o.toggle = [string]$pt.Current.ToggleState }
}
$tops = @($AE::RootElement.FindAll($TS::Children, $all))
$mains = @($tops | Where-Object { $_.Current.Name -like 'RemoteHub*' -or $_.Current.Name -like '* RemoteHub' })
$isWin = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
$scope = if ($q.title -and $q.title -ne 'RemoteHub*') { @(@($mains | ForEach-Object { @($_.FindAll($TS::Descendants, $isWin)) }) + $tops | Where-Object { $_.Current.Name -like $q.title }) } else { $mains }
$win = @($scope)[0]
$els = @(if ($win) { foreach ($e in $win.FindAll($TS::Descendants, $all)) { @{ e = $e; n = $e.Current.Name; h = $e.Current.HelpText } } })
$out = [ordered]@{ window = $(if ($win) { $win.Current.Name } else { $null }) }
foreach ($p in @($q.texts)) {
    $out[$p] = @(foreach ($x in $els) {
            if (-not ($x.n -like $p -or ($x.h -and $x.h -like $p))) { continue }
            $e = $x.e
            $o = [ordered]@{ name = $x.n; help = $x.h; type = ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''); enabled = $e.Current.IsEnabled; offscreen = $e.Current.IsOffscreen; rect = (Rect $e) }
            State $e $o
            if (-not $o.Contains('selected') -and -not $o.Contains('toggle')) { $par = $walker.GetParent($e); if ($par) { State $par $o } }
            $ti = Up $e 'TreeItem'
            if ($ti) { $o.treeItem = Rect $ti; $o.row = @($ti.FindAll($TS::Descendants, $all) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 30) }
            $o
        })
}
$out
'@
    function Probe([string[]]$Texts, [string]$Title = 'RemoteHub*') {
        $a = ConvertTo-Json -Compress -InputObject @{ texts = @($Texts); title = $Title }
        InSession $probeCode @{ ARGS = (B64 $a) }
    }
    function Hits($probe, [string]$Text) { $p = $probe.PSObject.Properties[$Text]; if ($p) { @($p.Value) } else { @() } }
    # the match that is in the navigation tree
    function TreeHit($probe, [string]$Text) { @(Hits $probe $Text | Where-Object { $_.treeItem }) | Select-Object -First 1 }
    # the middle of a tree item's text: the name's center
    function Center($probe, [string]$Name) {
        $n = TreeHit $probe $Name
        if (-not $n -or -not $n.rect) { throw "not in the tree: $Name" }
        @([int]($n.rect.x + [math]::Min($n.rect.w / 2, 30)), [int]($n.rect.y + $n.rect.h / 2))
    }
    # the vertical middle of a connection's row (name line and address line): a drop there means "into"
    function Mid($probe, [string]$Name, [string]$Address) {
        $n = TreeHit $probe $Name
        if (-not $n -or -not $n.rect) { throw "not in the tree: $Name" }
        $a = @(Hits $probe $Address | Where-Object { $_.treeItem -and $_.rect -and $_.treeItem.y -eq $n.treeItem.y }) | Select-Object -First 1
        $y = if ($a) { [int](($n.rect.y + $a.rect.y + $a.rect.h) / 2) } else { [int]($n.rect.y + $n.rect.h) }
        @([int]($n.rect.x + [math]::Min($n.rect.w / 2, 30)), $y)
    }
    # the tooltips RemoteHub shows right now (its top-level windows other than the main window, e.g. a ToolTip popup)
    $tipCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [Windows.Automation.AutomationElement]
$TS = [Windows.Automation.TreeScope]
$all = [Windows.Automation.Condition]::TrueCondition
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace RhLab -Name Tip -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);'
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function Go([int]$x, [int]$y) { [RhLab.Tip]::mouse_event(0x8001, [int]($x * 65535 / ($b.Width - 1)), [int]($y * 65535 / ($b.Height - 1)), 0, [IntPtr]::Zero) }
# the pointer onto the item from here, then the tooltip looked for until it shows (WPF hides it again after 5 s: read
# from the lab host after a new session task it was gone - empty in the run of 16.10.2026 13:55)
Go (__X__ + 4) (__Y__ + 1); Start-Sleep -Milliseconds 300; Go __X__ __Y__
$ids = @(Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object Id)
$tips = @()
$until = (Get-Date).AddSeconds(8)
do {
    Start-Sleep -Milliseconds 400
    $tips = @()
    foreach ($t in $AE::RootElement.FindAll($TS::Children, $all)) {
        $c = $t.Current
        if ($ids -notcontains $c.ProcessId) { continue }
        if ($c.Name -like 'RemoteHub*' -or $c.Name -like '* RemoteHub') { continue }
        $tips += @($c.Name) + @($t.FindAll($TS::Descendants, $all) | ForEach-Object { $_.Current.Name })
    }
    $tips = @($tips | Where-Object { $_ })
} while (-not $tips.Count -and (Get-Date) -lt $until)
Go 1010 700
@{ tips = $tips }
'@
    # the tooltip of a tree item: the item clicked, the pointer moved onto its name in the session ($tipCode)
    function Tip([string]$Text) {
        $h = TreeHit (Probe @($Text)) $Text
        if (-not $h -or -not $h.rect) { return "not in the tree: $Text" }
        $x = [int]($h.rect.x + [math]::Min($h.rect.w / 2, 20)); $y = [int]($h.rect.y + $h.rect.h / 2)
        $null = Ui $Text -Value realclick
        $t = InSession $tipCode @{ X = $x; Y = $y }
        (@($t.tips) -join "`n")
    }
    # drag and drop with the left button held, real input in the session (WPF's drag starts after a few pixels)
    $dragCode = @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace RhLab -Name Drag -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);'
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function Go([int]$x, [int]$y) { [RhLab.Drag]::mouse_event(0x8001, [int]($x * 65535 / ($b.Width - 1)), [int]($y * 65535 / ($b.Height - 1)), 0, [IntPtr]::Zero) }
$x0 = __X0__; $y0 = __Y0__; $x1 = __X1__; $y1 = __Y1__
Go $x0 $y0; Start-Sleep -Milliseconds 300
[RhLab.Drag]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 200
for ($i = 1; $i -le 30; $i++) { Go ([int]($x0 + ($x1 - $x0) * $i / 30)) ([int]($y0 + ($y1 - $y0) * $i / 30)); Start-Sleep -Milliseconds 40 }
Go $x1 $y1; Start-Sleep -Milliseconds 700
[RhLab.Drag]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 500
@{ dragged = $true }
'@
    function Drag([int[]]$From, [int[]]$To) {
        $null = InSession $dragCode @{ X0 = $From[0]; Y0 = $From[1]; X1 = $To[0]; Y1 = $To[1] }
        Start-Sleep -Seconds 2
    }
    # A file dragged from another program (Explorer's way: an OLE drag with a file list): a small window in the
    # session starts DoDragDrop when pressed; a second process presses there and moves the pointer to the target.
    $dragFileCode = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$files = New-Object System.Collections.Specialized.StringCollection
[void]$files.Add('__FILE__')
$data = New-Object System.Windows.Forms.DataObject
$data.SetFileDropList($files)
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = 'None'; $f.StartPosition = 'Manual'; $f.ShowInTaskbar = $false; $f.TopMost = $true
$f.Location = New-Object System.Drawing.Point(__SX__, __SY__); $f.Size = New-Object System.Drawing.Size(60, 60); $f.BackColor = [System.Drawing.Color]::Orange
$script:effect = 'none'
$f.Add_MouseDown({ $script:effect = [string]$f.DoDragDrop($data, [System.Windows.Forms.DragDropEffects]::Copy); $f.Close() })
$t = New-Object System.Windows.Forms.Timer; $t.Interval = 40000; $t.Add_Tick({ $f.Close() }); $t.Start()
$f.Add_Shown({ $null = Start-Process powershell.exe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', '__MOVER__' })
[System.Windows.Forms.Application]::Run($f)
@{ effect = $script:effect }
'@
    $moverCode = @'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace RhLab -Name Mv -MemberDefinition '[DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);'
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function Go([int]$x, [int]$y) { [RhLab.Mv]::mouse_event(0x8001, [int]($x * 65535 / ($b.Width - 1)), [int]($y * 65535 / ($b.Height - 1)), 0, [IntPtr]::Zero) }
$x0 = __X0__; $y0 = __Y0__; $x1 = __X1__; $y1 = __Y1__
Start-Sleep -Milliseconds 1500
Go $x0 $y0; Start-Sleep -Milliseconds 300
[RhLab.Mv]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 300
for ($i = 1; $i -le 30; $i++) { Go ([int]($x0 + ($x1 - $x0) * $i / 30)) ([int]($y0 + ($y1 - $y0) * $i / 30)); Start-Sleep -Milliseconds 50 }
Go $x1 $y1; Start-Sleep -Milliseconds 800
[RhLab.Mv]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
'@
    function DragFile([string]$File, [int[]]$To) {
        $sx = 900; $sy = 640
        $mover = $moverCode.Replace('__X0__', "$($sx + 30)").Replace('__Y0__', "$($sy + 30)").Replace('__X1__', "$($To[0])").Replace('__Y1__', "$($To[1])")
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($mover))
        $r = InSession $dragFileCode @{ FILE = $File; SX = $sx; SY = $sy; MOVER = $enc } -TimeoutSec 90
        Start-Sleep -Seconds 2
        $r
    }
    # The RDP tab is connected: its header says "Click into the session to type" or "Keyboard -> remote session"
    function Wait-RdpTab([int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = Invoke-LabUi -Name $VmName -Text '*' -Value list -Optional -TimeoutSec 5
            if (@(@($l.items) | Where-Object { $_ -match '\|(Click into the session to type|Keyboard\W+remote session)\|' }).Count -gt 0) { return $true }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $false
    }
    # every UI call costs 15-20 s (session task, UI Automation): stops at the first lookup that finds no tab
    function Close-Tabs {
        foreach ($i in 1..3) { $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 3; if (-not $c.found) { break }; Start-Sleep -Seconds 2 }
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
    # the device's RDP certificate (thumbprint)
    function RdpThumb { Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-CimInstance -Namespace root\cimv2\TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'").SSLCertificateSHA1Hash } }
    # "Allow delegating saved / fresh credentials with NTLM-only server authentication" for TERMSRV/* (the lab isn't a
    # domain - as UI-RDP)
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
    # (the tree, paired agents, settings), the dropped .rdp files and the NTLM delegation policy. Throws when something
    # stays.
    function Clear-ClientLeftovers {
        $left = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
            param($user)
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            $paths = "C:\Users\$user\AppData\Local\RemoteHub", "C:\Users\$user\AppData\Roaming\RemoteHub",
                "C:\Users\$user\Videos\RemoteHub", 'C:\Users\Public\dropA.rdp', 'C:\Users\Public\dropB.rdp',
                'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation'
            foreach ($p in $paths) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
            @($paths | Where-Object { Test-Path $_ })
        }
        if (@($left).Count) { throw "${VmName}: still there: $(@($left) -join ', ')" }
    }
    # The agent paired with RemoteHub on a VM without WinRM (as UI-RDP): RemoteHub's clipboard install, the two staged
    # files taken from this PC's clipboard to the VM over PowerShell Direct and msiexec run there. RemoteHub's
    # WaitForManualInstall then marks the connection installed.
    function Install-AgentByHand([string]$Node, [string]$TargetVm) {
        $o = [ordered]@{}
        $null = Menu 'RemoteHub Agent>[IU]* RemoteHub Agent*' -Node $Node
        $t = '*RemoteHub Agent'
        $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title $t -Value check -TimeoutSec 60
        $b = Ui 'Install' -Title $t -Optional -TimeoutSec 3
        if (-not $b.found) { $null = Ui 'Update' -Title $t }
        $null = Ui 'Done' -Title $t -Value exists -TimeoutSec 60
        $o.report = (@((Ui '*' -Title $t -Value list).items) -join "`n")
        $null = Ui 'Done' -Title $t -Optional
        $files = @((InSession @'
Add-Type -AssemblyName System.Windows.Forms
@{ files = @([System.Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { [string]$_ }) }
'@).files)
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
    # New folder (Ctrl+Shift+N) in the folder of the selected item, optionally with an account
    function New-Folder([string]$Display, [string]$User, [string]$Pw) {
        $null = Ui 'SearchBox' -Value 'keys:^+n'
        $null = Ui 'Display name' -Title 'New folder' -Value "setnext:$Display"
        if ($User) {
            $null = Ui 'Credentials' -Title 'New folder'
            $null = Ui 'Enter credentials for this item' -Title 'New folder' -Value check
            $null = Ui 'Username' -Title 'New folder' -Value "setnext:$User"
            $null = Ui 'Local account on the target computer*' -Title 'New folder' -Value check -Optional
            if ($Pw) { $null = Ui 'Password' -Title 'New folder' -Value "setnext:$Pw" }
        }
        $null = Ui 'Save' -Title 'New folder'
        $null = Ui $Display -Value exists
    }
    # the share editor (ShareEditorWindow), as in S-SHARE2
    function Set-ShareEditor([string]$Title, [hashtable]$S) {
        $null = Ui 'Name' -Title $Title -Value 'exists' -TimeoutSec 30
        if ($S.ContainsKey('Name')) { $null = Ui 'Name' -Title $Title -Value "setnext:$($S.Name)" }
        if ($S.ContainsKey('Type')) { $null = Ui 'Type' -Title $Title -Value "choose:$($S.Type)" }
        if ($S.ContainsKey('Host')) { $null = Ui 'Host' -Title $Title -Value "setnext:$($S.Host)" }
        if ($S.ContainsKey('Path')) { $null = Ui $S.PathLabel -Title $Title -Value "setnext:$($S.Path)" }
    }

    if ($isClient) {
        Reset-LabVm -Name $device.Name
        $computer = Invoke-LabGuest -Name $device.Name -ArgumentList $device.AdminAccount -ScriptBlock {
            param($admin)
            # Remote Desktop on (the firewall group by its resource name - display names are localized)
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            # an SMB share for the share tab through the jump host
            $null = New-Item C:\rhjump -ItemType Directory -Force
            Set-Content C:\rhjump\hello.txt 'reached through the jump host'
            if (-not (Get-SmbShare -Name rhjump -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name rhjump -Path C:\rhjump -FullAccess $admin }
            $null = icacls C:\rhjump /grant "${admin}:(OI)(CI)M"
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue   # File and Printer Sharing
            $env:COMPUTERNAME
        }
        # RemoteHub installed (kept from an earlier file when it left the VM so; AfterAll empties RemoteHub's data)
        $null = Initialize-LabVm -Name $VmName -Client
        Allow-NtlmDelegation
        $script:task = Start-LabClient -Name $VmName
        $script:jump = Start-LabJumpHost
        # the jump VM comes with an agent paired with the host: replaced by one RemoteHub pairs itself
        $null = Uninstall-LabAgent -Name 'JumpHost'
        # the jump host's own account, no stored password: asked once when going through it (J-18)
        New-LabClientConnection -Name $VmName -Display 'JumpHost' -TargetHost $script:jump.Address -User $script:jump.User
        # a plain connection at the top level: selected before New..., so new items land at the top level
        New-LabClientConnection -Name $VmName -Display 'Anchor' -TargetHost 'anchor.lab.test'
        $R.pair = Install-AgentByHand -Node 'JumpHost' -TargetVm 'JumpHost'
        $target = Move-LabVmBehindJump -Name $device.Name
        $null = Ui 'Anchor' -Value realclick
        New-LabClientConnection -Name $VmName -Display 'Behind' -TargetHost $target -User $device.AdminAccount -Password $password
        $null = Ui 'Behind' -Value 'menu:Move under>JumpHost'
        Start-Sleep -Seconds 2
        Save 'setup' @{ Pair = $R.pair; Target = $target; Jump = $script:jump.Address; Computer = $computer }
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        Save 'results' $R
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        # undone for the next file on this VM; anything that fails: the next file starts from the checkpoint
        try { Clear-ClientLeftovers } catch { Set-LabVmDirty -Name $VmName }
        try { Save-LabAgentEvidence -Name 'JumpHost' -Destination (Join-Path $EvidenceDir 'ui-jump-jumphost-logs') } catch { }
        try { Stop-LabJumpHost } catch { }
        try { Restore-LabVmNetwork -Name $device.Name } catch { }
        # the device: Remote Desktop on, the SMB share rhjump, moved behind the jump host
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'UI-JUMP <VmName>' -Tag Client, UI, Jump, 'J', 'UI-JUMP' -Skip:(-not $isClient) {

    Context 'RDP through the jump host: its password asked once, the certificate pin' {
        BeforeAll {
            $p = $R.rdp = [ordered]@{}
            Step $p first {
                Clear-DeviceSessions
                $p.thumb1 = RdpThumb
                # tried once more after a lookup that got no answer (the run of 16.10.2026 13:55: the person server took
                # this step's request and didn't answer within 90 s; nothing was open afterwards)
                try { $null = Menu 'Connect' -Node 'Behind' }
                catch { $p.connectRetry = "$_"; $null = Clear-Dialogs; $null = Menu 'Connect' -Node 'Behind' }
                # the jump host's account has no stored password: asked, kept in memory
                $p.ask = (Ui 'No password is stored for*' -Title 'Jump host' -Value read -TimeoutSec 60).name
                $null = Ui 'type:Edit' -Title 'Jump host' -Value "set:$password"
                $null = Ui 'Connect' -Title 'Jump host'
                $p.trust = Box '*reached through JumpHost, so the RDP client*' -Button 'Trust and connect' -TimeoutSec 120
                $p.connected1 = Wait-RdpTab 120
                Shot 'rdp-first'
                Close-Tabs
            }
            Step $p second {
                Clear-DeviceSessions
                $null = Menu 'Connect' -Node 'Behind'
                $p.askAgain = (Ui 'No password is stored for*' -Title 'Jump host' -Value exists -Optional -TimeoutSec 10).found
                $p.trustAgain = (Ui '*presents*' -Title '?*' -Value exists -Optional -TimeoutSec 15).found
                $p.connected2 = Wait-RdpTab 120
                Close-Tabs
            }
            Step $p changed {
                # a new RDP certificate on the device: its self-signed one removed, Remote Desktop Services make another
                Clear-DeviceSessions
                $null = Invoke-LabGuest -Name $device.Name -ScriptBlock {
                    Get-ChildItem 'Cert:\LocalMachine\Remote Desktop' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
                    Restart-Service TermService -Force
                    Start-Sleep -Seconds 5
                }
                $p.thumb2 = RdpThumb
                $null = Menu 'Connect' -Node 'Behind'
                $p.changed = Box '*presents a different RDP certificate*' -Button 'Cancel' -TimeoutSec 120
                Start-Sleep -Seconds 3
                $p.tabAfterCancel = Wait-RdpTab 10
                $p.tabsAfterCancel = @(Items | Where-Object { $_ -like 'Text|Remote desktop|*' }).Count
                $null = Menu 'Connect' -Node 'Behind'
                $p.changed2 = Box '*presents a different RDP certificate*' -Button 'Trust the new one' -TimeoutSec 120
                $p.connected3 = Wait-RdpTab 120
                Shot 'rdp-changed'
                Close-Tabs
            }
            Save 'rdp' $p
        }

        It 'UI-JUMP-02 the second connect asks nothing; a different certificate: "RDP certificate changed", Cancel opens no tab, "Trust the new one" connects' -Tag 'UI-JUMP-02' {
            $p.trust | Should -Match 'Behind is reached through JumpHost, so the RDP client can.t check its certificate by name'
            $p.connected1 | Should -BeTrue
            $p.trustAgain | Should -BeFalse
            $p.connected2 | Should -BeTrue
            $p.thumb2 | Should -Not -Be $p.thumb1 -Because 'the device has a new RDP certificate'
            $p.changed | Should -Match 'Behind presents a different RDP certificate than the one you trusted before'
            $p.changed | Should -Match 'RDP certificate changed|Trust the new one'
            $p.tabAfterCancel | Should -BeFalse
            $p.tabsAfterCancel | Should -Be 0
            $p.changed2 | Should -Match 'presents a different RDP certificate'
            $p.connected3 | Should -BeTrue
        }
    }

    Context 'the tree' {
        BeforeAll {
            $t = $R.tree = [ordered]@{}
            Step $t build {
                # a folder with a connection (the reference for J-15); a folder and a share on its own below the jump host
                $null = Ui 'Anchor' -Value realclick
                New-Folder 'Plain'
                New-LabClientConnection -Name $VmName -Display 'InFolder' -TargetHost 'infolder.lab.test'
                $null = Ui 'JumpHost' -Value realclick
                New-Folder 'JFolder'
                # a share on its own, first in the folder Plain
                $null = Ui 'Plain' -Value realclick
                $null = Ui 'NewButton' -Value 'pick:New network share'
                Set-ShareEditor 'New network share' @{ Name = 'JShare'; Type = 'SMB*'; Host = 'jshare.lab.test'; PathLabel = 'Share name'; Path = 'data' }
                $null = Ui 'Add' -Title 'New network share'
                $null = Ui 'JShare' -Value exists
                # a folder with its own account, a connection in it that inherits it (J-21)
                $null = Ui 'Anchor' -Value realclick
                New-Folder 'CredFolder' -User $vm.UserAccount -Pw $password
                New-LabClientConnection -Name $VmName -Display 'Mover' -TargetHost 'mover.lab.test'
                # a would-be jump host without an agent, and what goes under it (J-17)
                $null = Ui 'Anchor' -Value realclick
                New-LabClientConnection -Name $VmName -Display 'Lonely' -TargetHost 'lonely.lab.test'
                $null = Ui 'Anchor' -Value realclick
                New-LabClientConnection -Name $VmName -Display 'Orphan' -TargetHost 'orphan.lab.test'
                Shot 'tree-built'
            }
            Step $t shareDrop {
                # a share on its own dropped onto a connection lands next to it (not under it) ...
                $pr = Probe @('JShare', 'JumpHost', $script:jump.Address)
                Drag (Center $pr 'JShare') (Mid $pr 'JumpHost' $script:jump.Address)
                $pr = Probe @('JShare', 'JumpHost')
                $t.shareDropped = [ordered]@{ row = @((TreeHit $pr 'JShare').row); x = (TreeHit $pr 'JShare').treeItem.x; jumpX = (TreeHit $pr 'JumpHost').treeItem.x }
                # ... Move under puts it under the jump host
                $null = Ui 'JShare' -Value 'menu:Move under>JumpHost'
                Start-Sleep -Seconds 2
                $t.shareMoved = @((TreeHit (Probe @('JShare')) 'JShare').row)
            }
            Step $t indent {
                $pr = Probe @('Plain', 'InFolder', 'JumpHost', 'Behind', 'JFolder', 'JShare')
                $x = [ordered]@{}
                foreach ($k in 'Plain', 'InFolder', 'JumpHost', 'Behind', 'JFolder', 'JShare') { $h = TreeHit $pr $k; $x[$k] = $(if ($h) { $h.treeItem.x } else { $null }) }
                $t.indent = [ordered]@{
                    raw = $x; folder = $x.InFolder - $x.Plain
                    behind = $x.Behind - $x.JumpHost; jfolder = $x.JFolder - $x.JumpHost; jshare = $x.JShare - $x.JumpHost
                }
            }
            Step $t route {
                $pr = Probe @('Behind', 'via JumpHost')
                $t.behindRow = @((TreeHit $pr 'Behind').row)
                $t.viaHelp = @(Hits $pr 'via JumpHost' | ForEach-Object { $_.help })
                $t.location = Tip 'Behind'
                $null = Menu 'Edit*' -Node 'Behind'
                $t.editor = (Ui 'Reached through JumpHost*' -Title 'Edit connection' -Value read -TimeoutSec 30).name
                $null = Ui 'Cancel' -Title 'Edit connection'
            }
            Step $t moveCredentials {
                # into the jump host: the credentials it inherits change (the folder's -> none: the jump host's own
                # account isn't inherited)
                $null = Ui 'Mover' -Value 'menu:Move under>JumpHost'
                $t.moveIn = Box '*Move "Mover" to*' -Button 'Move'
                Start-Sleep -Seconds 2
                $pr = Probe @('Mover', 'CredFolder')
                $t.moverIn = @((TreeHit $pr 'Mover').row)
                # out of it again, dropped into the folder with the account
                Drag (Center $pr 'Mover') (Center $pr 'CredFolder')
                $t.moveOut = Box '*Move "Mover" to*' -Button 'Move'
                Start-Sleep -Seconds 2
                $t.moverOut = @((TreeHit (Probe @('Mover')) 'Mover').row)
            }
            Step $t noAgent {
                $pr = Probe @('Orphan', 'Lonely', 'lonely.lab.test')
                Drag (Center $pr 'Orphan') (Mid $pr 'Lonely' 'lonely.lab.test')
                $pr = Probe @('Orphan', 'via Lonely')
                $t.orphanRow = @((TreeHit $pr 'Orphan').row)
                $t.viaLonelyHelp = @(Hits $pr 'via Lonely' | ForEach-Object { $_.help })
                $null = Menu 'Connect' -Node 'Orphan'
                $t.orphanConnect = Box '*has no RemoteHub Agent*'
            }
            Step $t rdpDrop {
                $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                    Set-Content C:\Users\Public\dropA.rdp "full address:s:dropa.lab.test`r`nusername:s:labuser`r`n" -Encoding ascii
                    Set-Content C:\Users\Public\dropB.rdp "full address:s:dropb.lab.test`r`nusername:s:labuser`r`n" -Encoding ascii
                }
                # onto the jump host: imported under it
                $pr = Probe @('JumpHost', $script:jump.Address)
                $t.dropAEffect = (DragFile 'C:\Users\Public\dropA.rdp' (Mid $pr 'JumpHost' $script:jump.Address)).effect
                # onto a connection below it: imported next to that connection (under the same jump host)
                $pr = Probe @('Behind', $target)
                $t.dropBEffect = (DragFile 'C:\Users\Public\dropB.rdp' (Mid $pr 'Behind' $target)).effect
                $pr = Probe @('dropA', 'dropB')
                $t.dropA = @((TreeHit $pr 'dropA').row)
                $t.dropB = @((TreeHit $pr 'dropB').row)
            }
            Step $t duplicate {
                $null = Menu 'Duplicate' -Node 'JumpHost'
                Start-Sleep -Seconds 2
                $pr = Probe @('JumpHost (copy)', 'via JumpHost (copy)')
                $t.copy = [bool](TreeHit $pr 'JumpHost (copy)')
                $t.copyItems = @(Hits $pr 'via JumpHost (copy)').Count
                # its agent cleared: the menu offers the install, not the update
                $m = Ui 'JumpHost (copy)' -Value 'menu:RemoteHub Agent>Update RemoteHub Agent*' -Optional
                $t.copyAgent = [ordered]@{ error = "$($m.error)"; entries = @($m.entries) }
                Key 1B; Key 1B
                # a copy of the connection reached through it: its RDP certificate pin is cleared (asked as the first time)
                $null = Menu 'Duplicate' -Node 'Behind'
                Start-Sleep -Seconds 2
                $null = Menu 'Connect' -Node 'Behind (copy)'
                $t.copyRdp = Box '*Behind (copy) is reached through*' -Button 'Cancel' -TimeoutSec 120
                Close-Tabs
            }
            Shot 'tree'
            Save 'tree' $t
        }

        It 'J-15 inside a jump host''s frame its connections, folders and shares sit 12 px further in than a folder''s items' -Tag 'J-15' {
            foreach ($k in 'folder', 'behind', 'jfolder', 'jshare') { $t.indent[$k] | Should -Not -BeNullOrEmpty -Because $k }
            foreach ($k in 'behind', 'jfolder', 'jshare') { ($t.indent[$k] - $t.indent.folder) | Should -BeIn 11, 12, 13 -Because "$k against a folder's item" }
        }

        It 'J-16 route shown in the tree ("via JumpHost", its tooltip), the editor and the tooltip''s location' -Tag 'J-16' {
            $t.behindRow | Should -Contain 'via JumpHost'
            @($t.viaHelp | Where-Object { $_ -match '^Reached through JumpHost \(its RemoteHub Agent is the jump host\)\. Move it out of JumpHost to connect directly\.$' }).Count | Should -BeGreaterThan 0
            $t.editor | Should -Match '^Reached through JumpHost . its RemoteHub Agent is the jump host\. Move it out of JumpHost in the list to connect directly\.$'
            $t.location | Should -Match 'JumpHost . Behind'
        }

        It 'J-17 a jump host without an agent: the hint''s tooltip and the message when a child connects' -Tag 'J-17' {
            $t.orphanRow | Should -Contain 'via Lonely'
            @($t.viaLonelyHelp | Where-Object { $_ -match '^Lonely has no RemoteHub Agent . install it there to reach the items below it\.$' }).Count | Should -BeGreaterThan 0
            $t.orphanConnect | Should -Match 'Orphan is reached through Lonely, but Lonely has no RemoteHub Agent\. Install it there \(right-click Lonely . Install RemoteHub Agent.\), or move Orphan out of Lonely to connect directly\.'
        }

        It 'J-20 Duplicate a jump host: "[n] (copy)" without the items under it, its agent cleared; a copied child''s RDP certificate pin cleared' -Tag 'J-20' {
            $t.copy | Should -BeTrue
            $t.copyItems | Should -Be 0
            $t.copyAgent.error | Should -Match "no menu entry 'Update RemoteHub Agent"
            @($t.copyAgent.entries | Where-Object { $_ -like 'Install RemoteHub Agent*' }).Count | Should -BeGreaterThan 0
            # asked like the first time (not "changed"): no pin on the copy
            $t.copyRdp | Should -Match 'Behind \(copy\) is reached through JumpHost, so the RDP client can.t check its certificate by name'
        }

        It 'J-21 moving into or out of a jump host when the inherited credentials change asks first; the jump host''s own credentials are never inherited' -Tag 'J-21' {
            $t.moveIn | Should -Match 'Move "Mover" to connection "JumpHost" \(reached through it\)\?'
            $t.moveIn | Should -Match 'now: folder "CredFolder"'
            $t.moveIn | Should -Match 'after: none'
            $t.moveIn | Should -Not -Match 'after: [^\r\n]*(this item|JumpHost)'
            $t.moverIn | Should -Contain 'via JumpHost'
            $t.moveOut | Should -Match 'Move "Mover" to folder "CredFolder"\?'
            $t.moveOut | Should -Match 'after: folder "CredFolder"'
            $t.moverOut | Should -Not -Contain 'via JumpHost'
        }

        It 'J-24 a share on its own dropped onto a connection lands next to it, Move under puts it under the jump host; .rdp files dropped on a jump host and on its child are imported under the jump host' -Tag 'J-24' {
            $t.shareDropped.row | Should -Not -Contain 'via JumpHost'
            [math]::Abs($t.shareDropped.x - $t.shareDropped.jumpX) | Should -BeLessOrEqual 1 -Because 'next to the jump host: the same level'
            $t.shareMoved | Should -Contain 'via JumpHost'
            $t.dropA | Should -Contain 'via JumpHost'
            $t.dropB | Should -Contain 'via JumpHost'
        }
    }

    Context 'shares behind the jump host' {
        BeforeAll {
            $s = $R.shares = [ordered]@{}
            Step $s smb {
                $null = Menu 'Tools>Add network share*' -Node 'Behind'
                Set-ShareEditor 'Add network share to Behind' @{ Name = 'SmbBehind'; Type = 'SMB*'; PathLabel = 'Share name'; Path = 'rhjump' }
                $null = Ui 'Add' -Title 'Add network share to Behind'
                $null = Menu 'Open' -Node 'SmbBehind'
                # the jump host's password was entered before: not asked again for another tab
                $s.jumpAsk = (Ui 'No password is stored for*' -Title 'Jump host' -Value exists -Optional -TimeoutSec 10).found
                # asked anyway (J-18 fails on it): answered, so the share tab and the next steps can still be checked
                if ($s.jumpAsk) { $null = Ui 'type:Edit' -Title 'Jump host' -Value "set:$password"; $null = Ui 'Connect' -Title 'Jump host' }
                $b = Ui 'SMB ? RemoteHub client ? via JumpHost' -Value exists -Optional -TimeoutSec 90
                $s.badge = $b.found
                $s.badgeName = "$($b.name)"
                Shot 'share-tab'
            }
            Step $s ftp {
                $null = Menu 'Tools>Add network share*' -Node 'Behind'
                Set-ShareEditor 'Add network share to Behind' @{ Name = 'FtpBehind'; Type = 'FTP*' }
                $null = Ui 'Add' -Title 'Add network share to Behind'
                $m = Ui 'FtpBehind' -Value 'menu:Open' -Optional
                $s.ftpMenu = "$($m.error)"
                $s.ftpHelp = "$($m.help)"
                Key 1B
                $null = Ui 'FtpBehind' -Value doubleclick
                $s.ftpOpened = Box '*FTP can*t go through a jump host*'
            }
            Step $s hints {
                $null = Menu 'Tools>Add network share*' -Node 'JumpHost'
                $null = Ui 'Host' -Title 'Add network share to JumpHost' -Value 'setnext:other.lab.test'
                $s.jumpHint = (Ui 'Reached directly, like JumpHost itself*' -Title 'Add network share to JumpHost' -Value read -Optional -TimeoutSec 10).name
                $null = Ui 'Cancel' -Title 'Add network share to JumpHost'
                $null = Menu 'Tools>Add network share*' -Node 'Behind'
                $null = Ui 'Host' -Title 'Add network share to Behind' -Value 'setnext:other2.lab.test'
                $s.childHint = (Ui 'Another device, reached through*' -Title 'Add network share to Behind' -Value read -Optional -TimeoutSec 10).name
                $null = Ui 'Cancel' -Title 'Add network share to Behind'
            }
            Save 'shares' $s
        }

        It 'J-16 a share tab through the jump host says "[description] . via JumpHost"' -Tag 'J-16' {
            $s.badge | Should -BeTrue
            $s.badgeName | Should -Match '^SMB . RemoteHub client . via JumpHost$'
        }

        It 'J-22 an FTP share behind a jump host: Open is off with the reason, opened anyway the same text' -Tag 'J-22' {
            $s.ftpMenu | Should -Match "menu entry 'Open' is disabled"
            $s.ftpHelp | Should -Match '^Reached through JumpHost\. FTP can.t go through a jump host . its data connections go to addresses the server announces\. SMB, SFTP and WebDAV can\.$'
            $s.ftpOpened | Should -Match 'FtpBehind is reached through JumpHost\. FTP can.t go through a jump host . its data connections go to addresses the server announces\. SMB, SFTP and WebDAV can\.'
        }

        It 'J-23 the share editor''s host hints: a jump host''s share on another host is reached directly; a child''s is another device reached through the jump host' -Tag 'J-23' {
            $s.jumpHint | Should -Match '^Reached directly, like JumpHost itself\. For a device reached through JumpHost, add the share to that device.s connection'
            $s.childHint | Should -Match '^Another device, reached through JumpHost like Behind\.$'
        }
    }

    Context 'the jump host''s credentials' {
        BeforeAll {
            $c = $R.cred = [ordered]@{}
            Step $c none {
                $null = Menu 'Edit*' -Node 'JumpHost'
                $null = Ui 'Credentials' -Title 'Edit connection'
                $null = Ui 'Inherit from parent folder' -Title 'Edit connection' -Value check
                $null = Ui 'Save' -Title 'Edit connection'
                $null = Menu 'Connect' -Node 'Behind'
                $c.none = Box '*No credentials are set for the jump host*'
            }
            Step $c restore {
                # its own account again, still without a stored password
                $null = Menu 'Edit*' -Node 'JumpHost'
                $null = Ui 'Credentials' -Title 'Edit connection'
                $null = Ui 'Enter credentials for this item' -Title 'Edit connection' -Value check
                $null = Ui 'Username' -Title 'Edit connection' -Value "setnext:$($script:jump.User)"
                $null = Ui 'Local account on the target computer*' -Title 'Edit connection' -Value check -Optional
                $null = Ui 'Save' -Title 'Edit connection'
            }
            Save 'credentials' $c
        }

        It 'J-18 none: "No credentials are set for the jump host [j] ..."; no stored password: asked once ("... kept in memory only."), then reused for the next connect and another tab' -Tag 'J-18' {
            $c.none | Should -Match 'No credentials are set for the jump host JumpHost\. Its RemoteHub Agent signs in with a Windows account of JumpHost'
            $R.rdp.ask | Should -Match ('No password is stored for \S*' + [regex]::Escape($script:jump.User) + ' on the jump host JumpHost\. Enter it to connect to Behind through it . it is kept in memory only\.')
            $R.rdp.askAgain | Should -BeFalse
            $R.shares.jumpAsk | Should -BeFalse
            # Not covered: the password taken from an open agent tab of the jump host (KnownTarget) - see the report.
        }
    }

    Context 'installing the agent below a jump host without WinRM' {
        BeforeAll {
            $i = $R.install = [ordered]@{}
            Step $i dialog {
                $null = Menu 'RemoteHub Agent>Install RemoteHub Agent*' -Node 'Behind'
                # the jump host's address towards the device is asked through it: should its password be asked again
                # (the edit above), it is entered, so the dialog still comes
                $pw = Ui 'type:Edit' -Title 'Jump host' -Value "set:$password" -Optional -TimeoutSec 8
                if ($pw.found) { $i.askedAgain = $true; $null = Ui 'Connect' -Title 'Jump host' -Optional }
                $null = Ui 'Save the installer and the enrollment file to a folder*' -Title 'Install RemoteHub Agent' -Value exists -TimeoutSec 90
                $pr = Probe @('Save the installer and the enrollment file to a folder*', 'Copy the installer and the enrollment file to the clipboard*',
                    'Install silently over PowerShell remoting*', '*is reached through JumpHost, so its agent will only be reached from there*',
                    'Use the agent for this connection afterwards') -Title 'Install RemoteHub Agent'
                $i.window = $pr.window
                $i.save = @(Hits $pr 'Save the installer and the enrollment file to a folder*') | Select-Object -First 1
                $i.clipboard = @(Hits $pr 'Copy the installer and the enrollment file to the clipboard*') | Select-Object -First 1
                $i.hint = "$(@(Hits $pr '*is reached through JumpHost, so its agent will only be reached from there*' | ForEach-Object { $_.name }) -join "`n")"
                $i.useAfter = @(Hits $pr 'Use the agent for this connection afterwards').Count
                Shot 'install-dialog'
                $null = Ui 'Close' -Title 'Install RemoteHub Agent'
            }
            Save 'install' $i
        }

        It 'J-25 the install dialog defaults to "Save the installer and the enrollment file to a folder...", says to allow the jump host''s address, hides "Use the agent ... afterwards"' -Tag 'J-25' {
            $i.save | Should -Not -BeNullOrEmpty
            $i.save.selected | Should -BeTrue
            $i.clipboard.selected | Should -BeFalse
            $i.hint | Should -Match 'Behind is reached through JumpHost, so its agent will only be reached from there: allow connections from JumpHost.s address in that network\.'
            $i.useAfter | Should -Be 0
        }
    }

    Context 'deleting a jump host' {
        BeforeAll {
            $d = $R.del = [ordered]@{}
            Step $d all {
                Close-Tabs
                $null = Menu 'Delete' -Node 'Lonely'
                $d.all = Box '*Delete connection "Lonely"*' -Button 'Delete all'
                Start-Sleep -Seconds 2
                $d.afterAll = Items
            }
            Step $d keep {
                $null = Menu 'Delete' -Node 'JumpHost'
                $d.keep = Box '*Delete connection "JumpHost"*' -Button 'Keep them'
                Start-Sleep -Seconds 2
                $pr = Probe @('Behind', 'JumpHost', 'via JumpHost')
                $d.behindRow = @((TreeHit $pr 'Behind').row)
                $d.jumpLeft = [bool](TreeHit $pr 'JumpHost')
                $d.via = @(Hits $pr 'via JumpHost').Count
                Shot 'deleted'
            }
            Save 'delete' $d
        }

        It 'J-19 deleting a jump host asks Keep them (moved next to it, reached directly) or Delete all' -Tag 'J-19' {
            $d.all | Should -Match 'Delete connection "Lonely"\?'
            $d.all | Should -Match '1 item\(s\) are reached through it'
            @($d.afterAll | Where-Object { $_ -like '*|Orphan|*' -or $_ -like '*|Lonely|*' }).Count | Should -Be 0
            $d.keep | Should -Match 'Delete connection "JumpHost"\?'
            $d.keep | Should -Match '\d+ item\(s\) are reached through it\. Keep them \(they move next to it and are reached directly\), or delete them too\?'
            $d.jumpLeft | Should -BeFalse
            $d.behindRow | Should -Not -BeNullOrEmpty
            $d.behindRow | Should -Not -Contain 'via JumpHost'
            $d.via | Should -Be 0
        }
    }
}
