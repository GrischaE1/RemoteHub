# UI-AD / UI-CP / UI-SAS / UI-NET / R-AGENT / UI-AG - RemoteHub's agent desktop tab, driven through RemoteHub's own
# window on the admin's PC (this client VM, the console account's session, UI Automation through RhPerson.ps1 -Do uia),
# against the device (the Server VM) running the agent. normaluser is signed in at the device's console (autologon) and
# answers as the person there (OCR + console mouse on the agent's own windows). The admin's keyboard and mouse are the
# client VM's Hyper-V console keyboard and mouse, so key combinations reach RemoteHub as real input.
#
# Set-up: the agent is installed the way RemoteHub offers it without WinRM - "Install RemoteHub Agent..." with "Copy the
# installer and the enrollment file to the clipboard"; the enrollment file RemoteHub made is installed on the device by
# hand (msiexec, the PREVIOUS agent package), RemoteHub notices the agent and switches the connection to it. (An install
# from the host alone isn't enough: RemoteHub only trusts a device certificate it created itself.) The update to the
# package under test then goes through the UI (UI-AG-06).
#
# The tab's picture: the tab has no own way to save a still (only a video recording), so it is a screenshot of the client
# VM cropped to the picture element (the largest Image in RemoteHub's window, found by UI Automation).
# "Copy"/"paste" outside the tab: text and images are put on / read from the clipboard by code in the user's session,
# files are copied as a file list and pasted with the shell's own Paste verb (what Explorer's Ctrl+C / Ctrl+V do).
# Keys inside the tab (typing, Ctrl+V, Ctrl+A, Ctrl+C, Alt+Tab, ...) are pressed on the client's console keyboard.
# Notices over the picture last 6 s: a watcher in the client session writes every text RemoteHub shows to a log.
# Client VMs only (2VM). See docs/test-lab-cases.md section 10.5.
# LabRole: console
# TimeLimitMin: 120   (many UI steps with RemoteHub and the device, sign-ins and reconnects: > 60 min)
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
    $R = [ordered]@{}
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-desktop-$name.json") -Encoding UTF8 }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # the connection's context menu entry ("A>B" = submenu, right-click and entries in one process)
    function Menu([string]$Entry, [int]$TimeoutSec = 30) { $m = Ui 'Device' -Value "menu:$Entry" -TimeoutSec $TimeoutSec; if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" } }
    # the text of the message box on top (RemoteHub's dialogs are listed inside the main window), closed with -Button
    function Message([string]$Title = '?*', [string]$Button = 'OK', [int]$TimeoutSec = 120) {
        $m = Ui '*' -Title $Title -Value list -TimeoutSec $TimeoutSec
        $null = Ui $Button -Title $Title -Optional
        ($m.items -join "`n")
    }
    function Shot([string]$name) { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-desktop-$name.png") }
    function DevShot([string]$name) { $p = Join-Path $EvidenceDir "ui-desktop-$name-device.png"; $null = Get-LabScreenshot -Name $dn -Path $p; $p }
    # Esc on the client's console keyboard (closes a menu; with the tab focused it goes to the device)
    function Esc { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B; Start-Sleep -Milliseconds 400 }
    # one independent UI section: a failure is written down (error, screenshot, open dialogs), the next section runs
    function Step([string]$Name, [scriptblock]$Do) {
        try { EnsureClient $Name; . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            $R["$Name-dialogs"] = @((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60)
            foreach ($i in 1..2) { Esc }
        }
    }
    # RemoteHub still running on the admin's PC, else what is known about its end (crash / hang / .NET entries of the
    # Application log, the end of its diagnostic log - no secrets there) and RemoteHub started again, so the following
    # steps don't all fail on the missing window. In the run of 16.10.2026 21:25 its process ended about 9 s after the
    # agent refused the PowerShell tab's wrong password (UI-AG-04) while the PIN tab was live, and UI-AG-04, -07 and -08
    # failed on "windows: Program Manager". Only a missing process counts: a slow UI Automation answer must not start a
    # second RemoteHub.
    function EnsureClient([string]$Name) {
        if (-not $clientTasks) { return }
        $consoleUser = (Get-LabVm $VmName).ConsoleAccount
        $state = Invoke-LabGuest -Name $VmName -ArgumentList $consoleUser -ScriptBlock {
            param($u)
            $p = @(Get-Process RemoteHub -ErrorAction SilentlyContinue)
            if ($p.Count) { return $null }
            [ordered]@{
                at = (Get-Date).ToString('HH:mm:ss')
                events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-30) } -ErrorAction SilentlyContinue |
                        Where-Object { $_.ProviderName -in 'Application Error', 'Application Hang', '.NET Runtime', 'Windows Error Reporting' -and $_.Message -match 'RemoteHub' } |
                        Select-Object -First 10 | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($_.ProviderName) $($_.Id): $(($_.Message -split "`n" | Select-Object -First 15) -join ' ')" })
                diagLog = @(Get-Content "C:\Users\$u\AppData\Roaming\RemoteHub\logs\remotehub.log" -Tail 40 -ErrorAction SilentlyContinue)
            }
        }
        if (-not $state) { return }
        $R["$Name-clientGone"] = $state
        $clientTasks.Add((Start-LabClient -Name $VmName))
    }
    function NoError([string]$Name) { $R["$Name-error"] | Should -BeNullOrEmpty -Because "the UI steps of '$Name' ran" }
    function InSession([string]$Vm, [string]$Account, [string]$Code, [int]$TimeoutSec = 120) {
        Invoke-LabInSession -Name $Vm -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($Code))
    }
    function Lines($text) { @("$text" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    function Sha([string]$s) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($s))).Replace('-', '') }
    function Norm([string]$s) { ("$s" -replace "`r`n", "`n").TrimEnd("`n") }
    function WaitFor([scriptblock]$Cond, [int]$TimeoutSec = 60, [int]$Every = 3) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { if (& $Cond) { return $true }; Start-Sleep -Seconds $Every } while ((Get-Date) -lt $until)
        $false
    }
    function LockedCount([string]$Vm) { Invoke-LabGuest -Name $Vm -ScriptBlock { @(Get-Process LogonUI -ErrorAction SilentlyContinue).Count } }
    function ExplorerOwners([string]$Vm) { @(Invoke-LabGuest -Name $Vm -ScriptBlock { @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User }) }) }
    function Ocr([string]$Path) { "$((Get-LabOcr -Path $Path).text)" }
    # every text RemoteHub's window shows, as the watcher logged it ("HH:mm:ss.fff|text"), from line -From on
    function Notices([int]$From = 0) { @(Invoke-LabGuest -Name $VmName -ArgumentList $watchFile -ScriptBlock { param($f) Get-Content -LiteralPath $f -Encoding UTF8 -ErrorAction SilentlyContinue }) | Select-Object -Skip $From }
    function NoticeMark { @(Notices).Count }

    # ---- UI Automation / Win32 code run in a session
    # the agent tab's picture: the largest Image in RemoteHub's window (screen pixels). RemoteHub's window is brought to
    # the front first: a console window of the session's tasks (Windows Terminal ignores -WindowStyle Hidden) came over
    # it in the run of 16.10.2026, so the click went there and the cropped picture showed that window.
    $rectCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace RhLab -Name Fg -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, IntPtr e);'
$A = [Windows.Automation.AutomationElement]
$isImg = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::Image)
$best = $null; $area = 0
foreach ($w in @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition))) {
    $n = $w.Current.Name
    if ($n -notlike 'RemoteHub*' -and $n -notlike '* RemoteHub') { continue }
    $h = [IntPtr]$w.Current.NativeWindowHandle
    if ($h -ne [IntPtr]::Zero -and [RhLab.Fg]::GetForegroundWindow() -ne $h) {
        # an Alt tap lets this process set the foreground window
        [RhLab.Fg]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [RhLab.Fg]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
        $null = [RhLab.Fg]::SetForegroundWindow($h)
        Start-Sleep -Milliseconds 400
    }
    foreach ($i in @($w.FindAll([Windows.Automation.TreeScope]::Descendants, $isImg))) {
        if ($i.Current.IsOffscreen) { continue }
        $b = $i.Current.BoundingRectangle
        if ($b.Width * $b.Height -gt $area) { $area = $b.Width * $b.Height; $best = $b }
    }
}
if ($best) { 'RECT={0},{1},{2},{3}' -f [int]$best.X, [int]$best.Y, [int]$best.Width, [int]$best.Height } else { 'RECT=none' }
'@
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
    # visible top-level window titles and the foreground window of a session
    $winCode = @'
Add-Type -TypeDefinition 'using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices; public static class RhW { public delegate bool P(IntPtr h, IntPtr l); [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowTextW(h, s, 512); return s.ToString(); } public static List<string> Titles() { var l = new List<string>(); EnumWindows((h, p) => { if (IsWindowVisible(h)) { var t = Title(h); if (t.Length > 0) l.Add(t); } return true; }, IntPtr.Zero); return l; } }'
'FG=' + [RhW]::Title([RhW]::GetForegroundWindow())
'ALL=' + ([RhW]::Titles() -join '|')
'@
    # Notepad's text by WM_GETTEXT (when UI Automation doesn't answer)
    $npCode = @'
Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public static class RhN { [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string t); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string t); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, int m, IntPtr w, StringBuilder l); }'
$h = [RhN]::FindWindowW('Notepad', $null); $e = if ($h -ne [IntPtr]::Zero) { [RhN]::FindWindowExW($h, [IntPtr]::Zero, 'Edit', $null) } else { [IntPtr]::Zero }
if ($e -eq [IntPtr]::Zero) { 'NP-NONE' } else { $sb = New-Object Text.StringBuilder 65536; [void][RhN]::SendMessageW($e, 0x0D, [IntPtr]65536, $sb); 'NP<' + $sb.ToString() + '>NP' }
'@
    # a 200x200 test picture (pattern @@K@@) on the clipboard, or the clipboard's picture compared with it ("0" = equal)
    $patCode = @'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
function Pat([int]$k) { $b = New-Object Drawing.Bitmap 200, 200; for ($y = 0; $y -lt 200; $y++) { for ($x = 0; $x -lt 200; $x++) { $b.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, ($x * $k) % 256, ($y * 3 + $k * 40) % 256, (($x + $y) * 5) % 256)) } }; $b }
function PicDiff($a, $b) { if (-not $a -or -not $b) { return 'no image' }; if ($a.Width -ne $b.Width -or $a.Height -ne $b.Height) { return "size $($b.Width)x$($b.Height)" }; $d = 0; for ($y = 0; $y -lt $a.Height; $y++) { for ($x = 0; $x -lt $a.Width; $x++) { $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y); if ($p.R -ne $q.R -or $p.G -ne $q.G -or $p.B -ne $q.B) { $d++ } } }; "$d" }
'@
    $setImageCode = $patCode + "`n[Windows.Forms.Clipboard]::SetImage((Pat @@K@@)); 'set'"
    $getImageCode = $patCode + "`n`$img = [Windows.Forms.Clipboard]::GetImage(); if (`$img) { `$img = New-Object Drawing.Bitmap `$img }; 'DIFF=' + (PicDiff (Pat @@K@@) `$img)"
    function SetFileList([string]$Vm, [string]$Account, [string[]]$Paths) {
        $add = ($Paths | ForEach-Object { "`$c.Add('" + ($_ -replace "'", "''") + "') | Out-Null" }) -join '; '
        InSession $Vm $Account "Add-Type -AssemblyName System.Windows.Forms; `$c = New-Object System.Collections.Specialized.StringCollection; $add; [Windows.Forms.Clipboard]::SetFileDropList(`$c); 'set'"
    }
    # the clipboard's format names only (reading the files would fetch an offered copy - the paste under test)
    function ClipFormats([string]$Vm, [string]$Account) {
        @(Lines (InSession $Vm $Account "Add-Type -AssemblyName System.Windows.Forms; @([Windows.Forms.Clipboard]::GetDataObject().GetFormats()) -join '|'"))
    }
    # Explorer's Paste into a folder of the session's user (the shell's own verb, Invoke-LabPaste: messages pumped while
    # the shell fetches the offered files); answers "relative path=SHA-256" lines. The clipboard's formats before the
    # paste, the thread's apartment and whether the files came complete go to $R.<Dir>Paste.
    function PasteAt([string]$Vm, [string]$Account, [string]$Dir, [int]$Count, [int]$WaitSec = 120) {
        $p = Invoke-LabPaste -Name $Vm -Account $Account -Folder $Dir -Count $Count -TimeoutSec $WaitSec
        $R["$($Dir)Paste"] = [ordered]@{ formats = @($p.formats); apartment = $p.apartment; complete = $p.complete; seconds = $p.seconds }
        @($p.files | ForEach-Object { "$($_.Path)=$($_.Hash)" } | Sort-Object)
    }
    function Windows([string]$Vm, [string]$Account) {
        $l = Lines (InSession $Vm $Account $winCode)
        [pscustomobject]@{ Fg = "$(@($l | Where-Object { $_ -like 'FG=*' })[0])".Substring(3); All = @("$(@($l | Where-Object { $_ -like 'ALL=*' })[0])".Substring(4) -split '\|') }
    }

    # the picture of the tab on the client's screen; a click into it with the client's console mouse (real input: the
    # tab gets the focus and the device the click). X / Y: where in the picture, 0..1.
    function PictureRect {
        $o = "$(InSession $VmName 'Console' $rectCode)"
        if ($o -notmatch 'RECT=(-?\d+),(-?\d+),(\d+),(\d+)') { throw "the agent tab's picture isn't on the screen ($o)" }
        [pscustomobject]@{ X = [int]$Matches[1]; Y = [int]$Matches[2]; W = [int]$Matches[3]; H = [int]$Matches[4] }
    }
    function FocusTab([double]$X = 0.5, [double]$Y = 0.5) {
        $p = PictureRect
        Send-LabMouse -Name $VmName -X ([int]($p.X + $p.W * $X)) -Y ([int]($p.Y + $p.H * $Y))
        Start-Sleep -Seconds 1
    }
    function TabPicture([string]$name) {
        $p = PictureRect
        $full = Join-Path $EvidenceDir "ui-desktop-$name-client.png"
        $null = Get-LabScreenshot -Name $VmName -Path $full
        $out = Join-Path $EvidenceDir "ui-desktop-$name-tab.png"
        Add-Type -AssemblyName System.Drawing
        $src = [Drawing.Bitmap]::FromFile($full)
        try {
            $x = [math]::Max(0, $p.X); $y = [math]::Max(0, $p.Y)
            $w = [math]::Min($p.W, $src.Width - $x); $h = [math]::Min($p.H, $src.Height - $y)
            $crop = $src.Clone((New-Object Drawing.Rectangle $x, $y, $w, $h), $src.PixelFormat)
            try { $crop.Save($out, [Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
        }
        finally { $src.Dispose() }
        $out
    }
    # keys on the client's console keyboard (real input: RemoteHub's key hook sends them to the device)
    function Combo([int[]]$Codes) { Send-LabKeyCombo -Name $VmName -Codes $Codes; Start-Sleep -Milliseconds 600 }
    # Text typed key by key (press / release of US-layout virtual keys, Shift where needed) - the way Combo presses keys,
    # which reached the device in every case. Hyper-V's TypeText through the tab didn't (the run of 16.10.2026 13:55: the
    # user name and password at the sign-in screen signed nobody in). RemoteHub sends scan codes, so the device's layout
    # decides the characters: the device is en-US.
    $usKeys = @{ ' ' = 0x20; '-' = 0xBD; '=' = 0xBB; '[' = 0xDB; ']' = 0xDD; '\' = 0xDC; ';' = 0xBA; "'" = 0xDE; ',' = 0xBC; '.' = 0xBE; '/' = 0xBF; '`' = 0xC0 }
    $usShifted = @{ '!' = '1'; '@' = '2'; '#' = '3'; '$' = '4'; '%' = '5'; '^' = '6'; '&' = '7'; '*' = '8'; '(' = '9'; ')' = '0'; '_' = '-'; '+' = '='; '{' = '['; '}' = ']'; '|' = '\'; ':' = ';'; '"' = "'"; '<' = ','; '>' = '.'; '?' = '/'; '~' = '`' }
    function TypeText([string]$Text) {
        foreach ($ch in $Text.ToCharArray()) {
            $c = [string]$ch
            $shift = $false
            if ($usShifted.ContainsKey($c)) { $shift = $true; $c = $usShifted[$c] }
            $vk = if ($c -cmatch '^[a-z]$') { [int][char]$c.ToUpperInvariant() }
            elseif ($c -cmatch '^[A-Z]$') { $shift = $true; [int][char]$c }
            elseif ($c -match '^[0-9]$') { [int][char]$c }
            elseif ($usKeys.ContainsKey($c)) { $usKeys[$c] }
            else { throw "TypeText: no US key for '$c'" }
            Send-LabKeyCombo -Name $VmName -Codes $(if ($shift) { @(0x10, $vk) } else { @($vk) })
        }
        Start-Sleep -Milliseconds 600
    }
    function Key([string]$Key) { Send-LabKey -Name $VmName -Key $Key; Start-Sleep -Milliseconds 600 }

    # the device's Notepad (normaluser's session), maximized, opened on a file; its text
    function DeviceNotepad([string]$File, [string]$Content = '', [switch]$Keep) {
        Invoke-LabGuest -Name $dn -ArgumentList $File, $Content, $Keep.IsPresent -ScriptBlock {
            param($f, $c, $keep)
            if (-not $keep) { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 500 }
            [IO.File]::WriteAllText($f, $c, [Text.Encoding]::Unicode)
        }
        $null = Start-LabInSession -Name $dn -ScriptBlock ([scriptblock]::Create("Start-Process notepad.exe -ArgumentList '`"$File`"' -WindowStyle Maximized"))
        $leaf = [IO.Path]::GetFileName($File)
        $null = Invoke-LabPerson -Name $dn -Do uia -Title "$leaf*" -Text '*' -Value exists -TimeoutSec 30
        Start-Sleep -Seconds 2
    }
    # the screen rectangle of the first visible window whose title starts with @@T@@ (the person's click at the device)
    $rectOfCode = @'
Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public static class RhWr { public delegate bool P(IntPtr h, IntPtr l); [DllImport("user32.dll")] public static extern bool EnumWindows(P f, IntPtr l); [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n); [StructLayout(LayoutKind.Sequential)] public struct Rc { public int L, T, R, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rc r); public static string Find(string prefix) { string res = ""; EnumWindows((h, p) => { if (!IsWindowVisible(h)) return true; var s = new StringBuilder(512); GetWindowTextW(h, s, 512); if (!s.ToString().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true; Rc r; GetWindowRect(h, out r); res = r.L + "," + r.T + "," + (r.R - r.L) + "," + (r.B - r.T); return false; }, IntPtr.Zero); return res; } }'
'WR=' + [RhWr]::Find('@@T@@')
'@
    # The device's window -Leaf (the start of its title) in front before keys go to it. The tab's click (FocusTab, the
    # admin's way) first. The consent dialog takes the foreground and Windows needn't give it back when it closes (D-INPUT:
    # keys reached Windows but no window); in the run of 16.10.2026 21:25 the text typed right after Allow (UI-AD-02) and
    # the Ctrl+V of UI-CP-01 left Notepad empty. So the device's foreground is checked, and when it is another window the
    # person at the device clicks the window with the console mouse. Answers how the window got the focus: 'tab',
    # 'device (...)' or 'none (...)' - evidence whether the admin's click through the tab activates a window.
    function DeviceFocus([string]$Leaf) {
        FocusTab
        $fg = (Windows $dn 'User').Fg
        if ($fg -like "$Leaf*") { return 'tab' }
        $o = "$(InSession $dn 'User' $rectOfCode.Replace('@@T@@', $Leaf))"
        if ($o -match 'WR=(-?\d+),(-?\d+),(\d+),(\d+)') {
            Send-LabMouse -Name $dn -X ([int]([int]$Matches[1] + [int]$Matches[3] / 2)) -Y ([int]([int]$Matches[2] + [int]$Matches[4] / 2))
            Start-Sleep -Seconds 1
        }
        $fg2 = (Windows $dn 'User').Fg
        # the keyboard focus in the tab again (the keys go from there)
        FocusTab
        if ($fg2 -like "$Leaf*") { "device (the tab's click left '$fg' in front)" } else { "none ('$fg2' in front after the device's click; window: $o)" }
    }
    # The classic Notepad's edit control by WM_GETTEXT first (definite); UI Automation only when there is no such window.
    # (UI Automation's read of the edit came back empty for every case in the run of 16.10.2026 13:55 - typed, pasted -
    # so an empty answer from it proves nothing.)
    function NotepadText {
        $o = "$(InSession $dn 'User' $npCode)"
        if ($o -match '(?s)NP<(.*)>NP') { return $Matches[1] }
        $u = Invoke-LabPerson -Name $dn -Do uia -Title '*Notepad*' -Text 'Text Editor' -Value read -TimeoutSec 15
        if ($u.found -and $u.PSObject.Properties['value'] -and $null -ne $u.value) { "$($u.value)" } else { $null }
    }
    # this PC's (the client's) screen resolution changed in the console session; answers "rc;previous WxH"
    $clientResCode = @'
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class RhRes {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettingsW(string d, int m, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettingsW(ref DEVMODE dm, int flags);
    public static string Set(int w, int h) {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettingsW(null, -1, ref dm)) return "-99;?";
        var was = dm.dmPelsWidth + "x" + dm.dmPelsHeight;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettingsW(ref dm, 0) + ";" + was;
    }
}
"@
'CRES=' + [RhRes]::Set(@@W@@, @@H@@)
'@
    function ClientRes([int]$W, [int]$H) {
        $o = "$(InSession $VmName 'Console' $clientResCode.Replace('@@W@@', "$W").Replace('@@H@@', "$H"))"
        Start-Sleep -Seconds 3
        if ($o -match 'CRES=(-?\d+);(\S+)') { [pscustomobject]@{ Result = [int]$Matches[1]; Was = $Matches[2] } } else { [pscustomobject]@{ Result = $null; Was = $null; Output = $o } }
    }
    function DeviceRes { "$(InSession $dn 'User' "Add-Type -AssemblyName System.Windows.Forms; `$b = [Windows.Forms.Screen]::PrimaryScreen.Bounds; 'RES=' + `$b.Width + 'x' + `$b.Height")" -replace '(?s).*RES=(\d+x\d+).*', '$1' }

    # ---- the agent tab
    # tabs of the connection closed (the next one starts fresh): the group's x. "Close this tab" only shows in the second
    # tab row (more than one tab of a connection - as UI-MORE found), so in the run of 16.10.2026 21:25 nothing was closed:
    # Connect reused a disconnected or refused tab, but with the PIN tab still live it only showed that tab, and every
    # later "Signed in as" wait failed (UI-AD-06 to R-AGENT-03, UI-SAS-03). A session RemoteHub signed in automatically
    # with the end action Sign out asks first - signed out then.
    function CloseTabs {
        foreach ($i in 1..8) {
            $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 3
            if (-not $c.found) { $c = Ui 'Close this tab' -Optional -TimeoutSec 2 }
            if (-not $c.found) { break }
            Start-Sleep -Seconds 2
            foreach ($t in 'RemoteHub signed in on *automatically*', 'Sign out on the device?') {
                $m = Ui 'Disconnect' -Title $t -Optional -TimeoutSec 1
                if ($m.found) { Start-Sleep -Seconds 2; break }
            }
        }
    }
    # a new agent tab at its connect page ("Signed in as ... Choose what to show.")
    function OpenChoose { CloseTabs; Menu 'Connect'; Ui 'Signed in as *' -Value exists -TimeoutSec 90 }
    function ConnectChosen([string]$Session = 'Console*', [string]$Consent = 'Ask them to allow it', [switch]$ViewOnly) {
        $null = Ui $Session -Value check
        if ($Consent) { $null = Ui 'The signed-in user' -Value "choose:$Consent" }
        if ($ViewOnly) { $null = Ui 'View only*' -Value check }
        $null = Ui 'Connect'
    }
    # normaluser's answer in the agent's request dialog (OCR + console mouse). The screen capture throws while the session
    # is still on the lock or sign-in screen ("The handle is invalid" in the run of 16.10.2026 13:55): tried again until
    # -TimeoutSec.
    function Allow([string]$Answer = 'Allow', [int]$TimeoutSec = 60) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $r = try { Invoke-LabPerson -Name $dn -Do click -Text $Answer -TimeoutSec 20 } catch { [pscustomobject]@{ clicked = $false; error = "$_" } }
            if ($r.PSObject.Properties['clicked'] -and $r.clicked) { return $r }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $until)
        $r
    }
    function Live([int]$TimeoutSec = 60) { Ui 'Send Ctrl+Alt+Del' -Value exists -TimeoutSec $TimeoutSec -Optional }
    function TabTexts { "$((Ui '*' -Value list -Optional -TimeoutSec 10).items -join "`n")" }
    # an agent install / update / credential provider progress window (-Title) has finished: "Done" when it worked; its
    # failure text (or the window gone) ends the wait as well, instead of waiting -TimeoutSec for a "Done" that never
    # comes (the button stays "Close" then). Answers the last 'Done' lookup.
    function WaitDone([string]$Title, [int]$TimeoutSec = 600) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            # looked for, not clicked: a click closes the window before its rows are read (empty in the run of 16.10.2026 13:55)
            $d = Ui 'Done' -Title $Title -Value exists -TimeoutSec 30 -Optional
            if ($d.found) { return $d }
            $rows = @((Ui '*' -Title $Title -Value list -Optional -TimeoutSec 5).items)
            if (-not $rows.Count) { return $d }
            if (@($rows | Where-Object { $_ -match "^Text\|(Failed|Cancelled|The update didn.t install|The agent didn.t answer again|The device.s agent uses a certificate|Installed, but this PC can.t reach)" }).Count) { return $d }
        } while ((Get-Date) -lt $until)
        $d
    }
    # the progress window closed whichever way it ended ("Done" or "Close"): it is not modal and would stay over the tab
    function CloseProgress([string]$Title) {
        foreach ($b in 'Done', 'Close') { $null = Ui $b -Title $Title -Optional -TimeoutSec 3 }
    }
    function EditConnection([scriptblock]$Do) {
        Menu 'Edit*'
        $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
        & $Do
        $null = Ui 'Save' -Title 'Edit connection'
        Start-Sleep -Seconds 2
    }
    # the newest agent update log on the device: its msiexec command line. Read shared (msiexec may still write it) and
    # as UTF-16 when it is one; without a command line: the log's path, size and last lines instead.
    function UpdateCommandLine {
        Invoke-LabGuest -Name $dn -ScriptBlock {
            $log = Get-ChildItem 'C:\Users\*\AppData\Local\Temp\RemoteHub-Agent-update.log', 'C:\Windows\Temp\RemoteHub-Agent-update.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $log) { return 'no update log' }
            $b = try {
                $fs = [IO.File]::Open($log.FullName, 'Open', 'Read', 'ReadWrite')
                try { $ms = New-Object IO.MemoryStream; $fs.CopyTo($ms); $ms.ToArray() } finally { $fs.Dispose() }
            }
            catch { return "update log $($log.FullName) not readable: $_" }
            $t = if ($b.Length -gt 1 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE) { [Text.Encoding]::Unicode.GetString($b, 2, $b.Length - 2) }
            elseif ($b.Length -gt 1 -and $b[1] -eq 0) { [Text.Encoding]::Unicode.GetString($b) }
            else { [Text.Encoding]::Default.GetString($b) }
            $l = @($t -split "`r?`n")
            $cmd = @($l | Where-Object { $_ -match 'Command Line:' } | Select-Object -First 3)
            if ($cmd.Count) { $cmd -join "`n" } else { "no command line in $($log.FullName) ($($b.Length) bytes), last lines:`n" + (@($l | Select-Object -Last 15) -join "`n") }
        }
    }
    # after an update that didn't end with "Done": what the device shows of it (msiexec still running, Windows
    # Installer's events, the update logs), the agent's logs and the update log copied into the evidence
    function UpdateEvidence([string]$Name) {
        $info = Invoke-LabGuest -Name $dn -ScriptBlock {
            [ordered]@{
                msiexec = @(Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" })
                installer = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MsiInstaller'; StartTime = (Get-Date).AddMinutes(-30) } -MaxEvents 20 -ErrorAction SilentlyContinue | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($_.Id) $($_.Message)" })
                logs = @(Get-ChildItem 'C:\Users\*\AppData\Local\Temp\RemoteHub-Agent-update.log', 'C:\Windows\Temp\RemoteHub-Agent-update.log' -ErrorAction SilentlyContinue | ForEach-Object FullName)
            }
        }
        $dir = Join-Path $EvidenceDir "ui-desktop-$Name-device-logs"
        try { Save-LabAgentEvidence -Name $dn -Destination $dir } catch { }
        foreach ($f in @($info.logs)) { try { Get-LabFile -Name $dn -Path $f -Destination (Join-Path $dir ($f -replace '^C:\\(Users\\)?([^\\]+)\\.*$', '$2')) } catch { } }
        $info
    }

    $device = Get-LabDevice -For $VmName
    $dn = $device.Name
    $newVer = Get-LabMsiVersion (Get-LabAgentMsi)
    $prevMsi = Get-LabAgentMsi -Previous
    $prevVer = Get-LabMsiVersion $prevMsi
    # the test texts (multi-line, Unicode - written with character codes, the file stays ASCII)
    $uni = 'Zeile 1 ' + [char]0xE4 + [char]0xF6 + [char]0xFC + "`r`nLine 2 " + [char]0x2713 + ' ' + [char]0x65E5 + [char]0x672C
    $uni2 = 'Vom Ger' + [char]0xE4 + 't ' + [char]0xDF + "`r`nZweite Zeile " + [char]0x20AC + ' ' + [char]0x2713

    if ($isClient) {
        $setup = [ordered]@{}
        Reset-LabVm -Name $dn
        $deviceIp = Get-LabVmAddress -Name $dn
        # the documented preconditions: normaluser signed in at the device's console; RDP allowed there (RDP tab cases)
        Set-LabAutoLogon -Name $dn -Account User
        Invoke-LabGuest -Name $dn -ScriptBlock {
            Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        }
        # RemoteHub installed (kept from an earlier file when it left the VM so)
        $null = Initialize-LabVm -Name $VmName -Client
        $clientIp = Get-LabVmAddress -Name $VmName
        # (a list: EnsureClient starts RemoteHub again after it ended)
        $clientTasks = New-Object System.Collections.Generic.List[string]
        $clientTasks.Add((Start-LabClient -Name $VmName))
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
        # the watcher of RemoteHub's texts
        $watchFile = 'C:\ProgramData\RemoteHubLab\session\ui-desktop-texts.log'
        $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $d = 'C:\ProgramData\RemoteHubLab\session'; $null = New-Item -ItemType Directory -Path $d -Force; icacls $d /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null }
        $watch = Start-LabInSession -Name $VmName -Account Console -ScriptBlock ([scriptblock]::Create($watchCode.Replace('@@FILE@@', $watchFile)))

        # the agent from RemoteHub's install dialog: the clipboard way (no WinRM), "use the agent afterwards"
        Menu 'RemoteHub Agent>Install RemoteHub Agent*'
        $setup.dialog = (Ui '*' -Title 'Install RemoteHub Agent' -Value list -TimeoutSec 60).items
        $setup.autoSignInShown = Ui 'Allow automatic sign-in*' -Title 'Install RemoteHub Agent' -Value read -Optional
        $null = Ui 'Add this PC*' -Title 'Install RemoteHub Agent'
        $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value check
        $setup.autoSignInClipboard = Ui 'Allow automatic sign-in*' -Title 'Install RemoteHub Agent' -Value read -Optional
        $null = Ui 'Use the agent for this connection afterwards' -Title 'Install RemoteHub Agent' -Value check -Optional
        $null = Ui 'Install' -Title 'Install RemoteHub Agent'
        $setup.done = Ui 'Done' -Title 'Install RemoteHub Agent' -TimeoutSec 120
        $setup.rows = (Ui '*' -Title 'Install RemoteHub Agent' -Value list -Optional).items
        $null = Ui 'Done' -Title 'Install RemoteHub Agent' -Optional
        $setup.clip = Invoke-LabPerson -Name $VmName -Account Console -Do clip-get
        $enrollment = @($setup.clip.files | Where-Object { $_ -like '*.rhagent' })[0]
        if (-not $enrollment) { throw "RemoteHub put no enrollment file on the clipboard (files: $(@($setup.clip.files) -join ', '))" }
        # the admin installs it on the device by hand - here the previous release, for the update below
        $hostDir = Join-Path ([IO.Path]::GetTempPath()) "rhlab-enroll-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        Get-LabFile -Name $VmName -Path $enrollment -Destination $hostDir
        try {
            Install-LabSigningCertificate -Name $dn
            Copy-LabFile -Name $dn -Path @((Join-Path $hostDir (Split-Path $enrollment -Leaf)), $prevMsi) -Destination 'C:\LabSetup\agent'
        }
        finally { Remove-Item $hostDir -Recurse -Force -ErrorAction SilentlyContinue }
        $setup.msiexec = Invoke-LabGuest -Name $dn -ArgumentList (Split-Path $prevMsi -Leaf), (Split-Path $enrollment -Leaf) -ScriptBlock {
            param($msi, $enr)
            $p = Start-Process msiexec.exe -ArgumentList "/i `"C:\LabSetup\agent\$msi`" /qn /norestart /l*v C:\LabSetup\agent\install.log ENROLLMENT=`"C:\LabSetup\agent\$enr`" VIRTUALDISPLAY=1" -Wait -PassThru
            $p.ExitCode
        }
        # (the agent's firewall scope is this client's address: the host can't ask it for its health)
        $null = WaitFor -TimeoutSec 180 { "$((Get-LabAgentState -Name $dn).Service.State)" -eq 'Running' }
        # RemoteHub asks every 10 s; once it answers the connection is "installed" (the menu offers the update)
        $setup.known = WaitFor -TimeoutSec 240 -Every 5 {
            $m = Ui 'Device' -Value 'menu:RemoteHub Agent>Update RemoteHub Agent*' -Optional -TimeoutSec 10
            if ($m.PSObject.Properties['error'] -and $m.error) { Esc; Esc; return $false }
            $null = Ui 'Close' -Title 'Update RemoteHub Agent' -Optional -TimeoutSec 30
            $true
        }
        if (-not $setup.known) { throw 'RemoteHub didn''t notice the agent installed by hand within 4 minutes.' }
        Shot 'setup'
        Save 'setup' $setup
    }
}

AfterAll {
    if ($isClient) {
        # files copied and pasted all over both VMs (a 101 MB one too), the clipboard, keys and the device's console
        # session: the next file on either starts from the checkpoint
        Set-LabVmDirty -Name $VmName
        if ($device) { Set-LabVmDirty -Name $dn }
        Shot 'end'
        try { [IO.File]::WriteAllLines((Join-Path $EvidenceDir 'ui-desktop-texts.log'), [string[]]@(Notices)) } catch { }
        if ($watch) { try { Stop-LabInSession -Name $VmName -Task $watch } catch { } }
        Save 'results' $R
        foreach ($t in @($clientTasks)) { if ($t) { Stop-LabClient -Name $VmName -Task $t } }
        try { Save-LabAgentEvidence -Name $dn -Destination (Join-Path $EvidenceDir 'ui-desktop-device-logs') } catch { }
        Stop-LabVm -Name $dn
    }
}

Describe 'UI-DESKTOP <VmName>' -Tag Client, UI, 'UI-AD', 'UI-CP', 'UI-SAS', 'UI-NET', 'R-AGENT', 'UI-AG' -Skip:(-not $isClient) {

    Context 'update from the UI through the agent' {
        BeforeAll {
            Step 'update' {
                $R.certBefore = (Get-LabAgentState -Name $dn).Certificate.Sha256
                $R.versionBefore = (Get-LabAgentState -Name $dn).Product.Version
                Menu 'RemoteHub Agent>Update RemoteHub Agent*'
                $R.updateDialog = (Ui '*' -Title 'Update RemoteHub Agent' -Value list -TimeoutSec 60).items
                $null = Ui 'Update through the running agent' -Title 'Update RemoteHub Agent' -Value check
                $null = Ui 'Create a new device certificate*' -Title 'Update RemoteHub Agent' -Value uncheck -Optional -TimeoutSec 5
                # Allow automatic sign-in: offered for this way and ticked by default; unticked here, so the update passes
                # AUTOSIGNIN=0 (the MSI would install the provider by default otherwise)
                $R.updateAutoSignIn = Ui 'Allow automatic sign-in*' -Title 'Update RemoteHub Agent' -Value read -Optional
                $null = Ui 'Allow automatic sign-in*' -Title 'Update RemoteHub Agent' -Value uncheck -Optional -TimeoutSec 5
                $null = Ui 'Update' -Title 'Update RemoteHub Agent'
                $R.updateDone = WaitDone 'Update RemoteHub Agent'
                $R.updateRows = (Ui '*' -Title 'Update RemoteHub Agent' -Value list -Optional).items
                Shot 'update'
                # a failed update leaves its progress window open (button "Close"): it stayed over the agent tab for the
                # whole next context in the run of 16.10.2026
                CloseProgress 'Update RemoteHub Agent'
                $R.updateCommandLine = UpdateCommandLine
                if (-not $R.updateDone.found) { $R.updateDevice = UpdateEvidence 'update' }
                $state = Get-LabAgentState -Name $dn
                $R.certAfter = $state.Certificate.Sha256
                $R.versionAfter = $state.Product.Version
                # the shield's tooltip; the update dialog's "Installed: ..., version ..." line as well
                $R.shield = Ui "RemoteHub Agent $newVer*" -Value exists -Optional -TimeoutSec 10
                Menu 'RemoteHub Agent>Update RemoteHub Agent*'
                $R.updateDialogAfter = (Ui '*' -Title 'Update RemoteHub Agent' -Value list -TimeoutSec 60).items
                $null = Ui 'Close' -Title 'Update RemoteHub Agent' -Optional
            }
            Save 'update' $R
        }

        It 'UI-AG-06 Update RemoteHub Agent... through the running agent: "Updated - RemoteHub Agent [new] is running.", the new version on the shield, same certificate' -Tag 'UI-AG-06' {
            NoError 'update'
            $R.versionBefore | Should -Match "^$([regex]::Escape($prevVer))" -Because 'the previous release was installed first'
            $R.updateDone.found | Should -BeTrue
            ($R.updateRows -join "`n") | Should -Match "Updated . RemoteHub Agent $([regex]::Escape($newVer))\S* is running"
            $R.versionAfter | Should -Match "^$([regex]::Escape($newVer))"
            $R.certAfter | Should -Be $R.certBefore
            # the shield's tooltip, or the same version where the dialog shows the installed agent
            ($R.shield.found -or (($R.updateDialogAfter -join "`n") -match "version $([regex]::Escape($newVer))")) | Should -BeTrue
        }
    }

    Context 'agent desktop: open, control, keys, clipboard, Ctrl+Alt+Del, network' {
        BeforeAll {
            Step 'open' {
                # Remote desktop through: Automatic (explicitly); RDP without the certificate question (later cases)
                EditConnection {
                    $null = Ui 'PowerShell remoting' -Title 'Edit connection'
                    $null = Ui 'Remote desktop through' -Title 'Edit connection' -Value 'choose:Automatic*'
                    $null = Ui 'Security & session' -Title 'Edit connection'
                    $null = Ui 'Server authentication' -Title 'Edit connection' -Value 'choose:Connect without warning'
                }
                $R.resBefore = DeviceRes
                DeviceNotepad 'C:\Users\Public\ad02.txt'
                $since = Get-LabGuestTime -Name $dn
                # a double-click: Automatic with the agent installed opens the agent desktop
                $null = Ui 'Device' -Value doubleclick
                $h = Ui 'Remote desktop through the RemoteHub Agent' -Value exists -TimeoutSec 90
                $R.openHeading = $h.found
                $R.openWindow = $h.window
                $R.openStatus = (Ui 'Signed in as *' -Value read -TimeoutSec 60).name
                $R.openSessions = @((Ui '*' -Value list).items | Where-Object { $_ -like 'RadioButton|*' })
                Shot 'choose'
                ConnectChosen
                $R.allow = Allow
                $R.live = Live 90
                Start-Sleep -Seconds 5
                $dev = DevShot 'open'
                $tab = TabPicture 'open'
                $R.openSimilarity = Compare-LabImage -A $dev -B $tab
                $R.allowEvents = @(Get-LabAgentEvent -Name $dn -Since $since)
            }
            Step 'control' {
                $R.typedFocus = DeviceFocus 'ad02.txt'
                TypeText 'RemoteHub lab 123 !@#'
                Start-Sleep -Seconds 3
                $R.typed = NotepadText
                if ("$($R.typed)" -ne 'RemoteHub lab 123 !@#') { $R.typedFg = (Windows $dn 'User').Fg; $null = DevShot 'typed' }
            }
            Step 'cp01' {
                DeviceNotepad 'C:\Users\Public\cp01.txt'
                $R.cp01Set = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $uni
                Start-Sleep -Seconds 3
                $R.cp01Focus = DeviceFocus 'cp01.txt'
                # what the device's clipboard holds before the Ctrl+V: tells a copy that didn't arrive from a key that didn't
                # (text comes at once, nothing is fetched by reading it)
                $R.cp01DeviceClip = Invoke-LabPerson -Name $dn -Do clip-get
                Combo 0x11, 0x56
                Start-Sleep -Seconds 3
                $R.cp01 = NotepadText
                if ((Norm $R.cp01) -ne (Norm $uni)) { $R.cp01Fg = (Windows $dn 'User').Fg; $null = DevShot 'cp01' }
            }
            Step 'cp02' {
                DeviceNotepad 'C:\Users\Public\cp02.txt' $uni2
                FocusTab
                Combo 0x11, 0x41
                Combo 0x11, 0x43
                Start-Sleep -Seconds 4
                $R.cp02 = Invoke-LabPerson -Name $VmName -Account Console -Do clip-get
            }
            Step 'cp03' {
                $mark = NoticeMark
                $null = InSession $VmName 'Console' $setImageCode.Replace('@@K@@', '7') -TimeoutSec 240
                Start-Sleep -Seconds 4
                $R.cp03ToDevice = "$(InSession $dn 'User' $getImageCode.Replace('@@K@@', '7') -TimeoutSec 300)" -replace '(?s).*DIFF=([^\r\n]*).*', '$1'
                $null = InSession $dn 'User' $setImageCode.Replace('@@K@@', '11') -TimeoutSec 240
                Start-Sleep -Seconds 4
                $R.cp03ToAdmin = "$(InSession $VmName 'Console' $getImageCode.Replace('@@K@@', '11') -TimeoutSec 300)" -replace '(?s).*DIFF=([^\r\n]*).*', '$1'
                Start-Sleep -Seconds 1
                $R.cp03Notices = @(Notices $mark)
            }
            Step 'cp04' {
                # 3 files and a folder with 2 files (4 MB each) on the admin's PC
                $R.cp04Source = @(Invoke-LabGuest -Name $VmName -ScriptBlock {
                        $d = 'C:\lab\clip'; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue
                        $null = New-Item -ItemType Directory -Path "$d\folder" -Force
                        $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
                        foreach ($f in 'f1.bin', 'f2.bin', 'f3.bin', 'folder\inner1.bin', 'folder\inner2.bin') { $b = New-Object byte[] (4MB); $rng.GetBytes($b); [IO.File]::WriteAllBytes("$d\$f", $b) }
                        Get-ChildItem $d -Recurse -File | ForEach-Object { $_.FullName.Substring($d.Length + 1) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object
                    })
                $mark = NoticeMark
                $null = SetFileList $VmName 'Console' @('C:\lab\clip\f1.bin', 'C:\lab\clip\f2.bin', 'C:\lab\clip\f3.bin', 'C:\lab\clip\folder')
                Start-Sleep -Seconds 4
                # what the device's clipboard offers before the paste (no file reached either way in the run of 16.10.2026 13:55)
                $R.cp04DeviceClip = ClipFormats $dn 'User'
                $R.cp04Arrived = @(PasteAt $dn 'User' 'cp04' 5)
                Start-Sleep -Seconds 3
                $R.cp04Notices = @(Notices $mark)
            }
            Step 'cp05' {
                $R.cp05Source = @(Invoke-LabGuest -Name $dn -ScriptBlock {
                        $d = 'C:\Users\Public\cp05'; $null = New-Item -ItemType Directory -Path "$d\folder" -Force
                        foreach ($f in 'a.txt', 'b.txt', 'c.txt', 'folder\d.txt') { [IO.File]::WriteAllText("$d\$f", "from the device $f $([guid]::NewGuid())") }
                        Get-ChildItem $d -Recurse -File | ForEach-Object { $_.FullName.Substring($d.Length + 1) + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object
                    })
                $mark = NoticeMark
                $null = SetFileList $dn 'User' @('C:\Users\Public\cp05\a.txt', 'C:\Users\Public\cp05\b.txt', 'C:\Users\Public\cp05\c.txt', 'C:\Users\Public\cp05\folder')
                Start-Sleep -Seconds 4
                $R.cp05ClientClip = ClipFormats $VmName 'Console'
                $R.cp05Arrived = @(PasteAt $VmName 'Console' 'cp05' 4)
                Start-Sleep -Seconds 2
                $R.cp05Notices = @(Notices $mark)
            }
            Step 'cp06' {
                $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $null = New-Item -ItemType Directory -Path 'C:\lab\big' -Force; $fs = [IO.File]::Create('C:\lab\big\big101.bin'); $fs.SetLength(101MB); $fs.Dispose() }
                # the device's clipboard holds text: a paste there can only bring what this copy sends
                $null = Invoke-LabPerson -Name $dn -Do clip-set -Value 'cp06 guard'
                $mark = NoticeMark
                $null = SetFileList $VmName 'Console' @('C:\lab\big\big101.bin')
                Start-Sleep -Seconds 4
                $R.cp06Arrived = @(PasteAt $dn 'User' 'cp06' 1 20)
                $R.cp06Notices = @(Notices $mark)
                $R.cp06Partial = @(Invoke-LabGuest -Name $dn -ScriptBlock { @(Get-ChildItem 'C:\Users\*\AppData\Local\Temp' -Recurse -File -ErrorAction SilentlyContinue | Where-Object Length -gt 1MB | Where-Object { $_.FullName -match 'RemoteHub' } | ForEach-Object FullName) })
            }
            Step 'keys' {
                # two windows on the device: Alt+Tab switches between them there (either may be in front first)
                DeviceNotepad 'C:\Users\Public\keys-b.txt'
                DeviceNotepad 'C:\Users\Public\keys-a.txt' -Keep
                FocusTab
                $R.keysFgBefore = (Windows $dn 'User').Fg
                Combo 0x12, 0x09
                Start-Sleep -Seconds 2
                $R.keysFgAfter = (Windows $dn 'User').Fg
                $R.keysClientFg = (Windows $VmName 'Console').Fg
                Combo 0x5B, 0x52
                Start-Sleep -Seconds 3
                $R.keysRunDevice = @((Windows $dn 'User').All | Where-Object { $_ -eq 'Run' }).Count
                $R.keysRunClient = @((Windows $VmName 'Console').All | Where-Object { $_ -eq 'Run' }).Count
                Esc
                # Win+L stays with this PC: it locks the client, not the device
                Combo 0x5B, 0x4C
                Start-Sleep -Seconds 6
                $R.keysClientLocked = LockedCount $VmName
                $R.keysDeviceLocked = LockedCount $dn
                Unlock-LabSession -Name $VmName -Account Console
                Start-Sleep -Seconds 3
            }
            Step 'physical-cad' {
                FocusTab
                Send-LabCtrlAltDel -Name $VmName
                Start-Sleep -Seconds 5
                $p = Join-Path $EvidenceDir 'ui-desktop-physical-cad-client.png'
                $null = Get-LabScreenshot -Name $VmName -Path $p
                $R.cadClientOcr = Ocr $p
                $R.cadDeviceLogonUi = LockedCount $dn
                $null = DevShot 'physical-cad'
                Key 'Escape'
                Start-Sleep -Seconds 3
            }
            Step 'sas01' {
                Invoke-LabGuest -Name $dn -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
                FocusTab 0.5 0.01
                $null = Ui 'Send Ctrl+Alt+Del'
                Start-Sleep -Seconds 5
                $R.sasOptions = Ocr (TabPicture 'sas-options')
                # Esc for the device: the tab has the focus (in the run of 16.10.2026 it went to another window and the
                # security options stayed on the device through the Fit to window step)
                FocusTab 0.5 0.01
                Esc
                Start-Sleep -Seconds 4
                $R.sasBack = Ocr (TabPicture 'sas-back')
            }
            Step 'fit' {
                $R.fitMonitors = Ui 'Monitor of the device' -Value read -Optional -TimeoutSec 10
                $R.fitOn = DeviceRes
                $R.fitTipOn = (Ui '*follows this window*' -Value exists -Optional -TimeoutSec 10).found
                # the resolution button opens a menu (a popup window of its own, hence -Title '*')
                $null = Ui '*follows this window*'
                $null = Ui '*own resolution' -Title '*'
                Start-Sleep -Seconds 8
                $R.fitOff = DeviceRes
                $null = Ui 'Change the device*resolution*'
                $null = Ui 'Fit to this window' -Title '*'
                Start-Sleep -Seconds 8
                $R.fitOnAgain = DeviceRes
                # larger than the tab: the device gets the size (or the nearest it offers), the picture is scaled down
                $null = Ui '*follows this window*'
                $null = Ui '3840*2160' -Title '*'
                $R.fitLargeChanged = WaitFor -TimeoutSec 30 -Every 2 { $R.fitLarge = DeviceRes; $R.fitLarge -ne $R.fitOnAgain }
                Shot 'fit-large'
                $null = Ui 'The device runs at *'
                $null = Ui 'Fit to this window' -Title '*'
                Start-Sleep -Seconds 8
                # A larger tab: this PC's screen made larger (1600x900), RemoteHub maximized, the navigation hidden. At
                # 1024x768 even the tab without navigation (868x496 asked) holds no listed mode of the device but 640x480 -
                # the agent takes the largest listed mode that fits (ScreenCapture.cs DisplayModes.Choose), so "wider" could
                # never show (the run of 16.10.2026 13:55).
                $R.fitClientRes = ClientRes 1600 900
                $max = Ui 'TitleBarMaximizeButton' -Optional -TimeoutSec 10
                $null = Ui 'Hide navigation'
                Start-Sleep -Seconds 8
                $R.fitWide = DeviceRes
                Shot 'fit-wide'
                $null = Ui '*navigation' -Optional
                # back: the window restored, this PC's own resolution
                if ($max.found) { $null = Ui 'TitleBarMaximizeButton' -Optional -TimeoutSec 10 }
                if ($R.fitClientRes.Was -match '^\d+x\d+$') { $wh = $R.fitClientRes.Was -split 'x'; $null = ClientRes ([int]$wh[0]) ([int]$wh[1]) }
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'RemoteHub*' -Text 'NewButton' -Value fit -TimeoutSec 10
                Start-Sleep -Seconds 4
                if ($R.fitMonitors.enabled) {
                    # more than one monitor: each one, then all of them (the combo box by its tooltip)
                    $null = Ui 'Monitor of the device' -Value click
                    $R.fitMonitor2 = Ui 'Monitor 2*' -Value click -Optional
                    Start-Sleep -Seconds 5
                    $R.fitMonitor2Shot = TabPicture 'monitor-2'
                    $null = Ui 'Monitor of the device' -Value click
                    $R.fitAll = Ui 'All monitors*' -Value click -Optional
                    Start-Sleep -Seconds 5
                    $R.fitAllShot = TabPicture 'monitor-all'
                }
            }
            Step 'net' {
                FocusTab
                $since = Get-LabGuestTime -Name $dn
                $mark = NoticeMark
                # The 30 s count from the moment the adapter is off, and each check's time is taken when its answer came:
                # in the run of 16.10.2026 21:25 the clock started before Disconnect-LabNetwork and stopped after a second
                # UI lookup, so 38.9 s were measured while the tab never showed a reconnect (no notice, no event 1103).
                # Every check is written down (seconds since the cut, live, reconnecting).
                $switches = Disconnect-LabNetwork -Name $dn
                $cut = Get-Date
                try { Start-Sleep -Seconds 10 } finally { Connect-LabNetwork -Name $dn -Switches $switches; $R.netRestored = [math]::Round(((Get-Date) - $cut).TotalSeconds, 1) }
                $R.netBack = $null
                $R.netPolls = New-Object System.Collections.Generic.List[string]
                $null = WaitFor -TimeoutSec 90 -Every 1 {
                    $rec = Ui 'Stop reconnecting' -Value exists -Optional -TimeoutSec 1
                    $live = Live 2
                    $at = [math]::Round(((Get-Date) - $cut).TotalSeconds, 1)
                    $R.netPolls.Add("$at live=$($live.found) reconnecting=$($rec.found)")
                    if ($live.found -and -not $rec.found) { $R.netBack = $at; return $true }
                    $false
                }
                Start-Sleep -Seconds 3
                $R.netNotices = @(Notices $mark)
                $R.netConsent = @(Invoke-LabPerson -Name $dn -Do windows | Where-Object { $_.Title -ne 'RemoteHub' } | ForEach-Object Title)
                $R.netEvents = @(Get-LabAgentEvent -Name $dn -Since $since -Id 1103)
            }
            Step 'lock' {
                FocusTab
                $mark = NoticeMark
                Lock-LabSession -Name $dn
                Start-Sleep -Seconds 5
                $null = Ui 'Send Ctrl+Alt+Del'
                Start-Sleep -Seconds 5
                $R.lockOcr = Ocr (TabPicture 'lock-password')
                # copy on the admin's PC and Ctrl+V into the password field: nothing reaches the device's clipboard
                $R.lockText = "while locked $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $R.lockText
                Start-Sleep -Seconds 3
                Combo 0x11, 0x56
                Start-Sleep -Seconds 2
                $R.lockDeviceClip = Invoke-LabPerson -Name $dn -Do clip-get
                # copied on the device while locked: nothing comes back
                $R.lockDevText = "device locked $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $dn -Do clip-set -Value $R.lockDevText
                Start-Sleep -Seconds 4
                $R.lockClientClip = Invoke-LabPerson -Name $VmName -Account Console -Do clip-get
                # the password typed in the tab unlocks; then the tab waits for normaluser's answer. The field is emptied
                # first with Backspace as well: if the Ctrl+V above did paste, Ctrl+A + one Backspace left most of it in
                # front of the password (the run of 16.10.2026 13:55: "The password is incorrect", the device stayed
                # locked for every later case)
                FocusTab 0.5 0.01
                Combo 0x11, 0x41
                foreach ($i in 1..60) { Send-LabKey -Name $VmName -Key 'Back' }
                TypeText $password
                Key 'Enter'
                # normaluser allowed this connection before locking: the user who allowed it isn't asked again after the
                # unlock (agent-desktop-draft.md section 4.2 step 4; DesktopEndpoint.cs HoldUserDesktop) - the tab goes on.
                # (The run of 16.10.2026 21:25 expected a question: no dialog came, the tab stayed live.) Someone else
                # unlocking or signing in is asked - D-LOCK-02 and UI-SAS-03 cover that. A dialog that comes anyway is
                # written down and answered, so the next steps can go on.
                $R.lockUnlocked = (WaitFor -TimeoutSec 60 { (LockedCount $dn) -eq 0 })
                Start-Sleep -Seconds 8
                $R.lockAsked = @(Invoke-LabPerson -Name $dn -Do windows | Where-Object { $_.Title -and $_.Title -ne 'RemoteHub' } | ForEach-Object { "$($_.Title)" })
                if ($R.lockAsked.Count) { $R.lockAllow = Allow }
                $R.lockLive = Live 90
                $R.lockNotices = @(Notices $mark)
                # unlocked and allowed: copy and paste work again
                $R.lockAfterText = "after unlock $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $R.lockAfterText
                Start-Sleep -Seconds 4
                $R.lockAfterClip = Invoke-LabPerson -Name $dn -Do clip-get
            }
            # whatever happened above: the device unlocked for the next cases (a locked session changes the connect page's
            # choices - "When someone unlocks or signs in: ask them" - and every later step failed on it), the tab live again
            try {
                if ((LockedCount $dn) -gt 0) {
                    $R.lockCleanup = 'unlocked at the device'
                    Unlock-LabSession -Name $dn -Account User
                    $null = Allow -TimeoutSec 30
                    $null = Live 30
                }
            }
            catch { $R.lockCleanup = "unlock failed: $_" }
            Step 'uac' {
                FocusTab 0.5 0.01
                $uac = Start-LabInSession -Name $dn -ScriptBlock { Start-Process cmd.exe -ArgumentList '/k title RHLAB-ELEVATED' -Verb RunAs }
                $null = WaitFor -TimeoutSec 60 -Every 2 { Invoke-LabGuest -Name $dn -ScriptBlock { [bool](Get-Process consent -ErrorAction SilentlyContinue) } }
                Start-Sleep -Seconds 4
                $R.uacOcr = Ocr (TabPicture 'uac')
                # normaluser isn't an administrator: the prompt offers the device's administrators with the first one (the
                # console account, "Admin") chosen and its password field focused - so only the password is typed (as in
                # D-SECURE's D-UAC-01). The run of 16.10.2026 21:25 typed "PSAdmin<Tab>password" into that field and
                # nothing was elevated (ui-desktop-uac-tab.png: "Admin", Password).
                TypeText $password
                Key 'Enter'
                Start-Sleep -Seconds 10
                $R.uacElevated = @(Invoke-LabGuest -Name $dn -ScriptBlock {
                        @(Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object CommandLine -match 'RHLAB-ELEVATED' | ForEach-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User })
                    })
                Invoke-LabGuest -Name $dn -ScriptBlock { Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object CommandLine -match 'RHLAB-ELEVATED' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force } }
                Stop-LabInSession -Name $dn -Task $uac
            }
            # A prompt left open keeps normaluser's session on the secure desktop: the agent's consent dialog of the next
            # connection can't be read there (UI-AD-05's Deny: OCR read nothing, the question ran out after 30 s in the
            # run of 16.10.2026 21:25) - closed whatever happened above
            try { $R.uacLeftOpen = Invoke-LabGuest -Name $dn -ScriptBlock { $p = @(Get-Process consent -ErrorAction SilentlyContinue); $p | Stop-Process -Force -ErrorAction SilentlyContinue; $p.Count } }
            catch { $R.uacLeftOpen = "not checked: $_" }
            Step 'end' {
                $null = Ui 'Disconnect'
                Start-Sleep -Seconds 10
                $R.resAfter = DeviceRes
                CloseTabs
            }
            Save 'desktop' $R
        }

        It 'UI-AD-01 Remote desktop opens the agent tab: "Signed in as ...", the device''s sessions listed, Connected; the picture equals the device''s console' -Tag 'UI-AD-01' {
            NoError 'open'
            $R.openStatus | Should -BeLike 'Signed in as *Choose what to show.'
            ($R.openSessions -join "`n") | Should -Match 'Console'
            $R.live.found | Should -BeTrue
            $R.openSimilarity | Should -BeGreaterOrEqual 0.9
        }

        It 'UI-AD-02 a click into the tab and typing: Notepad on the device gets the text' -Tag 'UI-AD-02' {
            NoError 'control'
            Norm $R.typed | Should -Be 'RemoteHub lab 123 !@#' -Because "the device's Notepad got the focus by: $($R.typedFocus)"
            # the German layout case needs a de-DE device and characters the Hyper-V keyboard can't type (ASCII only)
        }

        It 'UI-AD-03 Resolution menu: fit to window follows the tab (wider tab - wider), the device''s own, 3840x2160 larger than the tab, back after disconnect; monitors as the device has them' -Tag 'UI-AD-03' {
            NoError 'fit'
            NoError 'end'
            $R.fitTipOn | Should -BeTrue -Because 'Fit to window is on by default'
            $R.fitOn | Should -Not -Be $R.resBefore
            $R.fitOff | Should -Be $R.resBefore
            $R.fitLargeChanged | Should -BeTrue -Because "3840x2160 changes the device's resolution (now $($R.fitLarge))"
            [int]($R.fitLarge -split 'x')[0] | Should -BeGreaterThan ([int]($R.fitOnAgain -split 'x')[0]) -Because 'the chosen size is larger than the tab'
            [int]($R.fitWide -split 'x')[0] | Should -BeGreaterThan ([int]($R.fitOnAgain -split 'x')[0])
            $R.resAfter | Should -Be $R.resBefore
            if ($R.fitMonitors.enabled) {
                $R.fitMonitor2.found | Should -BeTrue
                $R.fitAll.found | Should -BeTrue
            }
            else { Set-ItResult -Skipped -Because "the device has one monitor ($($R.fitMonitors.value)): Monitor 2 / All monitors can't be chosen; the Fit to window part passed" }
        }

        It 'UI-AD-04 Alt+Tab and Win+R in the tab act on the device, not this PC; Win+L locks this PC, not the device' -Tag 'UI-AD-04' {
            NoError 'keys'
            # which of the two Notepads is in front before doesn't matter (the one started second needn't get the foreground:
            # in the run of 16.10.2026 21:25 keys-b stayed in front, and Alt+Tab went to keys-a) - Alt+Tab switches to the other
            $R.keysFgBefore | Should -BeLike 'keys-?.txt*'
            $R.keysFgAfter | Should -BeLike 'keys-?.txt*'
            $R.keysFgAfter | Should -Not -Be $R.keysFgBefore
            $R.keysClientFg | Should -BeLike '*RemoteHub'
            $R.keysRunDevice | Should -BeGreaterThan 0
            $R.keysRunClient | Should -Be 0
            $R.keysClientLocked | Should -BeGreaterThan 0
            $R.keysDeviceLocked | Should -Be 0
        }

        It 'UI-CP-01 text copied on the admin''s PC, Ctrl+V in Notepad on the device through the tab: the same text (Unicode, multi-line)' -Tag 'UI-CP-01' {
            NoError 'cp01'
            Norm $R.cp01 | Should -Be (Norm $uni) -Because "Notepad got the focus by: $($R.cp01Focus); the device's clipboard before Ctrl+V: $(if ($R.cp01DeviceClip.sha256 -eq (Sha $uni)) { 'the copied text' } else { "'$($R.cp01DeviceClip.start)'" })"
        }

        It 'UI-CP-02 Ctrl+A, Ctrl+C in Notepad on the device through the tab: the same text on the admin''s PC' -Tag 'UI-CP-02' {
            NoError 'cp02'
            $R.cp02.sha256 | Should -Be (Sha $uni2)
        }

        It 'UI-CP-03 a picture both ways: pixels equal; "... the image from the device" said' -Tag 'UI-CP-03' {
            NoError 'cp03'
            $R.cp03ToDevice | Should -Be '0'
            $R.cp03ToAdmin | Should -Be '0'
            ($R.cp03Notices -join "`n") | Should -Match '(Copied|Pasted) the image from the device'
        }

        It 'UI-CP-04 3 files and a folder copied on the admin''s PC, pasted on the device: all arrive with equal hashes; progress and the device''s confirmation in the tab' -Tag 'UI-CP-04' {
            NoError 'cp04'
            @($R.cp04Arrived | Sort-Object) | Should -Be @($R.cp04Source | Sort-Object) -Because "the device's clipboard offered: $(@($R.cp04DeviceClip) -join ', ') (paste: $(@($R.cp04Paste.formats) -join ', '); $($R.cp04Paste.apartment) thread)"
            ($R.cp04Notices -join "`n") | Should -Match 'Copying 5 files to the device'
            ($R.cp04Notices -join "`n") | Should -Match 'Copied 5 files to the device'
        }

        It 'UI-CP-05 files copied on the device, pasted on the admin''s PC: they arrive, hashes equal' -Tag 'UI-CP-05' {
            NoError 'cp05'
            @($R.cp05Arrived | Sort-Object) | Should -Be @($R.cp05Source | Sort-Object) -Because "this PC's clipboard offered: $(@($R.cp05ClientClip) -join ', ') (paste: $(@($R.cp05Paste.formats) -join ', '); $($R.cp05Paste.apartment) thread)"
            # the sheet: "Copied <n> files from the device - paste them here." - with paste-on-demand the code says "Pasted <n> files ..."
            ($R.cp05Notices -join "`n") | Should -Match '(Copied|Pasted) 4 files from the device'
        }

        It 'UI-CP-06 101 MB: "Too much to copy through the remote desktop ... use the file transfer instead."; nothing on the device' -Tag 'UI-CP-06' {
            NoError 'cp06'
            ($R.cp06Notices -join "`n") | Should -Match 'Too much to copy through the remote desktop \(at most 100 MB and 1000 files\) . use the file transfer instead\.'
            @($R.cp06Arrived) | Should -BeNullOrEmpty
            @($R.cp06Partial) | Should -BeNullOrEmpty
        }

        It 'UI-CP-08 the session locked: nothing to or from the device''s clipboard; after unlock and consent copy and paste work again' -Tag 'UI-CP-08' {
            NoError 'lock'
            $R.lockDeviceClip.sha256 | Should -Not -Be (Sha $R.lockText)
            $R.lockClientClip.sha256 | Should -Not -Be (Sha $R.lockDevText)
            $R.lockAfterClip.sha256 | Should -Be (Sha $R.lockAfterText)
        }

        It 'UI-SAS-01 Send Ctrl+Alt+Del on the desktop: the security options in the tab; Esc back' -Tag 'UI-SAS-01' {
            NoError 'sas01'
            $R.sasOptions | Should -Match 'Lock'
            $R.sasOptions | Should -Match 'Sign out'
            $R.sasOptions | Should -Match 'Task Manager'
            $R.sasBack | Should -Not -Match 'Task Manager'
        }

        It 'UI-SAS-02 the device locked: Send Ctrl+Alt+Del shows the password field, the password typed in the tab unlocks; normaluser, who allowed it, isn''t asked again' -Tag 'UI-SAS-02' {
            NoError 'lock'
            $R.lockOcr | Should -Match ([regex]::Escape($device.UserAccount))
            $R.lockUnlocked | Should -BeTrue
            @($R.lockAsked) | Should -BeNullOrEmpty -Because 'the user who allowed the connection is not asked again after an unlock'
            ($R.lockNotices -join "`n") | Should -Not -Match 'Waiting for .* to allow access'
            $R.lockLive.found | Should -BeTrue
            # the own-account half (connected as normaluser) needs normaluser as an agent user - not covered here
        }

        It 'UI-SAS-04 UAC in normaluser''s session is in the tab; an administrator''s credentials typed there elevate' -Tag 'UI-SAS-04' {
            NoError 'uac'
            $R.uacOcr | Should -Match 'User Account Control|Do you want to allow'
            # the prompt's first administrator is the console account - its password was typed
            @($R.uacElevated) | Should -Contain $device.ConsoleAccount
        }

        It 'UI-SAS-05 Ctrl+Alt+Del on the admin''s own keyboard stays local: this PC''s security screen, nothing on the device' -Tag 'UI-SAS-05' {
            NoError 'physical-cad'
            $R.cadClientOcr | Should -Match 'Task Manager|Sign out'
            $R.cadDeviceLogonUi | Should -Be 0
        }

        # a 10 s cut usually leaves the TCP connection alive (a dead one is noticed through the 30 s keep-alive): the tab
        # either just goes on or reconnects (notice, event 1103) - either way within 30 s and without a new question
        It 'UI-NET-01 the device''s network cut for 10 s: the tab is back within 30 s without asking again' -Tag 'UI-NET-01' {
            NoError 'net'
            $R.netBack | Should -Not -BeNullOrEmpty
            $R.netBack | Should -BeLessOrEqual 30
            @($R.netConsent) | Should -BeNullOrEmpty
        }
    }

    Context 'the connect page: Ask, code, view-only, policy, RDP' {
        BeforeAll {
            Step 'deny' {
                $since = Get-LabGuestTime -Name $dn
                $null = OpenChoose
                ConnectChosen
                $R.denyClick = Allow 'Deny'
                $null = Ui 'Use RDP instead' -Value exists -TimeoutSec 60
                Start-Sleep -Seconds 2
                $R.denyText = TabTexts
                Shot 'deny'
                $R.denyEvents = @(Get-LabAgentEvent -Name $dn -Since $since -Id 2100, 2101)
            }
            Step 'pin' {
                $null = OpenChoose
                ConnectChosen -Consent 'Show them a code*'
                $code = Invoke-LabPerson -Name $dn -Do pin -TimeoutSec 60
                $R.pinCode = $code.pin
                $null = Ui 'type:Edit' -Value "set:$($code.pin)" -TimeoutSec 30
                $null = Ui 'Connect'
                $R.pinLive = Live 60
                # wrong three times
                $null = OpenChoose
                $mark = NoticeMark
                ConnectChosen -Consent 'Show them a code*'
                $real = (Invoke-LabPerson -Name $dn -Do pin -TimeoutSec 60).pin
                $wrong = if ($real -eq '135792') { '246801' } else { '135792' }
                $R.pinLeft = @()
                foreach ($i in 1..3) {
                    $null = Ui 'type:Edit' -Value "set:$wrong" -TimeoutSec 30
                    $null = Ui 'Connect'
                    Start-Sleep -Seconds 4
                    $R.pinLeft += "$((Ui '*attempt* left*' -Value read -Optional -TimeoutSec 3).name)"
                }
                $null = Ui 'Use RDP instead' -Value exists -TimeoutSec 30 -Optional
                $R.pinWrongText = TabTexts
                $R.pinWrongNotices = @(Notices $mark)
                $R.pinWrongLive = Live 3
                Shot 'pin-wrong'
            }
            Step 'viewonly' {
                DeviceNotepad 'C:\Users\Public\ad07.txt'
                $null = OpenChoose
                ConnectChosen -ViewOnly
                $R.voAllow = Allow
                $R.voBadge = Ui 'View only' -Value exists -TimeoutSec 60
                $R.voCad = Live 3
                FocusTab
                TypeText 'view only typed'
                Start-Sleep -Seconds 3
                $R.voTyped = NotepadText
                $R.voText = "view only $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $R.voText
                Start-Sleep -Seconds 4
                $R.voDeviceClip = Invoke-LabPerson -Name $dn -Do clip-get
                Invoke-LabGuest -Name $dn -ScriptBlock { Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force }
            }
            Step 'policy' {
                Set-LabAgentPolicy -Name $dn -Values @{ DesktopClipboard = 1 }
                $null = OpenChoose
                ConnectChosen
                $R.policyAllow = Allow
                $null = Live 60
                Start-Sleep -Seconds 3
                $R.policyText = "text only $([guid]::NewGuid().ToString('N').Substring(0, 8))"
                $null = Invoke-LabPerson -Name $VmName -Account Console -Do clip-set -Value $R.policyText
                Start-Sleep -Seconds 4
                $R.policyDeviceClip = Invoke-LabPerson -Name $dn -Do clip-get
                $mark = NoticeMark
                $null = SetFileList $VmName 'Console' @('C:\lab\clip\f1.bin')
                Start-Sleep -Seconds 5
                $R.policyNotices = @(Notices $mark)
                Invoke-LabGuest -Name $dn -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -Name DesktopClipboard -ErrorAction SilentlyContinue }
            }
            Step 'rdp' {
                $null = OpenChoose
                $null = Ui 'Use RDP instead'
                $R.rdpTab = Ui '*' -Title '*Remote desktop*' -Value exists -TimeoutSec 60 -Optional
                $R.rdpSession = WaitFor -TimeoutSec 120 -Every 5 { (ExplorerOwners $dn) -contains $device.AdminAccount }
                Shot 'rdp'
            }
            Step 'own-rdp' {
                # the RDP tab stays connected; a new agent tab lists its session
                Menu 'Connect'
                $null = Ui 'Signed in as *' -Value exists -TimeoutSec 90
                $R.ownSessions = @((Ui '*' -Value list).items | Where-Object { $_ -like 'RadioButton|*' })
                $null = Ui 'Remote session*' -Value check
                Start-Sleep -Seconds 2
                $R.ownWarning = (Ui 'This session is connected by RDP from this PC*' -Value read -Optional -TimeoutSec 10).name
                Shot 'own-rdp'
                CloseTabs
            }
            Save 'connect-page' $R
        }

        It 'UI-AD-05 Ask: Allow - the tab connects and shows the picture; Deny - the tab ends with the refusal, audit 2101 / 2100 on the device' -Tag 'UI-AD-05' {
            NoError 'open'
            NoError 'deny'
            $R.allow.clicked | Should -BeTrue
            $R.live.found | Should -BeTrue
            $R.denyClick.clicked | Should -BeTrue
            $R.denyText | Should -Match 'refused|didn.t allow'
            @($R.denyEvents).Count | Should -BeGreaterThan 0
        }

        It 'UI-AD-06 Show them a code: the code read at the device and typed into the code box connects; wrong three times - "[n] attempt(s) left." counts down, then refused' -Tag 'UI-AD-06' {
            NoError 'pin'
            $R.pinCode | Should -Match '^\d{6}$'
            $R.pinLive.found | Should -BeTrue
            ((@($R.pinLeft) + @($R.pinWrongNotices)) -join "`n") | Should -Match '2 attempt\(s\) left\.'
            ((@($R.pinLeft) + @($R.pinWrongNotices)) -join "`n") | Should -Match '1 attempt\(s\) left\.'
            $R.pinWrongLive.found | Should -BeFalse -Because 'after the third wrong code the connection is refused'
        }

        It 'UI-AD-07 View only (connect page): badge "View only"; typing doesn''t reach the device; the clipboard is off' -Tag 'UI-AD-07' {
            NoError 'viewonly'
            $R.voBadge.found | Should -BeTrue
            $R.voCad.found | Should -BeFalse
            Norm $R.voTyped | Should -BeNullOrEmpty
            $R.voDeviceClip.sha256 | Should -Not -Be (Sha $R.voText)
        }

        It 'UI-CP-07 DesktopClipboard=1: text arrives; files are refused with "Copying images and files isn''t allowed on this device - only text."' -Tag 'UI-CP-07' {
            NoError 'policy'
            $R.policyDeviceClip.sha256 | Should -Be (Sha $R.policyText)
            ($R.policyNotices -join "`n") | Should -Match 'Copying images and files isn.t allowed on this device . only text\.'
        }

        It 'UI-AD-08 Use RDP instead opens an RDP tab to the same device' -Tag 'UI-AD-08' {
            NoError 'rdp'
            $R.rdpTab.found | Should -BeTrue
            $R.rdpSession | Should -BeTrue
        }

        It 'R-AGENT-02 Remote desktop through Automatic: a double-click opens the agent desktop ("Remote desktop through the RemoteHub Agent"); Use RDP instead opens RDP' -Tag 'R-AGENT-02' {
            NoError 'open'
            NoError 'rdp'
            $R.openHeading | Should -BeTrue
            $R.openWindow | Should -BeLike '*Desktop (agent)*'
            $R.rdpTab.found | Should -BeTrue
        }

        It 'R-AGENT-03 the session this PC is connected to by RDP: picking it warns' -Tag 'R-AGENT-03' {
            NoError 'own-rdp'
            ($R.ownSessions -join "`n") | Should -Match 'Remote session'
            $R.ownWarning | Should -BeLike 'This session is connected by RDP from this PC (*). Its RDP window moves your mouse to follow the remote pointer*'
        }
    }

    Context 'sign-in screen' {
        BeforeAll {
            Step 'signin' {
                CloseTabs
                # nobody signed in; the sign-in screen asks for a user name too (not only the last user's password)
                Invoke-LabGuest -Name $dn -ScriptBlock { Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name DontDisplayLastUserName -Value 1 -Type DWord }
                Disconnect-LabConsole -Name $dn
                $since = Get-LabGuestTime -Name $dn
                $mark = NoticeMark
                $null = OpenChoose
                $R.signinSessions = @((Ui '*' -Value list).items | Where-Object { $_ -like 'RadioButton|*' })
                ConnectChosen -Session 'Console*sign-in screen*' -Consent ''
                $R.signinLive = Live 90
                FocusTab 0.5 0.01
                Start-Sleep -Seconds 2
                $R.signinScreen = Ocr (TabPicture 'signin-screen')
                $null = Ui 'Send Ctrl+Alt+Del'
                Start-Sleep -Seconds 5
                $R.signinFields = Ocr (TabPicture 'signin-fields')
                TypeText $device.AdminAccount
                Key 'Tab'
                TypeText $password
                Key 'Enter'
                $R.signinDesktop = WaitFor -TimeoutSec 180 -Every 5 { (ExplorerOwners $dn) -contains $device.AdminAccount }
                Start-Sleep -Seconds 10
                $R.signinAfterLive = Live 30
                $null = TabPicture 'signin-desktop'
                $R.signinNotices = @(Notices $mark)
                $R.signinEvents = @(Get-LabAgentEvent -Name $dn -Since $since -Id 1102)
                # PSAdmin's windows: only when the sign-in worked (a session task needs the account signed in - 90 s lost otherwise)
                if ($R.signinDesktop) { $R.signinAgentWindows = @(Invoke-LabPerson -Name $dn -Account Admin -Do windows | ForEach-Object Title) }
                CloseTabs
            }
            Invoke-LabGuest -Name $dn -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name DontDisplayLastUserName -ErrorAction SilentlyContinue }
            Save 'signin' $R
        }

        It 'UI-SAS-03 nobody signed in: the sign-in screen in the tab, Ctrl+Alt+Del gives the fields, PSAdmin signs in through the tab - desktop, nobody asked (own account)' -Tag 'UI-SAS-03' {
            NoError 'signin'
            ($R.signinSessions -join "`n") | Should -Match 'sign-in screen'
            $R.signinLive.found | Should -BeTrue
            $R.signinFields | Should -Match 'User name|Password'
            $R.signinDesktop | Should -BeTrue
            $R.signinAfterLive.found | Should -BeTrue
            ($R.signinNotices -join "`n") | Should -Not -Match 'Waiting for .* to allow access'
            @($R.signinEvents | Where-Object { $_.Message -match 'own account' }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'sign-in refused' {
        BeforeAll {
            # PowerShell through the agent; the menu offers it once the agent is known to work (else: the test)
            function PsMessage {
                $m = Ui 'Device' -Value 'menu:Tools>PowerShell' -Optional
                if (-not ($m.PSObject.Properties['error'] -and $m.error)) {
                    Start-Sleep -Seconds 20
                    $t = "$((Ui 'type:Document' -Value read -Optional -TimeoutSec 20).value)`n$(TabTexts)"
                    CloseTabs
                    return $t
                }
                Esc; Esc
                $null = Ui 'Device' -Value 'menu:Tools>Test RemoteHub Agent*' -Optional
                Message -Button 'Cancel' -TimeoutSec 90
            }
            function SetCredential([string]$User, [string]$Password) {
                EditConnection {
                    $null = Ui 'Credentials' -Title 'Edit connection'
                    $null = Ui 'Username' -Title 'Edit connection' -Value "setnext:$User"
                    $null = Ui 'Password' -Title 'Edit connection' -Value "setnext:$Password"
                }
            }
            Step 'refused' {
                CloseTabs
                # known to work first (PowerShell in the menu)
                $t = Ui 'Device' -Value 'menu:Tools>Test RemoteHub Agent*' -Optional
                if ($t.PSObject.Properties['error'] -and $t.error) { Esc; Esc } else { $R.refusedTest = Message -TimeoutSec 90 }
                SetCredential $device.AdminAccount "$password-wrong"
                $R.refusedWrong = PsMessage
                SetCredential $device.UserAccount $password
                $R.refusedUser = PsMessage
            }
            Step 'refused-restore' {
                SetCredential $device.AdminAccount $password
                $t = Ui 'Device' -Value 'menu:Tools>Test RemoteHub Agent*' -Optional
                if ($t.PSObject.Properties['error'] -and $t.error) { Esc; Esc } else { $R.refusedRestored = Message -TimeoutSec 90 }
            }
            Save 'refused' $R
        }

        It 'UI-AG-04 a wrong password: "... rejected the sign-in: wrong user name or password ..."; normaluser: "The account is not allowed to use the RemoteHub Agent ..."' -Tag 'UI-AG-04' {
            NoError 'refused'
            $R.refusedWrong | Should -Match 'rejected the sign-in: wrong user name or password'
            $R.refusedUser | Should -Match 'not allowed to use the RemoteHub Agent'
        }
    }

    Context 'credential provider and automatic sign-in' {
        BeforeAll {
            function Provider([string]$Header, [string]$Button) {
                Menu "RemoteHub Agent>$Header"
                $confirm = Message -Button $Button -TimeoutSec 60
                $done = WaitDone $Header
                $rows = (Ui '*' -Title $Header -Value list -Optional).items
                CloseProgress $Header
                [pscustomobject]@{ Confirm = $confirm; Done = $done.found; Rows = ($rows -join "`n"); State = (Get-LabAgentState -Name $dn).AutoSignIn }
            }
            Step 'provider-install' {
                CloseTabs
                $R.provInstall = Provider 'Install credential provider' 'Install'
            }
            Step 'autosignin' {
                EditConnection {
                    $null = Ui 'PowerShell remoting' -Title 'Edit connection'
                    $null = Ui 'Sign in automatically when nobody is signed in' -Title 'Edit connection' -Value 'choose:On'
                }
                Disconnect-LabConsole -Name $dn
                $since = Get-LabGuestTime -Name $dn
                $mark = NoticeMark
                $null = OpenChoose
                $R.autoBox = Ui 'Sign in automatically as *' -Value read -Optional -TimeoutSec 10
                $null = Ui 'Sign in automatically as *' -Value check -Optional -TimeoutSec 5
                ConnectChosen -Session 'Console*sign-in screen*' -Consent ''
                $R.autoDesktop = WaitFor -TimeoutSec 180 -Every 5 { (ExplorerOwners $dn) -contains $device.AdminAccount }
                Start-Sleep -Seconds 5
                $R.autoLive = Live 30
                $R.autoNotices = @(Notices $mark)
                $R.autoEvents = @(Get-LabAgentEvent -Name $dn -Since $since)
                Shot 'auto-sign-in'
                $null = Ui 'Disconnect' -Optional
                Start-Sleep -Seconds 5
                CloseTabs
            }
            Step 'provider-remove' {
                $R.provRemove = Provider 'Remove credential provider' 'Remove'
            }
            Step 'audit' {
                Menu 'More>Audit log*'
                $R.audit = ((Ui '*' -Title 'Audit log' -Value list -TimeoutSec 60).items -join "`n")
                $null = Ui 'Close' -Title 'Audit log' -Optional
            }
            # UI-AG-07: after RemoteHub installed the provider, the update dialog has the box ticked; unticked -> AUTOSIGNIN=0
            Step 'update0' {
                $R.provInstall2 = Provider 'Install credential provider' 'Install'
                Menu 'RemoteHub Agent>Update RemoteHub Agent*'
                $null = Ui 'Update through the running agent' -Title 'Update RemoteHub Agent' -Value check -TimeoutSec 60
                $R.update0Box = Ui 'Allow automatic sign-in*' -Title 'Update RemoteHub Agent' -Value read -Optional
                $null = Ui 'Allow automatic sign-in*' -Title 'Update RemoteHub Agent' -Value uncheck
                $null = Ui 'Update' -Title 'Update RemoteHub Agent'
                $R.update0Done = WaitDone 'Update RemoteHub Agent'
                $R.update0Rows = (Ui '*' -Title 'Update RemoteHub Agent' -Value list -Optional).items
                CloseProgress 'Update RemoteHub Agent'
                $R.update0CommandLine = UpdateCommandLine
                if (-not $R.update0Done.found) { $R.update0Device = UpdateEvidence 'update0' }
            }
            Save 'provider' $R
        }

        It 'UI-AG-08 Install / Remove credential provider from the menu (audit agent.credential_provider); "Sign in automatically when nobody is signed in" signs in on the sign-in screen (audit agent.auto_sign_in)' -Tag 'UI-AG-08' {
            NoError 'provider-install'
            NoError 'autosignin'
            NoError 'provider-remove'
            NoError 'audit'
            $R.provInstall.Done | Should -BeTrue
            $R.provInstall.Rows | Should -Match 'The credential provider is installed'
            $R.provInstall.State.Provider | Should -BeTrue
            $R.autoBox.value | Should -Be 'On' -Because 'the connection says to sign in automatically'
            $R.autoDesktop | Should -BeTrue
            $R.autoLive.found | Should -BeTrue
            $R.provRemove.Done | Should -BeTrue
            $R.provRemove.Rows | Should -Match 'The credential provider is removed'
            $R.audit | Should -Match 'Credential provider installed or removed'
            $R.audit | Should -Match 'Installed . credential provider'
            $R.audit | Should -Match 'Removed . credential provider'
            $R.audit | Should -Match 'Automatic sign-in'
            $R.audit | Should -Match 'Signed in automatically on the console'
        }

        It 'UI-AG-07 "Allow automatic sign-in": shown (the package has the provider), off for the clipboard way, on through the agent and ticked by default; unticked -> AUTOSIGNIN=0, also after RemoteHub installed it' -Tag 'UI-AG-07' {
            NoError 'update'
            NoError 'update0'
            $setup.autoSignInShown.found | Should -BeTrue
            $setup.autoSignInClipboard.enabled | Should -BeFalse
            $R.updateAutoSignIn.enabled | Should -BeTrue
            $R.updateAutoSignIn.value | Should -Be 'On'
            $R.updateCommandLine | Should -Match 'Command Line:'
            $R.updateCommandLine | Should -Match 'AUTOSIGNIN=0'
            $R.update0Box.value | Should -Be 'On'
            $R.update0Box.enabled | Should -BeTrue
            $R.update0Done.found | Should -BeTrue
            $R.update0CommandLine | Should -Match 'AUTOSIGNIN=0'
        }
    }
}
