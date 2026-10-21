# S-SCRIPT - running a script on a device from the Scripts view (docs/scripts-view-design.md section 6), through
# RemoteHub's ScriptRunner in the harness: as SYSTEM, as the signed-in user and as the connection's account, over the
# RemoteHub Agent and over WinRM HTTPS; exit codes, parameters, cancel, cleanup, nobody signed in, a GPO-set execution
# policy. What a run leaves on the device is checked over PowerShell Direct. See docs/test-lab-cases.md section 7.2.
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "s-script-$name.json") -Encoding UTF8 }
}

AfterAll {
    # tasks run, policies set, a user signed in: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'S-SCRIPT <VmName>' -Tag Scripts, 'S-SCRIPT' {

    Context 'over the agent, with a user signed in' {
        BeforeAll {
            $pin = (Initialize-LabVm -Name $VmName -Agent -AutoLogon User).Pin
            Wait-LabDesktop -Name $VmName -Account User -TimeoutSec 300
            function Run([string]$Context, [string]$Body, [hashtable]$More = @{}) {
                Invoke-LabScriptRun -Name $VmName -Via Agent -Pin $pin -Context $Context -Body $Body @More
            }
        }

        It 'S-SCRIPT-01 a System run prints NT AUTHORITY\SYSTEM and leaves no task or folder behind' -Tag 'S-SCRIPT-01' {
            $r = Run System 'whoami; [Environment]::Is64BitProcess'
            Save 'system-agent' $r
            $r.result.state | Should -Be 'Succeeded'
            $r.result.exitCode | Should -Be 0
            $r.result.output | Should -Match 'nt authority\\system'
            $r.result.output | Should -Match 'True'
            $left = Get-LabScriptLeftovers -Name $VmName
            Save 'system-agent-leftovers' $left
            @($left.Tasks) | Should -BeNullOrEmpty
            @($left.Folders) | Should -BeNullOrEmpty
        }

        It 'S-SCRIPT-02 the signed-in user is the console user, and a User run runs as them with their HKCU' -Tag 'S-SCRIPT-02' {
            $info = Invoke-LabTool -Name $VmName -Op 'script-info' -Via Agent -Pin $pin
            Save 'info' $info
            $console = @($info.result.users | Where-Object IsConsole)
            $console.Count | Should -Be 1
            $console[0].User | Should -Match ([regex]::Escape($vm.UserAccount) + '$')
            $r = Run User 'whoami; (Get-ItemProperty -Path "HKCU:\Volatile Environment").USERNAME; New-ItemProperty -Path HKCU:\Software -Name RemoteHubLab -Value 42 -Force | Out-Null'
            Save 'user-agent' $r
            $r.result.state | Should -Be 'Succeeded'
            $r.result.output | Should -Match ([regex]::Escape($vm.UserAccount))
            $r.result.output | Should -Not -Match 'nt authority\\system'
            # written to the user's own hive, not the admin's
            $sid = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u) (New-Object Security.Principal.NTAccount($u)).Translate([Security.Principal.SecurityIdentifier]).Value
            }
            Invoke-LabGuest -Name $VmName -ArgumentList $sid -ScriptBlock { param($s) (Get-ItemProperty -Path "Registry::HKEY_USERS\$s\Software").RemoteHubLab } | Should -Be 42
            @((Get-LabScriptLeftovers -Name $VmName).Tasks) | Should -BeNullOrEmpty
        }

        It 'S-SCRIPT-03 an As me run runs as the connection account' -Tag 'S-SCRIPT-03' {
            $r = Run AsMe 'whoami'
            Save 'asme-agent' $r
            $r.result.state | Should -Be 'Succeeded'
            $r.result.output | Should -Match ([regex]::Escape($vm.AdminAccount))
        }

        It 'S-SCRIPT-04 exit codes, a thrown error and parameters reach RemoteHub as they should' -Tag 'S-SCRIPT-04' {
            (Run System 'exit 3010').result.state | Should -Be 'SucceededRestart'
            $failed = Run System "Write-Output 'before'; throw 'lab failure'"
            $failed.result.state | Should -Be 'Failed'
            $failed.result.exitCode | Should -Be 1
            $failed.result.output | Should -Match 'lab failure'
            $p = Run System 'param([string]$Name, [int]$Days, [switch]$Apply) "$Name/$Days/$Apply"' @{ ParamsJson = '{"Name":"SRV 01","Days":45,"Apply":true}' }
            Save 'params' $p
            $p.result.output | Should -Match 'SRV 01/45/True'
        }

        It 'S-SCRIPT-05 Cancel stops the script and removes its task and folder' -Tag 'S-SCRIPT-05' {
            $r = Run System "Write-Output 'started'; Start-Sleep -Seconds 600" @{ CancelAfterSec = 5 }
            Save 'cancel' $r
            $r.result.state | Should -Be 'Cancelled'
            [double]$r.seconds | Should -BeLessThan 120
            $left = Get-LabScriptLeftovers -Name $VmName
            @($left.Tasks) | Should -BeNullOrEmpty
            @($left.Folders) | Should -BeNullOrEmpty
            # the sleeping powershell.exe is gone too
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                @(Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe'" | Where-Object { $_.CommandLine -match 'RemoteHub\\Runs' }).Count
            } | Should -Be 0
        }

        It 'S-SCRIPT-06 output over 1 MB is cut off and the run still ends' -Tag 'S-SCRIPT-06' {
            $r = Run System "1..40000 | ForEach-Object { 'x' * 60 }"
            $r.result.state | Should -Be 'Succeeded'
            $r.result.cut | Should -Be $true
            $r.result.output | Should -Match 'Output cut off after 1 MB'
        }
    }

    Context 'over WinRM, nobody signed in' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            # the checkpoint has Admin signed in at the console: signed out, so nobody is (run 20261021-062735 ran the
            # "signed-in user" script as Admin)
            Disconnect-LabConsole -Name $VmName
            $setup = Invoke-LabSetupScript -Name $VmName
            $cert = (Get-LabWinRmState -Name $VmName).Certificate.Thumbprint
            Save 'winrm-setup' @{ Setup = $setup; Certificate = $cert }
            function Run([string]$Context, [string]$Body) {
                Invoke-LabScriptRun -Name $VmName -Via WinRM -Cert $cert -Context $Context -Body $Body
            }
        }

        It 'S-SCRIPT-07 a System run works over WinRM and leaves nothing behind' -Tag 'S-SCRIPT-07' {
            $r = Run System 'whoami'
            Save 'system-winrm' $r
            $r.result.state | Should -Be 'Succeeded'
            $r.result.output | Should -Match 'nt authority\\system'
            $left = Get-LabScriptLeftovers -Name $VmName
            @($left.Tasks) | Should -BeNullOrEmpty
            @($left.Folders) | Should -BeNullOrEmpty
        }

        It 'S-SCRIPT-08 a User run with nobody signed in is refused with RemoteHub''s message' -Tag 'S-SCRIPT-08' {
            $r = Run User 'whoami'
            Save 'user-nobody' $r
            $r.result.state | Should -Be 'Refused'
            $r.result.message | Should -Be 'No user is signed in on the device.'
        }

        It 'S-SCRIPT-09 a GPO-set AllSigned execution policy refuses the run and isn''t worked around' -Tag 'S-SCRIPT-09' {
            Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\PowerShell'
                $null = New-Item -Path $k -Force
                Set-ItemProperty -Path $k -Name EnableScripts -Value 1 -Type DWord
                Set-ItemProperty -Path $k -Name ExecutionPolicy -Value 'AllSigned' -Type String
            }
            try {
                $r = Run System 'whoami'
                Save 'allsigned' $r
                $r.result.state | Should -Be 'Refused'
                $r.result.message | Should -Be "The device's group policy only allows signed scripts."
                @((Get-LabScriptLeftovers -Name $VmName).Tasks) | Should -BeNullOrEmpty
            }
            finally {
                Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\PowerShell' -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
    }
}
