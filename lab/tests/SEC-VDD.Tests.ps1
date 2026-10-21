# SEC-VDD - the Virtual Display Driver attacked from the device's own users (docs/security-test-lab-design.md section
# 4.8, docs/test-lab-cases.md section 13): the settings folder at the drive root (C:\VirtualDisplayDriver, written by the
# agent as SYSTEM) and the driver's control pipe (MTTVirtualDisplayPipe, which any user could create first).
#   SEC-VDD-01  a rogue pipe server of normaluser's gets nothing: no command, no usable token (the agent connects
#               anonymously and talks only to the driver host, WUDFHost.exe)
#   SEC-VDD-02  the settings folder is the agent's own (SYSTEM / Administrators write, users read); normaluser can't
#               replace it; a junction planted there (as an older build or a prepared PC could leave it) is removed,
#               not written through, and the folder made again with those permissions
# The virtual display goes on with "always" and a Fit size (as D-MON-01); a second connection with another size makes the
# agent send the driver its reload command.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "sec-vdd-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "sec-vdd-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    # one desktop connection that asks for a size (Fit), keeps it a few seconds and ends
    function Fit([string]$Name, [int]$Width, [int]$Height) {
        Invoke-LabDesktop -Name $VmName -Pin $pin -Account Console -Out (Out $Name) -Steps @(
            @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
            @{ do = 'settings'; fitWidth = $Width; fitHeight = $Height }, @{ do = 'sleep'; seconds = 8 }, @{ do = 'close' })
    }
    # what the settings folder is now, read as the admin account
    function Folder {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $f = 'C:\VirtualDisplayDriver'
            if (-not (Test-Path -LiteralPath $f)) { return [pscustomobject]@{ exists = $false } }
            $i = Get-Item -LiteralPath $f -Force
            $acl = Get-Acl -LiteralPath $f
            $sid = { param($r) try { $r.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { "$($r.IdentityReference)" } }
            $writers = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, ChangePermissions, TakeOwnership') } | ForEach-Object { & $sid $_ })
            $settings = Join-Path $f 'vdd_settings.xml'
            [pscustomobject]@{
                exists    = $true
                reparse   = "$($i.Attributes)" -match 'ReparsePoint'
                protected = $acl.AreAccessRulesProtected
                owner     = (New-Object Security.Principal.NTAccount $acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
                writers   = $writers
                settings  = $(if (Test-Path -LiteralPath $settings) { Get-Content -LiteralPath $settings -Raw } else { '' })
            }
        }
    }

    # normaluser signed in at the console: the rogue pipe server runs in that session
    $init = Initialize-LabVm -Name $VmName -Agent -AutoLogon User
    $pin = $init.Pin
    Set-LabAgentJsonValue -Name $VmName -Pin $pin -Key 'virtualDisplay' -Value 'always'
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'sec-vdd-agent-logs')
    try { Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item 'C:\Users\Public\rh-sec-vdd-*' -Force -ErrorAction SilentlyContinue } } catch { }
    # agent.json, a junction and a target folder planted, the virtual display switched: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'SEC-VDD <VmName>' -Tag Security, 'SEC-VDD' {

    Context 'the settings folder' {
        BeforeAll {
            $s = [ordered]@{}
            Fit 'first' 1280 720
            $s.installed = Folder
            # normaluser tries to put a link in its place (C:\ lets any signed-in user create folders)
            $s.userReplace = Invoke-LabGuest -Name $VmName -Account User -ScriptBlock {
                $r = [ordered]@{}
                try { Remove-Item -LiteralPath 'C:\VirtualDisplayDriver' -Recurse -Force -ErrorAction Stop; $r.removed = $true } catch { $r.removed = $false; $r.removeError = "$($_.Exception.Message)" }
                try { New-Item -ItemType File -Path 'C:\VirtualDisplayDriver\planted.txt' -Value 'x' -ErrorAction Stop | Out-Null; $r.planted = $true } catch { $r.planted = $false }
                [pscustomobject]$r
            }
            $s.afterUser = Folder
            # a junction where the folder was (an older build left the folder with C:\'s permissions, so anyone could)
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                Remove-Item -LiteralPath 'C:\VirtualDisplayDriver' -Recurse -Force
                $null = New-Item -ItemType Directory -Path 'C:\Users\Public\rh-sec-vdd-target' -Force
                cmd /c mklink /J C:\VirtualDisplayDriver C:\Users\Public\rh-sec-vdd-target | Out-Null
            }
            $s.planted = Folder
            $since = Get-Date
            Fit 'junction' 1600 900
            $s.after = Folder
            $s.target = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem -LiteralPath 'C:\Users\Public\rh-sec-vdd-target' -Force | ForEach-Object Name) }
            $s.vdLog = @(Get-LabAgentLog -Name $VmName -Since $since | ForEach-Object { "$_" } | Where-Object { $_ -match 'virtual (monitor|display)|VirtualDisplayDriver' })
            Save 'folder' $s
        }

        It 'SEC-VDD-02 the settings folder is the agent''s own: normaluser can''t replace it; a planted junction is removed, not written through' -Tag 'SEC-VDD-02' {
            $s.installed.exists | Should -BeTrue -Because 'the first connection wrote the settings'
            $s.installed.reparse | Should -BeFalse
            $s.installed.protected | Should -BeTrue -Because 'the folder doesn''t inherit C:\''s "any signed-in user may write"'
            $s.installed.owner | Should -BeIn @('S-1-5-18', 'S-1-5-32-544')
            @($s.installed.writers | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }) | Should -BeNullOrEmpty -Because "only SYSTEM and Administrators may write (writers: $($s.installed.writers -join ', '))"
            $s.userReplace.removed | Should -BeFalse -Because 'normaluser may not remove the agent''s folder'
            $s.userReplace.planted | Should -BeFalse -Because 'normaluser may not create files in it'
            $s.afterUser.reparse | Should -BeFalse
            $s.planted.reparse | Should -BeTrue -Because 'the test planted a junction (control)'
            @($s.target) | Should -BeNullOrEmpty -Because 'nothing may be written through the junction into the folder it pointed to'
            $s.after.reparse | Should -BeFalse -Because "the agent replaces the junction with its own folder (agent: $(@($s.vdLog) -join ' / '))"
            $s.after.protected | Should -BeTrue
            $s.after.settings | Should -Match '<width>1600</width>' -Because 'the settings of the connection after the junction went into the new folder'
        }
    }

    Context 'the control pipe' {
        BeforeAll {
            $p = [ordered]@{}
            $o = Out 'pipe'
            # connection A: the virtual display on (1280x720), held until the test says go
            $job = Start-LabDesktop -Name $VmName -Pin $pin -Account Console -Out $o -Steps @(
                @{ do = 'connect' }, @{ do = 'start'; session = 'console'; consent = 'auto' }, @{ do = 'wait'; state = 'Connected'; timeout = 60 }
                @{ do = 'settings'; fitWidth = 1280; fitHeight = 720 }, @{ do = 'sleep'; seconds = 8 }
                @{ do = 'markFile'; path = (Join-Path $o 'on.txt') }, @{ do = 'waitFile'; path = (Join-Path $o 'go.txt'); timeout = 300 }, @{ do = 'close' })
            Wait-LabHostFile (Join-Path $o 'on.txt') -TimeoutSec 180
            # normaluser's rogue servers under the driver's pipe name: they record what reaches them and who they could act as
            $rogue = Start-LabInSession -Name $VmName -Account User -ScriptBlock {
                $result = 'C:\Users\Public\rh-sec-vdd-rogue.json'
                $seen = New-Object System.Collections.ArrayList
                $servers = @()
                $err = ''
                try {
                    for ($i = 0; $i -lt 4; $i++) {
                        $servers += New-Object System.IO.Pipes.NamedPipeServerStream('MTTVirtualDisplayPipe', [System.IO.Pipes.PipeDirection]::InOut, -1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous)
                    }
                }
                catch { $err = "$($_.Exception.Message)" }
                $waits = @($servers | ForEach-Object { $_.WaitForConnectionAsync() })
                Set-Content 'C:\Users\Public\rh-sec-vdd-ready.txt' "$($servers.Count)" -Encoding ASCII
                $until = (Get-Date).AddSeconds(120)
                while ((Get-Date) -lt $until -and -not (Test-Path 'C:\Users\Public\rh-sec-vdd-stop.txt')) {
                    for ($i = 0; $i -lt $waits.Count; $i++) {
                        if ($waits[$i] -and $waits[$i].IsCompleted) {
                            $s = $servers[$i]; $waits[$i] = $null
                            $buf = New-Object byte[] 256
                            $read = $s.ReadAsync($buf, 0, $buf.Length)
                            $n = if ($read.Wait(5000)) { $read.Result } else { -1 }
                            $as = try { $s.GetImpersonationUserName() } catch { "refused: $($_.Exception.Message)" }
                            [void]$seen.Add([pscustomobject]@{ bytes = $n; impersonates = $as })
                        }
                    }
                    Start-Sleep -Milliseconds 200
                }
                [pscustomobject]@{ servers = $servers.Count; createError = $err; connections = @($seen) } | ConvertTo-Json -Depth 4 | Set-Content $result -Encoding UTF8
            }
            $deadline = (Get-Date).AddSeconds(60)
            do { $ready = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\Users\Public\rh-sec-vdd-ready.txt' }; if (-not $ready) { Start-Sleep -Seconds 2 } } while (-not $ready -and (Get-Date) -lt $deadline)
            if (-not $ready) { throw 'normaluser''s rogue pipe servers didn''t start within 60 s' }
            $since = Get-Date
            # connection B with another size: the device is on already, so the agent tells the driver to reload
            Fit 'reload' 1600 900
            Set-Content (Join-Path $o 'go.txt') 'go'
            $p.a = Receive-LabDesktop $job
            Invoke-LabGuest -Name $VmName -ScriptBlock { Set-Content 'C:\Users\Public\rh-sec-vdd-stop.txt' 'stop' -Encoding ASCII }
            $deadline = (Get-Date).AddSeconds(60)
            do { $p.rogue = Invoke-LabGuest -Name $VmName -ScriptBlock { $f = 'C:\Users\Public\rh-sec-vdd-rogue.json'; if (Test-Path $f) { Get-Content $f -Raw | ConvertFrom-Json } }; if (-not $p.rogue) { Start-Sleep -Seconds 2 } } while (-not $p.rogue -and (Get-Date) -lt $deadline)
            try { Stop-LabInSession -Name $VmName -Task $rogue } catch { }
            $p.vdLog = @(Get-LabAgentLog -Name $VmName -Since $since | ForEach-Object { "$_" } | Where-Object { $_ -match 'virtual (monitor|display)|control pipe' })
            Save 'pipe' $p
        }

        It 'SEC-VDD-01 a rogue server under the driver''s pipe name gets no command and no usable token' -Tag 'SEC-VDD-01' {
            $p.rogue | Should -Not -BeNullOrEmpty -Because 'the rogue servers must report what reached them within 60 s'
            $p.rogue.servers | Should -BeGreaterThan 0 -Because "normaluser could create the pipe name (the attack's precondition; error: $($p.rogue.createError))"
            foreach ($c in @($p.rogue.connections)) {
                $c.bytes | Should -BeLessOrEqual 0 -Because 'the agent sends RELOAD_DRIVER only to the driver host'
                "$($c.impersonates)" | Should -Not -Match 'SYSTEM' -Because 'the agent connects anonymously'
            }
            if (@($p.rogue.connections).Count -gt 0) {
                ($p.vdLog -join "`n") | Should -Match 'served by another program' -Because 'a refused reload says why in the agent log'
            }
        }
    }
}
