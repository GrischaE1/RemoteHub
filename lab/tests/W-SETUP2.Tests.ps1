# W (more) - WinRM set up with RemoteHub's script, seen from elsewhere: a connection from an address outside the
# allowed list (the other lab VM), a non-RID-500 administrator without LocalAccountTokenFilterPolicy (the difference to
# the agent, S-AUTH-09), and HTTP with Kerberos by name (domain only). See docs/test-lab-cases.md section 8.
# Every context starts from the checkpoint (Reset-LabVm): a device without WinRM set up.
# LabRole: device
# LabQuick: every kind
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "w2-$name.json") -Encoding UTF8 }
}

AfterAll {
    # WinRM set up, a test administrator added: the next file starts from the checkpoint
    Set-LabVmDirty -Name $VmName
}

Describe 'W2 <VmName>' -Tag WinRM, 'W' {

    Context 'from another address' {
        BeforeAll {
            # the other VM of this lane: the Server VM for a client, the W11 VM for a Server
            $other = Get-LabDevice -For $VmName -Os $(if ($vm.Os -eq 'Server') { 'W11' } else { 'Server' })
            Reset-LabVm -Name $VmName
            $setup = Invoke-LabSetupScript -Name $VmName   # allows this PC only
            $ip = Get-LabVmAddress -Name $VmName
            $fromHost = Invoke-LabJump -Kind direct -TargetHost $ip -Port 5986
            $fromOther = $null
            if ($other) {
                Reset-LabVm -Name $other.Name
                $fromOther = Invoke-LabGuest -Name $other.Name -ArgumentList $ip -ScriptBlock {
                    param($ip) $c = New-Object Net.Sockets.TcpClient
                    try { $t = $c.ConnectAsync($ip, 5986); [bool]($t.Wait(10000) -and $c.Connected) } catch { $false } finally { $c.Close() }
                }
                Stop-LabVm -Name $other.Name
                Set-LabVmDirty -Name $other.Name
            }
            Save 'other-address' @{ Setup = $setup; FromHost = $fromHost; FromOther = $fromOther; Other = $other.Name }
        }

        It 'W-HTTPS-06 a connection from an address outside the list fails (the other lab VM); this PC''s works' -Tag 'W-HTTPS-06' {
            if (-not $other) { Set-ItResult -Skipped -Because 'needs a second lab VM'; return }
            $fromHost.connected | Should -BeTrue
            $fromOther | Should -BeFalse
        }
    }

    Context 'non-RID-500 administrator' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $null = New-LabGuestUser -Name $VmName -User 'lapsadmin' -Admin
            # the case needs a device without LocalAccountTokenFilterPolicy (as W-SETUP's "set up before" context): the
            # checkpoint may have it on (seen as 1 in run 20261016-212514 on W11 lane 1) - recorded, then switched off
            $latfpBefore = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
                $v = (Get-ItemProperty $k -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy
                Set-ItemProperty -Path $k -Name LocalAccountTokenFilterPolicy -Type DWord -Value 0
                $v
            }
            # the script without "local administrators" leaves LocalAccountTokenFilterPolicy alone
            $setup = Invoke-LabSetupScript -Name $VmName -LocalAdmins $false
            $state = Get-LabWinRmState -Name $VmName
            $answer = try { Invoke-LabTool -Name $VmName -Op ps -Via WinRM -UserName 'lapsadmin' -Params @{ cert = $state.Certificate.Thumbprint; script = 'whoami /groups' } } catch { [pscustomobject]@{ failed = "$_" } }
            Save 'nonadmin' @{ LatfpBeforeTest = $latfpBefore; Setup = $setup; State = $state; Answer = $answer }
        }

        It 'W-NONADMIN-01 over WinRM a non-RID-500 admin without LocalAccountTokenFilterPolicy gets no full token (refused or filtered) - unlike the agent' -Tag 'W-NONADMIN-01' {
            $state.LocalAccountTokenFilterPolicy | Should -Not -Be 1
            $text = ($answer | ConvertTo-Json -Depth 6)
            if ($answer.PSObject.Properties['failed'] -or $answer.refused) { $text | Should -Match 'denied|Access' }
            else { ($answer.result.output -join "`n") | Should -Not -Match 'S-1-16-12288' }
        }
    }

    Context 'domain' {
        It 'W-HTTP-01 HTTP with Kerberos by name: the session works, 5985 only from the given addresses' -Tag 'W-HTTP-01' {
            Set-ItResult -Skipped -Because 'needs a domain-joined VM (Kerberos)'
        }
    }
}
