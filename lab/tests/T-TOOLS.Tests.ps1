# T - RemoteHub's remote tools (file transfer, registry, services, processes) through the same code the tabs use
# (RemoteFs, FileUploader/FileDownloader, RemoteRegistry, RemoteServices, RemoteProcesses), once over the RemoteHub
# Agent and once over WinRM HTTPS set up with RemoteHub's script. Results are checked on the device over PowerShell
# Direct. Each It name carries the transport in brackets. See docs/test-lab-cases.md section 7.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "t-$name.json") -Encoding UTF8 }
    function Get-Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
    function Get-GuestFiles([string]$Root) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Root -ScriptBlock {
            param($Root)
            if (-not (Test-Path -LiteralPath $Root)) { return @() }
            @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
                    [pscustomobject]@{ Path = $_.FullName.Substring($Root.Length).TrimStart('\'); Sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash; Length = $_.Length }
                })
        }
    }

    # test data on the host, made once: 300 small files with distinct content, 200 MB compressible, 200 MB random
    $data = Join-Path $env:TEMP "RemoteHubLab-T-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $small = Join-Path $data 'small'
    $null = New-Item -ItemType Directory -Path $small -Force
    1..300 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $small ("file{0:000}.txt" -f $_)), "RemoteHub lab file $_ $([guid]::NewGuid())") }
    $big = Join-Path $data 'big'
    $null = New-Item -ItemType Directory -Path $big -Force
    $text = Join-Path $big 'compressible.txt'
    $line = [Text.Encoding]::ASCII.GetBytes(('RemoteHub lab compressible line ' * 4) + "`r`n")
    $fs = [IO.File]::Create($text); try { while ($fs.Length -lt 200MB) { $fs.Write($line, 0, $line.Length) } } finally { $fs.Dispose() }
    $random = Join-Path $big 'random.bin'
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create(); $buf = New-Object byte[] (1MB)
    $fs = [IO.File]::Create($random); try { for ($i = 0; $i -lt 200; $i++) { $rng.GetBytes($buf); $fs.Write($buf, 0, $buf.Length) } } finally { $fs.Dispose() }
    $smallHashes = @(Get-ChildItem $small -File | ForEach-Object { Get-Sha $_.FullName } | Sort-Object)
    $bigHashes = @{ 'compressible.txt' = (Get-Sha $text); 'random.bin' = (Get-Sha $random) }
}

AfterAll {
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    # files, registry values, services and processes changed on the device, WinRM HTTPS set up: the next file starts
    # from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'T <VmName>' -Tag Tools, 'T' {

    Context 'over <Via>' -ForEach @(@{ Via = 'Agent' }, @{ Via = 'WinRM' }) {
        BeforeAll {
            $pin = $null
            if ($Via -eq 'Agent') {
                $pin = (Initialize-LabVm -Name $VmName -Agent).Pin
            }
            else {
                Reset-LabVm -Name $VmName
                # RemoteHub's setup script with local admins allowed (PSAdmin is not the built-in Administrator). The
                # session is pinned to the listener's certificate read over PowerShell Direct instead of trusting it on
                # this PC: the lab account can't answer the Windows prompt (the prompt itself is W-TRUST-01, 2VM).
                $setup = Invoke-LabSetupScript -Name $VmName
                $cert = (Get-LabWinRmState -Name $VmName).Certificate.Thumbprint
                Save 'winrm-setup' @{ Setup = $setup; Certificate = $cert }
            }
            function T([string]$Op, [hashtable]$P = @{}) {
                $P = $P.Clone()
                if ($Via -eq 'WinRM') { $P.cert = $cert }
                Invoke-LabTool -Name $VmName -Op $Op -Via $Via -Pin $pin -Params $P
            }
            $tag = $Via.ToLowerInvariant()
            $remote = "C:\LabUp-$tag"
        }

        It 'T-FS-01 [<Via>] lists drives and folders, flags hidden ones, expands %TEMP% on the device' -Tag 'T-FS-01' {
            $drives = T fs-list   # no path: the drive list
            @($drives.result.entries | ForEach-Object FullPath) | Should -Contain 'C:\'
            $root = T fs-list @{ path = 'C:\' }
            ($root.result.entries | Where-Object Name -eq 'ProgramData').hidden | Should -Be $true
            $temp = T fs-list @{ path = '%TEMP%' }
            $temp.result.path | Should -Match "^C:\\Users\\$([regex]::Escape($vm.AdminAccount))[^\\]*\\AppData\\Local\\Temp"
        }

        It 'T-FS-02 [<Via>] uploads 300 small files, all equal, no .remotehub-part left' -Tag 'T-FS-02' {
            $up = T fs-upload @{ files = $small; remote = $remote }
            Save "$tag-upload-small" $up
            $up.result.failed | Should -Be 0
            $up.result.done | Should -Be 300
            $g = @(Get-GuestFiles $remote)
            @($g | Where-Object { $_.Path -like '*.remotehub-part' }) | Should -BeNullOrEmpty
            @($g | ForEach-Object Sha | Sort-Object) | Should -Be $smallHashes
        }

        It 'T-FS-03 [<Via>] uploads 200 MB compressible and 200 MB random, hashes equal' -Tag 'T-FS-03' {
            $up = T fs-upload @{ files = @($text, $random); remote = "$remote\big" }
            Save "$tag-upload-big" $up
            $up.result.done | Should -Be 2
            $g = @(Get-GuestFiles "$remote\big")
            ($g | Where-Object Path -eq 'compressible.txt').Sha | Should -Be $bigHashes['compressible.txt']
            ($g | Where-Object Path -eq 'random.bin').Sha | Should -Be $bigHashes['random.bin']
        }

        It 'T-FS-05 [<Via>] skips existing files, replaces them with overwrite' -Tag 'T-FS-05' {
            $skip = T fs-upload @{ files = $small; remote = $remote }
            $skip.result.skipped | Should -Be 300
            $skip.result.done | Should -Be 0
            $victim = Invoke-LabGuest -Name $VmName -ArgumentList $remote -ScriptBlock {
                param($r) $f = Get-ChildItem -LiteralPath $r -Recurse -File -Filter 'file001.txt' | Select-Object -First 1
                Set-Content -Encoding ASCII -LiteralPath $f.FullName -Value 'changed on the device'; $f.FullName
            }
            $over = T fs-upload @{ files = $small; remote = $remote; overwrite = 1 }
            $over.result.done | Should -Be 300
            $now = Invoke-LabGuest -Name $VmName -ArgumentList $victim -ScriptBlock { param($p) (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash }
            $now | Should -Be (Get-Sha (Join-Path $small 'file001.txt'))
        }

        It 'T-FS-06 [<Via>] a cancelled upload leaves no partial file' -Tag 'T-FS-06' {
            $up = T fs-upload @{ files = $random; remote = "$remote\cancel"; 'cancel-after-mb' = 20 }
            Save "$tag-upload-cancel" $up
            $up.result.cancelled | Should -Be 1
            @(Get-GuestFiles "$remote\cancel") | Should -BeNullOrEmpty
        }

        It 'T-FS-07 [<Via>] downloads a file and a folder, hashes equal' -Tag 'T-FS-07' {
            $local = Join-Path $data "down-$tag"
            # the 200 MB file is put on the device over PowerShell Direct: the download must not depend on T-FS-03
            $null = Invoke-LabGuest -Name $VmName -ArgumentList "$remote\seed" -ScriptBlock { param($p) New-Item -ItemType Directory -Path $p -Force }
            Copy-LabFile -Name $VmName -Path $random -Destination "$remote\seed"
            $down = T fs-download @{ remote = @("$remote\seed\random.bin", "$remote\small"); local = $local }
            Save "$tag-download" $down
            @($down.result.items | Where-Object state -ne 'Done') | Should -BeNullOrEmpty
            ($down.result.items | Where-Object RelativePath -eq 'random.bin').sha256 | Should -Be $bigHashes['random.bin']
            @(Get-ChildItem (Join-Path $local 'small') -File | ForEach-Object { Get-Sha $_.FullName } | Sort-Object) | Should -Be $smallHashes
        }

        It 'T-FS-08 [<Via>] new folder, rename, delete; deleting a drive root is refused' -Tag 'T-FS-08' {
            (T fs-mkdir @{ path = "$remote\made" }).refused | Should -BeNullOrEmpty
            (T fs-rename @{ path = "$remote\made"; name = 'renamed' }).refused | Should -BeNullOrEmpty
            Invoke-LabGuest -Name $VmName -ArgumentList $remote -ScriptBlock { param($r) Test-Path "$r\renamed" } | Should -Be $true
            (T fs-delete @{ paths = "$remote\renamed" }).refused | Should -BeNullOrEmpty
            Invoke-LabGuest -Name $VmName -ArgumentList $remote -ScriptBlock { param($r) Test-Path "$r\renamed" } | Should -Be $false
            (T fs-delete @{ paths = 'C:\' }).refused | Should -Not -BeNullOrEmpty
            Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path C:\Windows } | Should -Be $true
        }

        It 'T-REG-01 [<Via>] browses HKLM\SOFTWARE in every path spelling' -Tag 'T-REG-01' {
            foreach ($p in 'HKLM\SOFTWARE', 'HKLM:\SOFTWARE', 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE', 'Computer\HKEY_LOCAL_MACHINE\SOFTWARE') {
                $l = T reg-list @{ path = $p }
                $l.refused | Should -BeNullOrEmpty -Because $p
                @($l.result.keys) | Should -Contain 'Microsoft' -Because $p
            }
        }

        It 'T-REG-02 [<Via>] creates, reads, changes and deletes every value type' -Tag 'T-REG-02' {
            $key = "HKLM\SOFTWARE\RemoteHubLab-$tag"
            (T reg-create-key @{ parent = 'HKLM\SOFTWARE'; name = "RemoteHubLab-$tag" }).refused | Should -BeNullOrEmpty
            $values = [ordered]@{ S = @('String', 'lab text'); E = @('ExpandString', '%SystemRoot%\lab'); M = @('MultiString', 'one\ntwo'); D = @('DWord', '42'); Q = @('QWord', '5000000000'); B = @('Binary', '0102A0FF') }
            foreach ($n in $values.Keys) { (T reg-set @{ key = $key; name = $n; kind = $values[$n][0]; data = $values[$n][1] }).refused | Should -BeNullOrEmpty -Because $n }
            $g = Invoke-LabGuest -Name $VmName -ArgumentList "HKLM:\SOFTWARE\RemoteHubLab-$tag" -ScriptBlock {
                param($k) $i = Get-Item $k
                foreach ($n in 'S', 'E', 'M', 'D', 'Q', 'B') {
                    $v = $i.GetValue($n, $null, 'DoNotExpandEnvironmentNames')
                    [pscustomobject]@{ Name = $n; Kind = [string]$i.GetValueKind($n); Data = $(if ($v -is [byte[]]) { ($v | ForEach-Object { $_.ToString('X2') }) -join '' } elseif ($v -is [array]) { $v -join '|' } else { [string]$v }) }
                }
            }
            ($g | Where-Object Name -eq 'S').Data | Should -Be 'lab text'
            ($g | Where-Object Name -eq 'E').Kind | Should -Be 'ExpandString'
            ($g | Where-Object Name -eq 'E').Data | Should -Be '%SystemRoot%\lab'
            ($g | Where-Object Name -eq 'M').Data | Should -Be 'one|two'
            ($g | Where-Object Name -eq 'D').Data | Should -Be '42'
            ($g | Where-Object Name -eq 'Q').Data | Should -Be '5000000000'
            ($g | Where-Object Name -eq 'B').Data | Should -Be '0102A0FF'
            $l = T reg-list @{ path = $key }
            ($l.result.values | Where-Object Name -eq 'E').data | Should -Be '%SystemRoot%\lab'
            (T reg-set @{ key = $key; name = 'D'; kind = 'DWord'; data = '43' }).refused | Should -BeNullOrEmpty
            (T reg-rename-value @{ key = $key; name = 'S'; new = 'S2' }).refused | Should -BeNullOrEmpty
            (T reg-delete-value @{ key = $key; name = 'B' }).refused | Should -BeNullOrEmpty
            $after = Invoke-LabGuest -Name $VmName -ArgumentList "HKLM:\SOFTWARE\RemoteHubLab-$tag" -ScriptBlock { param($k) $i = Get-Item $k; [pscustomobject]@{ Names = @($i.GetValueNames() | Sort-Object); D = $i.GetValue('D') } }
            $after.D | Should -Be 43
            @($after.Names) | Should -Be @('D', 'E', 'M', 'Q', 'S2')
            (T reg-delete-key @{ key = $key }).refused | Should -BeNullOrEmpty
            Invoke-LabGuest -Name $VmName -ArgumentList "HKLM:\SOFTWARE\RemoteHubLab-$tag" -ScriptBlock { param($k) Test-Path $k } | Should -Be $false
        }

        It 'T-REG-04 [<Via>] export gives the same .reg content as reg export' -Tag 'T-REG-04' {
            $out = Join-Path $data "export-$tag.reg"
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { New-Item HKLM:\SOFTWARE\RemoteHubLabExport -Force | Out-Null; Set-ItemProperty HKLM:\SOFTWARE\RemoteHubLabExport -Name V -Value 'exported' }
            $e = T reg-export @{ key = 'HKLM\SOFTWARE\RemoteHubLabExport'; out = $out }
            $e.result.bytes | Should -BeGreaterThan 0
            $ref = Invoke-LabGuest -Name $VmName -ScriptBlock { $f = Join-Path $env:TEMP 'ref.reg'; reg.exe export 'HKLM\SOFTWARE\RemoteHubLabExport' $f /y | Out-Null; Get-Content $f }
            $norm = { param($lines) @($lines | Where-Object { $_.Trim() }) -join "`n" }
            (& $norm (Get-Content $out)) | Should -Be (& $norm $ref)
        }

        It 'T-REG-05 [<Via>] refuses to delete or rename HKLM\SOFTWARE' -Tag 'T-REG-05' {
            (T reg-delete-key @{ key = 'HKLM\SOFTWARE' }).refused | Should -Not -BeNullOrEmpty
            (T reg-rename-key @{ key = 'HKLM\SOFTWARE'; new = 'SOFTWARE2' }).refused | Should -Not -BeNullOrEmpty
            Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path HKLM:\SOFTWARE\Microsoft } | Should -Be $true
        }

        It 'T-SVC-01 [<Via>] service list matches Win32_Service' -Tag 'T-SVC-01' {
            $l = T svc-list
            $ref = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-CimInstance Win32_Service | ForEach-Object { [pscustomobject]@{ Name = $_.Name; State = $_.State; Account = $_.StartName; ProcessId = [int]$_.ProcessId } }) }
            @($l.result.services).Count | Should -Be @($ref).Count
            foreach ($n in 'Spooler', 'WinRM', 'EventLog') {
                $a = $l.result.services | Where-Object Name -eq $n
                $b = $ref | Where-Object Name -eq $n
                $a.State | Should -Be $b.State -Because $n
                $a.Account | Should -Be $b.Account -Because $n
                $a.ProcessId | Should -Be $b.ProcessId -Because $n
            }
        }

        It 'T-SVC-02 [<Via>] stops, starts and restarts Spooler' -Tag 'T-SVC-02' {
            $state = { Invoke-LabGuest -Name $VmName -ScriptBlock { $s = Get-CimInstance Win32_Service -Filter "Name='Spooler'"; [pscustomobject]@{ State = $s.State; ProcessId = $s.ProcessId } } }
            (T svc-control @{ name = 'Spooler'; action = 'Stop' }).refused | Should -BeNullOrEmpty
            (& $state).State | Should -Be 'Stopped'
            (T svc-control @{ name = 'Spooler'; action = 'Start' }).refused | Should -BeNullOrEmpty
            $s1 = & $state
            $s1.State | Should -Be 'Running'
            (T svc-control @{ name = 'Spooler'; action = 'Restart' }).refused | Should -BeNullOrEmpty
            $s2 = & $state
            $s2.State | Should -Be 'Running'
            $s2.ProcessId | Should -Not -Be $s1.ProcessId
        }

        It 'T-SVC-03 [<Via>] sets every startup type' -Tag 'T-SVC-03' {
            # BITS, not Spooler: Windows refuses delayed start for services in a load-order group (sc.exe: error 87)
            $expect = [ordered]@{ Disabled = @('Disabled', $null); Manual = @('Manual', $null); AutomaticDelayed = @('Auto', 1); Automatic = @('Auto', 0) }
            foreach ($t in $expect.Keys) {
                (T svc-startup @{ name = 'BITS'; startup = $t }).refused | Should -BeNullOrEmpty -Because $t
                $g = Invoke-LabGuest -Name $VmName -ScriptBlock {
                    [pscustomobject]@{ Mode = (Get-CimInstance Win32_Service -Filter "Name='BITS'").StartMode; Delayed = (Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\BITS).DelayedAutostart }
                }
                $g.Mode | Should -Be $expect[$t][0] -Because $t
                if ($null -ne $expect[$t][1]) { [int]$g.Delayed | Should -Be $expect[$t][1] -Because $t }
            }
        }

        It 'T-PROC-01 [<Via>] lists processes with the tab''s own process and user names' -Tag 'T-PROC-01' {
            $l = T proc-list
            @($l.result.processes | ForEach-Object Id) | Should -Contain $l.result.ownPid
            # an admin sees the owners of other users' processes (the console user's or normaluser's explorer)
            $owners = @($l.result.processes | Where-Object Name -match '^explorer' | ForEach-Object User)
            $owners | Should -Not -BeNullOrEmpty
            ($owners -join ',') | Should -Match "\\($([regex]::Escape($vm.ConsoleAccount))|$([regex]::Escape($vm.UserAccount)))(,|$)"
        }

        It 'T-PROC-02 [<Via>] ends a process tree completely' -Tag 'T-PROC-02' {
            $tree = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $p = Start-Process cmd.exe -ArgumentList '/c ping -n 600 127.0.0.1 > nul' -PassThru -WindowStyle Hidden
                $until = (Get-Date).AddSeconds(15)
                do {
                    $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id)" | Where-Object Name -eq 'PING.EXE' | Select-Object -First 1
                    if ($child) { break }
                    Start-Sleep -Milliseconds 250
                } while ((Get-Date) -lt $until)
                [pscustomobject]@{ Parent = $p.Id; Child = [int]$child.ProcessId }
            }
            $tree.Child | Should -BeGreaterThan 0
            (T proc-stop @{ ids = $tree.Parent; names = 'cmd.exe'; tree = 1 }).refused | Should -BeNullOrEmpty
            $null = Wait-LabCondition -NoThrow -TimeoutSec 15 -Because 'the ended process tree is still running' -Until {
                (Invoke-LabGuest -Name $VmName -ArgumentList $tree.Parent, $tree.Child -ScriptBlock { param($a, $b) @(Get-Process -Id $a, $b -ErrorAction SilentlyContinue).Count }) -eq 0 }
            Invoke-LabGuest -Name $VmName -ArgumentList $tree.Parent, $tree.Child -ScriptBlock { param($a, $b) @(Get-Process -Id $a, $b -ErrorAction SilentlyContinue).Count } | Should -Be 0
        }

        It 'T-PROC-03 [<Via>] refuses to end critical processes' -Tag 'T-PROC-03' {
            $critical = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-Process lsass, csrss, winlogon | ForEach-Object { [pscustomobject]@{ Id = $_.Id; Name = "$($_.Name).exe" } }) }
            foreach ($c in $critical) {
                (T proc-stop @{ ids = $c.Id; names = $c.Name }).refused | Should -Not -BeNullOrEmpty -Because $c.Name
            }
            Invoke-LabGuest -Name $VmName -ArgumentList (, @($critical | ForEach-Object Id)) -ScriptBlock { param($ids) @(Get-Process -Id $ids -ErrorAction SilentlyContinue).Count } | Should -Be @($critical).Count
        }
    }
}
