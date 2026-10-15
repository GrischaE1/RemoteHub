# C-REC - recording RemoteHub's tabs to MP4 files on the admin's PC (this client VM, the console account's session,
# driven by UI Automation) against the device (the Server VM): every tab type (remote desktop, agent desktop, PowerShell,
# file transfer, registry, services, processes) into Videos\RemoteHub, the settings (30 fps, Full HD, graphics card
# capture and its border), RemoteHub killed while recording, the last frame against the stop click, and the N edition's
# message. The files are read in the client's session with Windows' Media Foundation (Windows.Media.Editing:
# duration, size, frame rate, a frame). The agent is paired with RemoteHub through its clipboard install (files taken
# to the device over PowerShell Direct, msiexec there) - no WinRM needed. Client VMs only (2VM; C-REC-06 only on an N
# edition VM). See docs/test-lab-cases.md section 10.6.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $labVm = Get-LabVm $VmName
    $isClient = $labVm.Os -ne 'Server'
    # an N edition (no Media Feature Pack): "W11-26H2-N-en-US"
    $isN = $labVm.Id -match '(^|-)N(-|$)'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    $device = @(Get-LabVm | Where-Object Os -eq 'Server')[0]
    $script:task = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "c-rec-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-rec-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of the connection's context menu ("A>B" = submenu: "Tools", "RemoteHub Agent", "More")
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) { $null = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec }
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
        }
    }
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
    function AuditText([string]$Node = 'Device') {
        Menu 'More>Audit log*' -Node $Node
        $l = Ui '*' -Title 'Audit log*' -Value list -TimeoutSec 30
        $null = Ui 'Close' -Title 'Audit log*' -Optional -TimeoutSec 5
        (@($l.items) -join "`n")
    }
    # a tool tab; when the connection's agent wasn't tested yet the menu offers "Test RemoteHub Agent" instead
    function Open-Tool([string]$Entry) {
        $m = Ui 'Device' -Value "menu:Tools>$Entry" -Optional
        if (-not $m.found -or ($m.PSObject.Properties['error'] -and $m.error)) {
            Key 1B
            Menu 'Tools>Test RemoteHub Agent*'
            $null = Message -TimeoutSec 90
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
    # decode them); -Thumb: the last frame saved there
    function VideoInfo([string[]]$Paths, [string]$Thumb = '') {
        $files = (@($Paths) | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','
        @(InSession $VmName Console $code.video @{ FILES = $files; THUMB = $Thumb } -TimeoutSec 240)
    }
    # Record this tab to a video file, the first-time notice accepted, -Seconds later stopped, "Recording saved" closed;
    # -During runs while it records
    function Record([System.Collections.IDictionary]$R, [string]$Key, [int]$Seconds = 10, [scriptblock]$During) {
        $null = Ui 'Record this tab to a video file' -TimeoutSec 20
        $R["$Key-notice"] = (Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 8).found
        $t0 = Get-Date
        if ($During) { . $During }
        $left = $Seconds - ((Get-Date) - $t0).TotalSeconds
        if ($left -gt 0) { Start-Sleep -Seconds ([int][math]::Ceiling($left)) }
        $null = Ui 'Stop recording and save the video' -TimeoutSec 20
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
        $until = (Get-Date).AddSeconds(150)
        do { Start-Sleep -Seconds 15; $o.audit = AuditText $Node } while ($o.audit -notmatch 'answered as RemoteHub Agent' -and (Get-Date) -lt $until)
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
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$t) { $task = $asTask.MakeGenericMethod($t).Invoke($null, @($op)); $task.Wait(); $task.Result }
$thumb = '__THUMB__'
$out = @(foreach ($p in @(__FILES__)) {
        $o = [ordered]@{ name = (Split-Path $p -Leaf); size = (Get-Item $p).Length }
        try {
            $f = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($p)) ([Windows.Storage.StorageFile])
            $clip = Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($f)) ([Windows.Media.Editing.MediaClip])
            $o.seconds = $clip.OriginalDuration.TotalSeconds
            $v = $clip.GetVideoEncodingProperties()
            $o.width = $v.Width; $o.height = $v.Height
            if ($v.FrameRate.Denominator) { $o.fps = [math]::Round($v.FrameRate.Numerator / $v.FrameRate.Denominator, 2) }
            if ($thumb) {
                $comp = New-Object Windows.Media.Editing.MediaComposition
                $comp.Clips.Add($clip)
                $at = [TimeSpan]::FromSeconds([math]::Max(0, $o.seconds - 0.3))
                $img = Await ($comp.GetThumbnailAsync($at, 0, 0, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream])
                $s = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($img)
                $fs = [IO.File]::Create($thumb)
                try { $s.CopyTo($fs) } finally { $fs.Close() }
                $o.thumb = $thumb
            }
        }
        catch { $o.error = "$_" }
        [pscustomobject]$o
    })
'JSON:' + (ConvertTo-Json -Compress -InputObject $out)
'@

    $kinds = 'Remote desktop', 'Desktop (agent)', 'PowerShell', 'File transfer', 'Registry', 'Services', 'Processes'

    if ($vm.Os -ne 'Server') {
        Reset-LabVm -Name $device.Name
        $null = Invoke-LabGuest -Name $device.Name -ScriptBlock {
            # Remote Desktop on (the firewall group by its resource name - display names are localized)
            Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
            Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752' -ErrorAction SilentlyContinue
        }
        $deviceIp = Get-LabVmAddress -Name $device.Name
        Reset-LabVm -Name $VmName
        # the lab isn't a domain: saved credentials go to RDP only with the NTLM delegation policy (R-RDP-01)
        Allow-NtlmDelegation
        $null = Install-LabClient -Name $VmName
        $script:task = Start-LabClient -Name $VmName
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $device.AdminAccount -Password $password
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
    if ($vm.Os -ne 'Server') {
        Shot 'end'
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        try { Save-LabAgentEvidence -Name $device.Name -Destination (Join-Path $EvidenceDir 'c-rec-device-logs') } catch { }
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
                Record $r1 'Remote desktop'
            }
            Step $r1 agent {
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
        }

        It 'C-REC-01 one MP4 per tab type in Videos\RemoteHub, named "Device - <tab> - <date time>.mp4", each with a duration; audit started / saved' -Tag 'C-REC-01' {
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
                $r2.gpu = (Ui 'Capture with the graphics card*' -Title 'Settings' -Value 'toggle-on').value
                $null = Ui 'Save' -Title 'Settings'
            }
            Step $r2 record {
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
            if ($vm.Os -eq 'W11') {
                (($r2.yellowDuring | Measure-Object -Maximum).Maximum - $r2.yellowBefore) | Should -BeLessThan 0.002 -Because 'a yellow border around the recorded window would add about 1 % of the screen'
            }
        }
    }

    Context 'RemoteHub killed while recording' -Skip:$isN {
        BeforeAll {
            $r4 = [ordered]@{}
            Step $r4 kill {
                $null = Ui 'Record this tab to a video file' -TimeoutSec 20
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

        It 'C-REC-04 Stop-Process -Force while recording: the MP4 opens and has a duration (fragmented MP4)' -Tag 'C-REC-04' {
            $r4.file.Size | Should -BeGreaterThan 0
            "$($r4.video.error)" | Should -BeNullOrEmpty
            $r4.video.seconds | Should -BeGreaterThan 0
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
                $null = Ui 'Record this tab to a video file' -TimeoutSec 20
                $null = Ui 'Start recording' -Title '?*' -Optional -TimeoutSec 5
                Start-Sleep -Seconds 12
                $null = Ui 'Stop recording and save the video' -TimeoutSec 20
                $r5.span = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
                # taken once the stop click has returned: no frame may show a later time
                $r5.deviceStop = (Get-LabGuestTime -Name $device.Name).ToString('HH:mm:ss')
                $r5.saved = Message -Button 'Close' -TimeoutSec 60
                $null = Ui 'Stop the running command (Ctrl+C)' -Optional -TimeoutSec 5
                $thumb = 'C:\ProgramData\RemoteHubLab\session\c-rec-last.img'
                $r5.video = @(VideoInfo @(@(Videos)[-1].Path) -Thumb $thumb)[0]
                Get-LabFile -Name $VmName -Path $thumb -Destination $EvidenceDir
                $r5.ocr = "$((Get-LabOcr -Path (Join-Path $EvidenceDir 'c-rec-last.img')).text)"
                $r5.times = @([regex]::Matches($r5.ocr, '\b([01]\d|2[0-3]):[0-5]\d:[0-5]\d\b') | ForEach-Object Value)
            }
            Shot 'clock'
            Save 'stop-time' $r5
        }

        It 'C-REC-05 the duration isn''t longer than start to stop click; the last frame shows no time after the stop' -Tag 'C-REC-05' {
            "$($r5.video.error)" | Should -BeNullOrEmpty
            $r5.video.seconds | Should -BeGreaterThan 0
            $r5.video.seconds | Should -BeLessOrEqual $r5.span
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
                $null = Ui 'Record this tab to a video file' -TimeoutSec 20
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
