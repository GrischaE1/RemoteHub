# S-SHARE - network shares the way RemoteHub opens them (ShareConnector, ShareTransfer - no agent on the device):
# SMB through Windows' client, a second account on the same server through RemoteHub's own SMB client, SFTP with the
# host key confirmed once, FTPS and WebDAV with a self-signed certificate. Every scenario connects, lists, uploads a
# 3 MB and a small file (subfolder), checks the size, renames, downloads and compares SHA-256, and deletes again.
# Only for Windows Server VMs (IIS FTP/WebDAV and the SMB shares are set up in the guest). See docs/network-shares.md.
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isServer = (Get-LabVm $VmName).Os -eq 'Server'
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "s-share-$name.json") -Encoding UTF8 }
    function Steps($r) { ($r.steps | ForEach-Object { "$($_.step): $(if ($_.ok) { 'ok' } else { $_.error })" }) -join "`n" }
    if ($vm.Os -eq 'Server') {
        Reset-LabVm -Name $VmName
        $servers = Enable-LabShareServers -Name $VmName
        Save 'servers' $servers
        $ip = Get-LabVmAddress -Name $VmName
        $admin = $vm.AdminAccount
    }
}

Describe 'S-SHARE <VmName>' -Tag Share, 'S-SHARE' -Skip:(-not $isServer) {

    Context 'SMB' {
        It 'S-SMB-01 opens a share through Windows and copies both ways' -Tag 'S-SMB-01' {
            $r = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhshare', '--user', $admin)
            Save 'smb' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $r.description | Should -Match '^SMB . Windows'
            $r.start | Should -Be "\\$ip\rhshare"
        }

        It 'S-SMB-02 uses RemoteHub''s own client for a second account on the same server' -Tag 'S-SMB-02' {
            $r = Invoke-LabShare -Conflict @('--host', $ip, '--share1', 'rhshare', '--user1', $admin, '--share2', 'rhshare2', '--user2', $vm.UserAccount)
            Save 'smb-conflict' $r
            $r.first | Should -Match '^SMB . Windows'
            $r.second | Should -Match 'RemoteHub client'
            $r.writtenBySecond | Should -Be 14
        }

        It 'S-SMB-03 the standard user copies both ways on the share it may use' -Tag 'S-SMB-03' {
            $r = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhshare2', '--user', $vm.UserAccount)
            Save 'smb-user' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
        }

        It 'S-SMB-04 a wrong password is reported as such' -Tag 'S-SMB-04' {
            $r = Invoke-LabShare -WrongPassword 'not-the-password' @('--protocol', 'smb', '--host', $ip, '--path', 'rhshare', '--user', $admin)
            $r.ok | Should -BeFalse
            ($r.steps | Where-Object step -eq 'connect').error | Should -Match 'password'
        }
    }

    Context 'SFTP' {
        It 'S-SFTP-01 asks about the host key once and copies both ways' -Tag 'S-SFTP-01' {
            $script:sftp = Invoke-LabShare @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--accept', '1')
            Save 'sftp' $sftp
            $sftp.ok | Should -BeTrue -Because (Steps $sftp)
            @($sftp.asked).Count | Should -Be 1
            $sftp.accepted | Should -Match '^SHA256:'
            $sftp.description | Should -Match '^SFTP'
        }

        It 'S-SFTP-02 connects without asking once the key is accepted' -Tag 'S-SFTP-02' {
            $r = Invoke-LabShare @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--trusted', $script:sftp.accepted, '--accept', '0')
            $r.ok | Should -BeTrue -Because (Steps $r)
            @($r.asked).Count | Should -Be 0
        }

        It 'S-SFTP-03 refuses a key that differs from the accepted one when the user says no' -Tag 'S-SFTP-03' {
            $r = Invoke-LabShare @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--trusted', 'SHA256:not-this-key', '--accept', '0')
            $r.ok | Should -BeFalse
            @($r.asked)[0].What | Should -Match 'changed'
            ($r.steps | Where-Object step -eq 'connect').error | Should -Match 'wasn''t accepted'
        }
    }

    Context 'FTPS' {
        It 'S-FTP-01 asks about the self-signed certificate once and copies both ways over TLS' -Tag 'S-FTP-01' {
            $r = Invoke-LabShare @('--protocol', 'ftp', '--host', $ip, '--user', $admin, '--ftp-security', 'explicit', '--accept', '1')
            Save 'ftps' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $r.accepted | Should -Be $servers.Certificate
            $r.description | Should -Match '^FTPS'
        }

        It 'S-FTP-02 sends nothing when the certificate is refused' -Tag 'S-FTP-02' {
            $r = Invoke-LabShare @('--protocol', 'ftp', '--host', $ip, '--user', $admin, '--accept', '0')
            $r.ok | Should -BeFalse
            ($r.steps | Where-Object step -eq 'connect').error | Should -Match 'certificate'
        }
    }

    Context 'WebDAV' {
        It 'S-DAV-01 copies both ways over HTTPS with Windows authentication' -Tag 'S-DAV-01' {
            $r = Invoke-LabShare @('--protocol', 'webdav', '--host', $ip, '--path', '/dav', '--user', $admin, '--accept', '1')
            Save 'webdav-https' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $r.accepted | Should -Be $servers.Certificate
        }

        It 'S-DAV-02 copies both ways over HTTP' -Tag 'S-DAV-02' {
            $r = Invoke-LabShare @('--protocol', 'webdav', '--host', $ip, '--path', '/dav', '--user', $admin, '--https', '0')
            Save 'webdav-http' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
        }

        It 'S-DAV-03 a wrong password is reported as such' -Tag 'S-DAV-03' {
            $r = Invoke-LabShare -WrongPassword 'not-the-password' @('--protocol', 'webdav', '--host', $ip, '--path', '/dav', '--user', $admin, '--https', '0')
            $r.ok | Should -BeFalse
            ($r.steps | Where-Object step -eq 'connect').error | Should -Match 'password'
        }
    }
}
