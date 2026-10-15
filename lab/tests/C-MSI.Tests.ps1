# C-MSI - RemoteHub's own package on a Windows client: silent install (files, shortcut, the bundled agent MSI), the
# first start without .NET, the .rdp association, upgrade from the previous release and from RDP Manager (same upgrade
# code), downgrade, uninstall, a standard user. The packages come from LabRoot\client (Start-LabRun.ps1). Client VMs
# only. See docs/test-lab-cases.md section 10.1.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = (Get-LabVm $VmName).Os -ne 'Server'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "c-msi-$name.json") -Encoding UTF8 }
    $msi = Get-LabClientMsi
    $version = (Split-Path $msi -Leaf) -replace '^RemoteHub-(.+)-x64\.msi$', '$1'
    # msiexec in the guest as PSAdmin: exit code and the product as Windows lists it
    function Install-Client([string]$Msi, [string]$Args = '') {
        Copy-LabFile -Name $VmName -Path $Msi -Destination 'C:\LabClient'
        Invoke-LabGuest -Name $VmName -ArgumentList (Split-Path $Msi -Leaf), $Args -ScriptBlock {
            param($leaf, $extra)
            $log = "C:\LabClient\$leaf.log"
            $p = Start-Process msiexec.exe -ArgumentList "/i `"C:\LabClient\$leaf`" /qn /norestart /l*v `"$log`" $extra" -Wait -PassThru
            [pscustomobject]@{ Exit = $p.ExitCode; Log = @(Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -match 'newer version|Return value 3|Error \d{4}' } | Select-Object -Last 8) }
        }
    }
    function Get-ClientState {
        Invoke-LabGuest -Name $VmName -ScriptBlock {
            $exe = 'C:\Program Files\RemoteHub\RemoteHub.exe'
            $arp = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^(RemoteHub|RDP Manager)( |$)' -and $_.DisplayName -notmatch 'Agent' })
            [pscustomobject]@{
                Exe        = Test-Path $exe
                Version    = $(if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion })
                Shortcut   = Test-Path 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\RemoteHub.lnk'
                BundledSha = $(Get-ChildItem 'C:\Program Files\RemoteHub' -Recurse -Filter 'RemoteHub-Agent*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
                ProgId     = Test-Path 'Registry::HKEY_CLASSES_ROOT\RemoteHub.rdp\shell\open\command'
                OpenWith   = [bool](Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\.rdp\OpenWithProgids' -ErrorAction SilentlyContinue).PSObject.Properties['RemoteHub.rdp']
                RegApp     = [bool](Get-ItemProperty 'HKLM:\SOFTWARE\RegisteredApplications' -ErrorAction SilentlyContinue).PSObject.Properties['RemoteHub']
                RdpDefault = (Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\.rdp' -ErrorAction SilentlyContinue).'(default)'
                Arp        = @($arp | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" })
                Folder     = Test-Path 'C:\Program Files\RemoteHub'
            }
        }
    }
}

Describe 'C-MSI <VmName>' -Tag Client, 'C-MSI' -Skip:(-not $isClient) {

    Context 'install, start, association, uninstall' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $dotnet = Invoke-LabGuest -Name $VmName -ScriptBlock { @(Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -ErrorAction SilentlyContinue).Count }
            $install = Install-Client $msi
            $state = Get-ClientState
            $agentSha = (Get-FileHash (Get-LabAgentMsi) -Algorithm SHA256).Hash
            # the first start in normaluser's session: the main window, no runtime prompt
            Set-LabAutoLogon -Name $VmName -Account User
            $task = Start-LabInSession -Name $VmName -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' }
            $main = Invoke-LabPerson -Name $VmName -Do uia -Title 'RemoteHub*' -Text '*' -Value exists -TimeoutSec 90
            $prompt = Invoke-LabPerson -Name $VmName -Do uia -Title '*' -Text '*NET*runtime*' -Value exists -TimeoutSec 5
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            # uninstall
            $uninstall = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $code = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'RemoteHub' }).PSChildName
                (Start-Process msiexec.exe -ArgumentList '/x', $code, '/qn', '/norestart' -Wait -PassThru).ExitCode
            }
            $after = Get-ClientState
            $userData = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock { param($u) Test-Path "C:\Users\$u\AppData\Local\RemoteHub" }
            Clear-LabAutoLogon -Name $VmName
            Save 'install' @{ Install = $install; State = $state; Main = $main; Prompt = $prompt; Uninstall = $uninstall; After = $after; UserData = $userData; DotNet = $dotnet }
        }

        It 'C-MSI-01 silent install: exit 0, RemoteHub.exe of the package''s version, Start menu shortcut, the bundled agent MSI identical' -Tag 'C-MSI-01' {
            $install.Exit | Should -BeIn 0, 3010
            $state.Exe | Should -BeTrue
            $state.Version | Should -Match "^$([regex]::Escape($version))"
            $state.Shortcut | Should -BeTrue
            $state.BundledSha | Should -Be $agentSha
        }

        It 'C-MSI-02 starts without .NET installed: the main window, no runtime prompt' -Tag 'C-MSI-02' {
            $dotnet | Should -Be 0 -Because 'the VM has no .NET desktop runtime'
            $main.found | Should -BeTrue
            $prompt.found | Should -BeFalse
        }

        It 'C-MSI-03 .rdp: RemoteHub in Open with and Default apps; mstsc stays the default' -Tag 'C-MSI-03' {
            $state.ProgId | Should -BeTrue
            $state.OpenWith | Should -BeTrue
            $state.RegApp | Should -BeTrue
            $state.RdpDefault | Should -Be 'RDP.File'
        }

        It 'C-MSI-07 uninstall: program folder, shortcut and ProgId gone; user data kept' -Tag 'C-MSI-07' {
            $uninstall | Should -BeIn 0, 3010
            $after.Folder | Should -BeFalse
            $after.Shortcut | Should -BeFalse
            $after.ProgId | Should -BeFalse
            $userData | Should -BeTrue
        }
    }

    Context 'upgrade, downgrade' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $u = [ordered]@{}
            $u.previous = Install-Client (Get-LabClientMsi -Previous)
            # data of the previous version: a connection made by starting it with an .rdp file
            $null = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u) Set-Content "C:\Users\$u\Desktop\lab-device.rdp" "full address:s:lab-device`r`nusername:s:labuser" -Encoding ASCII
            }
            Set-LabAutoLogon -Name $VmName -Account User
            $task = Start-LabInSession -Name $VmName -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' "$env:USERPROFILE\Desktop\lab-device.rdp" }
            $u.prevWindow = Invoke-LabPerson -Name $VmName -Do uia -Title 'RemoteHub*' -Text '*lab-device*' -Value exists -TimeoutSec 90
            Start-Sleep -Seconds 5
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep 5; Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            $u.upgrade = Install-Client $msi
            $u.state = Get-ClientState
            $task = Start-LabInSession -Name $VmName -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' }
            $u.kept = Invoke-LabPerson -Name $VmName -Do uia -Title 'RemoteHub*' -Text '*lab-device*' -Value exists -TimeoutSec 90
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            $u.downgrade = Install-Client (Get-LabClientMsi -Previous)
            $u.afterDowngrade = Get-ClientState
            Clear-LabAutoLogon -Name $VmName
            # from RDP Manager (same upgrade code)
            Reset-LabVm -Name $VmName
            $u.legacy = Install-Client (Get-LabClientMsi -Legacy)
            # RDP Manager's settings folder, with a marker the copy must carry over
            $null = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u) $d = "C:\Users\$u\AppData\Roaming\RdpManager"; $null = New-Item -ItemType Directory -Path $d -Force
                Set-Content "$d\lab-marker.txt" 'from RDP Manager' -Encoding ASCII
            }
            $u.fromLegacy = Install-Client $msi
            if ($u.fromLegacy.Exit -notin 0, 3010) {
                # documented path (README): 2.x is numbered above RemoteHub, so it is uninstalled first
                $u.legacyUninstall = Invoke-LabGuest -Name $VmName -ScriptBlock {
                    $e = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
                        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^RDP Manager( |$)' } | Select-Object -First 1
                    (Start-Process msiexec.exe -ArgumentList "/x $($e.PSChildName) /qn /norestart" -Wait -PassThru).ExitCode
                }
                $u.afterUninstall = Install-Client $msi
            }
            $u.legacyState = Get-ClientState
            # the first start as the user copies the folder
            Set-LabAutoLogon -Name $VmName -Account User
            $task = Start-LabInSession -Name $VmName -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' }
            $u.legacyWindow = Invoke-LabPerson -Name $VmName -Do uia -Title 'RemoteHub*' -Text 'New*' -Value exists -TimeoutSec 90
            $u.migrated = Invoke-LabGuest -Name $VmName -ArgumentList $vm.UserAccount -ScriptBlock {
                param($u) $r = "C:\Users\$u\AppData\Roaming"
                [pscustomobject]@{ Copied = Test-Path "$r\RemoteHub\lab-marker.txt"; Kept = Test-Path "$r\RdpManager\lab-marker.txt" }
            }
            Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force }
            Stop-LabInSession -Name $VmName -Task $task
            Clear-LabAutoLogon -Name $VmName
            Save 'upgrade' $u
        }

        It 'C-MSI-04 upgrade from the previous release: the data is kept, one entry in Apps' -Tag 'C-MSI-04' {
            $u.previous.Exit | Should -BeIn 0, 3010
            $u.prevWindow.found | Should -BeTrue -Because 'the previous version made the connection'
            $u.upgrade.Exit | Should -BeIn 0, 3010
            $u.state.Version | Should -Match "^$([regex]::Escape($version))"
            @($u.state.Arp).Count | Should -Be 1
            $u.kept.found | Should -BeTrue
        }

        It 'C-MSI-06 a downgrade is refused' -Tag 'C-MSI-06' {
            $u.downgrade.Exit | Should -Be 1603
            ($u.downgrade.Log -join "`n") | Should -Match 'newer version'
            $u.afterDowngrade.Version | Should -Match "^$([regex]::Escape($version))"
        }

        It 'C-MSI-05 from RDP Manager 2.x: upgrade (or uninstall first, as documented), one entry in Apps, %APPDATA%\RdpManager copied on the first start and kept' -Tag 'C-MSI-05' {
            $u.legacy.Exit | Should -BeIn 0, 3010
            if ($u.fromLegacy.Exit -notin 0, 3010) {
                ($u.fromLegacy.Log -join "`n") | Should -Match 'newer version' -Because 'a refused direct upgrade says why'
                $u.legacyUninstall | Should -BeIn 0, 3010
                $u.afterUninstall.Exit | Should -BeIn 0, 3010
            }
            @($u.legacyState.Arp) | Should -Be @("RemoteHub $version")
            $u.legacyWindow.found | Should -BeTrue
            $u.migrated.Copied | Should -BeTrue
            $u.migrated.Kept | Should -BeTrue
        }
    }

    Context 'standard user' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            Copy-LabFile -Name $VmName -Path $msi -Destination 'C:\LabClient'
            Set-LabAutoLogon -Name $VmName -Account User
            $leaf = Split-Path $msi -Leaf
            $exit = Invoke-LabInSession -Name $VmName -TimeoutSec 180 -ScriptBlock ([scriptblock]::Create("(Start-Process msiexec.exe -ArgumentList '/i','C:\LabClient\$leaf','/qn' -Wait -PassThru).ExitCode"))
            $state = Get-ClientState
            Clear-LabAutoLogon -Name $VmName
            Save 'standard-user' @{ Exit = "$exit"; State = $state }
        }

        It 'C-MSI-08 a standard user can''t install it (per-machine: refused without elevation)' -Tag 'C-MSI-08' {
            "$exit".Trim() | Should -Not -BeIn '0', '3010'
            $state.Exe | Should -BeFalse
        }
    }
}
