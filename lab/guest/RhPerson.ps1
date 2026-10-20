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
    [Parameter(Mandatory)][ValidateSet('windows', 'read', 'click', 'pin', 'marker', 'clip-get', 'clip-set', 'screen-read', 'key', 'close-window', 'hover', 'ocr-file', 'uia', 'serve')][string]$Do,
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
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class RhNative
{
    // -Do serve: the answer file of the request being run. An exception no one catches (on a thread of UI Automation or
    // the like) ends the process; the handler first writes it there as a serveError, so the lab host reads why.
    public static string CrashOut;
    private static bool _watching;
    public static void WatchCrash()
    {
        if (_watching) return;
        _watching = true;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var o = CrashOut;
            if (string.IsNullOrEmpty(o)) return;
            try
            {
                var m = "the person server crashed: " + Convert.ToString(e.ExceptionObject);
                var j = new StringBuilder("{\"serveError\":\"");
                foreach (var ch in m)
                {
                    if (ch == '"' || ch == '\\') j.Append('\\').Append(ch);
                    else if (ch < ' ' || ch > '~') j.Append("\\u").Append(((int)ch).ToString("x4"));
                    else j.Append(ch);
                }
                j.Append("\"}");
                File.WriteAllText(o + ".part", j.ToString());
                File.Move(o + ".part", o);
            }
            catch { }
        };
    }

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
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);

    // The top-level window of a window (GA_ROOT), and the one at a screen point: a popup over a point (a tooltip,
    // RemoteHub's expanded view rail) is a top-level window of its own.
    public static IntPtr Root(IntPtr h) { return h == IntPtr.Zero ? h : GetAncestor(h, 2); }
    public static IntPtr RootAt(int x, int y) { var p = new POINT(); p.X = x; p.Y = y; return Root(WindowFromPoint(p)); }

    // The agent keeps the banner and the privacy screen out of screen captures (WDA_EXCLUDEFROMCAPTURE): a capture
    // here shows what is below them, only the console picture shows them.
    public static bool Excluded(long h)
    {
        uint a;
        return GetWindowDisplayAffinity(new IntPtr(h), out a) && a != 0;
    }

    // -Do marker: a process started by a task may not take the foreground (SetForegroundWindow's rules), so the
    // marker can be shown without the keyboard. Joining the foreground window's input queue for the call lets it.
    public static bool TakeForeground(IntPtr h)
    {
        var fg = GetForegroundWindow();
        if (fg == h) return true;
        uint pid;
        var other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid);
        var me = GetCurrentThreadId();
        var joined = other != 0 && other != me && AttachThreadInput(me, other, true);
        try { SetForegroundWindow(h); }
        finally { if (joined) AttachThreadInput(me, other, false); }
        return GetForegroundWindow() == h;
    }

    // The foreground window as "title [class] pid N" - who has the keyboard when the marker doesn't.
    public static string ForegroundName()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(none)";
        var t = new StringBuilder(256); GetWindowTextW(h, t, t.Capacity);
        var c = new StringBuilder(256); GetClassNameW(h, c, c.Capacity);
        uint pid; GetWindowThreadProcessId(h, out pid);
        return t + " [" + c + "] pid " + pid;
    }

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
if (-not ('RhNative' -as [type])) {   # already loaded when -Do serve runs the actions in its own process
    if (-not (Test-Path $rhNativeDll)) {
        try { Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $rhNativeSource -OutputAssembly $rhNativeDll -OutputType Library } catch { }
    }
    if (Test-Path $rhNativeDll) { Add-Type -Path $rhNativeDll } else { Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition $rhNativeSource }
}
[void][RhNative]::SetProcessDPIAware()

# the answer: to -Out when given (output of a session task is wrapped at 4096 characters), else stdout
function Out-Json($o) {
    # -InputObject: an empty list is "[]" (piped, Windows PowerShell writes nothing - an answer that looks missing)
    $j = ConvertTo-Json -InputObject $o -Depth 6 -Compress
    # written under another name, then renamed: the reader never sees half an answer
    if ($Out -and $Do -ne 'marker') { [IO.File]::WriteAllText("$Out.part", $j); Move-Item -LiteralPath "$Out.part" -Destination $Out -Force } else { $j }
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

# The PIN dialog's code when OCR read only some of its digits as words. The agent draws six boxes of 52 px, 8 px apart,
# 18 px between the third and the fourth, each digit centred in its box (HelperWindows.cs, Paint; times the DPI scale).
# The digits found give the scale and where the boxes are; the middle of each box (inside its border) is cut out and
# the six put side by side on the box colour, which OCR reads as one number. Answers six digits or $null.
function Read-PinStrip($Window, $Found) {
    $off = @(0..5 | ForEach-Object { 26 + 60 * $_ + $(if ($_ -ge 3) { 10 } else { 0 }) })
    $centres = @($Found | ForEach-Object { $_.X + $_.W / 2 })
    $best = $null
    foreach ($s in 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3) {
        foreach ($c in $centres) {
            foreach ($o in $off) {
                $x0 = $c - $o * $s
                $err = 0
                foreach ($x in $centres) { $err += (@($off | ForEach-Object { [math]::Abs($x0 + $_ * $s - $x) }) | Measure-Object -Minimum).Minimum }
                if (-not $best -or $err -lt $best.Err) { $best = @{ S = $s; X0 = $x0; Err = $err } }
            }
        }
    }
    # every digit found within a few pixels of a box's middle, else the layout isn't the one above
    if ($best.Err / $centres.Count -gt 4 * $best.S) { return $null }
    $s = $best.S
    $top = [int](($Found | Measure-Object Y -Minimum).Minimum - 4 * $s)
    $bottom = [int](($Found | ForEach-Object { $_.Y + $_.H } | Measure-Object -Maximum).Maximum + 4 * $s)
    $half = [int](14 * $s); $m = [int](12 * $s)
    $bmp = [RhNative]::Capture($Window.X, $Window.Y, $Window.Width, $Window.Height)
    try {
        $h = $bottom - $top
        if ($top -lt 0 -or $bottom -gt $bmp.Height) { return $null }
        $strip = New-Object Drawing.Bitmap (6 * 2 * $half + 2 * $m), ($h + 2 * $m)
        try {
            $g = [Drawing.Graphics]::FromImage($strip)
            try {
                # the box colour: just inside the first box, left of its digit
                $g.Clear($bmp.GetPixel([int]($best.X0 + $off[0] * $s - 20 * $s), [int]($top + $h / 2)))
                for ($k = 0; $k -lt 6; $k++) {
                    $cx = [int]($best.X0 + $off[$k] * $s)
                    if ($cx - $half -lt 0 -or $cx + $half -gt $bmp.Width) { return $null }
                    $g.DrawImage($bmp, (New-Object Drawing.Rectangle ($m + $k * 2 * $half), $m, (2 * $half), $h), (New-Object Drawing.Rectangle ($cx - $half), $top, (2 * $half), $h), [Drawing.GraphicsUnit]::Pixel)
                }
            }
            finally { $g.Dispose() }
            $ocr = Get-Ocr $strip
        }
        finally { $strip.Dispose() }
    }
    finally { $bmp.Dispose() }
    $t = (@($ocr.Words | ForEach-Object Text) -join '')
    $t = $t -creplace '[OoQD]', '0' -creplace '[Il|]', '1' -replace '[^0-9]', ''
    if ($t -match '^\d{6}$') { $t } else { $null }
}

switch ($Do) {
    'serve' {
        # Stays in the session and runs the actions asked for in -Value (a folder): <id>.req.json { do, title, text,
        # value, timeout, out } - each one in this process, so a step costs no new process, task or compile (the lab
        # host's Invoke-LabPerson writes the request and waits for the answer file). alive.txt shows it runs. Ends
        # after 30 minutes without a request, on stop.txt, when this script file is replaced (the next request
        # starts the new one), or after an action that didn't finish (see below).
        $q = $Value
        $worker = $null
        $null = New-Item -ItemType Directory -Path $q -Force
        $self = $PSCommandPath
        $stamp = (Get-Item $self).LastWriteTimeUtc
        [RhNative]::WatchCrash()
        $idleUntil = (Get-Date).AddMinutes(30)
        while ((Get-Date) -lt $idleUntil) {
            Set-Content -Path (Join-Path $q 'alive.txt') -Value "$PID $((Get-Date).ToUniversalTime().ToString('o'))"
            if ((Get-Item $self).LastWriteTimeUtc -ne $stamp -or (Test-Path (Join-Path $q 'stop.txt'))) { break }
            $next = Get-ChildItem $q -Filter '*.req.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc | Select-Object -First 1
            if (-not $next) { Start-Sleep -Milliseconds 200; continue }
            $req = $null
            try { $req = [IO.File]::ReadAllText($next.FullName) | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 100; continue }   # still being written
            Remove-Item $next.FullName -ErrorAction SilentlyContinue
            $a = @{ Do = $req.do; Title = $(if ($req.title) { $req.title } else { '*' }); TimeoutSec = [int]$req.timeout; Out = $req.out }
            if ($req.text) { $a.Text = $req.text }
            if ($req.PSObject.Properties['value'] -and $null -ne $req.value) { $a.Value = [string]$req.value }
            # The action runs in a second runspace of this process (STA, as the clipboard and UI Automation need) with a
            # deadline: a call that never returns - UI Automation of a window whose program doesn't respond blocks with
            # no timeout of its own - is answered as such before the host gives up, and the server then ends, since the
            # stuck call can't be stopped (the host's next request starts a new server). The deadline is
            # max(5, timeout) + 50 s; the host (Invoke-LabPersonServer) waits 30 s longer, for the window list below
            # and the write. A crash on the way is written to the answer file too (RhNative.WatchCrash).
            if (-not $worker) {
                $worker = [runspacefactory]::CreateRunspace()
                $worker.ApartmentState = 'STA'
                $worker.ThreadOptions = 'ReuseThread'
                $worker.Open()
            }
            $ps = [powershell]::Create()
            $ps.Runspace = $worker
            $null = $ps.AddCommand($self)
            foreach ($k in $a.Keys) { $null = $ps.AddParameter($k, $a[$k]) }
            $limit = [Math]::Max(5, $a.TimeoutSec) + 50
            [RhNative]::CrashOut = $req.out
            $h = $ps.BeginInvoke()
            if ($h.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($limit))) {
                [RhNative]::CrashOut = $null
                try {
                    $null = $ps.EndInvoke($h)
                    if (-not (Test-Path -LiteralPath $req.out)) {
                        $why = @($ps.Streams.Error | ForEach-Object { "$_" }) -join '; '
                        [IO.File]::WriteAllText($req.out, (ConvertTo-Json -InputObject @{ serveError = "no answer written. $why" } -Compress))
                    }
                }
                catch {
                    # the script's own error, not EndInvoke's wrapper around it
                    $ex = $_.Exception; if ($ex -is [Management.Automation.MethodInvocationException] -and $ex.InnerException) { $ex = $ex.InnerException }
                    [IO.File]::WriteAllText($req.out, (ConvertTo-Json -InputObject @{ serveError = $ex.Message } -Compress))
                }
                $ps.Dispose()
            }
            else {
                [RhNative]::CrashOut = $null
                # which windows of this session don't respond (Process.Responding: SendMessageTimeout, 5 s at most each),
                # checked for 15 s at most so the answer is written well before the host gives up (deadline + 30 s)
                $mySession = (Get-Process -Id $PID).SessionId
                $wins = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession -and $_.MainWindowHandle -ne [IntPtr]::Zero })
                $checkUntil = (Get-Date).AddSeconds(15)
                $hung = @(foreach ($p in $wins) {
                        if ((Get-Date) -ge $checkUntil) { "$($p.ProcessName) ($($p.Id)): $($p.MainWindowTitle) - not checked"; continue }
                        if (-not $p.Responding) { "$($p.ProcessName) ($($p.Id)): $($p.MainWindowTitle)" }
                    })
                $why = "the action didn't finish within $limit s (a call that doesn't return$(if ($hung) { ' - not responding: ' + ($hung -join ', ') }))"
                $late = if ($a.Do -eq 'uia') {
                    # as a search that found nothing, with the reason: -Optional steps go on, the others fail with it
                    @{ found = $false; title = $a.Title; text = $a.Text; error = $why; notResponding = $hung; windows = @($wins | ForEach-Object { $_.MainWindowTitle } | Where-Object { $_ }) }
                }
                else { @{ serveError = $why } }
                Remove-Item (Join-Path $q 'alive.txt') -ErrorAction SilentlyContinue
                [IO.File]::WriteAllText("$($req.out).part", (ConvertTo-Json -InputObject $late -Depth 4 -Compress))
                Move-Item -LiteralPath "$($req.out).part" -Destination $req.out -Force
                [Diagnostics.Process]::GetCurrentProcess().Kill()
            }
            $idleUntil = (Get-Date).AddMinutes(30)
        }
        Remove-Item (Join-Path $q 'alive.txt') -ErrorAction SilentlyContinue
    }
    'windows' { Out-Json @(Get-AgentWindows) }
    'read' {
        $r = Read-AgentWindow
        if (-not $r) { Out-Json @{ found = $false }; break }
        Out-Json @{ found = $true; title = $r.Window.Title; text = $r.Ocr.Text; lines = $r.Ocr.Lines; window = $r.Window; excluded = [RhNative]::Excluded($r.Window.Handle) }
    }
    'click' {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $done = $null
        # Every agent window is read, dialogs first (not only the first one: another window of the agent's in front of
        # the list would hide the question for the whole wait). What was seen is answered when the label isn't found -
        # lab run 20261019-193627 (D-CLIP 'abort', C-REC): "OCR read:" was empty because only the last pass was kept,
        # after the question had already been refused and closed.
        $seen = [ordered]@{}
        $lastLines = @()
        $passes = 0
        do {
            $passes++
            $wins = @(Get-AgentWindows | Sort-Object { if ($_.Title -eq 'RemoteHub') { 1 } else { 0 } })
            if (-not $wins.Count) { $seen['(no agent window)'] = $true }
            foreach ($w in $wins) {
                Start-Sleep -Milliseconds 400   # fade-in
                $bmp = [RhNative]::Capture($w.X, $w.Y, $w.Width, $w.Height)
                try { $ocr = Get-Ocr $bmp } finally { $bmp.Dispose() }
                $seen["$($w.Title) $($w.Width)x$($w.Height)@$($w.X),$($w.Y)$(if ([RhNative]::Excluded($w.Handle)) { ' (kept out of captures)' }): $(@($ocr.Lines | Select-Object -First 2) -join ' / ')"] = $true
                if (@($ocr.Lines).Count) { $lastLines = @($ocr.Lines) }
                # OCR may glue punctuation or a countdown to the label ("Allow(29)", "Deny."): compare the letters only
                $word = $ocr.Words | Where-Object { $_.Text -eq $Text -or ($_.Text -replace '[^\p{L}\p{Nd}]+.*$', '') -eq $Text -or ($_.Text -replace '^[^\p{L}\p{Nd}]+', '') -eq $Text } |
                    # a button whose whole label is the text first ("Allow", not the "Allow" of "Allow and hide apps..."), then the last one
                    Sort-Object { if (($_.Line -replace '[^\p{L}\p{Nd} ]', '').Trim() -eq $Text) { 0 } else { 1 } }, { -$_.Y } | Select-Object -First 1
                if ($word) {
                    $x = $w.X + $word.X + [int]($word.W / 2); $y = $w.Y + $word.Y + [int]($word.H / 2)
                    # -Value locate: only where (the lab clicks with the console mouse - the agent's windows are SYSTEM's, UIPI drops input sent from here)
                    if ($Value -ne 'locate') { [RhNative]::Click($x, $y) }
                    $done = @{ clicked = $true; text = $Text; x = $x; y = $y; title = $w.Title; lines = $ocr.Lines }
                    break
                }
            }
            if (-not $done) { Start-Sleep -Milliseconds 500 }
        } while (-not $done -and (Get-Date) -lt $until)
        if (-not $done) { $done = @{ clicked = $false; text = $Text; lines = $lastLines; seen = @($seen.Keys | Select-Object -Last 12); passes = $passes; session = (Get-Process -Id $PID).SessionId } }
        Out-Json $done
    }
    'pin' {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $pin = $null
        do {
            $r = Read-AgentWindow
            if ($r) {
                # the code is drawn as large, spaced digits: OCR returns them as single words, not always in reading
                # order - the tallest digit words on one row, left to right. A lone big digit can come back as a
                # look-alike letter ("O" or "o" for 0, "I", "l" or "|" for 1): such one-character words count as digits
                # (the height check below keeps the dialog's text out)
                $digits = @($r.Ocr.Words | ForEach-Object {
                        $t = $_.Text
                        if ($t -cmatch '^[OoQD]$') { $t = '0' } elseif ($t -cmatch '^[Il|]$') { $t = '1' }
                        if ($t -match '^\d+$') { [pscustomobject]@{ Text = $t; X = $_.X; Y = $_.Y; W = $_.W; H = $_.H } }
                    })
                if ($digits) {
                    $big = ($digits | Measure-Object H -Maximum).Maximum
                    $row = @($digits | Where-Object { $_.H -ge $big * 0.7 })
                    $mid = @($row | Sort-Object Y)[[int][math]::Floor($row.Count / 2)].Y
                    $row = @($row | Where-Object { [math]::Abs($_.Y - $mid) -le $big } | Sort-Object X)
                    $text = -join @($row | ForEach-Object Text)
                    if ($text -match '^\d{6}$') { $pin = $text }
                    # OCR can drop a digit of the row altogether (a thin "1": five digits in the run of 17.10.2026
                    # 05:47): the six boxes read again as one strip (Read-PinStrip)
                    elseif ($row.Count -ge 2 -and $row.Count -lt 6 -and -not @($row | Where-Object { $_.Text.Length -ne 1 }).Count) {
                        $strip = Read-PinStrip $r.Window $row
                        if ($strip) { $pin = $strip }
                    }
                }
                if (-not $pin -and ($r.Ocr.Text -replace '[^0-9 ]', '' -replace '\s+', ' ') -match '(\d{3}\s?\d{3})') { $pin = $Matches[1] -replace '\s', '' }
            }
            if (-not $pin) { Start-Sleep -Milliseconds 500 }
        } while (-not $pin -and (Get-Date) -lt $until)
        Out-Json @{ pin = $pin; lines = $(if ($r) { $r.Ocr.Lines } else { @() }); words = $(if ($pin -or -not $r) { @() } else { @($r.Ocr.Words | ForEach-Object { @{ text = $_.Text; x = $_.X; y = $_.Y; h = $_.H } }) }) }
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
        $t = $null
        foreach ($i in 1..20) { try { $t = [Windows.Forms.Clipboard]::GetText(); break } catch { Start-Sleep -Milliseconds 250 } }
        $sha = if ($t) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($t))).Replace('-', '') } else { $null }
        Out-Json @{ length = $t.Length; sha256 = $sha; start = $(if ($t) { $t.Substring(0, [Math]::Min(100, $t.Length)) } else { '' })
            formats = @([Windows.Forms.Clipboard]::GetDataObject().GetFormats()); files = @([Windows.Forms.Clipboard]::GetFileDropList()) }
    }
    'clip-set' {
        Add-Type -AssemblyName System.Windows.Forms
        # another process may hold the clipboard (the agent reading a large copy): SetDataObject retries (20 x 250 ms),
        # and that again until the timeout
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $tries = 0
        while ($true) {
            $tries++
            try { [Windows.Forms.Clipboard]::SetDataObject($Value, $true, 20, 250); break }
            catch { if ((Get-Date) -ge $until) { throw } ; Start-Sleep -Milliseconds 500 }
        }
        Out-Json @{ set = $Value.Length; tries = $tries }
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
        Out-Json @{ text = $ocr.Text; lines = $ocr.Lines; words = @($ocr.Words | ForEach-Object { @{ text = $_.Text; x = $_.X; y = $_.Y; w = $_.W; h = $_.H; line = $_.Line } }) }
    }
    'uia' {
        # UI Automation of a normal window (RemoteHub, an installer dialog, Settings): -Title the window (wildcards),
        # -Text the element's name (wildcards), -Value what to do: click (Invoke / Toggle / Select / Expand, else a
        # real click), toggle-on, toggle-off, set:<text>, read (the element's value or name), exists, row:<button> (that
        # button in the element's own row - see there). Waits up to
        # -TimeoutSec for the element.
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
        $A = [Windows.Automation.AutomationElement]
        $until = (Get-Date).AddSeconds($TimeoutSec)
        $found = $null; $win = $null
        $searchError = $null
        $scrolled = $false
        do {
          # a list that refreshes while the tree is walked (the processes tab every second) can make FindAll throw
          # ("Unrecognized error", an element no longer there): that pass is tried again
          try {
            $tops = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition))
            # RemoteHub's dialogs are owned windows: UIA lists them inside the main window, not at the top - those first
            $isWin = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
            # the main window is "RemoteHub", or "<tab> . <kind> - RemoteHub" while a tab is selected: 'RemoteHub*' means both
            $isMain = { param($n) $n -like 'RemoteHub*' -or $n -like '* RemoteHub' }
            $owned = @($tops | Where-Object { & $isMain $_.Current.Name } | ForEach-Object { @($_.FindAll([Windows.Automation.TreeScope]::Descendants, $isWin)) })
            $wins = @(@($owned | Where-Object { $_.Current.Name -like $Title -and $Title -ne '*' }) +
                @($tops | Where-Object { $_.Current.Name -like $Title -or ($Title -eq 'RemoteHub*' -and (& $isMain $_.Current.Name)) }))
            # a plain name (no wildcard): UI Automation matches it itself - walking every element and filtering here takes
            # tens of seconds in a tab with a long refreshing list (services, processes)
            $exact = $null
            if ($Text -and $Text -notlike 'type:*' -and $Text -notmatch '[\*\?\[\]]') {
                $ic = [Windows.Automation.PropertyConditionFlags]::IgnoreCase
                $exact = New-Object Windows.Automation.OrCondition @(
                    (New-Object Windows.Automation.PropertyCondition ($A::NameProperty), $Text, $ic),
                    (New-Object Windows.Automation.PropertyCondition ($A::AutomationIdProperty), $Text, $ic),
                    (New-Object Windows.Automation.PropertyCondition ($A::HelpTextProperty), $Text, $ic))
            }
            foreach ($w in $wins) {
                $hit = @($w.FindAll([Windows.Automation.TreeScope]::Descendants, $(if ($exact) { $exact } else { [Windows.Automation.Condition]::TrueCondition })) |
                        Where-Object {
                            # "type:Edit": an element of that control type with neither a name nor an id (the PowerShell
                            # tab's input line and output); otherwise the name, the id or the tooltip (HelpText - RemoteHub's
                            # icon buttons have no name)
                            if ($Text -like 'type:*') { $_.Current.ControlType.ProgrammaticName -eq "ControlType.$(($Text.Substring(5) -split '#')[0])" -and -not $_.Current.Name -and -not $_.Current.AutomationId }
                            else { $_.Current.Name -like $Text -or $_.Current.AutomationId -like $Text -or ($_.Current.HelpText -and $_.Current.HelpText -like $Text) }
                        })
                $any = $hit
                $visible = @($hit | Where-Object { -not $_.Current.IsOffscreen })
                # a plain click is an Invoke, which works off the screen too: a button below the screen still counts
                $hit = if ($visible -or ($Value -and $Value -ne 'click')) { $visible } else { @($hit | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' }) }
                # only scrolled out of a list or tree (RemoteHub's tree grows past its window, a new item isn't scrolled
                # to): its item (the element or the nearest parent with ScrollItemPattern) scrolled into view once, then
                # looked for again
                if (-not $hit -and @($any).Count -and -not $scrolled) {
                    $scrolled = $true
                    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
                    $e = @($any)[0]; $moved = $false
                    while ($e -and -not [Windows.Automation.Automation]::Compare($e, $w)) {
                        $sp = $null
                        if ($e.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$sp)) { try { $sp.ScrollIntoView(); $moved = $true } catch { }; break }
                        $e = $walker.GetParent($e)
                    }
                    if ($moved) { Start-Sleep -Milliseconds 400; break }
                }
                # "type:Edit#2": the second one in tree order (the file transfer tab's remote address box)
                if ($Text -match '^type:.*#(\d+)$') { $hit = @($hit | Select-Object -Skip ([int]$Matches[1] - 1) -First 1) }
                elseif ($hit.Count -gt 1) {
                    # a name in the connection list and in a tab header: the list's item first
                    $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
                    $hit = @($hit | Sort-Object { $p = $walker.GetParent($_); if ($_.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem' -or ($p -and $p.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem')) { 0 } else { 1 } })
                }
                if ($hit) { $found = $hit[0]; $win = $w; break }
            }
          }
          catch { $searchError = "$($_.Exception.Message)"; $found = $null; $win = $null }
            if (-not $found) { Start-Sleep -Milliseconds 500 }
        } while (-not $found -and (Get-Date) -lt $until)
        $action = if ($Value) { $Value } else { 'click' }
        if (-not $found) { Out-Json @{ found = $false; title = $Title; text = $Text; searchError = $searchError; windows = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }; break }
        $pat = { param($p) $o = $null; if ($found.TryGetCurrentPattern($p, [ref]$o)) { $o } else { $null } }
        $result = @{ found = $true; name = $found.Current.Name; type = $found.Current.ControlType.ProgrammaticName; window = $win.Current.Name; enabled = $found.Current.IsEnabled }
        # The element's centre for a real click. Something of the window's own drawn over that point (RemoteHub's view rail
        # expands into a popup while the pointer rests on it and stays open while the pointer is over the popup; a tooltip)
        # would take the click instead - lab run 20261019-193627: a right-click on a connection opened no menu under the
        # expanded rail. Then the pointer is moved to the window's right edge first, until the point is the element's
        # window again (hover popups close when the pointer leaves them), at most 3 s.
        $center = {
            $r = $found.Current.BoundingRectangle
            $pt = @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            # the window the element is drawn in: its nearest ancestor with a window handle (a popup's own, else $win)
            $own = [IntPtr]::Zero
            $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
            $e = $found
            while ($e -and -not [Windows.Automation.Automation]::Compare($e, $win)) {
                if ($e.Current.NativeWindowHandle) { $own = [RhNative]::Root([IntPtr]$e.Current.NativeWindowHandle); break }
                $e = $walker.GetParent($e)
            }
            if ($own -eq [IntPtr]::Zero -and $win.Current.NativeWindowHandle) { $own = [RhNative]::Root([IntPtr]$win.Current.NativeWindowHandle) }
            if ($own -ne [IntPtr]::Zero -and [RhNative]::RootAt($pt[0], $pt[1]) -ne $own) {
                $wr = $win.Current.BoundingRectangle
                [void][RhNative]::SetCursorPos([int]($wr.Right - 6), [int]($wr.Top + $wr.Height / 2))
                $coverUntil = (Get-Date).AddSeconds(3)
                do { Start-Sleep -Milliseconds 100 } while ([RhNative]::RootAt($pt[0], $pt[1]) -ne $own -and (Get-Date) -lt $coverUntil)
            }
            $pt
        }
        # SelectionItemPattern.Select (a RadioButton, a ListItem) can fail with a COM error ("Unrecognized error") while
        # the element is there: a real click on it instead. A switched-off element isn't clicked - the step says so
        # (answers $false then).
        $selectOrClick = {
            param($el, $sp)
            if (-not $el.Current.IsEnabled) { $result.error = "'$($el.Current.Name)' is disabled"; $result.enabled = $false; return $false }
            try { $sp.Select() }
            catch {
                $result.selectError = "$($_.Exception.Message)"
                $r = $el.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
                Start-Sleep -Milliseconds 300
            }
            $true
        }
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
            'row:*' {
                # row:<button>: the button of that name (wildcards) in the found element's own row - a list whose rows
                # each have one (Settings > Apps: every app's "More options"). The element's ancestors are searched from
                # the nearest up; one that holds more than one such button is the list, not the row, and is not used.
                # Every visible match of -Text is tried (a search box's suggestion has the same name, but no row).
                $want = $action.Substring(4)
                $isButton = New-Object Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
                $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
                $btn = $null; $row = $null
                foreach ($cand in @($hit)) {
                    $e = $cand
                    while ($e -and -not [Windows.Automation.Automation]::Compare($e, $win)) {
                        $btns = @($e.FindAll([Windows.Automation.TreeScope]::Descendants, $isButton) | Where-Object { $_.Current.Name -like $want -or ($_.Current.HelpText -and $_.Current.HelpText -like $want) })
                        if ($btns.Count -eq 1) { $btn = $btns[0]; $row = $e; break }
                        if ($btns.Count -gt 1) { break }
                        $e = $walker.GetParent($e)
                    }
                    if ($btn) { break }
                }
                if (-not $btn) { $result.error = "no button '$want' in the row of '$Text'"; break }
                $result.row = $row.Current.Name
                $result.value = $btn.Current.Name
                $ip = $null
                if ($btn.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$ip)) { $ip.Invoke() }
                else { $r = $btn.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
            }
            'doubleclick' { $c = & $center; [RhNative]::Click($c[0], $c[1]); Start-Sleep -Milliseconds 80; [RhNative]::Click($c[0], $c[1]) }
            'realclick' { $c = & $center; [RhNative]::Click($c[0], $c[1]) }
            'keys:*' { $found.SetFocus(); Start-Sleep -Milliseconds 200; [Windows.Forms.SendKeys]::SendWait($action.Substring(5)) }
            'readnext' {
                # the current choice of the ComboBox after the label (-Text): its value, else its selected item's name
                $all = @($win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
                $at = [array]::IndexOf($all, ($all | Where-Object { [Windows.Automation.Automation]::Compare($_, $found) } | Select-Object -First 1))
                $field = $all | Select-Object -Skip ($at + 1) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' } | Select-Object -First 1
                if (-not $field) { $result.error = "no ControlType.ComboBox after '$Text'"; break }
                $o = $null
                $result.value = if ($field.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$o) -and $o.Current.Value) { $o.Current.Value }
                elseif ($field.TryGetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern, [ref]$o) -and @($o.Current.GetSelection()).Count) { @($o.Current.GetSelection())[0].Current.Name }
                else { $field.Current.Name }
                $result.enabled = $field.Current.IsEnabled
            }
            { $_ -like 'setnext:*' -or $_ -like 'choose:*' } {
                # RemoteHub's form fields have no names: the label (-Text) is a separate element before them - the next
                # Edit (setnext) or ComboBox (choose) after it in tree order is the field
                $all = @($win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition))
                $at = [array]::IndexOf($all, ($all | Where-Object { [Windows.Automation.Automation]::Compare($_, $found) } | Select-Object -First 1))
                $want = if ($action -like 'setnext:*') { 'ControlType.Edit' } else { 'ControlType.ComboBox' }
                $field = $all | Select-Object -Skip ($at + 1) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq $want -and $_.Current.IsEnabled } | Select-Object -First 1
                if (-not $field) {
                    # what was found and what follows it (lab run 20261019-193627, UI-RDP: "no ControlType.Edit after
                    # 'Password'" although the list showed the password box right after the label)
                    $next = @($all | Select-Object -Skip ($at + 1) -First 6 | ForEach-Object { "$($_.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '')|$($_.Current.Name)|$($_.Current.AutomationId)|$(if ($_.Current.IsEnabled) { 'on' } else { 'off' })" })
                    $result.error = "no $want after '$Text' (found: $($found.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '')|$($found.Current.Name)|$($found.Current.AutomationId) at $at of $($all.Count); next: $($next -join '; '))"
                    break
                }
                if ($action -like 'setnext:*') {
                    $text = $action.Substring(8)
                    $vp = $null
                    if ($field.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($text) }
                    else { $field.SetFocus(); [Windows.Forms.SendKeys]::SendWait('^a'); [Windows.Forms.SendKeys]::SendWait(($text -replace '[+^%~(){}\[\]]', '{$0}')) }
                    # a password isn't answered back (the answer can end up in the evidence)
                    $result.value = if ($field.Current.IsPassword) { '<hidden>' } else { $text }
                }
                else {
                    $item = $action.Substring(7)
                    $ec = $null; if ($field.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) { $ec.Expand(); Start-Sleep -Milliseconds 400 }
                    $li = @($field.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $item }) | Select-Object -First 1
                    if (-not $li) { $li = @($A::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like $item }) | Select-Object -First 1 }
                    if (-not $li) { $result.error = "no item '$item'"; if ($ec) { $ec.Collapse() }; break }
                    $sp = $null
                    if ($li.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $null = & $selectOrClick $li $sp } else { $r = $li.Current.BoundingRectangle; [RhNative]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
                    if ($ec) { try { $ec.Collapse() } catch { } }
                    $result.value = $li.Current.Name
                }
            }
            { $_ -in 'check', 'uncheck' } {
                $t = & $pat ([Windows.Automation.TogglePattern]::Pattern)
                $sel = & $pat ([Windows.Automation.SelectionItemPattern]::Pattern)
                if (-not $t -and $sel) { if ($action -eq 'check' -and -not (& $selectOrClick $found $sel)) { break }; $result.value = [string]$sel.Current.IsSelected; break }
                if (-not $t) {
                    # the label text inside a CheckBox / RadioButton: its parent has the pattern
                    $p = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($found)
                    $o = $null; if ($p.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$o)) { $t = $o }
                    elseif ($p.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$o)) { if (& $selectOrClick $p $o) { $result.value = 'selected' }; break }
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
                # the label text inside a CheckBox (WPF lists both with the same name, either may be found first): the
                # CheckBox's state, as check / uncheck use its parent's pattern
                if (-not $v -and -not $tp -and -not $t) {
                    $p = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($found)
                    $o = $null
                    if ($p -and $p.Current.ControlType.ProgrammaticName -eq 'ControlType.CheckBox' -and $p.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$o)) { $t = $o; $result.enabled = $p.Current.IsEnabled }
                }
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
                if ($i) { try { $i.Invoke() } catch { $result.invokeError = $_.Exception.InnerException.Message } } elseif ($t) { $t.Toggle() } elseif ($s) { $null = & $selectOrClick $found $s } elseif ($e) { $e.Expand() }
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
        # 'active' when the marker has the keyboard, 'inactive' (with who took it) when it loses it - a count of keys
        # that arrived only means something while it is active (Start-LabMarker waits for 'active')
        $f.Add_Activated({ W @{ e = 'active' } })
        $f.Add_Deactivate({ W @{ e = 'inactive'; to = [RhNative]::ForegroundName() } })
        # take the foreground until it has it (Activate alone may be refused), for 30 s at most
        $ft = New-Object Windows.Forms.Timer; $ft.Interval = 250
        $fsw = [Diagnostics.Stopwatch]::StartNew()
        $ft.Add_Tick({
                if ([RhNative]::TakeForeground($f.Handle) -or $fsw.Elapsed.TotalSeconds -gt 30) {
                    if ([RhNative]::GetForegroundWindow() -ne $f.Handle) { W @{ e = 'nofocus'; fg = [RhNative]::ForegroundName() } }
                    $ft.Stop()
                }
            })
        $f.Add_Shown({ $ft.Start() })
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
