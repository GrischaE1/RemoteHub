# RemoteHub – Notes and the view rail

Status: Draft v2 (2026-10-19), checked against the code on `main` (127cbf3). Nothing is built yet. v2 takes in the
answers to the open questions of v1 (§12): several notebooks, a Markdown editor with preview, Settings and the version
in the rail, a faster rail, no secret format, no OneNote import.
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
| **Notes** | Three panes, as in OneNote: notebooks and their sections with a colour, the pages of the selected section, and the page itself. Pages are **Markdown**, shown formatted and edited as Markdown, so they can be exported and used as they are in other tools and with AI. The typical content is commands, SQL and configuration snippets. Notes are saved automatically and can be searched. |

### 1.1 Principles

1. **Switching views never touches a session.** RDP, agent desktop and PowerShell sessions keep running, at the same
   size, while Notes is shown. Coming back shows them exactly as they were.
2. **Notes are as safe as passwords at rest.** They live in the encrypted database (local-db-design.md §3) and nowhere
   else: not in `local.json`, `DiagLog`, telemetry, crash reports or temp files. Notes regularly hold secrets – the
   reference example contains a client secret.
3. **Nothing typed is ever lost.** Autosave, no Save button, and deleting goes through *Recently deleted* (§6.5).
4. **The rail is navigation, not a toolbar.** It holds views and Settings. Actions belong to the view (the sidebar's
   *New* button, the Notes toolbar).

## 2. Non-goals (v1)

- **Free placement of text boxes on a canvas** (OneNote's page model). Pages are a single flowing document.
- **Ink, audio, embedded files and tables.** Pictures come in phase 4 (§10), with the `note_files` table planned in
  local-db-design.md §6.
- **Sharing notes with other users.** This waits for a server, as connections do.
- **Sync between the user's devices.** This comes with device sync (local-db-design.md §8, phase 4 there). Until
  then, notes stay on the PC, also in shared-folder mode.
- **Section groups** (sections inside sections). Notebooks and sections are enough for v1.
- **A WYSIWYG editor.** Pages are edited as Markdown text, with a formatted preview (§4.5).
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
- **Click:** opens `AboutWindow` (as `About_Click`), or `UpdateWindow` when an update is available. The tooltip and
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
- **Toolbar** on the right of the date line: *Edit* / *Done* (§4.5), *Copy as Markdown*, *Export…* (§4.7).
- **Body** – the preview or the editor (§4.5), filling the rest of the card, with its own scrolling.

### 4.5 Markdown: preview and editor

Pages are **Markdown** (CommonMark plus GitHub's tables, task lists and strikethrough). What is stored is exactly what
the user typed. Nothing is converted, so a page that is copied or exported goes into another tool, a Git repository or
an AI prompt as it is. Rich-text storage (the `RichTextBox`'s XAML) is ruled out: loading XAML with `XamlReader` can
create arbitrary .NET objects, so a page from a backup or later from sync would become an attack path. Markdown is
data only.

**Two modes, one page**

| Mode | Shows | How to get there |
|---|---|---|
| **Preview** (default) | The page formatted: headings, lists, check lists, tables, links, code blocks | Opening a page; *Done*; Esc; Ctrl+E |
| **Edit** | The Markdown text in a monospace `ui:TextBox` (`AcceptsReturn`, `AcceptsTab`) | *Edit*; double-click in the preview; Ctrl+E; a new page |

- **Side by side:** when the page card is at least 1,100 px wide, *Edit* shows the editor on the left and the preview
  on the right, updated 300 ms after typing stops. When it is narrower, the two modes take turns. The choice is stored
  in `UiState` (`NotesSplitPreview`).
- Double-clicking a paragraph in the preview opens the editor with the cursor on that paragraph's line (Markdig keeps
  each block's source line).
- A new page opens in Edit mode with the cursor in the title.

**The editor** is a text editor, not a formatter, but it helps with the usual things:

| Action | Shortcut | Does |
|---|---|---|
| Bold, italic | Ctrl+B, Ctrl+I | `**…**`, `*…*` around the selection |
| Inline code | Ctrl+` | `` `…` `` around the selection |
| Code block | Ctrl+Shift+` | ```` ``` ```` fences around the selected lines |
| Link | Ctrl+K | `[selection](…)` |
| Continue a list | Enter at the end of a list item | the next `-`, `1.` or `- [ ]` |
| Indent, outdent | Tab, Shift+Tab | two spaces in lists, four elsewhere |

Ctrl+B inside the editor means bold, not "collapse sidebar". In Notes, the editor handles Ctrl+B first.

**The preview** is a read-only `FlowDocumentScrollViewer`. Its `FlowDocument` is built from Markdig's syntax tree by our
own code (`NoteMarkdown.ToFlowDocument`), so only these elements can ever appear:

- Headings, paragraphs, emphasis, strikethrough, inline code, block quotes, bulleted, numbered and task lists, tables,
  thematic breaks and links.
- **Code blocks:** monospace (`Cascadia Mono`, then `Consolas`), the theme's code background, their own horizontal
  scrolling, no wrapping, and a *Copy* button in the top right that copies the block's text. The info string
  (```` ```sql ````) is shown as a small label. Syntax colouring is not in v1.
- **Task lists:** clicking a box in the preview ticks it and changes `[ ]` to `[x]` in the text. This is the only
  change the preview can make.
- **Links:** only `http`, `https`, `mailto` and `remotehub://note/` (§4.6) are clickable. `http(s)` asks first
  ("Open https://… in your browser?"). Other links (`file:`, `javascript:`, UNC paths) are shown as text.
- **Raw HTML** is shown as text (Markdig's HTML extension is off). **Images** are shown as their alt text and address
  in v1 – no picture is loaded from anywhere (§10, phase 4).

**Paste and copy**

- Pasting into the editor pastes plain text. When the clipboard holds only HTML (from a browser or OneNote), it is
  converted to Markdown for the elements above, and everything else is dropped.
- *Copy as Markdown* copies the whole page (title as `# Title`, then the body). Copying a selection in the preview
  copies its plain text. Notes are the user's own content, so this is the normal clipboard, not `SecureClipboard`.

**Parsing:** [Markdig](https://github.com/xoofx/markdig) (BSD-2-Clause) with the pipelines for the elements above. It
goes into `THIRD-PARTY-NOTICES.md`.

**Size limit:** 1 MB of Markdown per page. Beyond that, the editor says "This page is too large – split it into
several pages" and stops accepting input, rather than saving half a page.

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

Pages are Markdown, so an export is a copy of what is stored:

- **A page:** *Export…* saves `<title>.md` (UTF-8 without BOM). The file starts with the title as `# Title`.
- **A section or notebook:** *Export as Markdown…* asks for a folder and writes one folder per section, with one `.md`
  file per page. File names come from the titles: characters that Windows doesn't allow in a file name are replaced
  with `-`, names are cut at 100 characters, and duplicates get ` (2)`, ` (3)`. Nothing is overwritten – the export
  goes into a new folder `<notebook> <yyyy-MM-dd HH-mm>`.
- **Import:** *Import Markdown files…* on a section adds one page per `.md` file (up to 1 MB each). A first-line
  `# Heading` becomes the title, otherwise the file name does. Files are read as text only – nothing in them is
  followed or loaded.
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
- Indexes: `note_sections(notebook_id, sort)`, `notes(section_id, sort)`, `notes(modified_utc)`,
  `note_links(node_id)`.
- No field-level encryption for notes: the whole file is already encrypted (ChaCha20-Poly1305, local-db-design.md §3).
  Field-level encryption is for passwords that should stay encrypted in memory and in query results, and notes have
  to be decrypted to be shown and searched anyway.

### 5.2 Code

| Piece | Where | Does |
|---|---|---|
| `NoteStore` | `Data/NoteStore.cs` | Read and write notebooks, sections, pages and links through `LocalDb` (one writer, as today, §15 there). Search through `LocalDb.Read`. |
| Migration step 2 | `Data/LocalDb.cs` | Backup (`backup\remotehub-<stamp>-pre-v2.db`), create the tables in one transaction, set `meta.schema_version = 2`. |
| `NoteMarkdown` | `Services/NoteMarkdown.cs` | Markdown → `FlowDocument` for the preview (§4.5), Markdown → plain text for search, HTML → Markdown for paste, file names for export (§4.7). |
| `NotesViewModel` | `ViewModels/NotesViewModel.cs` | Selection, list order, autosave, search. |
| `NotesView` | `Views/NotesView.cs` | The three panes. |
| `ViewRail` | `Views/ViewRail.cs` | The rail and its expanded `Popup`. |

The notes are **not** part of `AppData`, and `SettingsStore` doesn't load them. Loading all notes at start would cost
time for no reason: the page list loads titles only (`id, title, sort, modified_utc`) when a section is opened, and the
body loads when a page is opened.

### 5.3 Downgrade

An older RemoteHub opens a version-2 database. It ignores tables it doesn't know, so connections keep working, and notes
are simply not shown. The migration code must therefore not change any version-1 table, and older versions must accept
`schema_version = 2`. Check: does 0.18.x refuse a higher `schema_version`? If it does, the release notes say that
downgrading needs the pre-v2 backup.

## 6. Saving, undo and deleting

### 6.1 Autosave

- The page is saved **1 second after the last keystroke**, and at once when the user switches page, section or view,
  when the window loses focus, before auto-lock (`MainWindow.AutoLock.cs`) and when RemoteHub closes.
- A save writes one row (the page) and is well under a millisecond (local-db-design.md §15). The editor's text is
  the Markdown itself, so nothing is converted to save; only `body_text` for search is worked out, on a background
  thread from a copy of the text.
- If a save fails (disk full, database closed by a lock), the page header shows "Not saved – retrying" and RemoteHub
  retries every 5 seconds. The text stays in the editor. Switching away from an unsaved page is still allowed. The
  unsaved Markdown is kept in memory and saved on the next retry.

### 6.2 Undo

- The editor's own undo (Ctrl+Z / Ctrl+Y) works within the page while it is open. Switching page clears it.
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
- Opening a result opens the page in Preview and highlights the matches.
- The connection tree's search (`NodeSearch`) does not search pages. It only searches the short `Notes` field, as today.

## 8. Policy

A new console feature in `SOFTWARE\Policies\RemoteHub` (admx-design.md §5):

| Value | Blocks |
|---|---|
| `AllowNotes` | The Notes view. The rail shows no Notes icon, and Ctrl+2 and `remotehub://note/…` links do nothing. Notes already stored are **kept** (not deleted) and come back if the policy is removed. Backups (§9.1) still include them. |

- `1` = allowed (the same as not set – there is no user switch to lock).
- It goes into `PolicyCatalog`, the ADMX/ADML and *Settings → Policies*, the same way as the other `Allow*` values.
- **Coordination:** the policy work is in progress on the `admx-phase3` branch. `AllowNotes` should be added there or
  after it is merged, not in parallel on this branch.

## 9. Security and privacy

| Threat | Measure |
|---|---|
| Notes on a stolen disk or in a copied DB file | Whole-file encryption (local-db-design.md §3–§5). The FTS index and WAL are inside the encrypted file too. |
| Note text in logs | `DiagLog` never gets titles, bodies, section names or search text – only ids and counts ("Notes: saved page, 3.2 KB"). Exceptions from `NoteMarkdown` are logged with `ex.GetType().Name`, not `ex.Message`, because a parser message can quote the input. |
| Note text in the audit log | Audit events (`note.added`, `note.changed`, `note.deleted`, `note.restored`) record the page id and the section id only. **No title**: a title like "Password for SRV-UEM01" is content. |
| Note text in telemetry or crash reports | Telemetry counts views only (`view.notes.opened`), no counts of pages. `CrashReporter` already sends no window content. A test checks that a crash report taken with a page open contains none of its text. |
| Malicious content (from a backup or later sync) | Stored as Markdown, never loaded as XAML (§4.5). Links: only `http`, `https`, `mailto` and `remotehub://note/` are clickable, and opening an `http(s)` link asks first ("Open https://… in your browser?"). There is no script, no HTML block (Markdig's HTML extension is off – raw HTML stays as text) and no remote image loading. |
| Search queries as SQL | Parameters only; the FTS query is escaped (§7). |
| Temp files | None. Paste and the preview work in memory. Export writes only the files the user asked for, and says they aren't encrypted (§4.7). |
| Malicious import files | Imported `.md` files are text up to 1 MB each and go through the same preview rules as any page. |
| Screen recordings of a session | Notes are not inside a session, so `SessionRecorder` never records them. |
| Clipboard | Copying from a note goes through the normal clipboard (§4.5). Secret fields in notes are a non-goal (§2). Passwords belong in connections and saved credentials, which use `SecureClipboard`. |

### 9.1 Backup

- `.rhbackup` (`Backup.Export` in `Services/SettingsStore.cs`) gains the notebooks, sections, pages and links,
  encrypted with the backup password like everything else in the file. The backup format gets a version number, so an
  older RemoteHub that imports a newer file ignores the notes and imports the connections.
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
- **admx-design.md:** §5 – add `AllowNotes` (§8).
- **README:** the feature list gets Notes; screenshots show the rail.

## 10. Phases

| Phase | Content | Done when |
|---|---|---|
| 1 | **View rail** with Remote Control, Notes (empty view saying "Notes are coming"), version and Settings; Settings removed from the sidebar; hover/keyboard expand; `LastView`; Ctrl+1/2 | Switching views never resizes or disconnects a session (checked with RDP and agent desktop sessions open) |
| 2 | **Notes core:** schema v2 and migration step, `NoteStore`, notebooks, sections and pages, Markdown editor and preview (§4.5), autosave, delete with undo and *Recently deleted*, search, Markdown export and import (§4.7) | The tests in §11 pass; 10,000 pages load and search within the local-db-design.md §7 targets |
| 3 | **Integration:** backup export/import, links to connections, `remotehub://note/` links, `AllowNotes` policy, audit events | Backup round trip with notes; the policy hides the view |
| 4 | Pictures in pages (`note_files`), syntax colouring in code blocks, print / PDF | – |
| Later | Scripts and Logs as views (§3.8), each with its own design | – |
| Later | Sync with device sync (local-db-design.md phase 4) | – |

## 11. Tests

xUnit in `tests/RemoteHub.Tests`. A new `NotesTests` class (with a `/// <summary>` describing the area), using the
same `[Collection]` as `LocalDbTests`, because both use a database in a temp folder.

- `Notes_survive_a_restart_of_the_database` – create section and page, close, reopen with the key, read back.
- `Schema_version_1_database_migrates_to_version_2_and_keeps_its_connections` – with a backup file written first.
- `A_failed_migration_leaves_the_version_1_database_unchanged`.
- `Pages_are_stored_exactly_as_typed` – Markdown with every element in §4.5, trailing spaces and CRLF/LF mixed comes
  back byte for byte.
- `Preview_shows_only_the_supported_elements` – each element in §4.5 becomes the expected `FlowDocument` block; a
  `[SkippableFact]` if creating a `FlowDocument` needs an STA thread the runner can't give.
- `Ticking_a_task_in_the_preview_changes_only_that_box`.
- `Pasted_html_becomes_markdown_with_only_the_supported_elements` and `Raw_html_in_markdown_stays_text`.
- `Export_writes_one_file_per_page_with_safe_unique_names` – titles with `\ / : * ? " < > |`, reserved names (`CON`,
  `NUL`), 300 characters and duplicates; the export never writes outside the chosen folder.
- `Import_takes_the_first_heading_as_title_and_refuses_files_over_1_MB`.
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
| 2 | Editor | **Markdown editor with preview** (§4.5). It also makes export and use in other tools and with AI easy (§4.7). |
| 3 | Settings in the rail? | **Yes.** Settings moves to the bottom of the rail and leaves the sidebar (§3.1). |
| 4 | Rail speed | **Faster:** 150 ms to expand, 150 ms to collapse, 100 ms animation (§3.3). |
| 5 | Secret format in notes | **Not needed** (§2). |
| 6 | Import from OneNote | **Not needed** (§2). Markdown files can be imported (§4.7). |
| 7 | More views | **Yes:** Scripts and Logs later (§3.8). The version is shown above Settings (§3.9). |

Still open:

- The downgrade check in §5.3 (does 0.18.x accept `schema_version = 2`?) – to be answered when phase 2 starts.
