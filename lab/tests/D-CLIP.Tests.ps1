# D-CLIP - the clipboard of the agent's remote desktop, both ways: text, images and files (virtual files on the device,
# fetched only when something pastes; pasted with the shell's own "paste" verb in normaluser's session, as Explorer
# does), the limits, names that try to leave the clipboard folder, reparse points, broken transfers, the user's own
# rights, the policy levels and view-only. normaluser is signed in and allows PSAdmin. See docs/test-lab-cases.md
# section 9.9.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "d-clip-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "d-clip-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    function Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
    function Mark([string]$o, [string]$n) { @{ do = 'markFile'; path = (Join-Path $o "$n.txt") } }
    function Park([string]$o, [string]$n, [int]$t = 300) { @{ do = 'waitFile'; path = (Join-Path $o "$n.txt"); timeout = $t } }
    function Reached([string]$o, [string]$n, [int]$t = 180) { Wait-LabHostFile (Join-Path $o "$n.txt") -TimeoutSec $t }
    function Release([string]$o, [string]$n) { Set-Content (Join-Path $o "$n.txt") 'go' }
    # a session normaluser allows: $Steps run once connected (the test acts between Mark / Park pairs). -Features: what
    # the client takes (left out: the harness' default, paste-on-demand; @(): copies go at once, as from an older RemoteHub)
    function Begin([string]$Name, [object[]]$Steps, [bool]$ViewOnly = $false, [object[]]$Features) {
        $st = @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $ViewOnly }
        if ($PSBoundParameters.ContainsKey('Features')) { $st.features = @($Features) }
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, $st, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }) + $Steps + @(@{ do = 'close' }))
        $allow = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        if (-not $allow.clicked) {
            # refused after 30 s: the context fails here, not at an empty clipboard or a missing transfer later on
            Stop-Job $job; Remove-Job $job -Force
            throw "${Name}: Allow on the agent's consent dialog wasn't clicked within 60 s (the dialog asks for 30 s); OCR read: $(@($allow.lines) -join ' | ')$(if ($allow.PSObject.Properties['seen']) { '; windows seen: ' + (@($allow.seen) -join ' || ') })"
        }
        $job
    }
    # pastes the clipboard into a folder of normaluser's, as Explorer's Paste does, until $Count files are there
    # (Invoke-LabPaste: it pumps messages while the shell fetches the offered files); answers the paste's result - files
    # (Path, Name, Length, Hash), the clipboard's formats before the paste and the thread's apartment
    function Paste-AtDevice([string]$Folder, [int]$Count = 1) {
        Invoke-LabPaste -Name $VmName -Account User -Folder $Folder -Count $Count -TimeoutSec 120
    }
    function Clip-Folder { Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) "C:\Users\$u\AppData\Local\Temp\RemoteHub Clipboard" } }
    # one TransferEnd per pipeline item (not the whole list as one object: Where-Object must see each end)
    function Ends($r) { @($r.transfers | Where-Object kind -eq 'end') }
    # a record as JSON text for -Match: 5.1's ConvertTo-Json writes ' as \u0027 ("isn\u0027t"), so it is unescaped
    function JsonText($r) { [regex]::Unescape(($r | ConvertTo-Json -Depth 8)) }

    # files on the host
    $data = Out 'data'
    $null = New-Item -ItemType Directory -Path "$data\folder\sub" -Force
    1..3 | ForEach-Object { [IO.File]::WriteAllText("$data\file$_.txt", "clipboard file $_ $([guid]::NewGuid())") }
    [IO.File]::WriteAllText("$data\folder\sub\inner.txt", "inner $([guid]::NewGuid())")
    $hostHashes = @{}; foreach ($f in Get-ChildItem $data -Recurse -File) { $hostHashes[$f.Name] = Sha $f.FullName }
    Add-Type -AssemblyName System.Drawing
    $png = "$data\image.png"; $bmp = New-Object Drawing.Bitmap 64, 48; $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::FromArgb(10, 200, 30)); $g.Dispose(); $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $install = $init.Install
    $pin = $init.Pin
    # the person's first call after the sign-in starts its server, compiles its helper and starts the OCR (slow): done
    # here, not against the consent dialog's 30 s countdown (as in D-BANNER and D-MORE)
    $null = Invoke-LabPerson -Name $VmName -Do windows
    $null = Invoke-LabPerson -Name $VmName -Do screen-read
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-clip-agent-logs')
    # left reusable: the policies cleared, normaluser's clipboard emptied, the files and folders this file made removed.
    # A reparse point (the junction to drivers\etc, or one that got into the clipboard folder) is removed as a link only,
    # never followed: the walk below uses .NET, not Remove-Item -Recurse, which follows junctions on PowerShell 5.1.
    Set-LabAgentPolicy -Name $VmName -Clear
    $null = Invoke-LabInSession -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::Clear() }
    $null = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
        param($u)
        function Remove-Tree([string]$p) {
            if ([IO.File]::Exists($p)) { [IO.File]::SetAttributes($p, 'Normal'); [IO.File]::Delete($p); return }
            if (-not [IO.Directory]::Exists($p)) { return }
            if ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) { [IO.Directory]::Delete($p); return }
            foreach ($d in [IO.Directory]::GetDirectories($p)) { Remove-Tree $d }
            foreach ($f in [IO.Directory]::GetFiles($p)) { Remove-Tree $f }
            [IO.Directory]::Delete($p)
        }
        # the junction itself first (no recursion: only the link goes)
        $j = "C:\Users\$u\Documents\withjunction\junction"
        if ([IO.Directory]::Exists($j)) { [IO.Directory]::Delete($j) }
        foreach ($p in "C:\Users\$u\Documents\withjunction", 'C:\ProgramData\RemoteHubLab\secret', 'C:\ProgramData\RemoteHubLab\copyme',
            'C:\ProgramData\RemoteHubLab\big101.bin', 'C:\ProgramData\RemoteHubLab\newer', "C:\Users\$u\Desktop\Pasted", "C:\Users\$u\Desktop\Aborted",
            "C:\Users\$u\AppData\Local\Temp\RemoteHub Clipboard") {
            try { Remove-Tree $p } catch { Write-Warning "${p}: $($_.Exception.Message)" }
        }
    }
}

Describe 'D-CLIP <VmName>' -Tag Desktop, 'D-CLIP' {

    Context 'text and images' {
        BeforeAll {
            $t = [ordered]@{}
            $o = Out 'text'
            $uni = "Unicode $([char]0x00E4)$([char]0x00F6)$([char]0x00FC) $([char]0x6587)$([char]0x4EF6) $([char]0x2013) end"
            $job = Begin 'text' @(
                @{ do = 'clipboardText'; text = $uni }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'sent'), (Park $o 'copied')
                @{ do = 'sleep'; seconds = 5 }, (Mark $o 'big'), (Park $o 'bigcopied'), @{ do = 'sleep'; seconds = 10 }
                @{ do = 'clipboardImage'; path = $png }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'image'), (Park $o 'imagecopied')
                # the device's image is offered (paste-on-demand): pasted here, as RemoteHub's paste asks for it
                @{ do = 'requestFiles'; timeout = 30 }, @{ do = 'sleep'; seconds = 2 })
            Reached $o 'sent'
            $t.atDevice = Invoke-LabPerson -Name $VmName -Do clip-get
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'RHLABDEVICECLIP text'
            Release $o 'copied'
            Reached $o 'big'
            # 1,000,000 characters copied at the device
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock { Add-Type -AssemblyName System.Windows.Forms; [Windows.Forms.Clipboard]::SetText(('0123456789' * 100000)) }
            Release $o 'bigcopied'
            Reached $o 'image'
            $t.imageFormats = (Invoke-LabPerson -Name $VmName -Do clip-get).formats
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock {
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                $b = New-Object Drawing.Bitmap 32, 32; $g = [Drawing.Graphics]::FromImage($b); $g.Clear([Drawing.Color]::Blue); $g.Dispose()
                [Windows.Forms.Clipboard]::SetImage($b)
            }
            Release $o 'imagecopied'
            $t.r = Receive-LabDesktop $job
            $t.uni = $uni
            Save 'text' $t
        }

        It 'D-CLIP-01 text from RemoteHub is on normaluser''s clipboard' -Tag 'D-CLIP-01' {
            $t.atDevice.start | Should -BeExactly $t.uni
        }

        It 'D-CLIP-02 text copied at the device arrives, also 1,000,000 characters' -Tag 'D-CLIP-02' {
            @($t.r.clipboard | Where-Object { $_.head -eq 'RHLABDEVICECLIP text' }).Count | Should -Be 1
            @($t.r.clipboard | Where-Object length -eq 1000000).Count | Should -Be 1
        }

        It 'D-CLIP-03 text the agent set isn''t sent back' -Tag 'D-CLIP-03' {
            @($t.r.clipboard | Where-Object { $_.head -like 'Unicode*' }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-04 images both ways: the device has PNG and a bitmap; the device''s image is offered and arrives on request (BMP: the copying program put no PNG)' -Tag 'D-CLIP-04' {
            @($t.imageFormats) | Should -Contain 'PNG'
            @($t.imageFormats) | Should -Contain 'DeviceIndependentBitmap'
            @($t.r.transfers | Where-Object { $_.kind -eq 'offer' -and $_.fromDevice -and $_.transfer.kind -eq 'image' }).Count | Should -BeGreaterThan 0
            $rec = @($t.r.transfers | Where-Object { $_.kind -eq 'received' -and $_.fromDevice -and $_.result.ok })
            $rec.Count | Should -BeGreaterThan 0
            # Windows Forms' SetImage puts a bitmap only: the agent sends it as BMP (docs/test-lab-test-document.md D-CLIP-04)
            @($rec[-1].result.files)[0].path | Should -BeLike '*image.bmp'
        }
    }

    Context 'files both ways' {
        BeforeAll {
            $f = [ordered]@{}
            $o = Out 'files'
            $target = "C:\Users\$($vm.UserAccount)\Desktop\Pasted"
            $job = Begin 'files' @(
                @{ do = 'files'; paths = @("$data\file1.txt", "$data\file2.txt", "$data\file3.txt", "$data\folder"); offer = $true; waitRequest = 0 }
                @{ do = 'sleep'; seconds = 15 }, (Mark $o 'offered'), (Park $o 'pasted'), @{ do = 'sleep'; seconds = 5 }
                (Mark $o 'device'), (Park $o 'devicecopied'), @{ do = 'requestFiles'; timeout = 60 }, @{ do = 'sleep'; seconds = 2 })
            Reached $o 'offered'
            $f.beforePaste = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) @(Get-ChildItem $c -Recurse -File -ErrorAction SilentlyContinue).Count }
            # evidence: the formats normaluser's programs see for the offer (names only - nothing is fetched by listing them)
            $f.offerFormats = (Invoke-LabPerson -Name $VmName -Do clip-get).formats
            # 3 files and the folder's inner.txt
            $f.paste = Paste-AtDevice $target 4
            $f.pasted = @($f.paste.files)
            $f.clipOwner = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) @(Get-ChildItem $c -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-Acl $_.FullName).Owner } | Sort-Object -Unique) }
            Release $o 'pasted'
            Reached $o 'device'
            # the output (an error from Set-Clipboard) and the clipboard afterwards are kept: did the copy reach the clipboard?
            $f.deviceSet = Invoke-LabInSession -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\copyme'; $null = New-Item -ItemType Directory -Path $d -Force
                1..2 | ForEach-Object { Set-Content "$d\device$_.txt" "from the device $_" }
                Set-Clipboard -Path "$d\device1.txt", "$d\device2.txt"
                'set'
            }
            $f.deviceClip = Invoke-LabPerson -Name $VmName -Do clip-get
            $f.deviceHashes = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\ProgramData\RemoteHubLab\copyme' -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Release $o 'devicecopied'
            $f.r = Receive-LabDesktop $job
            Save 'files' $f
        }

        It 'D-CLIP-05 3 files and a folder pasted at the device: in normaluser''s temp "RemoteHub Clipboard", owned by normaluser, hashes equal; a receipt' -Tag 'D-CLIP-05' {
            foreach ($n in 'file1.txt', 'file2.txt', 'file3.txt', 'inner.txt') {
                ($f.pasted | Where-Object { (Split-Path $_.Path -Leaf) -eq $n }).Hash | Should -Be $hostHashes[$n] -Because "$n (normaluser's clipboard offered: $(@($f.paste.formats) -join ', '); $($f.paste.apartment) thread)"
            }
            @($f.clipOwner) | Should -Be @("$((Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }))\$($vm.UserAccount)")
            @(Ends $f.r | Where-Object { $_.toDevice -and $_.ok }).Count | Should -BeGreaterThan 0
        }

        It 'D-CLIP-07 nothing is transferred until something pastes' -Tag 'D-CLIP-07' {
            $f.beforePaste | Should -Be 0
            $first = @($f.r.transfers | Where-Object { $_.kind -eq 'sent' })[0]
            $req = @($f.r.transfers | Where-Object { $_.kind -eq 'request' -and -not $_.fromDevice })
            $first.ms | Should -BeGreaterThan (@($f.r.transfers | Where-Object kind -eq 'offer')[0].ms + 10000) -Because "the paste fetched $(@($f.pasted).Count) file(s) (normaluser's clipboard offered: $(@($f.paste.formats) -join ', '); $($f.paste.apartment) thread)"
        }

        It 'D-CLIP-06 files copied at the device: names and sizes offered, contents only on request, hashes equal' -Tag 'D-CLIP-06' {
            $offer = @($f.r.transfers | Where-Object { $_.kind -eq 'offer' -and $_.fromDevice })
            $offer.Count | Should -BeGreaterThan 0 -Because "the device's clipboard held [$(@($f.deviceClip.formats) -join ', ')] after the copy ($("$($f.deviceSet)".Trim()))"
            $rec = @($f.r.transfers | Where-Object { $_.kind -eq 'received' -and $_.fromDevice })
            @($rec | ForEach-Object { $_.result.files } | ForEach-Object sha256 | Sort-Object) | Should -Be @($f.deviceHashes)
        }
    }

    Context 'limits, names, broken transfers' {
        BeforeAll {
            $l = [ordered]@{}
            $start = { param($id, [object[]]$items) @{ do = 'rawJson'; type = 9; json = (@{ id = $id; kind = 'files'; items = $items; total = (($items | ForEach-Object size) | Measure-Object -Sum).Sum } | ConvertTo-Json -Depth 4 -Compress) } }
            $o = Out 'limits'
            # too much to the device: started as is (the harness' "files" step, like RemoteHub, refuses it before sending -
            # the agent's own check is the one under test)
            $job = Begin 'limits' @(
                (& $start 905 @(@{ path = 'big101.bin'; size = 101MB })), @{ do = 'sleep'; seconds = 2 }
                (& $start 906 @(1..1001 | ForEach-Object { @{ path = "f$_.txt"; size = 1 } })), @{ do = 'sleep'; seconds = 2 }
                (& $start 901 @(@{ path = '../../evil.txt'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 902 @(@{ path = 'C:\evil.txt'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 903 @(@{ path = 'CON'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 904 @(@{ path = 'ok.txt'; size = 10 })), @{ do = 'raw'; hex = '1E' + '88030000' + '0500000000000000' + '68656C6C6F' }, @{ do = 'sleep'; seconds = 3 }
                (Mark $o 'sent'), (Park $o 'devicebig'), @{ do = 'sleep'; seconds = 20 })
            Reached $o 'sent'
            $l.evil = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) @(Get-ChildItem 'C:\evil.txt', "C:\Users\$u" -Recurse -Filter 'evil.txt' -ErrorAction SilentlyContinue | ForEach-Object FullName) }
            # too much copied at the device
            $l.deviceSet = Invoke-LabInSession -Name $VmName -ScriptBlock { $p = 'C:\ProgramData\RemoteHubLab\big101.bin'; $f = [IO.File]::Create($p); $f.SetLength(101MB); $f.Dispose(); Set-Clipboard -Path $p; 'set' }
            $l.deviceClip = Invoke-LabPerson -Name $VmName -Do clip-get
            Release $o 'devicebig'
            $l.r = Receive-LabDesktop $job
            $l.written = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) @(Get-ChildItem $c -Recurse -File -Include 'big101.bin', 'f1.txt' -ErrorAction SilentlyContinue | ForEach-Object FullName) }
            Save 'limits' $l
        }

        It 'D-CLIP-08 101 MB or 1001 items: "Too much at once" to the device, "Too much to copy ..." from it; nothing written' -Tag 'D-CLIP-08' {
            foreach ($id in 905, 906) {
                @(Ends $l.r | Where-Object { $_.id -eq $id -and $_.toDevice -and -not $_.ok -and $_.reason -match 'Too much at once' }).Count | Should -Be 1 -Because "transfer $id"
            }
            @($l.written) | Should -BeNullOrEmpty
            @(Ends $l.r | Where-Object { -not $_.toDevice -and -not $_.ok -and $_.reason -match 'Too much to copy through the remote desktop' }).Count | Should -Be 1 -Because "the device's clipboard held [$(@($l.deviceClip.formats) -join ', ')] after the copy ($("$($l.deviceSet)".Trim()))"
        }

        It 'D-CLIP-09 "..\..\evil.txt" and "CON" are refused, "C:\x" is made relative (C\x) inside the clipboard folder; nothing outside it' -Tag 'D-CLIP-09' {
            foreach ($id in 901, 903) { @(Ends $l.r | Where-Object { $_.id -eq $id -and -not $_.ok }).Count | Should -Be 1 -Because "transfer $id" }
            @($l.evil) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-11 data out of order: "came out of order" / incomplete, nothing left' -Tag 'D-CLIP-11' {
            @(Ends $l.r | Where-Object { $_.id -eq 904 -and -not $_.ok -and $_.reason -match 'out of order|incomplete' }).Count | Should -Be 1
        }
    }

    Context 'the user''s rights, reparse points, nobody signed in' {
        BeforeAll {
            $u = [ordered]@{}
            $o = Out 'rights'
            # each copy is offered (names and sizes); the secret file's contents are asked for like the folder's: reading
            # it is what has to fail
            $job = Begin 'rights' @((Mark $o 'c1'), (Park $o 'g1'), @{ do = 'requestFiles'; timeout = 30 }, @{ do = 'sleep'; seconds = 3 }
                (Mark $o 'c2'), (Park $o 'g2'), @{ do = 'requestFiles'; timeout = 30 }, @{ do = 'sleep'; seconds = 3 })
            Reached $o 'c1'
            # a file only Administrators may read, copied in normaluser's session
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\secret'; $null = New-Item -ItemType Directory -Path $d -Force
                Set-Content "$d\secret.txt" 'admins only'
                icacls "$d\secret.txt" /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
            }
            $u.secretSet = Invoke-LabInSession -Name $VmName -ScriptBlock { Set-Clipboard -Path 'C:\ProgramData\RemoteHubLab\secret\secret.txt'; 'set' }
            Release $o 'g1'
            Reached $o 'c2'
            # a folder with a junction inside
            $u.junctionSet = Invoke-LabInSession -Name $VmName -ScriptBlock {
                $d = "$env:USERPROFILE\Documents\withjunction"; $null = New-Item -ItemType Directory -Path "$d\real" -Force
                Set-Content "$d\real\a.txt" 'real file'
                cmd.exe /c mklink /J "$d\junction" "$env:WINDIR\System32\drivers\etc" | Out-Null
                Set-Clipboard -Path $d
                'set'
            }
            Release $o 'g2'
            $u.r = Receive-LabDesktop $job
            # nobody signed in: files to the sign-in screen
            Disconnect-LabConsole -Name $VmName
            $u.signin = Invoke-LabDesktop -Name $VmName -Pin $pin -Out (Out 'signin') -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; anyOf = @('Connected', 'Failed'); timeout = 60 }
                @{ do = 'files'; paths = @("$data\file1.txt"); offer = $false; waitReceipt = 20 }, @{ do = 'sleep'; seconds = 3 }, @{ do = 'close' })
            Unlock-LabSession -Name $VmName
            Save 'rights' $u
        }

        It 'D-CLIP-12 a file only an admin may read, copied in normaluser''s session: refused - never read with the agent''s rights' -Tag 'D-CLIP-12' {
            JsonText $u.r | Should -Match "The copied files can't be read on the device"
            @($u.r.transfers | Where-Object { $_.kind -eq 'received' -and $_.result.ok -and @($_.result.files | Where-Object { "$($_.path)" -match 'secret' }).Count }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-10 a junction in a copied folder is skipped' -Tag 'D-CLIP-10' {
            $offer = @($u.r.transfers | Where-Object { $_.kind -eq 'offer' -and $_.fromDevice })[-1]
            ($offer | ConvertTo-Json -Depth 6) | Should -Match 'a\.txt'
            ($offer | ConvertTo-Json -Depth 6) | Should -Not -Match 'hosts'
        }

        It 'D-CLIP-13 files to the sign-in screen: "Nobody is signed in on the device."' -Tag 'D-CLIP-13' {
            ($u.signin | ConvertTo-Json -Depth 8) | Should -Match 'Nobody is signed in on the device'
        }
    }

    Context 'policy levels and view-only' {
        BeforeAll {
            $p = [ordered]@{}
            foreach ($level in 0, 1, 2, 3) {
                Set-LabAgentPolicy -Name $VmName -Clear -Values @{ DesktopClipboard = $level }
                $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'unchanged'
                $o = Out "level$level"
                $job = Begin "level$level" @(
                    @{ do = 'clipboardText'; text = "text at level $level" }, @{ do = 'sleep'; seconds = 2 }
                    @{ do = 'clipboardImage'; path = $png; offer = $false; waitReceipt = 10 }, @{ do = 'files'; paths = @("$data\file1.txt"); offer = $false; waitReceipt = 10 }
                    (Mark $o 'sent'), (Park $o 'go'))
                Reached $o 'sent'
                $p["clip$level"] = Invoke-LabPerson -Name $VmName -Do clip-get
                Release $o 'go'
                $p["r$level"] = Receive-LabDesktop $job
            }
            Set-LabAgentPolicy -Name $VmName -Clear
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'unchanged'
            $o = Out 'viewonly'
            $job = Begin 'viewonly' @(@{ do = 'clipboardText'; text = 'view-only text' }, @{ do = 'sleep'; seconds = 3 }, (Mark $o 'sent'), (Park $o 'go')) -ViewOnly $true
            Reached $o 'sent'
            $p.view = Invoke-LabPerson -Name $VmName -Do clip-get
            Release $o 'go'
            $p.viewR = Receive-LabDesktop $job
            Save 'levels' $p
        }

        It 'D-CLIP-14 DesktopClipboard 0 / 1 / 2 / 3: only the allowed kinds pass, refusals as in the code' -Tag 'D-CLIP-14' {
            $p.r0.hello.clipboard | Should -Be 0
            $p.clip0.start | Should -Be 'unchanged'
            $p.clip1.start | Should -Be 'text at level 1'
            JsonText $p.r1 | Should -Match "Copying images isn't allowed on this device"
            JsonText $p.r2 | Should -Match "Copying files isn't allowed on this device"
            JsonText $p.r2 | Should -Not -Match "Copying images isn't allowed"
            JsonText $p.r3 | Should -Not -Match "isn't allowed on this device"
        }

        It 'D-CLIP-15 view-only has no clipboard' -Tag 'D-CLIP-15' {
            $p.view.start | Should -Be 'unchanged'
        }
    }

    Context 'newer copy, abort, old folders' {
        BeforeAll {
            $n = [ordered]@{}
            # an old clipboard folder of normaluser's
            $old = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) $d = Join-Path $c '20200101-000000'; $null = New-Item -ItemType Directory -Path $d -Force; Set-Content "$d\old.txt" 'old'; (Get-Item $d).CreationTime = (Get-Date).AddDays(-3); $d }
            $big50 = "$data\big50.bin"; $fs = [IO.File]::Create($big50); $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $buf = New-Object byte[] (1MB); for ($k = 0; $k -lt 50; $k++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) }; $fs.Dispose()
            # D-CLIP-16 as the sheet has it: copied at the device without paste-on-demand (the data goes at once), a 90 MB
            # file, then a small one while the first is still on its way. The first copy to the device removes the old folder.
            # The person's Allow came after 21 s on W11 and not within the 30 s countdown on Server 2025 for 'abort' (run
            # 20261019-193627, OCR read nothing): a longer countdown for this context's questions
            Set-LabAgentPolicy -Name $VmName -Values @{ ConsentTimeoutSeconds = 90 }
            $o = Out 'newer'
            $job = Begin 'newer' @(
                @{ do = 'files'; paths = @("$data\file2.txt"); offer = $false; waitReceipt = 20 }
                (Mark $o 'copy'), (Park $o 'copied'), @{ do = 'sleep'; seconds = 20 }) -Features @()
            Reached $o 'copy'
            # the second copy must come while the first is being sent: the session task copies big90.bin, then waits
            # for go.txt; the host writes go.txt once the harness has started receiving big90.bin (a condition, not a sleep)
            $nd = 'C:\ProgramData\RemoteHubLab\newer'
            $null = Invoke-LabGuest -Name $VmName -ArgumentList $nd -ScriptBlock {
                param($d)
                $null = New-Item -ItemType Directory -Path $d -Force
                icacls $d /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
                Remove-Item "$d\go.txt", "$d\set.txt" -ErrorAction SilentlyContinue
                $fs = [IO.File]::Create("$d\big90.bin"); $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $buf = New-Object byte[] (1MB)
                for ($k = 0; $k -lt 90; $k++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) }; $fs.Dispose()
                Set-Content "$d\small.txt" 'the newer copy' -Encoding ASCII
            }
            # 90 MB crossed the virtual switch before the second copy was made (run 20261019-193627 on W11: big90.bin
            # arrived whole): the device's link is held to 20 Mbit/s (~36 s for big90.bin) until the copies are made
            Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 20000000
            $copier = Start-LabInSession -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\newer'
                Set-Clipboard -Path "$d\big90.bin"
                $until = (Get-Date).AddSeconds(60)
                while (-not (Test-Path "$d\go.txt")) {
                    if ((Get-Date) -gt $until) { Set-Content "$d\set.txt" 'no go.txt within 60 s - the second copy was not made' -Encoding ASCII; return }
                    Start-Sleep -Milliseconds 20
                }
                Set-Clipboard -Path "$d\small.txt"
                Set-Content "$d\set.txt" 'set' -Encoding ASCII
            }
            try {
                # TransferStart of big90.bin: the harness creates its folder in received\<id>\ and the file with the first data
                Wait-LabHostFile (Join-Path $o 'received\*\big90.bin') -TimeoutSec 60
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $nd -ScriptBlock { param($d) Set-Content "$d\go.txt" 'go' -Encoding ASCII }
                $n.deviceSet = Invoke-LabGuest -Name $VmName -ArgumentList $nd -ScriptBlock {
                    param($d)
                    $until = (Get-Date).AddSeconds(30)
                    while (-not (Test-Path "$d\set.txt") -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200 }
                    if (Test-Path "$d\set.txt") { Get-Content "$d\set.txt" -Raw } else { 'no set.txt within 30 s - the session task did not finish' }
                }
            }
            catch { $n.deviceSet = "big90.bin never started: $($_.Exception.Message)" }
            finally { Stop-LabInSession -Name $VmName -Task $copier; Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 0 }
            Release $o 'copied'
            $n.newer = Receive-LabDesktop $job
            $n.oldGone = -not (Invoke-LabGuest -Name $VmName -ArgumentList $old -ScriptBlock { param($d) Test-Path $d })
            # the connection cut while a copy to the device is being pasted
            $o = Out 'abort'
            $job = Begin 'abort' @(@{ do = 'files'; paths = @($big50); offer = $true; waitRequest = 120 }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'drop' })
            Start-Sleep -Seconds 12
            # pasted with messages pumped for the whole 20 s (the shell fetches while the connection is cut)
            $n.abortPaste = Invoke-LabPaste -Name $VmName -Account User -Folder 'Desktop\Aborted' -TimeoutSec 20 -Hold
            $n.abort = Receive-LabDesktop $job
            Start-Sleep -Seconds 10
            $n.partial = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder), $vm.UserAccount -ScriptBlock {
                param($c, $u) @(Get-ChildItem $c, "C:\Users\$u\Desktop\Aborted" -Recurse -File -ErrorAction SilentlyContinue | Where-Object Length -lt 50MB | Where-Object Name -like 'big50*' | ForEach-Object FullName)
            }
            Save 'newer' $n
        }

        AfterAll {
            Set-VMNetworkAdapter -VMName $vm.Name -MaximumBandwidth 0
            Set-LabAgentPolicy -Name $VmName -Clear
        }

        It 'D-CLIP-16 a second copy at the device while the first transfers cancels it: "A newer copy replaced it."; the second arrives' -Tag 'D-CLIP-16' {
            # (one copy may start twice: a program's copy changes the clipboard twice - see the findings table)
            $big = @($n.newer.transfers | Where-Object { $_.kind -eq 'start' -and $_.fromDevice -and @($_.transfer.items | Where-Object path -eq 'big90.bin').Count } | ForEach-Object { [int]$_.transfer.id })
            $big.Count | Should -BeGreaterThan 0 -Because "the copies at the device: $("$($n.deviceSet)".Trim())"
            @(Ends $n.newer | Where-Object { $big -contains [int]$_.id -and -not $_.toDevice -and -not $_.ok -and $_.reason -match 'A newer copy replaced it' }).Count | Should -BeGreaterThan 0
            $received = @($n.newer.transfers | Where-Object { $_.kind -eq 'received' -and $_.result.ok })
            @($received | Where-Object { $big -contains [int]$_.id }) | Should -BeNullOrEmpty
            @($received | Where-Object { @($_.result.files | Where-Object { "$($_.path)" -like '*small.txt' }).Count }).Count | Should -BeGreaterThan 0
        }

        It 'D-CLIP-17 the connection cut during a file copy: no partial files on the device' -Tag 'D-CLIP-17' {
            @($n.partial) | Should -BeNullOrEmpty -Because "normaluser's clipboard offered: $(@($n.abortPaste.formats) -join ', '); $($n.abortPaste.apartment) thread"
        }

        It 'D-CLIP-18 clipboard folders older than today are removed at the next transfer' -Tag 'D-CLIP-18' {
            $n.oldGone | Should -BeTrue
        }
    }
}
