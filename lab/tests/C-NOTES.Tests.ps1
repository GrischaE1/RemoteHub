# C-NOTE - RemoteHub's Notes view and the view rail on the admin's PC (this client VM), end to end in the real app:
# switching views with the rail and Ctrl+1 / Ctrl+2 and the last view coming back after a restart; a notebook, section
# and page made through the UI; Markdown typed at line starts and Ctrl+B; a table and the other formats pasted as
# Markdown, plus HTML from a "browser", unchanged after a restart and not rewritten by opening; a pasted script block
# (documentation only - nothing runs it); images pasted as a picture and as a file (re-encoded, metadata gone); nothing
# of a note in plain text on disk; deleting with Undo and Recently deleted, including the separate stamps of a page and
# its section deleted after it (PR #37); search; the AllowNotes policy; notes in a .rhbackup. Typing goes through UI
# Automation values, the clipboard and SendKeys in the session (Invoke-LabUi keys:), not the console keyboard. The
# database is read in the console account's session with RemoteHub's own sqlite3mc.dll (Invoke-LabClientSql).
# Client VMs only. See docs/test-lab-cases.md section 10.8 and docs/notes-view-design.md.
# TimeLimitMin: 90   (seven client starts, about 40 database reads in the session, two file dialogs: about 60 min)
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
    $vm = Get-LabVm $VmName
    $isClient = Test-LabConsoleVm -Name $VmName
    $adminHome = "C:\Users\$($vm.ConsoleAccount)"
    $localDir = "$adminHome\AppData\Local\RemoteHub"
    $roamDir = "$adminHome\AppData\Roaming\RemoteHub"
    $tempDir = "$adminHome\AppData\Local\Temp"
    # test values of this file (not the lab password)
    $backupPw = 'Lab-Notes-Backup-91'
    # words that occur only in this file's notes: searched for on disk (C-NOTE-07) and with the search box (C-NOTE-10)
    $rand = Get-Random -Minimum 10000 -Maximum 99999
    $tok = [ordered]@{ title = "Title$rand"; section = "Section$rand"; body = "Body$rand"; cell = "Cell$rand"; code = "Code$rand"; html = "Html$rand"; gone = "Zulu$rand" }
    $book = 'Lab Notebook C2'
    $section = "Lab $($tok.section)"
    $pageC2 = "Lab page $($tok.title)"
    $pageGone = "Gone $($tok.gone)"
    # a script block that would leave this file behind if anything ran it (C-NOTE-05)
    $ranFile = 'C:\ProgramData\RemoteHubLab\c-notes-ran.txt'
    $code5 = "New-Item -ItemType File -Path '$ranFile' -Force`nGet-Service -Name '$($tok.code)' | Where-Object Status -ne 'Running'"
    # a comment segment put into the JPEG of C-NOTE-06: it must not reach the stored image
    $metaMark = "RHLABMETA$rand"
    $metaHex = -join ([Text.Encoding]::ASCII.GetBytes($metaMark) | ForEach-Object { $_.ToString('X2') })
    $md4 = @'
## Round trip C4

Some **bold**, *italic*, ~~struck~~ and `inline code` with a [link](https://example.com/c4).

- [ ] open task
- [x] done task

> A quoted line

| Host | Port | Note |
| --- | ---: | :---: |
| srv-c4-01 | 3389 | **main** |
| srv-c4-02 | 5986 | CELLTOKEN |

```sql
SELECT name FROM sys.databases WHERE name = 'c4';
```

1. first
2. second

End of C4.
'@
    $md4 = $md4.Replace('CELLTOKEN', $tok.cell)
    # what a browser puts on the clipboard: formats, a style, a script and a remote image that must not come along
    $html4 = '<p><b>' + $tok.html + '</b> from a <span style="color:#ff0000;font-family:Comic Sans MS">web page</span><script>alert("c4")</script></p>' +
        '<table><tr><th>HtmlHead</th><th>Second</th></tr><tr><td>hcell1</td><td>hcell2</td></tr></table>' +
        '<p><img src="http://198.51.100.7/c4.png" alt="remote-alt-c4"></p>'
    $pageCols = 'id, section_id, title, body, body_text, created_utc, modified_utc, version, deleted_utc'
    $filesSql = "SELECT f.id, f.note_id, n.title, f.mime, f.width, f.height, f.size, length(f.data) AS len, hex(substr(f.data, 1, 8)) AS head, " +
        "instr(f.data, X'$metaHex') AS meta, hex(f.sha256) AS sha FROM note_files f JOIN notes n ON n.id = f.note_id ORDER BY n.title, f.width"
    $script:rhTask = $null

    function Save($name, $data) { $data | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $EvidenceDir "c-notes-$name.json") -Encoding UTF8 }
    function Shot([string]$name) { try { $null = Get-LabScreenshot -Name $VmName -Path (Join-Path $EvidenceDir "c-notes-$name.png") } catch { } }
    function Ui([string]$Text, [string]$Value = 'click', [string]$Title = 'RemoteHub*', [int]$TimeoutSec = 30, [switch]$Optional) {
        Invoke-LabUi -Name $VmName -Text $Text -Value $Value -Title $Title -TimeoutSec $TimeoutSec -Optional:$Optional
    }
    # whether an element is shown (UI Automation finds hidden elements too, but not as visible: the hidden view's are not)
    function Shown([string]$Text, [int]$TimeoutSec = 5, [string]$Title = 'RemoteHub*') {
        [bool](Ui $Text -Value exists -Title $Title -Optional -TimeoutSec $TimeoutSec).found
    }
    # seconds until an element is no longer shown, or -1 after -TimeoutSec
    function Wait-Gone([string]$Text, [int]$TimeoutSec = 30) {
        $start = Get-Date
        do {
            if (-not (Shown $Text 2)) { return [int]((Get-Date) - $start).TotalSeconds }
            Start-Sleep -Seconds 1
        } while (((Get-Date) - $start).TotalSeconds -lt $TimeoutSec)
        -1
    }
    # each part on its own: a failing step is written down (error, screenshot, open windows) and the next one still runs
    function Step($Into, [string]$Name, [scriptblock]$Do) {
        try { . $Do }
        catch {
            $Into["$Name-error"] = "$_"
            Shot "fail-$Name"
            try { $Into["$Name-dialogs"] = @((Ui '*' -Title '?*' -Value list -Optional -TimeoutSec 3).items | Select-Object -First 60) } catch { }
        }
    }
    # A message box: the first of -Titles that shows up (RemoteHub's message boxes are named by their text in UI
    # Automation, a few by their title), its text, then -Button in that window ('' = leave it open).
    function Box([string[]]$Titles, [string]$Button = 'OK', [int]$TimeoutSec = 60) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            foreach ($t in $Titles) {
                $m = Ui '*' -Title $t -Value list -Optional -TimeoutSec 3
                if ($m.found) {
                    if ($Button) { $null = Ui $Button -Title ([WildcardPattern]::Escape($m.window)) -Optional -TimeoutSec 10 }
                    return "$($m.window)`n$(@($m.items) -join "`n")"
                }
            }
        } while ((Get-Date) -lt $deadline)
        ''
    }
    # RemoteHub started without waiting for a particular window (it may come up in Notes, where Start-LabClient's
    # 'New' button is hidden)
    function Start-Rh { $script:rhTask = Start-LabInSession -Name $VmName -Account Console -ScriptBlock { & 'C:\Program Files\RemoteHub\RemoteHub.exe' } }
    # the view the main window shows once it is up: 'Notes', 'Remote', or '' when it didn't come up
    function Wait-Main([int]$TimeoutSec = 120) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        do {
            $view = if (Shown 'Notebooks and sections' 2) { 'Notes' } elseif (Shown 'SearchBox' 2) { 'Remote' } else { '' }
            if ($view) {
                # the welcome tour comes modal over a fresh profile (Clear-RhData) and keeps real input from the window
                $null = Close-LabFirstStart -Name $VmName
                # moved into the screen, so real clicks reach all of it
                $null = Ui 'Remote Control' -Value fit -Optional -TimeoutSec 10
                return $view
            }
        } while ((Get-Date) -lt $deadline)
        ''
    }
    # closed like the user does, so the open page is saved and the database written to the end; killed only when it
    # doesn't end
    function Close-Rh {
        try { $null = Invoke-LabInSession -Name $VmName -Account Console -TimeoutSec 60 -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | ForEach-Object { $null = $_.CloseMainWindow() } } } catch { }
        $gone = Invoke-LabGuest -Name $VmName -ScriptBlock {
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            $left = @(Get-Process RemoteHub -ErrorAction SilentlyContinue)
            $left | Stop-Process -Force -ErrorAction SilentlyContinue
            $left.Count -eq 0
        }
        if ($script:rhTask) { try { Stop-LabInSession -Name $VmName -Task $script:rhTask } catch { }; $script:rhTask = $null }
        $gone
    }
    function Clear-RhData {
        Invoke-LabGuest -Name $VmName -ArgumentList $localDir, $roamDir -ScriptBlock {
            param($l, $r)
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force
            Get-Process RemoteHub -ErrorAction SilentlyContinue | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
            Remove-Item $l, $r -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    function Policy($Value) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Value -ScriptBlock {
            param($v)
            $k = 'HKLM:\SOFTWARE\Policies\RemoteHub'; $null = New-Item $k -Force
            if ($null -eq $v) { Remove-ItemProperty $k -Name AllowNotes -ErrorAction SilentlyContinue } else { Set-ItemProperty $k -Name AllowNotes -Value $v -Type DWord }
        }
    }
    function Settings-Page([string]$Page) {
        $null = Ui 'Settings'
        $null = Ui $Page -Title 'Settings'
    }
    # Windows' Save As / Open dialog: no usable UIA patterns, so the path is typed at the console keyboard into the file
    # name box (focused when the dialog opens) and Enter pressed - as C-DATA does for its backups.
    function FileDialog([string]$Path, [string]$Title = 'Save As') {
        $null = Ui '*' -Title $Title -Value exists -Optional -TimeoutSec 20
        Start-Sleep -Seconds 2
        $null = Ui 'File name:' -Title $Title -Value realclick -Optional -TimeoutSec 5
        Send-LabKeyCombo -Name $VmName -Codes 0x11, 0x41
        Send-LabText -Name $VmName -Text $Path
        Start-Sleep -Milliseconds 500
        Send-LabKey -Name $VmName -Key Enter
        Start-Sleep -Seconds 2
    }

    # ---- the clipboard of the console account's session (what a paste in RemoteHub reads)
    function Set-ClipText([string]$Text) {
        $null = Invoke-LabInSessionJson -Name $VmName -Data @{ text = $Text } -Code @'
Add-Type -AssemblyName System.Windows.Forms
[Windows.Forms.Clipboard]::SetDataObject([string]$A.text, $true, 20, 250)
'set'
'@
    }
    function Get-ClipText {
        $r = Invoke-LabInSessionJson -Name $VmName -Code @'
Add-Type -AssemblyName System.Windows.Forms
$t = $null
foreach ($i in 1..20) { try { $t = [Windows.Forms.Clipboard]::GetText(); break } catch { Start-Sleep -Milliseconds 250 } }
@{ text = "$t" }
'@
        "$($r.text)" -replace "`r`n", "`n"
    }
    # HTML as a browser copies it (CF_HTML with its header of byte offsets), with a plain-text version beside it
    function Set-ClipHtml([string]$Fragment, [string]$Text) {
        $null = Invoke-LabInSessionJson -Name $VmName -Data @{ frag = $Fragment; text = $Text } -Code @'
Add-Type -AssemblyName System.Windows.Forms
$pre = '<html><body><!--StartFragment-->'
$post = '<!--EndFragment--></body></html>'
$head = "Version:0.9`r`nStartHTML:{0:D10}`r`nEndHTML:{1:D10}`r`nStartFragment:{2:D10}`r`nEndFragment:{3:D10}`r`n"
$h = ($head -f 0, 0, 0, 0).Length
$f = (New-Object Text.UTF8Encoding $false).GetByteCount([string]$A.frag)
$s = $h + $pre.Length
$cf = ($head -f $h, ($s + $f + $post.Length), $s, ($s + $f)) + $pre + [string]$A.frag + $post
$o = New-Object Windows.Forms.DataObject
$o.SetData([Windows.Forms.DataFormats]::Html, $cf)
$o.SetData([Windows.Forms.DataFormats]::UnicodeText, [string]$A.text)
[Windows.Forms.Clipboard]::SetDataObject($o, $true, 20, 250)
'set'
'@
    }
    # a 64 x 48 picture, as a screenshot tool puts it on the clipboard
    function Set-ClipBitmap {
        $null = Invoke-LabInSessionJson -Name $VmName -Code @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = New-Object Drawing.Bitmap 64, 48
$g = [Drawing.Graphics]::FromImage($b)
$g.Clear([Drawing.Color]::FromArgb(200, 40, 40))
$g.FillRectangle([Drawing.Brushes]::White, 0, 0, 32, 24)
$g.Dispose()
[Windows.Forms.Clipboard]::SetImage($b)
'set'
'@
    }
    # a 120 x 90 JPEG with a comment segment (-Meta) after its start marker, offered as a copied file (Explorer's Copy)
    function Set-ClipJpegFile([string]$Meta) {
        $null = Invoke-LabInSessionJson -Name $VmName -Data @{ meta = $Meta } -Code @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$dir = 'C:\Users\Public\c-notes'
$null = New-Item -ItemType Directory -Path $dir -Force
$b = New-Object Drawing.Bitmap 120, 90
$g = [Drawing.Graphics]::FromImage($b)
$g.Clear([Drawing.Color]::FromArgb(30, 120, 200))
$g.FillRectangle([Drawing.Brushes]::Orange, 10, 10, 50, 40)
$g.Dispose()
$ms = New-Object IO.MemoryStream
$b.Save($ms, [Drawing.Imaging.ImageFormat]::Jpeg)
$jpg = $ms.ToArray()
$com = [Text.Encoding]::ASCII.GetBytes([string]$A.meta)
$len = $com.Length + 2
$seg = [byte[]](@(0xFF, 0xFE, [byte]($len -shr 8), [byte]($len -band 0xFF)) + $com)
$file = Join-Path $dir 'photo-c6.jpg'
[IO.File]::WriteAllBytes($file, [byte[]](@($jpg[0], $jpg[1]) + $seg + $jpg[2..($jpg.Length - 1)]))
$list = New-Object Collections.Specialized.StringCollection
$null = $list.Add($file)
[Windows.Forms.Clipboard]::SetFileDropList($list)
'set'
'@
    }

    # ---- the database
    function Sql([hashtable]$Query) { Invoke-LabClientSql -Name $VmName -Query $Query }
    function Q([string]$s) { $s -replace "'", "''" }
    function Page-Row([string]$Title) { @((Sql @{ p = "SELECT $pageCols FROM notes WHERE title = '$(Q $Title)'" }).p) | Select-Object -First 1 }
    # the page's row once it is saved with -Pattern in its Markdown (autosave: a second after the last change), or as it
    # is after -TimeoutSec
    function Wait-Page([string]$Title, [string]$Pattern = '', [int]$TimeoutSec = 45) {
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $row = $null
        do {
            try { $row = Page-Row $Title } catch { $row = $null }
            if ($row -and "$($row.body)" -match $Pattern) { return $row }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
        $row
    }
    # every page, image and audit entry as stored
    function Snapshot {
        Sql @{
            pages    = "SELECT $pageCols FROM notes ORDER BY title"
            sections = 'SELECT id, notebook_id, name, deleted_utc FROM note_sections'
            books    = 'SELECT id, name, deleted_utc FROM notebooks'
            files    = $filesSql
            audit    = 'SELECT event, target, detail FROM audit ORDER BY id'
        }
    }
    # A new page in the selected section with its title (a UI Automation value). Add page works through the dispatcher:
    # the title is set once the new, empty page is open.
    function Add-Page([string]$Title) {
        $null = Ui 'Add page'
        $deadline = (Get-Date).AddSeconds(20)
        do {
            $t = Ui 'Page title' -Value read -Optional -TimeoutSec 3
            if ($t.found -and "$($t.value)" -eq '') { break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        $null = Ui 'Page title' -Value "set:$Title"
    }
    # The search box's answer: the list header ("1 page found", "No pages found") and whether -Expect is in the list.
    # Emptied first, so the header of the search before isn't read.
    function Search([string]$Text, [string]$Expect) {
        $null = Ui 'Search notes' -Value 'set:'
        $null = Wait-Gone '*page*found' 10
        $null = Ui 'Search notes' -Value "set:$Text"
        $h = Ui '*page*found' -Value read -Optional -TimeoutSec 15
        [ordered]@{ text = $Text; head = "$($h.value)"; shown = $(if ($Expect) { Shown $Expect 5 } else { $false }) }
    }
    # Files under -Paths (folders searched through) that hold any of -Words as UTF-8 or UTF-16 - read with full sharing,
    # so files RemoteHub keeps open count too - and the first 16 bytes of the database file.
    function Find-Plain([string[]]$Paths, [string[]]$Words) {
        Invoke-LabGuest -Name $VmName -ArgumentList $Paths, $Words, "$localDir\remotehub.db" -ScriptBlock {
            param($paths, $words, $db)
            function ReadShared([string]$p, [int]$max) {
                $s = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite, Delete')
                try {
                    $n = [int][Math]::Min([long]$max, $s.Length)
                    $buf = New-Object byte[] $n
                    $got = 0
                    while ($got -lt $n) { $r = $s.Read($buf, $got, $n - $got); if ($r -le 0) { break }; $got += $r }
                    # the comma: returned bare, the array is unrolled into one pipeline object per byte - for a large
                    # file more than .NET can hold ("Array dimensions exceeded supported range", lab run 20261019-190129)
                    , $buf
                }
                finally { $s.Dispose() }
            }
            $latin = [Text.Encoding]::GetEncoding(28591)
            $needles = @(foreach ($w in $words) { $latin.GetString([Text.Encoding]::UTF8.GetBytes($w)); $latin.GetString([Text.Encoding]::Unicode.GetBytes($w)) })
            $files = @(Get-ChildItem -LiteralPath $paths -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Length -lt 200MB })
            $hits = @(foreach ($f in $files) {
                    $bytes = try { ReadShared $f.FullName ([int]::MaxValue) } catch { $null }
                    if (-not $bytes) { continue }
                    $t = $latin.GetString($bytes)
                    $found = @($needles | Where-Object { $t.Contains($_) })
                    if ($found.Count) { $f.FullName }
                })
            $head = if (Test-Path -LiteralPath $db) { try { $latin.GetString((ReadShared $db 16)) } catch { "error: $_" } } else { '' }
            [pscustomobject]@{ searched = $files.Count; files = @($files | ForEach-Object FullName); hits = $hits; dbHead = $head }
        }
    }
    function Ran { Invoke-LabGuest -Name $VmName -ArgumentList $ranFile -ScriptBlock { param($f) Test-Path -LiteralPath $f } }

    if ($isClient) {
        # always from the checkpoint: the file fills RemoteHub's database, sets a policy and replaces the data
        $null = Initialize-LabVm -Name $VmName -Client -Clean
        $null = Invoke-LabGuest -Name $VmName -ArgumentList $ranFile -ScriptBlock {
            param($ran)
            Remove-Item -LiteralPath $ran -Force -ErrorAction SilentlyContinue
            # C:\lab: the backup file (every account may write)
            $null = New-Item -ItemType Directory -Path 'C:\lab' -Force
            icacls 'C:\lab' /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null
        }
    }
}

AfterAll {
    if ($isClient) {
        Shot 'end'
        try { Invoke-LabGuest -Name $VmName -ScriptBlock { Get-Process RemoteHub -ErrorAction SilentlyContinue | Stop-Process -Force } } catch { }
        if ($script:rhTask) { try { Stop-LabInSession -Name $VmName -Task $script:rhTask } catch { } }
        try { Policy $null } catch { }
        # RemoteHub's data, a policy, files in C:\lab and C:\Users\Public: the next file starts from the checkpoint
        Set-LabVmDirty -Name $VmName
    }
}

Describe 'C-NOTES <VmName>' -Tag Client, UI, 'C-NOTE' -Skip:(-not $isClient) {

    Context 'view rail and pages' {
        BeforeAll {
            $v = [ordered]@{}
            Start-Rh
            $v.firstView = Wait-Main
            # C-NOTE-01: the rail, both views, the shortcuts
            Step $v 'rail' {
                $v.railRemote = Shown 'Remote Control'
                $v.railNotes = Shown 'Notes'
                $v.railSettings = Shown 'Settings'
                $null = Ui 'Notes'
                $v.notesShown = Shown 'Notebooks and sections' 20
                $v.remoteHiddenInNotes = -not (Shown 'SearchBox' 3)
                $v.defaultBook = Shown 'Notebook Notes'
                $v.defaultSection = Shown 'Quick Notes'
                Shot 'c-note-01-notes'
                $null = Ui 'Search notes' -Value 'keys:^1'
                $v.ctrl1 = Shown 'SearchBox' 10
                $v.notesHiddenInRemote = -not (Shown 'Notebooks and sections' 3)
                $null = Ui 'SearchBox' -Value 'keys:^2'
                $v.ctrl2 = Shown 'Notebooks and sections' 10
            }
            # C-NOTE-02: a notebook, a section in it and a page, each through its own dialog or button
            Step $v 'create' {
                $null = Ui 'New notebook'
                $null = Ui 'Name of the notebook' -Title 'New notebook' -Value "setnext:$book"
                $null = Ui 'OK' -Title 'New notebook'
                $v.bookShown = Shown "Notebook $book" 15
                # "+ New section" opens on a real click or Enter only (selecting it by the keyboard must not)
                $null = Ui "New section in $book" -Value realclick
                $null = Ui 'Name of the new section*' -Title 'New section' -Value "setnext:$section"
                $null = Ui 'OK' -Title 'New section'
                $v.sectionShown = Shown $section 15
                Add-Page $pageC2
                $v.c2 = Wait-Page $pageC2
                $v.c2Listed = Shown $pageC2 10
                $v.c2db = Sql @{ books = 'SELECT id, name FROM notebooks WHERE deleted_utc IS NULL'; sections = 'SELECT id, notebook_id, name FROM note_sections WHERE deleted_utc IS NULL' }
            }
            # C-NOTE-03: Markdown typed at the start of a line and Ctrl+B (SendKeys in the session), saved without a Save
            Step $v 'typed' {
                Add-Page 'Typed C3'
                # the marker and its space each sent on their own: the editor formats when a space arrives after "-" or
                # "#" at a line start, as when typed. "- " and "# " stayed text in runs 20261021-222745 and
                # 20261022-061254 because the app formatted only on a text input event, which a keyboard space doesn't
                # raise in a RichTextBox (fixed in the app: the Space key formats)
                $null = Ui 'Page' -Value 'keys:-'
                $null = Ui 'Page' -Value 'keys: '
                $null = Ui 'Page' -Value 'keys:alpha item{ENTER}beta item{ENTER}^.'
                $null = Ui 'Page' -Value "keys:^b$($tok.body)^b and plain{ENTER}"
                $null = Ui 'Page' -Value 'keys:#'
                $null = Ui 'Page' -Value 'keys: '
                $null = Ui 'Page' -Value 'keys:Typed heading'
                $v.c3 = Wait-Page 'Typed C3' 'Typed heading'
                $null = Ui 'Copy page as Markdown'
                $v.c3Copy = Get-ClipText
            }
            # C-NOTE-04: every element pasted as Markdown, then HTML as a browser copies it
            Step $v 'roundtrip' {
                Add-Page 'Round trip C4'
                Set-ClipText $md4
                $null = Ui 'Page' -Value 'menu:Paste Markdown'
                $v.c4md = Wait-Page 'Round trip C4' ([regex]::Escape($tok.cell))
                Set-ClipHtml $html4 "$($tok.html) from a web page"
                $null = Ui 'Page' -Value 'keys:^{END}^v'
                $v.c4 = Wait-Page 'Round trip C4' ([regex]::Escape($tok.html))
            }
            # C-NOTE-05: code pasted into an empty page becomes a PowerShell script block; nothing offers to run it
            Step $v 'script' {
                Add-Page 'Script C5'
                Set-ClipText $code5
                $null = Ui 'Page' -Value 'keys:^v'
                $v.c5Lang = Shown 'Language: PowerShell' 15
                $v.c5Read = "$((Ui 'Script block' -Value read -Optional -TimeoutSec 10).value)"
                $null = Ui 'Copy script block'
                $v.c5Copied = Shown 'Copied' 2
                $v.c5Clip = Get-ClipText
                # a menu entry that isn't there: the step answers the entries of the open menu (and closes it)
                $v.c5BlockMenu = @((Ui 'More actions for this script block' -Value 'pick:zz-no-such-entry' -Optional).entries)
                $v.c5PageMenu = @((Ui 'Page' -Value 'menu:zz-no-such-entry' -Optional).entries)
                $v.c5 = Wait-Page 'Script C5' ([regex]::Escape($tok.code))
            }
            # C-NOTE-06: a picture from the clipboard, then a JPEG file with a comment segment
            Step $v 'images' {
                Add-Page 'Image C6'
                Set-ClipBitmap
                $null = Ui 'Page' -Value 'keys:^v'
                $v.c6a = Wait-Page 'Image C6' 'note-file:'
                Set-ClipJpegFile $metaMark
                # the formats on the clipboard, noted: a copied file holds no text or picture, which the page's Paste
                # dropped without a message (runs 20261021-222745 and 20261022-061254: one picture, fixed in the app)
                $v.c6clip = Invoke-LabInSessionJson -Name $VmName -Code 'Add-Type -AssemblyName System.Windows.Forms; @([Windows.Forms.Clipboard]::GetDataObject().GetFormats())'
                $null = Ui 'Page' -Value 'keys:^{END}^v'
                $v.c6 = Wait-Page 'Image C6' '(?s)note-file:.*note-file:'
                if ("$($v.c6.body)" -notmatch '(?s)note-file:.*note-file:') { Shot 'c6-one-picture' }
            }
            Shot 'c-note-pages'
            $v.before = Snapshot
            # C-NOTE-07: nothing in plain text while RemoteHub runs (the WAL included) and after it closed
            $words = @($tok.title, $tok.section, $tok.body, $tok.cell, $tok.code, $tok.html)
            $v.diskRunning = Find-Plain @($localDir, $roamDir, $tempDir) $words
            $v.closed = Close-Rh
            $v.diskClosed = Find-Plain @($localDir, $roamDir, $tempDir) $words
            # the restart: the last view and the last page come back
            Start-Rh
            $v.restartView = Wait-Main
            Step $v 'after' {
                $v.afterPicture = Shown 'Pasted image' 15
                $v.afterPhoto = Shown 'photo-c6' 5
                $v.afterBook = Shown "Notebook $book" 5
                $v.afterSection = Shown $section 5
                $v.afterListed = [ordered]@{}
                foreach ($t in @($pageC2, 'Typed C3', 'Round trip C4', 'Script C5', 'Image C6')) { $v.afterListed[$t] = Shown $t 5 }
                $null = Ui 'Round trip C4'
                # read until the editor shows the page; " " in runs 20261021-222745 and 20261022-061254 was the app's
                # text pattern reading the editor's first, empty document (fixed in the app), not a slow load
                $readUntil = (Get-Date).AddSeconds(20)
                do { $v.c4Ui = "$((Ui 'Page' -Value read).value)"; if ($v.c4Ui -match [regex]::Escape($tok.cell)) { break }; Start-Sleep -Seconds 1 } while ((Get-Date) -lt $readUntil)
                $null = Ui 'Copy page as Markdown'
                $v.c4Copy = Get-ClipText
                $null = Ui 'Script C5'
                $v.c5After = "$((Ui 'Script block' -Value read -Optional -TimeoutSec 10).value)"
                $null = Ui 'Typed C3'
                # there is no condition for "nothing was written": the page stays open longer than the autosave delay
                # (1 s), then another page is opened, which saves a changed page at once
                Start-Sleep -Seconds 3
                $null = Ui $pageC2
                Start-Sleep -Seconds 3
            }
            $v.after = Snapshot
            $v.ran = Ran
            Save 'pages' $v
        }

        It 'C-NOTE-01 view rail: Notes and Remote Control switch by the rail and Ctrl+1 / Ctrl+2, a first notebook is there, the last view comes back after a restart' -Tag 'C-NOTE-01' {
            $v['rail-error'] | Should -BeNullOrEmpty
            $v.firstView | Should -Be 'Remote' -Because 'a new profile starts in Remote Control'
            $v.railRemote | Should -BeTrue
            $v.railNotes | Should -BeTrue
            $v.railSettings | Should -BeTrue -Because 'Settings moved into the rail (notes-view-design.md 3.1)'
            $v.notesShown | Should -BeTrue
            $v.remoteHiddenInNotes | Should -BeTrue -Because 'one view is shown at a time'
            $v.defaultBook | Should -BeTrue -Because 'the first start creates the notebook "Notes"'
            $v.defaultSection | Should -BeTrue -Because 'with the section "Quick Notes"'
            $v.ctrl1 | Should -BeTrue
            $v.notesHiddenInRemote | Should -BeTrue
            $v.ctrl2 | Should -BeTrue
            $v.restartView | Should -Be 'Notes' -Because 'LastView is stored and restored at start'
        }

        It 'C-NOTE-02 a notebook, a section and a page made through the UI are stored under each other and listed again after a restart' -Tag 'C-NOTE-02' {
            $v['create-error'] | Should -BeNullOrEmpty
            $v.bookShown | Should -BeTrue
            $v.sectionShown | Should -BeTrue
            $v.c2Listed | Should -BeTrue
            $books = @($v.c2db.books | Where-Object name -eq $book)
            $books.Count | Should -Be 1
            $sections = @($v.c2db.sections | Where-Object { $_.name -eq $section -and $_.notebook_id -eq $books[0].id })
            $sections.Count | Should -Be 1
            $v.c2 | Should -Not -BeNullOrEmpty -Because 'the page is saved without a Save button'
            $v.c2.section_id | Should -Be $sections[0].id
            @($v.before.audit | Where-Object { $_.event -eq 'note.added' -and $_.target -eq $v.c2.id }).Count | Should -BeGreaterThan 0 -Because 'adding a page is audited by its id'
            $v['after-error'] | Should -BeNullOrEmpty
            $v.afterBook | Should -BeTrue
            $v.afterSection | Should -BeTrue
            $v.afterListed[$pageC2] | Should -BeTrue
        }

        It 'C-NOTE-03 Markdown typed at line starts and Ctrl+B become formats; saved a second after typing, without Markdown in the search text' -Tag 'C-NOTE-03' {
            $v['typed-error'] | Should -BeNullOrEmpty
            $md = "$($v.c3.body)"
            $md | Should -Match '(?m)^- alpha item\s*$'
            $md | Should -Match '(?m)^- beta item\s*$'
            $md | Should -Match ('\*\*' + $tok.body + '\*\* and plain')
            $md | Should -Match '(?m)^# Typed heading\s*$'
            "$($v.c3.body_text)" | Should -Match $tok.body
            "$($v.c3.body_text)" | Should -Not -Match '\*\*|(?m)^#' -Because 'the search text is the body without Markdown markers'
            $v.c3Copy.TrimEnd() | Should -Be ("# Typed C3`n`n" + $md).TrimEnd() -Because '"Copy page as Markdown" gives the title and what is stored'
        }

        It 'C-NOTE-04 a table and every other element pasted as Markdown, and HTML from a browser, survive a restart unchanged; opening pages writes nothing' -Tag 'C-NOTE-04' {
            $v['roundtrip-error'] | Should -BeNullOrEmpty
            $md = "$($v.c4.body)"
            $md | Should -Match '(?m)^## Round trip C4\s*$'
            $md | Should -Match '\*\*bold\*\*'
            $md | Should -Match '(?<!\*)\*italic\*(?!\*)'
            $md | Should -Match '~~struck~~'
            $md | Should -Match '`inline code`'
            $md | Should -Match '\[link\]\(https://example\.com/c4\)'
            $md | Should -Match '(?m)^- \[ \] open task\s*$'
            $md | Should -Match '(?m)^- \[x\] done task\s*$'
            $md | Should -Match '(?m)^> A quoted line\s*$'
            $md | Should -Match '\|\s*srv-c4-01\s*\|\s*3389\s*\|\s*\*\*main\*\*\s*\|'
            $md | Should -Match ('\|\s*srv-c4-02\s*\|\s*5986\s*\|\s*' + $tok.cell + '\s*\|')
            $md | Should -Match '\|\s*-+:\s*\|' -Because 'the right-aligned column keeps its alignment'
            $md | Should -Match '(?m)^```sql\s*$'
            $md | Should -Match ([regex]::Escape("SELECT name FROM sys.databases WHERE name = 'c4';"))
            $md | Should -Match '(?m)^1\. first\s*$'
            # the HTML paste: formats and the table kept, style, script and the remote image not
            $md | Should -Match ('\*\*' + $tok.html + '\*\*')
            $md | Should -Match 'web page'
            $md | Should -Match '\|\s*HtmlHead\s*\|\s*Second\s*\|'
            $md | Should -Not -Match 'alert|<script|Comic|#ff0000|color:'
            $md | Should -Not -Match '198\.51\.100\.7' -Because 'a remote image is never linked or fetched'
            $md | Should -Match 'remote-alt-c4' -Because 'a remote image is replaced by its alt text (notes-view-design.md 4.5.4)'
            # after the restart: the same Markdown, the editor shows it, its Markdown is what is stored
            $v['after-error'] | Should -BeNullOrEmpty
            $a = @($v.after.pages | Where-Object title -eq 'Round trip C4') | Select-Object -First 1
            $a.body | Should -BeExactly $md
            $v.c4Ui | Should -Match ([regex]::Escape($tok.cell))
            $v.c4Ui | Should -Match 'srv-c4-02'
            $v.c4Copy.TrimEnd() | Should -Be ("# Round trip C4`n`n" + $md).TrimEnd() -Because 'Markdown -> editor -> Markdown gives the same text'
            # no page was written by the restart or by opening it (notes-view-design.md 4.5.6)
            foreach ($p in @($v.before.pages | Where-Object { -not $_.deleted_utc })) {
                $q = @($v.after.pages | Where-Object id -eq $p.id) | Select-Object -First 1
                $q | Should -Not -BeNullOrEmpty
                $q.body | Should -BeExactly $p.body -Because "page '$($p.title)' must not change"
                $q.modified_utc | Should -Be $p.modified_utc -Because "page '$($p.title)' must not be written by opening it"
                $q.version | Should -Be $p.version
            }
        }

        It 'C-NOTE-05 pasted code becomes a PowerShell script block, stored and copied exactly; nothing offers to run it and nothing ran' -Tag 'C-NOTE-05' {
            $v['script-error'] | Should -BeNullOrEmpty
            $v.c5Lang | Should -BeTrue -Because 'the language is guessed from Get-Service / Where-Object'
            $md = "$($v.c5.body)" -replace "`r`n", "`n"
            $md | Should -Match '(?m)^```powershell\s*$'
            $md.Contains($code5) | Should -BeTrue -Because 'the script text is stored exactly'
            $v.c5Clip | Should -BeExactly $code5 -Because 'Copy copies the block''s text exactly'
            ($v.c5Read -replace "`r`n", "`n") | Should -BeExactly $code5
            ($v.c5After -replace "`r`n", "`n") | Should -BeExactly $code5 -Because 'the block is the same after the restart'
            $v.c5BlockMenu | Should -Contain 'Copy'
            @($v.c5BlockMenu | Where-Object { $_ -like 'Save as file*' }).Count | Should -Be 1
            @($v.c5BlockMenu + $v.c5PageMenu | Where-Object { $_ -match '\b(Run|Execute)\b|Send to' }) | Should -BeNullOrEmpty -Because 'notes are documentation only (notes-view-design.md 4.5.5)'
            $v.ran | Should -BeFalse -Because 'the script in the note was never run'
        }

        It 'C-NOTE-06 a pasted picture and a pasted JPEG file are stored in the database re-encoded, without the file''s metadata, and shown again after a restart' -Tag 'C-NOTE-06' {
            $v['images-error'] | Should -BeNullOrEmpty
            $files = @($v.before.files | Where-Object title -eq 'Image C6')
            $files.Count | Should -Be 2
            foreach ($f in $files) {
                $f.mime | Should -Be 'image/png'
                $f.head | Should -Be '89504E470D0A1A0A' -Because 'only a picture is stored, re-encoded as PNG'
                [int]$f.meta | Should -Be 0 -Because 'the JPEG''s comment segment is not stored'
                [int]$f.size | Should -Be ([int]$f.len)
                "$($v.c6.body)" | Should -Match ('note-file:' + [regex]::Escape($f.id))
            }
            @($files | Where-Object { [int]$_.width -eq 64 -and [int]$_.height -eq 48 }).Count | Should -Be 1
            @($files | Where-Object { [int]$_.width -eq 120 -and [int]$_.height -eq 90 }).Count | Should -Be 1
            "$($v.c6.body)" | Should -Match '!\[Pasted image\]\(note-file:'
            "$($v.c6.body)" | Should -Match '!\[photo-c6\]\(note-file:'
            $v.afterPicture | Should -BeTrue -Because 'the last page opens again with its images'
            $v.afterPhoto | Should -BeTrue
        }

        It 'C-NOTE-07 nothing of a note is in plain text on disk (database, WAL, logs, settings, temp), while RemoteHub runs and after it closed; the audit log has ids only' -Tag 'C-NOTE-07' {
            # control: the words are stored (in the encrypted database)
            $all = (@($v.before.pages | ForEach-Object { "$($_.title) $($_.body)" }) + @($v.before.sections | ForEach-Object name)) -join "`n"
            foreach ($w in @($tok.title, $tok.section, $tok.body, $tok.cell, $tok.code, $tok.html)) { $all | Should -Match $w }
            $v.diskRunning.searched | Should -BeGreaterThan 0
            @($v.diskRunning.hits) | Should -BeNullOrEmpty
            $v.closed | Should -BeTrue
            @($v.diskClosed.hits) | Should -BeNullOrEmpty
            $v.diskClosed.dbHead | Should -Not -BeLike 'SQLite format 3*' -Because 'the database file is encrypted'
            $audit = (@($v.before.audit | ForEach-Object { "$($_.event)|$($_.target)|$($_.detail)" })) -join "`n"
            foreach ($w in @($tok.title, $tok.section, $tok.body, $tok.cell, $tok.code, $tok.html)) { $audit | Should -Not -Match $w }
            @($v.before.audit | Where-Object event -like 'note.*').Count | Should -BeGreaterThan 0
        }
    }

    Context 'deleting and search' {
        BeforeAll {
            $x = [ordered]@{}
            $null = Ui $section
            # C-NOTE-08: the toast's Undo, then Recently deleted's Restore
            Step $x 'undo' {
                Add-Page 'Undo C8'
                $x.c8 = Wait-Page 'Undo C8'
                $null = Ui 'Undo C8' -Value 'menu:Delete'
                $x.toast = Shown 'Page deleted' 5
                # at once: the toast stays 8 s, and reading the database first took longer (run 20261021-222745)
                $null = Ui 'Undo' -TimeoutSec 10
                $x.listedAgain = Shown 'Undo C8' 10
                $x.undone = Page-Row 'Undo C8'
                $null = Ui 'Undo C8' -Value 'menu:Delete'
                $x.toastGoneSec = Wait-Gone 'Page deleted' 30
                # the deleted page's stamp, read now that no toast waits
                $x.deleted = Page-Row 'Undo C8'
                $null = Ui 'Recently deleted'
                $x.bin = @((Ui '*' -Title 'Recently deleted' -Value list).items)
                $null = Ui 'Undo C8' -Title 'Recently deleted' -Value realclick
                $null = Ui 'Restore' -Title 'Recently deleted'
                $null = Ui 'Close' -Title 'Recently deleted'
                $x.restored = Page-Row 'Undo C8'
                $x.restoredListed = Shown 'Undo C8' 10
            }
            # C-NOTE-09: a page deleted, then its section; "Delete now" on the section keeps the page deleted before it
            Step $x 'stamps' {
                $null = Ui "New section in $book" -Value realclick
                $null = Ui 'Name of the new section*' -Title 'New section' -Value 'setnext:Doomed C9'
                $null = Ui 'OK' -Title 'New section'
                $x.c9Shown = Shown 'Doomed C9' 15
                Add-Page 'C9 early'
                Add-Page 'C9 late'
                $x.c9late = Wait-Page 'C9 late'
                $x.c9early = Wait-Page 'C9 early'
                $null = Ui 'C9 early' -Value 'menu:Delete'
                $null = Ui 'Doomed C9' -Value 'menu:Delete?'
                $x.sectionAsk = Box -Titles 'Delete the section*', 'Delete section' -Button 'Delete'
                $c9 = @{ pages = "SELECT id, title, section_id, deleted_utc FROM notes WHERE title LIKE 'C9 %'"; section = "SELECT id, deleted_utc FROM note_sections WHERE name = 'Doomed C9'"; live = 'SELECT id FROM note_sections WHERE deleted_utc IS NULL' }
                $x.db1 = Sql $c9
                $null = Ui 'Recently deleted'
                $x.bin1 = @((Ui '*' -Title 'Recently deleted' -Value list).items)
                $null = Ui 'Doomed C9' -Title 'Recently deleted' -Value realclick
                $null = Ui 'Delete now' -Title 'Recently deleted'
                $x.nowAsk = Box -Titles '*for good*', 'Delete now' -Button 'Delete'
                $null = Wait-Gone 'Doomed C9' 15
                $x.bin2 = @((Ui '*' -Title 'Recently deleted' -Value list).items)
                $x.db2 = Sql $c9
                $null = Ui 'C9 early' -Title 'Recently deleted' -Value realclick
                $null = Ui 'Restore' -Title 'Recently deleted'
                $null = Ui 'Close' -Title 'Recently deleted'
                $x.db3 = Sql $c9
            }
            # C-NOTE-10: search by a word, a prefix, a script block's word; deleted pages and a quote
            Step $x 'search' {
                # a step before that failed may have left the modal Recently deleted open: then no menu opens here
                $null = Ui 'Close' -Title 'Recently deleted' -Optional -TimeoutSec 3
                $null = Ui $section
                Add-Page $pageGone
                $x.gone = Wait-Page $pageGone
                $null = Ui $pageGone -Value 'menu:Delete'
                $x.searchCell = Search $tok.cell 'Round trip C4'
                $x.searchPrefix = Search $tok.cell.Substring(0, $tok.cell.Length - 2) 'Round trip C4'
                $x.searchCode = Search $tok.code 'Script C5'
                $x.searchGone = Search $tok.gone ''
                $x.searchQuote = Search ('"' + $tok.gone) ''
                $null = Ui 'Search notes' -Value 'keys:{ESC}'
            }
            $x.audit = (Sql @{ a = "SELECT event, target, detail FROM audit WHERE event LIKE 'note.%' ORDER BY id" }).a
            Shot 'c-note-deleted'
            Save 'deleted' $x
        }

        It 'C-NOTE-08 a deleted page comes back with the toast''s Undo and from Recently deleted; both audited by id' -Tag 'C-NOTE-08' {
            $x['undo-error'] | Should -BeNullOrEmpty
            $x.toast | Should -BeTrue -Because 'deleting a page shows "Page deleted" with Undo'
            $x.deleted.deleted_utc | Should -Not -BeNullOrEmpty
            $x.undone.deleted_utc | Should -BeNullOrEmpty
            $x.listedAgain | Should -BeTrue
            $x.toastGoneSec | Should -BeGreaterThan 4 -Because 'the toast stays for 8 seconds'
            $x.toastGoneSec | Should -BeLessThan 25
            @($x.bin | Where-Object { $_ -like '*|Undo C8|*' }).Count | Should -BeGreaterThan 0
            $x.restored.deleted_utc | Should -BeNullOrEmpty
            $x.restoredListed | Should -BeTrue
            $id = $x.c8.id
            @($x.audit | Where-Object { $_.event -eq 'note.deleted' -and $_.target -eq $id }).Count | Should -Be 2
            @($x.audit | Where-Object { $_.event -eq 'note.restored' -and $_.target -eq $id }).Count | Should -Be 2
        }

        It 'C-NOTE-09 a page deleted before its section keeps its own stamp: Delete now on the section removes only the section''s own pages, the earlier page can still be restored' -Tag 'C-NOTE-09' {
            $x['stamps-error'] | Should -BeNullOrEmpty
            $x.sectionAsk | Should -Match 'Delete the section "Doomed C9" and its 1 pages'
            $early = @($x.db1.pages | Where-Object title -eq 'C9 early')[0]
            $late = @($x.db1.pages | Where-Object title -eq 'C9 late')[0]
            $sec = @($x.db1.section)[0]
            [long]$early.deleted_utc | Should -BeLessThan ([long]$sec.deleted_utc)
            [long]$late.deleted_utc | Should -Be ([long]$sec.deleted_utc) -Because 'what is deleted with the section shares its stamp'
            @($x.bin1 | Where-Object { $_ -like '*|Doomed C9|*' }).Count | Should -BeGreaterThan 0
            @($x.bin1 | Where-Object { $_ -like '*|C9 early|*' }).Count | Should -BeGreaterThan 0
            $x.nowAsk | Should -Match 'for good'
            @($x.db2.section).Count | Should -Be 0
            @($x.db2.pages | Where-Object title -eq 'C9 late').Count | Should -Be 0 -Because 'Delete now removed the section''s own page'
            $kept = @($x.db2.pages | Where-Object title -eq 'C9 early')
            $kept.Count | Should -Be 1 -Because 'the page deleted before the section is not removed with it (PR #37)'
            $kept[0].deleted_utc | Should -Not -BeNullOrEmpty
            @($x.bin2 | Where-Object { $_ -like '*|Doomed C9|*' }).Count | Should -Be 0
            @($x.bin2 | Where-Object { $_ -like '*|C9 early|*' }).Count | Should -BeGreaterThan 0
            $back = @($x.db3.pages | Where-Object title -eq 'C9 early')[0]
            $back.deleted_utc | Should -BeNullOrEmpty
            @($x.db3.live | ForEach-Object id) | Should -Contain $back.section_id -Because 'a page whose section is gone is restored into a live section'
        }

        It 'C-NOTE-10 search finds a table cell by word and prefix and a script block''s word; deleted pages and a typed quote find nothing without an error' -Tag 'C-NOTE-10' {
            $x['search-error'] | Should -BeNullOrEmpty
            $x.searchCell.head | Should -Be '1 page found'
            $x.searchCell.shown | Should -BeTrue
            $x.searchPrefix.head | Should -Be '1 page found'
            $x.searchPrefix.shown | Should -BeTrue
            $x.searchCode.head | Should -Be '1 page found'
            $x.searchCode.shown | Should -BeTrue
            $x.gone.id | Should -Not -BeNullOrEmpty
            $x.searchGone.head | Should -Be 'No pages found'
            $x.searchQuote.head | Should -Be 'No pages found' -Because 'FTS syntax typed by the user is escaped'
        }
    }

    Context 'AllowNotes policy' {
        BeforeAll {
            $p = [ordered]@{}
            $p.pagesBefore = [int]@((Sql @{ n = 'SELECT count(*) AS n FROM notes WHERE deleted_utc IS NULL' }).n)[0].n
            Step $p 'running' {
                Policy 0
                # policies are read when the window is next activated: Settings opened and closed
                $null = Ui 'Settings'
                $null = Ui 'Cancel' -Title 'Settings'
                $p.remoteNow = Shown 'SearchBox' 15
                $p.notesHidden = -not (Shown 'Notebooks and sections' 3)
                $p.railNotes = Shown 'Notes' 3
                $null = Ui 'SearchBox' -Value 'keys:^2'
                $p.ctrl2 = Shown 'Notebooks and sections' 5
            }
            $null = Close-Rh
            Start-Rh
            $p.startBlocked = Wait-Main
            $p.railNotesRestart = Shown 'Notes' 3
            Shot 'c-note-11-blocked'
            $null = Close-Rh
            $p.pagesBlocked = [int]@((Sql @{ n = 'SELECT count(*) AS n FROM notes WHERE deleted_utc IS NULL' }).n)[0].n
            Policy $null
            Start-Rh
            $p.startAllowed = Wait-Main
            $p.railNotesBack = Shown 'Notes' 3
            # the page's own section: RemoteHub opens the section shown last, which may be another one
            $null = Ui $section -Optional -TimeoutSec 10
            $p.listedBack = Shown $pageC2 10
            Save 'policy' $p
        }

        It 'C-NOTE-11 AllowNotes = 0: the Notes rail item and Ctrl+2 are gone, a shown Notes view leaves, notes are kept; without the policy the last view (Notes) comes back' -Tag 'C-NOTE-11' {
            $p['running-error'] | Should -BeNullOrEmpty
            $p.remoteNow | Should -BeTrue -Because 'a change applies when the window is next activated, and an open Notes view leaves'
            $p.notesHidden | Should -BeTrue
            $p.railNotes | Should -BeFalse
            $p.ctrl2 | Should -BeFalse -Because 'Ctrl+2 is not taken while Notes are blocked'
            $p.startBlocked | Should -Be 'Remote' -Because 'a saved LastView of Notes opens Remote Control under the policy'
            $p.railNotesRestart | Should -BeFalse
            $p.pagesBlocked | Should -Be $p.pagesBefore -Because 'notes are kept while blocked'
            $p.startAllowed | Should -Be 'Notes' -Because 'LastView was kept for when the policy is removed'
            $p.railNotesBack | Should -BeTrue
            $p.listedBack | Should -BeTrue
        }
    }

    Context 'backup' {
        BeforeAll {
            $b = [ordered]@{}
            $live = @{
                pages = 'SELECT n.title, n.body, b.name AS book FROM notes n JOIN note_sections s ON s.id = n.section_id JOIN notebooks b ON b.id = s.notebook_id ' +
                    'WHERE n.deleted_utc IS NULL AND s.deleted_utc IS NULL AND b.deleted_utc IS NULL'
                total = 'SELECT count(*) AS n FROM notes WHERE deleted_utc IS NULL'
                files = 'SELECT hex(f.sha256) AS sha FROM note_files f JOIN notes n ON n.id = f.note_id WHERE n.deleted_utc IS NULL'
                books = 'SELECT name FROM notebooks WHERE deleted_utc IS NULL'
                audit = "SELECT event, target, detail FROM audit WHERE event LIKE 'backup.%' ORDER BY id"
            }
            $b.before = Sql $live
            Step $b 'export' {
                Settings-Page 'Security & storage'
                $null = Ui 'Export backup?' -Title 'Settings'
                FileDialog 'C:\lab\c-notes.rhbackup'
                $null = Ui 'type:Edit#1' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'type:Edit#2' -Title 'Protect backup' -Value "set:$backupPw"
                $null = Ui 'Export' -Title 'Protect backup'
                $b.exported = Box -Titles 'Backup saved to*', 'Export backup' -Button 'OK' -TimeoutSec 60
                $null = Ui 'Cancel' -Title 'Settings' -Optional -TimeoutSec 5
            }
            $b.exportAudit = @((Sql @{ a = $live.audit }).a)
            $b.file = Find-Plain @('C:\lab\c-notes.rhbackup') @($tok.title, $tok.section, $tok.body, $tok.cell, $tok.code, $tok.html)
            $null = Close-Rh
            # a fresh profile: the backup is the only way the notes can come back
            Clear-RhData
            Start-Rh
            $b.freshView = Wait-Main
            Step $b 'import' {
                Settings-Page 'Security & storage'
                # closes the settings window first, then Windows' Open dialog
                $null = Ui 'Import backup?' -Title 'Settings'
                FileDialog 'C:\lab\c-notes.rhbackup' -Title 'Open'
                $null = Ui 'type:Edit' -Title 'Unlock backup' -Value "set:$backupPw"
                $null = Ui 'Import' -Title 'Unlock backup'
                $b.ask = Box -Titles 'Add the backup as a new folder*', 'Import backup' -Button 'Add as folder' -TimeoutSec 90
                $deadline = (Get-Date).AddSeconds(45)
                do {
                    $b.books = @((Sql @{ b = $live.books }).b | ForEach-Object name)
                    if (@($b.books | Where-Object { $_ -like '*(c-notes)' }).Count) { break }
                    Start-Sleep -Seconds 3
                } while ((Get-Date) -lt $deadline)
                $null = Ui 'Notes'
                $b.bookShown = Shown "Notebook $book (c-notes)" 15
            }
            Shot 'c-note-12-imported'
            $b.after = Sql $live
            Save 'backup' @{ exported = $b.exported; exportAudit = $b.exportAudit; file = $b.file; freshView = $b.freshView; ask = $b.ask; books = $b.books; bookShown = $b.bookShown
                before = $b.before.pages.Count; after = $b.after.pages.Count; importAudit = $b.after.audit; errors = @($b.Keys | Where-Object { $_ -like '*-error' } | ForEach-Object { "${_}: $($b[$_])" }) }
        }

        It 'C-NOTE-12 a .rhbackup carries the notes encrypted: export counts the pages, a fresh profile adds them as notebooks named after the file, text and images identical' -Tag 'C-NOTE-12' {
            $b['export-error'] | Should -BeNullOrEmpty
            $b.exported | Should -Match 'Backup saved to\s+C:\\lab\\c-notes\.rhbackup'
            $count = [int]@($b.before.total)[0].n
            $count | Should -BeGreaterThan 0
            @($b.exportAudit | Where-Object { $_.event -eq 'backup.exported' -and $_.detail -match "\b$count pages" }).Count | Should -Be 1
            $b.file.searched | Should -Be 1
            @($b.file.hits) | Should -BeNullOrEmpty -Because 'the backup is encrypted with its password'
            $b['import-error'] | Should -BeNullOrEmpty
            $b.freshView | Should -Be 'Remote'
            # no own "Notes" here: the first notebook is made when Notes first opens with none (NoteStore.EnsureDefault,
            # so the view is never empty) - the import came before that
            $b.books | Should -Contain "$book (c-notes)"
            $b.bookShown | Should -BeTrue
            # every page again, in "<notebook> (c-notes)", with the same Markdown (image ids are new)
            $norm = { param($s) "$s" -replace 'note-file:[^)\s]+', 'note-file:X' }
            foreach ($p in @($b.before.pages)) {
                $q = @($b.after.pages | Where-Object { $_.title -eq $p.title -and $_.book -eq "$($p.book) (c-notes)" })
                $q.Count | Should -Be 1 -Because "page '$($p.title)' is imported once"
                (& $norm $q[0].body) | Should -BeExactly (& $norm $p.body)
            }
            foreach ($f in @($b.before.files)) { @($b.after.files | ForEach-Object sha) | Should -Contain $f.sha }
            @($b.after.audit | Where-Object { $_.event -eq 'backup.imported' -and $_.detail -match "Added as a folder, and $count pages" }).Count | Should -Be 1
        }
    }
}
