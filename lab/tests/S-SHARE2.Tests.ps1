# S-SHARE2 - network shares beyond the copy scenarios of S-SHARE.Tests.ps1 (docs/test-lab-cases.md section 7.1).
# Covers: S-SMB-05, S-SMB-06, S-SMB-07, S-SMB-08, S-SFTP-04, S-SFTP-05, S-FTP-03, S-FTP-04, S-DAV-04,
#         S-SHR-01, S-SHR-02, S-SHR-03, S-SHR-04, S-SHR-05, S-SHR-06.
# Two parts, each for its own VM of the run:
#   - on the Server VM: RemoteHub's own share code from this PC through the lab harness ('share', and the proposed
#     'share-lifetime' / 'share-session' commands and 'share' options --windows-user / --safety - see the report),
#     against Enable-LabShareServers plus what this file adds in the guest: an FTP site without TLS (port 2121, also
#     anonymous), an implicit FTPS site (port 990), two shares only administrators may use, and a small HTTP mock
#     (port 8085) that answers WebDAV requests with a chosen status code;
#   - on a client VM: RemoteHub's own window (UI Automation in the console account's session) with the Server VM as
#     the device (Enable-LabShareServers there): the password prompt, the trust dialog and Forget, New > Network share,
#     the editor's messages and a pasted \\host\share\folder.
# Needs: the Server VM (both parts), a client VM (UI part). No jump host. See docs/network-shares.md.
# Runs on every kind: the harness part on a Server VM, the UI part on the admin PC (Test-LabConsoleVm) with its lane's
# Server VM (Get-LabDevice -For); the part that isn't for this VM reports each of its cases as skipped with the reason
# ($notServer / $notClient) - the 12 harness cases skipped on the admin PC and the 6 UI cases on the Server VM are
# expected, and every case of the file is skipped on a W11 VM.
# LabRole: any
# LabQuick: every kind
# LabNeeds: lane
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $vm = Get-LabVm $VmName
    $password = (Get-LabConfig).Password
    # which part runs here (as in BeforeDiscovery): the harness part on a Server VM, the UI part on the admin PC
    $isServer = $vm.Os -eq 'Server'
    $isClient = Test-LabConsoleVm -Name $VmName
    $device = if ($isClient) { Get-LabDevice -For $VmName } else { $null }
    $script:task = $null
    # Why a part doesn't run here, shown with each of its cases (a skipped Describe gives no reason). The runner
    # schedules the file on every kind (LabRole: any); each part has its own kind.
    $notServer = "the harness part runs on the run's Server VM (Server-2025 kind, against its own share servers); $VmName is not a Server VM"
    $notClient = "the UI part runs on the admin PC (a Console VM, or a client Windows VM in a lab without one) with its lane's Server VM as the device; $VmName is not that PC"

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "s-share2-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "s-share2-$name.png") } catch { } }
    function Steps($r) { (@($r.steps) | ForEach-Object { "$($_.step): $(if ($_.ok) { 'ok' } else { $_.error })" }) -join "`n" }
    # the error of the harness' connect step ("ShareException: <message>")
    function ConnectError($r) { "$(@(@($r.steps) | Where-Object { $_.step -eq 'connect' }) | Select-Object -First 1 | ForEach-Object { $_.error })" }
    # harness commands Invoke-LabShare doesn't wrap: Invoke-LabHarness is internal to the module, so it is called in
    # the module's scope (as A-MORE2 does)
    function Harness([string[]]$Arguments, [int]$TimeoutSec = 600) {
        & (Get-Module RemoteHub.TestLab) { param($a, $t) Invoke-LabHarness $a -TimeoutSec $t } $Arguments $TimeoutSec
    }

    # ------------------------------------------------------------------ the server side (harness part)

    # A tiny HTTP server for WebDAV answers RemoteHub can't get from IIS on demand: /s/<code> answers that status code
    # (3xx with "Location: http://moved.lab.test/dav/"), /auth answers 401 with "WWW-Authenticate: NTLM / Negotiate"
    # every time. Each request is written to a JSON line (method, path, the Authorization scheme sent).
    $davMock = @'
param([int]$Port, [string]$Log)
$l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
$l.Start()
while ($true) {
    $c = $l.AcceptTcpClient()
    try {
        $c.ReceiveTimeout = 15000
        $s = $c.GetStream()
        $rd = New-Object System.IO.StreamReader($s, [Text.Encoding]::ASCII)
        while ($true) {
            $line = $rd.ReadLine()
            if ($null -eq $line) { break }
            if ($line -eq '') { continue }
            $req = $line -split ' '
            $h = @{}
            while ($true) {
                $x = $rd.ReadLine()
                if ($null -eq $x -or $x -eq '') { break }
                $i = $x.IndexOf(':')
                if ($i -gt 0) { $h[$x.Substring(0, $i).Trim().ToLowerInvariant()] = $x.Substring($i + 1).Trim() }
            }
            $len = 0
            if ($h.ContainsKey('content-length')) { $len = [int]$h['content-length'] }
            if ($len -gt 0) { $buf = New-Object char[] $len; $n = 0; while ($n -lt $len) { $k = $rd.Read($buf, $n, $len - $n); if ($k -le 0) { break }; $n += $k } }
            $path = $req[1]
            $auth = ''
            if ($h.ContainsKey('authorization')) { $auth = ($h['authorization'] -split ' ')[0] }
            Add-Content -Path $Log -Value (ConvertTo-Json -Compress -InputObject @{ m = $req[0]; p = $path; auth = $auth })
            $code = 500; $extra = ''
            if ($path -match '^/s/(\d{3})') { $code = [int]$Matches[1] }
            elseif ($path -like '/auth*') { $code = 401; $extra = "WWW-Authenticate: Negotiate`r`nWWW-Authenticate: NTLM`r`n" }
            if ($code -in 301, 302, 307, 308) { $extra = "Location: http://moved.lab.test/dav/`r`n" }
            $b = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $code Lab`r`nContent-Length: 0`r`n$extra`r`n")
            $s.Write($b, 0, $b.Length)
            $s.Flush()
        }
    }
    catch { }
    finally { $c.Close() }
}
'@

    # What Enable-LabShareServers doesn't set up (local to this file - see the report for a shared version):
    # FTP without TLS on 2121 (basic and anonymous), implicit FTPS on 990 (the certificate of RHFtp), the share
    # "rhadmins" (everyone on the share, only Administrators in the folder), the share "rhadmins2" (only Administrators
    # on the share), and the HTTP mock on 8085.
    function Enable-ExtraShareServers([string]$CertificateSha256) {
        Invoke-LabGuest -Name $VmName -ArgumentList $vm.AdminAccount, $CertificateSha256, $davMock -ScriptBlock {
            param($admin, $certSha, $mock)
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            Import-Module WebAdministration
            $everyone = (New-Object Security.Principal.SecurityIdentifier 'S-1-1-0').Translate([Security.Principal.NTAccount]).Value
            $admins = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value
            $cert = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
                    (([Security.Cryptography.SHA256]::Create().ComputeHash($_.RawData) | ForEach-Object { $_.ToString('X2') }) -join ':') -eq $certSha
                })[0]
            # FTP without TLS: SslAllow, basic and anonymous sign-in
            $null = New-Item C:\RHFtpPlain -ItemType Directory -Force
            $null = icacls C:\RHFtpPlain /grant 'Users:(OI)(CI)M' 'IUSR:(OI)(CI)M'
            if (-not (Get-Website -Name RHFtpPlain)) { $null = New-WebFtpSite -Name RHFtpPlain -Port 2121 -PhysicalPath C:\RHFtpPlain -Force }
            $site = 'IIS:\Sites\RHFtpPlain'
            Set-ItemProperty $site -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
            Set-ItemProperty $site -Name ftpServer.security.authentication.anonymousAuthentication.enabled -Value $true
            Set-ItemProperty $site -Name ftpServer.security.ssl.controlChannelPolicy -Value 0   # SslAllow
            Set-ItemProperty $site -Name ftpServer.security.ssl.dataChannelPolicy -Value 0
            Add-WebConfiguration -PSPath IIS:\ -Location RHFtpPlain -Filter system.ftpServer/security/authorization -Value @{ accessType = 'Allow'; users = '*'; permissions = 'Read,Write' }
            Add-WebConfiguration -PSPath IIS:\ -Location RHFtpPlain -Filter system.ftpServer/security/authorization -Value @{ accessType = 'Allow'; users = '?'; permissions = 'Read,Write' }
            # implicit FTPS: IIS speaks TLS from the first byte on port 990
            $null = New-Item C:\RHFtpImplicit -ItemType Directory -Force
            $null = icacls C:\RHFtpImplicit /grant 'Users:(OI)(CI)M'
            if (-not (Get-Website -Name RHFtpImplicit)) { $null = New-WebFtpSite -Name RHFtpImplicit -Port 990 -PhysicalPath C:\RHFtpImplicit -Force }
            $site = 'IIS:\Sites\RHFtpImplicit'
            Set-ItemProperty $site -Name ftpServer.security.authentication.basicAuthentication.enabled -Value $true
            if ($cert) { Set-ItemProperty $site -Name ftpServer.security.ssl.serverCertHash -Value $cert.Thumbprint }
            Set-ItemProperty $site -Name ftpServer.security.ssl.controlChannelPolicy -Value 1   # SslRequire
            Set-ItemProperty $site -Name ftpServer.security.ssl.dataChannelPolicy -Value 1
            Add-WebConfiguration -PSPath IIS:\ -Location RHFtpImplicit -Filter system.ftpServer/security/authorization -Value @{ accessType = 'Allow'; users = '*'; permissions = 'Read,Write' }
            $null = New-NetFirewallRule -DisplayName 'RemoteHub lab FTP plain and implicit' -Protocol TCP -LocalPort 2121, 990 -Direction Inbound -Action Allow
            Restart-Service ftpsvc
            # SMB: only Administrators in the folder (the share open to everyone) ...
            $null = New-Item C:\rhadmins -ItemType Directory -Force
            $null = icacls C:\rhadmins /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F'
            if (-not (Get-SmbShare -Name rhadmins -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name rhadmins -Path C:\rhadmins -FullAccess $everyone }
            # ... and only Administrators on the share (the folder allows the admin account itself)
            $null = New-Item C:\rhadmins2 -ItemType Directory -Force
            $null = icacls C:\rhadmins2 /grant "${admin}:(OI)(CI)F"
            if (-not (Get-SmbShare -Name rhadmins2 -ErrorAction SilentlyContinue)) { $null = New-SmbShare -Name rhadmins2 -Path C:\rhadmins2 -FullAccess $admins }
            # the WebDAV status mock, started outside the PowerShell Direct session
            $root = 'C:\ProgramData\RemoteHubLab\davmock'
            $null = New-Item -ItemType Directory -Path $root -Force
            [IO.File]::WriteAllText("$root\mock.ps1", $mock)
            $null = New-NetFirewallRule -DisplayName 'RemoteHub lab DAV mock' -Protocol TCP -LocalPort 8085 -Direction Inbound -Action Allow
            $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$root\mock.ps1`" -Port 8085 -Log `"$root\requests.jsonl`""
            $null = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
            Start-Sleep -Seconds 3
            [pscustomobject]@{
                FtpPlain = (Get-Website -Name RHFtpPlain).State; FtpImplicit = (Get-Website -Name RHFtpImplicit).State
                ImplicitCert = $(if ($cert) { $cert.Thumbprint } else { $null })
                Smb = @(Get-SmbShare -Name rhadmins, rhadmins2 | ForEach-Object Name)
                Mock = [bool](Get-NetTCPConnection -LocalPort 8085 -State Listen -ErrorAction SilentlyContinue)
                Computer = $env:COMPUTERNAME
            }
        }
    }
    # what the mock saw
    function Get-DavMockRequests {
        @(Invoke-LabGuest -Name $VmName -ScriptBlock {
                @(Get-Content 'C:\ProgramData\RemoteHubLab\davmock\requests.jsonl' -ErrorAction SilentlyContinue | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
            })
    }

    # ------------------------------------------------------------------ RemoteHub's window (UI part)

    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # an entry of an item's context menu (right-click and the entry in one step; "A>B" = submenu)
    function Menu([string]$Entry, [string]$Node = 'Device', [int]$TimeoutSec = 30) {
        $m = Ui $Node -Value "menu:$Entry" -TimeoutSec $TimeoutSec
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu ${Node}: $($m.error) - entries: $(@($m.entries) -join ', ')" }
        $m
    }
    # A message box, found by its text (-Like, wildcards): its window name is the message text; if not, the dialog that
    # shows that text. Answers the window name and everything in it, then clicks -Button. (Waiting for the text, not
    # for any dialog: a question asked from a connecting thread comes a moment later.)
    function Box([string]$Like, [string]$Button = 'OK', [int]$TimeoutSec = 60) {
        $title = $Like
        $m = Ui '*' -Title $Like -Value list -TimeoutSec $TimeoutSec -Optional
        if (-not $m.found) {
            $e = Ui $Like -Title '?*' -Value exists -TimeoutSec 5 -Optional
            $title = if ($e.found) { $e.window } else { $null }
            if ($title) { $m = Ui '*' -Title $title -Value list -TimeoutSec 5 -Optional }
        }
        if ($title -and $m.found) { $null = Ui $Button -Title $title -Optional -TimeoutSec 10 }
        "$($m.window)`n" + (@($m.items) -join "`n")
    }
    function Key([string]$Hex) { $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value $Hex }
    # the console account's screen as text (OCR)
    function Screen { try { "$((Invoke-LabPerson -Name $VmName -Account Console -Do screen-read -TimeoutSec 30).text)" } catch { "OCR failed: $_" } }
    # Share editors still open (Cancel, through UI Automation - Esc from the console keyboard only reaches the foreground
    # window, and a dialog opened while RemoteHub wasn't in front isn't). The editor is modal: while one is open the
    # main window's menus don't open and everything after it fails. Answers the titles closed, written to
    # $script:strays with -Where.
    $script:strays = New-Object System.Collections.ArrayList
    function Close-Editors([string]$Where) {
        # "New network share", "Add network share to <server>", "Network share <name>" (and the password prompt)
        $closed = @()
        foreach ($i in 1..3) {
            $c = Ui 'Cancel' -Title '*network share*' -Optional -TimeoutSec 2
            if (-not $c.found) { break }
            $closed += $c.window
            Start-Sleep -Milliseconds 500
        }
        if ($closed -and $Where) { $null = $script:strays.Add("${Where}: $($closed -join ', ')") }
        $closed
    }
    # each section on its own: a failing step is written down (error, screenshot, open dialogs) and the next one runs
    function Step([System.Collections.IDictionary]$R, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $R["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $R["$Name-dialogs"] = @((Invoke-LabUi -Name $VmName -Text '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
            foreach ($i in 1..2) { try { Key 1B } catch { }; Start-Sleep -Milliseconds 500 }
            try { $R["$Name-closed"] = @(Close-Editors "after the failed step $Name") } catch { }
        }
    }
    # code in the console account's session; placeholders __NAME__ from -Values; the code's last value (a hashtable)
    # comes back through a file (no line wrapping of long answers)
    function InSession([string]$Code, [hashtable]$Values = @{}, [int]$TimeoutSec = 120) {
        foreach ($k in $Values.Keys) { $Code = $Code.Replace("__$($k)__", [string]$Values[$k]) }
        $out = "C:\ProgramData\RemoteHubLab\session\s-share2-$([guid]::NewGuid().ToString('N')).json"
        $text = "`$r = & {`n$Code`n}`n`$j = if (`$null -eq `$r) { 'null' } else { ConvertTo-Json -InputObject `$r -Depth 8 -Compress }`nSet-Content -LiteralPath '$out' -Encoding UTF8 -Value `$j"
        $raw = Invoke-LabInSession -Name $VmName -Account Console -TimeoutSec $TimeoutSec -ScriptBlock ([scriptblock]::Create($text))
        $answer = Invoke-LabGuest -Name $VmName -ArgumentList $out -ScriptBlock { param($f) if (Test-Path $f) { Get-Content -LiteralPath $f -Raw; Remove-Item -LiteralPath $f } }
        if (-not $answer) { throw "session code gave no answer: $raw" }
        $answer | ConvertFrom-Json
    }
    function B64([string]$s) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s)) }
    # UI Automation details RhPerson's uia doesn't answer: per pattern (name or tooltip, wildcards) every match in the
    # window (-Title like RhPerson) with type, tooltip, state, screen rectangle, the rectangle of the tab (ListItem) it
    # is in and, with -Sibling, of the element in that tab whose tooltip or name is like -Sibling (a tab's close button)
    $probeCode = @'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$q = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__ARGS__')) | ConvertFrom-Json
$AE = [Windows.Automation.AutomationElement]
$TS = [Windows.Automation.TreeScope]
$all = [Windows.Automation.Condition]::TrueCondition
$walker = [Windows.Automation.TreeWalker]::ControlViewWalker
function Rect($e) { if (-not $e) { return $null }; $r = $e.Current.BoundingRectangle; if ($r.IsEmpty) { return $null }; @{ x = [int]$r.X; y = [int]$r.Y; w = [int]$r.Width; h = [int]$r.Height } }
function Up($e, [string]$type) {
    $p = $walker.GetParent($e)
    while ($p) {
        if ([Windows.Automation.Automation]::Compare($p, $AE::RootElement)) { return $null }
        if ($p.Current.ControlType.ProgrammaticName -eq "ControlType.$type") { return $p }
        $p = $walker.GetParent($p)
    }
    $null
}
$tops = @($AE::RootElement.FindAll($TS::Children, $all))
$mains = @($tops | Where-Object { $_.Current.Name -like 'RemoteHub*' -or $_.Current.Name -like '* RemoteHub' })
$isWin = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)
$scope = if ($q.title -and $q.title -ne 'RemoteHub*') { @(@($mains | ForEach-Object { @($_.FindAll($TS::Descendants, $isWin)) }) + $tops | Where-Object { $_.Current.Name -like $q.title }) } else { $mains }
$win = @($scope)[0]
$els = @(if ($win) { foreach ($e in $win.FindAll($TS::Descendants, $all)) { @{ e = $e; n = $e.Current.Name; h = $e.Current.HelpText } } })
$out = [ordered]@{ window = $(if ($win) { $win.Current.Name } else { $null }) }
foreach ($p in @($q.texts)) {
    $out[$p] = @(foreach ($x in $els) {
            if (-not ($x.n -like $p -or ($x.h -and $x.h -like $p))) { continue }
            $e = $x.e
            $o = [ordered]@{ name = $x.n; help = $x.h; type = ($e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''); enabled = $e.Current.IsEnabled; offscreen = $e.Current.IsOffscreen; rect = (Rect $e) }
            $pt = $null; if ($e.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pt)) { $o.selected = $pt.Current.IsSelected }
            $pt = $null; if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pt)) { $o.toggle = [string]$pt.Current.ToggleState }
            $pt = $null; if ($e.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pt)) { try { $o.value = $pt.Current.Value } catch { } }
            $ti = Up $e 'TreeItem'
            if ($ti) { $o.treeItem = Rect $ti; $o.row = @($ti.FindAll($TS::Descendants, $all) | ForEach-Object { $_.Current.Name } | Where-Object { $_ } | Select-Object -First 30) }
            $li = Up $e 'ListItem'
            if ($li) {
                $o.listItem = Rect $li
                if ($q.sibling) {
                    $s = @($li.FindAll($TS::Descendants, $all) | Where-Object { $_.Current.HelpText -like $q.sibling -or $_.Current.Name -like $q.sibling }) | Select-Object -First 1
                    if ($s) { $o.sibling = Rect $s }
                }
            }
            $o
        })
}
$out
'@
    function Probe([string[]]$Texts, [string]$Title = 'RemoteHub*', [string]$Sibling) {
        $a = ConvertTo-Json -Compress -InputObject @{ texts = @($Texts); title = $Title; sibling = $Sibling }
        InSession $probeCode @{ ARGS = (B64 $a) }
    }
    function Hits($probe, [string]$Text) { $p = $probe.PSObject.Properties[$Text]; if ($p) { @($p.Value) } else { @() } }
    # closes the tab labelled -Label with its own x (console mouse); $false when there is no such tab
    function Close-Tab([string]$Label) {
        $h = @(Hits (Probe @($Label) -Sibling 'Close this tab') $Label | Where-Object { $_.sibling }) | Select-Object -First 1
        if (-not $h) { return $false }
        Send-LabMouse -Name $VmName -X ([int]($h.sibling.x + $h.sibling.w / 2)) -Y ([int]($h.sibling.y + $h.sibling.h / 2))
        Start-Sleep -Seconds 2
        $true
    }
    # the names of everything in the main window ("Type|Name|Id")
    function Items { @((Ui '*' -Value list).items) }
    # RemoteHub closed like the user does (a session asks "Disconnect and exit?" first); killed only when it doesn't end
    function Close-Rh {
        try { $null = Invoke-LabInSession -Name $VmName -Account Console -TimeoutSec 60 -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() } } } catch { }
        try { $null = Ui 'Exit' -Title '?*' -Optional -TimeoutSec 5 } catch { }
        $gone = Invoke-LabGuest -Name $VmName -ScriptBlock {
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            $left = @(Get-Process RemoteHub -ErrorAction SilentlyContinue)
            $left | Stop-Process -Force -ErrorAction SilentlyContinue
            $left.Count -eq 0
        }
        if ($script:task) { try { Stop-LabInSession -Name $VmName -Task $script:task } catch { }; $script:task = $null }
        $gone
    }
    # the share editor (ShareEditorWindow): -S Name, Type ('SMB*', 'SFTP*', 'FTP*', 'WebDAV*'), Host, Port (not for
    # SMB - its port box is off), Security ('None*', ...), PathLabel + Path, User (+ Password): "Enter an account for
    # this share"
    function Set-ShareEditor([string]$Title, [hashtable]$S) {
        $null = Ui 'Name' -Title $Title -Value 'exists' -TimeoutSec 30
        if ($S.ContainsKey('Name')) { $null = Ui 'Name' -Title $Title -Value "setnext:$($S.Name)" }
        if ($S.ContainsKey('Type')) { $null = Ui 'Type' -Title $Title -Value "choose:$($S.Type)" }
        if ($S.ContainsKey('Host')) { $null = Ui 'Host' -Title $Title -Value "setnext:$($S.Host)" }
        if ($S.ContainsKey('Port')) { $null = Ui 'Port' -Title $Title -Value "setnext:$($S.Port)" }
        if ($S.ContainsKey('Security')) { $null = Ui 'Encryption' -Title $Title -Value "choose:$($S.Security)" }
        if ($S.ContainsKey('Path')) { $null = Ui $S.PathLabel -Title $Title -Value "setnext:$($S.Path)" }
        if ($S.ContainsKey('User')) {
            $null = Ui 'Enter an account for this share' -Title $Title -Value check
            if ($S.User) { $null = Ui 'Username' -Title $Title -Value "setnext:$($S.User)" }
            if ($S.Password) { $null = Ui 'Password' -Title $Title -Value "setnext:$($S.Password)" }
        }
    }
    # New > New network share (in the folder of the selected item; -Select picks that item first)
    # (an editor left open by an earlier step is closed first and written down: the new one would open on top of it
    # and the fields be filled in whichever comes first)
    function New-Share([hashtable]$S, [string]$Select = 'Device') {
        $null = Close-Editors "before New-Share $($S.Name)"
        if ($Select) { $null = Ui $Select -Value realclick }
        $null = Ui 'NewButton' -Value 'pick:New network share'
        Set-ShareEditor 'New network share' $S
    }
    # Add (or Save) that must close the editor -Title: one still open afterwards (a second editor, opened by a repeated
    # action) is closed and written down
    function Submit([string]$Title = 'New network share', [string]$Button = 'Add') {
        $null = Ui $Button -Title $Title
        Start-Sleep -Seconds 1
        $null = Close-Editors "still open after $Button in '$Title'"
    }

    if ($isServer) {
        Reset-LabVm -Name $VmName
        $servers = Enable-LabShareServers -Name $VmName
        $extra = Enable-ExtraShareServers $servers.Certificate
        Save 'servers' @{ Servers = $servers; Extra = $extra }
        $ip = Get-LabVmAddress -Name $VmName
        $admin = $vm.AdminAccount
        $computer = $extra.Computer
    }
    elseif ($isClient) {
        Reset-LabVm -Name $device.Name
        $deviceServers = Enable-LabShareServers -Name $device.Name
        $deviceIp = Get-LabVmAddress -Name $device.Name
        $devAdmin = $device.AdminAccount
        # a file that must survive "Remove" (nothing on the share itself is deleted)
        $null = Invoke-LabGuest -Name $device.Name -ScriptBlock { Set-Content C:\rhshare\keep.txt 'kept by S-SHR-06' }
        $init = Initialize-LabVm -Name $VmName -Client
        $script:task = Start-LabClient -Name $VmName
        # credentials for this item, "Local account on the target computer (.\user)" ticked: .\PSAdmin
        New-LabClientConnection -Name $VmName -Display 'Device' -TargetHost $deviceIp -User $devAdmin -Password $password
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        # share editors found still open (Close-Editors): where, and which
        try { Save 'ui-strays' @($script:strays) } catch { }
        try { Stop-LabClient -Name $VmName -Task $script:task } catch { }
        Stop-LabVm -Name $device.Name
        # the share servers on the device; RemoteHub's connections and stored credentials on this VM
        Set-LabVmDirty -Name $device.Name
    }
    # the server side: IIS sites, shares, accounts and firewall rules set up in the guest
    if ($isServer -or $isClient) { Set-LabVmDirty -Name $VmName }
}

Describe 'S-SHARE2 harness <VmName>' -Tag Share, 'S-SHARE', 'S-SMB', 'S-SFTP', 'S-FTP', 'S-DAV', 'S-SHR' {

    Context 'SMB' {
        # first in the file: a session the host still has to the server from an earlier test would keep its token
        It 'S-SMB-08 access denied for a local admin over the network: Windows'' message plus the remote UAC hint' -Tag 'S-SMB-08' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = [ordered]@{}
            $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
            $r.policyBefore = Invoke-LabGuest -Name $VmName -ArgumentList $key -ScriptBlock { param($k) (Get-ItemProperty $k -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy }
            try {
                # Windows' default: a local administrator signing in over the network gets the filtered token
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $key -ScriptBlock {
                    param($k)
                    Set-ItemProperty $k -Name LocalAccountTokenFilterPolicy -Value 0 -Type DWord
                    Get-SmbSession -ErrorAction SilentlyContinue | Close-SmbSession -Force -ErrorAction SilentlyContinue
                }
                # only Administrators in the folder's permissions
                $r.folder = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhadmins', '--user', $admin)
                # only Administrators in the share's permissions
                $r.share = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhadmins2', '--user', $admin)
            }
            finally {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $key, $r.policyBefore -ScriptBlock {
                    param($k, $v)
                    if ($null -eq $v) { Remove-ItemProperty $k -Name LocalAccountTokenFilterPolicy -ErrorAction SilentlyContinue }
                    else { Set-ItemProperty $k -Name LocalAccountTokenFilterPolicy -Value $v -Type DWord }
                }
            }
            Save 'smb-admins' $r
            # the case itself: a share only Administrators may use. WNetAddConnection2 accepts the account, the share's
            # folder doesn't exist for it and the listing in the WindowsSmbShare constructor gives Windows' reason plus the
            # hint - all in the connect step.
            $r.share.ok | Should -BeFalse
            $e = ConnectError $r.share
            $e | Should -Match 'denied' -Because "Windows' own message"
            $e | Should -Match 'A local administrator signing in over the network gets no administrator rights there \(Windows'' remote UAC\)'
            # Only Administrators in the folder (the share open to everyone): the share's root still answers
            # Directory.Exists, so connect succeeds and the refusal comes with the first listing ("list start") - Windows'
            # message, without the hint (List goes through Io, not the constructor's check). Asserted as it is; the
            # missing hint there is a product finding, see the report.
            $r.folder.ok | Should -BeFalse
            $first = @(@($r.folder.steps) | Where-Object { -not $_.ok }) | Select-Object -First 1
            $first.step | Should -BeIn 'connect', 'list start'
            "$($first.error)" | Should -Match 'denied' -Because "the first refused step ($($first.step)) gives Windows' own message"
        }

        It 'S-SMB-05 no account: SMB connects with the signed-in Windows account, "SMB . Windows . your Windows account"' -Tag 'S-SMB-05' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            # Proposed harness option --windows-user: the scenario runs with that account as the process' Windows account
            # for the network (LogonUser LOGON32_LOGON_NEW_CREDENTIALS + impersonation, like runas /netonly) - the lab
            # host's own account (labop) has no account on the VM. No --user: the share has no account.
            $r = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhshare', '--windows-user', "$computer\$admin")
            Save 'smb-windows-account' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $r.description | Should -Match '^SMB . Windows . your Windows account$'
            $r.start | Should -Be "\\$ip\rhshare"
            # Not covered here: the tab's source "your Windows account (no account set)" (MainWindow.ShareAccount, UI only).
        }

        It 'S-SMB-06 Windows connection lifetime: no drive letter, one connection for two users, removed with the last; one that existed before stays; exit removes RemoteHub''s own' -Tag 'S-SMB-06' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            # Proposed harness command share-lifetime (WindowsSmbShare through ShareConnector.Open, connections listed
            # with WNetOpenEnum like "net use"): open A, open B on the same share, close A, close B, open C, ReleaseAll
            # (what RemoteHub does when it exits). --existing 1: the harness connects the share itself first (like
            # "net use \\host\share"), before A. Answer: uses.<moment> = [{ remote, local }] for
            # afterFirst, afterSecond, afterCloseFirst, afterCloseSecond, afterThird, afterReleaseAll.
            $unc = "\\$ip\rhshare"
            $fresh = Harness @('share-lifetime', '--host', $ip, '--path', 'rhshare', '--user', $admin, '--password', $password)
            $existing = Harness @('share-lifetime', '--host', $ip, '--path', 'rhshare', '--user', $admin, '--password', $password, '--existing', '1')
            Save 'smb-lifetime' @{ Fresh = $fresh; Existing = $existing }
            function Of($list) { @(@($list) | Where-Object { "$($_.remote)".TrimEnd('\') -eq $unc }) }
            # no drive letter
            @(Of $fresh.uses.afterFirst).Count | Should -Be 1
            "$(@(Of $fresh.uses.afterFirst)[0].local)" | Should -BeNullOrEmpty
            # two users, one connection; the last one removes it
            @(Of $fresh.uses.afterSecond).Count | Should -Be 1
            @(Of $fresh.uses.afterCloseFirst).Count | Should -Be 1
            @(Of $fresh.uses.afterCloseSecond).Count | Should -Be 0
            # RemoteHub exiting removes its own
            @(Of $fresh.uses.afterThird).Count | Should -Be 1
            @(Of $fresh.uses.afterReleaseAll).Count | Should -Be 0
            # a connection that existed before is used and stays
            @(Of $existing.uses.afterCloseSecond).Count | Should -Be 1
            @(Of $existing.uses.afterReleaseAll).Count | Should -Be 1
        }

        It 'S-SMB-07 port other than 445 and an empty share name are refused with their messages' -Tag 'S-SMB-07' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $port = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--port', '1445', '--path', 'rhshare', '--user', $admin)
            $noName = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--user', $admin)
            Save 'smb-rules' @{ Port = $port; NoName = $noName }
            $port.ok | Should -BeFalse
            ConnectError $port | Should -Match 'SMB always uses port 445 . remove the port from the share\.'
            $noName.ok | Should -BeFalse
            ConnectError $noName | Should -Match 'The share has no share name . edit it and enter the name'
            # the pasted \\host\share\folder in the editor: the UI part of this file
        }
    }

    Context 'SFTP' {
        It 'S-SFTP-04 SFTP needs an account; password and keyboard-interactive only (a key-only server is refused like a wrong password)' -Tag 'S-SFTP-04' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = [ordered]@{}
            $r.noAccount = Invoke-LabShare @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--accept', '1')
            # the server offers only public keys: RemoteHub has nothing else to sign in with
            $cfg = Join-Path 'C:\ProgramData' 'ssh\sshd_config'
            try {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $cfg -ScriptBlock {
                    param($f)
                    Copy-Item $f "$f.s-share2" -Force
                    $lines = @(Get-Content $f | Where-Object { $_ -notmatch '^\s*#?\s*(PasswordAuthentication|KbdInteractiveAuthentication|ChallengeResponseAuthentication|AuthenticationMethods)\s' })
                    Set-Content $f -Value (@('PasswordAuthentication no', 'KbdInteractiveAuthentication no', 'AuthenticationMethods publickey') + $lines) -Encoding ascii
                    Restart-Service sshd
                }
                $r.keyOnly = Invoke-LabShare @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--accept', '1')
            }
            finally {
                $null = Invoke-LabGuest -Name $VmName -ArgumentList $cfg -ScriptBlock { param($f) if (Test-Path "$f.s-share2") { Move-Item "$f.s-share2" $f -Force; Restart-Service sshd } }
            }
            Save 'sftp-account' $r
            $r.noAccount.ok | Should -BeFalse
            ConnectError $r.noAccount | Should -Match 'SFTP needs an account . set one for the share or its server\.'
            $r.keyOnly.ok | Should -BeFalse
            ConnectError $r.keyOnly | Should -Match 'The user name or password is incorrect \(or the server doesn.t allow password sign-in\)\.'
            # the dialog "<name> (SFTP) needs an account. ..." and the editor's "SFTP needs a user name.": UI part
        }

        It 'S-SFTP-05 a wrong password: "The user name or password is incorrect (or the server doesn''t allow password sign-in)."' -Tag 'S-SFTP-05' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = Invoke-LabShare -WrongPassword 'not-the-password' @('--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--accept', '1')
            Save 'sftp-wrong' $r
            $r.ok | Should -BeFalse
            ConnectError $r | Should -Match 'The user name or password is incorrect \(or the server doesn.t allow password sign-in\)\.$'
        }
    }

    Context 'FTP' {
        It 'S-FTP-03 FTP without TLS: "FTP . not encrypted", no certificate question, copies both ways' -Tag 'S-FTP-03' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = Invoke-LabShare @('--protocol', 'ftp', '--host', $ip, '--port', '2121', '--ftp-security', 'none', '--user', $admin)
            Save 'ftp-plain' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $r.description | Should -Match '^FTP . not encrypted'
            @($r.asked).Count | Should -Be 0
            # the editor's warning and the ftp:// address: UI part
        }

        It 'S-FTP-04 implicit TLS on port 990, anonymous without an account, a wrong password' -Tag 'S-FTP-04' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = [ordered]@{}
            # no --port: the default for implicit TLS (990) - the only port that site listens on
            $r.implicit = Invoke-LabShare @('--protocol', 'ftp', '--host', $ip, '--ftp-security', 'implicit', '--user', $admin, '--accept', '1')
            # no --user: "anonymous" / "anonymous@" (the site allows anonymous sign-in)
            $r.anonymous = Invoke-LabShare @('--protocol', 'ftp', '--host', $ip, '--port', '2121', '--ftp-security', 'none')
            $r.wrong = Invoke-LabShare -WrongPassword 'not-the-password' @('--protocol', 'ftp', '--host', $ip, '--port', '2121', '--ftp-security', 'none', '--user', $admin)
            # who signed in (the FTP log is written with a delay - evidence only)
            $r.log = @(Invoke-LabGuest -Name $VmName -ScriptBlock { Get-ChildItem C:\inetpub\logs\LogFiles\FTPSVC* -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Tail 40 } | Select-String -Pattern 'USER|PASS' | ForEach-Object { "$_" } | Select-Object -Last 20 })
            Save 'ftp-implicit-anonymous' $r
            $r.implicit.ok | Should -BeTrue -Because (Steps $r.implicit)
            $r.implicit.description | Should -Match '^FTPS'
            $r.implicit.accepted | Should -Be $servers.Certificate
            $r.anonymous.ok | Should -BeTrue -Because (Steps $r.anonymous)
            $r.wrong.ok | Should -BeFalse
            ConnectError $r.wrong | Should -Match 'The user name or password is incorrect\.$'
            # Not covered here: the tab's source "anonymous sign-in (no account set)" (MainWindow.ShareAccount, UI only).
        }
    }

    Context 'WebDAV' {
        It 'S-DAV-04 no account: the Windows credentials; redirects and status codes in words' -Tag 'S-DAV-04' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $r = [ordered]@{}
            # IIS /dav over HTTP, Windows authentication, no account: the process' Windows account (proposed --windows-user)
            $r.windows = Invoke-LabShare @('--protocol', 'webdav', '--host', $ip, '--path', '/dav', '--https', '0', '--windows-user', "$computer\$admin")
            # the mock only ever answers 401: what RemoteHub offered without an account
            $r.auth = Invoke-LabShare @('--protocol', 'webdav', '--host', $ip, '--port', '8085', '--https', '0', '--path', '/auth')
            $r.codes = [ordered]@{}
            foreach ($code in 301, 302, 307, 308, 403, 404, 409, 423, 507) {
                $r.codes["$code"] = ConnectError (Invoke-LabShare @('--protocol', 'webdav', '--host', $ip, '--port', '8085', '--https', '0', '--path', "/s/$code", '--user', $admin))
            }
            $r.requests = Get-DavMockRequests
            Save 'webdav-codes' $r
            $r.windows.ok | Should -BeTrue -Because (Steps $r.windows)
            $r.windows.description | Should -Match '^WebDAV . HTTP, not encrypted$'
            $r.auth.ok | Should -BeFalse
            @($r.requests | Where-Object { $_.p -like '/auth*' -and $_.auth -in 'NTLM', 'Negotiate' }).Count | Should -BeGreaterThan 0 -Because 'without an account the Windows account is offered (NTLM / Negotiate)'
            foreach ($code in '301', '302', '307', '308') {
                $r.codes[$code] | Should -Match 'The server redirects to http://moved\.lab\.test/dav/ . use that address for the share\.' -Because $code
            }
            $r.codes['403'] | Should -Match 'Access denied: /s/403'
            # 404 at the start: the folder isn't there ("Folder not found: ..."); "Not found: ..." elsewhere
            $r.codes['404'] | Should -Match '(?i)not found: /s/404'
            $r.codes['409'] | Should -Match 'Not found: /s/409'
            $r.codes['423'] | Should -Match 'Locked by someone else: /s/423'
            $r.codes['507'] | Should -Match 'The server has no space left\.'
        }
    }

    Context 'the share tab''s connection and transfers' {
        BeforeAll {
            if (-not $isServer) { return }
            # Proposed harness command share-session: RemoteHub's ShareSession (the share tab's connection) on a
            # dispatcher thread, ShareSession.IdleTime = --idle-sec: connect, list, delete the share's top folder
            # (DeleteAsync), create a.txt and b.txt and rename a.txt to b.txt (RenameAsync), wait until the idle timer
            # closed the connection, list again. Answer: connected / idle / afterList (StatusText at each point),
            # deleteRoot / renameExisting (the errors), listedAfterIdle (bool), opens (times the connection was
            # opened), asked (key / certificate questions).
            $script:sess = $null
            $script:sessError = $null
            try {
                $script:sess = Harness @('share-session', '--protocol', 'sftp', '--host', $ip, '--port', '2222', '--user', $admin, '--password', $password,
                    '--accept', '1', '--idle-sec', '5') -TimeoutSec 300
            }
            catch { $script:sessError = "$_" }
            Save 'share-session' @{ Answer = $script:sess; Error = $script:sessError }
        }

        It 'S-SHR-03 idle: the connection is closed after the idle time ("Idle - ... opens again when you use it.") and the next listing reconnects without asking' -Tag 'S-SHR-03' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $script:sessError | Should -BeNullOrEmpty
            $script:sess.connected | Should -Match '^Connected . SFTP'
            $script:sess.idle | Should -Match '^Idle . the connection to the share was closed and opens again when you use it\.$'
            $script:sess.listedAfterIdle | Should -BeTrue
            $script:sess.afterList | Should -Match '^Connected . SFTP'
            $script:sess.opens | Should -BeGreaterOrEqual 2
            @($script:sess.asked).Count | Should -Be 1 -Because 'the host key is asked about once, the reconnect asks nothing'
        }

        It 'S-SHR-05 browse actions: the share itself can''t be deleted; renaming onto an existing name is refused' -Tag 'S-SHR-05' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            $script:sessError | Should -BeNullOrEmpty
            $script:sess.deleteRoot | Should -Match 'The share itself can.t be deleted\.'
            $script:sess.renameExisting | Should -Match '^"b\.txt" already exists\.$'
        }

        It 'S-SHR-04 transfer safety: written as .remotehub-part, size checked, renamed; skipped file / folder; a size mismatch fails and leaves no part file' -Tag 'S-SHR-04' {
            if (-not $isServer) { Set-ItResult -Skipped -Because $notServer; return }
            # Proposed harness option --safety 1 for 'share': after the scenario, through ShareTransfer on a wrapped
            # ShareFileSystem that records what is written and moved: safety.writes (paths written), safety.moves
            # ("from -> to"), safety.doneText (status of an upload), safety.existingFile (the same file again, no
            # overwrite), safety.existingFolder (a folder with the file's name on the share), safety.mismatch (status of a
            # file that grew after its transfer item was made), safety.partLeft (the .remotehub-part file still there).
            $r = Invoke-LabShare @('--protocol', 'smb', '--host', $ip, '--path', 'rhshare', '--user', $admin, '--safety', '1')
            Save 'transfer-safety' $r
            $r.ok | Should -BeTrue -Because (Steps $r)
            $s = $r.safety
            @($s.writes).Count | Should -BeGreaterThan 0
            foreach ($w in @($s.writes)) { "$w" | Should -Match '\.remotehub-part$' }
            @($s.moves | Where-Object { $_ -match '\.remotehub-part -> ' }).Count | Should -BeGreaterThan 0
            $s.doneText | Should -Match 'size checked$'
            $s.existingFile | Should -Match 'Skipped . the file already exists'
            $s.existingFolder | Should -Match 'Skipped . a folder has that name'
            $s.mismatch | Should -Match 'the transfer was incomplete\.'
            $s.partLeft | Should -BeFalse
        }
    }
}

Describe 'S-SHARE2 UI <VmName>' -Tag Client, UI, Share, 'S-SHARE', 'S-SMB', 'S-SFTP', 'S-FTP', 'S-SHR' {

    Context 'shares on their own and the editor' {
        BeforeAll {
            if (-not $isClient) { return }
            $n = [ordered]@{}
            Step $n folder {
                # a folder with an account: a share on its own in it inherits it
                $null = Ui 'Device' -Value realclick
                $null = Ui 'SearchBox' -Value 'keys:^+n'
                $null = Ui 'Display name' -Title 'New folder' -Value 'setnext:ShareFolder'
                $null = Ui 'Credentials' -Title 'New folder'
                $null = Ui 'Enter credentials for this item' -Title 'New folder' -Value check
                $null = Ui 'Username' -Title 'New folder' -Value "setnext:$devAdmin"
                $null = Ui 'Local account on the target computer*' -Title 'New folder' -Value check
                $null = Ui 'Password' -Title 'New folder' -Value "setnext:$password"
                $null = Ui 'Save' -Title 'New folder'
                $null = Ui 'ShareFolder' -Value exists
            }
            Step $n new {
                # New > New network share with the folder selected: host left empty first
                New-Share @{ Name = 'OwnShare'; Type = 'SMB*'; PathLabel = 'Share name'; Path = 'rhshare' } -Select 'ShareFolder'
                $n.inherit = (Ui '*from folder*' -Title 'New network share' -Value read -Optional -TimeoutSec 5).name
                $null = Ui 'Add' -Title 'New network share'
                $n.hostRequired = Box '*Enter the host the share is on*'
                $null = Ui 'Host' -Title 'New network share' -Value "setnext:$deviceIp"
                Submit
                $null = Ui 'OwnShare' -Value exists
                $null = Menu 'Open' -Node 'OwnShare'
                $n.connected = (Ui 'SMB ? Windows*' -Value exists -Optional -TimeoutSec 90).found
                Shot 'own-share'
            }
            Step $n menu {
                # the share's own menu: an entry that isn't there lists all of them (Move under opened on the way)
                $m = Ui 'OwnShare' -Value 'menu:Move under>zz-no-such-entry' -Optional
                $n.entries = @($m.entries)
                Key 1B; Key 1B
                $null = Menu 'Duplicate' -Node 'OwnShare'
                $n.copy = (Ui 'OwnShare (copy)' -Value exists -Optional -TimeoutSec 10).found
            }
            Step $n remove {
                $null = Menu 'Remove' -Node 'OwnShare'
                $n.removeText = Box '*Nothing on the share itself is deleted*' -Button 'Remove'
                Start-Sleep -Seconds 3
                $n.after = Items
                $n.kept = Invoke-LabGuest -Name $device.Name -ScriptBlock { Test-Path C:\rhshare\keep.txt }
            }
            Step $n paste {
                # a pasted \\host\share\folder fills in the host (empty) and the path
                New-Share @{ Name = 'Pasted'; Type = 'SMB*'; PathLabel = 'Share name'; Path = "\\$deviceIp\rhshare\sub" }
                Submit
                $n.pasted = (Ui "\\$deviceIp\rhshare\sub" -Value exists -Optional -TimeoutSec 10).found
            }
            Step $n sftp {
                # "Enter an account for this share" without a user name
                New-Share @{ Name = 'SftpNoUser'; Type = 'SFTP*'; Host = $deviceIp; Port = '2222'; PathLabel = 'Start folder'; Path = ''; User = '' }
                $null = Ui 'Add' -Title 'New network share'
                $n.sftpEditor = Box '*SFTP needs a user name*'
                # no account at all (inherit, nothing set above it): saved, but opening says it needs one
                $null = Ui 'Inherit from parent folder' -Title 'New network share' -Value check
                Submit
                $null = Menu 'Open' -Node 'SftpNoUser'
                $n.sftpOpen = Box '*SftpNoUser (SFTP) needs an account*'
            }
            Step $n ftp {
                New-Share @{ Name = 'FtpPlain'; Type = 'FTP*'; Host = $deviceIp; Security = 'None*' }
                $n.ftpWarning = (Ui 'Not encrypted: *' -Title 'New network share' -Value read -Optional -TimeoutSec 10).name
                Submit
                $n.ftpAddress = (Ui "ftp://$deviceIp" -Value exists -Optional -TimeoutSec 10).found
            }
            Shot 'editor'
            Save 'ui-editor' $n
        }

        It 'S-SHR-06 New > Network share: host required, inherits the folder''s account, Duplicate / Move under in its menu; Remove deletes nothing on the share and closes its tab' -Tag 'S-SHR-06' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $n.hostRequired | Should -Match 'Enter the host the share is on \(name or address\)\.'
            $n.inherit | Should -Match ([regex]::Escape($devAdmin) + ' . from folder .ShareFolder.')
            $n.connected | Should -BeTrue
            $n.entries | Should -Contain 'Duplicate'
            $n.entries | Should -Contain 'Move under'
            $n.copy | Should -BeTrue
            $n.removeText | Should -Match 'Its open tab is closed\.'
            $n.removeText | Should -Match 'Nothing on the share itself is deleted\.'
            # neither the tree item nor its tab: nothing named exactly OwnShare is left (the copy is "OwnShare (copy)")
            @($n.after | Where-Object { $_ -like '*|OwnShare|*' }).Count | Should -Be 0
            $n.kept | Should -BeTrue
        }

        It 'S-SMB-07 a pasted \\host\share\folder fills in the host and the path in the editor' -Tag 'S-SMB-07' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $n.pasted | Should -BeTrue -Because "the share's address shows \\$deviceIp\rhshare\sub"
        }

        It 'S-SFTP-04 the editor: "SFTP needs a user name."; opened without an account: "[name] (SFTP) needs an account. Right-click it ..."' -Tag 'S-SFTP-04' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $n.sftpEditor | Should -Match 'SFTP needs a user name\.'
            $n.sftpOpen | Should -Match 'SftpNoUser \(SFTP\) needs an account\. Right-click it . Edit. and set one'
        }

        It 'S-FTP-03 the editor warns about FTP without TLS; the address is ftp://' -Tag 'S-FTP-03' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $n.ftpWarning | Should -Match '^Not encrypted: the password and every file go over the network in plain text\.'
            $n.ftpAddress | Should -BeTrue
        }
    }

    Context 'a password that isn''t stored' {
        BeforeAll {
            if (-not $isClient) { return }
            $p = [ordered]@{}
            Step $p add {
                # a share of the server, its own account without a password
                $null = Menu 'Tools>Add network share*' -Node 'Device'
                Set-ShareEditor 'Add network share to Device' @{ Name = 'SmbAsk'; Type = 'SMB*'; PathLabel = 'Share name'; Path = 'rhshare'; User = $devAdmin }
                Submit 'Add network share to Device'
                $null = Ui 'SmbAsk' -Value exists
            }
            Step $p open {
                $null = Menu 'Open' -Node 'SmbAsk'
                $p.ask = (Ui 'No password is stored for*' -Title 'Network share' -Value read -TimeoutSec 60).name
                $null = Ui 'type:Edit' -Title 'Network share' -Value "set:$password"
                $null = Ui 'Connect' -Title 'Network share'
                $p.connected = (Ui 'SMB ? Windows*' -Value exists -Optional -TimeoutSec 90).found
            }
            Step $p reconnect {
                $null = Ui 'Reconnect'
                $p.askedAgain = (Ui 'No password is stored for*' -Title 'Network share' -Value exists -Optional -TimeoutSec 10).found
                Start-Sleep -Seconds 3
                $p.reconnected = (Ui 'SMB ? Windows*' -Value exists -Optional -TimeoutSec 60).found
            }
            Step $p restart {
                # kept in memory only: a new RemoteHub asks again
                $p.closed = Close-Rh
                $script:task = Start-LabClient -Name $VmName
                $null = Menu 'Open' -Node 'SmbAsk'
                $p.askedAfterRestart = (Ui 'No password is stored for*' -Title 'Network share' -Value exists -Optional -TimeoutSec 60).found
                $null = Ui 'Cancel' -Title 'Network share' -Optional -TimeoutSec 5
            }
            Shot 'password'
            Save 'ui-password' $p
        }

        It 'S-SHR-01 asked "No password is stored for [user]. Enter it to open [server] > [share] - it is kept in memory only."; Reconnect doesn''t ask; a new start asks again' -Tag 'S-SHR-01' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            $p.ask | Should -Match ('No password is stored for ' + [regex]::Escape($devAdmin) + '\. Enter it to open Device . SmbAsk . it is kept in memory only\.')
            $p.connected | Should -BeTrue
            $p.askedAgain | Should -BeFalse
            $p.reconnected | Should -BeTrue
            $p.askedAfterRestart | Should -BeTrue -Because 'nothing was written to the stored secrets'
        }
    }

    Context 'the trust dialog and Forget' {
        BeforeAll {
            if (-not $isClient) { return }
            $t = [ordered]@{}
            # The question is answered after $think seconds, like a user who compares the fingerprint with the server's
            # (the dialog asks for that). It is longer than SftpShare's connection timeout (20 s): an answer after the
            # timeout must still connect (first time) or be refused with its message (changed key). Before this, how
            # fast the lab clicked (OCR and UI Automation round trips) decided whether the first connection worked.
            $think = 30
            function Wait-Think([Diagnostics.Stopwatch]$Since) {
                $left = $think - [int]$Since.Elapsed.TotalSeconds
                if ($left -gt 0) { Start-Sleep -Seconds $left }
            }
            Step $t add {
                New-Share @{ Name = 'SftpTrust'; Type = 'SFTP*'; Host = $deviceIp; Port = '2222'; PathLabel = 'Start folder'; Path = ''; User = $devAdmin; Password = $password }
                Submit
                $null = Ui 'SftpTrust' -Value exists
            }
            Step $t first {
                $null = Menu 'Open' -Node 'SftpTrust'
                $null = Ui '*doesn*t know yet*' -Title '?*' -Value exists -TimeoutSec 60
                $asked = [Diagnostics.Stopwatch]::StartNew()
                $t.firstScreen = Screen
                Wait-Think $asked
                $t.firstAnsweredSec = [int]$asked.Elapsed.TotalSeconds
                $t.first = Box '*doesn*t know yet*' -Button 'Trust and connect'
                $t.connected1 = (Ui 'SFTP ?*' -Value exists -Optional -TimeoutSec 60).found
                # not connected: what the tab says instead (its status line, read from the screen)
                if (-not $t.connected1) { $t.firstFailScreen = Screen }
                $null = Menu 'Edit*' -Node 'SftpTrust'
                $t.accepted1 = (Ui 'Accepted host key: *' -Title 'Network share SftpTrust' -Value read -TimeoutSec 30).name
                $null = Ui 'Cancel' -Title 'Network share SftpTrust'
                $null = Close-Tab 'SftpTrust'
            }
            Step $t second {
                $null = Menu 'Open' -Node 'SftpTrust'
                $t.askedAgain = (Ui '*presents a*' -Title '?*' -Value exists -Optional -TimeoutSec 15).found
                $t.connected2 = (Ui 'SFTP ?*' -Value exists -Optional -TimeoutSec 60).found
                $null = Close-Tab 'SftpTrust'
            }
            Step $t changed {
                # a new host key on the device (as after a reinstall)
                # The sshd service writes missing host keys itself when it starts (as SYSTEM, with the permissions it
                # accepts); keys made with "ssh-keygen -A" from the PowerShell Direct session got permissions sshd
                # refused ("Failed to start service", sshd left stopped). That is only the fallback here, with the
                # private keys' permissions set to SYSTEM and Administrators.
                $t.newKey = Invoke-LabGuest -Name $device.Name -ScriptBlock {
                    $ErrorActionPreference = 'Stop'
                    $dir = "$env:ProgramData\ssh"
                    $hash = { @(Get-ChildItem "$dir\ssh_host_*_key.pub" -ErrorAction SilentlyContinue | ForEach-Object { (Get-FileHash $_.FullName).Hash }) }
                    $before = & $hash
                    Stop-Service sshd
                    Remove-Item "$dir\ssh_host_*" -Force -ErrorAction SilentlyContinue
                    $how = 'sshd'
                    try { Start-Service sshd }
                    catch {
                        $how = "ssh-keygen ($($_.Exception.Message))"
                        Push-Location $dir
                        try { & "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe" -A 2>&1 | Out-Null } finally { Pop-Location }
                        foreach ($k in @(Get-ChildItem "$dir\ssh_host_*_key")) { $null = icacls $k.FullName /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' }
                        Start-Service sshd
                    }
                    $after = & $hash
                    [pscustomobject]@{
                        How = $how; Status = (Get-Service sshd).Status.ToString()
                        Keys = @(Get-ChildItem "$dir\ssh_host_*_key.pub" -ErrorAction SilentlyContinue | ForEach-Object Name)
                        Changed = @($after | Where-Object { $_ -notin $before }).Count -gt 0
                    }
                }
                $null = Menu 'Open' -Node 'SftpTrust'
                # the title ("Server key changed") isn't in the dialog's UI Automation tree (only its text and buttons):
                # read from the screen while the dialog is open
                $null = Ui '*presents a DIFFERENT*' -Title '?*' -Value exists -TimeoutSec 60
                $asked = [Diagnostics.Stopwatch]::StartNew()
                $t.changedScreen = Screen
                Wait-Think $asked
                $t.changedAnsweredSec = [int]$asked.Elapsed.TotalSeconds
                $t.changed = Box '*presents a DIFFERENT*' -Button 'Cancel'
                $t.refused = (Ui 'The server*s key wasn*t accepted*' -Value exists -Optional -TimeoutSec 30).found
                if (-not $t.refused) { $t.changedFailScreen = Screen }
                $null = Close-Tab 'SftpTrust'
                $null = Menu 'Edit*' -Node 'SftpTrust'
                $t.accepted2 = (Ui 'Accepted host key: *' -Title 'Network share SftpTrust' -Value read -TimeoutSec 30).name
                # Forget: asked again next time
                $null = Ui 'Forget' -Title 'Network share SftpTrust'
                $t.afterForget = (Ui 'Accepted host key: *' -Title 'Network share SftpTrust' -Value exists -Optional -TimeoutSec 3).found
                Submit 'Network share SftpTrust' 'Save'
            }
            Step $t forgotten {
                $null = Menu 'Open' -Node 'SftpTrust'
                $t.again = Box '*doesn*t know yet*' -Button 'Trust and connect'
                $t.connected3 = (Ui 'SFTP ?*' -Value exists -Optional -TimeoutSec 60).found
                $null = Close-Tab 'SftpTrust'
            }
            Shot 'trust'
            Save 'ui-trust' $t
        }

        It 'S-SHR-02 "Trust this server?" the first time, nothing the second time; a changed key "Server key changed" (refused, the accepted key kept); Forget asks again' -Tag 'S-SHR-02' {
            if (-not $isClient) { Set-ItResult -Skipped -Because $notClient; return }
            # the window name is the message text: RemoteHub's AskTrust wording; the title only on the screen (OCR)
            $t.first | Should -Match 'presents a SSH host key that RemoteHub doesn.t know yet'
            $t.first | Should -Match 'Trust and connect'
            $t.firstScreen | Should -Match 'Trust this server'
            $t.connected1 | Should -BeTrue -Because "Trust and connect was clicked $($t.firstAnsweredSec) s after the question appeared; the tab then showed: $($t.firstFailScreen)"
            $t.accepted1 | Should -Match '^Accepted host key: SHA256:'
            $t.askedAgain | Should -BeFalse
            $t.connected2 | Should -BeTrue
            $t.newKey.Status | Should -Be 'Running' -Because "sshd on the device after the new host keys ($($t.newKey.How))"
            $t.newKey.Changed | Should -BeTrue
            $t.changed | Should -Match 'presents a DIFFERENT SSH host key than the one you accepted before'
            $t.changedScreen | Should -Match 'Server key changed'
            $t.refused | Should -BeTrue -Because "Cancel was clicked $($t.changedAnsweredSec) s after the question appeared; the tab then showed: $($t.changedFailScreen)"
            $t.accepted2 | Should -Be $t.accepted1
            $t.afterForget | Should -BeFalse
            $t.again | Should -Match 'doesn.t know yet'
            $t.again | Should -Not -Match 'DIFFERENT'
            $t.connected3 | Should -BeTrue
        }
    }
}
