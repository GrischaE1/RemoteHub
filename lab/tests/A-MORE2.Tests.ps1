# A (more, 2) - agent cases checked from the host (harness) and in the guest (PowerShell Direct), on both VMs:
#   A-INST-27   a folder a user made before the install is taken over (owner Administrators, inherited ACEs only)
#   A-INST-28   AUTOSIGNIN=1 on a package without the credential provider (needs such a package - see Find-NoProviderMsi)
#   A-HAND-07   the credential provider added / removed on an installed agent (ADDLOCAL / REMOVE=CredentialProvider)
#   A-UNINST-16 policy values IT changed after the agent wrote them survive uninstall and AUTOSIGNIN=0
#   S-THR-06    what counts toward the sign-in block (403 counts, 500 doesn't)
#   S-THR-07    the shared sign-in slots (skipped: needs 2 source addresses and Windows sign-ins slower than 30 s)
#   P-SES-10    PowerShell can't start (IFEO Debugger on powershell.exe): 500, log line, no event 1000, slot released
#   W-NAME-01   WinRM setup script for a computer name: CN / SAN, reuse by CN, a bad name refused
# Needs: one lab VM (agent MSI under test; A-HAND-07 / A-UNINST-16 need it built with the credential provider,
# build-installer.ps1 -CredentialProvider). See docs/test-lab-cases.md sections 5, 6, 7 and 8.
# Every context starts from the checkpoint (Reset-LabVm): each changes the installation itself.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    $clsid = '{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}'   # Package.wxs
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "a-more2-$name.json") -Encoding UTF8 }
    function Out([string]$name) { $d = Join-Path $EvidenceDir "a-more2-$name"; $null = New-Item -ItemType Directory -Path $d -Force; $d }
    # the module's harness runner (not exported) - for harness commands the module has no wrapper for
    function Invoke-Harness([string[]]$Arguments, [int]$TimeoutSec = 120) {
        & (Get-Module RemoteHub.TestLab) { param($a, $t) Invoke-LabHarness $a -TimeoutSec $t } $Arguments $TimeoutSec
    }
    # does the MSI have this feature (Feature table)?
    function Test-MsiFeature([string]$Path, [string]$Feature) {
        $wi = New-Object -ComObject WindowsInstaller.Installer
        try {
            $db = $wi.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $wi, @($Path, 0))
            $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT ``Feature`` FROM ``Feature`` WHERE ``Feature`` = '$Feature'"))
            [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @())
            $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, @())
            [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, @())
            $null -ne $rec
        }
        finally { $rec = $null; $view = $null; $db = $null; $wi = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers() }
    }
    # an agent package of the build under test without the credential provider: the published one when it was built
    # without -CredentialProvider, else LabRoot\msi\no-provider (Start-LabRun.ps1 doesn't publish one today)
    function Find-NoProviderMsi {
        $candidates = @(Get-LabAgentMsi) + @(Get-ChildItem (Join-Path (Get-LabConfig).LabRoot 'msi\no-provider') -Filter 'RemoteHub-Agent-*.msi' -ErrorAction SilentlyContinue | ForEach-Object FullName)
        foreach ($c in $candidates) { if ($c -and -not (Test-MsiFeature $c 'CredentialProvider')) { return $c } }
        $null
    }
    # the provider's files and keys, the policy values, the service and its identity - what A-HAND-07 compares
    function Get-Snapshot {
        Invoke-LabGuest -Name $VmName -ArgumentList $clsid -ScriptBlock {
            param($clsid)
            $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue
            $rights = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue
            $cfg = 'C:\ProgramData\RemoteHub\Agent\agent.json'
            [pscustomobject]@{
                Dll          = Test-Path 'C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll'
                Provider     = Test-Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid"
                Clsid        = Test-Path "HKLM:\SOFTWARE\Classes\CLSID\$clsid"
                Behavior     = $(if ($policy -and $policy.PSObject.Properties['NoUserBehavior']) { [string]$policy.NoUserBehavior })
                Right        = (@($(if ($rights -and $rights.PSObject.Properties['AutoSignIn']) { $rights.AutoSignIn })) -join ';')
                Marker       = Test-Path 'C:\ProgramData\RemoteHub\Agent\autosignin.policy'
                ServicePid   = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").ProcessId
                ServiceState = (Get-CimInstance Win32_Service -Filter "Name='RemoteHubAgent'").State
                ConfigHash   = $(if (Test-Path $cfg) { (Get-FileHash $cfg -Algorithm SHA256).Hash })
                EnrollLines  = @(Get-ChildItem 'C:\ProgramData\RemoteHub\Agent\logs' -Filter 'agent-*.log' -ErrorAction SilentlyContinue | Get-Content | Where-Object { $_ -match 'Enrolled as device' }).Count
            }
        }
    }
    # msiexec in the guest as PSAdmin (elevated): exit code
    function Invoke-GuestMsi([string[]]$Arguments, [string]$Log) {
        Invoke-LabGuest -Name $VmName -ArgumentList (, $Arguments), $Log -ScriptBlock {
            param($a, $log)
            $p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList ((@($a | ForEach-Object { [string]$_ }) + @('/qn', '/norestart', '/l*v', "`"$log`"")) -join ' ') -Wait -PassThru -WindowStyle Hidden
            $p.ExitCode
        }
    }
    # the policy values as the device has them (also after the agent is gone)
    function Get-PolicyNow {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent' -ErrorAction SilentlyContinue
            $rights = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub\Agent\Rights' -ErrorAction SilentlyContinue
            [pscustomobject]@{
                Behavior = $(if ($policy -and $policy.PSObject.Properties['NoUserBehavior']) { [string]$policy.NoUserBehavior })
                Right    = (@($(if ($rights -and $rights.PSObject.Properties['AutoSignIn']) { $rights.AutoSignIn })) -join ';')
                Marker   = Test-Path 'C:\ProgramData\RemoteHub\Agent\autosignin.policy'
            }
        }
    }
    function Ws([string]$User, [string]$Pass, [hashtable]$More = @{}) {
        $p = @{ pin = $pin; user = $User; password = $Pass }
        foreach ($k in $More.Keys) { $p[$k] = $More[$k] }
        @((Invoke-LabProbe -Name $VmName -Kind ws -Params $p).results)
    }
    function Status([object[]]$Results) { @($Results | ForEach-Object { [int]$_.status }) }
    function Hold { @((Invoke-LabProbe -Name $VmName -Kind hold -Params @{ pin = $pin; user = $vm.AdminAccount; password = $password; count = 1; seconds = 1 }).results)[0] }
    # RemoteHub's setup script (RemotingSetup.BuildScript) for -TargetHost as typed, run in the guest the way
    # Invoke-LabSetupScript runs it (that one always uses the VM's address)
    function Invoke-SetupFor([string]$TargetHost) {
        $local = Join-Path $env:TEMP "RemoteHub-setup-$([guid]::NewGuid().ToString('N').Substring(0, 8)).ps1"
        $null = Invoke-Harness @('tool', '--op', 'script', '--host', $TargetHost, '--allow', (Get-LabHostAddress -Name $VmName), '--https', '1', '--local-admins', '1', '--out', $local)
        try {
            $text = [IO.File]::ReadAllText($local)
            Invoke-LabGuest -Name $VmName -ArgumentList $text -ScriptBlock {
                param($text)
                $file = Join-Path $env:TEMP 'RemoteHub-setup.ps1'
                Set-Content -Path $file -Value $text -Encoding UTF8
                $job = Start-Job -ArgumentList $file -ScriptBlock {
                    param($file)
                    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
                    try { & $file *>&1 | ForEach-Object { "$_" } } catch { "FAILED: $($_.Exception.Message)" }
                }
                $finished = Wait-Job $job -Timeout 600
                $out = @(Receive-Job $job -Keep 2>&1 | ForEach-Object { "$_" })
                $failed = $null
                if (-not $finished) { Stop-Job $job; $failed = "The setup script didn't finish within 600 s. Last output: $(@($out)[-1])" }
                elseif ($out -and $out[-1] -like 'FAILED: *') { $failed = $out[-1].Substring(8); $out = @($out | Select-Object -SkipLast 1) }
                Remove-Job $job -Force
                Remove-Item $file -ErrorAction SilentlyContinue
                [pscustomobject]@{ Output = @($out); Failed = $failed }
            }
        }
        finally { Remove-Item $local -ErrorAction SilentlyContinue }
    }
}

AfterAll {
    try { Save-LabAgentEvidence -Name $VmName -Destination (Join-Path $EvidenceDir 'a-more2-agent-logs') } catch { }
    # installs, uninstalls, IFEO and WinRM certificates changed: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'A-MORE2 <VmName>' -Tag Agent, 'A-INST', 'A-HAND', 'A-UNINST', 'S-THR', 'P-SES', 'W-NAME' {

    Context 'a folder made before the install' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $logs = 'C:\ProgramData\RemoteHub\Agent\logs'
            # normaluser makes the folder, a file, a subfolder and a file in it (ProgramData lets users create folders)
            $make = {
                param($logs)
                $null = New-Item -ItemType Directory -Path "$logs\sub" -Force
                Set-Content "$logs\x.log" 'made by a user before the install'
                Set-Content "$logs\sub\y.log" 'made by a user before the install'
                (Get-Acl "$logs\x.log").Owner
            }
            $made = [ordered]@{ How = 'normaluser over PowerShell Direct' }
            try { $made.Owner = Invoke-LabGuest -Name $VmName -Account User -ArgumentList $logs -ScriptBlock $make }
            catch {
                # PowerShell Direct as a standard user not possible: the admin makes them and hands them to normaluser
                # (owner and an explicit full-control entry - what a user's own files have)
                $made.How = "made by the admin, given to normaluser (PowerShell Direct as normaluser: $($_.Exception.Message))"
                $made.Owner = Invoke-LabGuest -Name $VmName -ArgumentList $logs, $vm.UserAccount -ScriptBlock {
                    param($logs, $user)
                    $null = New-Item -ItemType Directory -Path "$logs\sub" -Force
                    Set-Content "$logs\x.log" 'made by a user before the install'
                    Set-Content "$logs\sub\y.log" 'made by a user before the install'
                    $who = New-Object Security.Principal.NTAccount $user
                    foreach ($p in 'C:\ProgramData\RemoteHub', 'C:\ProgramData\RemoteHub\Agent', $logs, "$logs\sub", "$logs\x.log", "$logs\sub\y.log") {
                        $acl = Get-Acl $p
                        $acl.SetOwner($who)
                        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule $who, 'FullControl', 'Allow'))
                        Set-Acl $p $acl
                    }
                    (Get-Acl "$logs\x.log").Owner
                }
            }
            $install = Install-LabAgent -Name $VmName
            $acls = Invoke-LabGuest -Name $VmName -ArgumentList $logs -ScriptBlock {
                param($logs)
                $sid = [Security.Principal.SecurityIdentifier]
                @(foreach ($p in $logs, "$logs\x.log", "$logs\sub", "$logs\sub\y.log") {
                        $a = Get-Acl $p
                        [pscustomobject]@{
                            Path  = $p
                            Owner = $a.GetOwner($sid).Value
                            Rules = @($a.GetAccessRules($true, $true, $sid) | ForEach-Object { [pscustomobject]@{ Sid = $_.IdentityReference.Value; Inherited = $_.IsInherited; Type = [string]$_.AccessControlType } })
                        }
                    })
            }
            # normaluser tries to read and to write afterwards
            $try = {
                param($logs)
                $r = [ordered]@{}
                $r.read = try { $null = Get-Content "$logs\x.log" -ErrorAction Stop; 'read' } catch { 'refused' }
                $r.readSub = try { $null = Get-Content "$logs\sub\y.log" -ErrorAction Stop; 'read' } catch { 'refused' }
                $r.write = try { Add-Content "$logs\x.log" 'user' -ErrorAction Stop; 'written' } catch { 'refused' }
                $r.create = try { Set-Content "$logs\new.log" 'user' -ErrorAction Stop; 'written' } catch { 'refused' }
                [pscustomobject]$r
            }
            $access = try { Invoke-LabGuest -Name $VmName -Account User -ArgumentList $logs -ScriptBlock $try } catch { [pscustomobject]@{ error = "$_" } }
            Save 'folder-before' @{ Made = $made; Install = $install; Acls = $acls; Access = $access }
        }

        It 'A-INST-27 a logs folder a user made before the install: every entry owned by Administrators, only inherited SYSTEM / Administrators entries, the user can neither read nor write' -Tag 'A-INST-27' {
            $acls.Count | Should -Be 4
            foreach ($a in $acls) {
                $a.Owner | Should -Be 'S-1-5-32-544' -Because $a.Path
                @($a.Rules | Where-Object { -not $_.Inherited }) | Should -BeNullOrEmpty -Because "$($a.Path): only inherited entries"
                @($a.Rules | Where-Object { $_.Sid -notin 'S-1-5-18', 'S-1-5-32-544' }) | Should -BeNullOrEmpty -Because "$($a.Path): only SYSTEM and Administrators"
            }
            if ($access.PSObject.Properties['error']) { Set-ItResult -Inconclusive -Because "the ACLs are right; normaluser's own try needs PowerShell Direct as normaluser: $($access.error)"; return }
            $access.read | Should -Be 'refused'
            $access.readSub | Should -Be 'refused'
            $access.write | Should -Be 'refused'
            $access.create | Should -Be 'refused'
        }
    }

    Context 'AUTOSIGNIN=1 on a package without the provider' {
        BeforeAll {
            $noProvider = Find-NoProviderMsi
            if ($noProvider) {
                Reset-LabVm -Name $VmName
                $since = Get-LabGuestTime -Name $VmName
                $install = try { Install-LabAgent -Name $VmName -Msi $noProvider -AutoSignIn $true } catch { [pscustomobject]@{ error = "$_" } }
                $state = Get-LabAgentState -Name $VmName
                $log = @(Get-LabAgentLog -Name $VmName -Since $since)
                $run = $null
                if (-not $install.PSObject.Properties['error']) {
                    $pin = $install.pin
                    Disconnect-LabConsole -Name $VmName
                    $run = try { Invoke-LabAutoSignIn -Name $VmName -Pin $pin } catch { [pscustomobject]@{ error = "$_"; rights = $null; features = @() } }
                }
                Save 'no-provider' @{ Msi = $noProvider; Install = $install; State = $state; Log = $log; Run = $run }
            }
        }

        # Known product gap (docs/test-lab-cases.md A-INST-28): auto-sign-in=[AUTOSIGNIN] is passed to the enroll step
        # outside the CredentialProvider condition, so NoUserBehavior and Rights\AutoSignIn are written anyway. Tested
        # for the correct behaviour - fails until fixed.
        It 'A-INST-28 AUTOSIGNIN=1 on a package without the provider: installs, no DLL / CLSID keys, no policy values, Hello autoSignIn false' -Tag 'A-INST-28' {
            if (-not $noProvider) {
                Set-ItResult -Skipped -Because 'needs an agent MSI of the build under test without the credential provider (build-installer.ps1 without -CredentialProvider) in LabRoot\msi or LabRoot\msi\no-provider - Start-LabRun.ps1 publishes only the one under test'
                return
            }
            $install.PSObject.Properties['error'] | Should -BeNullOrEmpty -Because "$($install.error)"
            $state.Service.State | Should -Be 'Running'
            $state.AutoSignIn.Dll | Should -BeFalse
            $state.AutoSignIn.Provider | Should -BeFalse
            $state.AutoSignIn.InprocServer | Should -BeNullOrEmpty
            $run.rights.autoSignIn | Should -BeFalse
            @($run.features) | Should -Not -Contain 'autoSignIn'
            # the gap: today these are written
            $state.AutoSignIn.Behavior | Should -BeNullOrEmpty -Because 'without the provider the install must not allow automatic sign-in by policy'
            @($state.AutoSignIn.Right | Where-Object { $_ }) | Should -BeNullOrEmpty
            $state.AutoSignIn.Marker | Should -BeFalse
            @($log | Where-Object { $_ -match 'Automatic sign-in allowed on this device' }) | Should -BeNullOrEmpty
        }
    }

    Context 'provider added and removed on an installed agent' {
        BeforeAll {
            $msi = Get-LabAgentMsi
            $hasProvider = Test-MsiFeature $msi 'CredentialProvider'
            if ($hasProvider) {
                Reset-LabVm -Name $VmName
                # installed with automatic sign-in: the policy values are there, so "not touched" means something
                $install = Install-LabAgent -Name $VmName -AutoSignIn $true
                Copy-LabFile -Name $VmName -Path $msi -Destination 'C:\LabHand7'
                $code = (Get-LabAgentState -Name $VmName).Product.Code
                # a maintenance install needs the package under the name it was installed from (else Error 1316):
                # the copy gets the PackageName Windows Installer has for the product
                $guestMsi = Invoke-LabGuest -Name $VmName -ArgumentList ('C:\LabHand7\' + (Split-Path $msi -Leaf)), $code -ScriptBlock {
                    param($copy, $c)
                    $installer = New-Object -ComObject WindowsInstaller.Installer
                    $name = $installer.GetType().InvokeMember('ProductInfo', 'GetProperty', $null, $installer, @($c, 'PackageName'))
                    $target = Join-Path (Split-Path $copy) $name
                    if ($target -ne $copy) { Move-Item -Path $copy -Destination $target -Force }
                    $target
                }
                $h = [ordered]@{}
                $h.before = Get-Snapshot
                $h.remove = Invoke-GuestMsi @('/i', $code, 'REMOVE=CredentialProvider') 'C:\LabHand7\remove.log'
                $h.afterRemove = Get-Snapshot
                $h.add = Invoke-GuestMsi @('/i', "`"$guestMsi`"", 'ADDLOCAL=CredentialProvider') 'C:\LabHand7\add.log'
                $h.afterAdd = Get-Snapshot
                # msiexec's own words for a failure: the lines around 'Return value 3' and the error lines of both logs
                $h.logs = Invoke-LabGuest -Name $VmName -ScriptBlock {
                    foreach ($l in 'C:\LabHand7\remove.log', 'C:\LabHand7\add.log') {
                        [pscustomobject]@{ Log = $l; Lines = @(Select-String -Path $l -Pattern 'Return value 3|Error \d{4}|CustomAction .* returned actual error|failed' -Context 3, 1 -ErrorAction SilentlyContinue | Select-Object -First 8 | ForEach-Object { (@($_.Context.PreContext) + $_.Line) -join ' | ' }) }
                    }
                }
                $h.health = Get-LabAgentHealth -Name $VmName -Pin $install.pin
                Save 'provider-add-remove' $h
            }
        }

        It 'A-HAND-07 REMOVE= / ADDLOCAL=CredentialProvider: 0/3010, DLL and both keys removed / added; same service PID, agent.json unchanged, no new enrollment, policy values untouched' -Tag 'A-HAND-07' {
            if (-not $hasProvider) { Set-ItResult -Skipped -Because 'the agent MSI under test has no CredentialProvider feature (build-installer.ps1 -CredentialProvider)'; return }
            $h.before.Dll | Should -BeTrue
            $h.before.Provider | Should -BeTrue
            $h.before.Behavior | Should -Be 'AutoSignIn'
            # removed
            $h.remove | Should -BeIn 0, 3010
            $h.afterRemove.Dll | Should -BeFalse
            $h.afterRemove.Provider | Should -BeFalse
            $h.afterRemove.Clsid | Should -BeFalse
            # added again
            $h.add | Should -BeIn 0, 3010
            $h.afterAdd.Dll | Should -BeTrue
            $h.afterAdd.Provider | Should -BeTrue
            $h.afterAdd.Clsid | Should -BeTrue
            foreach ($s in $h.afterRemove, $h.afterAdd) {
                $s.ServiceState | Should -Be 'Running'
                $s.ServicePid | Should -Be $h.before.ServicePid
                $s.ConfigHash | Should -Be $h.before.ConfigHash
                $s.EnrollLines | Should -Be $h.before.EnrollLines
                # only RemoteHub's "auto-sign-in on/off" sets them
                $s.Behavior | Should -Be $h.before.Behavior
                $s.Right | Should -Be $h.before.Right
                $s.Marker | Should -Be $h.before.Marker
            }
            $h.health.deviceId | Should -Be $install.deviceId
        }
    }

    Context 'values IT changed after the agent wrote them' {
        BeforeAll {
            $hasProvider = Test-MsiFeature (Get-LabAgentMsi) 'CredentialProvider'
            if ($hasProvider) {
                $u = [ordered]@{}
                # uninstall: IT changed Rights\AutoSignIn
                Reset-LabVm -Name $VmName
                $null = Install-LabAgent -Name $VmName -AutoSignIn $true
                $u.written = Get-PolicyNow
                $null = Invoke-LabGuest -Name $VmName -ScriptBlock { if (-not (Get-LocalGroup 'RH Operators' -ErrorAction SilentlyContinue)) { New-LocalGroup 'RH Operators' } }
                Set-LabAgentPolicy -Name $VmName -Rights @{ AutoSignIn = @('S-1-5-32-544', 'RH Operators') }
                $u.edited = Get-PolicyNow
                $u.uninstall = Uninstall-LabAgent -Name $VmName
                $u.afterUninstall = Get-PolicyNow
                # AUTOSIGNIN=0 (an update through the agent): IT changed NoUserBehavior
                Reset-LabVm -Name $VmName
                $install = Install-LabAgent -Name $VmName -AutoSignIn $true
                Set-LabAgentPolicy -Name $VmName -Values @{ NoUserBehavior = 'SignInScreen' }
                $u.edited0 = Get-PolicyNow
                $rebuilt = New-LabRebuiltMsi -Source (Get-LabAgentMsi) -Path (Join-Path (Out 'rebuilt') 'RemoteHub-Agent-rebuilt-x64.msi')
                $u.update = Update-LabAgent -Name $VmName -Pin $install.pin -Msi $rebuilt.FullName -AutoSignIn $false
                $u.afterOff = Get-PolicyNow
                Save 'it-changed' $u
            }
        }

        It 'A-UNINST-16 a value IT changed survives uninstall and AUTOSIGNIN=0; only the unchanged one is removed, the marker deleted' -Tag 'A-UNINST-16' {
            if (-not $hasProvider) { Set-ItResult -Skipped -Because 'the agent MSI under test has no CredentialProvider feature (AUTOSIGNIN needs build-installer.ps1 -CredentialProvider)'; return }
            $u.written.Behavior | Should -Be 'AutoSignIn'
            $u.written.Right | Should -Be 'S-1-5-32-544'
            $u.written.Marker | Should -BeTrue
            # uninstall
            $u.uninstall.ExitCode | Should -BeIn 0, 3010
            $u.afterUninstall.Right | Should -Be $u.edited.Right -Because 'IT changed Rights\AutoSignIn - it stays'
            $u.afterUninstall.Behavior | Should -BeNullOrEmpty -Because 'NoUserBehavior was unchanged - removed'
            $u.afterUninstall.Marker | Should -BeFalse
            # AUTOSIGNIN=0
            $u.update.verdict | Should -Be 'updated' -Because $u.update.message
            $u.afterOff.Behavior | Should -Be 'SignInScreen' -Because 'IT changed NoUserBehavior - it stays'
            $u.afterOff.Right | Should -BeNullOrEmpty -Because 'Rights\AutoSignIn was unchanged - removed'
            $u.afterOff.Marker | Should -BeFalse
        }
    }

    Context 'what counts toward the sign-in block' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            # rhok: an administrator for the right-password control; rhdeny: administrator, "Deny access to this
            # computer from the network"; rhexp: administrator, account expired (LogonUser 1793, not one the agent maps)
            $null = New-LabGuestUser -Name $VmName -User 'rhok' -Admin
            $null = New-LabGuestUser -Name $VmName -User 'rhdeny' -Admin
            $null = New-LabGuestUser -Name $VmName -User 'rhexp' -Admin
            $null = New-LabGuestUser -Name $VmName -User $vm.UserAccount
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $null = net.exe accounts /lockoutthreshold:0
                Set-LocalUser -Name 'rhexp' -AccountExpires (Get-Date).AddDays(-1)
                $sid = (Get-LocalUser 'rhdeny').SID.Value
                $inf = 'C:\Windows\Temp\rh-rights.inf'; $db = 'C:\Windows\Temp\rh-rights.sdb'
                $null = secedit.exe /export /cfg $inf /areas USER_RIGHTS
                $lines = @(Get-Content $inf)
                $found = $false
                $lines = @($lines | ForEach-Object { if ($_ -match '^SeDenyNetworkLogonRight\s*=') { $found = $true; "$_,*$sid" } else { $_ } })
                if (-not $found) {
                    $i = [array]::IndexOf($lines, '[Privilege Rights]')
                    $lines = @($lines[0..$i]) + @("SeDenyNetworkLogonRight = *$sid") + @($lines[($i + 1)..($lines.Count - 1)])
                }
                Set-Content $inf $lines -Encoding Unicode
                $null = secedit.exe /configure /db $db /cfg $inf /areas USER_RIGHTS /quiet
            }
            $t = [ordered]@{}
            # normaluser: right password, not in an allowed group -> 403
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $t.user5 = Ws $vm.UserAccount $password @{ count = 5 }
            $t.userNext = Ws 'rhok' $password
            # deny network logon: right password, ERROR_LOGON_TYPE_NOT_GRANTED -> 403
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $t.deny5 = Ws 'rhdeny' $password @{ count = 5 }
            $t.denyNext = Ws 'rhok' $password
            # an error the agent doesn't map (500): doesn't count
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $t.exp6 = Ws 'rhexp' $password @{ count = 6 }
            $t.expNext = Ws 'rhok' $password
            Save 'throttle-counts' $t
        }

        It 'S-THR-06 5 refused sign-ins with the right password (normaluser 403, deny-network-logon 403) block the address, the 6th gets 429; unmapped errors (500) don''t count' -Tag 'S-THR-06' {
            Status $t.user5 | Should -Be @(403, 403, 403, 403, 403)
            Status $t.userNext | Should -Be @(429)
            Status $t.deny5 | Should -Be @(403, 403, 403, 403, 403)
            Status $t.denyNext | Should -Be @(429)
            Status $t.exp6 | Should -Be @(500, 500, 500, 500, 500, 500)
            Status $t.expNext | Should -Be @(101)
        }

        It 'S-THR-07 at most 8 Windows sign-ins at once across all addresses; no slot within 30 s -> 503' -Tag 'S-THR-07' {
            # SignInGate: 8 sign-in slots shared by all addresses, at most 5 attempts running per address - 9 at once need
            # at least 2 source addresses, and each Windows sign-in must take longer than 30 s for the 9th to time out.
            Set-ItResult -Skipped -Because 'needs >= 2 source addresses (the harness probe runs on the host only and can''t pick its source address) and Windows sign-ins that stay in LogonUser for more than 30 s (no way to slow them down in the lab)'
        }
    }

    Context 'PowerShell can''t start' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $install = Install-LabAgent -Name $VmName
            $pin = $install.pin
            # one session slot: a slot that isn't released makes the next try 503 instead of 500
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'; $c = Get-Content $f -Raw | ConvertFrom-Json
                $c | Add-Member -NotePropertyName maxSessions -NotePropertyValue 1 -Force; [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
            }
            $null = Restart-LabAgent -Name $VmName -Pin $pin
            $since = Get-LabGuestTime -Name $VmName
            # IFEO Debugger on powershell.exe to a missing file. PowerShell Direct itself may need powershell.exe, so the
            # value removes itself after ~120 s (cmd.exe, not affected) and the guest isn't touched until then.
            $ifeo = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\powershell.exe'
            Invoke-LabGuest -Name $VmName -ArgumentList $ifeo -ScriptBlock {
                param($ifeo)
                $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "cmd.exe /c ping -n 121 127.0.0.1 >nul & reg.exe delete `"HKLM\$ifeo`" /v Debugger /f" }
                $null = New-Item "HKLM:\$ifeo" -Force
                $null = New-ItemProperty "HKLM:\$ifeo" -Name Debugger -Value 'C:\RemoteHubLab\no-such-debugger.exe' -PropertyType String -Force
            }
            $p = [ordered]@{}
            $p.first = Hold
            $p.second = Hold
            Start-Sleep -Seconds 130
            $p.ifeoLeft = Invoke-LabGuest -Name $VmName -ArgumentList $ifeo -ScriptBlock {
                param($ifeo)
                $left = (Get-ItemProperty "HKLM:\$ifeo" -ErrorAction SilentlyContinue).Debugger
                Remove-Item "HKLM:\$ifeo" -Recurse -Force -ErrorAction SilentlyContinue
                $left
            }
            $p.events = @(Get-LabAgentEvent -Name $VmName -Since $since -Id 1000)
            $p.log = @(Get-LabAgentLog -Name $VmName -Since $since)
            $p.after = Hold
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $f = 'C:\ProgramData\RemoteHub\Agent\agent.json'; $c = Get-Content $f -Raw | ConvertFrom-Json; $c.maxSessions = 20; [IO.File]::WriteAllText($f, ($c | ConvertTo-Json -Depth 5))
            }
            Save 'ps-cant-start' $p
        }

        It 'P-SES-10 PowerShell can''t start (IFEO Debugger to a missing file): 500, log "Could not start PowerShell for [acct]", no event 1000, the session slot released' -Tag 'P-SES-10' {
            $p.first.status | Should -Be 500
            $p.second.status | Should -Be 500 -Because 'maxSessions 1: a slot kept by the failed start would answer 503'
            @($p.log | Where-Object { $_ -match "Could not start PowerShell for .*$([regex]::Escape($vm.AdminAccount))" }).Count | Should -Be 2
            @($p.events) | Should -BeNullOrEmpty
            $p.after.status | Should -Be 101
            $p.after.open | Should -BeTrue
        }
    }

    Context 'setup by computer name' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $computer = Invoke-LabGuest -Name $VmName -ScriptBlock { $env:COMPUTERNAME }
            # as typed in RemoteHub: a fully qualified name (the script only names the certificate, nothing resolves it)
            $fqdn = "$computer.rhlab.test".ToLowerInvariant()
            $w = [ordered]@{ Computer = $computer; Fqdn = $fqdn }
            $w.first = Invoke-SetupFor $fqdn
            $w.firstState = Get-LabWinRmState -Name $VmName
            $w.again = Invoke-SetupFor $fqdn
            $w.againState = Get-LabWinRmState -Name $VmName
            $w.short = Invoke-SetupFor $computer
            $w.shortState = Get-LabWinRmState -Name $VmName
            $w.bad = try { Invoke-Harness @('tool', '--op', 'script', '--host', 'bad name!', '--allow', '10.0.0.1', '--https', '1', '--out', (Join-Path $env:TEMP 'rh-bad-name.ps1')); 'accepted' } catch { "$_" }
            Save 'by-name' $w
        }

        It 'W-NAME-01 by computer name: CN=[name], SAN DNS=[fqdn] and DNS=[short]; reused only for the same CN; a name with other characters refused' -Tag 'W-NAME-01' {
            foreach ($r in $w.first, $w.again, $w.short) { $r.Failed | Should -BeNullOrEmpty }
            $c1 = $w.firstState.Certificate
            $c1.Subject | Should -Be "CN=$fqdn"
            $c1.San | Should -Match "=$([regex]::Escape($fqdn))(,|\s|$)"
            $c1.San | Should -Match "=$([regex]::Escape($computer))(,|\s|$)"
            # the same name again: the certificate is reused
            $w.againState.Certificate.Thumbprint | Should -Be $c1.Thumbprint
            @($w.againState.Certificates).Count | Should -Be 1
            # the short name: another CN - a new certificate, SAN only the short name
            $c3 = $w.shortState.Certificate
            $c3.Subject | Should -Be "CN=$computer"
            $c3.Thumbprint | Should -Not -Be $c1.Thumbprint
            $c3.San | Should -Not -Match '\.'
            # refused by RemoteHub before any script exists
            $w.bad | Should -Match "isn't a computer name or IP address"
        }
    }
}
