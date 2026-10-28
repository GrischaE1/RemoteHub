# C-REC (more cases) - recording RemoteHub's tabs to MP4 files on the admin's PC (this client VM, the console account's
# session, driven by UI Automation) against the device (the Server VM).
#
# Covers (docs/test-lab-cases.md section 10.6):
#   C-REC-07 a network-share tab recorded ("<connection> - <share name> - <date time>.mp4"), C-REC-08 the tab hidden while
#   recording (the video says so), C-REC-09 a normal exit while recording (the file is finished and plays, no dialog),
#   C-REC-10 "Recording saved" (file, mm:ss . size, folder; Play / Show in folder / Close / Delete recording...),
#   C-REC-11 unsafe connection names (invalid characters -> "_", cut at 80 characters, a second file " (2)").
# Needs: 2VM (this Windows 11 client + the Server VM: Remote Desktop on, an SMB share "rhrec"); a Windows edition with
# Media Foundation's H.264 encoder (not an N edition - C-REC-06). This PC: the NTLM delegation policy for TERMSRV/* (the
# lab isn't a domain - R-RDP-01). The files are read in the client's session with Windows' Media Foundation
# (Windows.Media.Editing: duration, frames), frames are read by OCR on the host.
# Client VMs only. See docs/test-lab-cases.md section 10.6.
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
    # an N edition (no Media Feature Pack) can't record at all (C-REC-06)
    $isN = $labVm.Id -match '(^|-)N(-|$)'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $password = (Get-LabConfig).Password
    $device = Get-LabDevice -For $VmName
    $script:task = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-rec2-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-rec2-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of a connection's context menu ("A>B" = submenu, a parent that isn't there is skipped). The right-click
    # right after a dialog closed sometimes opens no menu at all (no menu entries anywhere): once more
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
    # a message box of RemoteHub (owned by the main window): its window name is the message text (or its title) -
    # -Titles the start of the message and the box's title; answers the window name and its texts, then clicks -Button
    function Box([string[]]$Titles, [string]$Button = '', [int]$TimeoutSec = 60) {
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
    function BoxShown([string[]]$Titles, [int]$TimeoutSec = 3) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { foreach ($t in $Titles) { if ((Ui '*' -Title $t -Value exists -Optional -TimeoutSec 1).found) { return $true } } } while ((Get-Date) -lt $until)
        $false
    }
    $savedBox = @('*.mp4*', 'Recording saved')
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
            # a recording the failed step started is stopped and saved: else the next case finds "Stop recording" where it
            # wants "Record this tab" (C-REC-08's recording ran on into C-REC-09)
            try {
                $s = Ui 'Stop recording and save the video' -Optional -TimeoutSec 3
                if ($s.found) { $R["$Name-stoppedRecording"] = $true; $null = Box $savedBox -Button 'Close' -TimeoutSec 60 }
            }
            catch { }
        }
    }
    function InSession([string]$Vm, [string]$Account, [string]$Code, [hashtable]$Values = @{}, [int]$TimeoutSec = 120) {
        foreach ($k in $Values.Keys) { $Code = $Code.Replace("__$($k)__", [string]$Values[$k]) }
        $raw = "$(Invoke-LabInSession -Name $Vm -Account $Account -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($Code)))"
        if ($raw -match '(?m)^JSON:(.*)$') { return ($Matches[1].Trim() | ConvertFrom-Json) }
        if ($raw.Trim() -like 'ERROR:*') { throw "$Vm / ${Account}: $($raw.Trim())" }
        $raw.Trim()
    }
    function WaitFor([scriptblock]$Cond, [int]$TimeoutSec = 60, [int]$Every = 3) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do { if (& $Cond) { return $true }; Start-Sleep -Seconds $Every } while ((Get-Date) -lt $until)
        $false
    }
    # the admin PC's local time (RemoteHub names a recording after it): a moment on the recording's own clock
    function GuestNow {
        $s = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff') }
        [datetime]::ParseExact("$s", 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
    }
    function TopWindows { @((Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'zz-no-such-window' -Text 'zz' -Value exists -TimeoutSec 1).windows) }
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
    # (connections, settings, recordings) and the NTLM delegation policy. Throws when something stays.
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
    function EditConnection([scriptblock]$Do, [string]$Node = 'Device') {
        Menu 'Edit*' -Node $Node
        $null = Ui 'Save' -Title 'Edit connection' -Value exists -TimeoutSec 30
        & $Do
        $null = Ui 'Save' -Title 'Edit connection'
        Start-Sleep -Seconds 2
    }
    # The share recshare under Device (Tools > Add network share...), added when it isn't there yet: C-REC-08 needs it
    # too, and a rerun filtered to C-REC-08 skipped the context that adds it (the run of 17.10.2026 05:47: "'recshare'
    # not found", the tree had Device only)
    function Add-RecShare {
        if ((Ui 'recshare' -Value exists -Optional -TimeoutSec 5).found) { return }
        # "Add network share..." is in the Tools group of the connection's menu (MainWindow.xaml CmAddShare)
        Menu 'Tools>Add network share*'
        $null = Ui 'Name' -Title 'Add network share to Device' -Value 'setnext:recshare' -TimeoutSec 30
        $null = Ui 'Share name' -Title 'Add network share to Device' -Value 'setnext:rhrec'
        $null = Ui 'Add' -Title 'Add network share to Device'
        Start-Sleep -Seconds 3
    }
    # Double-clicks recshare in the tree. Not found there (a new item isn't always scrolled into view, its parent may be
    # collapsed): searched for in the search box, opened from the result, the search cleared again.
    function Open-RecShare {
        if ((Ui 'recshare' -Value exists -Optional -TimeoutSec 10).found) { $null = Ui 'recshare' -Value doubleclick -TimeoutSec 20; return }
        $null = Ui 'SearchBox' -Value 'set:recshare'
        Start-Sleep -Seconds 2
        try { $null = Ui 'recshare' -Value doubleclick -TimeoutSec 20 }
        finally { $null = Ui 'SearchBox' -Value 'set:' -Optional -TimeoutSec 5 }
    }
    function CloseAll { foreach ($i in 1..6) { $c = Ui 'Close all tabs of this connection' -Optional -TimeoutSec 3; if (-not $c.found) { break }; Start-Sleep -Seconds 2 } }
    # the console account's recordings, oldest first (empty files are the placeholders of C-REC-11)
    function Videos {
        @(Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock {
                param($u) @(Get-ChildItem "C:\Users\$u\Videos\RemoteHub" -Filter *.mp4 -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Path = $_.FullName; Size = $_.Length } })
            })
    }
    function NewestVideo { @(Videos | Where-Object Size -gt 0) | Select-Object -Last 1 }
    # duration of MP4 files (Media Foundation through Windows.Media.Editing, as the unit tests decode them)
    function VideoInfo([string[]]$Paths) {
        $files = (@($Paths) | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','
        @(InSession $VmName Console $code.video @{ FILES = $files } -TimeoutSec 240)
    }
    # frames of a recording at the given seconds, taken to the evidence folder and read by OCR on the host
    function FrameTexts([string]$Path, [double[]]$Seconds, [string]$Prefix) {
        $guestDir = 'C:\ProgramData\RemoteHubLab\session\c-rec2-frames'
        $times = ($Seconds | ForEach-Object { [string]$_ }) -join ','
        $out = InSession $VmName Console $code.frames @{ FILE = ("'" + ($Path -replace "'", "''") + "'"); TIMES = $times; OUT = $guestDir } -TimeoutSec 240
        @(foreach ($f in @($out)) {
                if (-not $f.path) { [pscustomobject]@{ at = $f.at; text = ''; error = $f.error }; continue }
                $local = Join-Path $EvidenceDir "c-rec2-$Prefix-$($f.at)s.img"
                Get-LabFile -Name $VmName -Path $f.path -Destination $EvidenceDir
                Move-Item (Join-Path $EvidenceDir (Split-Path $f.path -Leaf)) $local -Force
                [pscustomobject]@{ at = $f.at; text = "$((Get-LabOcr -Path $local).text)"; error = $null }
            })
    }
    # Record this tab to a video file; the first-time notice accepted
    function StartRecording([System.Collections.IDictionary]$R, [string]$Key) {
        $null = Ui 'Record this tab to a video file' -TimeoutSec 20
        $R["$Key-notice"] = BoxShown @('Recording saves everything*', 'Record this tab') -TimeoutSec 4
        if ($R["$Key-notice"]) { $null = Box @('Recording saves everything*', 'Record this tab') -Button 'Start recording' }
    }
    # stopped: the button clicked, or - when it isn't found but "Recording saved" is up - it was clicked already (an
    # action of the UI driver run twice: the first click stopped it, the second didn't find the button any more)
    function StopRecording {
        $s = Ui 'Stop recording and save the video' -Optional -TimeoutSec 60
        if (-not $s.found -and -not (BoxShown $savedBox -TimeoutSec 5)) {
            throw "UI step failed: 'Stop recording and save the video' not found in 60 s and no 'Recording saved'$(if ($s.PSObject.Properties['searchError'] -and $s.searchError) { " (last search error: $($s.searchError))" })"
        }
    }

    $code = @{}
    $wrt = @'
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaClip, Windows.Media.Editing, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaComposition, Windows.Media.Editing, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.ImageStream, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
function Clip([string]$p) {
    $f = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($p)) ([Windows.Storage.StorageFile])
    Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($f)) ([Windows.Media.Editing.MediaClip])
}
'@
    $code.video = $wrt + @'
$out = @(foreach ($p in @(__FILES__)) {
        $o = [ordered]@{ name = (Split-Path $p -Leaf); size = (Get-Item $p).Length }
        try { $o.seconds = (Clip $p).OriginalDuration.TotalSeconds }
        catch { $o.error = "$_" }
        [pscustomobject]$o
    })
'JSON:' + (ConvertTo-Json -Compress -InputObject $out)
'@
    $code.frames = $wrt + @'
$dir = '__OUT__'
$null = New-Item -ItemType Directory -Path $dir -Force
$out = @(foreach ($t in @(__TIMES__)) {
        $o = [ordered]@{ at = $t }
        try {
            $comp = New-Object Windows.Media.Editing.MediaComposition
            # Clips is a WinRT IVector: in Windows PowerShell a __ComObject without Add - called through the .NET
            # interface it is projected to
            $null = [System.Collections.Generic.ICollection[Windows.Media.Editing.MediaClip]].GetMethod('Add').Invoke($comp.Clips, @(, (Clip __FILE__)))
            $img = Await ($comp.GetThumbnailAsync([TimeSpan]::FromSeconds($t), 0, 0, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream])
            $s = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($img)
            $o.path = Join-Path $dir ('frame-' + ([string]$t).Replace('.', '_') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.img')
            $fs = [IO.File]::Create($o.path)
            try { $s.CopyTo($fs) } finally { $fs.Close() }
        }
        catch { $o.error = "$_" }
        [pscustomobject]$o
    })
'JSON:' + (ConvertTo-Json -Compress -InputObject $out)
'@
    $code.closeMain = @'
$p = @(Get-Process RemoteHub -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0)
foreach ($x in $p) { [void]$x.CloseMainWindow() }
'JSON:' + (ConvertTo-Json -Compress -InputObject @{ closed = $p.Count })
'@
    $code.recycled = @'
'JSON:' + (ConvertTo-Json -Compress -InputObject @(@((New-Object -ComObject Shell.Application).NameSpace(10).Items()) | ForEach-Object { [string]$_.Name }))
'@

    # C-REC-11: a connection name with characters Windows doesn't allow in file names, longer than 80 characters with the
    # tab kind; the name RemoteHub's Recordings.FileName makes of it
    $unsafeName = 'Rec11 a:b*c?d<e>f|g"h/i\j ' + ('L' * 70)
    $invalid = [IO.Path]::GetInvalidFileNameChars()
    $stem = -join @(("$unsafeName - Remote desktop").ToCharArray() | ForEach-Object { if ($invalid -contains $_) { '_' } else { [string]$_ } })
    $stem = $stem.Trim(' ', '.')
    if ($stem.Length -gt 80) { $stem = $stem.Substring(0, 80).TrimEnd(' ', '.') }

    if ($isClient) {
        Reset-LabVm -Name $device.Name
        $null = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
            # an SMB share for the network-share tab (File and Printer Sharing by its resource name)
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue
            $everyone = (New-Object Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([Security.Principal.NTAccount]).Value
            $null = New-Item -ItemType Directory -Path 'C:\rhrec' -Force
            Set-Content -Encoding ASCII 'C:\rhrec\readme.txt' 'a file on the share'
            if (-not (Get-SmbShare -Name 'rhrec' -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name 'rhrec' -Path 'C:\rhrec' -FullAccess $everyone }
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        # RemoteHub installed (kept from an earlier file when it left the VM so; AfterAll empties RemoteHub's data)
        $null = Initialize-LabVm -Name $VmName -Client
        Allow-NtlmDelegation
        $null = Invoke-LabGuest -Name $VmName -ScriptBlock { $d = 'C:\ProgramData\RemoteHubLab\session'; $null = New-Item -ItemType Directory -Path $d -Force; icacls $d /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null }
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
        # the device: Remote Desktop on, the SMB share rhrec
        if ($device) { Set-LabVmDirty -Name $device.Name }
        Stop-LabVm -Name $device.Name
    }
}

Describe 'C-REC2 <VmName>' -Tag Client, UI, 'C-REC' -Skip:(-not $isClient -or $isN) {

    Context 'a network-share tab recorded' {
        BeforeAll {
            $r7 = [ordered]@{}
            Step $r7 share {
                Add-RecShare
                Open-RecShare
                $r7.tab = Ui 'Upload selected files' -Value exists -Optional -TimeoutSec 60
                Start-Sleep -Seconds 3
                StartRecording $r7 'share'
                Start-Sleep -Seconds 8
                StopRecording
                $r7.saved = Box $savedBox -Button 'Close'
                $r7.video = NewestVideo
                $r7.info = @(VideoInfo @($r7.video.Path))[0]
            }
            Shot 'share'
            Save 'share' $r7
        }

        It 'C-REC-07 a network-share tab recorded: "[connection] - [share name] - [date time].mp4", it plays' -Tag 'C-REC-07' {
            $r7['share-error'] | Should -BeNullOrEmpty
            $r7.tab.found | Should -BeTrue
            $r7.video.Name | Should -Match '^Device - recshare - \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}( \(\d+\))?\.mp4$'
            "$($r7.info.error)" | Should -BeNullOrEmpty
            $r7.info.seconds | Should -BeGreaterThan 0
        }
    }

    Context 'the tab hidden while recording' {
        BeforeAll {
            $r8 = [ordered]@{}
            Step $r8 hidden {
                CloseAll
                Add-RecShare
                Menu 'Connect'
                $r8.connected = Wait-RdpTab 90
                StartRecording $r8 'hidden'
                Start-Sleep -Seconds 4
                # another tab of the connection in front: the recorded one isn't visible. The hidden period is taken on
                # the video's own clock, from when the share tab's toolbar is found: a clock started after "Record" and
                # the double-click step returning are not on it (run 20261016-212514: frames up to 38 s still showed the
                # remote desktop, the placeholder began at 40 s, but the test had taken 22.6 s as the start)
                Open-RecShare
                $r8.shareTab = (Ui 'Upload selected files' -Value exists -Optional -TimeoutSec 60).found
                $hiddenAt = GuestNow; $r8.hiddenAt = $hiddenAt.ToString('HH:mm:ss.fff')
                Start-Sleep -Seconds 10
                # ends at the latest when Connect is clicked: the guest's time before it
                $shownAt = GuestNow; $r8.shownAt = $shownAt.ToString('HH:mm:ss.fff')
                # the RDP tab in front again (Connect selects the open one)
                Menu 'Connect'
                Start-Sleep -Seconds 4
                StopRecording
                $null = Box $savedBox -Button 'Close'
                $r8.video = NewestVideo
                # seconds into the video: it starts at the time in its file name (the guest's local time as RemoteHub
                # took it, whole seconds - the first frame is up to 1 s later), on the same clock as GuestNow
                $started = [datetime]::ParseExact([regex]::Match($r8.video.Name, '\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}').Value, 'yyyy-MM-dd HH-mm-ss', [Globalization.CultureInfo]::InvariantCulture)
                $r8.hiddenFrom = [math]::Round(($hiddenAt - $started).TotalSeconds, 1)
                $r8.hiddenTo = [math]::Round(($shownAt - $started).TotalSeconds, 1)
                $from = [math]::Ceiling($r8.hiddenFrom + 1); $to = [math]::Floor($r8.hiddenTo - 2)
                $at = @(if ($to -ge $from) { $from..$to | Where-Object { ($_ - $from) % 2 -eq 0 } } else { $r8.hiddenFrom + 1 })
                $r8.frames = @(FrameTexts $r8.video.Path $at 'hidden')
                $r8.visible = @(FrameTexts $r8.video.Path @(1) 'visible')
            }
            Shot 'hidden'
            Save 'hidden' $r8
        }

        It 'C-REC-08 the recorded tab hidden: the video shows "Tab not visible - recording continues when it is shown again"' -Tag 'C-REC-08' {
            $r8['hidden-error'] | Should -BeNullOrEmpty
            $r8.connected | Should -BeTrue
            $r8.shareTab | Should -BeTrue -Because 'the share tab came to the front, hiding the recorded one'
            @($r8.frames).Count | Should -BeGreaterThan 0
            # every frame of the hidden period: the placeholder, never the remote screen. The dash between the two
            # parts is matched loosely - OCR may read the en dash as another dash, and Get-LabOcr reads RhPerson's
            # UTF-8 answer as ANSI in Windows PowerShell, so one dash arrives as up to three characters
            foreach ($f in @($r8.frames)) {
                "$($f.error)" | Should -BeNullOrEmpty -Because "the frame at $($f.at) s was read"
                $f.text | Should -Match 'Tab not visible \S{1,3} recording continues when it is shown again' -Because "the frame at $($f.at) s lies in the hidden period ($($r8.hiddenFrom) - $($r8.hiddenTo) s)"
            }
            (@($r8.visible | ForEach-Object text) -join "`n") | Should -Not -Match 'Tab not visible' -Because 'while the tab is in front it is recorded itself'
        }
    }

    Context 'a normal exit while recording' {
        BeforeAll {
            $r9 = [ordered]@{}
            Step $r9 exit {
                $before = @(Videos).Count
                Menu 'Connect'
                $r9.connected = Wait-RdpTab 60
                StartRecording $r9 'exit'
                Start-Sleep -Seconds 10
                $null = InSession $VmName Console $code.closeMain
                # sessions and a recording: "... tab(s) are being recorded. Disconnect and exit? Recordings are saved."
                $r9.ask = Box @('*Disconnect and exit?*', 'Exit') -Button 'Exit' -TimeoutSec 30
                $t0 = Get-Date
                $r9.dialog = $false
                $r9.gone = WaitFor -TimeoutSec 60 -Every 1 {
                    if (BoxShown $savedBox -TimeoutSec 1) { $r9.dialog = $true }
                    (Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process RemoteHub -ErrorAction SilentlyContinue).Count }) -eq 0
                }
                $r9.exitSeconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                $r9.newFiles = @(Videos).Count - $before
                $r9.video = NewestVideo
                $r9.info = @(VideoInfo @($r9.video.Path))[0]
            }
            # RemoteHub again for the next cases
            try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
            $script:task = Start-LabClient -Name $VmName
            Save 'exit' $r9
        }

        It 'C-REC-09 a normal exit while recording: asked once, the recording finishes (up to 10 s), the file plays, no "Recording saved" dialog' -Tag 'C-REC-09' {
            $r9['exit-error'] | Should -BeNullOrEmpty
            $r9.ask | Should -Match 'tab\(s\) are being recorded'
            $r9.ask | Should -Match 'Recordings are saved\.'
            $r9.gone | Should -BeTrue
            $r9.exitSeconds | Should -BeLessOrEqual 20 -Because 'the recordings get up to 10 s to finish'
            $r9.dialog | Should -BeFalse
            $r9.newFiles | Should -Be 1
            "$($r9.info.error)" | Should -BeNullOrEmpty
            $r9.info.seconds | Should -BeGreaterOrEqual 8
        }
    }

    Context '"Recording saved": Play, Show in folder, Delete recording' {
        BeforeAll {
            $r10 = [ordered]@{}
            $folder = "C:\Users\$($vm.ConsoleAccount)\Videos\RemoteHub"
            Step $r10 saved {
                Menu 'Connect'
                $r10.connected = Wait-RdpTab 90
                StartRecording $r10 'saved'
                Start-Sleep -Seconds 5
                StopRecording
                $r10.box = Box $savedBox
                $r10.file = NewestVideo
                $r10.buttons = [ordered]@{}
                $boxTitle = [WildcardPattern]::Escape(($r10.box -split "`n")[0]) + '*'
                foreach ($b in 'Play', 'Show in folder', 'Close', 'Delete recording*') { $r10.buttons[$b] = (Ui $b -Title $boxTitle -Value exists -Optional -TimeoutSec 5).found }
                Shot 'saved'
                $null = Box $savedBox -Button 'Show in folder'
                $r10.explorer = WaitFor -TimeoutSec 30 -Every 2 { @(TopWindows | Where-Object { $_ -eq 'RemoteHub' -or $_ -like 'RemoteHub - File Explorer*' }).Count -gt 0 }
            }
            Step $r10 play {
                $windowsBefore = @(TopWindows)
                StartRecording $r10 'play'
                Start-Sleep -Seconds 4
                StopRecording
                $r10.playFile = NewestVideo
                $null = Box $savedBox -Button 'Play'
                $stemPlay = [IO.Path]::GetFileNameWithoutExtension($r10.playFile.Name)
                $r10.playWindows = @()
                $r10.player = WaitFor -TimeoutSec 45 -Every 3 {
                    $r10.playWindows = @(TopWindows | Where-Object { $windowsBefore -notcontains $_ })
                    @($r10.playWindows | Where-Object { $_ -like "*$stemPlay*" -or $_ -match 'Media Player|Movies|Films' }).Count -gt 0
                }
                Shot 'play'
                $null = Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process Microsoft.Media.Player, wmplayer, Video.UI -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
            }
            Step $r10 delete {
                StartRecording $r10 'delete'
                Start-Sleep -Seconds 4
                StopRecording
                $r10.deleteFile = NewestVideo
                $null = Box $savedBox -Button 'Delete recording*'
                $r10.deleteAsk = Box @('Delete *', 'Delete recording') -Button 'Delete'
                Start-Sleep -Seconds 3
                $r10.deleteLeft = @(Videos | Where-Object Name -eq $r10.deleteFile.Name).Count
                $r10.recycled = @(InSession $VmName Console $code.recycled)
            }
            CloseAll
            Save 'saved' $r10
        }

        It 'C-REC-10 "Recording saved": file, mm:ss . size, folder; Play opens it, Show in folder opens the folder, Delete recording... moves it to the Recycle Bin' -Tag 'C-REC-10' {
            foreach ($k in 'saved', 'play', 'delete') { $r10["$k-error"] | Should -BeNullOrEmpty -Because "the steps of '$k' ran" }
            $r10.box | Should -Match ([regex]::Escape($r10.file.Name))
            $r10.box | Should -Match '\b\d{2}:\d{2} \S \d+([.,]\d+)? (B|KB|MB|GB)\b'
            $r10.box | Should -Match ([regex]::Escape($folder))
            foreach ($b in $r10.buttons.Keys) { $r10.buttons[$b] | Should -BeTrue -Because "the box has '$b'" }
            $r10.explorer | Should -BeTrue -Because 'Show in folder opens Videos\RemoteHub'
            $r10.player | Should -BeTrue -Because "Play opens the file (new windows: $(@($r10.playWindows) -join ', '))"
            $r10.deleteAsk | Should -Match 'It goes to the Recycle Bin\.'
            $r10.deleteLeft | Should -Be 0
            @($r10.recycled | Where-Object { $_ -like "$([IO.Path]::GetFileNameWithoutExtension($r10.deleteFile.Name))*" }).Count | Should -BeGreaterThan 0
        }
    }

    Context 'unsafe connection names' {
        BeforeAll {
            $r11 = [ordered]@{}
            Step $r11 names {
                # a connection with the unsafe name; its RDP tab (nothing answers on that TEST-NET address) is recorded
                New-LabClientConnection -Name $VmName -Display $unsafeName -TargetHost '192.0.2.1'
                # not through its context menu: the name is wider than the list, and the address line below it is as
                # wide (a stack panel), so the middle of either text is outside the list - a right-click there opens no
                # menu. Not by Enter in the tree either: UIA SetFocus on the TreeView fails ("Operation is not valid due
                # to the current state of the object" - the tree doesn't take the keyboard focus itself), and its items
                # are all named "RemoteHub.Models.Connection". Through the search box instead: the name searched for
                # (ValuePattern), Enter in the box opens the first hit (SearchBox_PreviewKeyDown -> ActivateHit, as
                # Connect does), then the search cleared. The real click first brings RemoteHub to the front, so the
                # box can take the focus
                $null = Ui 'SearchBox' -Value realclick
                $null = Ui 'SearchBox' -Value 'set:Rec11'
                Start-Sleep -Milliseconds 500
                $null = Ui 'SearchBox' -Value 'keys:{ENTER}'
                Start-Sleep -Seconds 1
                $null = Ui 'SearchBox' -Value 'set:'
                Start-Sleep -Seconds 5
                StartRecording $r11 'first'
                Start-Sleep -Seconds 3
                StopRecording
                $null = Box $savedBox -Button 'Close'
                $r11.first = NewestVideo
                # the same name taken: files for every second of the next minute are there already (empty placeholders),
                # so the next recording gets " (2)"
                $r11.placeholders = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount, $stem -ScriptBlock {
                    param($u, $stem)
                    $d = "C:\Users\$u\Videos\RemoteHub"
                    $now = Get-Date
                    $n = 0
                    foreach ($i in 0..90) { $p = Join-Path $d ("$stem - " + $now.AddSeconds($i).ToString('yyyy-MM-dd HH-mm-ss') + '.mp4'); if (-not (Test-Path -LiteralPath $p)) { [IO.File]::WriteAllBytes($p, [byte[]]@()); $n++ } }
                    $n
                }
                StartRecording $r11 'second'
                Start-Sleep -Seconds 3
                StopRecording
                $null = Box $savedBox -Button 'Close'
                $r11.second = NewestVideo
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $vm.ConsoleAccount -ScriptBlock { param($u) Get-ChildItem "C:\Users\$u\Videos\RemoteHub" -Filter *.mp4 | Where-Object Length -eq 0 | Remove-Item -Force }
                CloseAll
            }
            Shot 'names'
            Save 'names' $r11
        }

        It 'C-REC-11 unsafe connection names: invalid characters become "_", the name is cut at 80 characters, a taken name gets " (2)"' -Tag 'C-REC-11' {
            $r11['names-error'] | Should -BeNullOrEmpty
            $stem.Length | Should -Be 80 -Because 'the test name is longer than 80 characters with the tab kind'
            $r11.first.Name | Should -Match ('^' + [regex]::Escape($stem) + ' - \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.mp4$')
            $r11.first.Name | Should -Not -Match '[:*?<>|"/\\]'
            $r11.second.Name | Should -Match ('^' + [regex]::Escape($stem) + ' - \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2} \(2\)\.mp4$')
        }
    }
}
