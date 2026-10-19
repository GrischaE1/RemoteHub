#Requires -Version 5.1
<#
.SYNOPSIS
    RemoteHub Lab console: choose the tests and VMs, optionally build new MSIs, start the run, follow it and read
    the failure summary when it ends - one window for Start-LabRun.ps1, Stop-LabRun.ps1 and Get-LabFailures.ps1.
.DESCRIPTION
    Everything is read when the window opens, so new test files, cases and VMs appear without changing this script:
    - tests: lab\tests\*.Tests.ps1 - grouped by the first bare tag of their Describe (Agent, Desktop, Client ...),
      one entry per It with a case ID tag;
    - VMs: Hyper-V VMs matching lab.json's VmNamePattern, as kinds (both lanes of a kind run together);
    - MSIs: the newest RemoteHub-Agent-<version>-x64.msi in artifacts\.
    The run itself is Start-LabRun.ps1 in a hidden Windows PowerShell; closing the window doesn't stop it (Stop does).
    Run with lab\LabConsole.cmd, or: powershell -STA -File lab\LabConsole.ps1
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    # WPF needs a single-threaded apartment
    Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-File', "`"$PSCommandPath`""
    return
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
# started from LabConsole.cmd there is no console to show an error in
trap { [Windows.MessageBox]::Show("LabConsole failed: $($_.Exception.Message)`n$($_.InvocationInfo.PositionMessage)", 'RemoteHub Lab', 'OK', 'Error') | Out-Null; break }

$script:LabDir = $PSScriptRoot
$script:Repo = Split-Path $PSScriptRoot
$script:Cfg = Get-Content (Join-Path $PSScriptRoot 'lab.json') -Raw | ConvertFrom-Json
$script:Artifacts = Join-Path $script:Repo 'artifacts'
$script:ResultsRoot = Join-Path $script:Artifacts 'lab'

# ---------------------------------------------------------------------------------------------------------------
# What there is to run
# ---------------------------------------------------------------------------------------------------------------

# One object per test file: Name, About (its first comment line), Role, Groups (bare Describe tags), Cases (Id, Title).
function Get-LabCatalog {
    foreach ($f in @(Get-ChildItem (Join-Path $script:LabDir 'tests') -Filter '*.Tests.ps1' | Sort-Object Name)) {
        $lines = [IO.File]::ReadAllLines($f.FullName)
        $about = if ($lines.Count -and $lines[0] -match '^#\s*(.+)$') { $Matches[1] } else { '' }
        $role = ''
        $groups = New-Object System.Collections.Generic.List[string]
        $cases = New-Object System.Collections.Generic.List[object]
        foreach ($l in $lines) {
            if ($l -match '^#\s*LabRole:\s*(\S+)') { $role = $Matches[1]; continue }
            if ($l -match "^\s*Describe\s+(['""]).*?\1\s+-Tag\s+([^{]+)") {
                foreach ($t in ($Matches[2] -split ',')) {
                    $t = $t.Trim()
                    if ($t -match '^[A-Za-z]+$' -and -not $groups.Contains($t)) { $groups.Add($t) }
                }
                continue
            }
            $id = if ($l -match "-Tag\s+'([A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2,})'") { $Matches[1] } else { $null }
            if (-not $id) { continue }
            if ($l -match "^\s*It\s+'((?:[^']|'')*)'") { $title = $Matches[1] -replace "''", "'" }
            elseif ($l -match '^\s*It\s+"([^"]*)"') { $title = $Matches[1] }
            else { continue }
            $cases.Add([pscustomobject]@{ Id = $id; Title = $title })
        }
        # arrays, not the lists: Windows PowerShell 5.1 fails on a List[string] indexed inside the literal below
        $groupList = [string[]]$groups.ToArray()
        $main = if ($groupList.Count) { $groupList[0] } else { 'Other' }
        [pscustomobject]@{
            Name = $f.Name -replace '\.Tests\.ps1$', ''; About = $about; Role = $role
            Groups = $groupList; Group = $main; Cases = [object[]]$cases.ToArray()
        }
    }
}

# The VM kinds (Os-Version-Language) with their lanes, from Hyper-V; empty when this account can't list the VMs.
function Get-LabKinds {
    $vms = @(try { Get-VM -Name $script:Cfg.VmNamePattern -ErrorAction Stop } catch { })
    $kinds = @{}
    foreach ($v in $vms) {
        $parts = $v.Name -split '\s+-\s+'
        if ($parts.Count -notin 4, 5) { continue }
        $base = $parts[1..3] -join '-'
        if (-not $kinds.ContainsKey($base)) { $kinds[$base] = 0 }
        $kinds[$base]++
    }
    foreach ($k in @($kinds.Keys | Sort-Object)) { [pscustomobject]@{ Id = $k; Lanes = $kinds[$k] } }
}

# The newest agent MSI's version in artifacts\, or $null.
function Get-NewestMsiVersion {
    if (-not (Test-Path $script:Artifacts)) { return $null }
    $v = @(Get-ChildItem $script:Artifacts -Filter 'RemoteHub-Agent-*-x64.msi' |
            Where-Object { $_.Name -match '^RemoteHub-Agent-\d+\.\d+\.\d+-x64\.msi$' } |
            ForEach-Object { [version]($_.Name -replace '^RemoteHub-Agent-(.+)-x64\.msi$', '$1') } | Sort-Object -Descending)
    if ($v.Count) { $v[0] } else { $null }
}

function Read-Shared([string]$Path) {
    if (-not (Test-Path $Path)) { return '' }
    try {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try { (New-Object IO.StreamReader($fs, $true)).ReadToEnd() } finally { $fs.Dispose() }
    }
    catch { '' }
}

# ---------------------------------------------------------------------------------------------------------------
# The window
# ---------------------------------------------------------------------------------------------------------------

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="RemoteHub Lab" Width="1180" Height="780" MinWidth="900" MinHeight="560"
        FontFamily="Segoe UI" FontSize="13" WindowStartupLocation="CenterScreen">
  <Grid Margin="12">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="5*"/>
      <ColumnDefinition Width="12"/>
      <ColumnDefinition Width="6*"/>
    </Grid.ColumnDefinitions>

    <!-- tests -->
    <DockPanel Grid.Column="0">
      <TextBlock DockPanel.Dock="Top" Text="Tests" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,6"/>
      <DockPanel DockPanel.Dock="Top" Margin="0,0,0,6">
        <Button x:Name="FailedButton" DockPanel.Dock="Right" Content="Failed in last run" Padding="10,3" Margin="6,0,0,0"/>
        <Button x:Name="NoneButton" DockPanel.Dock="Right" Content="None" Padding="10,3" Margin="6,0,0,0"/>
        <Button x:Name="AllButton" DockPanel.Dock="Right" Content="All" Padding="10,3" Margin="6,0,0,0"/>
        <TextBox x:Name="SearchBox" VerticalContentAlignment="Center" ToolTip="Filter by case ID, file or words of the test name"/>
      </DockPanel>
      <TextBlock x:Name="SelectionText" DockPanel.Dock="Bottom" TextWrapping="Wrap" Margin="0,6,0,0" Foreground="#555"/>
      <TreeView x:Name="TestTree"/>
    </DockPanel>

    <!-- run -->
    <DockPanel Grid.Column="2">
      <StackPanel DockPanel.Dock="Top">
        <TextBlock Text="VMs" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,6"/>
        <WrapPanel x:Name="VmPanel" Margin="0,0,0,10"/>

        <TextBlock Text="Build" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,6"/>
        <TextBlock x:Name="MsiText" Margin="0,0,0,4"/>
        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
          <CheckBox x:Name="BuildMsi" Content="Build new MSIs first, version" VerticalAlignment="Center"/>
          <TextBox x:Name="BuildVersion" Width="90" Margin="6,0,0,0" VerticalContentAlignment="Center"/>
        </StackPanel>
        <CheckBox x:Name="BuildHarness" Content="Rebuild the lab harness (needed after client or harness changes)" IsChecked="True" Margin="0,0,0,10"/>

        <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
          <Button x:Name="StartButton" Content="Start run" Padding="18,5" FontWeight="SemiBold"/>
          <Button x:Name="StopButton" Content="Stop" Padding="18,5" Margin="8,0,0,0" IsEnabled="False"/>
          <Button x:Name="ResultsButton" Content="Open results" Padding="12,5" Margin="8,0,0,0"/>
        </StackPanel>
        <TextBlock x:Name="StatusText" FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,0,0,8"/>
      </StackPanel>
      <TabControl x:Name="Tabs">
        <TabItem Header="Failures">
          <TextBox x:Name="FailuresBox" IsReadOnly="True" FontFamily="Consolas" FontSize="12" TextWrapping="Wrap"
                   VerticalScrollBarVisibility="Auto"/>
        </TabItem>
        <TabItem Header="Progress">
          <TextBox x:Name="ProgressBox" IsReadOnly="True" FontFamily="Consolas" FontSize="12"
                   VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"/>
        </TabItem>
        <TabItem Header="Output">
          <TextBox x:Name="OutputBox" IsReadOnly="True" FontFamily="Consolas" FontSize="12"
                   VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"/>
        </TabItem>
      </TabControl>
    </DockPanel>
  </Grid>
</Window>
'@
$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'FailedButton', 'NoneButton', 'AllButton', 'SearchBox', 'SelectionText', 'TestTree', 'VmPanel', 'MsiText',
    'BuildMsi', 'BuildVersion', 'BuildHarness', 'StartButton', 'StopButton', 'ResultsButton', 'StatusText', 'Tabs',
    'FailuresBox', 'ProgressBox', 'OutputBox') { $ui[$n] = $window.FindName($n) }

# ---------------------------------------------------------------------------------------------------------------
# The test tree: group > file > case, with check boxes (a parent shows "some" when only part of it is chosen)
# ---------------------------------------------------------------------------------------------------------------

$script:Catalog = @(Get-LabCatalog)
$script:CaseItems = New-Object System.Collections.Generic.List[object]   # every case's TreeViewItem
$script:GroupItems = New-Object System.Collections.Generic.List[object]

function New-Node([string]$Text, [object]$Data, [string]$Tip) {
    $cb = New-Object Windows.Controls.CheckBox
    $cb.Content = $Text
    $cb.IsChecked = $false
    if ($Tip) { $cb.ToolTip = $Tip }
    $item = New-Object Windows.Controls.TreeViewItem
    $item.Header = $cb
    $item.Tag = $Data
    $cb.Tag = $item
    $cb.Add_Checked({ param($s, $e) Switch-Node $s })
    $cb.Add_Unchecked({ param($s, $e) Switch-Node $s })
    $item
}

# A box ticked or cleared (by the mouse, the keyboard or UI Automation): its branch follows, its parents show
# all / some / none. The changes made here raise the same events - $script:Busy keeps them from starting over.
$script:Busy = $false
function Switch-Node($Box) {
    if ($script:Busy) { return }
    $script:Busy = $true
    try { Set-Branch $Box.Tag ([bool]$Box.IsChecked); Update-Parents $Box.Tag; Update-Selection }
    finally { $script:Busy = $false }
}

function Set-Branch($Item, [bool]$Value) {
    $Item.Header.IsChecked = $Value
    foreach ($c in $Item.Items) { Set-Branch $c $Value }
}

function Update-Parents($Item) {
    $p = $Item.Parent
    while ($p -is [Windows.Controls.TreeViewItem]) {
        $states = @($p.Items | ForEach-Object { $_.Header.IsChecked })
        $on = @($states | Where-Object { $_ -eq $true }).Count
        $p.Header.IsChecked = if ($on -eq $states.Count) { $true } elseif (@($states | Where-Object { $_ -ne $false }).Count) { $null } else { $false }
        $p = $p.Parent
    }
}

foreach ($g in @($script:Catalog | Group-Object Group | Sort-Object { if ($_.Name -eq 'Smoke') { '' } else { $_.Name } })) {
    $count = ($g.Group | ForEach-Object { $_.Cases.Count } | Measure-Object -Sum).Sum
    $gi = New-Node "$($g.Name)  ($count)" ([pscustomobject]@{ Kind = 'Group'; Name = $g.Name; Files = @($g.Group) }) ''
    foreach ($f in $g.Group) {
        $fi = New-Node "$($f.Name)  ($($f.Cases.Count))$(if ($f.Role) { '  - ' + $f.Role })" ([pscustomobject]@{ Kind = 'File'; File = $f }) $f.About
        foreach ($c in $f.Cases) {
            $short = if ($c.Title.Length -gt 110) { $c.Title.Substring(0, 110) + '...' } else { $c.Title }
            $ci = New-Node $short ([pscustomobject]@{ Kind = 'Case'; Id = $c.Id; File = $f.Name; Text = $c.Title }) $c.Title
            $fi.Items.Add($ci) | Out-Null
            $script:CaseItems.Add($ci)
        }
        $gi.Items.Add($fi) | Out-Null
    }
    $ui.TestTree.Items.Add($gi) | Out-Null
    $script:GroupItems.Add($gi)
}

# The -Tag list for what is ticked: nothing (all) for a full run; a group's own tag where the group is ticked whole
# and no file outside it carries that tag; else the ticked cases' IDs.
function Get-SelectedTags {
    $ticked = @($script:CaseItems | Where-Object { $_.Header.IsChecked -eq $true })
    if (-not $ticked.Count) { return $null }
    # the comma: "return @()" would return nothing at all, which means "none ticked" here
    if ($ticked.Count -eq $script:CaseItems.Count) { return , @() }
    $tags = New-Object System.Collections.Generic.List[string]
    $covered = @{}
    foreach ($gi in $script:GroupItems) {
        if ($gi.Header.IsChecked -ne $true) { continue }
        $name = $gi.Tag.Name
        $carriers = @($script:Catalog | Where-Object { $_.Groups -contains $name } | ForEach-Object Name)
        $mine = @($gi.Tag.Files | ForEach-Object Name)
        if (@($carriers | Where-Object { $mine -notcontains $_ }).Count -eq 0) {
            $tags.Add($name)
            foreach ($m in $mine) { $covered[$m] = $true }
        }
    }
    foreach ($ci in $ticked) {
        if ($covered.ContainsKey($ci.Tag.File)) { continue }
        if (-not $tags.Contains($ci.Tag.Id)) { $tags.Add($ci.Tag.Id) }
    }
    , @($tags)
}

function Update-Selection {
    $n = @($script:CaseItems | Where-Object { $_.Header.IsChecked -eq $true }).Count
    $tags = Get-SelectedTags
    if ($null -eq $tags) { $ui.SelectionText.Text = "Nothing selected - $($script:CaseItems.Count) cases in $($script:Catalog.Count) files." }
    elseif (-not $tags.Count) { $ui.SelectionText.Text = "All $n cases: a full run." }
    else {
        $list = $tags -join ','
        if ($list.Length -gt 300) { $list = $list.Substring(0, 300) + ' ...' }
        $ui.SelectionText.Text = "$n of $($script:CaseItems.Count) cases.  -Tag $list"
    }
    $ui.StartButton.IsEnabled = ($null -ne $tags) -and -not $script:Running -and -not $script:Missing
}

# Start-LabRun.ps1 needs the lab password file (git-ignored, so a new checkout has none) and a signed build the
# lab's signing certificate
$script:Missing = @(foreach ($f in 'lab.local.json', 'lab-signing.cer') { if (-not (Test-Path (Join-Path $script:LabDir $f))) { $f } }) -join ', '

function Set-All([bool]$Value) {
    $script:Busy = $true
    try { foreach ($gi in $script:GroupItems) { Set-Branch $gi $Value } } finally { $script:Busy = $false }
    Update-Selection
}

$ui.AllButton.Add_Click({ Set-All $true })
$ui.NoneButton.Add_Click({ Set-All $false })

# the cases that failed in the newest run, ticked alone - to run them again
$ui.FailedButton.Add_Click({
        try {
            $rows = @(& (Join-Path $script:LabDir 'Get-LabFailures.ps1') -Root $script:ResultsRoot -PassThru)
        }
        catch { [Windows.MessageBox]::Show($_.Exception.Message, 'RemoteHub Lab') | Out-Null; return }
        $ids = @($rows | ForEach-Object Id | Sort-Object -Unique)
        if (-not $ids.Count) { [Windows.MessageBox]::Show('The newest run has no failed cases.', 'RemoteHub Lab') | Out-Null; return }
        $script:Busy = $true
        try {
            foreach ($gi in $script:GroupItems) { Set-Branch $gi $false }
            foreach ($ci in $script:CaseItems) {
                if ($ids -contains $ci.Tag.Id) { $ci.Header.IsChecked = $true; Update-Parents $ci; $ci.Parent.IsExpanded = $true; $ci.Parent.Parent.IsExpanded = $true }
            }
        }
        finally { $script:Busy = $false }
        Update-Selection
    })

# filter: cases whose ID, file or title contains every word typed; groups and files with a match open up
$ui.SearchBox.Add_TextChanged({
        $words = @($ui.SearchBox.Text -split '\s+' | Where-Object { $_ })
        foreach ($gi in $script:GroupItems) {
            $gAny = $false
            foreach ($fi in $gi.Items) {
                $fAny = $false
                foreach ($ci in $fi.Items) {
                    $hay = "$($ci.Tag.Id) $($ci.Tag.File) $($ci.Tag.Text)"
                    $hit = -not @($words | Where-Object { $hay -notlike "*$_*" }).Count
                    $ci.Visibility = if ($hit) { 'Visible' } else { 'Collapsed' }
                    if ($hit) { $fAny = $true }
                }
                $fi.Visibility = if ($fAny) { 'Visible' } else { 'Collapsed' }
                $fi.IsExpanded = $fAny -and $words.Count -gt 0
                if ($fAny) { $gAny = $true }
            }
            $gi.Visibility = if ($gAny) { 'Visible' } else { 'Collapsed' }
            $gi.IsExpanded = $gAny -and $words.Count -gt 0
        }
    })

# ---------------------------------------------------------------------------------------------------------------
# VMs and build
# ---------------------------------------------------------------------------------------------------------------

$script:Kinds = @(Get-LabKinds)
$script:VmBoxes = @()
foreach ($k in $script:Kinds) {
    $cb = New-Object Windows.Controls.CheckBox
    $cb.Content = "$($k.Id)$(if ($k.Lanes -gt 1) { "  ($($k.Lanes) lanes)" })"
    $cb.Tag = $k.Id
    $cb.IsChecked = $true
    $cb.Margin = '0,0,16,4'
    $ui.VmPanel.Children.Add($cb) | Out-Null
    $script:VmBoxes += $cb
}
if (-not $script:Kinds.Count) {
    $t = New-Object Windows.Controls.TextBlock
    $t.Text = 'No lab VMs listed (this account may not see Hyper-V) - the run uses all of them.'
    $ui.VmPanel.Children.Add($t) | Out-Null
}

$newest = Get-NewestMsiVersion
if (-not (Test-Path $script:Artifacts)) {
    $ui.MsiText.Text = "No artifacts folder in $script:Repo - link it: mklink /J `"$script:Artifacts`" C:\rh-artifacts"
    $ui.MsiText.Foreground = 'Firebrick'
}
elseif ($newest) {
    $ui.MsiText.Text = "Under test: RemoteHub $newest (the newest MSI in artifacts\)"
    $ui.BuildVersion.Text = "$($newest.Major).$($newest.Minor).$($newest.Build + 1)"
}
else { $ui.MsiText.Text = 'No agent MSI in artifacts\ yet - build one.'; $ui.BuildMsi.IsChecked = $true; $ui.BuildVersion.Text = '0.1.0' }

# ---------------------------------------------------------------------------------------------------------------
# Running: Start-LabRun.ps1 (after the build, if chosen) in a hidden Windows PowerShell, its output in a file
# ---------------------------------------------------------------------------------------------------------------

$script:Running = $false
$script:Proc = $null
$script:RunId = $null
$script:OutFile = $null
$script:Started = $null
$script:LastOut = ''

function Show-Failures([string]$Run) {
    $file = if ($Run) { Join-Path $script:ResultsRoot "$Run\failures.txt" } else { $null }
    $text = if ($file -and (Test-Path $file)) { Read-Shared $file } else {
        $a = @{ Root = $script:ResultsRoot }
        if ($Run) { $a.Run = $Run }
        try { & (Join-Path $script:LabDir 'Get-LabFailures.ps1') @a | Out-String }
        catch { "No results yet: $($_.Exception.Message)" }
    }
    $ui.FailuresBox.Text = $text
}

function Set-Running([bool]$On) {
    $script:Running = $On
    $ui.StopButton.IsEnabled = $On
    foreach ($c in @($ui.BuildMsi, $ui.BuildVersion, $ui.BuildHarness, $ui.TestTree)) { $c.IsEnabled = -not $On }
    Update-Selection
}

$ui.StartButton.Add_Click({
        $tags = Get-SelectedTags
        if ($null -eq $tags) { return }
        $kinds = @($script:VmBoxes | Where-Object { $_.IsChecked -eq $true } | ForEach-Object Tag)
        if ($script:VmBoxes.Count -and -not $kinds.Count) { [Windows.MessageBox]::Show('Choose at least one VM.', 'RemoteHub Lab') | Out-Null; return }
        $q = { param($s) "'" + ($s -replace "'", "''") + "'" }
        $cmd = New-Object Text.StringBuilder
        [void]$cmd.AppendLine("`$ErrorActionPreference = 'Stop'")
        $what = if (-not $tags.Count) { 'full run' } else { "$($tags.Count) tag(s)" }
        if ($ui.BuildMsi.IsChecked) {
            $ver = $ui.BuildVersion.Text.Trim()
            if ($ver -notmatch '^\d+\.\d+\.\d+$') { [Windows.MessageBox]::Show('The version must look like 0.18.23.', 'RemoteHub Lab') | Out-Null; return }
            [void]$cmd.AppendLine("Write-Host 'Building RemoteHub $ver ...'")
            [void]$cmd.AppendLine("& $(& $q (Join-Path $script:Repo 'build\build-installer.ps1')) -Version '$ver' -LabSigning -CredentialProvider")
            [void]$cmd.AppendLine("if (`$LASTEXITCODE) { throw ""The build failed (exit `$LASTEXITCODE)."" }")
            $what += ", build $ver"
        }
        $run = "& $(& $q (Join-Path $script:LabDir 'Start-LabRun.ps1'))"
        if ($tags.Count) { $run += ' -Tag ' + (($tags | ForEach-Object { & $q $_ }) -join ',') }
        if ($script:VmBoxes.Count -and $kinds.Count -lt $script:VmBoxes.Count) { $run += ' -Vm ' + (($kinds | ForEach-Object { & $q $_ }) -join ',') }
        if (-not $ui.BuildHarness.IsChecked) { $run += ' -NoBuild' }
        [void]$cmd.AppendLine($run)
        [void]$cmd.AppendLine('exit $LASTEXITCODE')

        $dir = Join-Path $env:TEMP 'RemoteHubLabConsole'
        $null = New-Item -ItemType Directory -Path $dir -Force
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $script:OutFile = Join-Path $dir "$stamp.out.log"
        $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd.ToString()))
        $script:Proc = Start-Process powershell -ArgumentList '-NoProfile', '-EncodedCommand', $enc -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $script:OutFile -RedirectStandardError (Join-Path $dir "$stamp.err.log")
        # read once now: without an open handle .NET can't tell the exit code later (it reads as empty)
        $null = $script:Proc.Handle
        $script:RunId = $null
        $script:Started = Get-Date
        $script:LastOut = ''
        $ui.OutputBox.Text = ''
        $ui.ProgressBox.Text = ''
        $ui.FailuresBox.Text = ''
        $ui.StatusText.Text = "Started ($what)."
        $ui.Tabs.SelectedIndex = 1
        Set-Running $true
    })

$ui.StopButton.Add_Click({
        $answer = [Windows.MessageBox]::Show('Stop this run? The lab cancels the scenario it is running; the VMs are reverted by the next run.', 'RemoteHub Lab', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { return }
        if ($script:RunId) {
            # the watcher cancels the request; Start-LabRun then copies what there is and ends by itself
            Start-Process powershell -ArgumentList '-NoProfile', '-File', "`"$(Join-Path $script:LabDir 'Stop-LabRun.ps1')`"" -WindowStyle Hidden
            $ui.StatusText.Text = 'Stopping - the lab ends the running scenario ...'
        }
        elseif ($script:Proc -and -not $script:Proc.HasExited) {
            # still building or not queued yet: end the process tree
            & taskkill.exe /T /F /PID $script:Proc.Id | Out-Null
        }
    })

$ui.ResultsButton.Add_Click({
        $dir = if ($script:RunId -and (Test-Path (Join-Path $script:ResultsRoot $script:RunId))) { Join-Path $script:ResultsRoot $script:RunId } else { $script:ResultsRoot }
        if (Test-Path $dir) { Start-Process explorer.exe $dir } else { [Windows.MessageBox]::Show("No results in $dir yet.", 'RemoteHub Lab') | Out-Null }
    })

# every 2 s: the run's output, its progress from the lab's console.log, and the end
$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$timer.Add_Tick({
        if (-not $script:Running) { return }
        $out = Read-Shared $script:OutFile
        if ($out -ne $script:LastOut) {
            $script:LastOut = $out
            $ui.OutputBox.Text = $out
            $ui.OutputBox.ScrollToEnd()
        }
        if (-not $script:RunId -and $out -match '(?m)^Run (\d{8}-\d{6})') { $script:RunId = $Matches[1] }
        $elapsed = '{0:hh\:mm\:ss}' -f ((Get-Date) - $script:Started)
        $status = if ($out -match 'Building RemoteHub' -and -not $script:RunId) { 'Building the MSIs' } elseif (-not $script:RunId) { 'Preparing' } else { "Run $script:RunId" }
        if ($script:RunId) {
            $log = Read-Shared (Join-Path $script:Cfg.LabRoot "results\$script:RunId\console.log")
            $lines = @($log -split "`r?`n" | Where-Object { $_ -match '^\d\d:\d\d:\d\d (start|done) ' -or $_ -match 'file runs on' })
            $total = if ($log -match '(\d+) file runs? on') { [int]$Matches[1] } else { 0 }
            $done = @($lines | Where-Object { $_ -match '^\d\d:\d\d:\d\d done ' }).Count
            $failed = @($lines | Where-Object { $_ -match 'done .*, [1-9]\d* failed' }).Count
            $text = $lines -join "`r`n"
            if ($ui.ProgressBox.Text -ne $text) { $ui.ProgressBox.Text = $text; $ui.ProgressBox.ScrollToEnd() }
            if ($total) { $status += " - $done of $total files done$(if ($failed) { ", $failed with failures" })" }
            elseif (-not $log) { $status += ' - queued, waiting for the lab' }
            else { $status += ' - preparing the VMs' }
        }
        $ui.StatusText.Text = "$status  ($elapsed)"
        if ($script:Proc.HasExited) {
            $code = $script:Proc.ExitCode
            Set-Running $false
            $ui.StatusText.Text = if ($script:RunId) { "Run $script:RunId finished after $elapsed - $(if ($code -eq 0) { 'all passed' } else { "exit code $code" })." } else { "Ended after $elapsed before a run started (exit code $code) - see Output." }
            if ($script:RunId) { Show-Failures $script:RunId; $ui.Tabs.SelectedIndex = 0 } else { $ui.Tabs.SelectedIndex = 2 }
        }
    })
$timer.Start()

$window.Add_Closing({
        param($s, $e)
        if ($script:Running) {
            $a = [Windows.MessageBox]::Show('A run is going on. It goes on without this window (Stop ends it). Close anyway?', 'RemoteHub Lab', 'YesNo', 'Question')
            if ($a -ne 'Yes') { $e.Cancel = $true }
        }
    })

Update-Selection
Show-Failures $null
$ui.StatusText.Text = if ($script:Missing) {
    "Can't start: $script:Missing missing in $script:LabDir (git-ignored - copy them from the checkout the lab ran from before)."
} else { "$($script:CaseItems.Count) cases in $($script:Catalog.Count) files. The newest run's failures are shown below." }
if ($script:Missing) { $ui.StatusText.Foreground = 'Firebrick' }
$null = $window.ShowDialog()
