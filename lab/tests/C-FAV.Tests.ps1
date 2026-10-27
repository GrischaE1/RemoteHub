# C-FAV / C-NAV / C-RAIL - RemoteHub's navigation on a Windows client: a connection in a folder pinned to the
# Favourites group (a link, drawn as a chip: the connection stays in its folder), the link's menu and Edit, unpinning
# with Del, favourites kept after a restart and gone when the folder is deleted; one-line rows by default and two with
# Compact rows off, kept after a restart; the rail's version opens About. The client VM only, RemoteHub driven by UI
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
    function Save($name, $data) { $data | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $EvidenceDir "c-fav-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-fav-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # the entries of an item's context menu (an entry that isn't there answers the menu's entries; Esc closes it)
    function MenuEntries([string]$Node) {
        $m = Ui $Node -Value 'menu:zz-no-such-entry' -Optional
        $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 1B
        Start-Sleep -Milliseconds 500
        @($m.entries)
    }
    function Menu([string]$Node, [string]$Entry) {
        $m = Ui $Node -Value "menu:$Entry"
        if ($m.PSObject.Properties['error'] -and $m.error) { throw "menu: $($m.error) - entries: $(@($m.entries) -join ', ')" }
    }
    # how often a text shows in the main window (the original and its favourite link both show the connection's name)
    function Count([string]$Text) { @((Ui '*' -Value list -TimeoutSec 10).items | Where-Object { ($_ -split '\|')[1] -eq $Text }).Count }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $script:task
    }
}

Describe 'C-FAV <VmName>' -Tag Client, UI, 'C-FAV', 'C-NAV', 'C-RAIL' -Skip:(-not $isClient) {

    Context 'favourites' {
        BeforeAll {
            $f = [ordered]@{}
            $null = Initialize-LabVm -Name $VmName -Client
            $script:task = Start-LabClient -Name $VmName
            # a folder "Lab" (Ctrl+Shift+N) and in it the connection "Alpha" (Ctrl+N with the folder selected)
            $null = Ui 'SearchBox' -Value 'keys:^+n'
            $null = Ui 'Display name' -Title 'New folder' -Value 'setnext:Lab'
            $null = Ui 'Save' -Title 'New folder'
            $null = Ui 'Lab' -Value realclick
            New-LabClientConnection -Name $VmName -Display 'Alpha' -TargetHost 'alpha.lab.invalid'
            $f.before = Count 'Alpha'
            Menu 'Alpha' 'Add to favourites'
            Start-Sleep -Seconds 1
            Shot 'pinned'
            $f.group = (Ui 'Favourites' -Value exists -Optional -TimeoutSec 10).found
            $f.after = Count 'Alpha'
            # the link by its own name "Alpha (favourite)" (both rows show a label "Alpha"): its menu, and Edit opens the
            # connection itself; the element list kept for the record
            $f.uia = @((Ui '*' -Value list -TimeoutSec 10).items | Where-Object { $_ -match 'Alpha|Favourites|Lab' })
            $f.linkMenu = MenuEntries 'Alpha (favourite)'
            $null = Ui 'Alpha (favourite)' -Value realclick
            # F2 and Del on the console keyboard: the row is selected, and a name label can't take the focus itself
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 71
            $f.editTitle = (Ui 'Display name' -Title 'Edit connection' -Value exists -Optional -TimeoutSec 15).found
            # the editor's display name box (AutomationId NameBox)
            $f.editName = (Ui 'NameBox' -Title 'Edit connection' -Value read -Optional).value
            $null = Ui 'Cancel' -Title 'Edit connection' -Optional
            # restarted: kept
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = Start-LabClient -Name $VmName
            $f.afterRestart = (Ui 'Favourites' -Value exists -Optional -TimeoutSec 15).found
            # Del on the link unpins; the connection stays in its folder
            $null = Ui 'Alpha (favourite)' -Value realclick
            $null = Invoke-LabPerson -Name $VmName -Account Console -Do key -Value 2E
            Start-Sleep -Seconds 1
            $f.groupAfterDel = (Ui 'Favourites' -Value exists -Optional -TimeoutSec 3).found
            $f.countAfterDel = Count 'Alpha'
            $f.deleteAsked = (Ui 'Delete' -Title '?*' -Value exists -Optional -TimeoutSec 2).found
            # pinned again, then the folder deleted: the link goes with it
            Menu 'Alpha' 'Add to favourites'
            Start-Sleep -Seconds 1
            Menu 'Lab' 'Delete'
            $null = Ui 'Delete' -Title '?*' -TimeoutSec 15
            Start-Sleep -Seconds 1
            $f.groupAfterFolderDelete = (Ui 'Favourites' -Value exists -Optional -TimeoutSec 3).found
            $f.countAfterFolderDelete = Count 'Alpha'
            Shot 'deleted'
            Save 'favourites' $f
        }

        It 'C-FAV-01 Add to favourites pins a link in a Favourites group at the top; the connection stays in its folder' -Tag 'C-FAV-01' {
            $f.before | Should -Be 1
            $f.group | Should -BeTrue
            $f.after | Should -Be 2 -Because 'the link and the connection in its folder'
        }

        It 'C-FAV-02 the link''s menu offers Remove from favourites but no Delete, Move or Duplicate; F2 edits the connection itself' -Tag 'C-FAV-02' {
            ($f.linkMenu -join ', ') | Should -Match 'Remove from favourites'
            ($f.linkMenu -join ', ') | Should -Not -Match '(^|, )(Delete|Duplicate)(, |$)'
            $f.editTitle | Should -BeTrue
            $f.editName | Should -Be 'Alpha'
        }

        It 'C-FAV-03 favourites are kept after a restart' -Tag 'C-FAV-03' {
            $f.afterRestart | Should -BeTrue
        }

        It 'C-FAV-04 Del on a link only unpins it: no question, the connection stays' -Tag 'C-FAV-04' {
            $f.groupAfterDel | Should -BeFalse
            $f.countAfterDel | Should -Be 1
            $f.deleteAsked | Should -BeFalse
        }

        It 'C-FAV-05 deleting the folder of a pinned connection removes its link too' -Tag 'C-FAV-05' {
            $f.groupAfterFolderDelete | Should -BeFalse
            $f.countAfterFolderDelete | Should -Be 0
        }
    }

    Context 'rows' {
        BeforeAll {
            $n = [ordered]@{}
            # a connection nobody connected to yet: a one-line row says "never", a two-line row "never connected"
            New-LabClientConnection -Name $VmName -Display 'Beta' -TargetHost 'beta.lab.invalid'
            $n.oneLine = (Ui 'never' -Value exists -Optional -TimeoutSec 15).found
            $n.oneLineLong = (Ui 'never connected' -Value exists -Optional -TimeoutSec 2).found
            Shot 'one-line'
            # the filter button's menu: Compact rows off
            $null = Ui 'Filter: not used for' -Value 'pick:Compact rows'
            $n.twoLines = (Ui 'never connected' -Value exists -Optional -TimeoutSec 15).found
            Shot 'two-lines'
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = Start-LabClient -Name $VmName
            $n.keptAfterRestart = (Ui 'never connected' -Value exists -Optional -TimeoutSec 30).found
            $null = Ui 'Filter: not used for' -Value 'pick:Compact rows'
            $n.backToOneLine = (Ui 'never' -Value exists -Optional -TimeoutSec 15).found
            Save 'rows' $n
        }

        It 'C-NAV-01 rows take one line by default; Compact rows off puts the address and the full last use on a second line, kept after a restart' -Tag 'C-NAV-01' {
            $n.oneLine | Should -BeTrue -Because 'a one-line row shows the short last use'
            $n.oneLineLong | Should -BeFalse
            $n.twoLines | Should -BeTrue
            $n.keptAfterRestart | Should -BeTrue
            $n.backToOneLine | Should -BeTrue
        }
    }

    Context 'the rail' {
        BeforeAll {
            $r = [ordered]@{}
            # the button is named "RemoteHub <version> - about this version" (an en dash in the app)
            $null = Ui 'RemoteHub * about this version'
            $r.about = (Ui 'Close' -Title 'About RemoteHub' -Value exists -Optional -TimeoutSec 15).found
            $r.update = (Ui 'Download and install' -Title 'RemoteHub updates' -Value exists -Optional -TimeoutSec 2).found
            Shot 'about'
            $null = Ui 'Close' -Title 'About RemoteHub' -Optional
            Save 'rail' $r
        }

        It 'C-RAIL-01 the version on the rail opens About RemoteHub' -Tag 'C-RAIL-01' {
            $r.about | Should -BeTrue
            $r.update | Should -BeFalse
        }
    }
}
