# RemoteHub – Notes and the view rail

Status: v5 (2026-10-19) – phases 1–4 are built on the branch `notes-view`; §13 lists what
differs from the plan. v2 took in the
answers to the open questions of v1 (§12): several notebooks, Settings and the version in the rail, a faster rail, no
secret format, no OneNote import. v3 replaces v2's Markdown editor with a **WYSIWYG editor with tables, images and
script blocks** (§4.5); pages are still stored as Markdown. v4: notes are documentation only (no running or sending
scripts) and script blocks cover all relevant languages (§4.5.5a).
Owner: Grischa Ernst

This covers two things that belong together:

- a **view rail**: a slim vertical bar of icons on the far left of the main window that switches between the app's
  top-level views, and
- the first new view on it, **Notes**: notebook pages organised like OneNote, stored in the encrypted local database.

It is phase 3 ("Notes") of [local-db-design.md](local-db-design.md) §12. Where this document differs from the plan
there (§6 schema, notes per connection or folder), this document wins, and §9.3 lists what changes in that document.

## 1. Goal

| Part | What |
|---|---|
| **View rail** | Icons only, on the far left of the window, one for each view: **Remote Control** (everything RemoteHub does today) and **Notes**. When the pointer rests on the rail, it expands over the content and shows a label next to each icon. It collapses when the pointer leaves. |
| **Notes** | Three panes, as in OneNote: notebooks and their sections with a colour, the pages of the selected section, and the page itself. Pages are edited as they look (WYSIWYG) with formatted text, tables, images and – most important – **script blocks** for commands, scripts, SQL and configuration. They are stored as Markdown, so they can be exported and used in other tools and with AI. Notes are saved automatically and can be searched. |

### 1.1 Principles

1. **Switching views never touches a session.** RDP, agent desktop and PowerShell sessions keep running, at the same
   size, while Notes is shown. Coming back shows them exactly as they were.
2. **Notes are as safe as passwords at rest.** They live in the encrypted database (local-db-design.md §3) and nowhere
   else: not in `local.json`, `DiagLog`, telemetry, crash reports or temp files. Notes regularly hold secrets – the
   reference example contains a client secret.
3. **Nothing typed is ever lost.** Autosave, no Save button, and deleting goes through *Recently deleted* (§6.5).
4. **The rail is navigation, not a toolbar.** It holds views and Settings. Actions belong to the view (the sidebar's
   *New* button, the Notes format bar).

## 2. Non-goals (v1)

- **Free placement of text boxes on a canvas** (OneNote's page model). Pages are a single flowing document.
- **Ink, audio and attached files** (other than images). Images are in v1 (§4.5.4).
- **Running or sending scripts from a note.** Notes are documentation only. A script block can be copied and saved as
  a file, but never run, and it isn't sent to a session either (§4.5.5). Running scripts belongs to the Scripts view
  (§3.8).
- **Fonts, font sizes and text colours.** Pages use the theme's styles, so they look the same everywhere and stay
  plain Markdown.
- **Sharing notes with other users.** This waits for a server, as connections do.
- **Sync between the user's devices.** This comes with device sync (local-db-design.md §8, phase 4 there). Until
  then, notes stay on the PC, also in shared-folder mode.
- **Section groups** (sections inside sections). Notebooks and sections are enough for v1.
- **Secret fields in notes** (hidden values, copying through `SecureClipboard`). Notes are protected as a whole (§9).
- **Import from OneNote.** Copying and pasting a page's text works. A Markdown file can be imported (§4.7).

## 3. The view rail

### 3.1 Placement

The main window's content row (`MainWindow.xaml`, the `Grid` in `Grid.Row="2"`) gets one more column on the left:

```
┌────┬───────────────────────────────────────────────────────────────┐
│ ▣  │  (title bar, update banner – unchanged, full width)           │
├────┼───────────────────────────────────────────────────────────────┤
│ 🖥 │  View host                                                   │
│ 📓 │   Remote Control: today's sidebar + splitter + session area   │
│    │   Notes:          NotesView (§4)                              │
│    │                                                               │
│0.19│                                                               │
│ ⚙  │                                                               │
└────┴───────────────────────────────────────────────────────────────┘
  48 px
```

- The existing five-column grid (`SidebarColumn`, `MainSplitter`, `SessionArea`, the transfers splitter,
  `TransfersColumn`) moves unchanged into a `Grid x:Name="RemoteView"`. `NotesView` sits next to it in the same cell.
  The view host shows one of them.
- The rail has no `Border` card of its own. It sits directly on `ChromeBackgroundBrush`, like the title bar, so it reads
  as window chrome and not as a third panel next to the sidebar and session cards.
- **Top:** the views, in this order: Remote Control, Notes. Scripts and Logs follow later (§3.8). **Bottom:** the
  version (§3.9), then Settings.
- **Settings moves into the rail.** It is the same handler as today (`Settings_Click`, Ctrl+,). The Settings buttons in
  the sidebar header (`DockPanel.Dock="Right"` next to `PanelLeftContract24`) and in the collapsed `SidebarRail` are
  removed, so Settings has one place in every view.

### 3.2 Collapsed and expanded

| | Collapsed | Expanded |
|---|---|---|
| Width | 48 px | 200 px |
| Shows | Icon (20 px, `ui:SymbolIcon`), selection marker, short version | Icon + label (`Remote Control`, `Notes`, `Settings`), full version line |
| Layout | Takes its column | **Lies over** the view; the view keeps its size |

Icons (Fluent, from WPF-UI's `SymbolRegular`): Remote Control `Desktop24`, Notes `Notebook24`, Settings `Settings24`.
The selected view gets the same marker the tree uses for its selected item: a 3 × 16 px rounded bar on the left
(`ActiveRectangle` in the `TreeViewItem` template), in the accent colour, plus the subtle selected background.

**Why it lies over the view, and how.** An expanding rail that pushes the content would resize the session area. That
changes the size of every embedded RDP control (and with *Smart sizing* off, the remote resolution) every time the
pointer passes. So the expanded rail must overlap. But the RDP ActiveX control (`mstscax.dll`) and the agent desktop
are `HwndHost`s, and WPF can't draw over a child window (the "airspace" problem): a WPF overlay would be hidden behind
the remote desktop. So the expanded rail is a **`Popup`** (its own top-level window, which can draw over the
`HwndHost`), placed exactly over the collapsed rail and 200 px wide. It has `AllowsTransparency="True"`, the
`SidebarBackgroundBrush` background, `ChromeStrokeBrush` border, a corner radius of 8 on the right-hand side, and the
theme's flyout shadow.

### 3.3 When it expands and collapses

- **Pointer:** expands after the pointer has rested on the rail for **150 ms**, and collapses **150 ms** after it leaves
  both the rail and the expanded popup. The short delay still keeps the rail from flashing open when the pointer only
  crosses it on its way to the sidebar. Clicking an item switches the view at once and collapses the rail. Both delays
  are constants in `ViewRail`, so they can be tuned after trying them.
- **Keyboard:** when the rail gets keyboard focus (Tab, or F6 from the title bar), it expands at once. It collapses
  when focus leaves. Up and Down move between the items, Enter or Space selects one, and Esc collapses and returns focus
  to the view.
- **Animation:** the width grows over 100 ms with an ease-out curve. With *Show animations in Windows* off
  (`SystemParameters.ClientAreaAnimation` is false), it opens without animation.
- **Never while** a modal dialog is open, the window is locked (`AppLock`) or a drag is in progress (tree drag and
  drop, a file being dragged into a session).
- **Touch and pen:** there is no hover. A tap on an icon switches the view, and a press-and-hold shows the expanded
  rail.

### 3.4 Accessibility

- Every item has `AutomationProperties.Name` (the label), a `ToolTip` with the label and shortcut (it shows only when
  the rail can't expand, so it doesn't repeat the label), and is a real `ListBoxItem`/`RadioButton`. Screen readers
  hear "Notes, 2 of 2, selected".
- Colours come from the theme (`TextFillColorPrimaryBrush`, the accent, `SubtleFillColorSecondaryBrush` for hover), so
  light and dark themes and high contrast work without extra code. No hard-coded colours.
- Hit targets are 40 × 40 px.

### 3.5 Keyboard shortcuts

- **Ctrl+1** shows Remote Control and **Ctrl+2** shows Notes. They are added to `MainWindow_PreviewKeyDown` next to
  Ctrl+B, Ctrl+F and Ctrl+, (`MainWindow.xaml.cs` ≈ line 494). Like those shortcuts, they don't fire while an RDP
  session has the keyboard, because RDP gets the keys.
- The session switch hotkeys (`SwitchHotkeys`: Ctrl+Alt+F1/F2, PageUp/PageDown …) and opening a connection from
  anywhere (a notification, a jump list, `SingleInstance` forwarding an `.rdp` file) **switch to Remote Control first**.
- Ctrl+F searches the current view: the connection tree in Remote Control, all notes in Notes.

### 3.6 Switching views and what happens to the sessions

- The hidden view gets `Visibility.Hidden`, **not** `Collapsed`. Hidden keeps the layout, so the `HwndHost`s keep their
  size and the RDP controls don't resize or reconnect. They only stop being visible. The sessions keep running, and
  recording (`SessionRecorder`) keeps recording – it captures the session's own surface, not the window.
- The remote view keeps keyboard focus state. When the user comes back, focus returns to the session that had it.
- Detached session windows (`DetachedWindow`) are not affected.
- The view shown last is stored in `UiState` (`Models/TreeNodes.cs`) as `LastView` (`"Remote"` / `"Notes"`, default
  `"Remote"`), next to `SidebarCollapsed`, and restored at start. An unknown value (from a newer version) means
  Remote Control.
- When the Notes policy blocks Notes (§8), the rail still shows (with Remote Control and Settings). That keeps the
  window's layout the same for everyone.

### 3.7 The existing collapsed sidebar

Today Ctrl+B collapses the connection sidebar into `SidebarRail`, a 40 px column of buttons inside the sidebar card.
With the view rail, the window then has two narrow bars next to each other. They stay separate on purpose: the view rail
is window chrome (flat, on the chrome background) and `SidebarRail` is part of the Remote Control view (inside its
card). `SidebarRail` keeps *Show navigation*, *New* and *Search*, and loses *Settings* (§3.1).

### 3.8 Later views

Scripts (local-db-design.md phase 5) and **Logs** come next. Logs is today's audit log viewer (`AuditLogWindow`) as a
view, so it no longer needs its own window. Each gets its own design and policy. The rail is built from a list of
views (`id`, icon, label, shortcut, factory, the policy that can hide it), so a new view is one entry. Shortcuts follow
the order: Ctrl+3, Ctrl+4. Up to about six views fit before the rail would need to scroll at the minimum window height.

### 3.9 Version

Above Settings, the rail shows the running version:

- **Collapsed:** the short version in the `Caption` style (`0.19`, the first two parts of `AppVersion`), centred.
  It is trimmed to fit 48 px.
- **Expanded:** "RemoteHub 0.19.2", and under it the channel when it isn't Stable ("Beta channel").
- **Update available:** when the start-up update check finds a newer version (the same result that shows
  `UpdateBanner`), a small accent dot appears on the version, and the expanded line says "Update available: 0.19.3".
- **Click:** opens `AboutWindow` (as `About_Click`), also when an update is available – the update is offered by
  `UpdateBanner` and in *Settings → Updates & privacy*. The tooltip and
  `AutomationProperties.Name` read "RemoteHub 0.19.2 – about this version".
- Pre-release builds show their suffix in the expanded line only ("0.19.2-beta.1"). The collapsed line stays short.

## 4. The Notes view

### 4.1 Layout

```
┌─ Notebooks ──────┬─ Pages ───────────────┬─ Page ─────────────────────────────────────────┐
│ ▾ Grischa @ Work │ [+ Add page]    [⇅]   │ Enable EARv2 in UEM                             │
│ ▌ Quick Notes    │ Move Bifrost from …   │ Wednesday, 15 April 2026  14:32                 │
│ ▌ Dev Items      │▐Enable EARv2 in UEM   │ ─────────────────────────────────────────────── │
│ ▌ Roadmap        │ syncML Generator      │ ┌───────────────────────────────────────────┐   │
│ ▌ Winget         │ Enable modern stack   │ │ update dbo.Setting set Value = 5846 …     │   │
│▐▌ Devwork        │                       │ └───────────────────────────────────────────┘   │
│ + New section    │                       │                                                 │
└──────────────────┴───────────────────────┴─────────────────────────────────────────────────┘
   220 px (resizable)   260 px (resizable)        *
```

- Three cards with the same look as the Remote Control view: `Border` with `CornerRadius="10"`,
  `SidebarBackgroundBrush`, `ChromeStrokeBrush`, the same 10 px outer margins and `GridSplitter`s in between. The
  notebook and page panes can be resized. Their widths go to `UiState` (`NotesNotebooksWidth`, `NotesPagesWidth`).
- `NotesView` is a class built in code (`Views/NotesView.cs`), like `RegistryView` and `FileBrowserView`, with a
  `NotesViewModel` (CommunityToolkit.Mvvm, `[ObservableProperty]`) in `ViewModels/`.
- **Narrow window:** below 900 px of view width, the notebook pane collapses into a drop-down above the page list.
  Below 600 px, the page list does the same, so only the page is shown.

### 4.2 Notebooks and sections

**Notebooks**

- The pane shows **all notebooks** as collapsible groups, as in OneNote: the notebook's name ("Grischa @ Work" in the
  reference) with a chevron, and its sections below it. A notebook remembers whether it was expanded (`UiState`).
- **+ New notebook** sits at the bottom of the pane, under the last notebook. The first start creates one notebook
  named "Notes" with one section "Quick Notes", so the view is never empty.
- Notebook context menu: *Rename* (F2), *New section*, *Move up/down*, *Export as Markdown…* (§4.7), *Delete…*.
  Deleting a notebook asks first and names the number of sections and pages.

**Sections**

- Each section has a coloured bar (3 px, the same `TagBrush` style as the tree items). Colours come from
  `ColorTags.All` (`Views/AppChrome.cs`), so sections use the same palette as folders and connections. New sections get
  the next colour in turn.
- **+ New section** at the end of each notebook creates "New section" in edit mode for the name.
- Context menu: *Rename* (F2), *Colour* (the same submenu as for folders), *Move up/down*, *Export as Markdown…*,
  *Import Markdown files…* (§4.7), *Delete…*. Sections can be reordered by dragging, also into another notebook.

### 4.3 Pages

- Toolbar: **Add page** (`ui:Button`, `Add24`, Ctrl+N while Notes is shown) and a sort button (`ArrowSort24`): *Manual
  order*, *Title*, *Date modified*, *Date created*.
- Each row shows the title, trimmed with an ellipsis. With *Date modified* sorting, the date is shown under it in the
  `Caption` style.
- A page without a title shows "Untitled page" in the secondary text colour.
- Context menu: *Rename*, *Move to section ›*, *Copy link* (§4.6), *Delete* (Del).
- Dragging reorders the pages (in *Manual order*) or moves them onto a section in the notebook pane.

### 4.4 The page

- **Title** – a large text box (the theme's `TitleTextBlockStyle` size) with no border until focused. Enter moves to
  the body.
- **Date line** – created date and time in the user's regional format, in the secondary text colour. Read-only. The
  tooltip shows the date modified.
- **Format bar** – one row above the body (§4.5): text style, bold/italic/strikethrough, lists, *Table*, *Image*,
  *Script block*, link. On the right: *Copy as Markdown* and *Export…* (§4.7).
- **Body** – the editor (§4.5), filling the rest of the card, with its own scrolling.

### 4.5 The editor

The page is edited **as it looks** (WYSIWYG), like OneNote: formatted text, tables, images and script blocks, with no
separate edit and preview modes. Underneath, a page is still stored as **Markdown** (§4.5.6), so export and use in other
tools and with AI keep working.

#### 4.5.1 Technology

| Option | For | Against |
|---|---|---|
| **A. WPF `RichTextBox`, with script blocks in an embedded code editor** | Native: same theme, fonts, keyboard handling and accessibility as the rest of the app. Note text stays in the process – no browser cache or profile on disk. No JavaScript in the path of secrets. | Table editing (insert row, resize columns) and moving the caret into and out of embedded blocks are our own work. |
| **B. WebView2 with a JavaScript editor (e.g. TipTap/ProseMirror)** | Mature table, image and code block editing out of the box | A browser engine with its own profile folder on disk (cache, IndexedDB, crash dumps) that can hold note text; a JavaScript dependency chain to keep up to date and audit; a second theme system; WebView2 is an `HwndHost` (airspace, §3.2) |

**Recommendation: A.** Notes hold secrets (§1.1, principle 2), and option B would put their text into a browser
profile on disk and a JavaScript supply chain. The extra editing work in A is bounded: tables are simple (§4.5.3), and
there is one kind of embedded block.

Script blocks use **[AvalonEdit](https://github.com/icsharpcode/AvalonEdit)** (MIT, the editor from SharpDevelop and
ILSpy) inside a `BlockUIContainer`. It brings syntax colouring, line numbers, its own undo and fast handling of long
scripts. It goes into `THIRD-PARTY-NOTICES.md`.

#### 4.5.2 Text

| Format | Shortcut | Stored as |
|---|---|---|
| Heading 1–3 | Ctrl+Alt+1 … 3 | `#` … `###` |
| Normal text | Ctrl+Alt+0 | paragraph |
| Bold, italic, strikethrough | Ctrl+B, Ctrl+I, Ctrl+Shift+X | `**`, `*`, `~~` |
| Inline code | Ctrl+` | `` ` `` |
| Bulleted, numbered and check lists (nested) | Ctrl+., Ctrl+/, Ctrl+Shift+C; Tab / Shift+Tab to nest | `-`, `1.`, `- [ ]` |
| Quote | Ctrl+Shift+Q | `>` |
| Link | Ctrl+K | `[text](url)` |
| Horizontal line | `---` + Enter | `---` |

- Markdown typed at the start of a line becomes the format at once (`# ` → heading, `- ` → list, `1. ` → numbered
  list, `[] ` → check box, `> ` → quote, ```` ``` ```` + Enter → script block). This is the quickest way for people who
  know Markdown, and nobody else needs it.
- Ctrl+B inside the editor means bold, not "collapse sidebar". In Notes, the editor handles Ctrl+B first.
- **Links:** only `http`, `https`, `mailto` and `remotehub://note/` (§4.6) are clickable, with Ctrl+click while
  editing. `http(s)` asks first ("Open https://… in your browser?"). Other targets (`file:`, `javascript:`, UNC paths)
  stay text.
- No font, size or colour choice: pages look the same everywhere and stay plain Markdown. Text colour highlighting is a
  candidate for later (§12).

#### 4.5.3 Tables

- *Table* in the format bar inserts a 3 × 3 table with a header row. Typing `| a | b |` + Enter does the same.
- Tab moves to the next cell and adds a row at the end. A table's context menu: *Insert row above/below*, *Insert column
  left/right*, *Delete row/column/table*, *Align column left/centre/right*. Columns can be resized by dragging their
  border. The width is a display setting only and isn't stored.
- Cells hold **one line of text with inline formats** (bold, italic, inline code, links). Enter in a cell moves to the
  cell below. This is the limit of Markdown tables (GitHub's pipe tables), and it keeps every table exportable. Merged
  cells, lists, images or script blocks inside cells are not possible; pasting them into a cell pastes their text.
- Pasting a table from Excel, a web page or OneNote (HTML or tab-separated text) makes a table.

#### 4.5.4 Images

- An image comes from **paste** (a screenshot from Snipping Tool, an image copied from a browser), **drag and drop** of
  a file, or *Image* in the format bar (a file dialog for PNG, JPEG, GIF, BMP, WebP).
- On insert, the image is **re-encoded as PNG** (JPEG stays JPEG at quality 90 when it is a photo larger than 1 MB as
  PNG). This drops all metadata – EXIF with GPS position, camera and author – and makes sure only a picture is stored,
  whatever the source file held.
- Limits: 25 MB per image after re-encoding (the limit planned for attachments in local-db-design.md §8), 16,384 px on
  the longer side (larger images are scaled down), 200 images per page.
- An image is shown at its natural size up to the page width. Dragging its corner resizes it (keeps the aspect ratio).
  The width is stored. Its context menu: *Copy*, *Save as…*, *Alternative text…*, *Original size*, *Delete*.
- Images are stored in the database (`note_files`, §5.1), never as separate files and never loaded from a URL. Pasted
  HTML that links to a web image doesn't fetch it – the image is left out and replaced by its alt text.

#### 4.5.5 Script blocks

Script blocks are the most important element: commands, scripts, source code, queries and configuration that the user
documents and copies from. Notes are **documentation only** – a script block is text with colouring, and nothing in
the Notes view runs it or sends it anywhere.

```
┌─ PowerShell ▾ ─────────────────────────────────────── [⧉ Copy] [⋯] ┐
│  1  Get-Service -Name 'RemoteHubAgent' |                           │
│  2      Where-Object Status -ne 'Running' |                        │
│  3      Start-Service -PassThru                                    │
└────────────────────────────────────────────────────────────────────┘
```

- **Insert:** *Script block* in the format bar (Ctrl+Shift+`), or ```` ``` ```` + Enter, or ```` ```sql ```` + Enter
  for a language. Pasting text that looks like code (several lines that start with spaces or tabs, or start with a
  common command or keyword such as `Get-`, `$`, `SELECT`, `UPDATE`, `INSERT`, `{`, `<`) into an empty paragraph offers
  *Paste as script block* (on by default; Ctrl+Z turns it back into text).
- **Language:** the drop-down in the header, with a search box, the five most recently used languages at the top and
  the groups in §4.5.5a below. The choice is stored as the fence's info string (```` ```powershell ````). A new block
  guesses from the pasted text (§4.5.5a) and can be changed.
- **Editing:** monospace (`Cascadia Mono`, then `Consolas`), syntax colouring, line numbers, no wrapping (horizontal
  scrolling inside the block), Tab indents. The block grows with its content up to 30 lines, then scrolls inside
  itself; dragging the bottom edge makes it taller.
  - Arrow keys move the caret out of the block at its first and last line, so it feels like one document. Ctrl+Enter
    at the end leaves the block and starts a new paragraph below it.
  - The block has its own undo while the caret is inside. The page's undo treats a block's changes as one step.
- **Colours:** theme-aware highlighting palettes for dark and light (AvalonEdit's definitions recoloured). PowerShell
  uses the same palette as the PowerShell console (`PsSyntax` in `Views/PsSyntax.cs`), which today exists for the dark
  theme only – it gets a light variant.
- **Copy:** the *Copy* button copies the block's text exactly (no line numbers, line ends as stored) and shows "Copied"
  for two seconds.
- **⋯ menu:** *Copy*, *Language ›*, *Wrap long lines* (display only), *Save as file…* (the extension from the
  language, §4.5.5a), *Delete block*. There is no *Run* and no *Send to session*: Notes are documentation only.

#### 4.5.5a Languages

| Group | Language | Fence id (aliases accepted on paste and import) | *Save as* |
|---|---|---|---|
| **Shells and scripting** | PowerShell | `powershell` (`ps1`, `pwsh`, `ps`) | `.ps1` |
| | Command Prompt / batch | `batch` (`bat`, `cmd`) | `.cmd` |
| | Bash / shell | `bash` (`sh`, `shell`, `zsh`) | `.sh` |
| | Python | `python` (`py`) | `.py` |
| | VBScript | `vbscript` (`vbs`) | `.vbs` |
| **Programming** | C | `c` (`h`) | `.c` |
| | C++ | `cpp` (`c++`, `cc`, `hpp`) | `.cpp` |
| | C# | `csharp` (`cs`, `c#`) | `.cs` |
| | Visual Basic .NET | `vbnet` (`vb`) | `.vb` |
| | Java | `java` | `.java` |
| | JavaScript | `javascript` (`js`) | `.js` |
| | TypeScript | `typescript` (`ts`) | `.ts` |
| | Go | `go` | `.go` |
| | Rust | `rust` (`rs`) | `.rs` |
| | PHP | `php` | `.php` |
| **Queries** | SQL (standard) | `sql` | `.sql` |
| | T-SQL (SQL Server, Azure SQL) | `tsql` (`mssql`) | `.sql` |
| | PostgreSQL | `pgsql` (`postgres`, `postgresql`) | `.sql` |
| | MySQL / MariaDB | `mysql` (`mariadb`) | `.sql` |
| | Oracle PL/SQL | `plsql` (`oracle`) | `.sql` |
| | SQLite | `sqlite` | `.sql` |
| | KQL (Log Analytics, Defender, Intune, Azure Resource Graph) | `kql` (`kusto`) | `.kql` |
| | WQL (WMI, ConfigMgr) | `wql` | `.wql` |
| | LDAP filter | `ldap` | `.txt` |
| **Configuration and data** | JSON | `json` (`jsonc`) | `.json` |
| | XML | `xml` (`xaml`, `xsd`, `admx`, `adml`, `wxs`, `csproj`) | `.xml` |
| | YAML | `yaml` (`yml`) | `.yaml` |
| | INI / .conf | `ini` (`conf`, `cfg`, `inf`) | `.ini` |
| | TOML | `toml` | `.toml` |
| | Registry | `reg` | `.reg` |
| | CSV | `csv` | `.csv` |
| | HTML | `html` (`htm`) | `.html` |
| | CSS | `css` | `.css` |
| | Markdown | `markdown` (`md`) | `.md` |
| **Infrastructure** | Bicep | `bicep` | `.bicep` |
| | Terraform / HCL | `hcl` (`terraform`, `tf`) | `.tf` |
| | Dockerfile | `dockerfile` (`docker`) | `Dockerfile` |
| | Nginx / Apache config | `nginx` (`apache`) | `.conf` |
| **Other** | Diff / patch | `diff` (`patch`) | `.diff` |
| | Log output | `log` | `.log` |
| | Plain text | `text` (`txt`, `plaintext`, none) | `.txt` |

- **Colouring:** AvalonEdit's own definition where it ships one for the language; otherwise our own `.xshd` file in
  `src/RemoteHub/Assets/Highlighting/` (our code, AGPL, nothing third-party to declare). Each definition has a dark and
  a light palette. A language whose definition isn't ready yet still works – it is coloured as plain text and keeps its
  name in the header and in the Markdown.
- **Unknown fence ids** (from an import or a paste) are kept as they are, shown in the header (e.g. `fsharp`) and
  coloured as plain text. Nothing is lost on export.
- **Guessing on paste:** from clear signs only, otherwise *Plain text*. Examples: `Get-`/`Set-` cmdlets, `$var =` and
  `| Where-Object` → PowerShell; `#include` → C/C++; `using System;`, `namespace` with `{` → C#; `SELECT … FROM`,
  `UPDATE … SET`, `INSERT INTO` → SQL, and T-SQL when `[dbo]`, `GO` or `@@` appear; a table name followed by `|` and
  `where`/`summarize`/`project` → KQL; `{`/`[` that parses as JSON → JSON; `<?xml` or a single root element → XML;
  `Windows Registry Editor Version 5.00` → Registry; `#!/bin/bash` → Bash. The user's last choice for similar pastes
  isn't learned – it stays predictable.

#### 4.5.6 Storage

The editor's `FlowDocument` is converted to Markdown on every save and back when a page opens. The converter
(`NoteMarkdown`) is our own code on top of [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause, into
`THIRD-PARTY-NOTICES.md`), so only the elements above are ever written or read:

| Element | Markdown |
|---|---|
| Text formats, lists, quotes, links, lines | CommonMark, GitHub task lists and strikethrough |
| Tables | GitHub pipe tables (alignment in the separator row) |
| Images | `![alt text](note-file:<id>){width=640}` – the id points to `note_files`; the width uses Markdig's generic attributes |
| Script blocks | Fenced code with the language as info string (```` ```powershell ````) |

- Storing the `RichTextBox`'s own XAML (`XamlPackage`, `XamlWriter`) is ruled out: loading XAML with `XamlReader` can
  create arbitrary .NET objects, so a page from a backup or later from sync would become an attack path. Markdown is
  data only.
- Markdown that the editor can't show as is (raw HTML, a table cell with a line break, nested block quotes deeper than
  one level, from an import) is kept as text in a paragraph, never dropped. **A page is never changed just by opening
  it**: when nothing was edited, nothing is written back.
- A round trip of every element must give the same Markdown (§11). That is what makes the stored text predictable for
  export and for diffs.

#### 4.5.7 Paste and copy

- **Paste** takes, in this order: our own format (when copying inside RemoteHub, keeps everything including images),
  HTML (from a browser, Word, OneNote, Teams), an image, plain text. HTML is reduced to the elements above; fonts,
  colours, styles, scripts, forms and remote images are dropped. Ctrl+Shift+V pastes plain text.
- **Copy** puts three formats on the clipboard: plain text, HTML (so a paste into Teams, Outlook or Word keeps the
  formatting), and Markdown as text for a text editor or an AI chat via *Copy as Markdown*. Notes are the user's own
  content, so this is the normal clipboard, not `SecureClipboard`.
- **Plain Markdown both ways:** *Copy as Markdown* (Ctrl+Alt+C, right-click menu) puts the selection – or the whole
  page when nothing is selected – on the clipboard as Markdown text. *Paste Markdown* (Ctrl+Alt+V) reads Markdown text
  from the clipboard and inserts it formatted (headings, lists, tables, script blocks …). Ctrl+Alt uses the left Alt
  key only: AltGr types characters on many keyboards.
- **View Markdown** (the *Markdown* toggle at the end of the format bar, Ctrl+Shift+M): the page shows as its Markdown
  text, with colouring, and can be edited there; the format buttons are off meanwhile. Turning it off shows the edited
  Markdown formatted again. The choice is kept (`UiState.NotesMarkdownView`) and applies to every page.
- **Inside a script block** paste, copy and the shortcuts are the script block's own: Ctrl+V pastes plain text into the
  script, and Markdown typed there (`# `, `- `) stays text.

#### 4.5.8 Size limit

1 MB of Markdown text per page plus its images (§4.5.4). Beyond that, the editor says "This page is too large – split
it into several pages" and stops accepting input, rather than saving half a page.

### 4.6 Links to connections

The reference shows pages that belong to a system ("Enable EARv2 in UEM", "Move Bifrost from prod…"). A page can be
**linked to one or more connections or folders** in the tree:

- In the page header: *Linked to: SRV-UEM01 ✕ · + Link*.
- In the tree, linked connections get *Notes (2)* in their context menu. It switches to Notes and filters the page list
  to those pages.
- *Copy link* on a page copies `remotehub://note/<id>`, which opens the page from another note (or from a connection's
  `Notes` field).

The existing short `Notes` field of folders and connections (`TreeNodes.cs`, edited in `EditorWindow.xaml`) stays as it
is. It is a one-line hint shown in the tree and searched by `NodeSearch`. It is not moved into pages.

### 4.7 Export and import

Pages are stored as Markdown (§4.5.6), so an export is close to a copy of what is stored:

- **A page:** *Export…* asks for a folder and writes `<title>.md` (UTF-8 without BOM, starting with `# Title`). Its
  images go into `<title>.assets\` next to it, and the `note-file:` links are rewritten to those relative paths
  (`![alt](Title.assets/image-1.png)`), so the page shows correctly in VS Code, GitHub or Obsidian.
- **A section or notebook:** *Export as Markdown…* writes one folder per section, with one `.md` file (and its assets
  folder) per page. File names come from the titles: characters that Windows doesn't allow in a file name are replaced
  with `-`, reserved names (`CON`, `NUL` …) get a `_`, names are cut at 100 characters, and duplicates get ` (2)`,
  ` (3)`. Nothing is overwritten – the export goes into a new folder `<notebook> <yyyy-MM-dd HH-mm>`.
- **Import:** *Import Markdown files…* on a section adds one page per `.md` file (up to 1 MB each). A first-line
  `# Heading` becomes the title, otherwise the file name does. Images with a **relative** path inside the chosen
  folder are imported (and re-encoded, §4.5.4). Absolute paths, paths with `..` leaving the folder, UNC paths and URLs
  are not followed – they stay as text.
- An export is plain text on disk, outside the encrypted database. The export dialog says so: "Exported pages are not
  encrypted. Keep them somewhere safe." The audit log gets `notes.exported` with the number of pages and no titles.

## 5. Data model

### 5.1 Schema (version 2)

New tables in `remotehub.db`. This is the first schema change after version 1, so it also builds the "numbered
migration steps with a backup before each one" from local-db-design.md §9 and §16.

```sql
notebooks      (id TEXT PK, name TEXT, sort INT,
                version INT, modified_utc INT, dirty INT, deleted_utc INT)
note_sections  (id TEXT PK, notebook_id TEXT, name TEXT, color TEXT, sort INT,
                version INT, modified_utc INT, dirty INT, deleted_utc INT)
notes          (id TEXT PK, section_id TEXT, title TEXT, body TEXT,          -- body: Markdown (§4.5)
                body_text TEXT,                                            -- body without Markdown, for search
                sort INT, created_utc INT,
                version INT, modified_utc INT, dirty INT, deleted_utc INT)
note_files     (id TEXT PK, note_id TEXT, mime TEXT, width INT, height INT, -- images (§4.5.4), PNG or JPEG only
                size INT, sha256 BLOB, data BLOB,
                version INT, modified_utc INT, dirty INT)
note_links     (note_id TEXT, node_id TEXT, PRIMARY KEY (note_id, node_id)) -- §4.6, node = folder or connection
notes_fts      FTS5 (title, body_text, content='notes', content_rowid='rowid',
                     tokenize='unicode61 remove_diacritics 2')
```

- `version`, `modified_utc` and `dirty` are the same columns `nodes` and `credentials` have, so device sync (phase 4)
  can treat notes like every other record.
- `deleted_utc` is set for *Recently deleted* (§6.5). The FTS triggers leave deleted rows out of search.
- `notes_fts` is kept up to date by triggers on `notes` (insert, update of `title`/`body_text`, delete). The app
  writes `body_text` (`NoteMarkdown`, §5.2) with every save, so the triggers don't parse Markdown and the index doesn't
  contain Markdown markers.
- `note_files` holds the images of a page, already re-encoded (§4.5.4). An image whose `note-file:` link no longer
  appears in its page's Markdown is deleted when the page is saved – but only after the page's undo history is gone
  (page switched or closed), so Ctrl+Z can bring a deleted image back. Copying a page copies its images. Images are
  loaded when the page opens, not with the page list.
- Indexes: `note_sections(notebook_id, sort)`, `notes(section_id, sort)`, `notes(modified_utc)`,
  `note_files(note_id)`, `note_links(node_id)`.
- No field-level encryption for notes: the whole file is already encrypted (ChaCha20-Poly1305, local-db-design.md §3).
  Field-level encryption is for passwords that should stay encrypted in memory and in query results, and notes have
  to be decrypted to be shown and searched anyway.

### 5.2 Code

| Piece | Where | Does |
|---|---|---|
| `NoteStore` | `Data/NoteStore.cs` | Read and write notebooks, sections, pages and links through `LocalDb` (one writer, as today, §15 there). Search through `LocalDb.Read`. |
| Migration step 2 | `Data/LocalDb.cs` | Backup (`backup\remotehub-<stamp>-pre-v2.db`), create the tables in one transaction, set `meta.schema_version = 2`. |
| `NoteMarkdown` | `Services/NoteMarkdown.cs` | `FlowDocument` ↔ Markdown for the elements in §4.5.6, Markdown → plain text for search, HTML → `FlowDocument` for paste, file names for export (§4.7). |
| `NoteImages` | `Services/NoteImages.cs` | Decode, scale, re-encode and check images (§4.5.4). |
| `NotesViewModel` | `ViewModels/NotesViewModel.cs` | Selection, list order, autosave, search. |
| `NotesView` | `Views/NotesView.cs` | The three panes. |
| `NoteEditor` | `Views/NoteEditor.cs` | The `RichTextBox`, the format bar, table editing, image handles. |
| `ScriptBlock` | `Views/ScriptBlock.cs` | The script block (AvalonEdit `TextEditor` with header, copy and menu) inside a `BlockUIContainer`, and the caret hand-over to the page (§4.5.5). |
| `ViewRail` | `Views/ViewRail.cs` | The rail and its expanded `Popup`. |

The notes are **not** part of `AppData`, and `SettingsStore` doesn't load them. Loading all notes at start would cost
time for no reason: the page list loads titles only (`id, title, sort, modified_utc`) when a section is opened, and the
body loads when a page is opened.

### 5.3 Downgrade

Not supported. RemoteHub isn't GA yet (local-db-design.md §9 already accepts this before GA), so going back to an older
version after the upgrade to schema version 2 isn't a goal. The pre-v2 backup (§5.2) is there for a failed migration,
not for downgrades. Upgrading an existing version-1 database stays a requirement – the databases of current
installations move forward.

## 6. Saving, undo and deleting

### 6.1 Autosave

- The page is saved **1 second after the last keystroke**, and at once when the user switches page, section or view,
  when the window loses focus, before auto-lock (`MainWindow.AutoLock.cs`) and when RemoteHub closes.
- A save writes one row (the page, plus any new images) and is well under a millisecond (local-db-design.md §15). The
  conversion of the `FlowDocument` to Markdown runs on the UI thread, where the document lives. For a large page
  (1 MB of text) it must stay under 30 ms, measured (§11). `body_text` for search is worked out on a background thread
  from the Markdown.
- Images are written when they are inserted, not on every save.
- If a save fails (disk full, database closed by a lock), the page header shows "Not saved – retrying" and RemoteHub
  retries every 5 seconds. The text stays in the editor. Switching away from an unsaved page is still allowed. The
  unsaved Markdown is kept in memory and saved on the next retry.

### 6.2 Undo

- The editor's own undo (Ctrl+Z / Ctrl+Y) works within the page while it is open, including inserting and deleting
  tables, images and script blocks. Inside a script block, Ctrl+Z undoes that block's typing first (§4.5.5). Switching
  page clears it.
- There is no version history for pages in v1. The daily database backup (local-db-design.md §10) is the safety net.

### 6.3 Auto-lock

When RemoteHub locks (`AppLock`), the windows are hidden as today. In addition, the open page is saved and the editor
is cleared. After unlocking, the page is loaded again. This keeps the page's text out of the window's visual tree while
locked (a UI Automation client could otherwise read it while the window is hidden).

### 6.4 Exit

Closing RemoteHub saves the open page synchronously before the database closes.

### 6.5 Deleting

- Deleting a page or section sets `deleted_utc` and shows a toast "Page deleted · Undo" (same snackbar as the other
  info toasts) for 8 seconds.
- Deleting a section or notebook asks first ("Delete the section 'Devwork' and its 14 pages?"). Pages don't ask.
- *Recently deleted* (at the bottom of the notebook pane) shows what was deleted in the last 30 days, with *Restore*
  and *Delete now*. After 30 days, rows are removed for good in the same background task that applies audit retention.
  `secure_delete = ON` (local-db-design.md §5) overwrites the deleted content.

## 7. Search

- The search box in the page pane's toolbar (and Ctrl+F) searches **all notebooks and sections** with `notes_fts`.
  The result replaces the page list: title, section (with its colour bar) and a snippet with the matching words in
  bold. Esc clears the search.
- Query: each word as a prefix (`word*`), all words must match, as in `NodeSearch`. FTS5 syntax that the user types
  (`"`, `NEAR`, `-`) is escaped, so typing a quote can't cause a query error.
- Search covers titles, text, table cells, the text of script blocks and the images' alternative text.
- Opening a result opens the page and highlights the matches, also inside script blocks.
- The connection tree's search (`NodeSearch`) does not search pages. It only searches the short `Notes` field, as today.

## 8. Policy

A new console feature in `SOFTWARE\Policies\RemoteHub` (admx-design.md §5):

| Value | Blocks |
|---|---|
| `AllowNotes` | The Notes view. The rail shows no Notes icon, and Ctrl+2 and `remotehub://note/…` links do nothing. Notes already stored are **kept** (not deleted) and come back if the policy is removed. Backups (§9.1) still include them. |

- `1` = allowed (the same as not set – there is no user switch to lock).
- It goes into `PolicyCatalog`, the ADMX/ADML and *Settings → Policies*, the same way as the other `Allow*` values.
- In the template under *RemoteHub Console → Features → Notes* (admx-design.md §5.7), since 0.19.0.

## 9. Security and privacy

| Threat | Measure |
|---|---|
| Notes on a stolen disk or in a copied DB file | Whole-file encryption (local-db-design.md §3–§5). The FTS index and WAL are inside the encrypted file too. |
| Note text in logs | `DiagLog` never gets titles, bodies, section names or search text – only ids and counts ("Notes: saved page, 3.2 KB"). Exceptions from `NoteMarkdown` are logged with `ex.GetType().Name`, not `ex.Message`, because a parser message can quote the input. |
| Note text in the audit log | Audit events (`note.added`, `note.changed`, `note.deleted`, `note.restored`) record the page id and the section id only. **No title**: a title like "Password for SRV-UEM01" is content. |
| Note text in telemetry or crash reports | Telemetry counts views only (`view.notes.opened`), no counts of pages. `CrashReporter` already sends no window content. A test checks that a crash report taken with a page open contains none of its text. |
| Malicious content (from a backup, an import or later sync) | Stored as Markdown, never loaded as XAML (§4.5.6). Links: only `http`, `https`, `mailto` and `remotehub://note/` are clickable, and opening an `http(s)` link asks first. Raw HTML stays text (Markdig's HTML extension is off). Images are only ever loaded from `note_files`, never from a path or URL. |
| Script blocks | Text only: never run, never sent to a session, no *Run* command anywhere in Notes (§4.5.5). *Save as file* writes the text with the language's extension and nothing else. |
| Malicious images (pasted, dropped or imported) | Decoded by WPF's built-in decoders (WIC) with `BitmapCreateOptions.IgnoreColorProfile` and a size check on the header before the full decode (more than 16,384 px or 25 MB is refused before pixels are read), then re-encoded, so only plain pixels are stored (§4.5.4). |
| Search queries as SQL | Parameters only; the FTS query is escaped (§7). |
| Temp files | None. Paste, images and the editor work in memory. Export writes only the files the user asked for, and says they aren't encrypted (§4.7). |
| Malicious import files | Imported `.md` files are text up to 1 MB each and go through the same converter as any page. Image paths are followed only inside the chosen folder (§4.7). |
| Screen recordings of a session | Notes are not inside a session, so `SessionRecorder` never records them. |
| Clipboard | Copying from a note goes through the normal clipboard (§4.5). Secret fields in notes are a non-goal (§2). Passwords belong in connections and saved credentials, which use `SecureClipboard`. |

### 9.1 Backup

- `.rhbackup` (`Backup.Export` in `Services/SettingsStore.cs`) gains the notebooks, sections, pages, images and links,
  encrypted with the backup password like everything else in the file. Older RemoteHub versions don't need to read the
  new format (not GA, §5.3); backups from older versions, without notes, still import.
- Import: *Add as folder* adds the notes as a second notebook, named after the backup file. *Replace everything* also
  replaces the notes, after the existing confirmation. The audit entry `backup.imported` gets "… and N pages".
- `*.rdpmbackup` (from RdpManager) has no notes and imports as today.
- The export dialog's confirmation counts pages too: "… 412 folders and connections, 6 saved credentials, 128 pages".

### 9.2 Shared-folder mode

In shared-folder mode (`SettingsFolder`), the connections live in `RemoteHub.settings`. Notes stay in this PC's
database. The Notes view says so once, at the top: "Notes are stored on this PC only. They aren't in the shared
settings folder." This lasts until device sync (local-db-design.md §8) is built.

### 9.3 Changes to other documents

- **local-db-design.md:** §6 – replace the `notes` table with §5.1 of this document (sections and links instead of
  `connection_id`/`folder_id`); §12 – phase 3 points here; §16 – the migration steps are built with schema version 2.
- **admx-design.md:** §5.7 – `AllowNotes` (§8).
- **README:** the feature list gets Notes; screenshots show the rail.

## 10. Phases

| Phase | Content | Done when |
|---|---|---|
| 1 | **View rail** with Remote Control, Notes (empty view saying "Notes are coming"), version and Settings; Settings removed from the sidebar; hover/keyboard expand; `LastView`; Ctrl+1/2 | Switching views never resizes or disconnects a session (checked with RDP and agent desktop sessions open) |
| 2 | **Notes core:** schema v2 and migration step, `NoteStore`, notebooks, sections and pages, the WYSIWYG editor with text formats (§4.5.2) and **script blocks** (§4.5.5), paste and copy, autosave, delete with undo and *Recently deleted*, search | The tests in §11 pass; 10,000 pages load and search within the local-db-design.md §7 targets |
| 3 | **Tables and images** (§4.5.3, §4.5.4), Markdown export and import (§4.7) | Round trip of every element; export opens correctly in VS Code |
| 4 | **Integration:** backup export/import, links to connections, `remotehub://note/` links, `AllowNotes` policy, audit events | Backup round trip with notes and images; the policy hides the view |
| 5 | Print / PDF, text highlighting colours | – |
| Later | Scripts and Logs as views (§3.8), each with its own design | – |
| Later | Sync with device sync (local-db-design.md phase 4) | – |

## 11. Tests

xUnit in `tests/RemoteHub.Tests`. A new `NotesTests` class (with a `/// <summary>` describing the area), using the
same `[Collection]` as `LocalDbTests`, because both use a database in a temp folder.

- `Notes_survive_a_restart_of_the_database` – create section and page, close, reopen with the key, read back.
- `Schema_version_1_database_migrates_to_version_2_and_keeps_its_connections` – with a backup file written first.
- `A_failed_migration_leaves_the_version_1_database_unchanged`.
- `Every_element_survives_a_round_trip` – for each element in §4.5.6, Markdown → `FlowDocument` → Markdown gives the
  same text. Runs on an STA thread; a `[SkippableFact]` if the runner can't create a `FlowDocument`.
- `Opening_a_page_without_editing_writes_nothing` and `Unsupported_markdown_is_kept_as_text`.
- `Script_block_text_is_stored_exactly` – tabs, trailing spaces, backticks and ```` ``` ```` inside the script (the
  fence gets longer), CRLF; the language survives.
- `Pasted_code_is_offered_as_a_script_block` – each guessing rule in §4.5.5a picks its language; prose and unclear
  code stay plain text.
- `Fence_aliases_map_to_one_language_and_unknown_ids_are_kept` (`ps1` → `powershell`, `c#` → `csharp`; `fsharp` stays).
- `Table_cells_keep_inline_formats_and_pipes_are_escaped`.
- `Images_are_re_encoded_without_metadata` – a JPEG with EXIF GPS comes back without it; an image that claims
  100,000 px in its header is refused before decoding; a file that isn't an image is refused.
- `Images_no_longer_in_a_page_are_removed_after_the_undo_history_is_gone`.
- `Pasted_html_keeps_only_the_supported_elements` – fonts, colours, scripts and remote images are dropped;
  `Raw_html_in_markdown_stays_text`.
- `Large_page_converts_within_30_ms` (benchmark project).
- `Export_writes_one_file_per_page_with_safe_unique_names` – titles with `\ / : * ? " < > |`, reserved names (`CON`,
  `NUL`), 300 characters and duplicates; the export never writes outside the chosen folder.
- `Import_takes_the_first_heading_as_title_and_refuses_files_over_1_MB`;
  `Import_follows_image_paths_only_inside_the_chosen_folder` (`..\`, absolute, UNC and URL paths stay text).
- `Rail_version_shows_major_and_minor_collapsed_and_the_full_version_expanded`.
- `Only_http_https_mailto_and_note_links_are_clickable` – `javascript:`, `file:`, `\\server\share` links stay text.
- `Search_finds_words_by_prefix_and_ignores_deleted_pages`; `Search_text_with_quotes_does_not_throw`.
- `Deleted_pages_are_removed_after_30_days`.
- `Backup_round_trip_includes_notes`; `An_older_backup_without_notes_imports`.
- `Diag_log_has_no_note_text` – save, search and fail a save, then check that the `DiagLog` file has none of the
  test page's words.
- `Unknown_last_view_opens_remote_control`.
- Performance (as in local-db-design.md §7, in the benchmark project): open a section of 1,000 pages < 50 ms; open a
  page of 1 MB < 100 ms; full-text search of 10,000 pages < 50 ms.

**Lab:** no lab test is needed. Notes touch neither the agent nor the MSI nor sign-in. The view switch with running
sessions (phase 1) is checked by hand against a lab VM with RDP and agent desktop sessions open and written into the
phase's PR.

## 12. Decided

The questions of Draft v1, answered on 2026-10-19:

| # | Question | Decision |
|---|---|---|
| 1 | One notebook or several? | **Several**, shown as groups in the notebook pane (§4.2). |
| 2 | Editor | First a Markdown editor with preview (v2), then changed: a **rich WYSIWYG editor with tables, images and – most important – script blocks** (§4.5), stored as Markdown so export and use with other tools and AI stay easy (§4.7). Native `RichTextBox` with AvalonEdit for script blocks, not WebView2 (§4.5.1). |
| 3 | Settings in the rail? | **Yes.** Settings moves to the bottom of the rail and leaves the sidebar (§3.1). |
| 4 | Rail speed | **Faster:** 150 ms to expand, 150 ms to collapse, 100 ms animation (§3.3). |
| 5 | Secret format in notes | **Not needed** (§2). |
| 6 | Import from OneNote | **Not needed** (§2). Markdown files can be imported (§4.7). |
| 7 | More views | **Yes:** Scripts and Logs later (§3.8). The version is shown above Settings (§3.9). |
| 8 | Run or send script blocks? | **No.** Notes are documentation only – no running, no sending to a session (§2, §4.5.5). |
| 9 | Script languages | **All relevant ones**, including C, C++, C# and the SQL dialects (§4.5.5a). |
| 10 | Downgrades to older versions | **Not supported** – RemoteHub isn't GA yet (§5.3). |

Nothing is open.

## 13. As built (phases 1–4)

Built on the branch `notes-view` (2026-10-19). Code: `Views/ViewRail.cs`, `MainWindow.Views.cs`, `Views/NotesView.cs`,
`Views/NoteEditor.cs`, `Views/ScriptBlock.cs`, `Views/ScriptHighlighting.cs`, `Views/NoteImageView.cs`,
`Views/NotesDialogs.cs`, `Data/NoteStore.cs`, `Services/NoteMarkdown.cs`, `Services/NoteHtml.cs`,
`Services/NoteImages.cs`, `Services/NoteFiles.cs`, `Services/ScriptLanguages.cs`. Tests: `NotesTests`,
`NoteMarkdownTests`, `NoteFilesTests`.

| Plan | Built | Why |
|---|---|---|
| `AllowNotes` policy (§8) | As planned, through `Features` like the other `Allow*` values. With Notes blocked, Ctrl+2 isn't taken (it goes on to the session); a saved `LastView` of Notes opens Remote Control but is kept for when the policy is removed. A change while RemoteHub runs applies when the window is next activated; an open page is saved first | Policies are read when asked (admx-design.md §5), and the rail is only rebuilt when the value changed. |
| `NotesViewModel` (§5.2) | The logic lives in `NotesView` | Like the other views built in code (`RegistryView`, `FileBrowserView`). |
| FTS triggers on `notes` (§5.1) | `NoteStore` writes the index in the same transaction; `notes` has an `rid` column for the index | With `trusted_schema = OFF`, triggers can't write to an FTS5 table; `rid` can't be renumbered by `VACUUM`. |
| Narrow window: panes collapse into drop-downs (§4.1) | Not built | The panes have minimum widths; the window's minimum width still fits them. |
| Table columns resized by dragging (§4.5.3) | Not built | Columns share the width. The width was display-only anyway. |
| Reordering by dragging (§4.2, §4.3) | *Move up / Move down* in the context menus; dragging a page onto a section and a section onto a notebook moves it | Dragging inside the lists can come later. |
| *Paste as script block* offered (§4.5.5) | Code-looking text pasted into an empty paragraph becomes a script block; Ctrl+Z undoes it. Tab-separated rows (Excel) become a table | Fewer clicks; the undo is the way back. |
| Page undo covers script blocks as one step (§6.2) | A script block has its own undo while the caret is in it; the page's undo doesn't include typing inside a block | AvalonEdit and the RichTextBox keep separate histories. |
| Images: over 16,384 px refused (§9) | Scaled down to 16,384 px; refused when the header claims more than 65,535 px a side or 300 megapixels, or the file is over 64 MB | Large screenshots of several monitors stay usable; decompression bombs are still refused before decoding. |
| Touch: press-and-hold shows the expanded rail (§3.3) | Not built | Tapping an icon switches the view. |
| F6 from the title bar to the rail; rail items as a list ("Notes, 2 of 2, selected") (§3.3, §3.4) | Not built: Tab reaches the rail, Up/Down move in it, the selected item is announced as "selected" | A list control with its own selection would fight the popup copy of the items; can follow. |
| Tooltips on the rail only when it can't expand (§3.4) | Every rail button has a tooltip "Label (shortcut)" | The same as every other button in the main window. |
| Page title in `TitleTextBlockStyle` (§4.4) | 26 px semibold, the same size as a heading 1 in the page | The theme style is for text blocks, not the editable title box. |
| Notebook export folder name `<notebook> <yyyy-MM-dd HH-mm>` | As planned; a page export writes `Title.md` (+ `Title.assets\`) into the chosen folder, never overwriting (`Title (2).md`) | – |

Audit events: `note.added`, `note.changed` (once per page and run), `note.deleted`, `note.restored`, `notes.exported`,
`notes.imported` – ids and counts only, in the audit viewer's group *Notes*.

Checked in the running app (test data folder, UI Automation): the rail, switching views, notebooks and sections, a page
with SQL and PowerShell script blocks, a check list, a table, a quote and an image, and search with highlighted
snippets. Not checked by hand yet: hovering the rail, typing in the editor (shortcuts, Markdown typed at line starts),
paste from Word/Excel/browsers, drag and drop, image resizing, export/import dialogs, the light theme, and switching
views with RDP and agent desktop sessions open.
