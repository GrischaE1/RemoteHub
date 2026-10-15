#Requires -Version 5.1
<#
.SYNOPSIS
    The person at the device, for the lab's remote desktop tests. Runs in a user's interactive session (through
    Invoke-LabInSession / Start-LabInSession) and answers JSON on stdout.

    The agent's dialogs and banner are drawn by the agent itself (no UI Automation), so they are found by their window
    class and read with Windows' OCR (Windows.Media.Ocr); buttons are clicked by their label with real mouse input.

.EXAMPLE
    RhPerson.ps1 -Do windows                      # the agent's windows in this session: title, position, size
    RhPerson.ps1 -Do read -Title 'Remote access*' # the text of the first matching window, word by word
    RhPerson.ps1 -Do click -Text Allow            # clicks the word "Allow" in the agent's dialog (or banner)
    RhPerson.ps1 -Do pin                          # the 6-digit code the PIN dialog shows
    RhPerson.ps1 -Do marker -Out C:\x\marker.jsonl  # a full-screen window recording keys, mouse and clipboard (runs until killed)
    RhPerson.ps1 -Do clip-get | clip-set -Value 'text'
    RhPerson.ps1 -Do screen-read                  # OCR of the whole screen
    RhPerson.ps1 -Do close-window -Title RemoteHub  # WM_CLOSE to the agent window (what Alt+F4 sends)
    RhPerson.ps1 -Do hover -Title RemoteHub         # the mouse onto the window
    RhPerson.ps1 -Do ocr-file -Value C:\x\frame.png   # OCR of a picture file (also on the lab host)
    RhPerson.ps1 -Do uia -Title "RemoteHub*" -Text "Connect" [-Value click|realclick|rightclick|doubleclick|toggle-on|toggle-off|set:text|keys:{ENTER}|read|exists|list]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('windows', 'read', 'click', 'pin', 'marker', 'clip-get', 'clip-set', 'screen-read', 'key', 'close-window', 'hover', 'ocr-file', 'uia')][string]$Do,
    [string]$Title = '*',
    [string]$Text,
    [string]$Value,
    [string]$Out,
    [int]$TimeoutSec = 30
)
$ErrorActionPreference = 'Stop'

$rhNativeSource = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

public static class RhNative
{
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<object[]> Windows(string cls)
    {
        var list = new List<object[]>();
        EnumWindows((h, p) =>
        {
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            if (c.ToString() != cls || !IsWindowVisible(h)) return true;
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            RECT r; GetWindowRect(h, out r);
            if (r.Right - r.Left > 0 && r.Bottom - r.Top > 0) list.Add(new object[] { h.ToInt64(), t.ToString(), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Bitmap Capture(int x, int y, int w, int h)
    {
        var b = new Bitmap(w, h);
        using (var g = Graphics.FromImage(b)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        return b;
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // left down
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // left up
    }
}
'@
# compiled once per user and source (csc takes seconds; each person action is a new process): loaded from TEMP after that
$rhNativeDll = Join-Path $env:TEMP ('RhNative-' + [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($rhNativeSource)), 0, 8).Replace('-', '') + '.dll')
if (-not (Test-Path $rhNativeDll)) {
    try { Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $rhNativeSource -OutputAssembly $rhNativeDll -OutputType Library } catch { }
}
if (Test-Path $rhNativeDll) { Add-Type -Path $rhNativeDll } else { Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $rhNativeSource }
[void][RhNative]::SetProcessDPIAware()

# the answer: to -Out when given (output of a session task is wrapped at 4096 characters), else stdout
function Out-Json($o) {
    $j = $o | ConvertTo-Json -Depth 6 -Compress
    if ($Out -and $Do -ne 'marker') { [IO.File]::WriteAllText($Out, $j) } else { $j }
}

# Windows OCR on a bitmap: words with their boxes (bitmap pixels)
function Get-Ocr([Drawing.Bitmap]$Bitmap) {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
    function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
    # small text reads better enlarged
    $scale = 2
    $big = New-Object Drawing.Bitmap ($Bitmap.Width * $scale), ($Bitmap.Height * $scale)
    $g = [Drawing.Graphics]::FromImage($big); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($Bitmap, 0, 0, $big.Width, $big.Height); $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $big.Save($ms, [Drawing.Imaging.ImageFormat]::Bmp); $big.Dispose()
    $stream = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($ms)
    $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $soft = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if (-not $engine) { throw 'No OCR engine for the user''s languages.' }
    $r = Await ($engine.RecognizeAsync($soft)) ([Windows.Media.Ocr.OcrResult])
    $words = foreach ($line in $r.Lines) {
        foreach ($w in $line.Words) {
            [pscustomobject]@{ Text = $w.Text; X = [int]($w.BoundingRect.X / $scale); Y = [int]($w.BoundingRect.Y / $scale)
                W = [int]($w.BoundingRect.Width / $scale); H = [int]($w.BoundingRect.Height / $scale); Line = $line.Text }
        }
    }
    [pscustomobject]@{ Text = $r.Text; Lines = @($r.Lines | ForEach-Object Text); Words = @($words) }
}

function Get-AgentWindows {
    @([RhNative]::Windows('RemoteHubAgentWindow') | ForEach-Object {
            [pscustomobject]@{ Handle = $_[0]; Title = $_[1]; X = $_[2]; Y = $_[3]; Width = $_[4]; Height = $_[5] }
        } | Where-Object { $_.Title -like $Title -or $_.Title -like "RemoteHub*$Title" })
}

# waits for a matching agent window and reads it
function Read-AgentWindow {
    $until = (Get-Date).AddSeconds($TimeoutSec)
    do {
        # dialogs first (the banner is titled just "RemoteHub")
        $w = Get-AgentWindows | Sort-Object { if ($_.Title -eq 'RemoteHub') { 1 } else { 0 } } | Select-Object -First 1
        if ($w) {
            Start-Sleep -Milliseconds 400   # fade-in
            $bmp = [RhNative]::Capture($w.X, $w.Y, $w.Width, $w.Height)
            try { $ocr = Get-Ocr $bmp } finally { $bmp.Dispose() }
            return [pscustomobject]@{ Window = $w; Ocr = $ocr }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $until)
    $null
}

switch ($Do) {
    'windows' { Out-Json @(Get-AgentWindows) }
    'read' {
        $r = Read-AgentWindow
        if (-not $r) { Out-Json @{ found = $false }; break }
        Out-Json @{ found = $true; title = $r.Window.Title; text = $r.Ocr.Text; lines = $r.Ocr.Lines; window = $r.Window }
    }
    'click' {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $done = $null
        do {
            $r = Read-AgentWindow
            if ($r) {
                # OCR may glue punctuation or a countdown to the label ("Allow(29)", "Deny."): compare the letters only
                $word = $r.Ocr.Words | Where-Object { $_.Text -eq $Text -or ($_.Text -replace '[^\p{L}\p{Nd}]+.*$', '') -eq $Text -or ($_.Text -replace '^[^\p{L}\p{Nd}]+', '') -eq $Text } |
                    # a button whose whole label is the text first ("Allow", not the "Allow" of "Allow and hide apps..."), then the last one
                    Sort-Object { if (($_.Line -replace '[^\p{L}\p{Nd} ]', '').Trim() -eq $Text) { 0 } else { 1 } }, { -$_.Y } | Select-Object -First 1
                if ($word) {
                    $x = $r.Window.X + $word.X + [int]($word.W / 2); $y = $r.Window.Y + $word.Y + [int]($word.H / 2)
                    # -Value locate: only where (the lab clicks with the console mouse - the agent's windows are SYSTEM's, UIPI drops input sent from here)
                    if ($Value -ne 'locate') { [RhNative]::Click($x, $y) }
                    $done = @{ clicked = $true; text = $Text; x = $x; y = $y; title = $r.Window.Title; lines = $r.Ocr.Lines }
                }
            }
            if (-not $done) { Start-Sleep -Milliseconds 500 }
        } while (-not $done -and (Get-Date) -lt $until)
        if (-not $done) { $done = @{ clicked = $false; text = $Text; lines = $(if ($r) { $r.Ocr.Lines } else { @() }) } }
        Out-Json $done
    }
    'pin' {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $pin = $null
        do {
            $r = Read-AgentWindow
            if ($r) {
                # the code is drawn as large, spaced digits: OCR returns them as single words, not always in reading
                # order - the tallest digit words on one row, left to right
                $digits = @($r.Ocr.Words | Where-Object { $_.Text -match '^\d+$' })
                if ($digits) {
                    $big = ($digits | Measure-Object H -Maximum).Maximum
                    $row = @($digits | Where-Object { $_.H -ge $big * 0.7 })
                    $mid = @($row | Sort-Object Y)[[int][math]::Floor($row.Count / 2)].Y
                    $text = -join @($row | Where-Object { [math]::Abs($_.Y - $mid) -le $big } | Sort-Object X | ForEach-Object Text)
                    if ($text -match '^\d{6}$') { $pin = $text }
                }
                if (-not $pin -and ($r.Ocr.Text -replace '[^0-9 ]', '' -replace '\s+', ' ') -match '(\d{3}\s?\d{3})') { $pin = $Matches[1] -replace '\s', '' }
            }
            if (-not $pin) { Start-Sleep -Milliseconds 500 }
        } while (-not $pin -and (Get-Date) -lt $until)
        Out-Json @{ pin = $pin; lines = $(if ($r) { $r.Ocr.Lines } else { @() }) }
    }
    'screen-read' {
        Add-Type -AssemblyName System.Windows.Forms
        $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = [RhNative]::Capture($b.X, $b.Y, $b.Width, $b.Height)
        try { $ocr = Get-Ocr $bmp } finally { $bmp.Dispose() }
        Out-Json @{ text = $ocr.Text; lines = $ocr.Lines }
    }
    'clip-get' {
        Add-Type -AssemblyName System.Windows.Forms
        $t = [Windows.Forms.Clipboard]::GetText()
        $sha = if ($t) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($t))).Replace('-', '') } else { $null }
        Out-Json @{ length = $t.Length; sha256 = $sha; start = $(if ($t) { $t.Substring(0, [Math]::Min(100, $t.Length)) } else { '' })
            formats = @([Windows.Forms.Clipboard]::GetDataObject().GetFormats()); files = @([Windows.Forms.Clipboard]::GetFileDropList()) }
    }
    'clip-set' {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.Clipboard]::SetText($Value)
        Out-Json @{ set = $Value.Length }
    }
    'close-window' {
        $w = @(Get-AgentWindows)
        foreach ($x in $w) { [void][RhNative]::PostMessageW([IntPtr]$x.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }   # WM_CLOSE
        Start-Sleep -Seconds 2
        Out-Json @{ closed = $w.Count; left = @(Get-AgentWindows).Count }
    }
    'hover' {
        $w = Get-AgentWindows | Select-Object -First 1
        $before = $w
        if ($w) { [void][RhNative]::SetCursorPos($w.X + [int]($w.Width / 2), $w.Y + [int]($w.Height / 2)) }
        Start-Sleep -Seconds 2
        Out-Json @{ before = $before; after = (Get-AgentWindows | Select-Object -First 1) }
    }
    'ocr-file' {
        $bmp = [Drawing.Bitmap]::FromFile($Value)
        try { $ocr = Get-Ocr $bmp } finally { $bmp.Dispose() }
        Out-Json @{ text = $ocr.Text; lines = $ocr.Lines; words = @($ocr.Words | ForEach-Object { @{ text = $_.Text; x = $_.X; y = $_.Y; w = $_.W; h = $_.H } }) }
    }
    'uia' {
        # UI Automation of a normal window (RemoteHub, an installer dialog, Settings): -Title the window (wildcards),
        # -Text the element's name (wildcards), -Value what to do: click (Invoke / Toggle / Select / Expand, else a
        # real click), toggle-on, toggle-off, set:<text>, read (the element's value or name), exists. Waits up to
        # -TimeoutSec for the element.
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
        $A = [Windows.Automation.AutomationElement]
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $found = $null; $win = $null
        do {
            $tops = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition))
            # RemoteHub's dialogs are owned windows: UIA lists them inside the main window, not at the top - those first
            $isWin = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
            # the main window is "RemoteHub", or "<tab> . <kind> - RemoteHub" while a tab is selected: 'RemoteHub*' means both
            $isMain = { param($n) $n -like 'RemoteHub*' -or $n -like '* RemoteHub' }
            $owned = @($tops | Where-Object { & $isMain $_.Current.Name } | ForEach-Object { @($_.FindAll([Windows.Automation.TreeScope]::Descendants, $isWin)) })
            $wins = @(@($owned | Where-Object { $_.Current.Name -like $Title -and $Title -ne '*' }) +
                @($tops | Where-Object { $_.Current.Name -like $Title -or ($Title -eq 'RemoteHub*' -and (& $isMain $_.Current.Name)) }))
            foreach ($w in $wins) {
                $hit = @($w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
                        Where-Object {
                            # "type:Edit": an element of that control type with neither a name nor an id (the PowerShell
                            # tab's input line and output); otherwise the name, the id or the tooltip (HelpText - RemoteHub's
                            # icon buttons have no name)
                            if ($Text -like 'type:*') { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$(($Text.Substring(5) -split '#')[0])" -and -not $_.Current.Name -and -not $_.Current.AutomationId }
                            else { $_.Current.Name -like $Text -or $_.Current.AutomationId -like $Text -or ($_.Current.HelpText -and $_.Current.HelpText -like $Text) }
                        })
                $visible = @($hit | Where-Object { -not $_.Current.IsOffscreen })
                # a plain click is an Invoke, which works off the screen too: a button below the screen still counts
                $hit = if ($visible -or ($Value -and $Value -ne 'click')) { $visible } else { @($hit | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' }) }
                # "type:Edit#2": the second one in tree order (the file transfer tab's remote address box)
                if ($Text -match '^type:.*#(\d+)$') { $hit = @($hit | Select-Object -Skip ([int]$Matches[1] - 1) -First 1) }
                elseif ($hit.Count -gt 1) {
                    # a name in the connection list and in a tab header: the list's item first
                    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
                    $hit = @($hit | Sort-Object { $p = $walker.GetParent($_); if ($_.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -or ($p -and $p.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem')) { 0 } else { 1 } })
                }
                if ($hit) { $found = $hit[0]; $win = $w; break }
            }
            if (-not $found) { Start-Sleep -Milliseconds 500 }
        } while (-not $found -and (Get-Date) -lt $until)
        $action = if ($Value) { $Value } else { 'click' }
        if (-not $found) { Out-Json @{ found = $false; title = $Title; text = $Text; windows = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }; break }
        $pat = { param($p) $o = $null; if ($found.TryGetCurrentPattern($p, [ref]$o)) { $o } else { $null } }
        $result = @{ found = $true; name = $found.Current.Name; type = $found.Current.ControlType.ProgrammaticName; window = $win.Current.Name; enabled = $found.Current.IsEnabled }
        $center = { $r = $found.Current.BoundingRectangle; @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
        switch -Wildcard ($action) {
            'exists' { }
            'fit' {
                # the element's window moved to the top left and kept within the screen, so real clicks reach all of it
                # (maximized, RemoteHub's dialogs grow with it and their buttons end up below the screen)
                $tp = $null
                if ($win.TryGetCurrentPattern([Windows.Automation.TransformPattern]::Pattern, [ref]$tp)) {
                    $wa = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
                    $r = $win.Current.BoundingRectangle
                    if ($tp.Current.CanMove) { $tp.Move($wa.X, $wa.Y) }
                    # a little shorter than the screen: the dialogs are about as large as their owner
                    if ($tp.Current.CanResize) { $tp.Resize([math]::Min($r.Width, $wa.Width - 40), [math]::Min($r.Height, $wa.Height - 60)) }
                }
                $result.value = "$($win.Current.BoundingRectangle)"
            }
            'rightclick' { $c = & $center; [void][RhNative]::SetCursorPos($c[0], $c[1]); Start-Sleep -Milliseconds 150; [RhNative]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [RhNative]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero) }
            { $_ -like 'menu:*' -or $_ -like 'pick:*' } {
                # menu: right-click, pick: left-click (a drop-down button) - then the menu's entry in the same process (a new
                # process takes the focus and the menu closes); "Parent>Entry" opens a submenu first
                $c = & $center; [void][RhNative]::SetCursorPos($c[0], $c[1]); Start-Sleep -Milliseconds 150
                if ($action -like 'pick:*') { $ip = & $pat ([Windows.Automation.InvokePattern]::Pattern); if ($ip) { $ip.Invoke() } else { [RhNative]::Click($c[0], $c[1]) } } else { [RhNative]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60; [RhNative]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero) }
                $isItem = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::MenuItem)
                $path = @($action.Substring(5) -split '>')
                foreach ($entry in $path) {
                    $item = $null
                    $last = $entry -eq $path[-1]
                    # a parent that isn't there is skipped (the tree's menu lists the tools flat, a tab's in groups)
                    $menuUntil = (Get-Date).AddSeconds($(if ($last) { 10 } else { 3 }))
                    do {
                        Start-Sleep -Milliseconds 300
                        $item = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $isItem) | Where-Object { $_.Current.Name -like $entry -and -not $_.Current.IsOffscreen }) | Select-Object -First 1
                    } while (-not $item -and (Get-Date) -lt $menuUntil)
                    if (-not $item -and -not $last) { continue }
                    if (-not $item) {
                        $result.error = "no menu entry '$entry'"
                        $result.entries = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, $isItem) | ForEach-Object { $_.Current.Name })
                        [RhNative]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero); [RhNative]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero)
                        break
                    }
                    if (-not $item.Current.IsEnabled) {
                        # switched off: say so, with its tooltip (RemoteHub's menus say there why an entry is off)
                        $result.error = "menu entry '$entry' is disabled"
                        $result.help = $item.Current.HelpText
                        [RhNative]::keybd_event(0x1B, 0, 0, [IntPtr]::Zero); [RhNative]::keybd_event(0x1B, 0, 2, [IntPtr]::Zero)
                        break
                    }
                    $o = $null
                    if ($item.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$o) -and $entry -ne ($action.Substring(5) -split '>')[-1]) { $o.Expand() }
                    elseif ($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$o)) { $o.Invoke() }
                    else { $r = $item.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
                    $result.value = $item.Current.Name
                    $result.help = $item.Current.HelpText
                }
            }
            'doubleclick' { $c = & $center; [RhNative]::Click($c[0], $c[1]); Start-Sleep -Milliseconds 80; [RhNative]::Click($c[0], $c[1]) }
            'realclick' { $c = & $center; [RhNative]::Click($c[0], $c[1]) }
            'keys:*' { $found.SetFocus(); Start-Sleep -Milliseconds 200; [Windows.Forms.SendKeys]::SendWait($action.Substring(5)) }
            { $_ -like 'setnext:*' -or $_ -like 'choose:*' } {
                # RemoteHub's form fields have no names: the label (-Text) is a separate element before them - the next
                # Edit (setnext) or ComboBox (choose) after it in tree order is the field
                $all = @($win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
                $at = [array]::IndexOf($all, ($all | Where-Object { [Windows.Automation.Automation]::Compare($_, $found) } | Select-Object -First 1))
                $want = if ($action -like 'setnext:*') { 'ControlType.Edit' } else { 'ControlType.ComboBox' }
                $field = $all | Select-Object -Skip ($at + 1) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq $want -and $_.Current.IsEnabled } | Select-Object -First 1
                if (-not $field) { $result.error = "no $want after '$Text'"; break }
                if ($action -like 'setnext:*') {
                    $text = $action.Substring(8)
                    $vp = $null
                    if ($field.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($text) }
                    else { $field.SetFocus(); [Windows.Forms.SendKeys]::SendWait('^a'); [Windows.Forms.SendKeys]::SendWait(($text -replace '[+^%~(){}\[\]]', '{$0}')) }
                    $result.value = $text
                }
                else {
                    $item = $action.Substring(7)
                    $ec = $null; if ($field.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) { $ec.Expand(); Start-Sleep -Milliseconds 400 }
                    $li = @($field.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $item }) | Select-Object -First 1
                    if (-not $li) { $li = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like $item }) | Select-Object -First 1 }
                    if (-not $li) { $result.error = "no item '$item'"; if ($ec) { $ec.Collapse() }; break }
                    $sp = $null
                    if ($li.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Select() } else { $r = $li.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
                    if ($ec) { try { $ec.Collapse() } catch { } }
                    $result.value = $li.Current.Name
                }
            }
            { $_ -in 'check', 'uncheck' } {
                $t = & $pat ([Windows.Automation.TogglePattern]::Pattern)
                $sel = & $pat ([Windows.Automation.SelectionItemPattern]::Pattern)
                if (-not $t -and $sel) { if ($action -eq 'check') { $sel.Select() }; $result.value = [string]$sel.Current.IsSelected; break }
                if (-not $t) {
                    # the label text inside a CheckBox / RadioButton: its parent has the pattern
                    $p = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($found)
                    $o = $null; if ($p.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$o)) { $t = $o }
                    elseif ($p.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$o)) { $o.Select(); $result.value = 'selected'; break }
                }
                $want = if ($action -eq 'check') { 'On' } else { 'Off' }
                if ($t -and [string]$t.Current.ToggleState -ne $want) { $t.Toggle() }
                $result.value = if ($t) { [string]$t.Current.ToggleState } else { $null }
            }
            'list' {
                $result.items = @($win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
                        Where-Object { $_.Current.Name -or $_.Current.AutomationId } | Select-Object -First 400 |
                        ForEach-Object { "$($_.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '')|$($_.Current.Name)|$($_.Current.AutomationId)" })
            }
            'read' {
                $v = & $pat ([Windows.Automation.ValuePattern]::Pattern)
                $t = & $pat ([Windows.Automation.TogglePattern]::Pattern)
                $tp = & $pat ([Windows.Automation.TextPattern]::Pattern)
                $result.value = if ($v) { $v.Current.Value } elseif ($tp) { $tp.DocumentRange.GetText(200000) } elseif ($t) { [string]$t.Current.ToggleState } else { $found.Current.Name }
            }
            'toggle-*' {
                $t = & $pat ([Windows.Automation.TogglePattern]::Pattern)
                $want = if ($action -eq 'toggle-on') { 'On' } else { 'Off' }
                if ($t -and [string]$t.Current.ToggleState -ne $want) { $t.Toggle() }
                $result.value = if ($t) { [string]$t.Current.ToggleState } else { $null }
            }
            'set:*' {
                $v = & $pat ([Windows.Automation.ValuePattern]::Pattern)
                if ($v) { $v.SetValue($action.Substring(4)) } else { $found.SetFocus(); [Windows.Forms.SendKeys]::SendWait($action.Substring(4)) }
                $result.value = $action.Substring(4)
            }
            default {
                $i = & $pat ([Windows.Automation.InvokePattern]::Pattern)
                if (-not $i) {
                    # the label inside a button (WPF lists both with the same name): the button's Invoke
                    $p = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($found)
                    $o = $null; if ($p -and $p.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$o)) { $i = $o }
                }
                $s = & $pat ([Windows.Automation.SelectionItemPattern]::Pattern)
                $e = & $pat ([Windows.Automation.ExpandCollapsePattern]::Pattern)
                $t = & $pat ([Windows.Automation.TogglePattern]::Pattern)
                # a button that opens a modal dialog (Dialogs.Confirm): Invoke may come back with an error while the dialog
                # is open - the click happened
                if ($i) { try { $i.Invoke() } catch { $result.invokeError = $_.Exception.InnerException.Message } } elseif ($t) { $t.Toggle() } elseif ($s) { $s.Select() } elseif ($e) { $e.Expand() }
                else { $r = $found.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
            }
        }
        Out-Json $result
    }
    'key' {
        # a key pressed at the device's own keyboard (virtual-key code in -Value, e.g. 0x1B Esc, 0x0D Enter)
        $vk = [byte][Convert]::ToInt32($Value, 16)
        [RhNative]::keybd_event($vk, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 50; [RhNative]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
        Out-Json @{ key = $Value }
    }
    'marker' {
        # A full-screen window in a known colour that records what reaches it - the remote input and clipboard as
        # the session sees them. One JSON line per event in -Out.
        Add-Type -AssemblyName System.Windows.Forms
        $log = [IO.StreamWriter]::new($Out, $true, [Text.Encoding]::UTF8); $log.AutoFlush = $true
        $sw = [Diagnostics.Stopwatch]::StartNew()
        function W($o) { $o['ms'] = $sw.ElapsedMilliseconds; $log.WriteLine(($o | ConvertTo-Json -Compress)) }
        $f = New-Object Windows.Forms.Form
        $f.Text = 'RemoteHub lab marker'; $f.BackColor = [Drawing.Color]::FromArgb(255, 0, 255); $f.FormBorderStyle = 'None'
        $f.WindowState = 'Maximized'; $f.TopMost = $true; $f.KeyPreview = $true
        $f.Add_KeyDown({ W @{ e = 'down'; vk = [int]$_.KeyCode; mods = [string]$_.Modifiers } ; $_.Handled = $true; $_.SuppressKeyPress = $false })
        $f.Add_KeyUp({ W @{ e = 'up'; vk = [int]$_.KeyCode } })
        $f.Add_KeyPress({ W @{ e = 'char'; c = [string]$_.KeyChar; code = [int]$_.KeyChar } })
        $f.Add_MouseDown({ W @{ e = 'mdown'; b = [string]$_.Button; x = $_.X; y = $_.Y } })
        $f.Add_MouseUp({ W @{ e = 'mup'; b = [string]$_.Button; x = $_.X; y = $_.Y } })
        $f.Add_MouseMove({ W @{ e = 'move'; x = $_.X; y = $_.Y } })
        $f.Add_MouseWheel({ W @{ e = 'wheel'; d = $_.Delta; x = $_.X; y = $_.Y } })
        $f.Add_Shown({ W @{ e = 'shown'; w = $f.Width; h = $f.Height; sx = $f.Left; sy = $f.Top }; $f.Activate() })
        # horizontal wheel (WM_MOUSEHWHEEL) and clipboard changes come as window messages
        $t = New-Object Windows.Forms.Timer; $t.Interval = 500
        $last = ''
        $t.Add_Tick({
                try { $c = [Windows.Forms.Clipboard]::GetText() } catch { $c = $null }
                if ($c -and $c -ne $script:last) { $script:last = $c; W @{ e = 'clipboard'; length = $c.Length; start = $c.Substring(0, [Math]::Min(60, $c.Length)) } }
            })
        $t.Start()
        [Windows.Forms.Application]::Run($f)
    }
}
