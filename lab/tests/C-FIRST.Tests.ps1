# C-FIRST - RemoteHub's first start on a Windows client: the welcome tour after a new install (its pages, pages for
# features IT policy turns off left out, shown once), What's new after an upgrade from the previous release (its
# highlights, shown once), and both again from Settings - Help & about. The client VM only, RemoteHub driven by UI
# Automation in the console account's session (RhPerson.ps1 -Do uia). See docs/test-lab-cases.md section 10.9.
# LabRole: console
param(
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$EvidenceDir
)

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
}

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\RemoteHub.TestLab') -Force
    $isClient = Test-LabConsoleVm -Name $VmName
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "c-first-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-first-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    $tour = 'Welcome to RemoteHub'
    $news = "What's new"
    # the texts a window shows (UIA's element list: "Type|Name|AutomationId")
    function Texts([string]$Title, [int]$TimeoutSec = 10) { @((Ui '*' -Title $Title -Value list -Optional -TimeoutSec $TimeoutSec).items) }
    # whether a window with that title is open (a search for its close button answers within the timeout)
    function Shown([string]$Title, [string]$Button, [int]$TimeoutSec = 15) { [bool](Ui $Button -Title $Title -Value exists -Optional -TimeoutSec $TimeoutSec).found }
    # goes through the tour with Next and answers each page's texts, one string per page (the window's title bar is a
    # text too, so a page is told by all of its texts, not by the first); the last page's button says Get started
    function Walk-Tour {
        $pages = @()
        for ($i = 0; $i -lt 8; $i++) {
            $items = Texts $tour
            $pages += (@($items | Where-Object { $_ -match '^Text\|' } | ForEach-Object { ($_ -split '\|')[1] }) -join ' ')
            if (Shown $tour 'Get started' 2) { $null = Ui 'Get started' -Title $tour; return , $pages }
            $null = Ui 'Next' -Title $tour
            Start-Sleep -Milliseconds 500
        }
        throw "Walk-Tour: no 'Get started' after 8 pages ($($pages -join ' / '))"
    }
    function Help-Page {
        $null = Ui 'Settings'
        $null = Ui 'Help*' -Title 'Settings'
    }
    function Install-Client([string]$Msi) {
        Copy-LabFile -Name $VmName -Path $Msi -Destination 'C:\LabClient'
        Invoke-LabGuest -Name $VmName -ArgumentList (Split-Path $Msi -Leaf) -ScriptBlock {
            param($leaf)
            (Start-Process msiexec.exe -ArgumentList "/i `"C:\LabClient\$leaf`" /qn /norestart /l*v `"C:\LabClient\$leaf.log`"" -Wait -PassThru).ExitCode
        }
    }
    $version = (Split-Path (Get-LabClientMsi) -Leaf) -replace '^RemoteHub-(.+)-x64\.msi$', '$1'
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $script:task
        # installs, upgrades and policies: the next file starts from the checkpoint
        Set-LabVmDirty -Name $VmName
    }
}

Describe 'C-FIRST <VmName>' -Tag Client, UI, 'C-FIRST' -Skip:(-not $isClient) {

    Context 'a new install' {
        BeforeAll {
            Reset-LabVm -Name $VmName
            $n = [ordered]@{}
            $null = Install-LabClient -Name $VmName
            $script:task = Start-LabClient -Name $VmName -KeepFirstStart
            $n.shown = Shown $tour 'Skip' 30
            Shot 'tour'
            $n.firstPage = Texts $tour
            $n.backOnFirst = (Ui 'Back' -Title $tour -Value exists -Optional -TimeoutSec 2).found
            $n.pages = Walk-Tour
            Start-Sleep -Seconds 1
            $n.closed = -not (Shown $tour 'Skip' 3)
            # started again: the version is remembered, nothing opens
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = Start-LabClient -Name $VmName -KeepFirstStart
            $n.again = (Shown $tour 'Skip' 15) -or (Shown $news 'Close' 2)
            # Help & about: both again
            Help-Page
            $null = Ui 'Welcome tour*' -Title 'Settings'
            $n.tourFromHelp = Shown $tour 'Skip' 15
            $null = Ui 'Skip' -Title $tour -Optional
            $null = Ui "What*s new*" -Title 'Settings'
            $n.newsFromHelp = Shown $news 'Close' 15
            $n.newsTexts = Texts $news
            Shot 'news-from-help'
            $null = Ui 'Close' -Title $news -Optional
            $null = Ui 'Close' -Title 'Settings' -Optional
            Save 'new-install' $n
        }

        It 'C-FIRST-01 a new install opens the welcome tour on the first start' -Tag 'C-FIRST-01' {
            $n.shown | Should -BeTrue
            ($n.firstPage -join "`n") | Should -Match 'Welcome to RemoteHub'
            $n.backOnFirst | Should -BeFalse -Because 'the first page has nothing to go back to'
        }

        It 'C-FIRST-02 the tour''s pages (connections, the agent, Notes, keep it safe) end with Get started, which closes it' -Tag 'C-FIRST-02' {
            @($n.pages).Count | Should -Be 5
            ($n.pages -join ' / ') | Should -Match 'Add your connections'
            ($n.pages -join ' / ') | Should -Match 'RemoteHub Agent'
            ($n.pages -join ' / ') | Should -Match 'Notes'
            ($n.pages -join ' / ') | Should -Match 'Keep it safe'
            $n.closed | Should -BeTrue
        }

        It 'C-FIRST-03 the next start opens neither the tour nor What''s new' -Tag 'C-FIRST-03' {
            $n.again | Should -BeFalse
        }

        It 'C-FIRST-04 Settings - Help & about opens the welcome tour and What''s new again' -Tag 'C-FIRST-04' {
            $n.tourFromHelp | Should -BeTrue
            $n.newsFromHelp | Should -BeTrue
            ($n.newsTexts -join "`n") | Should -Match ([regex]::Escape("What's new in RemoteHub $version"))
        }
    }

    Context 'pages for features IT turns off' {
        BeforeAll {
            $p = [ordered]@{}
            Stop-LabClient -Name $VmName -Task $script:task
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
                Set-ItemProperty $k -Name AllowNotes -Value 0 -Type DWord
                Set-ItemProperty $k -Name AllowAgent -Value 0 -Type DWord
            }
            $script:task = Start-LabClient -Name $VmName -KeepFirstStart
            Help-Page
            $null = Ui 'Welcome tour*' -Title 'Settings'
            $p.shown = Shown $tour 'Skip' 15
            $p.pages = Walk-Tour
            $null = Ui 'Close' -Title 'Settings' -Optional
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock { Remove-ItemProperty 'HKLM:\SOFTWARE\Policies\RemoteHub' -Name AllowNotes, AllowAgent -ErrorAction SilentlyContinue }
            Save 'policy' $p
        }

        It 'C-FIRST-05 AllowNotes = 0 and AllowAgent = 0 leave the Notes and agent pages out of the tour' -Tag 'C-FIRST-05' {
            $p.shown | Should -BeTrue
            @($p.pages).Count | Should -Be 3
            ($p.pages -join ' / ') | Should -Not -Match 'Notes|RemoteHub Agent'
        }
    }

    Context 'an upgrade from the previous release' {
        BeforeAll {
            $u = [ordered]@{}
            Stop-LabClient -Name $VmName -Task $script:task
            Reset-LabVm -Name $VmName
            # the previous release with a connection (made by starting it with an .rdp file): settings from a version
            # before What's new, so the first start of this one is an upgrade
            $previous = Get-LabClientMsi -Previous
            $u.previous = (Split-Path $previous -Leaf)
            $u.previousExit = Install-Client $previous
            $null = Invoke-LabGuest -Name $VmName -ScriptBlock {
                $null = New-Item -ItemType Directory C:\lab -Force
                Set-Content 'C:\lab\lab-device.rdp' "full address:s:lab-device`r`nusername:s:labuser" -Encoding ASCII
                $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
                Set-ItemProperty $k -Name DisableUpdateCheck -Value 1 -Type DWord
                Set-ItemProperty $k -Name DisableTelemetry -Value 1 -Type DWord
            }
            $t = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' 'C:\lab\lab-device.rdp' }
            $u.previousWindow = (Invoke-LabPerson -Name $VmName -Account Console -Do uia -Title 'RemoteHub*' -Text '*lab-device*' -Value exists -TimeoutSec 90).found
            Start-Sleep -Seconds 5
            Stop-LabClient -Name $VmName -Task $t
            $u.upgradeExit = Install-Client (Get-LabClientMsi)
            $script:task = Start-LabClient -Name $VmName -KeepFirstStart
            $u.newsShown = Shown $news 'Close' 30
            $u.tourShown = Shown $tour 'Skip' 2
            $u.texts = Texts $news
            Shot 'news-after-upgrade'
            $null = Ui 'Close' -Title $news -Optional
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = Start-LabClient -Name $VmName -KeepFirstStart
            $u.again = (Shown $news 'Close' 15) -or (Shown $tour 'Skip' 2)
            Save 'upgrade' $u
        }

        It 'C-FIRST-06 after the upgrade What''s new (not the tour) lists this version''s highlights' -Tag 'C-FIRST-06' {
            $u.previousExit | Should -BeIn 0, 3010
            $u.upgradeExit | Should -BeIn 0, 3010
            $u.previousWindow | Should -BeTrue
            $u.newsShown | Should -BeTrue
            $u.tourShown | Should -BeFalse
            $all = $u.texts -join "`n"
            $all | Should -Match ([regex]::Escape("What's new in RemoteHub $version"))
            $all | Should -Match 'Favourites'
            $all | Should -Match 'All release notes'
        }

        It 'C-FIRST-07 What''s new is shown once' -Tag 'C-FIRST-07' {
            $u.again | Should -BeFalse
        }
    }
}
