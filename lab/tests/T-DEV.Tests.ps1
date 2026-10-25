# T-DEV - a connection's Device actions (who is signed in, send a message, lock, sign out, update Group Policy, sync
# with Intune, restart, shut down) through the same code the connection's menu uses (DeviceActions), once over the
# RemoteHub Agent and once over WinRM HTTPS set up with RemoteHub's script, with the lab user signed in at the console.
# Results are checked on the device over PowerShell Direct (WTS sessions, the System log), on its screen (OCR) and on
# the Hyper-V host (the VM's state). See docs/test-lab-cases.md section 7.3.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "t-dev-$name.json") -Encoding UTF8 }
    # the console user's sessions (the one automatic sign-in created)
    # (",": the array as it is - PowerShell would hand back $null for none and the object itself for one)
    function Mine { , @(Get-LabSessions -Name $VmName | Where-Object { $_.User -match "\\$([regex]::Escape($vm.UserAccount))$" }) }
    # Polls a condition until it holds; the timeout says what didn't happen.
    function Wait-Until([scriptblock]$Condition, [string]$What, [int]$TimeoutSec = 90) {
        $until = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $r = $null
            try { $r = & $Condition } catch { Write-Verbose $_.Exception.Message }
            if ($r) { return $r }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $until)
        throw "Timed out after $TimeoutSec s waiting for $What."
    }
    function Get-BootTime { Invoke-LabGuest -Name $VmName -ScriptBlock { (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o') } }
}

AfterAll {
    # sessions signed out, Group Policy applied, the device restarted and shut down: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'T-DEV <VmName>' -Tag Tools, 'T-DEV' {

    Context 'over <Via>' -ForEach @(@{ Via = 'Agent' }, @{ Via = 'WinRM' }) {
        BeforeAll {
            $pin = $null
            $cert = $null
            if ($Via -eq 'Agent') {
                $pin = (Initialize-LabVm -Name $VmName -Agent -AutoLogon User).Pin
            }
            else {
                Reset-LabVm -Name $VmName
                # RemoteHub's setup script, the session pinned to the listener's certificate (as in T-TOOLS)
                $null = Invoke-LabSetupScript -Name $VmName
                $cert = (Get-LabWinRmState -Name $VmName).Certificate.Thumbprint
                Set-LabAutoLogon -Name $VmName -Account User
            }
            function T([string]$Op, [hashtable]$P = @{}, [string]$UserName) {
                $P = $P.Clone()
                if ($Via -eq 'WinRM') { $P.cert = $cert }
                if ($UserName) { Invoke-LabTool -Name $VmName -Op $Op -Via $Via -Pin $pin -Params $P -UserName $UserName }
                else { Invoke-LabTool -Name $VmName -Op $Op -Via $Via -Pin $pin -Params $P }
            }
            function Act([string]$Action, [hashtable]$P = @{}) {
                $P = $P.Clone()
                $P.action = $Action
                $P.device = $VmName
                T 'dev-action' $P
            }
            $tag = $Via.ToLowerInvariant()
        }

        It 'T-DEV-01 [<Via>] lists the console user with their session, nothing of Windows own' -Tag 'T-DEV-01' {
            $r = T 'dev-users'
            Save "$tag-users" $r
            $r.refused | Should -BeNullOrEmpty
            $console = @(Mine | Where-Object { $_.Console -and $_.Explorer.Count -gt 0 })
            $console.Count | Should -Be 1 -Because 'automatic sign-in made one console session'
            $listed = @($r.result.users | Where-Object { $_.SessionId -eq $console[0].Id })
            $listed.Count | Should -Be 1
            $listed[0].Account | Should -Match "\\$([regex]::Escape($vm.UserAccount))$"
            @($r.result.users | Where-Object { $_.Account -match '\\(DWM|UMFD)-\d+$' }) | Should -BeNullOrEmpty
            @($r.result.users | Where-Object { $_.SessionId -le 0 }) | Should -BeNullOrEmpty -Because 'the remoting session itself runs in session 0'
        }

        It 'T-DEV-02 [<Via>] shows a message on the signed-in user''s screen' -Tag 'T-DEV-02' {
            $r = Act 'SendMessage' @{ message = "RemoteHub lab message Bluebird $tag" }
            Save "$tag-message" $r
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            $r.result.text | Should -Match "^Message shown on $([regex]::Escape($VmName)) to "
            $shot = Join-Path $EvidenceDir "t-dev-$tag-message.png"
            $null = Wait-Until { $null = Get-LabScreenshot -Name $VmName -Path $shot; Find-LabPictureText -Path $shot -Text 'Bluebird' } 'the message on the console' 60
        }

        It 'T-DEV-03 [<Via>] locks the console session: disconnected, the same desktop kept' -Tag 'T-DEV-03' {
            $before = @(Mine | Where-Object { $_.Explorer.Count -gt 0 })
            $before.Count | Should -BeGreaterThan 0
            $r = Act 'Lock'
            Save "$tag-lock" @{ Before = $before; Result = $r }
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            $r.result.text | Should -Match '^Locked on '
            $after = Wait-Until { $s = @(Mine | Where-Object { $_.Id -eq $before[0].Id -and $_.State -eq 'Disconnected' }); if ($s.Count -eq 1) { , $s } } 'the session to be disconnected' 60
            (@($after[0].Explorer) -join ',') | Should -Be (@($before[0].Explorer) -join ',') -Because 'locking keeps the user''s programs'
            @(Get-LabSessions -Name $VmName | Where-Object { $_.Console -and $_.LogonUI }).Count | Should -Be 1 -Because 'the console shows the sign-in screen'
        }

        It 'T-DEV-04 [<Via>] signs the user out: the session and its programs end' -Tag 'T-DEV-04' {
            $before = @(Mine | Where-Object { $_.Explorer.Count -gt 0 })
            $before.Count | Should -BeGreaterThan 0
            $pids = @($before | ForEach-Object { $_.Explorer })
            $r = Act 'SignOut'
            Save "$tag-signout" @{ Before = $before; Result = $r }
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            $r.result.text | Should -Match '^Signed out on '
            # automatic sign-in may sign the user in again in a new session: the old session's programs are what must end
            $null = Wait-Until {
                $left = @(Mine | Where-Object { $s = $_; @($s.Explorer | Where-Object { $pids -contains $_ }).Count -gt 0 -or ($before.Id -contains $s.Id -and $s.State -eq 'Disconnected') })
                $left.Count -eq 0
            } 'the signed-out session to end' 90
        }

        It 'T-DEV-05 [<Via>] updates Group Policy and reports gpupdate''s answer' -Tag 'T-DEV-05' {
            $since = Get-LabGuestTime -Name $VmName
            $r = Act 'GroupPolicyUpdate'
            Save "$tag-gpupdate" $r
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            $r.result.text | Should -Match '^Group Policy was updated on '
            # Group Policy's own log: a manual run (4004 computer, 4005 user) that started after the request
            $runs = Invoke-LabGuest -Name $VmName -ArgumentList $since -ScriptBlock {
                param($t)
                @(Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-GroupPolicy/Operational'; Id = 4004, 4005; StartTime = $t } -ErrorAction SilentlyContinue).Count
            }
            $runs | Should -BeGreaterThan 0
        }

        It 'T-DEV-06 [<Via>] syncing a device that isn''t enrolled in Intune says so' -Tag 'T-DEV-06' {
            $enrolled = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ScheduledTask -TaskPath '\Microsoft\Windows\EnterpriseMgmt\*' -ErrorAction SilentlyContinue).Count -gt 0 }
            $r = Act 'IntuneSync'
            Save "$tag-intune" @{ Enrolled = $enrolled; Result = $r }
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $enrolled
            if (-not $enrolled) { $r.result.text | Should -Match "isn't enrolled in Intune" }
        }

        It 'T-DEV-07 [<Via>] an account with the PowerShell right but no administrator''s rights is told why' -Tag 'T-DEV-07' -Skip:($Via -ne 'Agent') {
            $null = New-LabGuestUser -Name $VmName -User 'rhdevstd'
            # Administrators keep the right; local Users get it too
            Set-LabAgentPolicy -Name $VmName -Rights @{ PowerShell = @('S-1-5-32-544', 'S-1-5-32-545') }
            $r = T 'dev-users' -UserName 'rhdevstd'
            Save "$tag-standard-user" $r
            $r.refused | Should -Match "isn't an administrator on the device"
        }

        It 'T-DEV-08 [<Via>] restarts the device: a new boot, the request in the System log' -Tag 'T-DEV-08' {
            $before = Get-BootTime
            $since = Get-LabGuestTime -Name $VmName
            $r = Act 'Restart'
            Save "$tag-restart" $r
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            $null = Wait-Until { Wait-LabVm -Name $VmName -TimeoutSec 120; (Get-BootTime) -ne $before } 'the device to start again' 600
            $e = Invoke-LabGuest -Name $VmName -ArgumentList $since -ScriptBlock {
                param($t)
                @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1074; StartTime = $t } -ErrorAction SilentlyContinue | ForEach-Object Message)
            }
            Save "$tag-restart-events" $e
            ($e -join "`n") | Should -Match 'Requested from RemoteHub'
            if ($Via -eq 'Agent') { $null = Wait-LabAgent -Name $VmName -Pin $pin }
            Wait-LabDesktop -Name $VmName -Account User
        }

        It 'T-DEV-09 [<Via>] shuts the device down: the VM is off' -Tag 'T-DEV-09' {
            $r = Act 'ShutDown'
            Save "$tag-shutdown" $r
            $r.refused | Should -BeNullOrEmpty
            $r.result.ok | Should -Be $true
            # Msvm_ComputerSystem.EnabledState 3 = off
            $null = Wait-Until { (Get-LabCimVm -Name $VmName).EnabledState -eq 3 } 'the VM to be off' 300
        }
    }
}
