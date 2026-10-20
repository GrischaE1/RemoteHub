# RemoteHub – Scripts view

Status: v4 (2026-10-20) – phases 1–4 are built on the branch `scripts-view-design`; §15 lists what differs from the
plan. v2 took in the answers to the open questions of v1
(§13): a fourth run context *As me*, favourites as links like the connection tree's (0.21.0), the console user for
*User* runs, no script signing, and shared undo, *Recently deleted* and colour pieces that connections get too.
v3 (2026-10-20): autosave writes a **draft**, *Ready to deploy* makes it a **version**, the last 10 versions are kept
and can be **compared** like a Git diff (§4.10), and many scripts can be **imported and exported** at once (§4.7).
Owner: Grischa Ernst

This is phase 5 ("Scripts") of [local-db-design.md](local-db-design.md) §12. It is the third view on the view rail
([notes-view-design.md](notes-view-design.md) §3.8). Where this document and those documents disagree, this document
wins, and §14 lists what changes in them. It builds on main as of 0.21.0 (connection favourites, filter and sort).

## 1. Goal

| Part | What |
|---|---|
| **Scripts view** | A view on the rail, next to Notes. It has three panes: a tree of folders, the scripts of the selected folder, and the script in a code editor with syntax colouring and IntelliSense. Scripts are either **PowerShell** (`.ps1`) or **SQL** (`.sql`). Folders and scripts have a **colour**, and scripts can be pinned to **Favourites**, both the way connections have them in Remote Control. |
| **Run context** | Every PowerShell script says how it runs on the device: **System** (`NT AUTHORITY\SYSTEM`), **User** (the user signed in on the device), **Both** (System first, then User) or **As me** (the account RemoteHub signs in with). |
| **Run a script** | *Run script…* in a connection's *Tools* menu, offered where PowerShell remoting (WinRM) or the agent's PowerShell is available. It opens a window to search and filter the scripts, then runs the chosen one on that device and shows the result. Only PowerShell scripts can be run. SQL scripts are kept, coloured and copied, but never run. |
| **Versions** | Typing is saved automatically as a **draft**. *Ready to deploy* turns the draft into a numbered **version**, and only versions are run on devices. The last 10 versions are kept, and any two can be compared side by side with the changes marked, like a Git diff. |
| **Import and export** | Many scripts at once: files, whole folders, or a RemoteHub script package that keeps folders, settings, colours and versions. |
| **Script library** | The scripts of the *PowerShell Script Catalog for Windows Administrators* (97 entries, INV/SEC/SRV/DSC/FIX) are added during implementation as a built-in, read-only library (§8). |
| **Shared pieces** | The undo bar, *Recently deleted* and the colour swatches become shared views, used by Notes, Scripts and – new – the connection tree (§4.9). |

### 1.1 Principles

1. **Scripts are as safe as passwords at rest.** They live in the encrypted database, like notes (notes-view-design.md
   §1.1). Script text, names and output never go to `DiagLog`, telemetry, crash reports or temp files on this PC.
2. **Running is always a deliberate act on one device.** Nothing runs on its own: no schedules, no runs on save, no
   runs from a note. Every run is confirmed (§6.4), audited (§10) and can be cancelled.
3. **No new rights on the device.** A run uses the PowerShell session that RemoteHub already opens (WinRM or the agent).
   It can't do anything the signed-in account couldn't do in the PowerShell console.
4. **Nothing is left behind on the device.** The run's files and its scheduled task are removed when the run ends,
   fails or is cancelled. A later run removes what an interrupted run left behind (§6.3).
5. **Built like Notes and the connection tree.** Panes, autosave, *Recently deleted* and policy work as in Notes;
   favourites and colours work as in the connection tree. A user who knows them doesn't need to learn anything new.
6. **Only what an admin declared ready runs.** Typing never changes what runs on devices: autosave writes a draft, and
   devices get the latest version that was marked *Ready to deploy* (§4.10).

## 2. Non-goals (v1)

- **Running SQL scripts.** They are documentation and a library to copy from. Running SQL would need a connection to
  a database server with its own credentials, which is a separate design.
- **Running on several devices at once** (a folder, a selection, waves). V1 runs on one connection. The catalog's R2
  scripts must never go to the whole fleet in one step (§13 of the catalog), so a multi-device run needs rings, stop
  conditions and approval, which is a separate design.
- **Schedules, sensors and compliance reports.** RemoteHub isn't a management platform. The catalog's sensor-style
  scripts can still be run by hand.
- **Interactive scripts.** Runs are non-interactive (`-NonInteractive`). `Read-Host`, credential prompts and
  `-Confirm` prompts fail instead of hanging (the catalog's §4 *UI and prompts*).
- **Signing scripts.** Runs use `-ExecutionPolicy Bypass` (§6.2). Neither the library nor the user's own scripts are
  signed. Where a GPO enforces an execution policy, the run fails with a clear message and isn't worked around.
- **Sharing scripts with other users, and sync.** These wait for the server and device sync, as for notes.
- **Running from a note's script block.** Notes stay documentation only (notes-view-design.md §2). A script block can
  be saved as a new script (§4.7).

## 3. The view rail

- A new entry, after Notes: `RailView("Scripts", SymbolRegular.Code24, "Scripts", "Ctrl+3")` in
  `MainWindow.RailViews`. It is gated by the policy `AllowScripts` (§9) in the same way as Notes.
- Switching to Scripts never touches a session (notes-view-design.md §1.1).
- `LastView` can be `Scripts`. `ViewFor` falls back to Remote Control while the policy blocks the view.

## 4. The Scripts view

### 4.1 Layout

The view is three cards, like Notes: the same `Card`, `GridSplitter` and `EmptyHint` structure, with widths saved in
`UiState` (`ScriptsFoldersWidth`, `ScriptsListWidth`).

```
┌ Folders ──────────┐┌ [+ New script ▾]   [⇅] ┐┌ Get-PendingReboot                            ● ────────┐
│ ▾ Favourites      ││ 🔍 Search scripts (Ctrl+F)││ PowerShell · Runs as: System ▾ · R0 · Both          │
│   ↗ Disk capacity ││ All colours ▾            ││ ┌─────────────────────────────────────────────────┐ │
│ ⏱ Recently run    ││ ● Get-PendingReboot      ││ │ 1 <#                                            │ │
│ ▾ My scripts      ││   System · INV-05        ││ │ 2 .SYNOPSIS                                     │ │
│   ▸ Inventory  ●  ││ ● Disk capacity          ││ │ 3   Returns whether a restart is pending.       │ │
│   ▸ Fixes      ●  ││   System · INV-07        ││ │ ...                                             │ │
│ ▾ Library 🔒      ││ ● User environment       ││ └─────────────────────────────────────────────────┘ │
│   ▸ INV           ││   User · INV-16          ││ [▶ Run on…]  [⧉ Copy]  [⋯]                         │
│ + New folder      ││                          ││ Draft saved · changes since version 3               │
│                   ││                          ││ [✓ Ready to deploy]  [⇆ Compare]  [Discard draft]   │
│ 🗑 Recently deleted││                          ││                                                     │
└───────────────────┘└──────────────────────────┘└─────────────────────────────────────────────────────┘
```

### 4.2 Folders

- **Any depth of folders**, unlike Notes' two levels (notebooks and sections). Script libraries are sorted by area,
  then topic, and the catalog alone has five areas with up to 24 entries each. The tree works like the connection tree:
  drag a folder onto a folder to move it, drag scripts onto a folder, Del deletes, F2 renames.
- **Colours work as for connections** (README *Icons & colours*): a folder or script takes a colour from the shared
  swatches (§4.9) – the palette or a custom `#RRGGBB` – or inherits its folder's colour when it has none. Folders are
  framed in their colour like folders in the connection tree.
- **Favourites work as for connections** (0.21.0, `Models/Favorites.cs`): a **Favourites** group at the top of the
  folder tree holds **links** to pinned scripts. The script stays in its folder.
  - Pin with *Add to favourites* in a script's context menu or by dragging it onto the group. *Remove from favourites*
    or Del on a link only unpins it. Opening a link opens the script itself.
  - Favourites are kept per user in `UiState.FavoriteScripts` (ids, in pin order), like `FavoriteConnections`. Ids
    that aren't found are kept, not dropped. An id goes when its script is deleted for good.
  - Library scripts can be pinned too (their catalog ID is the favourite's id).
- Under Favourites, **Recently run** lists the last 20 scripts run from this PC, newest first.
- **My scripts** is the root of the user's own folders. **Library** (§8) is read-only: it can be browsed, searched,
  pinned and run, and a script can be copied to *My scripts* (*Duplicate to My scripts*). Library scripts can't be
  edited, moved, recoloured or deleted.
- *New folder* and *Recently deleted* sit at the bottom of the pane, as in Notes.

### 4.3 The script list

- A toolbar with the primary *New script* button and a drop-down for *PowerShell script* (Ctrl+N) and *SQL script*,
  and a sort button (*Name*, *Last changed*, *Last run*), like the connection tree's sort in 0.21.0.
- The search box (`Search scripts (Ctrl+F)`) searches the selected folder and its subfolders. Escape clears it.
- A colour filter (*All colours*, then each colour in use) narrows the list, next to the search as in the tree.
- Each row shows the colour dot, the name and one caption line: the language for SQL, the run context for PowerShell,
  and the catalog ID where there is one (`System · INV-05`). A pinned script shows the favourite mark used in the
  connection tree. A script with an undeployed draft shows *Draft* in its caption; one that was never deployed shows
  *Not deployed yet*.
- **Several scripts can be selected** (Ctrl+click, Shift+click, Ctrl+A) to move, delete, pin or export them together.

### 4.4 The script

- **Header:** the name (edited in place, like a page title) and the colour dot (click opens the swatches).
- **Properties line:** the language (*PowerShell*, or the SQL dialect for SQL scripts), and for PowerShell:
  - **Runs as:** *System*, *User*, *Both* or *As me* (§6.1). The default for a new script is *System*.
  - **Risk:** *R0 Read-only*, *R1 Reversible change* or *R2 Disruptive* (the catalog's §2 classes). The default is R1.
    It decides how a run is confirmed (§6.4).
  - **OS:** *Client*, *Server* or *Both* (information only – RemoteHub warns on the wrong OS, it doesn't refuse).
  - **Timeout:** 10 minutes by default, 1 to 120.
- **Description:** one plain text field under the header. For a PowerShell script it can be taken from the
  comment-based help (`.SYNOPSIS`) with *Use the script's synopsis*.
- **Editor** (§5), then the action bar: *Run on…* (PowerShell only, §6.5), *Copy*, and a ⋯ menu with *Save as
  file…*, *Duplicate*, *Move to…*, *Add to favourites*, *Versions…* (§4.10), *Run history* (§6.6) and *Delete*.
- **Autosave** writes the **draft** a second after typing stops and when the script, folder or view changes, as for
  notes (notes-view-design.md §6.1). The status line says *Draft saved*, or the error and a retry, and under it the
  version bar of §4.10: *Ready to deploy*, *Compare* and *Discard draft*.

### 4.5 SQL scripts

- The dialect is one of the SQL entries of notes-view-design.md §4.5.5a: SQL (standard), T-SQL (the default),
  PostgreSQL, MySQL / MariaDB, Oracle PL/SQL, SQLite. It only decides the colouring and the keyword list (§5.4).
- There is no *Run on…* and no *Runs as* for SQL scripts, and they don't appear in the run window (§6.5).

### 4.6 Deleting and undo

As in Notes, through the shared pieces (§4.9): a deleted script or folder goes to *Recently deleted* for 30 days, the
undo bar offers *Undo* for eight seconds, and *Recently deleted* restores or deletes for good.

### 4.7 Import and export

Many scripts can be moved in and out at once, in two formats:

| Format | What it holds | For |
|---|---|---|
| **Files** – `.ps1` and `.sql` in a folder tree | One file per script, subfolders for folders. The settings go into a header in the file: the `.NOTES` block of §8.2 for PowerShell, a `-- RemoteHub: …` comment line for SQL. | Git repositories, editors, other tools |
| **Script package** – `.rhscripts` | Folders, scripts, settings, colours, descriptions, the draft and all kept versions with their notes and dates (§4.10). Encrypted with a password like `.rhbackup` (same code, same format of key derivation), unless *Protect with a password* is turned off. | Moving scripts to another PC or admin, keeping the history |

**Export**

- *Export…* acts on what is selected: one or more scripts in the list, a folder (with its subfolders) from the
  folder's menu, or *Export all scripts…* from *My scripts*. Library scripts can be exported too.
- The dialog asks for the format and, for files, **which text** to write: the **deployed version** (the default; a
  script that was never deployed is written from its draft and listed as such) or the **draft**.
- It says what isn't protected: files are plain text, and a package without a password isn't encrypted (as Notes'
  export does).
- Audit: `scripts.exported` with the number of scripts and the format – no names.

**Import**

- *Import…* in a folder's menu, or dropping onto a folder: any number of `.ps1` and `.sql` files, whole folders
  (with subfolders, which become folders), or a `.rhscripts` package. Other files are skipped and counted.
- A **preview** lists what will come in, as a tree with check boxes, and what each script's settings are. Scripts
  whose name already exists in the target folder show a choice: *Keep both* (the new one gets "(2)"), *Replace* (the
  imported text becomes a **new draft** of the existing script, so nothing is lost: its versions stay and can be
  compared), or *Skip*. One choice can be applied to all.
- **Imported text arrives as a draft**, never as a deployed version: plain files come from outside, so an admin
  reviews them and marks them *Ready to deploy*. A package keeps its version history for comparison, and its latest
  deployed version becomes the draft. *Import as deployed* (off by default) is offered for packages only, where the
  versions were deployed by an admin before.
- **Limits** (the input is untrusted): 1 MB per script, 5,000 scripts and 50 MB of text per import, folder depth 20.
  Package entries are checked before anything is written: no absolute paths, no `..`, no names leaving the package,
  sizes taken from the content, not the ZIP header, and the total uncompressed size limited. A package that fails a
  check is refused as a whole, with what failed.
- **Encoding:** files with a BOM are read as UTF-8 or UTF-16; others as UTF-8, and as Windows-1252 if that isn't
  valid UTF-8 (what Windows PowerShell 5.1 does with BOM-less files).
- The headers fill in the run context, risk, OS and timeout. Without a header, PowerShell scripts get the defaults
  (§4.4).
- Audit: `scripts.imported` with the number of scripts and the format.
- **From a note:** a PowerShell or SQL script block's ⋯ menu gets *Save as script…*, which creates a script in the
  chosen folder. The script block still can't run (notes-view-design.md §4.5.5).
- **Backup:** `.rhbackup` gains the folders and scripts with their drafts and versions (not the run history's output,
  §6.6). Older backups still import.

### 4.8 Accessibility and keyboard

The panes, tree, list and editor have automation names. Ctrl+N new script, Ctrl+F search, F2 rename, Del delete,
F5 *Run on…* (PowerShell only), Ctrl+Shift+D *Ready to deploy*, Ctrl+Shift+C *Compare*. Each key is shown in its
tooltip.

### 4.9 How the view is built, and the shared pieces

The UI follows the Notes view (the reference), the connection tree (favourites, colours) and the link picker:

| Role | Built with | Settled by |
|---|---|---|
| View | `ScriptsView : Grid` in `Views/ScriptsView.cs`, built in code with a `ScriptsHost` record like `NotesHost` | Notes, the named reference |
| Panes | `Card` (radius 10, `SidebarBackgroundBrush`, `ChromeStrokeBrush`), `GridSplitter` with widths in `UiState`, `EmptyHint` in `TextFillColorSecondaryBrush` | Notes |
| Pane headers | `TextBlock` with the `SectionHeader` style | Notes |
| Primary action | `Wpf.Ui.Controls.Button` with `ControlAppearance.Primary` and a `SymbolIcon` | Notes (*Add page*) |
| Small tools | `RemoteToolChrome.Tool(icon, tip, action)` | Notes, and the shared helper |
| Search | `Wpf.Ui.Controls.TextBox` with `PlaceholderText`, `ClearButtonEnabled` and the `Search24` icon, a 250 ms timer | Notes |
| Favourites | A group of links at the top of the tree, *Add to favourites* with `Star24`, drag to pin | The connection tree (0.21.0) |
| Colours | `ColorTags.All`, `ColorTags.Brush`, inherited from the folder, folder frames | The connection tree |
| Editor | AvalonEdit `TextEditor` with `ScriptHighlighting` and `PsSyntax`, as in `ScriptBlock` | The shared script colouring |
| Run window | `FluentWindow` with `TitleBar`, `ChromeBackgroundBrush`, the search `TextBox`, buttons at the bottom right | `LinkPickerWindow` |
| Menu entries | *Run script…* in *Tools*, with `SymbolRegular.Play24`, beside *PowerShell* | The connection and session menus |

**Shared pieces** – moved out of `NotesView`, `NotesDialogs` and the connection editor into `Views/`, and used by
Notes (unchanged behaviour), Scripts and the connection tree:

| Shared view | From | Used by |
|---|---|---|
| `UndoBar` – the message with *Undo* that hides after eight seconds | `NotesView.BuildUndoBar` | Notes, Scripts, connection tree |
| `RecentlyDeletedWindow` – made generic over a source (list, restore, delete for good, 30 days) | `NotesDialogs.RecentlyDeletedWindow` | Notes, Scripts, connection tree |
| `ColorSwatches` – palette, *None*, custom `#RRGGBB` | `EditorWindow.xaml` *Appearance* and `EditorViewModel.ColorSwatch` | Connection editor, Notes sections, Scripts |

**Recently deleted for connections** (new, so the tree works like Notes and Scripts):

- Deleting a connection or folder from the tree moves it, with everything inside it, to *Recently deleted* for 30
  days and shows the undo bar. The confirmation that asks before a delete today stays as it is. *Delete these…* (the
  clean-up filter) goes the same way.
- *Recently deleted* is opened from the tree's *More* menu and from the sidebar's bottom bar. Restoring puts the item
  back in its folder, or at the top level if the folder is gone, with its saved credentials and its favourite pin.
- Storage: `nodes` gets `deleted_utc` (schema 3, §7.1). A deleted node keeps its record and its `secrets` rows until
  it is deleted for good, then both are removed with `secure_delete` on.
- **Shared-folder mode:** the connections live in `RemoteHub.settings`, which other PCs read. A deleted item leaves the
  shared file at once, and its copy is kept in this PC's database, so only the person who deleted it can restore it.
- **A tree IT provides read-only** has no delete and so no *Recently deleted*.
- Audit: `node.deleted`, `node.restored`, `node.purged` with the node id.

### 4.10 Drafts, versions and comparing

**Draft and versions.** A PowerShell or SQL script has one **draft** – what the editor shows, saved automatically –
and up to **10 versions**, numbered 1, 2, 3 … A version is a frozen copy of everything that decides what runs: the
text, *Runs as*, risk, OS, timeout and description. The name, colour, folder and favourite pin aren't versioned: they
organise, they don't change what runs.

| State | Shown as | What runs on a device |
|---|---|---|
| New script, never deployed | *Not deployed yet* | Nothing: it isn't offered in the run window (§6.5) |
| Draft equal to the latest version | *Version 4 · deployed 20 Oct 2026* | Version 4 |
| Draft with changes | *Draft saved · changes since version 4* | Version 4, until the draft is deployed |

**Ready to deploy** (the primary button of the version bar, Ctrl+Shift+D):

1. Opens a dialog with the comparison of the draft against the latest version (below), so the admin sees exactly what
   they deploy, and an optional **note** ("Also checks the WU policy key"), up to 500 characters.
2. A PowerShell script with syntax errors (§5.3) can't be deployed: the dialog names the first error and its line.
   The editor's secret warning (§5.5), if any, is repeated here.
3. *Deploy* stores the draft as the next version, with the time, the note and the Windows account of the admin. The
   audit log gets `script.deployed` with the script id, the version number and the SHA-256 of the text.
4. When this makes 11 versions, the oldest is removed for good (`secure_delete`). Run history keeps its own hash of
   what ran, so a removed version still shows as "version 2 (no longer kept)".

**Discard draft** puts the draft back to the latest version, after a question. **Restore** (in the version list)
copies an older version into the draft – it is deployed only through *Ready to deploy*, as a new version, so the
history only ever grows forward, as with `git revert`.

**Versions…** (in the ⋯ menu) lists the versions, newest first, with number, date, admin, note and how often each
ran, and the draft at the top if it has changes. From there: *Compare with…*, *Restore to draft*, *Export this
version…*.

**Library scripts** have no drafts. Their version is the RemoteHub release they came with. *Duplicate to My scripts*
creates a script whose version 1 is the library text, already deployed (the library is part of the signed app).
Imports arrive as drafts (§4.7).

**Compare** (Ctrl+Shift+C, the version bar, the version list, the *Ready to deploy* dialog) opens a comparison window,
like a Git diff:

```
┌ Compare – Get-PendingReboot ─────────────────────────────────────────────────────────────────┐
│ [Version 1 · 02 Oct ▾]  ⇄  [Version 4 · 20 Oct ▾]     Side by side | Inline     ↑ ↓  3 changes │
│ Runs as: System → Both     Timeout: 10 → 15 min                                               │
├──────────────────────────────────────────┬───────────────────────────────────────────────────┤
│ 12   $reasons = @()                      │ 12   $reasons = @()                               │
│ 13 - if (Test-Path $cbs) {               │ 13 + if (Test-Path -LiteralPath $cbs) {           │
│      ⋯ 24 unchanged lines                │      ⋯ 24 unchanged lines                         │
│                                          │ 38 + $reasons += 'Rename pending'                 │
└──────────────────────────────────────────┴───────────────────────────────────────────────────┘
```

- **Any two of** the kept versions and the draft can be compared. It opens with the **oldest kept version on the
  left and the latest on the right**; from the editor with a changed draft, it opens with the latest version against
  the draft. ⇄ swaps the sides.
- **Side by side** (the default) or **Inline** (one column, like `git diff`), remembered in `UiState`.
- Removed lines are marked on the left, added lines on the right, and a changed line shows the changed words inside
  it. Unchanged stretches longer than 6 lines fold to "⋯ N unchanged lines" and open on click. Both sides keep the
  syntax colouring and line numbers, and scroll together.
- ↑ ↓ (F7, Shift+F7) jump between changes; the count says how many there are. Changes to *Runs as*, risk, OS,
  timeout and description are listed above the text.
- The marks use theme brushes added for this (`DiffAddedBrush`, `DiffRemovedBrush`, and stronger variants for the
  changed words), each with a dark and a light value, and a + / − in the margin so they don't depend on colour.
- *Copy* on either side copies that version's text. The window is read-only.
- **Diff code:** our own line diff (Myers' algorithm) with a word diff inside changed lines, in `Services/TextDiff.cs`,
  a few hundred lines with its own tests. No third-party library. Scripts are at most 1 MB, so the diff runs on a
  background thread and shows "Comparing…" only if it takes longer than 200 ms.

## 5. The editor

### 5.1 Technology

The AvalonEdit `TextEditor` already used for script blocks (AvalonEdit 6.3.1.120, already in `THIRD-PARTY-NOTICES.md`),
full height, with line numbers, `Cascadia Mono` then `Consolas`, Tab indents, bracket matching, folding of `{ }` blocks
and regions, Ctrl+F find, Ctrl+H replace and Ctrl+G go to line. No new third-party code.

### 5.2 Colouring

- **PowerShell:** `PsSyntax` (PowerShell's own tokenizer), with the dark and light palettes of the script blocks.
- **SQL:** the TSQL and SQL definitions `ScriptHighlighting` already loads, recoloured for the theme.
- The palette follows the theme when it changes (`ScriptHighlighting.Changed`).

### 5.3 IntelliSense for PowerShell

- **Completion** (Ctrl+Space, and as you type after `-`, `$`, `.`, `::` and a verb-noun prefix) comes from PowerShell
  itself: `CommandCompletion.CompleteInput` of the PowerShell SDK, run in a **local runspace** in the background. It
  completes cmdlets, functions, parameters, parameter values, variables, types, members and paths. It uses the same
  popup as the PowerShell console (`PsConsoleView`'s completion list), so the two look alike.
- **Signature help:** after a cmdlet name, a tooltip shows its parameter sets (`Get-Command -Syntax`).
- **Errors:** `Parser.ParseInput` runs a second after typing stops. Syntax errors are underlined, and listed in the
  status line ("2 syntax errors – line 14"). A run asks before sending a script with syntax errors.
- **Safety of the local runspace:** completion parses and never runs the script. PowerShell may load a module to
  complete its commands, which is its normal behaviour. The runspace is created with `InitialSessionState.CreateDefault2()`,
  no profile, and is shared by all editors.
- **Known limit:** the local runspace is PowerShell 7.6, while scripts run on Windows PowerShell 5.1 on the device
  (§6.2). Completion can offer commands or parameters that 5.1 or the device doesn't have. Later, an editor can
  complete against a connection (§11, phase 4), the way the PowerShell console does with the remote `TabExpansion2`.
- **Snippets:** `function` (an advanced function with `[CmdletBinding(SupportsShouldProcess)]`), `try`, `foreach`,
  `param`, the catalog's script skeleton (§8.3). The snippet list is our own code.

### 5.4 IntelliSense for SQL

Keyword and function completion for the chosen dialect, from a static list (our own), and snippets (`SELECT … FROM`,
`BEGIN TRAN … COMMIT`). There is no schema completion: SQL scripts aren't connected to a database (§2).

### 5.5 Secrets in scripts

Scripts must never hold secrets (the catalog's §3). The editor warns, without blocking, when a line looks like a secret:
`ConvertTo-SecureString '…' -AsPlainText`, `-Password '…'`, `$password = '…'`, a long base64 or hex literal next to
`key`, `secret` or `token`. The warning says to use LAPS, gMSA or SecretManagement instead. This is a hint, not
protection: the script still saves.

## 6. Running a script

### 6.1 Run contexts

| Context | Runs as | Needs |
|---|---|---|
| **System** | `NT AUTHORITY\SYSTEM` (session 0, no desktop, machine-wide) | An administrator account on the connection |
| **User** | The user signed in on the device, in their session (their HKCU, profile, mapped drives, user certificates) | An administrator account on the connection, and a user signed in on the device |
| **Both** | System first, then User, as two separate runs with two results | Both of the above. If nobody is signed in, the System part still runs and the User part reports "No user is signed in on the device". |
| **As me** | The account RemoteHub signs in with – the connection's credentials – with its full token | The PowerShell right on the device (no other) |

- **Which user** a *User* run uses: the **console user** – the user of the session at the device's console. On a
  device with one signed-in user who isn't at the console (a client used over RDP), that user counts as the console
  user. The run window lists every other signed-in user too, unticked, and each ticked user is a separate result.
  With nobody to run as, the run window says so before the run starts.
- **As me** is for the catalog's USER entries on servers and Active Directory, which mean "a named admin", not the
  end user (SRV-05, SRV-07, SRV-09, SRV-10, SRV-17, SEC-09, DSC-11, DSC-14). **Second hop:** the session's sign-in
  (WinRM, or the agent's network logon) can't pass the account on to other computers, so an *As me* script that
  reaches another server (a domain controller, a cluster node) fails with access denied. Such scripts are run on that
  server itself. The run window says this for *As me* runs, and the library marks these scripts with `os=Server`.
- The context is stored with the script, and the run window shows it preselected and lets it be changed for this run
  (the catalog has entries like SRV-11 "USER or SYSTEM").

### 6.2 How a run works

A run goes through the **PowerShell session RemoteHub already opens** for the connection (`PsRemoting.Open`): WinRM,
through the jump host if there is one, or the agent's `/v1/powershell` WebSocket. There is **one implementation for
both transports** and no new agent protocol. The PowerShell session runs as the connection's account with its full
token. For *System* and *User*, it creates a scheduled task that runs as SYSTEM or as the signed-in user, and waits
for it. For *As me*, it starts the script as a child process of the session, with no task.

```
RemoteHub (this PC)                       Device (in the remote runspace, as the connection's account)
──────────────────────────────            ─────────────────────────────────────────────────────────────
1. Confirm (§6.4)
2. Open runspace (WinRM or agent)  ─────▶ 3. Remove leftovers of runs older than 24 h (§6.3)
                                          4. Create %ProgramData%\RemoteHub\Runs\<runId>\
                                             ACL: SYSTEM, Administrators; for User also that user;
                                             for As me that account
                                          5. Write script.ps1 (UTF-8 with BOM, so 5.1 reads it right)
                                          6. System / User: register task "RemoteHub run <runId>"
                                               principal: S-1-5-18, or the user with LogonType Interactive
                                             As me: start the same command as a child process
                                             command: 64-bit powershell.exe -NoProfile -NonInteractive
                                                      -ExecutionPolicy Bypass -File script.ps1
                                             a wrapper writes stdout, stderr and the exit code to
                                             output.txt and result.json in the run folder
                                          7. Poll every second until it ends or times out
   Show output as it arrives  ◀─────────  8. Read new output (at most 64 KB a poll)
                                          9. finally: stop the task or process, unregister the task,
                                             delete the folder
10. Store the result (§6.6), audit (§10)
```

- **Engine:** Windows PowerShell 5.1 (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, the 64-bit one,
  so no `SysWOW64` or `Wow6432Node` redirection), which every target has (the catalog's §3 *Compatibility*). A script
  can ask for PowerShell 7 with `#Requires -PSEdition Core`; then `pwsh.exe` is used if the device has it, otherwise
  the run fails with "PowerShell 7 isn't installed on the device".
- **A child process for *As me*,** not the runspace itself: the script's `exit`, `$ErrorActionPreference` and loaded
  modules then don't change the session, and every context gets the same engine, timeout and output handling. Over
  the agent, the child process is inside the session's job object, so it ends with the session.
- **Script text** is written to a file, never put on the command line: a command line shows up in process audit
  events (4688) and in the task's XML, and is limited to 32,767 characters. The file exists only while the run lasts.
- **Execution policy:** `-ExecutionPolicy Bypass` at invocation, as the catalog's §3 allows ("bypass the policy only at
  invocation time through the management tool"). Scripts aren't signed (§2). Where a GPO sets the execution policy
  (`MachinePolicy` or `UserPolicy`), Bypass has no effect and the run fails: RemoteHub says "The device's group policy
  only allows signed scripts". It doesn't try another way around the organisation's policy.
- **Exit codes:** 0 is *Succeeded*, 3010 is *Succeeded – restart needed*, anything else is *Failed (exit code N)*. A
  script that throws a terminating error is *Failed*, with the error.
- **Output limit:** 1 MB per result. After that, RemoteHub stops reading and says "Output cut off after 1 MB". The
  script keeps running until it ends or times out.
- **Timeout:** the script's timeout (§4.4). When it passes, the task or process is stopped and the result is *Timed
  out*.
- **Cancel:** the run window's *Cancel* stops the task or process, removes it and the folder, and records *Cancelled*.
- **Connection lost** during a run: RemoteHub reports *Connection lost – the script may still be running on the
  device*. A task keeps its own time limit (the timeout plus 5 minutes, `ExecutionTimeLimit`), so it can't run
  forever, and the next run cleans up (§6.3). An *As me* process ends with the WinRM or agent session.
- **Account without admin rights:** registering a task as SYSTEM or another user fails. RemoteHub says "Running as
  System or User needs an administrator account on the device". *As me* still works.

### 6.3 Cleanup and what stays on the device

- The run folder and the task are removed in a `finally` on the device, on every path.
- If the connection is lost, the next run on that device removes run folders and `RemoteHub run *` tasks older than
  24 hours (older than the longest possible run).
- What the device keeps, by its own settings: Task Scheduler's history, PowerShell script block logging (event 4104)
  and transcription, if the organisation turned them on. This is wanted for its SIEM, and it is why scripts must never
  contain secrets (§5.5). The task's name holds only the run id.

### 6.4 Confirming a run

- **What runs is the latest deployed version** (§4.10), with that version's *Runs as*, risk, OS and timeout – never
  the draft, whatever the editor shows.
- Every run shows a summary before it starts: the script and its version ("version 4, deployed 20 Oct 2026"), the
  device, the context (and for *User*, which users), the risk class and the transport. If the script has an undeployed
  draft, the summary says "The draft has changes that aren't deployed – version 4 runs". *Run* starts it.
- **Testing a draft:** the editor's ⋯ menu has *Test draft on…* for PowerShell scripts with a draft. It runs the
  draft once, on one device, after the same confirmation with "This is a draft, not a deployed version" at the top.
  Test runs are marked *Draft* in the run window, the run history and the audit log (`script.run` with version
  `draft` and the hash). They are never offered in the run window from the connection menus. The policy
  `AllowScriptDraftRun` (§9) turns them off.
- **R2** scripts also need the device name typed in, and the summary says "This script can restart the device, stop
  services or delete data". This mirrors the catalog's R2 controls (§2 there).
- A script whose OS scope doesn't match the device's (when RemoteHub knows the OS) gets a warning in the summary, not
  a block.
- A deployed version can't have syntax errors (§4.10). A draft under test that has them asks "Run anyway?".

### 6.5 Where *Run* is offered

- **Connection tree:** `CmRunScript` *Run script…* in the *Tools* submenu, after *PowerShell*. It is visible under the
  same condition as `CmPs`: PowerShell remoting or the agent is available (`PsState(c) == Available`) and
  `Features.IsAllowed(RemoteTool.PowerShell, viaAgent)`, plus `Features.IsAllowed(Feature.ScriptRun)` (§9). On a
  favourite link it acts on the connection, as the other entries do.
- **Session tab menu:** the same entry in its *Tools* submenu (`MainWindow.xaml.cs`, beside
  `Tool(RemoteTool.PowerShell, …)`), so a script can be run while working in an RDP or agent desktop session.
- **Scripts view:** *Run on…* (F5) opens a connection picker (a `LinkPickerWindow` with connections only, single
  selection, favourite connections first), which lists only connections where PowerShell is available, then goes
  straight to the confirmation.

The **run window** (`RunScriptWindow : FluentWindow`, resizable, 720 × 560, remembers its size) has two pages:

1. **Pick:** the device name in the title ("Run script on SRV-UEM01"), the search box ("Search scripts"), filter chips
   *Favourites* and *Recently run*, a colour drop-down and a folder drop-down, and the list. It lists **PowerShell
   scripts with a deployed version only**, from *My scripts* and the library. Each row has the colour dot, the name,
   the favourite mark, the folder path, the version, the context and the risk; its context menu has *Add to favourites* / *Remove from favourites*. Enter
   or double-click chooses, and the confirmation (§6.4) follows on the same page.
2. **Run:** one tab per result (System, User, As me, or one per user), each with its state (*Running…* with the
   elapsed time, *Succeeded*, *Failed*, *Timed out*, *Cancelled*), the exit code and the output in a read-only editor
   (monospace, plain text, search with Ctrl+F). Buttons: *Cancel* while it runs, then *Copy output*, *Save output…*,
   *Run again* and *Close*.

The window isn't modal: several runs on several devices can be open at once, and closing a running window asks
whether to cancel the run.

### 6.6 Run history

- Each run stores, in the encrypted database: the script id, the version number (or *draft*) and a SHA-256 of the text
  that ran, the connection id, the context and user, the start, duration, state and exit code, and the output (up to
  1 MB).
- Output can contain personal data and machine details, so it is kept for the last 10 runs of each script, at most
  30 days, and a setting can turn off storing output (*Settings → Scripts → Keep the output of runs*).
- *Run history* (in a script's ⋯ menu, and for a connection in *More → Audit log…*) lists the runs with their version.
  *Compare with latest* opens the comparison (§4.10) of the version that ran against the latest one, while that
  version is still kept.
- Run history isn't in backups.

## 7. Data model

### 7.1 Schema (version 3)

A new `LocalDb.Migrate` step to `user_version = 3`, with a backup first, as for version 2. Times are Unix
milliseconds (`INTEGER`) and the sync columns are `NoteColumns`, as in schema 2. The FTS table keeps its own copy of
the text and is written by `ScriptStore` in the same transaction (triggers can't write to FTS5 with `trusted_schema`
off, as for `notes_fts`).

```sql
ALTER TABLE nodes ADD COLUMN deleted_utc INTEGER;        -- Recently deleted for connections (§4.9)

CREATE TABLE script_folders (id TEXT PRIMARY KEY, parent_id TEXT, name TEXT NOT NULL, color TEXT,
                             sort INTEGER NOT NULL, deleted_utc INTEGER, {NoteColumns});
CREATE INDEX ix_script_folders ON script_folders(parent_id, sort);

CREATE TABLE scripts (rid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, folder_id TEXT NOT NULL, name TEXT NOT NULL,
                      language TEXT NOT NULL,       -- 'powershell' or a SQL fence id (notes-view-design.md §4.5.5a)
                      -- The draft: what the editor shows and autosave writes.
                      body TEXT NOT NULL, description TEXT NOT NULL,
                      run_context TEXT,             -- 'System' | 'User' | 'Both' | 'AsMe'; NULL for SQL
                      risk TEXT, os_scope TEXT, timeout_sec INTEGER,
                      deployed_version INTEGER,     -- the latest version number; NULL = never deployed
                      color TEXT,
                      library_id TEXT,              -- catalog ID (INV-05) when copied from the library
                      sort INTEGER NOT NULL, created_utc INTEGER NOT NULL, deleted_utc INTEGER, {NoteColumns});
CREATE INDEX ix_scripts_folder ON scripts(folder_id, sort);

-- Deployed versions, at most 10 per script (the oldest is removed when an 11th is deployed).
CREATE TABLE script_versions (script_id TEXT NOT NULL, version INTEGER NOT NULL,
                              body TEXT NOT NULL, description TEXT NOT NULL, run_context TEXT, risk TEXT,
                              os_scope TEXT, timeout_sec INTEGER, body_sha256 BLOB NOT NULL,
                              note TEXT NOT NULL, deployed_by TEXT NOT NULL, deployed_utc INTEGER NOT NULL,
                              {NoteColumns}, PRIMARY KEY (script_id, version));

CREATE TABLE script_runs (id TEXT PRIMARY KEY, script_id TEXT NOT NULL,
                          version INTEGER,                -- NULL = a draft under test (§6.4)
                          body_sha256 BLOB NOT NULL,
                          node_id TEXT NOT NULL, context TEXT NOT NULL, run_user TEXT, transport TEXT NOT NULL,
                          started_utc INTEGER NOT NULL, duration_ms INTEGER, state TEXT NOT NULL, exit_code INTEGER,
                          output TEXT);
CREATE INDEX ix_script_runs ON script_runs(script_id, started_utc);
CREATE INDEX ix_script_runs_node ON script_runs(node_id, started_utc);

CREATE VIRTUAL TABLE scripts_fts USING fts5(name, description, body, tokenize = 'unicode61 remove_diacritics 2');
```

- Version numbers only grow: after versions 1–12 the kept ones are 3–12, and the next is 13. A version's text and
  settings never change once written.
- Searching (`scripts_fts`) covers the draft. Older versions aren't searched.
- Favourites aren't in the database: they are `UiState.FavoriteScripts`, as connection favourites are (§4.2).
- Library scripts have no rows: they come from the app (§8). Their colour is their area's, fixed.
- The colour is a `ColorTags` id or `#RRGGBB`, as for connections.

### 7.2 Code

- `Data/ScriptStore.cs` – folders, scripts, drafts and versions, search, deleted items, run history (like `NoteStore`).
- `Services/TextDiff.cs` – the line and word diff (§4.10); `Views/CompareWindow.cs` – the comparison window.
- `Services/ScriptTransfer.cs` – import and export of files and `.rhscripts` packages (§4.7), with the checks.
- `Services/ScriptLibrary.cs` – loads the built-in library (§8).
- `Services/ScriptRunner.cs` – the run (§6.2), on a background thread, reporting to the UI through the dispatcher.
  The device-side part is one PowerShell script (`Assets/Scripts/RunScript.ps1`, our own code, ASCII-only like the lab
  scripts, because the device runs it under 5.1), with the script text, timeout and principal passed as parameters
  (`AddParameter`), never by building a string.
- `Services/PsCompletion.cs` – the local runspace for completion and parsing (§5.3).
- `Views/ScriptsView.cs`, `Views/ScriptEditor.cs`, `Views/RunScriptWindow.cs`, and the shared `Views/UndoBar.cs`,
  `Views/RecentlyDeletedWindow.cs`, `Views/ColorSwatches.cs` (§4.9).
- `Models/Favorites.cs` gets a script variant of `Resolve`/`Forget`, or is made generic over the id.

### 7.3 Downgrade

Not GA: older RemoteHub versions don't need to read schema 3 (as notes-view-design.md §5.3).

## 8. The script library

### 8.1 What it is

The scripts of the *PowerShell Script Catalog for Windows Administrators* (97 entries in five areas), written during
implementation and shipped with RemoteHub as `.ps1` files embedded in the app. The library is read-only in the app.
It is updated with each release, and the user's copies (*Duplicate to My scripts*) are never changed by an update.

The library tree follows the catalog: **Inventory and reporting (INV)**, **Security and compliance (SEC)**, **Server
roles and features (SRV)**, **Desired State Configuration (DSC)**, **Troubleshooting and remediation (FIX)**, plus a
**Starter set** folder with the catalog's §12 entries (the same scripts, not copies).

### 8.2 From the catalog to a script

Every library script starts with a RemoteHub header, read on import and for the library:

```powershell
<#
.SYNOPSIS
    Returns whether a restart is pending, and why.
.NOTES
    RemoteHub: id=INV-05; context=System; os=Both; risk=R0
#>
```

| Catalog tag | RemoteHub |
|---|---|
| SYSTEM | `context=System` |
| USER on clients (the signed-in end user) | `context=User` |
| USER on servers and Active Directory (a named admin: SRV-05, SRV-07, SRV-09, SRV-10, SRV-17, SEC-09, DSC-11, DSC-14) | `context=AsMe` |
| SYSTEM and USER (DSC-03, DSC-12, FIX-07) | `context=Both` |
| USER or SYSTEM (SRV-08, SRV-11, SRV-12, SRV-14, SRV-18–20) | The usual one as the default, and the run window lets it be changed (§6.1) |
| Client / Server / Both | `os=` |
| R0 / R1 / R2, and mixed classes (R0 / R1) | `risk=`, with the highest class of the entry, so the confirmation is never weaker than the riskiest part |

**R2 entries** are in the library with WhatIf as their default: they report the plan unless run with `-Apply`, which
the confirmation asks for separately. This keeps the catalog's rule that "unattended execution of R2 scripts is out of
policy".

### 8.3 Standards every library script meets

The catalog's §3 standards, checked by a test over the library (§12), except signing (§2):

- An advanced script with `[CmdletBinding(SupportsShouldProcess)]` for R1 and R2, idempotent, `$ErrorActionPreference
  = 'Stop'`, exit codes 0, 1 and 3010.
- Windows PowerShell 5.1 syntax (no `??`, `?.`, ternaries or `&&`), CIM rather than WMI cmdlets, no `Win32_Product`,
  no parsing of localised tool output where the catalog says so.
- No secrets. Output redacts personal data as `[REDACTED]` where the catalog says so. "Data unavailable" rather than
  a guess.
- ASCII-only, saved UTF-8 with a BOM.

## 9. Policy

New console features in `SOFTWARE\Policies\RemoteHub`, on by default (admx-design.md §5), in `PolicyCatalog`, the
ADMX/ADML and *Settings → Policies*, under *RemoteHub Console → Features → Scripts*:

| Value | Blocks |
|---|---|
| `AllowScripts` | The Scripts view: no rail entry, Ctrl+3 does nothing, no *Run script…*. Scripts already stored are kept. |
| `AllowScriptRun` | Running scripts: no *Run script…* or *Run on…*. The view and the editor stay. |
| `AllowScriptRunSystem` | The *System* context (and so *Both*). |
| `AllowScriptRunUser` | The *User* context (and so *Both*). |
| `AllowScriptRunAsMe` | The *As me* context. |
| `AllowScriptDraftRun` | *Test draft on…*: only deployed versions can run (§6.4). |
| `AllowScriptLibrary` | The built-in library. The organisation's own scripts are still allowed. |
| `AllowScriptImport` | Importing files, folders and packages (§4.7). |
| `AllowScriptExport` | Exporting files and packages, and *Save as file…* (§4.7). |

- `AllowScriptRun` also depends on the transport's PowerShell switch: `AllowAgentPowerShell` for the agent and
  `AllowRemotingPowerShell` for WinRM. A run is the PowerShell tool used in another way.
- **On the device**, the agent's policy decides as it does today: `PowerShellEnabled` and the `PowerShell` right.
  With the agent, a run is a PowerShell session, so a device that blocks PowerShell blocks runs too, whatever this
  PC's policy says. A separate device-side `ScriptsEnabled` would need the native endpoint (§11, later).
- *Recently deleted* for connections needs no policy: it changes how deleting works, not what is allowed.

## 10. Security and privacy

| Threat | Measure |
|---|---|
| Scripts or output on a stolen disk | The encrypted database, like notes. The FTS index is inside it too. |
| Script text, names, host names or output in `DiagLog` | Never. Only ids, counts, states and durations ("Scripts: run finished, state Failed, 2.1 s"). Exceptions from the run are logged with `ex.GetType().Name` where the message can quote the script (parser errors). |
| Audit log | `script.added`, `script.deleted`, `script.restored` with the script id. `script.deployed` with the script id, the version and the SHA-256 of the text (autosaving a draft isn't audited – it changes nothing that runs). `scripts.imported` and `scripts.exported` with a count and the format. `script.run` with the script id, the version (or `draft`), the SHA-256 of the text, the connection id, the context, the transport, the state and the exit code – never the text or the output. This builds the "script run" event that local-db-design.md §11 plans. |
| A run doing more than the account may | No: the run uses the account's own PowerShell session (§1.1, 3). SYSTEM is reachable only for an administrator, who could create the same task in the console. |
| Script text on the device | In a folder with an ACL for SYSTEM, Administrators and the account the script runs as, for as long as the run lasts. Never on a command line (§6.2). |
| A user tampering with a *User* run | The user can read and change their own run's files while it runs, but the task runs only as them, so they gain nothing. SYSTEM runs use a folder the user can't read or write. |
| Bypassing the organisation's execution policy | `-ExecutionPolicy Bypass` only where no GPO sets the policy. A GPO-set policy wins and the run fails (§6.2). |
| Output with personal data | Kept only for the last 10 runs and 30 days, and can be turned off (§6.6). *Copy output* uses the normal clipboard (not `SecureClipboard`: output isn't a password). |
| Deleted connections with saved passwords | In *Recently deleted* their `secrets` rows stay encrypted as before, and are removed with `secure_delete` when the item is deleted for good or after 30 days (§4.9). |
| A half-finished edit reaching devices | Autosave writes only the draft. Devices get the latest version an admin deployed after seeing the comparison (§4.10). Draft tests are explicit, marked and can be turned off (§6.4). |
| Who changed what | Each version records the Windows account that deployed it, the time and the note, and versions can't be changed afterwards (§7.1). |
| Malicious imported scripts | Imported files are text up to 1 MB and arrive as drafts, so an admin reviews them before they can run. Nothing is run on import, in the editor or by completion (§5.3). |
| Malicious packages | Every entry is checked before anything is written: paths (no absolute paths, no `..`), counts and sizes from the content with a total limit, so a ZIP bomb or a path that leaves the package is refused as a whole (§4.7). The package's JSON is read with size limits and unknown fields ignored. |
| Exported scripts | Files are plain text and the dialog says so. Packages are encrypted with a password by default, like backups. |
| Library integrity | Embedded in the signed app. A test checks every library header and the standards (§8.3). |
| Prompts hanging a run | `-NonInteractive` and the timeout (§6.2). |

## 11. Phases

| Phase | Content | Done when |
|---|---|---|
| 1 | **Shared pieces and the view:** `UndoBar`, `RecentlyDeletedWindow`, `ColorSwatches` moved out and used by Notes and the connection editor; *Recently deleted* for connections; rail entry, schema 3, `ScriptStore`, folders, scripts, colours, favourites, search, the editor with colouring, parse errors and PowerShell completion, drafts and versions with *Ready to deploy*, the comparison window, import and export of files and packages, `AllowScripts`, `AllowScriptImport`, `AllowScriptExport` | The unit and view tests in §12 pass; Notes behaves as before; 5,000 scripts load and search within the local-db-design.md §7 targets; comparing two 1 MB versions takes under a second |
| 2 | **Running:** `ScriptRunner`, the run window, *Run script…* in both menus, *Run on…*, *Test draft on…*, the four contexts, confirmation, cancel, cleanup, run history, audit, the run policies | Lab: a script runs as SYSTEM, as the signed-in user and as the admin over WinRM and over the agent, and leaves no task or folder behind |
| 3 | **Library:** the catalog scripts, the header, the standards test, `AllowScriptLibrary` | Every library script passes the standards test; the starter set runs in the lab on Windows 11 and Server 2025 |
| 4 | Completion against a connection, script parameters as a form in the run window (from the `param()` block) | – |
| Later | A native agent endpoint `/v1/script` (device-side policy `ScriptsEnabled`, `AgentLog` event for runs, no scheduled task), runs on several devices with rings, SQL against a database server, signing | Own design |

## 12. Tests

- **Unit (`tests/RemoteHub.Tests`):** `ScriptStore` (folders at any depth, moves, deletion and restore, search), script
  favourites (pin, unpin, order, unknown ids kept, forgotten on delete for good), colour inheritance, the migration
  from schema 2 to 3 with a backup, *Recently deleted* for connections (restore into a deleted folder, credentials
  restored, purge removes `secrets`, shared-folder mode keeps the copy here), the header parser (every catalog tag in
  §8.2), the run script's parameters (no text is put into a command string; quotes, `$`, backticks and non-ASCII
  survive), output truncation at 1 MB, exit code mapping, the console-user choice, policies and their dependencies,
  and that `DiagLog` and the audit entries never contain a script's text, name or output.
- **Unit – versions:** autosave changes only the draft; *Ready to deploy* writes the next number and refuses syntax
  errors; the 11th version removes the oldest and numbers keep growing; *Discard draft* and *Restore* never change a
  version; a run takes the latest deployed version and its settings, not the draft; run history keeps the version and
  hash after the version is removed.
- **Unit – diff (`TextDiff`):** identical texts, empty texts, only additions, only removals, moved blocks, changed
  words inside a line, CRLF against LF, a trailing newline, non-ASCII, and two 1 MB texts within the time limit.
- **Unit – import and export:** files with and without headers, BOMs and encodings, folders with subfolders, name
  conflicts (keep both, replace as a new draft, skip), a package round trip with versions, notes, colours and
  settings, with and without a password, a wrong password; refused packages – `..` and absolute paths, more than
  the limits, a ZIP whose header understates its size – leave nothing written.
- **View (STA):** the view builds and restores its widths; the shared undo bar, *Recently deleted* and swatches work
  in Notes, Scripts and the connection tree; the run window lists only PowerShell scripts, filters by favourite,
  colour and folder, preselects only the console user, and shows the confirmation for R2 with the typed name; *Run
  script…* is hidden without PowerShell or under the policies; the run window leaves out scripts that were never
  deployed; the version bar shows the three states of §4.10; the comparison window opens oldest against latest,
  switches side by side and inline, folds unchanged lines and jumps between changes; the import preview shows
  conflicts and applies one choice to all.
- **Library:** every library script parses under PowerShell, has a valid header, is ASCII-only with a BOM, and meets
  §8.3 (checked with the AST: `SupportsShouldProcess` for R1/R2, no `Get-WmiObject`, no `Win32_Product`, no 7-only
  syntax).
- **Lab (`lab/tests/S-SCRIPT.Tests.ps1`, a new case in docs/test-lab-cases.md and the test document):** on a VM over
  WinRM and over the agent: a System run prints `nt authority\system`; a User run with a standard user signed in
  prints that user and reads their HKCU; *Both* gives two results; *As me* prints the connection's account; a User run
  with nobody signed in reports it; a run that sleeps past its timeout is stopped; *Cancel* removes the task and the
  folder; after a dropped connection, the next run cleans up; a GPO-set `AllSigned` policy fails with the message from
  §6.2; an account without admin rights gets the message from §6.2 for System and still runs *As me*; with a changed
  draft, a run prints the deployed version's output, and *Test draft on…* prints the draft's.
- **Lab (connections):** delete a connection with saved credentials, restore it from *Recently deleted*, and connect.

## 13. Decided

| Question (v1) | Answer |
|---|---|
| The connection's own account as a context? | Yes: **As me** (§6.1), with the second-hop limit stated. |
| Favourites the same as Remote Control? | Yes, as the connection tree's favourites in 0.21.0: links in a Favourites group, per user in `UiState` (§4.2). |
| *User* runs on multi-session servers | The **console user** only; the others can be ticked (§6.1). |
| Signing scripts | Not in scope: `-ExecutionPolicy Bypass`, no signing, a GPO-set policy is respected (§2, §6.2). |
| Shared pieces | Moved into shared views, and connections get *Recently deleted* and undo too (§4.9). |
| Import and export (v2 review) | Many scripts at once, as files or as a `.rhscripts` package with versions (§4.7). |
| Versioning (v2 review) | Autosave writes a draft; *Ready to deploy* makes a version; 10 versions kept; any two compared like a Git diff, oldest against latest by default (§4.10). |
| Running a draft before it is deployed | Yes: *Test draft on…*, one device at a time, marked as a draft everywhere, and `AllowScriptDraftRun` can turn it off (§6.4). |

## 14. Changes to other documents

- **notes-view-design.md:** §3.8 points here for Scripts (Ctrl+3); §4.5.5 – the script block's ⋯ menu gets *Save as
  script…* (§4.7); §6.5 – *Recently deleted* and the undo bar are shared views.
- **local-db-design.md:** §6 – the tables of §7.1 and `nodes.deleted_utc`; §11 – the "script run" audit event is
  built; §12 – phase 5 points here; §16 – migration to schema 3.
- **admx-design.md:** §5.7 – the policies of §9.
- **agent-spec.md:** a note that runs use the PowerShell session, the scheduled task or a child process (§6.2), and
  that the device's `PowerShellEnabled` and `PowerShell` right govern them.
- **README:** the feature list gets Scripts; *Favourites* and deleting connections mention *Recently deleted*.
- **New copy for review:** "Search scripts (Ctrl+F)", "Run script…", "Run on…", "Runs as: System / User / Both / As
  me", "No user is signed in on the device", "Running as System or User needs an administrator account on the device",
  "The device's group policy only allows signed scripts", "Connection lost – the script may still be running on the
  device", "Output cut off after 1 MB", "PowerShell 7 isn't installed on the device", "Ready to deploy", "Draft saved ·
  changes since version N", "Not deployed yet", "Discard draft", "Compare", "N unchanged lines", "Test draft on…",
  "This is a draft, not a deployed version", "The draft has changes that aren't deployed – version N runs", "Keep
  both", "Replace", "Skip", "Protect with a password".
- **Theme:** `DiffAddedBrush`, `DiffRemovedBrush` and their strong variants, dark and light (§4.10).

## 15. As built (phases 1–4)

What differs from the plan above, and why:

- **Script packages are a JSON envelope, not a ZIP** (§4.7): the same format of key derivation and encryption as a
  backup (PBKDF2-SHA256, AES-256-GCM), the folders and scripts inside as JSON. With no file paths in a package there is
  nothing to traverse; the checks of §4.7 (counts, sizes, depth, loops) run on the JSON before anything is written.
- **"Recently deleted" for connections keeps a sealed record per item** (§4.9), in the table `deleted_nodes`, instead
  of a `deleted_utc` column on `nodes`: the tree is saved as a whole and a row in `nodes` is the live tree. The record
  holds the item with everything below it and its passwords, sealed with the database's secret key and bound to its row.
  It works the same in shared-folder mode. The confirmation before deleting stays; *Undo* and *More → Recently
  deleted…* bring an item back into its folder, or to the top when the folder is gone, with its favourite pins.
- **The shared colour piece is the context menu's *Colour* entry** (`Views/ColorMenu.cs`): Notes sections, script
  folders and scripts, and – new – folders and connections in the tree use it. The connection editor keeps its
  *Appearance* swatches, which also offer *Inherit*.
- **Favourites:** a Favourites group of links at the top of the folder tree, *Add to favourites* / *Remove from
  favourites* in the menus, Del on a link unpins; pinned scripts show a star in the list. Dragging a script onto the
  group isn't built.
- **Editor:** find is AvalonEdit's search panel (Ctrl+F); there is no replace (Ctrl+H) yet. The command syntax shows on
  hover over a command rather than after typing it. Snippets aren't built.
- **Settings:** *Keep the output of runs* is in *Settings → Logs*, next to the audit log, not on a page of its own.
  Turning it off removes the output already kept.
- **Run history:** for a script in its ⋯ menu, for a connection in *More → Run history…*.
- **The folder frame is shared:** the connection tree's `FolderItemTemplate` moved to `Themes/Styles.xaml`, so Scripts folders
  get the same frame in their colour. *Versions…* uses the branch icon, so the clock means *Run history…* everywhere.
- **Library copies:** *Duplicate to My scripts* deploys the copy as version 1, by "RemoteHub", with a note naming the
  release – the library is part of the app (§4.10).
- **`script_versions.number`:** the version number column is `number`, because `version` is the row's sync counter
  that every table of notes and scripts has.
- **The run window asks the device first** (client or server, administrator or not, who is signed in) while the
  confirmation shows; a script for the other kind of Windows gets a warning there.
- **Lab:** S-SCRIPT-01 … 09 (docs/test-lab-cases.md §7.2). An account without administrator rights isn't covered in
  the lab – the agent admits only administrators by default; the message is a unit test.
