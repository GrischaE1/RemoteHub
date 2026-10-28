# T (more) - RemoteHub's remote tools beyond T-TOOLS.Tests.ps1, over the agent and over WinRM HTTPS: a folder tree
# with Unicode names and paths over 260 characters, an upload over a file someone holds open, uploads with Defender's
# real-time scan on, and renaming a registry key with its own ACL. Results are checked on the device over PowerShell
# Direct. See docs/test-lab-cases.md section 7.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "t2-$name.json") -Encoding UTF8 }
    function Get-Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

    # a tree with Unicode names (written as char codes: lab scripts stay ASCII) and one path over 260 characters
    $data = Join-Path $env:TEMP "RemoteHubLab-T2-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $uni = "$([char]0xDC)n$([char]0xEF)c$([char]0xF6)d$([char]0xE9) $([char]0x6587)$([char]0x4EF6)"   # Unicode + CJK
    $tree = Join-Path $data 'tree'
    $deep = "\\?\$tree\$uni\" + ((1..6 | ForEach-Object { 'level-' + ('x' * 40) + "-$_" }) -join '\')
    $null = [IO.Directory]::CreateDirectory($deep)
    [IO.File]::WriteAllText("$deep\deep file $uni.txt", "deep $([guid]::NewGuid())")
    [IO.File]::WriteAllText("\\?\$tree\$uni\top.txt", "top $([guid]::NewGuid())")
    [IO.File]::WriteAllText("\\?\$tree\plain.txt", "plain $([guid]::NewGuid())")
    # relative path -> hash, read through \\?\ (Get-ChildItem in 5.1 stops at 260)
    function Get-TreeHashes([string]$Root) {
        $prefix = "\\?\$Root"
        $h = @{}
        foreach ($f in [IO.Directory]::EnumerateFiles($prefix, '*', 'AllDirectories')) {
            $s = [IO.File]::OpenRead($f); try { $h[$f.Substring($prefix.Length).TrimStart('\')] = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
        }
        $h
    }
    $treeHashes = Get-TreeHashes $tree
    $longest = ($treeHashes.Keys | Sort-Object Length -Descending | Select-Object -First 1).Length

    $small = Join-Path $data 'small'
    $null = New-Item -ItemType Directory -Path $small -Force
    1..200 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $small ("f{0:000}.txt" -f $_)), "RemoteHub lab file $_ $([guid]::NewGuid())") }
    $smallHashes = @(Get-ChildItem $small -File | ForEach-Object { Get-Sha $_.FullName } | Sort-Object)
}

AfterAll {
    Remove-Item "\\?\$data" -Recurse -Force -ErrorAction SilentlyContinue
    # LongPathsEnabled, uploaded files, registry keys and Defender's scan changed, WinRM HTTPS set up: the next file
    # starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'T2 <VmName>' -Tag Tools, 'T' {

    Context 'over <Via>' -ForEach @(@{ Via = 'Agent' }, @{ Via = 'WinRM' }) {
        BeforeAll {
            $pin = $null
            $cert = $null
            if ($Via -eq 'Agent') { $pin = (Initialize-LabVm -Name $VmName -Agent).Pin }
            else {
                Reset-LabVm -Name $VmName
                $null = Invoke-LabSetupScript -Name $VmName
                $cert = (Get-LabWinRmState -Name $VmName).Certificate.Thumbprint
            }
            function T([string]$Op, [hashtable]$P = @{}) {
                $P = $P.Clone()
                if ($Via -eq 'WinRM') { $P.cert = $cert }
                Invoke-LabTool -Name $VmName -Op $Op -Via $Via -Pin $pin -Params $P
            }
            # paths over 260 characters: the remote side is Windows PowerShell 5.1 without \?\ paths, so it needs this
            Invoke-LabGuest -Name $VmName -ScriptBlock { Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -Name LongPathsEnabled -Value 1 -Type DWord }
            $tag = $Via.ToLowerInvariant()
            $remote = "C:\LabUp2-$tag"
        }

        It 'T-FS-04 [<Via>] uploads a folder tree with Unicode names and paths over 260 characters, all equal' -Tag 'T-FS-04' {
            $longest | Should -BeGreaterThan 260
            $up = T fs-upload @{ files = $tree; remote = $remote }
            Save "$tag-tree" $up
            $up.result.failed | Should -Be 0
            $g = Invoke-LabGuest -Name $VmName -ArgumentList "$remote\tree" -ScriptBlock {
                param($root)
                $prefix = "\\?\$root"
                $h = @{}
                if ([IO.Directory]::Exists($prefix)) {
                    foreach ($f in [IO.Directory]::EnumerateFiles($prefix, '*', 'AllDirectories')) {
                        $s = [IO.File]::OpenRead($f); try { $h[$f.Substring($prefix.Length).TrimStart('\')] = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)).Replace('-', '') } finally { $s.Dispose() }
                    }
                }
                $h
            }
            @($g.Keys | Sort-Object) | Should -Be @($treeHashes.Keys | Sort-Object)
            foreach ($k in $treeHashes.Keys) { $g[$k] | Should -Be $treeHashes[$k] -Because $k }
        }

        It 'T-FS-09 [<Via>] uploading over a file held open exclusively fails clearly, the original stays' -Tag 'T-FS-09' {
            $target = "$remote\locked"
            $orig = Invoke-LabGuest -Name $VmName -ArgumentList $target -ScriptBlock {
                param($t)
                $null = New-Item -ItemType Directory -Path $t -Force
                Set-Content -Encoding ASCII "$t\f001.txt" 'held open on the device'
                $hash = (Get-FileHash "$t\f001.txt" -Algorithm SHA256).Hash
                # held by a process outside this PowerShell Direct session (it would close with it); it says when it holds it
                $ready = "$t.ready"
                Remove-Item $ready -ErrorAction SilentlyContinue
                $cmd = "powershell.exe -NoProfile -Command `"`$f = [IO.File]::Open('$t\f001.txt', 'Open', 'ReadWrite', 'None'); Set-Content -Encoding ASCII '$ready' 1; Start-Sleep 600`""
                $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
                $until = (Get-Date).AddSeconds(60)
                while (-not (Test-Path $ready) -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200 }
                if (-not (Test-Path $ready)) { throw "the process holding $t\f001.txt didn't open it within 60 s" }
                # the marker can still be open while the other process writes it (run 20261021-222745: "being used by
                # another process"): removed once it is free, at most 5 s - it is only tidying up
                $gone = (Get-Date).AddSeconds(5)
                while ((Test-Path $ready) -and (Get-Date) -lt $gone) { try { Remove-Item $ready -ErrorAction Stop } catch { Start-Sleep -Milliseconds 200 } }
                $hash
            }
            $up = T fs-upload @{ files = (Join-Path $small 'f001.txt'); remote = $target; overwrite = 1 }
            Save "$tag-locked" $up
            ($up.result.failed + $(if ($up.refused) { 1 } else { 0 })) | Should -BeGreaterThan 0
            ($up | ConvertTo-Json -Depth 6) | Should -Match 'used by another process|being used|locked|access'
            $now = Invoke-LabGuest -Name $VmName -ArgumentList $target -ScriptBlock {
                param($t)
                Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object CommandLine -like '*f001.txt*' | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
                Start-Sleep -Seconds 1
                [pscustomobject]@{ Hash = (Get-FileHash "$t\f001.txt" -Algorithm SHA256).Hash; Parts = @(Get-ChildItem $t -Filter '*.remotehub-part').Count }
            }
            $now.Hash | Should -Be $orig
            $now.Parts | Should -Be 0
        }

        It 'T-FS-10 [<Via>] with Defender real-time scanning on, a tree upload is still verified' -Tag 'T-FS-10' {
            $mp = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $s = Get-MpComputerStatus -ErrorAction SilentlyContinue
                if ($s -and -not $s.RealTimeProtectionEnabled) { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction SilentlyContinue; $s = Get-MpComputerStatus }
                [pscustomobject]@{ Present = [bool]$s; RealTime = $(if ($s) { $s.RealTimeProtectionEnabled } else { $false }) }
            }
            if (-not $mp.RealTime) { Set-ItResult -Skipped -Because 'Defender real-time protection is not available on this VM'; return }
            $up = T fs-upload @{ files = $small; remote = "$remote\scanned" }
            Save "$tag-defender" $up
            $up.result.failed | Should -Be 0
            $g = Invoke-LabGuest -Name $VmName -ArgumentList "$remote\scanned" -ScriptBlock { param($r) @(Get-ChildItem $r -Recurse -File | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash } | Sort-Object) }
            @($g) | Should -Be $smallHashes
        }

        It 'T-REG-03 [<Via>] renaming a key keeps its own ACL' -Tag 'T-REG-03' {
            $sddl = Invoke-LabGuest -Name $VmName -ArgumentList $tag -ScriptBlock {
                param($tag)
                $k = "HKLM:\SOFTWARE\RHLabAcl-$tag"
                $null = New-Item $k -Force
                $null = New-Item "$k\child" -Force
                $acl = Get-Acl $k
                $acl.SetAccessRuleProtection($true, $true)
                $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule ((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'FullControl', 'ContainerInherit', 'None', 'Allow')))
                Set-Acl $k $acl
                (Get-Acl $k).Sddl
            }
            (T reg-rename-key @{ key = "HKLM\SOFTWARE\RHLabAcl-$tag"; new = "RHLabAcl-$tag-renamed" }).refused | Should -BeNullOrEmpty
            $after = Invoke-LabGuest -Name $VmName -ArgumentList $tag -ScriptBlock {
                param($tag)
                $k = "HKLM:\SOFTWARE\RHLabAcl-$tag-renamed"
                [pscustomobject]@{ Exists = Test-Path $k; Old = Test-Path "HKLM:\SOFTWARE\RHLabAcl-$tag"; Child = Test-Path "$k\child"; Sddl = $(if (Test-Path $k) { (Get-Acl $k).Sddl }) }
            }
            $after.Exists | Should -BeTrue
            $after.Old | Should -BeFalse
            $after.Child | Should -BeTrue
            $after.Sddl | Should -Be $sddl
        }
    }
}
