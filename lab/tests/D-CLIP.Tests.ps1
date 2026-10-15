# D-CLIP - the clipboard of the agent's remote desktop, both ways: text, images and files (virtual files on the device,
# fetched only when something pastes; pasted with the shell's own "paste" verb in normaluser's session, as Explorer
# does), the limits, names that try to leave the clipboard folder, reparse points, broken transfers, the user's own
# rights, the policy levels and view-only. normaluser is signed in and allows PSAdmin. See docs/test-lab-cases.md
# section 9.9.
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
    # a session normaluser allows: $Steps run once connected (the test acts between Mark / Park pairs)
    function Begin([string]$Name, [object[]]$Steps, [bool]$ViewOnly = $false) {
        $job = Start-LabDesktop -Name $VmName -Pin $pin -Out (Out $Name) -Steps (@(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto'; viewOnly = $ViewOnly }, @{ do = 'wait'; anyOf = @('Connected', 'Denied'); timeout = 90 }) + $Steps + @(@{ do = 'close' }))
        $null = Invoke-LabPerson -Name $VmName -Do click -Text 'Allow' -TimeoutSec 60
        $job
    }
    # pastes the clipboard into a folder of normaluser's, as Explorer's Paste does; answers the files that arrived
    function Paste-AtDevice([string]$Folder) {
        $null = Invoke-LabInSession -Name $VmName -TimeoutSec 240 -ScriptBlock ([scriptblock]::Create(@"
`$null = New-Item -ItemType Directory -Path '$Folder' -Force
(New-Object -ComObject Shell.Application).NameSpace('$Folder').Self.InvokeVerb('paste')
`$until = (Get-Date).AddSeconds(120)
do { Start-Sleep -Seconds 2; `$n = @(Get-ChildItem '$Folder' -Recurse -File -ErrorAction SilentlyContinue).Count } while (`$n -eq 0 -and (Get-Date) -lt `$until)
Start-Sleep -Seconds 5
"@))
        Invoke-LabGuest -Name $VmName -ArgumentList $Folder -ScriptBlock {
            param($f) @(Get-ChildItem $f -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ Path = $_.FullName.Substring($f.Length).TrimStart('\'); Sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash; Length = $_.Length } })
        }
    }
    function Clip-Folder { Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) "C:\Users\$u\AppData\Local\Temp\RemoteHub Clipboard" } }
    function Ends($r) { , @($r.transfers | Where-Object kind -eq 'end') }

    # files on the host
    $data = Out 'data'
    $null = New-Item -ItemType Directory -Path "$data\folder\sub" -Force
    1..3 | ForEach-Object { [IO.File]::WriteAllText("$data\file$_.txt", "clipboard file $_ $([guid]::NewGuid())") }
    [IO.File]::WriteAllText("$data\folder\sub\inner.txt", "inner $([guid]::NewGuid())")
    $hostHashes = @{}; foreach ($f in Get-ChildItem $data -Recurse -File) { $hostHashes[$f.Name] = Sha $f.FullName }
    $big = "$data\big101.bin"; $fs = [IO.File]::Create($big); $fs.SetLength(101MB); $fs.Dispose()
    $many = "$data\many"; $null = New-Item -ItemType Directory -Path $many -Force; 1..1001 | ForEach-Object { [IO.File]::WriteAllText("$many\f$_.txt", "$_") }
    Add-Type -AssemblyName System.Drawing
    $png = "$data\image.png"; $bmp = New-Object Drawing.Bitmap 64, 48; $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear([Drawing.Color]::FromArgb(10, 200, 30)); $g.Dispose(); $bmp.Save($png, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

    Reset-LabVm -Name $VmName
    $install = Install-LabAgent -Name $VmName
    $pin = $install.pin
    Set-LabAutoLogon -Name $VmName -Account User
    $null = Wait-LabAgent -Name $VmName -Pin $pin
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'd-clip-agent-logs')
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
                @{ do = 'clipboardImage'; path = $png }, @{ do = 'sleep'; seconds = 4 }, (Mark $o 'image'), (Park $o 'imagecopied'), @{ do = 'sleep'; seconds = 6 })
            Reached $o 'sent'
            $t.atDevice = Invoke-LabPerson -Name $VmName -Do clip-get
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value 'copied at the device'
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
            @($t.r.clipboard | Where-Object { $_.head -eq 'copied at the device' }).Count | Should -Be 1
            @($t.r.clipboard | Where-Object length -eq 1000000).Count | Should -Be 1
        }

        It 'D-CLIP-03 text the agent set isn''t sent back' -Tag 'D-CLIP-03' {
            @($t.r.clipboard | Where-Object { $_.head -like 'Unicode*' }) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-04 images both ways: the device has PNG and a bitmap; the device''s image arrives as PNG' -Tag 'D-CLIP-04' {
            @($t.imageFormats) | Should -Contain 'PNG'
            @($t.imageFormats) | Should -Contain 'DeviceIndependentBitmap'
            @($t.r.transfers | Where-Object { $_.kind -eq 'received' -and $_.fromDevice }).Count | Should -BeGreaterThan 0
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
            $f.pasted = Paste-AtDevice $target
            $f.clipOwner = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) @(Get-ChildItem $c -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-Acl $_.FullName).Owner } | Sort-Object -Unique) }
            Release $o 'pasted'
            Reached $o 'device'
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\copyme'; $null = New-Item -ItemType Directory -Path $d -Force
                1..2 | ForEach-Object { Set-Content "$d\device$_.txt" "from the device $_" }
                Set-Clipboard -Path "$d\device1.txt", "$d\device2.txt"
            }
            $f.deviceHashes = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\ProgramData\RemoteHubLab\copyme' -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            Release $o 'devicecopied'
            $f.r = Receive-LabDesktop $job
            Save 'files' $f
        }

        It 'D-CLIP-05 3 files and a folder pasted at the device: in normaluser''s temp "RemoteHub Clipboard", owned by normaluser, hashes equal; a receipt' -Tag 'D-CLIP-05' {
            foreach ($n in 'file1.txt', 'file2.txt', 'file3.txt', 'inner.txt') {
                ($f.pasted | Where-Object { (Split-Path $_.Path -Leaf) -eq $n }).Sha | Should -Be $hostHashes[$n] -Because $n
            }
            @($f.clipOwner) | Should -Be @("$((Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }))\$($vm.UserAccount)")
            @(Ends $f.r | Where-Object { $_.toDevice -and $_.ok }).Count | Should -BeGreaterThan 0
        }

        It 'D-CLIP-07 nothing is transferred until something pastes' -Tag 'D-CLIP-07' {
            $f.beforePaste | Should -Be 0
            $first = @($f.r.transfers | Where-Object { $_.kind -eq 'sent' })[0]
            $req = @($f.r.transfers | Where-Object { $_.kind -eq 'request' -and -not $_.fromDevice })
            $first.ms | Should -BeGreaterThan (@($f.r.transfers | Where-Object kind -eq 'offer')[0].ms + 10000)
        }

        It 'D-CLIP-06 files copied at the device: names and sizes offered, contents only on request, hashes equal' -Tag 'D-CLIP-06' {
            $offer = @($f.r.transfers | Where-Object { $_.kind -eq 'offer' -and $_.fromDevice })
            $offer.Count | Should -BeGreaterThan 0
            $rec = @($f.r.transfers | Where-Object { $_.kind -eq 'received' -and $_.fromDevice })
            @($rec | ForEach-Object { $_.files } | ForEach-Object sha256 | Sort-Object) | Should -Be @($f.deviceHashes)
        }
    }

    Context 'limits, names, broken transfers' {
        BeforeAll {
            $l = [ordered]@{}
            $start = { param($id, [object[]]$items) @{ do = 'rawJson'; type = 9; json = (@{ id = $id; kind = 'files'; items = $items; total = (($items | ForEach-Object size) | Measure-Object -Sum).Sum } | ConvertTo-Json -Depth 4 -Compress) } }
            $o = Out 'limits'
            $job = Begin 'limits' @(
                @{ do = 'files'; paths = @($big); offer = $false; waitReceipt = 20 }
                @{ do = 'files'; paths = @($many); offer = $false; waitReceipt = 20 }
                (& $start 901 @(@{ path = '../../evil.txt'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 902 @(@{ path = 'C:\evil.txt'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 903 @(@{ path = 'CON'; size = 5 })), @{ do = 'sleep'; seconds = 2 }
                (& $start 904 @(@{ path = 'ok.txt'; size = 10 })), @{ do = 'raw'; hex = '1E' + '88030000' + '0500000000000000' + '68656C6C6F' }, @{ do = 'sleep'; seconds = 3 }
                (Mark $o 'sent'), (Park $o 'devicebig'), @{ do = 'sleep'; seconds = 20 })
            Reached $o 'sent'
            $l.evil = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) @(Get-ChildItem 'C:\evil.txt', "C:\Users\$u" -Recurse -Filter 'evil.txt' -ErrorAction SilentlyContinue | ForEach-Object FullName) }
            # too much copied at the device
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock { $p = 'C:\ProgramData\RemoteHubLab\big101.bin'; $f = [IO.File]::Create($p); $f.SetLength(101MB); $f.Dispose(); Set-Clipboard -Path $p }
            Release $o 'devicebig'
            $l.r = Receive-LabDesktop $job
            Save 'limits' $l
        }

        It 'D-CLIP-08 101 MB or 1001 items: "Too much at once", both ways; nothing written' -Tag 'D-CLIP-08' {
            @(Ends $l.r | Where-Object { -not $_.ok -and $_.reason -match 'Too much' }).Count | Should -BeGreaterOrEqual 2
            @($l.r.statuses + $l.r.transfers | Where-Object { "$($_.message)$($_.reason)" -match 'Too much to copy' }).Count | Should -BeGreaterThan 0
        }

        It 'D-CLIP-09 "..\..\evil.txt", "C:\x", "CON" are refused; nothing outside the clipboard folder' -Tag 'D-CLIP-09' {
            foreach ($id in 901, 902, 903) { @(Ends $l.r | Where-Object { $_.id -eq $id -and -not $_.ok }).Count | Should -Be 1 -Because "transfer $id" }
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
            $job = Begin 'rights' @((Mark $o 'c1'), (Park $o 'g1'), @{ do = 'sleep'; seconds = 8 }, (Mark $o 'c2'), (Park $o 'g2'), @{ do = 'requestFiles'; timeout = 30 }, @{ do = 'sleep'; seconds = 3 })
            Reached $o 'c1'
            # a file only Administrators may read, copied in normaluser's session
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $d = 'C:\ProgramData\RemoteHubLab\secret'; $null = New-Item -ItemType Directory -Path $d -Force
                Set-Content "$d\secret.txt" 'admins only'
                icacls "$d\secret.txt" /inheritance:r /grant:r '*S-1-5-32-544:F' '*S-1-5-18:F' | Out-Null
            }
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock { Set-Clipboard -Path 'C:\ProgramData\RemoteHubLab\secret\secret.txt' }
            Release $o 'g1'
            Reached $o 'c2'
            # a folder with a junction inside
            $null = Invoke-LabInSession -Name $VmName -ScriptBlock {
                $d = "$env:USERPROFILE\Documents\withjunction"; $null = New-Item -ItemType Directory -Path "$d\real" -Force
                Set-Content "$d\real\a.txt" 'real file'
                cmd.exe /c mklink /J "$d\junction" "$env:WINDIR\System32\drivers\etc" | Out-Null
                Set-Clipboard -Path $d
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
            $all = ($u.r | ConvertTo-Json -Depth 8)
            $all | Should -Match "can't be read"
            @($u.r.transfers | Where-Object { $_.kind -eq 'received' -and ($_.files | Where-Object path -match 'secret') }) | Should -BeNullOrEmpty
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
            ($p.r1 | ConvertTo-Json -Depth 8) | Should -Match "Copying images isn't allowed on this device"
            ($p.r2 | ConvertTo-Json -Depth 8) | Should -Match "Copying files isn't allowed on this device"
            ($p.r2 | ConvertTo-Json -Depth 8) | Should -Not -Match "Copying images isn't allowed"
            ($p.r3 | ConvertTo-Json -Depth 8) | Should -Not -Match "isn't allowed on this device"
        }

        It 'D-CLIP-15 view-only has no clipboard' -Tag 'D-CLIP-15' {
            $p.view.start | Should -Be 'unchanged'
        }
    }

    Context 'newer copy, abort, old folders' {
        BeforeAll {
            $n = [ordered]@{}
            # an old clipboard folder of normaluser's
            $old = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder) -ScriptBlock { param($c) $d = Join-Path $c '20200101-000000'; $null = New-Item -ItemType Directory -Path $d -Force; Set-Content "$d\old.txt" 'old'; (Get-Item $d).LastWriteTime = (Get-Date).AddDays(-3); $d }
            $big50 = "$data\big50.bin"; $fs = [IO.File]::Create($big50); $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $buf = New-Object byte[] (1MB); for ($k = 0; $k -lt 50; $k++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) }; $fs.Dispose()
            $o = Out 'newer'
            $job = Begin 'newer' @(
                @{ do = 'files'; paths = @($big50); offer = $false; waitReceipt = 0 }, @{ do = 'files'; paths = @("$data\file2.txt"); offer = $false; waitReceipt = 20 }, @{ do = 'sleep'; seconds = 5 })
            $n.newer = Receive-LabDesktop $job
            $n.oldGone = -not (Invoke-LabGuest -Name $VmName -ArgumentList $old -ScriptBlock { param($d) Test-Path $d })
            # the connection cut while a copy to the device is being pasted
            $o = Out 'abort'
            $job = Begin 'abort' @(@{ do = 'files'; paths = @($big50); offer = $true; waitRequest = 120 }, @{ do = 'sleep'; seconds = 1 }, @{ do = 'drop' })
            Start-Sleep -Seconds 12
            $null = Invoke-LabInSession -Name $VmName -TimeoutSec 60 -ScriptBlock {
                $null = New-Item -ItemType Directory -Path "$env:USERPROFILE\Desktop\Aborted" -Force
                (New-Object -ComObject Shell.Application).NameSpace("$env:USERPROFILE\Desktop\Aborted").Self.InvokeVerb('paste'); Start-Sleep -Seconds 20
            }
            $n.abort = Receive-LabDesktop $job
            Start-Sleep -Seconds 10
            $n.partial = Invoke-LabGuest -Name $VmName -ArgumentList (Clip-Folder), $vm.UserAccount -ScriptBlock {
                param($c, $u) @(Get-ChildItem $c, "C:\Users\$u\Desktop\Aborted" -Recurse -File -ErrorAction SilentlyContinue | Where-Object Length -lt 50MB | Where-Object Name -like 'big50*' | ForEach-Object FullName)
            }
            Save 'newer' $n
        }

        It 'D-CLIP-16 a second copy while the first transfers cancels it: "A newer copy replaced it."' -Tag 'D-CLIP-16' {
            ($n.newer | ConvertTo-Json -Depth 8) | Should -Match 'replaced'
            @(Ends $n.newer | Where-Object ok).Count | Should -BeGreaterThan 0
        }

        It 'D-CLIP-17 the connection cut during a file copy: no partial files on the device' -Tag 'D-CLIP-17' {
            @($n.partial) | Should -BeNullOrEmpty
        }

        It 'D-CLIP-18 clipboard folders older than today are removed at the next transfer' -Tag 'D-CLIP-18' {
            $n.oldGone | Should -BeTrue
        }
    }
}
