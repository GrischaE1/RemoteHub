# SMOKE - revert, console, accounts, auto-logon. Proves the lab itself works on a VM before any RemoteHub scenario.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    Reset-LabVm -Name $VmName
}

Describe 'SMOKE <VmName>' -Tag Smoke {

    It 'has the console account signed in after the revert' {
        Send-LabKey -Name $VmName -Key ControlKey   # wake the display
        Wait-LabDesktop -Name $VmName -Account Console -TimeoutSec 120
        Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'smoke-console.png') | Out-Null
    }

    It 'answers PowerShell Direct as the admin account with a full admin token' {
        $admins = Invoke-LabGuest -Name $VmName -ScriptBlock {
            whoami /groups /fo csv | ConvertFrom-Csv | Where-Object SID -eq 'S-1-5-32-544'
        }
        $admins.Attributes | Should -Match 'Enabled group'
    }

    It 'has a standard user that is not an administrator' {
        $members = Invoke-LabGuest -Name $VmName -ScriptBlock {
            # by SID, so it works on localized Windows
            @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object { ($_.Name -split '\\')[-1] })
        }
        $members | Should -Contain $vm.AdminAccount
        $members | Should -Not -Contain $vm.UserAccount
    }

    It 'reports an IPv4 address to Hyper-V' {
        Get-LabVmAddress -Name $VmName | Should -Match '^\d{1,3}(\.\d{1,3}){3}$'
    }

    Context 'user signed in by auto-logon' {
        BeforeAll { Set-LabAutoLogon -Name $VmName -Account User }

        It 'reaches the desktop' {
            $shot = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir 'smoke-desktop.png')
            $shot.IsBlank | Should -BeFalse
        }

        It 'runs code in the user session' {
            $out = Invoke-LabInSession -Name $VmName -Account User -ScriptBlock {
                "$env:USERNAME session=$((Get-Process -Id $PID).SessionId)"
            }
            $out | Should -Match "^$([regex]::Escape($vm.UserAccount)) session=[1-9]"
        }

        It 'locks and unlocks like a person' {
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
}
