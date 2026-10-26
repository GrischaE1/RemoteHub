# SEC-DEC - what a compromised device sends the administrator's PC, and where RemoteHub decodes it
# (docs/security-test-lab-design.md section 4.9, docs/decoder-isolation-design.md, docs/test-lab-cases.md section 13).
#   SEC-DEC-06  the decoder process of the installed build: RemoteHub.exe --decoder-check, run by a standard user, starts
#               RemoteHubDecoder.exe from Program Files in an AppContainer with no capabilities; it decodes a picture
#               (H.264) and a PNG, a BMP and a JPEG, and can reach no network, none of the user's files, no registry
#               key to write, no clipboard, no process and no other window
# SEC-DEC-01 .. 05 need the rogue agent (security-test-lab-design.md section 5.1) and aren't automated yet; the decoder
# process gets malformed video and images in CI (DecoderProcessTests).
# LabRole: device
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "sec-dec-$name.json") -Encoding UTF8 }
    # RemoteHub installed; normaluser signed in at the console, the check runs in that session
    $null = Initialize-LabVm -Name $VmName -Client -AutoLogon User
}

AfterAll {
    try { Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-Item 'C:\ProgramData\RemoteHubLab\session\sec-dec-*' -Force -ErrorAction SilentlyContinue } } catch { }
    # RemoteHub installed and an AppContainer profile made for normaluser: not for the next file
    Set-LabVmDirty -Name $VmName
}

Describe 'SEC-DEC <VmName>' -Tag Security, 'SEC-DEC' {

    Context 'the decoder sandbox on the installed build' {
        BeforeAll {
            $file = 'C:\ProgramData\RemoteHubLab\session\sec-dec-06.json'
            $code = "`$p = Start-Process 'C:\Program Files\RemoteHub\RemoteHub.exe' -ArgumentList '--decoder-check', '`"$file`"' -Wait -PassThru; 'exit ' + `$p.ExitCode"
            $exit = Invoke-LabInSession -Name $VmName -Account User -TimeoutSec 120 -ScriptBlock ([scriptblock]::Create($code))
            $json = Invoke-LabGuest -Name $VmName -ArgumentList $file -ScriptBlock { param($f) if (Test-Path $f) { Get-Content -LiteralPath $f -Raw -Encoding UTF8 } }
            $check = if ($json) { $json | ConvertFrom-Json } else { $null }
            Save 'check' ([ordered]@{ exit = "$exit".Trim(); result = $check })
            $probe = @{}
            if ($check -and $check.PSObject.Properties['probes']) { foreach ($p in $check.probes.PSObject.Properties) { $probe[$p.Name] = "$($p.Value)" } }
        }

        It 'SEC-DEC-06 the decoder runs in an AppContainer, decodes video and PNG/BMP/JPEG, and reaches nothing of the PC' -Tag 'SEC-DEC-06' {
            $check | Should -Not -BeNullOrEmpty -Because "RemoteHub.exe --decoder-check writes its result ($exit)"
            $check.PSObject.Properties['error'] | Should -BeNullOrEmpty -Because "$(if ($check.PSObject.Properties['error']) { $check.error })"
            $check.confinement | Should -Be 'AppContainer'
            $check.video.sent | Should -BeGreaterThan 0
            # low latency gives a picture per access unit; one may come out only with the next at the stream's format change
            # (Server 2025, run 20261026-032740: 29 of 30) - the user still sees the picture, one frame later
            $check.video.shown | Should -BeGreaterOrEqual ($check.video.sent - 1)
            $check.video.meanDifference | Should -BeLessThan 5
            foreach ($kind in 'png', 'bmp', 'jpeg') { "$($check.images.$kind)" | Should -Match '^64.48$' -Because "the $kind image is decoded to its 64 x 48 pixels" }
            $probe['sandbox'] | Should -Be 'AppContainer'
            foreach ($name in 'network', 'loopback', 'profile', 'database', 'write', 'registry', 'clipboard', 'process', 'parent', 'window') {
                $probe[$name] | Should -BeLike 'blocked*' -Because "the decoder must not reach '$name' ($($probe[$name]))"
            }
        }
    }
}
