# RemoteHub

A modern hub for remote Windows administration: remote desktop sessions, a PowerShell console, file transfer, registry editor, services and processes – organised in folders with inherited settings and credentials. It's built with **.NET 10 (LTS)**, WPF and Fluent design (WPF-UI), and uses the Microsoft RDP client built into Windows (`mstscax.dll`). PowerShell remoting uses the PowerShell 7.6 SDK.

> **Formerly "RDP Manager".** The rename (internal build 2.0, before the version numbers were reset to 0.x – see *Versioning & releases*) changed the product, program (`RemoteHub.exe`), installer, code and settings folder. Upgrading keeps everything:
> - The installer replaces an installed RDP Manager (same upgrade code); the Start menu entry and install folder are now *RemoteHub*.
> - On the first start, `%APPDATA%\RdpManager` is copied to `%APPDATA%\RemoteHub` (settings, remembered shared-file key, log). The old folder is left as a backup and can be deleted later.
> - A shared folder that already has `RdpManager.settings` keeps using that file, so PCs with older versions keep syncing; new shared folders get `RemoteHub.settings`. The IT policy key `HKLM/HKCU\SOFTWARE\Policies\RemoteHub` is read first, `…\Policies\RdpManager` still works.
> - Backups are now `*.rhbackup`; older `*.rdpmbackup` files can still be imported.
> - The remoting setup script creates firewall rules named `RemoteHub-WinRM-HTTPS-In` / `-HTTP-In` and removes a rule of the old name (`RdpManager-WinRM-…`) when it runs. Targets set up earlier keep working without running it again.

## Build & run

```bash
dotnet build
dotnet run --project src/RemoteHub
dotnet test
```

## Installer (MSI)

```bash
powershell -ExecutionPolicy Bypass -File build/build-installer.ps1
```

Optional parameters: `-Version 0.9.0` (default: `<Version>` in `src/RemoteHub/RemoteHub.csproj`), `-Configuration Release`, `-OutputPath artifacts`. This builds `artifacts/RemoteHub-<version>-x64.msi`: a self-contained, per-machine install (no .NET runtime needed) that adds a Start menu shortcut.

- Silent install: `msiexec /i RemoteHub-0.9.0-x64.msi /qn`. Silent uninstall: `msiexec /x … /qn`.
- **`.rdp` files:** the installer registers RemoteHub under "Open with" and in *Settings → Default apps*. It doesn't take the default away from mstsc. To make RemoteHub the default, use Menu → *Use for .rdp files…*, or set it through IT policy (a default-associations XML).
- Building uses the WiX Toolset v5 MSBuild SDK, which comes from NuGet at build time, so nothing extra has to be installed.

## Versioning & releases

- **0.x = pre-release.** Versions stay below 1.0 until the first GA release, which will be **1.0.0**. Builds made before the move to GitHub were numbered up to 2.1.0; they were reset to **0.9.0** (same features as 2.1.0).
- **Format:** `major.minor.patch` (e.g. `0.9.1`), optionally with a pre-release suffix (`0.9.1-beta.1`). An MSI only knows three numeric parts, so a fourth part would be ignored by upgrades. The program file shows the same version with `.0` appended (e.g. 0.9.0.0).
- **The git tag is the version.** Pushing a tag `v0.9.1` runs [.github/workflows/release.yml](.github/workflows/release.yml): restore, build, run the tests, build the MSI with `build/build-installer.ps1 -Version 0.9.1`, and publish a GitHub Release with the MSI attached. Tags below 1.0 or with a suffix are marked as pre-releases. Keep `<Version>` in the csproj in step (it's used for local builds); the workflow warns when it differs from the tag.
- **Every push and pull request** to `main` runs [.github/workflows/ci.yml](.github/workflows/ci.yml) (build and tests).
- **Cutting a release:**

  ```bash
  git tag -a v0.9.1 -m "RemoteHub 0.9.1"
  git push origin v0.9.1
  ```

- **Downgrade from an older 2.x install:** the MSI refuses to install over a higher version. Uninstall RemoteHub 2.x first (settings in `%APPDATA%\RemoteHub` are kept), then install 0.9.x.
- **Not yet in place:** code signing. The MSI and the program are unsigned, so SmartScreen may warn when they're downloaded.

## Features

- **Folders with inheritance.** Credentials and every RDP setting can be set on a folder. Sub-folders and connections inherit them, or override them one value at a time ("Inherit · value" shows what the parent would give).
- **Saved credentials** (Menu → Saved credentials): named credentials that any folder, connection or the global defaults can reference. Editing one updates every item that uses it.
- **Local accounts on standalone servers:** tick *Local account on the target computer (.\user)* (or type `.\admin`). The credential then signs in with that local account on whichever server you connect to, so one folder credential covers many non-domain servers.
  - Windows' default policy refuses to send **saved** passwords to servers it can only verify via NTLM, which is typical for non-domain servers. You'll see "The server's authentication policy does not allow connection requests using saved credentials". That's a Windows policy (it affects mstsc too), not an app setting. Either allow saved credentials for those servers ("Allow delegating saved credentials with NTLM-only server authentication" → `TERMSRV/<server>`, via IT or Group Policy), or turn off NLA for those connections.
- **How passwords reach the server:** RemoteHub uses the Microsoft RDP client API (`mstscax.dll`, not `mstsc.exe`) and hands the stored password to the session in memory right before connecting – no clipboard involved.
- **Server refuses saved credentials** ("Your credentials did not work … does not allow connection requests using saved credentials"): the server requires the password to be entered in Windows' *Windows Security* dialog on every connection (e.g. "Always prompt for password upon connection"). RemoteHub does not get around this. Setting *Security & session → When Windows asks for the password* (inherited per folder/connection, default in Global defaults):
  - **Copy to clipboard** (default): the stored password is put on the clipboard so you can press Ctrl+V and OK; a notice appears at the bottom centre of the RemoteHub window. It is excluded from clipboard history (Win+V) and cloud clipboard and cleared after 30 s or right after a successful login. Other open RDP sessions with clipboard redirection could paste it in the meantime.
  - **Do nothing**: type it yourself.
  - The dialog is matched to the session by the username it shows when that is readable; otherwise the dialog must belong to RemoteHub (or have no owner) and exactly one session must have started connecting within the last 60 s. Otherwise nothing is copied.
  - To avoid the prompt entirely, the server's owner can review the "Always prompt for password upon connection" policy for that host.
  - Menu → *Open diagnostic log* shows what happened (no passwords, usernames or host names are logged).
- **Search** (Ctrl+F) finds folders and connections by name, host, notes and folder path; several words narrow the results (`prod sql`). Enter or double-click opens a connection or jumps to a folder; right-click → *Show in list*. If nothing matches, Enter quick-connects to the typed computer name.
- **Icons & colours** (editor → *Appearance*): choose one of ~35 built-in icons (server, database, cloud, firewall, laptop …) or your own image (PNG/ICO/JPG, scaled to 64 px and stored in the settings file). Colours: palette swatches or a custom `#RRGGBB`, inherited from the folder (e.g. a red *Production* folder). The colour is used for the icon in the list, the tab and the session frame.
- **Clear separation from other windows:** solid, tinted window colours instead of see-through effects, an accent-coloured window border (Windows 11), and the app icon plus the session name in the title and taskbar. Each session sits in a frame with a header (name, address); the frame lights up in the session's colour while the keyboard goes to the remote session ("Keyboard → remote session").
- **Global defaults** (Menu → Global defaults) sit at the root of the inheritance chain.
- **Drag & drop:** move connections and folders around the tree, and drop `.rdp` files anywhere in the window to import them.
- **`.rdp` export:** right-click a connection → *Save as .rdp file…*, or right-click a folder → *Export folder as .rdp files…*. The effective (inherited) settings are written. You can optionally include the password; it's encrypted for the current Windows user, as mstsc does.
- **Single instance:** opening a `.rdp` file while the app is running hands it to the open window, which reuses an existing entry for the same server.
- **Startup protection** (Menu → *Startup protection…*, off by default): RemoteHub asks for a startup password – or Windows Hello (PIN, fingerprint or face) – every time it starts.
  - **What it protects:** everything RemoteHub stores on this PC – the settings file (connections, credentials) and the remembered password of a shared settings file – is encrypted with a random 256-bit key, inside the existing Windows (DPAPI) encryption. That key is stored only wrapped: with the startup password (PBKDF2-SHA256, 600,000 iterations, AES-256-GCM) and, if chosen, with Windows Hello (a Hello key of this PC, kept in the TPM where available, signs a fixed challenge; its SHA-256 wraps the key). Without the password or Hello the settings can't be opened, also not by other programs running under your Windows account or from a copy of the files.
  - **Turning it on** rewrites the files with the new protection and removes the previous copies (`.bak`). If an old `%APPDATA%\RdpManager` folder exists, you can delete that unprotected copy in the same step.
  - **Changing the password, adding or removing Windows Hello, turning it off:** same menu; each needs the current password.
  - **Keep the password safe:** without it – and without Windows Hello on this PC – the settings on this PC can't be recovered. Export a backup first. Windows Hello is per PC and per Windows user.
  - It doesn't protect against malware that runs while RemoteHub is unlocked, and a shared settings file keeps its own password (other PCs aren't affected).
- **Menu → Settings overview…** shows every setting in one place – where the settings are stored, global defaults, refresh intervals of the remote tools, credentials (counts only, never passwords or user names), theme and keys, and how many folders and connections there are – with buttons to change each part. *Copy as text* puts it on the clipboard for support.
- **Menu → About RemoteHub…** shows the version, developer and copyright, build date, .NET, Windows, remote desktop client (`mstscax.dll`), PowerShell SDK and WPF-UI versions, and where the program, settings and log are. *Copy version info* copies it.
- **What's in which folder:** every folder is framed together with everything in it – in the folder's colour with a light tint of it (a neutral frame for folders without a colour; sub-folders get their own frame inside). Each folder also shows how many items it holds (*empty* when none). Hover over a folder or connection (also in search results) to see where it is (e.g. `LAB › NUC13` or `Top level › NUC13`) and where its credentials come from. Dragging an item into or out of a folder so that its **credentials change** (e.g. dropped just below a folder, which puts it at the top level) asks first and shows the old and the new source.
- **Navigation sidebar:** collapse it to a slim rail with the button in its header or **Ctrl+B** (expand / new connection / new folder / search stay available). The state and width are remembered per PC. The status dot next to a connection uses the same colours as its tab: green = connected, amber = connecting, red = failed, grey = open but disconnected.
- **Tabs grouped by connection:** one tab per connection (name, status dot; for a single PowerShell / file transfer / registry / services / processes tab also its icon). Only when a connection has **more than one tab** (e.g. remote desktop + registry) does a second row appear below with that connection's tabs – *Remote desktop*, *PowerShell*, *File transfer*, *Registry*, *Services*, *Processes* – each with its own status dot; the connection tab then shows how many are open. **+** next to the connection tabs (or right-click a tab) opens another tab for the selected connection; tools that need PowerShell remoting reuse the credentials of an open remoting tab. Selecting a connection shows the tab you used last there. Middle-click closes a tab; on a connection tab it closes all tabs of that connection (also *Close all* in its right-click menu). Closing a tab moves to the next tab of the same connection first.
  - **Ctrl+Alt+<** (the `< > |` key left of Y on German keyboards) goes to the next session, and **Ctrl+Alt+Shift+<** to the previous one. It also works while a session has keyboard focus and in full screen; switching from full screen keeps the next session in full screen. Use the **left** Alt key: AltGr+< still types `|` (in the app and in sessions), because AltGr = Ctrl+Alt on German layouts. Other choices are under Menu → *Session switch keys*: Ctrl+Alt+^, F1/F2, PageUp/PageDown, Left/Right.
  - Ctrl+Tab also switches (when the app has focus), Ctrl+W closes a session, and Ctrl+F searches or quick-connects (type a host and press Enter).
- **Session actions:** reconnect, disconnect, Ctrl+Alt+Del, full screen (native RDP connection bar), detach into a separate resizable window, and dock back.
- **Clipboard redirection** is on by default. It works host ↔ session, and therefore across sessions.
- **Windows key forwarding** (per connection or inherited): Win, Alt+Tab and similar keys go to the session while it has focus.
- **Dynamic resolution:** the remote desktop follows the window size. Smart sizing and fixed resolution are also available.

## PowerShell remoting (console, file transfer, registry, services, processes)

Right-click any connection → **Test PowerShell remoting** to check it by hand. To check automatically, turn on *Global defaults → PowerShell remoting → Check automatically after RDP login* (or per folder / connection; inherited like every other setting, default off). It uses the same (inherited) credentials as the RDP connection. With no credentials set, your own Windows account is used (Kerberos).

- **Automatic check** (when switched on): after the first successful RDP login of a connection, RemoteHub opens a WinRM session in the background (port 5985, or 5986 with *Use HTTPS*) and reads the PowerShell version. The result (available / not available, the reason and when) is stored **with the connection** in the settings file, so it is kept across restarts. It is shown in the connection editor.
- **Right-click** a connection or its tab:
  - available → **PowerShell**, **File transfer**, **Registry**, **Services** and **Processes**
  - not checked yet / not available → **Test PowerShell remoting (again)**, which shows the result and the reason. If opening a PowerShell tab later fails, the connection is marked as not available again.
- **PowerShell tab:** opens a new remote session to the host, as a tab next to the RDP sessions (detachable like them). Output is formatted on the remote computer, with colours for Write-Host, warnings and errors. **Tab** completes against the remote session: one match is inserted directly; several open a list above the input line (Tab/↓ next, Shift+Tab/↑ previous, Enter accepts, Esc restores what you typed; Ctrl+Space always opens the list). The list also opens by itself after typing `-` (parameters, or after `Get-`), `.` (members) or `\` (paths); nothing is pre-selected then, so Enter still runs the line. Typing narrows the open list. Completion uses the remote computer's own `TabExpansion2`, so it works with Windows PowerShell 5.1 and PowerShell 7 targets. The input line and the commands echoed in the output are **colour-coded** like PSReadLine (commands, parameters, strings, variables, numbers, keywords, operators, types, comments), using PowerShell's parser on this PC. The current folder comes from the remote session. Also: ↑/↓ history, Ctrl+C stops the running command (or copies selected text), Ctrl+L / `cls` clears, `exit` closes the tab. Read-Host and confirmation prompts are answered on the input line; passwords (Get-Credential, `-AsSecureString`) in a dialog. Not supported: full-screen console programs and single-key input (`ReadKey`).
- **File transfer tab** (right-click → *File transfer*, or the *File transfer* button of a PowerShell tab, which opens at that tab's current folder): a split view with **this PC** on the left and the **remote computer** on the right. The remote side starts at `C:\` (or the last remote folder of that connection); the local side starts at the last local folder.
  - Each side has an **address bar**: type or paste a path and press Enter (environment variables such as `%TEMP%` are expanded on that side; empty or *This PC* shows the drives). There's also *Up* (Backspace), *Refresh* (F5) and *New folder*. Double-click or Enter opens a folder.
  - Only the folder that is shown is loaded: one remote call each time you open a folder. Nothing is scanned in the background. Hidden files are dimmed.
  - **Drag & drop:** local → remote uploads, remote → local downloads. You can also drop files from Explorer onto the remote side, or drop onto a folder row to put the files into that folder. The arrow buttons in the middle transfer the selection. Folders are transferred with their contents. If items already exist, you choose *Overwrite* or *Skip existing*.
  - **Right-click** (or keys) on the remote side: *Download* to the local folder, *Rename…* (F2), *Delete…* (Del; asks first – folders are deleted with their contents, and there is no recycle bin for remote deletes; drive roots are refused), *New folder…*, *Copy path*, *Refresh* (F5). On the local side: *Upload*, *Show in Explorer*, *New folder…*, *Copy path*, *Refresh*.
  - Dragging remote files directly into Explorer isn't supported; drop them on the local side (or use *Download*).
  - Files dropped on a **PowerShell tab** are uploaded into its current folder.
  - **Transfers** run over their own remoting session in 1 MB chunks into `<name>.remotehub-part`. The SHA-256 checksum is compared with the source before the file gets its final name, and cancelled or failed transfers remove the partial file. Progress, speed and result appear in the **File transfers** pane on the right (styled like the navigation: resizable, collapses to a slim rail with the number of running transfers, width remembered per PC; it appears when a transfer starts), and the destination side refreshes when a transfer is done.
- **Registry tab** (right-click → *Registry*): the remote computer's registry like regedit – keys on the left, values (name, type, data) of the selected key on the right.
  - **Address bar:** type or paste a key and press Enter. `HKLM\…`, `HKLM:\…`, `HKEY_LOCAL_MACHINE\…`, `Registry::…` and regedit's `Computer\…` all work. The tree expands down to the key. It starts at the last key used for that connection (first time: `HKEY_LOCAL_MACHINE\SOFTWARE`).
  - Only the key that is opened is read (one remote call per key, sub keys load when you expand them). The 64-bit registry view is used. `HKEY_CURRENT_USER` is the profile of the account used for remoting, not of the user logged on via RDP.
  - **Values:** double-click or Enter edits (DWORD/QWORD in hex or decimal, multi-string one per line, binary as hex bytes). Right-click → *Modify…*, *Rename…* (F2), *Delete…* (Del), *Copy name*, *Copy data*, *New* → string, expandable string, multi-string, DWORD, QWORD or binary value. Expandable strings are shown and saved unexpanded.
  - **Keys:** right-click → *New* (key or value), *Rename…* (F2; a real rename like regedit, permissions are kept), *Delete…* (Del; the key and everything below it), *Export…* (saves the key as a `.reg` file on this PC, made by `reg export` on the remote computer), *Copy key path*, *Refresh* (F5).
  - **Safety:** every change is a single action you start; deleting keys or values always asks first and can't be undone (export first). Hives and the keys directly below them (e.g. `HKLM\SOFTWARE`) can't be deleted or renamed here. What you may change is decided by the remote account's permissions (`HKLM` needs an administrator). Value data is never written to the diagnostic log. Importing `.reg` files isn't offered.
- **Services tab** (right-click → *Services*): the remote computer's services like the Services console – name with a status dot, status, startup type, service name, *Log on as*, PID and description.
  - **Start**, **Stop** and **Restart** in the toolbar or the right-click menu; stop and restart ask first (running services that depend on it are stopped too). Each waits up to 60 s for the service and reports if it doesn't get there.
  - Right-click → *Startup type* → Automatic, Automatic (delayed start), Manual or Disabled (Disabled asks first). *Details…* (or double-click / Enter) shows path and description; *Copy service name / display name / path*.
- **Processes tab** (right-click → *Processes*): the remote computer's processes like Task Manager's details – name, PID, CPU %, working set, private bytes, user, session, threads, handles, start time and command line. The status line shows the number of processes, total CPU and memory use.
  - CPU % is the average since the previous refresh (the second reading follows 2 s after opening). User names need an administrator account on the target.
  - **End process** (Del) and **End process tree** (Shift+Del, also ends everything it started) ask first; several processes can be selected. Critical Windows processes (System, csrss, wininit, winlogon, services, lsass, smss, …) and the tab's own remoting process can't be ended from here. *Copy name / PID / path / command line*.
- **Both lists:** click a column header to sort (again to reverse), **Ctrl+F** or the filter box narrows the list (name, PID, user, status, …), F5 refreshes now. Rows are updated in place, so selection and sorting stay. They **refresh automatically while the tab is visible** – processes every **15 s**, services every **60 s** by default; change it (or turn it off) under **Menu → Global defaults → Remote tools**. The pause button in the tab stops the automatic refresh for that tab. Every action is written to the diagnostic log (what was done, not the names).
- **Passwords:** taken from the connection's credentials and passed in memory only. If a credential has no password, you're asked when connecting; that password is kept only for that tab and isn't saved.
- **Requirements on the target:** WinRM / PowerShell remoting enabled (`Enable-PSRemoting`), port 5985 (HTTP) or 5986 (HTTPS with a certificate this PC trusts) reachable, and the account must be an administrator or a member of *Remote Management Users*. Over HTTP, the WinRM traffic is encrypted by Kerberos/NTLM.
- **Computers outside the domain or addressed by IP** can't use Kerberos. For NTLM over HTTP, Windows only sends credentials to hosts in this PC's WinRM *TrustedHosts* list. RemoteHub does **not** change that list. Use *Use HTTPS* for those connections, or have an administrator add specific hosts to TrustedHosts (avoid `*`). Certificate checks are never skipped.
- **Setting up a target:** when a test fails, RemoteHub offers **Set up on the target…** (also right-click → *Set up PowerShell remoting on the target…*). It builds a ready-to-run script for an **elevated PowerShell on the target**. Copy it (it isn't saved in clipboard history and is cleared after 120 s) or save it as `.ps1`, paste it into the target's PowerShell (e.g. in its RDP session), run it, then click **Test again**. RemoteHub never runs the script itself.
  - **Already filled in:** all addresses of this PC (IPv4 and routable IPv6 of every active network adapter; the one used to reach the host comes first). You can change the list, e.g. add a subnet when NAT or VPN is in between. Only IP addresses or subnets are accepted. The script runs `Enable-PSRemoting` and adds a firewall rule (`RemoteHub-WinRM-HTTPS-In` / `-HTTP-In`) that allows WinRM only from those addresses. The script header lists every change and how to undo it; set `$DryRun = $true` at the top to only show what it would do.
  - **HTTPS (recommended, required for IP addresses and computers outside the domain):** RemoteHub creates a certificate for that one target: RSA 3072, 2 years, the host name or IP address as its name, server authentication only, and not a CA. The script installs it in `LocalMachine\My` and binds the WinRM HTTPS listener (5986) to it. If an HTTPS listener already exists, its certificate is replaced, and the script warns about it. This PC trusts the certificate for your Windows account (`CurrentUser\Root`, shown as "RemoteHub – WinRM <host>"; Windows asks you to confirm), and the connection switches to HTTPS. The private key exists only in the script; RemoteHub doesn't keep it. Delete a saved `.ps1` after use. To remove the trust later, open `certmgr.msc` → *Trusted Root Certification Authorities*.
  - **HTTP:** only for domain computers reached by name (Kerberos).
- Local accounts (`.\user`) are sent as `COMPUTER\user`, using the first part of the host name (`.\user` for IP addresses).

## Storage & security

Choose the location under Menu → *Settings location (OneDrive / shared)…*. The current location is shown at the bottom of the connection list.

| Mode | File | Encryption |
|---|---|---|
| **This PC** (default) | `%APPDATA%\RemoteHub\settings.dat` | Windows DPAPI for your user account. No password, but only you on this PC can read it. The previous version is kept as `.bak`. |
| **Shared folder** (OneDrive, network share) | `<folder>\RemoteHub.settings` | AES-256-GCM with a password (PBKDF2-SHA256, 600k iterations). The password is asked **once per PC**; after that the derived key is remembered on that PC (DPAPI-protected, `keys.dat`). Use it to sync between your PCs or to share connections with other admins. |

- **Sync conflicts:** when the shared file is changed on another PC, RemoteHub notices it and offers to reload. If both sides changed it, you choose *Reload* (take theirs) or *Keep mine*. Nothing is overwritten silently.
- **Shared file unavailable at start** (not synced yet, offline, password cancelled): the app starts with this PC's settings and doesn't touch the shared file. Restart to try again.
- **Dark theme** is the default (Menu → Theme to change). **Window layout, theme and switch keys** are per PC (`%APPDATA%\RemoteHub\local.json`) and don't sync.
- **Preset by IT (Intune / GPO):** registry value `SettingsFolder` (REG_SZ or REG_EXPAND_SZ) under `HKLM\SOFTWARE\Policies\RemoteHub` or `HKCU\SOFTWARE\Policies\RemoteHub`, e.g. `%OneDriveCommercial%\RemoteHub`. Users can't change the location while the policy is set.
- *Forget remembered passwords on this PC* (in the same dialog) removes the cached keys.
- **Export / import backup** (Menu): portable, password-protected file (AES-256-GCM, PBKDF2-SHA256 with 600k iterations), including saved credentials. You can restore it on any PC or user account, either as a new folder or by replacing everything.
- The single-instance pipe only accepts connections from the same Windows user (`PipeOptions.CurrentUserOnly`).
- The `RDPMANAGER_DATA_DIR` environment variable points the app at a different local data folder, which is useful for testing.
