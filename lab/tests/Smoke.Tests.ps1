# SMOKE - revert, console, accounts, auto-logon. Proves the lab itself works on a VM before any RemoteHub scenario.
# Always from the checkpoint (Reset-LabVm, not Initialize-LabVm): SMOKE-01 checks the state right after the revert.
# Runs on every kind (Console VMs too): every case checks the lab VM itself, none needs the agent.
# LabRole: any
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    Reset-LabVm -Name $VmName
}

AfterAll {
    # auto-logon changed, a session locked and unlocked: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'SMOKE <VmName>' -Tag Smoke {

    It 'SMOKE-01 has the console account signed in after the revert' -Tag 'SMOKE-01' {
        Send-LabKey -Name $VmName -Key ControlKey   # wake the display
        Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 120
        Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'smoke-console.png') | Out-Null
    }

    It 'SMOKE-02 answers PowerShell Direct as the admin account with a full admin token' -Tag 'SMOKE-02' {
        $admins = Invoke-LabGuest -Name $VmName -ScriptBlock {
            whoami /groups /fo csv | ConvertFrom-Csv | Where-Object SID -eq 'S-1-5-32-544'
        }
        $admins.Attributes | Should -Match 'Enabled group'
    }

    It 'SMOKE-03 has a standard user that is not an administrator' -Tag 'SMOKE-03' {
        $members = Invoke-LabGuest -Name $VmName -ScriptBlock {
            # by SID, so it works on localized Windows
            @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object { ($_.Name -split '\\')[-1] })
        }
        $members | Should -Contain $vm.AdminAccount
        $members | Should -Not -Contain $vm.UserAccount
    }

    It 'SMOKE-04 reports an IPv4 address to Hyper-V' -Tag 'SMOKE-04' {
        Get-LabVmAddress -Name $VmName | Should -Match '^\d{1,3}(\.\d{1,3}){3}$'
    }

    Context 'user signed in by auto-logon' {
        BeforeAll { Set-LabAutoLogon -Name $VmName -Account User }

        It 'SMOKE-05 reaches the desktop' -Tag 'SMOKE-05' {
            $shot = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'smoke-desktop.png')
            $shot.IsBlank | Should -BeFalse
        }

        It 'SMOKE-06 runs code in the user session' -Tag 'SMOKE-06' {
            $out = Invoke-LabInSession -Name $VmName -Account User -ScriptBlock {
                "$env:USERNAME session=$((Get-Process -Id $PID).SessionId)"
            }
            $out | Should -Match "^$([regex]::Escape($vm.UserAccount)) session=[1-9]"
        }

        It 'SMOKE-07 locks and unlocks like a person' -Tag 'SMOKE-07' {
            Lock-LabSession -Name $VmName
            Start-Sleep -Seconds 3   # the lock screen takes a moment to appear
            # LogonUI in the user's own session (another session may sit at a lock screen anyway)
            Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u)
                $sessions = @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" |
                        Where-Object { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $u } |
                        ForEach-Object SessionId)
                [bool](Get-Process LogonUI -ErrorAction SilentlyContinue | Where-Object { $sessions -contains $_.SessionId })
            } | Should -BeTrue
            Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'smoke-locked.png') | Out-Null
            Unlock-LabSession -Name $VmName
        }
    }

    It 'SMOKE-08 has the guest clock within 2 minutes of the host' -Tag 'SMOKE-08' {
        $guest = Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-Date).ToUniversalTime() }
        [math]::Abs(((Get-Date).ToUniversalTime() - $guest).TotalSeconds) | Should -BeLessOrEqual 120
    }

    It 'SMOKE-09 has no reboot pending and Windows Update idle (else a warning in the report)' -Tag 'SMOKE-09' {
        $state = Invoke-LabGuest -Name $VmName -ScriptBlock {
            $until = (Get-Date).AddMinutes(5)
            while (@(Get-Process TiWorker -ErrorAction SilentlyContinue).Count -gt 0 -and (Get-Date) -lt $until) { Start-Sleep -Seconds 10 }
            [pscustomobject]@{
                RebootPending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
                    (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
                Busy = @(Get-Process TiWorker -ErrorAction SilentlyContinue).Count -gt 0
            }
        }
        $state | ConvertTo-Json | Set-Content (Join-Path $EvidenceDir 'smoke-pending.json') -Encoding UTF8
        # a checkpoint with an update half done makes later scenarios flaky: the report shows it as skipped with the reason
        if ($state.RebootPending -or $state.Busy) {
            Set-ItResult -Skipped -Because "the checkpoint has $(if ($state.RebootPending) { 'a reboot pending' } else { 'Windows Update still busy after 5 min' }) - refresh it"
        }
    }

    It 'SMOKE-10 every account and the sign-in screen have only the image''s keyboard; console typing round-trips a-z A-Z 0-9 and !@#$%' -Tag 'SMOKE-10' {
        $text = 'abcxyzABCXYZ0189 !@#$%'
        $file = 'C:\ProgramData\RemoteHubLab\session\smoke-typed.txt'
        # Send-LabText sends shifted symbols as US key positions, and the tests type paths and the password at the
        # console in any account and at the sign-in screen: one German layout anywhere breaks them (docs/test-lab-spec.md 5.1)
        $want = if ($vm.Id -like '*de-DE*') { '00000407' } else { '00000409' }
        # each profile's layouts: the language list's input methods (lang:layout) and Preload with its Substitutes;
        # profiles not signed in are loaded from their NTUSER.DAT for the read
        $layouts = @(Invoke-LabGuest -Name $VmName -ScriptBlock {
            function Read-Layouts([string]$Root, [string]$Who) {
                $ids = @()
                $up = "Registry::$Root\Control Panel\International\User Profile"
                foreach ($k in @(Get-ChildItem $up -ErrorAction SilentlyContinue)) {
                    $ids += @($k.Property | Where-Object { $_ -match '^[0-9A-Fa-f]{4}:([0-9A-Fa-f]{8})$' } | ForEach-Object { $_.Split(':')[1] })
                }
                $pre = Get-ItemProperty "Registry::$Root\Keyboard Layout\Preload" -ErrorAction SilentlyContinue
                $sub = Get-ItemProperty "Registry::$Root\Keyboard Layout\Substitutes" -ErrorAction SilentlyContinue
                if ($pre) {
                    foreach ($p in @($pre.PSObject.Properties | Where-Object Name -match '^\d+$')) {
                        $id = [string]$p.Value
                        if ($sub -and $sub.PSObject.Properties[$id]) { $id = [string]$sub.$id }
                        $ids += $id
                    }
                }
                [pscustomobject]@{ Account = $Who; Layouts = @($ids | ForEach-Object { $_.ToUpper() } | Sort-Object -Unique) }
            }
            Read-Layouts 'HKEY_USERS\.DEFAULT' '(sign-in screen)'
            foreach ($p in @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' | Where-Object PSChildName -like 'S-1-5-21-*')) {
                $sid = $p.PSChildName
                $who = try { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value } catch { $null }
                # the profile of an account deleted since: nobody signs in with it
                if (-not $who) { continue }
                if (Test-Path "Registry::HKEY_USERS\$sid") { Read-Layouts "HKEY_USERS\$sid" $who; continue }
                $hive = Join-Path $p.GetValue('ProfileImagePath') 'NTUSER.DAT'
                if (-not (Test-Path $hive)) { continue }
                $null = & reg.exe load 'HKU\RhLabLayout' $hive 2>&1
                if ($LASTEXITCODE -ne 0) { [pscustomobject]@{ Account = $who; Layouts = @('unreadable') }; continue }
                try { Read-Layouts 'HKEY_USERS\RhLabLayout' $who }
                finally { [gc]::Collect(); [gc]::WaitForPendingFinalizers(); $null = & reg.exe unload 'HKU\RhLabLayout' 2>&1 }
            }
        })
        $layouts | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $EvidenceDir 'smoke-layout.json') -Encoding UTF8
        # a profile without any layout (never signed in) gets the default one, so it isn't counted
        $other = @($layouts | Where-Object { @($_.Layouts | Where-Object { $_ -ne $want }).Count -gt 0 })
        Set-LabAutoLogon -Name $VmName -Account User
        # Notepad in the user's session, typed at the console, saved with Ctrl+S to a known file
        $task = Start-LabInSession -Name $VmName -ScriptBlock ([scriptblock]::Create("New-Item -ItemType File -Path '$file' -Force | Out-Null; notepad.exe '$file'"))
        # the console's keys go to the foreground window: Notepad's must exist and be in front first
        $ready = Invoke-LabInSession -Name $VmName -ScriptBlock {
            $wsh = New-Object -ComObject WScript.Shell
            $until = (Get-Date).AddSeconds(60)
            do {
                if (@(Get-Process notepad -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0).Count -gt 0 -and $wsh.AppActivate('smoke-typed')) { 'ready'; return }
                Start-Sleep -Milliseconds 500
            } while ((Get-Date) -lt $until)
            'timeout'
        }
        if ("$ready" -notmatch 'ready') { throw "Notepad with smoke-typed.txt didn't come to the front within 60 s" }
        Start-Sleep -Milliseconds 500
        Send-LabText -Name $VmName -Text $text
        $typed = Invoke-LabInSession -Name $VmName -ScriptBlock {
            $wsh = New-Object -ComObject WScript.Shell
            $null = $wsh.AppActivate('Notepad'); Start-Sleep -Milliseconds 500
            $wsh.SendKeys('^s'); Start-Sleep -Seconds 2
            Get-Content 'C:\ProgramData\RemoteHubLab\session\smoke-typed.txt' -Raw
        }
        Stop-LabInSession -Name $VmName -Task $task
        Clear-LabAutoLogon -Name $VmName
        "$typed".Trim() | Should -Be $text
        @($other | ForEach-Object { "$($_.Account): $($_.Layouts -join ', ')" }) | Should -BeNullOrEmpty -Because "every account and the sign-in screen should have only $want"
    }
}
