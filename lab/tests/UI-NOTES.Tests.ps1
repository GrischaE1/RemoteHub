# UI-NOTE - the Notes view in RemoteHub's own window on the admin's PC (this client VM, the console account's session,
# UI Automation through RhPerson.ps1 -Do uia; the clipboard set and read in that session with -Do clip-set / clip-get).
#
# Covers (docs/test-lab-cases.md section 10.8):
#   UI-NOTE-01 the view rail switches to Notes and back, UI-NOTE-02 a new page with a title,
#   UI-NOTE-03 paste into a script block, UI-NOTE-04 View Markdown shows the page as Markdown,
#   UI-NOTE-05 an edit in View Markdown comes back formatted, UI-NOTE-06 Copy page as Markdown,
#   UI-NOTE-07 after a restart: the page, the view and search.
# Needs: one client VM with RemoteHub installed (Install-LabClient). No device, no mock server.
# The order matters - each context builds on the page the one before made. Client VMs only.
# LabRole: console
# TimeLimitMin: 30
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
    # BeforeDiscovery's variables don't reach the run: without this the setup below was skipped (run 20261021-062735)
    $isClient = Test-LabConsoleVm -Name $VmName
    $R = [ordered]@{}
    $title = 'Lab notes page'
    $script = "Get-Service -Name 'Spooler' | Select-Object Status"
    function Save($name, $data) { $data | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDir "ui-notes-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "ui-notes-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # the text of an editor (a script block, the Markdown view, the page): its Value or Text pattern, line ends as \n
    function Read-Ui([string]$Text) { "$((Ui $Text -Value read).value)" -replace "`r`n", "`n" }
    # waits until an element's text has -Like in it (the editors update after the UIA step returns)
    function Wait-Text([string]$Text, [string]$Like, [int]$TimeoutSec = 15) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $t = Read-Ui $Text
            if ($t -like $Like) { return $t }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        throw "'$Text' did not get '$Like' within $TimeoutSec s; it has: $($t.Substring(0, [Math]::Min(300, $t.Length)))"
    }
    # each part on its own: a failing step is written down (error, screenshot) and the next one still runs
    function Step([string]$Name, [scriptblock]$Do) {
        try { $R[$Name] = . $Do }
        catch { $R["$Name-error"] = "$_"; Shot "fail-$Name" }
    }
    $script:task = $null

    if ($isClient) {
        Reset-LabVm -Name $VmName
        $null = Install-LabClient -Name $VmName
        $script:task = Start-LabClient -Name $VmName

        # ---- 1. The rail: Notes, then Remote Control, then Notes again
        Step 'rail' {
            $null = Ui 'Notes'
            $notes = (Ui 'Add page' -Value exists).found
            $null = Ui 'Remote Control'
            $remote = (Ui 'SearchBox' -Value exists).found
            $null = Ui 'Notes'
            @{ notes = $notes; remote = $remote; again = (Ui 'Notebooks and sections' -Value exists).found }
        }

        # ---- 2. A new page in the first section ("Quick Notes", made on the first start)
        Step 'page' {
            $null = Ui 'Add page'
            $null = Ui 'Page title' -Value "set:$title"
            Start-Sleep -Seconds 2   # autosave runs a second after the last change
            @{ listed = (Ui $title -Value exists -TimeoutSec 15).found; section = (Ui 'Quick Notes' -Value exists).found }
        }

        # ---- 3. A script block from the format bar, the script pasted into it with Ctrl+V
        Step 'paste' {
            # the format bar's button (a backtick in a wildcard pattern escapes the next character, so it ends in *)
            $null = Ui 'Script block (Ctrl+Shift+*'
            $null = Invoke-LabPerson -Name $VmName -Do clip-set -Value $script
            $null = Ui 'Script block' -Value 'keys:^v'
            $block = Wait-Text 'Script block' "*$script*"
            Shot 'pasted'
            @{ block = $block }
        }

        # ---- 4. View Markdown: the page as Markdown text; the format buttons off
        Step 'markdown' {
            $null = Ui 'View Markdown' -Value 'toggle-on'
            $md = Wait-Text 'Markdown' '*```*'
            Shot 'markdown'
            @{ markdown = $md; boldEnabled = (Ui 'Bold (Ctrl+B)' -Value exists).enabled; toggle = (Ui 'View Markdown' -Value read).value }
        }

        # ---- 5. A heading added in the Markdown text, then back to the formatted page
        Step 'edit' {
            $md = Read-Ui 'Markdown'
            $null = Ui 'Markdown' -Value "set:$($md.TrimEnd("`n"))`n`n## Lab heading`n"
            $null = Ui 'View Markdown' -Value 'toggle-off'
            $page = Wait-Text 'Page' '*Lab heading*'
            Shot 'formatted'
            @{ page = $page; toggle = (Ui 'View Markdown' -Value read).value }
        }

        # ---- 6. Copy page as Markdown: title, script block and the new heading as plain Markdown text
        Step 'copy' {
            $null = Ui 'Copy page as Markdown'
            Start-Sleep -Seconds 1
            $c = Invoke-LabPerson -Name $VmName -Do clip-get
            @{ length = $c.length; start = $c.start; formats = @($c.formats) }
        }

        # ---- 7. Restarted: Notes shown again (the last view), the page kept, search finds it by the script
        Step 'restart' {
            Start-Sleep -Seconds 2
            Stop-LabClient -Name $VmName -Task $script:task
            $script:task = Start-LabClient -Name $VmName
            $listed = (Ui $title -Value exists -TimeoutSec 30).found
            $null = Ui 'Search notes' -Value 'set:Spooler'
            Start-Sleep -Seconds 2
            $found = (Ui $title -Value exists -TimeoutSec 15).found
            $null = Ui 'Search notes' -Value 'set:'
            $null = Ui $title
            @{ listed = $listed; found = $found; markdownView = (Ui 'View Markdown' -Value read).value; block = (Read-Ui 'Script block') }
        }
        Save 'results' $R
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        Stop-LabClient -Name $VmName -Task $script:task
    }
}

Describe 'UI-NOTES <VmName>' -Tag Client, UI, 'UI-NOTE' -Skip:(-not $isClient) {

    It 'UI-NOTE-01 the view rail switches to Notes and back' -Tag 'UI-NOTE-01' {
        $R['rail-error'] | Should -BeNullOrEmpty
        $R.rail.notes | Should -BeTrue
        $R.rail.remote | Should -BeTrue
        $R.rail.again | Should -BeTrue
    }

    It 'UI-NOTE-02 a new page gets its title and shows in the section''s list' -Tag 'UI-NOTE-02' {
        $R['page-error'] | Should -BeNullOrEmpty
        $R.page.listed | Should -BeTrue
    }

    It 'UI-NOTE-03 Ctrl+V pastes into a script block' -Tag 'UI-NOTE-03' {
        $R['paste-error'] | Should -BeNullOrEmpty
        $R.paste.block | Should -BeLike "*$script*"
    }

    It 'UI-NOTE-04 View Markdown shows the page as Markdown text, with the format buttons off' -Tag 'UI-NOTE-04' {
        $R['markdown-error'] | Should -BeNullOrEmpty
        $R.markdown.markdown | Should -BeLike "*``````*$script*``````*"
        $R.markdown.boldEnabled | Should -BeFalse
        $R.markdown.toggle | Should -Be 'On'
    }

    It 'UI-NOTE-05 an edit in View Markdown comes back formatted' -Tag 'UI-NOTE-05' {
        $R['edit-error'] | Should -BeNullOrEmpty
        $R.edit.page | Should -BeLike '*Lab heading*'
        $R.edit.page | Should -Not -BeLike '*## Lab heading*'
        $R.edit.toggle | Should -Be 'Off'
    }

    It 'UI-NOTE-06 Copy page as Markdown puts the page on the clipboard as Markdown text' -Tag 'UI-NOTE-06' {
        $R['copy-error'] | Should -BeNullOrEmpty
        $R.copy.start | Should -BeLike "# $title*"
        $R.copy.length | Should -BeGreaterThan ($title.Length + $script.Length)
    }

    It 'UI-NOTE-07 after a restart Notes shows again, the page is kept and search finds it' -Tag 'UI-NOTE-07' {
        $R['restart-error'] | Should -BeNullOrEmpty
        $R.restart.listed | Should -BeTrue
        $R.restart.found | Should -BeTrue
        $R.restart.markdownView | Should -Be 'Off'
        $R.restart.block | Should -BeLike "*$script*"
    }
}
