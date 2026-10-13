# W - RemoteHub's WinRM setup script (RemotingSetup.BuildScript, "Set up on the target..."), run in the guest as the
# admin account the way an admin runs it, and the HTTPS connection RemoteHub then makes: fetch the certificate, compare
# its thumbprint, connect pinned to it (the trust prompt itself is W-TRUST-01, 2VM). See docs/test-lab-cases.md section 8.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $rule = 'RemoteHub-WinRM-HTTPS-In'
    $legacyRule = 'RdpManager-WinRM-HTTPS-In'
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "w-$name.json") -Encoding UTF8 }
    # what the dry run must leave alone
    function Get-Fingerprint($s) {
        [pscustomobject]@{
            Listeners = @($s.Listeners | ForEach-Object { "$($_.Transport)=$($_.Thumbprint)" } | Sort-Object)
            Rules = @($s.Rules | ForEach-Object { "$($_.Name):$($_.Enabled):$($_.RemoteAddress -join ',')" } | Sort-Object)
            Certificates = @($s.Certificates | Sort-Object)
            Latfp = $s.LocalAccountTokenFilterPolicy
        } | ConvertTo-Json -Compress
    }
    function Get-PrintedThumbprint($run) {
        $line = @($run.Output) | Where-Object { $_ -match 'Certificate thumbprint:\s+([0-9A-F ]+)' } | Select-Object -First 1
        if ($line -match 'Certificate thumbprint:\s+([0-9A-F ]+)') { $Matches[1] -replace '\s', '' }
    }
}

Describe 'W-SETUP <VmName>' -Tag WinRM, 'W' {

    Context 'HTTPS setup on a fresh device, dry run first, then twice' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            # after the revert: a VM that is off reports no address
            $vmIp = Get-LabVmAddress -Name $VmName
            $hostIp = Get-LabHostAddress -Name $VmName
            $pre = Get-LabWinRmState -Name $VmName
            $dry = Invoke-LabSetupScript -Name $VmName -DryRun
            $afterDry = Get-LabWinRmState -Name $VmName
            $run1 = Invoke-LabSetupScript -Name $VmName
            $s1 = Get-LabWinRmState -Name $VmName
            # "Test again": RemoteHub fetches the certificate and shows its thumbprint; then an HTTPS session pinned to
            # the listener's certificate (trusting it on this PC needs the Windows prompt - W-TRUST-01, 2VM)
            $fetched = try { Invoke-LabTool -Name $VmName -Op fetch -Via WinRM -TimeoutSec 120 } catch { [pscustomobject]@{ error = $_.Exception.Message } }
            $pinned = @{ cert = $s1.Certificate.Thumbprint }
            $test = Invoke-LabTool -Name $VmName -Op test -Via WinRM -Params $pinned -TimeoutSec 120
            $who = Invoke-LabTool -Name $VmName -Op ps -Via WinRM -Params ($pinned + @{ script = 'whoami; $PSVersionTable.PSVersion.ToString()' }) -TimeoutSec 120
            $run2 = Invoke-LabSetupScript -Name $VmName
            $s2 = Get-LabWinRmState -Name $VmName
            Save 'https' @{ Pre = $pre; Dry = $dry; AfterDry = $afterDry; Run1 = $run1; S1 = $s1; Fetched = $fetched; Test = $test; Who = $who; Run2 = $run2; S2 = $s2 }
        }

        It 'W-DRY-01 dry run prints the changes and changes nothing' -Tag 'W-DRY-01' {
            $dry.Failed | Should -BeNullOrEmpty
            (@($dry.Output) -join "`n") | Should -Match 'WOULD: '
            Get-Fingerprint $afterDry | Should -Be (Get-Fingerprint $pre)
        }

        It 'W-HTTPS-01 runs without errors, prints the thumbprint, listener bound to it' -Tag 'W-HTTPS-01' {
            $run1.Failed | Should -BeNullOrEmpty
            (@($run1.Output) -join "`n") | Should -Match 'Done\. PowerShell remoting is enabled over HTTPS'
            $printed = Get-PrintedThumbprint $run1
            $printed | Should -Not -BeNullOrEmpty
            $https = @($s1.Listeners | Where-Object Transport -eq 'HTTPS')
            $https.Count | Should -Be 1
            $https[0].Thumbprint | Should -Be $printed
            $s1.Service | Should -Be 'Running'
            $s1.StartType | Should -Be 'Automatic'
        }

        It 'W-HTTPS-02 certificate: end-entity, server auth only, RSA 3072, key not exportable, SAN, 2 years' -Tag 'W-HTTPS-02' {
            $c = $s1.Certificate
            $c | Should -Not -BeNullOrEmpty
            $c.FriendlyName | Should -Be 'WinRM HTTPS (RemoteHub)'
            $c.IsCa | Should -Not -Be $true
            @($c.Eku) | Should -Be @('1.3.6.1.5.5.7.3.1')
            $c.KeySize | Should -Be 3072
            $c.Exportable | Should -Be $false
            $c.San | Should -Match ([regex]::Escape($vmIp))
            $c.Years | Should -BeGreaterOrEqual 1.9
            $c.Years | Should -BeLessOrEqual 2.1
        }

        It 'W-HTTPS-05 firewall: RemoteHub rule only for the host, built-in HTTP rule off' -Tag 'W-HTTPS-05' {
            $r = @($s1.Rules | Where-Object Name -eq $rule)
            $r.Count | Should -Be 1
            $r[0].Enabled | Should -Be 'True'
            @($r[0].Port) | Should -Be @('5986')
            @($r[0].RemoteAddress) | Should -Be @($hostIp)
            $builtinBefore = @($pre.Rules | Where-Object { $_.Name -like 'WINRM-HTTP-In-TCP*' -and $_.Enabled -eq 'True' } | ForEach-Object Name)
            foreach ($b in $s1.Rules | Where-Object { $_.Name -like 'WINRM-HTTP-In-TCP*' -and $builtinBefore -notcontains $_.Name }) {
                $b.Enabled | Should -Be 'False' -Because "$($b.Name) wasn't on before the script"
            }
        }

        It 'W-LATFP-01 sets LocalAccountTokenFilterPolicy when local admins are allowed' -Tag 'W-LATFP-01' {
            $s1.LocalAccountTokenFilterPolicy | Should -Be 1
        }

        It 'W-HTTPS-03 the certificate RemoteHub fetches is the one the script made' -Tag 'W-HTTPS-03' {
            $fetched.error | Should -BeNullOrEmpty
            $fetched.thumbprint | Should -Be $s1.Certificate.Thumbprint
            $fetched.thumbprint | Should -Be (Get-PrintedThumbprint $run1) -Because 'the admin compares the two'
        }

        It 'W-HTTPS-04 HTTPS session from the host works (pinned to the certificate)' -Tag 'W-HTTPS-04' {
            $test.state | Should -Be 'Available' -Because $test.message
            $test.transport | Should -Be 'PowerShell remoting (HTTPS)'
            $who.refused | Should -BeNullOrEmpty
            @($who.result.output)[0] | Should -Match "\\$([regex]::Escape($vm.AdminAccount))$"
        }

        It 'W-TWICE-01 a second run keeps the certificate and one rule' -Tag 'W-TWICE-01' {
            $run2.Failed | Should -BeNullOrEmpty
            Get-PrintedThumbprint $run2 | Should -Be (Get-PrintedThumbprint $run1)
            @($s2.Certificates).Count | Should -Be 1
            @($s2.Rules | Where-Object Name -eq $rule).Count | Should -Be 1
            @($s2.Listeners | Where-Object Transport -eq 'HTTPS')[0].Thumbprint | Should -Be $s1.Certificate.Thumbprint
        }
    }

    Context 'device set up before: built-in rule on, own HTTPS listener, old rule name, local admins not allowed' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $prep = Invoke-LabGuest -Name $VmName -ArgumentList $legacyRule -ScriptBlock {
                param($legacyRule)
                $ErrorActionPreference = 'Stop'
                Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
                Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' | Enable-NetFirewallRule
                New-NetFirewallRule -Name $legacyRule -DisplayName 'WinRM HTTPS (RDP Manager)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5986 -Profile Any | Out-Null
                $own = New-SelfSignedCertificate -DnsName 'lab-own-listener' -CertStoreLocation Cert:\LocalMachine\My
                Get-ChildItem WSMan:\localhost\Listener | Where-Object { $_.Keys -contains 'Transport=HTTPS' } | Remove-Item -Recurse -Force
                New-Item -Path WSMan:\localhost\Listener -Transport HTTPS -Address * -CertificateThumbPrint $own.Thumbprint -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name LocalAccountTokenFilterPolicy -Type DWord -Value 0
                [pscustomobject]@{ OwnThumbprint = $own.Thumbprint; BuiltinOn = @(Get-NetFirewallRule -Name 'WINRM-HTTP-In-TCP*' | Where-Object Enabled -eq 'True' | ForEach-Object Name) }
            }
            $pre = Get-LabWinRmState -Name $VmName
            $run = Invoke-LabSetupScript -Name $VmName -LocalAdmins $false
            $post = Get-LabWinRmState -Name $VmName
            $text = @($run.Output) -join "`n"
            Save 'prior-state' @{ Prep = $prep; Pre = $pre; Run = $run; Post = $post }
        }

        It 'W-PRE-01 a built-in rule that was on stays on, with a warning' -Tag 'W-PRE-01' {
            $run.Failed | Should -BeNullOrEmpty
            @($prep.BuiltinOn).Count | Should -BeGreaterThan 0
            foreach ($n in $prep.BuiltinOn) { ($post.Rules | Where-Object Name -eq $n).Enabled | Should -Be 'True' }
            $text | Should -Match "Windows' own WinRM rule was already on before"
        }

        It 'W-LISTENER-01 an existing HTTPS listener gets RemoteHub''s certificate, with a warning' -Tag 'W-LISTENER-01' {
            $text | Should -Match 'An HTTPS listener already exists - its certificate is replaced'
            $https = @($post.Listeners | Where-Object Transport -eq 'HTTPS')
            $https.Count | Should -Be 1
            $https[0].Thumbprint | Should -Not -Be $prep.OwnThumbprint
            $post.Certificate.FriendlyName | Should -Be 'WinRM HTTPS (RemoteHub)'
        }

        It 'W-LEGACY-01 the rule with the old RDP Manager name is removed' -Tag 'W-LEGACY-01' {
            @($pre.Rules | Where-Object Name -eq $legacyRule).Count | Should -Be 1
            @($post.Rules | Where-Object Name -eq $legacyRule).Count | Should -Be 0
            @($post.Rules | Where-Object Name -eq $rule).Count | Should -Be 1
        }

        It 'W-LATFP-01 leaves LocalAccountTokenFilterPolicy alone when not chosen, and warns' -Tag 'W-LATFP-01' {
            $post.LocalAccountTokenFilterPolicy | Should -Be 0
            $text | Should -Match 'Local administrators are restricted over the network here'
        }
    }
}
