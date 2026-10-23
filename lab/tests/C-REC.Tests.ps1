# C-REC - recording RemoteHub's tabs to MP4 files on the admin's PC (this client VM, the console account's session,
# driven by UI Automation) against the device (the Server VM): every tab type (remote desktop, agent desktop, PowerShell,
# file transfer, registry, services, processes) into Videos\RemoteHub, the settings (30 fps, Full HD, graphics card
# capture and its border), RemoteHub killed while recording, the last frame against the stop click, and the N edition's
# message. The files are read in the client's session with Windows' Media Foundation (Windows.Media.Editing:
# duration, size, frame rate, a frame). The agent is paired with RemoteHub through its clipboard install (files taken
# to the device over PowerShell Direct, msiexec there) - no WinRM needed. Client VMs only (2VM; C-REC-06 only on an N
# edition VM). See docs/test-lab-cases.md section 10.6.
# LabRole: console
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $labVm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    # an N edition (no Media Feature Pack): "W11-26H2-N-en-US"
    $isN = $labVm.Id -match '(^|-)N(-|$)'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $password = (Get-LabConfig).Password
    $device = Get-LabDevice -For $VmName
    $script:task = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-rec-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-rec-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of the connection's context menu ("A>B" = submenu: "Tools", "RemoteHub Agent", "More"). The right-click
    # right after a dialog closed ("Recording saved") sometimes opens no menu at all (no menu entries anywhere): once more
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) {
        $bad = { param($m) -not $m.found -or ($m.PSObject.Properties['error'] -and $m.error) }
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec -Optional
        if ((& $bad $m) -and $m.found -and $m.PSObject.Properties['entries'] -and @($m.entries).Count -eq 0) {
            Start-Sleep -Seconds 2
            $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec -Optional
        }
        if (& $bad $m) {
            $why = if (-not $m.found) { 'not found' } else { "$($m.error)" + $(if ($m.PSObject.Properties['entries']) { ' - entries: ' + (@($m.entries) -join ', ') }) }
            throw "UI step failed: '$Node' (menu:$Entry): $why"
        }
    }
    # the text of a message box (its window name is the message text), closed with -Button
    function Message([string]$Title = '?*', [string]$Button = 'OK', [int]$TimeoutSec = 120) {
        $m = Ui '*' -Title $Title -Value list -TimeoutSec $TimeoutSec
        $null = Ui $Button -Title $Title -Optional
        ($m.items -join "`n")
    }
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
            # a recording the failed step started is stopped and saved: else the next steps find "Stop recording" where
            # they want "Record this tab", and the file stays open (Services ran on into C-REC-02..05)
            try {
                $s = Ui 'Stop recording and save the video' -Optional -TimeoutSec 20
                if ($s.found) { $R["$Name-stoppedRecording"] = $true; $null = Message -Button 'Close' -TimeoutSec 60 }
            }
            catch { }
        }
    }
    # the record / stop button of the selected tab. With a long list in the tab (services, processes - refreshed every
    # few seconds) a UIA pass over the window takes long or fails: up to 60 s, and the search error in the message
    function RecButton([string]$Text) {
        $b = Ui $Text -Optional -TimeoutSec 60
        if (-not $b.found) { throw "UI step failed: '$Text' not found in 60 s$(if ($b.PSObject.Properties['searchError'] -and $b.searchError) { " (last search error: $($b.searchError))" }); windows: $(@($b.windows) -join ', ')" }
    }
    # the connection's tabs closed (a long refreshing list in a tab makes every UIA search slow)
    function CloseAll { foreach ($i in 1..6) { $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 5; if (-not $c.found) { break }; Start-Sleep -Seconds 2 } }
    function InSession([string]$Vm, [string]$Account, [string]$Code, [hashtable]$Values = @{}, [switch]$Elevated, [int]$TimeoutSec = 120) {
        foreach ($k in $Values.Keys) { $Code = $Code.Replace("__$($k)__", [string]$Values[$k]) }
        $raw = "$(Invoke-LabInSession -Name $Vm -Account $Account -Elevated:$Elevated -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($Code)))"
        if ($raw -match '(?m)^JSON:(.*)$') { return ($Matches[1].Trim() | ConvertFrom-Json) }
        if ($raw.Trim() -like 'ERROR:*') { throw "$Vm / ${Account}: $($raw.Trim())" }
        $raw.Trim()
    }
    # the RDP tab is connected: its header says "Click into the session to type" / "Keyboard -> remote session"
    function Wait-RdpTab([int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $l = Ui '*' -Value list -Optional -TimeoutSec 5
            if (@(@($l.items) | Where-Object { $_ -match '\|(Click into the session to type|Keyboard\W+remote session)\|' }).Count -gt 0) { return $true }
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $until)
        $false
    }
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
    # (connections, paired devices, settings, recordings) and the NTLM delegation policy. Throws when something stays.
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
    # a tool tab; when the connection's agent wasn't tested yet the menu offers "Test RemoteHub Agent" instead
    # - only then; any other miss (a slow UIA pass, no menu) is tried once more as it is
    function Open-Tool([string]$Entry) {
        $m = Ui 'Device' -Value "menu:Tools>$Entry" -Optional
        if (-not $m.found -or ($m.PSObject.Properties['error'] -and $m.error)) {
            Key 1B
            Start-Sleep -Seconds 1
            if (@(@($m.entries) | Where-Object { $_ -like 'Test RemoteHub Agent*' }).Count) {
                Menu 'Tools>Test RemoteHub Agent*'
                $null = Message -TimeoutSec 90
            }
            Menu "Tools>$Entry"
        }
    }
    # the console account's recordings, oldest first
    function Videos {
        @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($u) @(Get-ChildItem "C:\Users\$u\Videos\RemoteHub" -Filter *.mp4 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.FullName; Size = $_.Length } })
            })
    }
    # duration, size and frame rate of MP4 files (Media Foundation through Windows.Media.Editing, as the unit tests
    # decode them: seconds, width, height, fps, error); -Thumb: the last frame saved there. The editing API wants a
    # closed file, so each file is also opened the way players do (MediaSource: opened, openError, sourceSeconds) and
    # its boxes are read: the movie header's and mehd's durations (mvhdSeconds, mehdSeconds), the fragments (moofs) and
    # the length of the frames in them (fragSeconds, fragFrames), truncated when the last box is cut off (a killed recording)
    function VideoInfo([string[]]$Paths, [string]$Thumb = '') {
        $files = (@($Paths) | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','
        @(InSession $VmName Console $code.video @{ FILES = $files; THUMB = $Thumb } -TimeoutSec 240)
    }
    # Record this tab to a video file, the first-time notice accepted, -Seconds later stopped, "Recording saved" closed;
    # -During runs while it records
    function Record([System.Collections.IDictionary]$R, [string]$Key, [int]$Seconds = 10, [scriptblock]$During) {
        RecButton 'Record this tab to a video file'
        $R["$Key-notice"] = (Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 8).found
        $t0 = Get-Date
        if ($During) { . $During }
        $left = $Seconds - ((Get-Date) - $t0).TotalSeconds
        if ($left -gt 0) { Start-Sleep -Seconds ([int][math]::Ceiling($left)) }
        RecButton 'Stop recording and save the video'
        $R["$Key-saved"] = Message -Button 'Close' -TimeoutSec 60
    }
    # The agent paired with RemoteHub without WinRM: Install RemoteHub Agent... "Copy the installer and the enrollment
    # file to the clipboard", the two staged files taken from this PC's clipboard to the device over PowerShell Direct,
    # msiexec there; RemoteHub's WaitForManualInstall marks the connection installed (audit "Installed - answered as ...").
    function Install-AgentByHand([string]$Node, [string]$TargetVm) {
        $o = [ordered]@{}
        Menu 'RemoteHub Agent>Install RemoteHub Agent*' -Node $Node
        $null = Ui 'Copy the installer and the enrollment file to the clipboard*' -Title 'Install RemoteHub Agent' -Value check -TimeoutSec 60
        $null = Ui 'Install' -Title 'Install RemoteHub Agent'
        $null = Ui 'Done' -Title 'Install RemoteHub Agent' -Value exists -TimeoutSec 60
        $null = Ui 'Done' -Title 'Install RemoteHub Agent' -Optional
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
        $until = (Get-Date).AddSeconds(150)
        # since enrollment without a shared device key RemoteHub records "Claimed - installed by hand, certificate
        # SHA-256 ..." (the old "answered as RemoteHub Agent" is gone; run 20261022-061254, A-CLIP-01)
        do { Start-Sleep -Seconds 15; $o.audit = AuditText $Node } while ($o.audit -notmatch 'Claimed\W+installed by hand' -and (Get-Date) -lt $until)
        $o
    }
    # share of yellow pixels (the graphics capture border) in a screenshot - every pixel, the border is thin
    if (-not ('CRecPixels' -as [type])) {
        Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System.Drawing;
using System.Drawing.Imaging;
public static class CRecPixels {
    public static double Yellow(string path) {
        using (var src = new Bitmap(path))
        using (var bmp = src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format24bppRgb)) {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var bytes = new byte[d.Stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(d);
            long hit = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++) {
                    int i = y * d.Stride + 3 * x;
                    if (bytes[i + 2] >= 200 && bytes[i + 1] >= 160 && bytes[i] <= 90) hit++;
                }
            return (double)hit / ((double)bmp.Width * bmp.Height);
        }
    }
}
'@
    }
    function YellowShare([string]$Path) { [math]::Round([CRecPixels]::Yellow($Path), 5) }
    function DiagLog([string]$Pattern) {
        @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount, $Pattern -ScriptBlock {
                param($u, $p) $f = "C:\Users\$u\AppData\Roaming\RemoteHub\logs\remotehub.log"; if (Test-Path $f) { @(Get-Content $f | Where-Object { $_ -match $p }) }
            })
    }

    $code = @{}
    $code.clipFiles = @'
Add-Type -AssemblyName System.Windows.Forms
'JSON:' + (ConvertTo-Json -Compress -InputObject @([System.Windows.Forms.Clipboard]::GetFileDropList() | ForEach-Object { [string]$_ }))
'@
    $code.video = @'
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaClip, Windows.Media.Editing, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaComposition, Windows.Media.Editing, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.ImageStream, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$null = [Windows.Media.Core.MediaSource, Windows.Media.Core, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asTaskAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
# the innermost message ("One or more errors occurred." says nothing)
function Why($e) { $x = $e.Exception; while ($x.InnerException) { $x = $x.InnerException }; "$($e.Exception.Message) / $($x.GetType().Name): $($x.Message)" }
function U32([byte[]]$b, [long]$i) { [long]$b[$i] * 16777216 + [long]$b[$i + 1] * 65536 + [long]$b[$i + 2] * 256 + [long]$b[$i + 3] }
function U64([byte[]]$b, [long]$i) { (U32 $b $i) * 4294967296 + (U32 $b ($i + 4)) }
# the boxes between $from and $to: type, payload start, end; cut = the last one goes past $to
function Boxes([byte[]]$b, [long]$from, [long]$to) {
    $p = $from
    while ($p + 8 -le $to) {
        $size = U32 $b $p; $hdr = 8
        $type = [Text.Encoding]::ASCII.GetString($b, $p + 4, 4)
        if ($size -eq 1) { $size = U64 $b ($p + 8); $hdr = 16 } elseif ($size -eq 0) { $size = $to - $p }
        if ($size -lt $hdr) { break }
        [pscustomobject]@{ type = $type; start = $p + $hdr; end = [math]::Min($p + $size, $to); cut = ($p + $size -gt $to) }
        $p += $size
    }
}
function Child($b, $box, [string[]]$path) { foreach ($t in $path) { if (-not $box) { return }; $box = @(Boxes $b $box.start $box.end | Where-Object type -eq $t)[0] }; $box }
function Mp4Info([string]$p, $o) {
    $b = [IO.File]::ReadAllBytes($p)
    $top = @(Boxes $b 0 $b.Length)
    $o.boxes = (@($top | Select-Object -First 6 | ForEach-Object type) -join ',') + $(if ($top.Count -gt 6) { ",... ($($top.Count))" })
    $o.truncated = [bool]($top.Count -and $top[-1].cut)
    $moov = @($top | Where-Object type -eq 'moov')[0]
    $scale = 0; $defDur = 0
    if ($moov) {
        $mvhd = Child $b $moov 'mvhd'
        if ($mvhd) {
            $v = $b[$mvhd.start]
            $ms = if ($v -eq 1) { U32 $b ($mvhd.start + 20) } else { U32 $b ($mvhd.start + 12) }
            $md = if ($v -eq 1) { U64 $b ($mvhd.start + 24) } else { U32 $b ($mvhd.start + 16) }
            if ($ms) { $o.mvhdSeconds = [math]::Round($md / $ms, 2) }
        }
        $mdhd = Child $b $moov 'trak', 'mdia', 'mdhd'
        if ($mdhd) { $scale = if ($b[$mdhd.start] -eq 1) { U32 $b ($mdhd.start + 20) } else { U32 $b ($mdhd.start + 12) } }
        $mehd = Child $b $moov 'mvex', 'mehd'
        if ($mehd -and $scale) { $o.mehdSeconds = [math]::Round($(if ($b[$mehd.start] -eq 1) { U64 $b ($mehd.start + 4) } else { U32 $b ($mehd.start + 4) }) / $scale, 2) }
        $trex = Child $b $moov 'mvex', 'trex'
        if ($trex) { $defDur = U32 $b ($trex.start + 12) }
    }
    $moofs = @($top | Where-Object { $_.type -eq 'moof' -and -not $_.cut })
    $o.moofs = $moofs.Count
    $ticks = [long]0; $frames = [long]0
    foreach ($moof in $moofs) {
        foreach ($traf in @(Boxes $b $moof.start $moof.end | Where-Object type -eq 'traf')) {
            $dur = $defDur
            foreach ($x in @(Boxes $b $traf.start $traf.end)) {
                $flags = [long]$b[$x.start + 1] * 65536 + [long]$b[$x.start + 2] * 256 + [long]$b[$x.start + 3]
                if ($x.type -eq 'tfhd') {
                    $q = $x.start + 8
                    if ($flags -band 0x01) { $q += 8 }
                    if ($flags -band 0x02) { $q += 4 }
                    if ($flags -band 0x08) { $dur = U32 $b $q }
                }
                elseif ($x.type -eq 'trun') {
                    $n = U32 $b ($x.start + 4); $q = $x.start + 8
                    if ($flags -band 0x01) { $q += 4 }
                    if ($flags -band 0x04) { $q += 4 }
                    $per = 4 * (@(0x100, 0x200, 0x400, 0x800) | Where-Object { $flags -band $_ }).Count
                    for ($k = 0; $k -lt $n; $k++) {
                        $ticks += $(if ($flags -band 0x100) { U32 $b $q } else { $dur })
                        $q += $per
                    }
                    $frames += $n
                }
            }
        }
    }
    $o.fragFrames = $frames
    if ($scale -and $frames) { $o.fragSeconds = [math]::Round($ticks / $scale, 2) }
}
$thumb = '__THUMB__'
$out = @(foreach ($p in @(__FILES__)) {
        $o = [ordered]@{ name = (Split-Path $p -Leaf); size = (Get-Item $p).Length }
        try { Mp4Info $p $o } catch { $o.boxError = Why $_ }
        try {
            $f = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($p)) ([Windows.Storage.StorageFile])
            $src = [Windows.Media.Core.MediaSource]::CreateFromStorageFile($f)
            try {
                $t = $asTaskAction.Invoke($null, @($src.OpenAsync()))
                if (-not $t.Wait(30000)) { throw 'MediaSource.OpenAsync: no answer in 30 s' }
                $o.opened = $src.IsOpen
                if ($null -ne $src.Duration) { $o.sourceSeconds = [math]::Round(([TimeSpan]$src.Duration).TotalSeconds, 2) }
            }
            finally { $src.Dispose() }
        }
        catch { $o.opened = $false; $o.openError = Why $_ }
        try {
            $f = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($p)) ([Windows.Storage.StorageFile])
            $clip = Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($f)) ([Windows.Media.Editing.MediaClip])
            $o.seconds = $clip.OriginalDuration.TotalSeconds
            $v = $clip.GetVideoEncodingProperties()
            $o.width = $v.Width; $o.height = $v.Height
            if ($v.FrameRate.Denominator) { $o.fps = [math]::Round($v.FrameRate.Numerator / $v.FrameRate.Denominator, 2) }
            if ($thumb) {
                $comp = New-Object Windows.Media.Editing.MediaComposition
                # (Clips is a WinRT IVector: Windows PowerShell gives it no Add - through the .NET interface)
                $null = [System.Collections.Generic.ICollection[Windows.Media.Editing.MediaClip]].GetMethod('Add').Invoke($comp.Clips, @(, $clip))
                # the last frame the editing API can reach; near the end of its (too short, see C-REC-05) duration it
                # has refused one ("timeFromStart is outside of the composition bounds", lab run 20261019-193627):
                # then a little earlier. thumbAt says which one it is.
                $img = $null; $why = $null
                foreach ($back in 0.3, 1, 2, 4) {
                    $at = [TimeSpan]::FromSeconds([math]::Max(0, $o.seconds - $back))
                    try { $img = Await ($comp.GetThumbnailAsync($at, 0, 0, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream]); $o.thumbAt = $at.TotalSeconds; break }
                    catch { $why = $_ }
                    if ($at.TotalSeconds -eq 0) { break }
                }
                if (-not $img) { throw $why }
                $s = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($img)
                $fs = [IO.File]::Create($thumb)
                try { $s.CopyTo($fs) } finally { $fs.Close() }
                $o.thumb = $thumb
            }
        }
        catch { $o.error = Why $_ }
        [pscustomobject]$o
    })
'JSON:' + (ConvertTo-Json -Compress -InputObject $out)
'@

    $kinds = 'Remote desktop', 'Desktop (agent)', 'PowerShell', 'File transfer', 'Registry', 'Services', 'Processes'

    if ($isClient) {
        Reset-LabVm -Name $device.Name
        $null = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            # Remote Desktop on (the firewall group by its resource name - display names are localized)
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # RemoteHub installed (kept from an earlier file when it left the VM so; AfterAll empties RemoteHub's data)
        $null = Initialize-LabVm -Name $VmName -Client
        # the lab isn't a domain: saved credentials go to RDP only with the NTLM delegation policy (R-RDP-01)
        Allow-NtlmDelegation
        $script:task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
        # the device's RDP certificate is self-signed: without "Connect without warning" the tab stops at Windows' certificate
        # question (run 20261016-024318: "Disconnected (code 263)" once Esc dismissed it), so nothing is recorded connected
        Menu 'Edit*'
        $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
        $null = Ui 'Security & session' -Title 'Edit connection'
        $null = Ui 'Server authentication' -Title 'Edit connection' -Value 'choose:Connect without warning'
        $null = Ui 'Save' -Title 'Edit connection'
        Start-Sleep -Seconds 2
        $script:pair = [ordered]@{}
        Step $script:pair pair {
            $script:pair.install = Install-AgentByHand 'Device' $device.Name
            # the agent's test, so the menu offers the tools
            Menu 'Tools>Test RemoteHub Agent*'
            $script:pair.test = Message -TimeoutSec 90
        }
        Save 'pair' $script:pair
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        # undone for the next file on this VM: RemoteHub's data (connections, paired devices, settings, recordings) and
        # the NTLM delegation policy; anything that fails: the next file starts from the checkpoint
        try { Clear-ClientLeftovers } catch { Set-LabVmDirty -Name $VmName }
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'c-rec-device-logs') } catch { }
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'C-REC <VmName>' -Tag Client, UI, 'C-REC' -Skip:(-not $isClient) {

    Context 'every tab type recorded' -Skip:$isN {
        BeforeAll {
            $r1 = [ordered]@{}
            Step $r1 rdp {
                Menu 'Remote desktop via RDP'
                $r1.rdpConnected = Wait-RdpTab 90
                if (-not $r1.rdpConnected) {
                    # the tab is recorded anyway (C-REC-01 is about the file), but say why RDP didn't connect
                    Shot 'rdp-not-connected'
                    $r1.rdpLog = @(DiagLog 'Session [0-9a-f]+: disconnected' | Select-Object -Last 5 | ForEach-Object { "$_" })
                }
                Record $r1 'Remote desktop'
            }
            Step $r1 agent {
                # The person's first call on the device (reverted by Reset-LabVm, so not warmed up as Initialize-LabVm
                # does) starts its server, compiles its helper and loads the OCR - done here, not against the consent
                # dialog's 30 s countdown. Lab run 20261019-193627: that first call was the Allow itself; the server
                # wasn't up within 60 s, the question was refused after 30 s and the fallback task gave no result.
                $r1.warm = @(Invoke-LabPerson -Name $device.Name -Account Console -Do windows -TimeoutSec 120).Count
                $null = Invoke-LabPerson -Name $device.Name -Account Console -Do screen-read -TimeoutSec 120
                Menu 'Connect'
                $null = Ui 'Connect' -Optional -TimeoutSec 15
                $r1.allow = Invoke-LabPerson -Name $device.Name -Account Console -Do click -Text 'Allow' -TimeoutSec 60
                $null = Ui 'Send Ctrl+Alt+Del' -Value exists -TimeoutSec 60
                Record $r1 'Desktop (agent)'
            }
            Step $r1 ps {
                Open-Tool 'PowerShell'
                $null = Ui 'Stop the running command (Ctrl+C)' -Value exists -Optional -TimeoutSec 60
                Record $r1 'PowerShell'
            }
            Step $r1 files {
                Open-Tool 'File transfer'
                $null = Ui 'Upload selected files' -Value exists -Optional -TimeoutSec 60
                Record $r1 'File transfer'
            }
            Step $r1 registry {
                Open-Tool 'Registry'
                $null = Ui 'New key or value' -Value exists -Optional -TimeoutSec 60
                Record $r1 'Registry'
            }
            Step $r1 services {
                Open-Tool 'Services'
                $null = Ui 'Stop the service' -Value exists -Optional -TimeoutSec 60
                Record $r1 'Services'
            }
            Step $r1 processes {
                Open-Tool 'Processes'
                $null = Ui 'End process (Del)' -Value exists -Optional -TimeoutSec 60
                Record $r1 'Processes'
            }
            Step $r1 files-check {
                $r1.videos = Videos
                $r1.info = VideoInfo @($r1.videos | ForEach-Object Path)
                $r1.audit = AuditText
            }
            Shot 'tabs'
            Save 'tabs' $r1
            # the services and processes lists refresh all the time and slowed every later UIA search past its timeout
            # (the record button, the menu): the next contexts work in a PowerShell tab of their own
            CloseAll
        }

        It 'C-REC-01 one MP4 per tab type in Videos\RemoteHub, named "Device - [tab] - [date time].mp4", each with a duration; audit started / saved' -Tag 'C-REC-01' {
            foreach ($k in $kinds) {
                $f = @($r1.info | Where-Object { $_.name -match ('^Device - ' + [regex]::Escape($k) + ' - \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?\.mp4$') })
                $f.Count | Should -BeGreaterThan 0 -Because "a recording of the $k tab"
                "$($f[0].error)" | Should -BeNullOrEmpty -Because "$k plays"
                $f[0].seconds | Should -BeGreaterThan 0 -Because "$k has a duration"
            }
            $r1.audit | Should -Match 'Recording started'
            $r1.audit | Should -Match 'Recording saved'
        }
    }

    Context 'settings: 30 fps, Full HD, graphics card capture' -Skip:$isN {
        BeforeAll {
            $r2 = [ordered]@{}
            Step $r2 settings {
                $null = Ui 'Settings'
                $null = Ui 'Recordings' -Title 'Settings'
                $r2.fps = (Ui 'Frame rate' -Title 'Settings' -Value 'choose:30 fps*').value
                $r2.size = (Ui 'Video size' -Title 'Settings' -Value 'choose:Full HD*').value
                # check, not toggle-on: the name is found on the switch's label text too, which has no Toggle pattern
                # (toggle-on then does nothing and answers no value); check falls back to the label's parent
                $g = Ui 'Capture with the graphics card*' -Title 'Settings' -Value check
                $r2.gpu = $g.value
                $r2.gpuElement = "$($g.type) '$($g.name)'"
                $null = Ui 'Save' -Title 'Settings'
            }
            Step $r2 record {
                # a light tab to record (C-REC-04 records it too)
                Open-Tool 'PowerShell'
                $null = Ui 'Stop the running command (Ctrl+C)' -Value exists -Optional -TimeoutSec 60
                $before = Join-Path $EvidenceDir 'c-rec-gpu-before.png'
                $null = Get-LabScreenshot -Name $VmName -Path $before
                $r2.yellowBefore = YellowShare $before
                Record $r2 'gpu' -Seconds 10 -During {
                    Start-Sleep -Seconds 3
                    $d1 = Join-Path $EvidenceDir 'c-rec-gpu-during1.png'
                    $null = Get-LabScreenshot -Name $VmName -Path $d1
                    Start-Sleep -Seconds 2
                    $d2 = Join-Path $EvidenceDir 'c-rec-gpu-during2.png'
                    $null = Get-LabScreenshot -Name $VmName -Path $d2
                    $r2.yellowDuring = @((YellowShare $d1), (YellowShare $d2))
                }
                $r2.video = @(VideoInfo @(@(Videos)[-1].Path))[0]
                $r2.log = DiagLog 'GPU capture|Recording (started|saved)'
            }
            Save 'settings' $r2
        }

        It 'C-REC-02 Settings > Recordings 30 fps, Full HD: the file is 1920 x 1080 at 30 fps' -Tag 'C-REC-02' {
            $r2.video.width | Should -Be 1920
            $r2.video.height | Should -Be 1080
            $r2.video.fps | Should -BeGreaterOrEqual 29
            $r2.video.fps | Should -BeLessOrEqual 31
        }

        It 'C-REC-03 capture with the graphics card: the file plays; on Windows 11 no yellow capture border on the screen' -Tag 'C-REC-03' {
            $r2.gpu | Should -Be 'On'
            "$($r2.video.error)" | Should -BeNullOrEmpty
            $r2.video.seconds | Should -BeGreaterThan 0
            # the Console VMs are Windows 11 too
            if ($vm.Os -in 'W11', 'Console') {
                (($r2.yellowDuring | Measure-Object -Maximum).Maximum - $r2.yellowBefore) | Should -BeLessThan 0.002 -Because 'a yellow border around the recorded window would add about 1 % of the screen'
            }
        }
    }

    Context 'RemoteHub killed while recording' -Skip:$isN {
        BeforeAll {
            $r4 = [ordered]@{}
            Step $r4 kill {
                RecButton 'Record this tab to a video file'
                $null = Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 5
                Start-Sleep -Seconds 20
                Stop-LabClient -Name $VmName -Task $script:task
                $script:task = $null
                Start-Sleep -Seconds 3
                $r4.file = @(Videos)[-1]
                $r4.video = @(VideoInfo @($r4.file.Path))[0]
            }
            $script:task = Start-LabClient -Name $VmName
            Save 'kill' $r4
        }

        # The file was never closed: Windows.Media.Editing (MediaClip) refuses such a file ("One or more errors occurred",
        # as the unit test notes: the editing API wants a closed file), players open it through MediaSource. The duration
        # is the length of the frames in its complete fragments.
        It 'C-REC-04 Stop-Process -Force while recording: the MP4 opens and has a duration (fragmented MP4)' -Tag 'C-REC-04' {
            $r4.file.Size | Should -BeGreaterThan 0
            "$($r4.video.openError)" | Should -BeNullOrEmpty
            $r4.video.opened | Should -BeTrue -Because 'a player opens the file'
            $r4.video.moofs | Should -BeGreaterThan 0 -Because 'fragments were written while recording'
            $r4.video.fragSeconds | Should -BeGreaterThan 0
        }
    }

    Context 'the last frame and the stop click' -Skip:$isN {
        BeforeAll {
            $r5 = [ordered]@{}
            Step $r5 clock {
                Open-Tool 'PowerShell'
                $null = Ui 'Stop the running command (Ctrl+C)' -Value exists -TimeoutSec 60
                Start-Sleep -Seconds 5
                # a clock in the PowerShell tab (the device's time)
                $null = Ui 'type:Edit' -Value 'set:while ($true) { Get-Date -Format HH:mm:ss; Start-Sleep -Seconds 1 }'
                $null = Ui 'type:Edit' -Value 'keys:{ENTER}'
                Start-Sleep -Seconds 5
                $t0 = Get-Date
                RecButton 'Record this tab to a video file'
                $null = Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 5
                Start-Sleep -Seconds 12
                RecButton 'Stop recording and save the video'
                $r5.span = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                # taken once the stop click has returned: no frame may show a later time
                # formatted on the device: the clock in the picture is its local time, and a time brought over to the lab host is
                # shown in the host's time zone (run 20261021-222745: 15:59 in the frame, 00:59 here)
                $r5.deviceStop = Invoke-LabGuest -Name $device.Name -ScriptBlock { (Get-Date).ToString('HH:mm:ss') }
                $r5.saved = Message -Button 'Close' -TimeoutSec 60
                $null = Ui 'Stop the running command (Ctrl+C)' -Optional -TimeoutSec 5
                $thumb = 'C:\ProgramData\RemoteHubLab\session\c-rec-last.img'
                $r5.video = @(VideoInfo @(@(Videos)[-1].Path) -Thumb $thumb)[0]
                Get-LabFile -Name $VmName -Path $thumb -Destination $EvidenceDir
                $r5.ocr = "$((Get-LabOcr -Path (Join-Path $EvidenceDir 'c-rec-last.img')).text)"
                # OCR splits the clock's separators into spaces, dots and bullets ("22: 48:35", "22. *48*.38" with a
                # bullet; run 20261022-061254): any of them between the parts, the time rebuilt as HH:mm:ss
                $sep = "[\s:.$([char]0x2022)]"
                $clock = "(?<!\d)([01]\d|2[0-3])$sep*?[:.$([char]0x2022)]$sep*([0-5]\d)$sep*?[:.$([char]0x2022)]$sep*([0-5]\d)(?!\d)"
                function ClockTimes([string]$Text) { @([regex]::Matches($Text, $clock) | ForEach-Object { '{0}:{1}:{2}' -f $_.Groups[1].Value, $_.Groups[2].Value, $_.Groups[3].Value }) }
                $r5.times = ClockTimes $r5.ocr
                if (-not $r5.times.Count) {
                    # Windows OCR missed the console's clock lines at the frame's own size while the frame showed them
                    # (runs 20261019-193627 and 20261021-062735): read again from a copy twice as large
                    Add-Type -AssemblyName System.Drawing
                    $big = Join-Path $EvidenceDir 'c-rec-last-x2.png'
                    $img = [Drawing.Image]::FromFile((Join-Path $EvidenceDir 'c-rec-last.img'))
                    try {
                        $bmp = New-Object Drawing.Bitmap($img, ($img.Width * 2), ($img.Height * 2))
                        try { $bmp.Save($big, [Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
                    }
                    finally { $img.Dispose() }
                    $r5.ocr2 = "$((Get-LabOcr -Path $big).text)"
                    $r5.times = ClockTimes $r5.ocr2
                }
                # fragments of neighbouring lines can make a time ("22. 22. 48" -> 22:22:48): only times from the
                # recording on are kept; one after the stop still counts (that is what C-REC-05 looks for)
                $r5.timesRead = $r5.times
                $from = [TimeSpan]::Parse("$($r5.deviceStop)") - [TimeSpan]::FromSeconds($r5.span + 30)
                $r5.times = @($r5.times | Where-Object { [TimeSpan]::Parse($_) -ge $from })
            }
            Shot 'clock'
            Save 'stop-time' $r5
        }

        It 'C-REC-05 the duration isn''t longer than start to stop click; the last frame shows no time after the stop' -Tag 'C-REC-05' {
            "$($r5.video.error)" | Should -BeNullOrEmpty
            $r5.video.seconds | Should -BeGreaterThan 0
            $r5.video.seconds | Should -BeLessOrEqual $r5.span
            # the editing API's duration has been shorter than the frames in the file (10 s for 24 s recorded): the
            # frames' length and the player's duration aren't longer either
            foreach ($s in @($r5.video.fragSeconds, $r5.video.sourceSeconds) | Where-Object { $_ }) { $s | Should -BeLessOrEqual $r5.span }
            @($r5.times).Count | Should -BeGreaterThan 0 -Because 'the clock is in the last frame'
            (@($r5.times) | Sort-Object | Select-Object -Last 1) | Should -BeLessOrEqual $r5.deviceStop
        }
    }

    Context 'N edition without the Media Feature Pack' -Skip:(-not $isN) {
        BeforeAll {
            $r6 = [ordered]@{}
            Step $r6 record {
                $m = Ui 'Device' -Value 'menu:Remote desktop via RDP' -Optional
                if (-not $m.found -or ($m.PSObject.Properties['error'] -and $m.error)) { Key 1B; Menu 'Connect' }
                $r6.connected = Wait-RdpTab 90
                RecButton 'Record this tab to a video file'
                $null = Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 10
                # Dialogs.Info "Recording stopped": "The recording of Device . Remote desktop stopped: ..."
                $r6.message = Message -Title 'The recording of*' -TimeoutSec 60
                Start-Sleep -Seconds 3
                $r6.running = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count }
                $r6.videos = Videos
            }
            Shot 'n-edition'
            Save 'n-edition' $r6
        }

        It 'C-REC-06 N edition: "Recording stopped" names the missing H.264 encoder; RemoteHub keeps running, no broken file' -Tag 'C-REC-06' {
            $r6.message | Should -Match 'The recording of Device'
            $r6.message | Should -Match 'The H\.264 video encoder is not available on this PC \(Windows N editions need the Media Feature Pack\)'
            $r6.running | Should -Be 1
            @($r6.videos | Where-Object Size -eq 0) | Should -BeNullOrEmpty
        }
    }
}
