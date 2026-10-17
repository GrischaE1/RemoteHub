# A-HAND - installing the agent by hand, the way the clipboard install leaves it to the admin: the MSI next to the
# .rhagent RemoteHub made (or ENROLLMENT=<path>), msiexec run on the device. Checks the refusals of a wrong folder or a
# wrong enrollment (each rolled back without a trace), the ENROLLMENT property, and the install from the console
# user's session, seen by RemoteHub's polling for the answer (WaitForManualInstall: health every 10 s, 5 s timeout).
# The interactive install dialog (A-HAND-06) is UI automation, not here. See docs/test-lab-cases.md section 5.2.
# Starts from the checkpoint (Reset-LabVm): the agent must not be installed yet.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "a-hand-$name.json") -Encoding UTF8 }
    $work = Join-Path $EvidenceDir 'a-hand-files'
    $null = New-Item -ItemType Directory -Path $work -Force
    $msi = Get-LabAgentMsi
    # host -> guest: a folder with the MSI (copied in the guest) and the given enrollment files
    function New-Case([string]$Case, [string[]]$Enrollments = @()) {
        $dir = "C:\LabHand\$Case"
        $null = Invoke-LabGuest -Name $VmName -ArgumentList $dir -ScriptBlock { param($d) New-Item -ItemType Directory -Path $d -Force; Copy-Item 'C:\LabHand\msi\*.msi' $d }
        if ($Enrollments) { Copy-LabFile -Name $VmName -Path $Enrollments -Destination $dir }
        $dir
    }
    # msiexec /i as PSAdmin (elevated over PowerShell Direct): exit code and the lines of the log the agent wrote
    function Invoke-Msi([string]$Dir, [string]$Enrollment) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Dir, $Enrollment -ScriptBlock {
            param($dir, $enrollment)
            $msi = (Get-ChildItem $dir -Filter *.msi | Select-Object -First 1).FullName
            $log = Join-Path $dir 'install.log'
            $a = @('/i', "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"")
            if ($enrollment) { $a += "ENROLLMENT=`"$enrollment`"" }
            $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $a -Wait -PassThru -WindowStyle Hidden
            [pscustomobject]@{
                ExitCode = $p.ExitCode
                Lines    = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'WixQuietExec|rhagent|enrollment|Enrollment|Port \d+ is not|allowed groups|device ID' } | ForEach-Object { [string]$_ } | Select-Object -Last 20)
                Left     = @(Get-ChildItem $dir -Filter *.rhagent -ErrorAction SilentlyContinue | ForEach-Object Name)
            }
        }
    }
    function Test-Clean($State) {
        $State.Product | Should -BeNullOrEmpty
        $State.Service | Should -BeNullOrEmpty
        $State.Config | Should -BeNullOrEmpty
        $State.Firewall | Should -BeNullOrEmpty
        @($State.OurCertificates) | Should -BeNullOrEmpty
    }
    # an enrollment RemoteHub made, with fields changed afterwards
    function New-BadEnrollment([string]$Path, [scriptblock]$Change) {
        $null = New-LabEnrollment -Name $VmName -Path $Path
        $e = Get-Content $Path -Raw | ConvertFrom-Json
        & $Change $e
        [IO.File]::WriteAllText($Path, ($e | ConvertTo-Json -Depth 4))
        $Path
    }

    Reset-LabVm -Name $VmName
    Install-LabSigningCertificate -Name $VmName
    Copy-LabFile -Name $VmName -Path $msi -Destination 'C:\LabHand\msi'
}

AfterAll {
    Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-hand-agent-logs')
    # installed and uninstalled by hand, signing certificate added: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-HAND <VmName>' -Tag Agent, 'A-HAND' {

    Context 'refused installs' {
        BeforeAll {
            $r = [ordered]@{}
            $one = New-LabEnrollment -Name $VmName -Path (Join-Path $work 'one\first.rhagent')
            $two = New-LabEnrollment -Name $VmName -Path (Join-Path $work 'two\second.rhagent')
            $r.two = Invoke-Msi (New-Case 'two' @($one.file, $two.file))
            $r.twoState = Get-LabAgentState -Name $VmName
            $r.none = Invoke-Msi (New-Case 'none')
            $r.noneState = Get-LabAgentState -Name $VmName
            $bad = [ordered]@{
                format = { param($e) $e.format = 2 }
                guid   = { param($e) $e.deviceId = 'not-a-guid' }
                port   = { param($e) $e.listenPort = 0 }
                groups = { param($e) $e.allowedGroups = @() }
            }
            foreach ($k in $bad.Keys) {
                $file = New-BadEnrollment (Join-Path $work "bad-$k\device.rhagent") $bad[$k]
                $r["bad-$k"] = Invoke-Msi (New-Case "bad-$k" @($file))
                $r["bad-$k-state"] = Get-LabAgentState -Name $VmName
            }
            Save 'refused' $r
        }

        It 'A-HAND-02 two .rhagent files: "More than one .rhagent file", nothing enrolled, rolled back' -Tag 'A-HAND-02' {
            $r.two.ExitCode | Should -Be 1603
            ($r.two.Lines -join "`n") | Should -Match 'More than one \.rhagent file'
            Test-Clean $r.twoState
        }

        It 'A-HAND-03 no .rhagent and no configuration: fails, nothing left' -Tag 'A-HAND-03' {
            $r.none.ExitCode | Should -Be 1603
            ($r.none.Lines -join "`n") | Should -Match 'No \.rhagent enrollment file found'
            Test-Clean $r.noneState
        }

        It 'A-HAND-05 enrollment format 2, bad GUID, port 0, no groups: each refused with its reason, rolled back' -Tag 'A-HAND-05' {
            $reasons = @{ format = 'format 2 is not supported'; guid = 'no valid device ID'; port = 'Port 0 is not valid'; groups = 'No allowed groups' }
            foreach ($k in $reasons.Keys) {
                $r["bad-$k"].ExitCode | Should -Be 1603 -Because $k
                ($r["bad-$k"].Lines -join "`n") | Should -Match ([regex]::Escape($reasons[$k])) -Because $k
                Test-Clean $r["bad-$k-state"]
            }
        }
    }

    Context 'ENROLLMENT property' {
        BeforeAll {
            $e = New-LabEnrollment -Name $VmName -Path (Join-Path $work 'elsewhere\device.rhagent')
            $dir = New-Case 'property'
            Copy-LabFile -Name $VmName -Path $e.file -Destination 'C:\LabHand\elsewhere'
            $result = Invoke-Msi $dir 'C:\LabHand\elsewhere\device.rhagent'
            $state = Get-LabAgentState -Name $VmName
            $fileLeft = Invoke-LabGuest -Name $VmName -ScriptBlock { Test-Path 'C:\LabHand\elsewhere\device.rhagent' }
            $health = Get-LabAgentHealth -Name $VmName -Pin $e.pin
            $uninstall = Uninstall-LabAgent -Name $VmName
            Save 'property' @{ Enrollment = $e; Result = $result; State = $state; FileLeft = $fileLeft; Health = $health; Uninstall = $uninstall }
        }

        It 'A-HAND-04 ENROLLMENT=[path] in another folder is used, and the file is deleted afterwards' -Tag 'A-HAND-04' {
            $result.ExitCode | Should -BeIn 0, 3010
            $state.Config.deviceId | Should -Be $e.deviceId
            $state.Certificate.Sha256 | Should -Be $e.pin
            $health.deviceId | Should -Be $e.deviceId
            $fileLeft | Should -BeFalse
        }
    }

    Context 'by hand in the console user''s session' {
        BeforeAll {
            $e = New-LabEnrollment -Name $VmName -Path (Join-Path $work 'session\device.rhagent')
            $dir = New-Case 'session' @($e.file)
            # the handle is read before waiting: with -Wait -PassThru the exit code stays empty in a session task
            $script = "`$p = Start-Process msiexec.exe -ArgumentList '/i', '`"$dir\$(Split-Path $msi -Leaf)`"', '/qn', '/norestart' -PassThru; `$null = `$p.Handle; `$p.WaitForExit(); `$p.ExitCode"
            # RemoteHub polls as WaitForManualInstall does: every 10 s, 5 s each, from the moment the admin was told to install
            $job = Start-Job -ArgumentList (Get-LabVmAddress -Name $VmName), $e.pin, (Join-Path (Get-LabConfig).LabRoot 'harness\RemoteHub.LabHarness.exe') -ScriptBlock {
                param($ip, $pin, $exe)
                $until = (Get-Date).AddMinutes(10)
                while ((Get-Date) -lt $until) {
                    $out = & $exe health --host $ip --port 58732 --pin $pin 2>$null
                    if ($LASTEXITCODE -eq 0) { return [pscustomobject]@{ At = Get-Date; Health = ($out -join "`n") } }
                    Start-Sleep -Seconds 10
                }
            }
            $exit = Invoke-LabInSession -Name $VmName -Account Console -Elevated -TimeoutSec 600 -ScriptBlock ([scriptblock]::Create($script))
            $installedAt = Get-Date
            $seen = Receive-Job $job -Wait -AutoRemoveJob
            $state = Get-LabAgentState -Name $VmName
            Save 'session' @{ Enrollment = $e; Exit = $exit; InstalledAt = $installedAt; Seen = $seen; State = $state }
        }

        It 'A-HAND-01 MSI next to the .rhagent, run as admin in the console session: installed, and RemoteHub''s polling sees it within 10 s' -Tag 'A-HAND-01' {
            "$exit".Trim() | Should -BeIn '0', '3010'
            $state.Config.deviceId | Should -Be $e.deviceId
            $state.Certificate.Sha256 | Should -Be $e.pin
            $seen | Should -Not -BeNullOrEmpty
            # polling every 10 s: the answer comes at the latest one interval (plus the 5 s try) after the install ended
            ($seen.At - $installedAt).TotalSeconds | Should -BeLessOrEqual 15
        }
    }
}
