# RemoteHub Agent – Apps in their own tabs

Status: Draft v2 (2026-10-29), checked against the code of main (`3d8b5a3`). Nothing is built yet. v1 of this draft (show only the apps a signed-in user shares) was replaced the same day: the goal is apps of the admin's own, opened from a connection, each in its own tab.
Owner: Grischa Ernst

A connection gets a list of **apps** – Event Viewer, Services, SAP Logon, a PowerShell window – picked from the device's Start menu, or entered by hand for anything that isn't there. Opening one starts it on the device, in the admin's own session, and shows **only that app** in its own RemoteHub tab. The app's window follows the tab's size, so it feels like a local window. Several apps of the same device open side by side in several tabs. Nothing has to be running before; nothing else of the device's desktop is shown.

It works like RDP RemoteApp, but over the agent's connection – also where RDP is turned off – and without RDS on the device.

## 1. Goal

| Example | What happens |
|---|---|
| Event Viewer on a file server | *Open app ▸ Event Viewer* on the connection. The agent signs the admin in on the console if nobody is (automatic sign-in), starts `mmc.exe eventvwr.msc`, and the tab shows Event Viewer filling the tab. |
| Three tools at once | Services, Event Viewer and Computer Management of the same server, each in its own tab (or detached into its own window), all in the same session on the device. |
| A line-of-business app on a PC | SAP Logon from the connection's apps; the admin's own session on the PC, the physical screen covered by the privacy screen. |

### 1.1 Principles

1. **One app per tab.** A tab shows its app's windows (main window, dialogs, menus) and nothing else. Other apps, the desktop and the taskbar never appear in it.
2. **The admin's own session only.** Apps run as the account that signed in to the agent, in that account's console session. No one else's session is touched, so nobody is asked and no banner is needed (§4).
3. **Started by the agent, on demand.** Opening the tab starts the app; it doesn't need to be running beforehand or published anywhere.
4. **Built from what exists.** Each tab is an ordinary agent desktop connection – same sign-in, rights, resume, clipboard, video – with one virtual monitor of its own (§5).

## 2. Non-goals

- **Apps in another user's session** (support: "show me only Outlook"). The v1 draft of this document covered it by masking everything but the shared app; it can come back as its own design.
- **A second session next to someone else's.** If another user is signed in on the console, app tabs are refused (§4.1). Extra sessions on Windows Server through RDS are for later (§12, Q4).
- **Separate windows without a tab.** A tab can be detached into its own window (as today); seamless windows straight on the admin's desktop are for later.
- **Sound, printing, drive redirection.**

## 3. How it fits together

```
RemoteHub                                   Device (console session of CONTOSO\admin)
┌──────────────────────────────┐            ┌──────────────────────────────────────────┐
│ Tab "FS01 – Event Viewer"    │ connection │ helper 1 ─ virtual monitor 2             │
│   AgentDesktopSession (app)  │ ─────────▶ │            mmc.exe eventvwr.msc (job 1)  │
│ Tab "FS01 – Services"        │ connection │ helper 2 ─ virtual monitor 3             │
│   AgentDesktopSession (app)  │ ─────────▶ │            mmc.exe services.msc (job 2)  │
│ Tab "FS01" (whole desktop,   │ connection │ helper 3 ─ monitor 1 (physical/primary,  │
│   optional)                  │ ─────────▶ │            privacy screen on)            │
└──────────────────────────────┘            └──────────────────────────────────────────┘
```

- **One connection per tab.** Each app tab opens its own `/v1/desktop` connection with `Start.App` (§7). That reuses everything a connection has (pinned TLS, sign-in, rights, flow control, quality steps, resume, clipboard) and keeps tabs independent: one dropping doesn't touch the others. The spike checks that nothing in the service assumes one connection per session (§11).
- **One virtual monitor per tab.** The service gives each app tab its own monitor from the virtual display driver (§5). The app lives on that monitor, maximised; the tab streams only that monitor. No other app's window overlaps it, so there is no covering or z-order to work out between tabs.
- **One session for all tabs.** The first app tab finds or creates the admin's session (§4); the next ones join it without asking.

## 4. The session

### 4.1 Which session

The agent looks at the console session:

| Console | App tab |
|---|---|
| The admin's own session (same account by SID), unlocked | Used, no question (the existing rule for one's own session, agent-desktop-draft §4.2 step 4) |
| The admin's own session, locked | Refused for now: "Your session on FS01 is locked – unlock it in a desktop tab first". Unlocking through the credential provider is planned (credential-provider-design §12); then it unlocks the way automatic sign-in signs in. |
| Nobody signed in | **Automatic sign-in** with the connection's credentials (needs the `AutoSignIn` right and the credential provider, as today). Without them: refused, "Nobody is signed in on FS01 and automatic sign-in isn't available". |
| Someone else | Refused: "CONTOSO\jane is signed in on FS01 – app tabs need your own session". RemoteHub offers the desktop tab (with consent) or RDP instead. |

The second and further app tabs find the session the first one made and connect to it like the first case. While the automatic sign-in of the first tab is still under way, the others wait for it: the service queues them behind the sign-in rather than starting a second one.

### 4.2 The physical screen

While any app tab is open, the physical monitors are covered by the privacy screen with the device's mouse and keyboard blocked – the cover a session automatic sign-in created already gets (credential-provider-design §9). It is turned on for an own session that existed before as well: someone walking past the console mustn't see or use the admin's apps. It comes down when the last app tab closes, unless a desktop tab still has it on. The overlays are excluded from capture and only cover the physical monitors; the virtual monitors don't need them.

### 4.3 The end

| Event | What happens |
|---|---|
| The app closes (its last window and its processes are gone) | The tab shows "Event Viewer was closed" with [Start again] and [Close tab]. The connection stays until one is chosen. |
| The admin closes the tab | The agent asks the app to close (`WM_CLOSE` to its top-level windows). If it shows a dialog ("Save changes?"), the tab stays open and shows it, with the heading "Closing…" and a [Leave it running] button. Closed or left: the tab and its monitor go. An app left running moves to the primary monitor (Windows does that when its monitor goes away). |
| The connection drops | Resume as today (agent-desktop-draft §4.6): the app keeps running, and the monitor stays for the resume window (2 minutes), then it is removed. |
| The last app tab ends | In a session automatic sign-in created: the end action – **SignOut** by default for app sessions (§12, Q1); `AutoSignInEndAction` from the policy wins. In a session that existed before: nothing, except the privacy screen comes down. |

## 5. Virtual monitors

The agent already ships a virtual display driver (Indirect Display, [VirtualDisplay.cs](../src/RemoteHub.Agent/Desktop/VirtualDisplay.cs), [third-party/VirtualDisplayDriver](../third-party/VirtualDisplayDriver)) for headless servers. It runs one monitor today; the driver's settings take a monitor count (`<monitors><count>`) and a resolution list.

- **One monitor per app tab**, added when the tab opens and removed when it ends (or its resume window runs out). `VirtualDisplay` grows from "on/off at one size" to a set of leases, each with its own monitor and size. The limit is policy `MaxAppTabs` (default 8, at most what the driver supports).
- **The monitor is the tab's size.** RemoteHub sends the tab's size in pixels as `FitWidth`/`FitHeight` (`ValidFitSize`, as for the desktop's resolution fit). The service writes that size into the driver's resolution list and the helper sets the mode (`DisplayModes.Fit`). A resized tab changes the mode again, 500 ms after the last change.
- **Never primary.** The physical (or basic display) monitor stays primary, so the Start menu, notifications and new windows of other programs open there – never in an app tab.
- **No taskbar on app monitors.** While app tabs are open, the admin's "show my taskbar on all displays" (`HKCU\…\Explorer\Advanced` `MMTaskbarEnabled`) is set to 0 and put back afterwards. It is recorded and put back the way the Ctrl+Alt+Del settings are (privacy-mode-design §5.9: value or "absent", owners, put back by the next helper or by the service after a crash). It is the admin's own profile; nothing of another user is touched.
- **After a topology change** (a monitor added or removed, a mode change), Windows may move windows between monitors. Each helper puts its app's windows back on its monitor (§6.2) after `WM_DISPLAYCHANGE`.

Without the driver (an agent installed without it, or a Windows build it doesn't support), app tabs aren't offered: Hello leaves out the `apps` feature and RemoteHub says why in the tooltip. A fallback on the physical monitor – bringing the active tab's app to the front and freezing the other tabs – was considered and dropped: other apps would show whenever they overlap.

## 6. The app

### 6.1 Starting it

The **service** starts the program:

- **As the admin:** `WTSQueryUserToken` for the session, checked against the connection's account SID; an environment block from `CreateEnvironmentBlock`; `CreateProcessAsUser` on `winsta0\default` – as `clipboard-files` is started today ([ClipboardFilesReader.cs](../src/RemoteHub.Agent/Desktop/ClipboardFilesReader.cs)).
- **What may be started:** `Program` must be a fully qualified path after expanding environment variables **in the admin's environment** (`%SystemRoot%\system32\mmc.exe`, `%ProgramFiles%\…`); no device paths (`\\.\`, `\\?\`); at most 32 KiB of arguments (Windows' limit). It is started directly with `CreateProcessAsUser`, not through the shell, so no file associations or verbs run. `.msc` consoles are started as `mmc.exe` with the file as argument (the Start menu list and RemoteHub's presets do this, §6.5 and §8.3).
- **Its own job object** per tab. The program and the processes it starts belong to the tab (§6.2). The job doesn't kill on close – closing a tab asks the app to close (§4.3).
- **Elevated, optional:** an app marked *Run as administrator* starts with the admin's linked (full) token, when the account is an administrator on the device and the policy allows it (`DesktopAppsElevated`, default on). The service holds `SeTcbPrivilege`, so no UAC prompt is needed – it is the same account that would confirm one. Audited (§9). Without it, an app that asks for elevation later raises a UAC prompt on the secure desktop; the tab that gave the last input shows the primary monitor's secure desktop while it is up (needs the `SecureDesktop` right, as for the desktop tab).
- **Store apps** (by AppUserModelID) are started through `IApplicationActivationManager`, which must run as the user: by a short-lived `RemoteHubAgent.exe launch-app` started with the admin's token, like `clipboard-files`. Phase 2 (§11).

### 6.2 Which windows belong to the tab

A top-level window belongs to an app tab when its process is in the tab's job – the started program or anything it started. When the started process ends within 10 s without having shown a window (a launcher that handed over to another process, or a single-instance app that passed its arguments to a running copy), the tab also takes new windows of the **same program** (`AppIds` in [AppWindows.cs](../src/RemoteHub.Agent/Desktop/AppWindows.cs)) that appear after the start and that no other tab owns. If two tabs could own one, the one started last does.

The helper walks the windows after every capture, as it does for hidden apps (privacy-mode-design §14.1):

- **New windows of the app** that open on another monitor are moved to the tab's monitor, keeping their size, centred on the app's main window. A window larger than the monitor goes to its top left.
- **The main window** (the first unowned, visible window with a title) is maximised on the tab's monitor and kept there. A window that can't be resized (fixed-size tools) is centred instead.
- **Another program's window** that ended up on the tab's monitor is moved to the primary monitor. Until it is gone, its area is filled in the picture with the console's dark colour (`AppMask.Fill`, `AppMask.Apply`), so another program never shows in the tab.
- **The desktop background** around a window that doesn't fill the monitor is filled the same way.

### 6.3 Input

Each tab's input goes to its own monitor, as today. What is new:

- **The tab's app gets the keyboard.** Windows has one foreground window per session. When input arrives for a tab whose app isn't in the foreground, the helper first brings the app's last active window to the front (`SetForegroundWindow`, allowed because the helper sent the last input event). Switching tabs in RemoteHub and typing therefore types into the right app.
- **Shell keys are dropped** in app tabs: the Windows keys alone and with anything, Ctrl+Esc, Alt+Tab, Alt+Esc and Ctrl+Shift+Esc. They'd open things on the primary monitor, which the tab doesn't show. Task Manager can be an app of its own.
- **Not offered** in app tabs: Ctrl+Alt+Del, the monitor switch, the resolution menu (the tab sets it), the privacy screen button (§4.2 handles it), and view-only (an app nobody can use is pointless).

### 6.4 Clipboard and files

As for a desktop connection, at the policy's level (`DesktopClipboard`). It is one clipboard for the whole session, so a copy in one app tab can be pasted in another – the same as on a local PC. Files dropped on an app tab are saved on the admin's own Desktop in *From RemoteHub*, as today.

### 6.5 The Start menu list

So the admin can pick what is installed instead of typing paths, the agent lists the device's Start menu (§8.2). It needs no session: the list comes from files on disk, so it works when nobody is signed in.

- **Where from:** the shortcuts (`.lnk`) under the common Start menu (`%ProgramData%\Microsoft\Windows\Start Menu\Programs`), and under the admin's own Start menu when their profile is on the device (found by SID in `ProfileList`; a redirected or roaming folder that isn't on the device is left out). Subfolders are kept as groups ("Administrative Tools", "Microsoft Office").
- **Read by the service, without running anything:** each shortcut is loaded with `IShellLinkW` / `IPersistFile::Load` on an STA thread and read with `GetPath(SLGP_RAWPATH)`, `GetArguments`, `GetWorkingDirectory` and the *Run as administrator* flag (`SLDF_RUNAS_USER`). `Resolve` is never called, so no link tracking, network lookup or search for a moved target. The shortcuts are files other programs wrote, so every field is checked as untrusted: lengths capped (path 1024, arguments 4096, name 256 characters), environment variables kept as written (they are expanded at start, §6.1).
- **What is listed:**
  - a target that is a program (`.exe`) – with its arguments and *Run as administrator* as the shortcut has them;
  - a target that is a console (`.msc`) – turned into `%SystemRoot%\system32\mmc.exe` with the console as argument, as the presets do;
  - nothing else: documents, web links (`.url`), help files and folders are skipped, because app tabs start programs directly and never through the shell (§6.1). Shortcuts whose target isn't on the device are skipped, and so are duplicates (same program and arguments).
- **Name and icon:** the name is the shortcut's localised display name (`SHGetFileInfo`, `SHGFI_DISPLAYNAME` – so "Event Viewer" or "Ereignisanzeige" as the device shows it). The icon is the shortcut's, 32×32, sent as PNG (at most 8 KiB each; larger ones are left out and RemoteHub shows a generic icon).
- **Filtered by policy:** when `DesktopAppsAllowed` is set, only allowed programs are listed; with `DesktopAppsElevated` = 0, *Run as administrator* is cleared.
- **Limits:** at most 2000 entries and 4 MiB per list; the walk stops at a depth of 8 folders and doesn't follow junctions or symbolic links (reparse points) in the Start menu folders.
- **Store apps** aren't in these folders. They are listed in phase 2 together with starting them (§11), from the user's `shell:AppsFolder` by `RemoteHubAgent.exe launch-app --list` run as the admin – which needs their session.

The list is only a suggestion: an entry the admin adds from it is stored on the connection like one typed in, and checked again when it starts (§6.1).

## 7. Protocol

In [DesktopProtocol.cs](../src/RemoteHub.Agent/Desktop/DesktopProtocol.cs):

```csharp
// DesktopFeatures
/// <summary>App tabs: DesktopStart.App starts a program in the account's own session, on a virtual monitor of its own.</summary>
public const string Apps = "apps";

/// <param name="Program">Full path on the device; environment variables are expanded in the account's environment.</param>
/// <param name="Arguments">Command line arguments; never logged.</param>
/// <param name="WorkingFolder">Start in this folder ("" = the program's folder).</param>
/// <param name="Elevated">Start with the account's full administrator token (§6.1).</param>
/// <param name="Name">Shown in the device's messages and the tab ("Event Viewer").</param>
public sealed record DesktopAppStart(string Program, string Arguments, string WorkingFolder, bool Elevated, string Name);

/// <param name="App">Start this app on a virtual monitor of its own in the account's own console session (feature apps).
/// SessionId and Consent are ignored; the agent finds or creates the session (§4).</param>
public sealed record DesktopStart(..., DesktopAppStart? App = null);

// DesktopStatusState: new states
//   "StartingApp" – the program is being started (Message: "Starting Event Viewer…")
//   "AppClosed"   – the app's last window and processes are gone; RemoteHub offers Start again / Close tab
//   "AppClosing"  – the admin closed the tab and the app shows something before it closes

// DesktopMessage
/// <summary>RemoteHub → agent: close the tab's app (Force: leave it running and end the tab).</summary>
AppClose = 15,
/// <summary>RemoteHub → agent: start the tab's app again after AppClosed.</summary>
AppRestart = 16,

public sealed record DesktopAppClose(bool Force);

/// <summary>RemoteHub → agent, after Hello and instead of Start: list the Start menu's apps (§6.5). Agent → RemoteHub: the list.</summary>
AppList = 17,

/// <param name="Group">The Start menu folder it is in, "" for the top ("Administrative Tools").</param>
/// <param name="Icon">32×32 PNG, null when there is none or it was too large.</param>
public sealed record DesktopListedApp(string Name, string Group, string Program, string Arguments, string WorkingFolder, bool Elevated, byte[]? Icon);
/// <param name="Truncated">More than the limits allow were found; the rest is left out.</param>
public sealed record DesktopAppList(DesktopListedApp[] Apps, bool Truncated);
```

**Listing:** RemoteHub opens a connection as for a tab, reads `Hello`, sends `AppList` instead of `Start`, gets the list and closes. Nothing starts and no session is needed; the service checks the `Desktop` right and the policy `DesktopApps` as for a start, and answers at most one `AppList` per connection.

- `DesktopRights` gets `Apps` (may open app tabs: `Desktop` right, policy, driver present) and `AppsElevated`.
- `Layout` for an app tab lists only its monitor, so the viewer has nothing to switch to.
- `HelperInit` gets the tab's monitor (device name) and what it needs to find the job's processes; the service keeps the job and the monitor lease, so they outlive a helper restart.
- The arguments travel inside the pinned TLS channel like every other message. They may hold paths or server names, so no log line on either side contains them.

## 8. RemoteHub

### 8.1 On a connection

- **Settings → Apps** (agent connections, inherited from the folder like the other agent options; a connection's own list replaces its folder's): a list with **Name**, **Program**, **Arguments**, **Start in** and **Run as administrator**, plus *Remove* and up/down to order them. Stored in the local database with the connection, with the icon when it came from the device.
- **Adding:** *Add from the device…* (the usual way, §8.2), and *Add another app* for what isn't in the Start menu – an empty entry, or one of the presets (§8.3).
- **Opening one:** the connection's context menu gets *Open app ▸* with the apps by name and, at the bottom, *More apps on FS01…*; where *Connect* is, a split button offers the same. Each opens a tab titled "FS01 – Event Viewer".
- **Several at once:** each choice opens its own tab; choosing an app that already has a tab switches to it (a second copy is *Open again* in the tab's menu).
- **No dialog before connecting:** there is no session, consent or monitor to choose. Quality comes from the connection's setting.

### 8.2 Picking from the device's Start menu

A dialog built in code like its neighbours (`Views/*.cs`, `FluentWindow`, WPF-UI controls and the theme's resources):

```
"Apps on FS01"
├ Search box                       filters by name, group and program, as you type
├ List, grouped by Start menu folder (collapsible group headers)
│   row: [☐] [icon 32] Event Viewer
│                      %SystemRoot%\system32\mmc.exe eventvwr.msc   (secondary text)
│                      "Runs as administrator" badge where the shortcut says so
│   empty: "No apps found in the Start menu of FS01"
│   truncated: "Only the first 2,000 apps are shown – search to narrow them down"
├ Link  "Not in the list? Add another app"   → closes and adds an empty entry (§8.1)
└ Buttons  [Add n apps] Primary (disabled while nothing is ticked) · [Cancel]
```

- **Loading:** it opens at once with a progress ring and "Reading the Start menu of FS01…", then fills from `AppList` (§7). A failure shows the agent's message (no right, apps off by policy, device not reachable) with [Try again]. The list is kept for the RemoteHub session per device, with a refresh button in the dialog's header.
- **Already on the connection:** those rows are ticked and disabled ("Added"), so nothing is added twice.
- **From *More apps on FS01…*:** the same dialog in single-choice mode with [Open] instead of [Add]: a double-click or [Open] opens the app in a tab right away. The tick box *Keep it in the connection's apps* (on by default) adds it as well.
- **From a folder's settings:** the dialog asks which of the folder's devices to read the Start menu from (the first agent connection is preselected), since the folder itself has no device.

### 8.3 Presets

Built into RemoteHub, editable after adding – for devices whose Start menu doesn't have them, or before the device can be reached: Event Viewer (`%SystemRoot%\system32\mmc.exe eventvwr.msc`), Services (`services.msc`), Computer Management (`compmgmt.msc`), Task Scheduler (`taskschd.msc`), Hyper-V Manager (`virtmgmt.msc`), Server Manager, Registry Editor (elevated), Windows PowerShell, PowerShell 7 (`%ProgramFiles%\PowerShell\7\pwsh.exe`), Command Prompt; Windows Terminal (a Store app) in phase 2. A preset whose program isn't on the device fails at start: "C:\Windows\system32\virtmgmt.msc isn't on FS01".

### 8.4 The tab

- The picture fills the tab 1:1 – the monitor has the tab's size, so there is no scaling and no black bars. Resizing the tab resizes the app (§5).
- The toolbar keeps quality, connection info and the keys toggle; the rest is hidden (§6.3).
- Detaching the tab (as today) gives a window of its own that resizes the app with it.
- The states of §7 use the existing waiting and paused panels: "Signing in on FS01…", "Starting Event Viewer on FS01…", "Event Viewer was closed" [Start again] [Close tab], "Closing Event Viewer…" [Leave it running].

## 9. Rights, policy and audit

| Item | Kind | Default | Meaning |
|---|---|---|---|
| `Rights\Desktop` | right | as today | Needed for app tabs. `DesktopViewOnly` alone isn't enough. |
| `Rights\AutoSignIn` | right | as today | Needed only when nobody is signed in (§4.1). |
| `DesktopApps` | DWORD | 1 | 0: no app tabs on this device, and no Start menu list. |
| `DesktopAppsElevated` | DWORD | 1 | 0: *Run as administrator* is refused. |
| `DesktopAppsAllowed` | REG_MULTI_SZ | – | When set: only these programs (full paths, `AppIds` matching) may be started in app tabs. |
| `MaxAppTabs` | DWORD | 8 | App tabs open at the same time per device, 1–16. |

`AgentPolicy`, the `AgentRight` docs, the ADMX template ([admx-design.md](admx-design.md)) and agent-desktop-draft §4.5 get the new entries.

**Audit** (`AgentLog`):

| Event | Name | Text |
|---|---|---|
| 1109 | `DesktopAppStarted` **(new)** | "Desktop: CONTOSO\admin from 10.0.0.5 started c:\windows\system32\mmc.exe (elevated) in session 1 for an app tab" – the program path only, **never the arguments** |
| 1100 / 1101 | `DesktopStarted` / `DesktopEnded` | as today, with "app tab" in the text |
| 2100 | `DesktopRefused` | with the reasons of §4.1 and §9 (policy off, program not allowed, elevation refused, too many tabs, someone else signed in) |

## 10. Security

- **No new reach.** An account with `Desktop` in its own session could already start any program through the desktop tab. App tabs start programs without the round trip through Start or Run, so each start is audited (§9) and can be limited (`DesktopAppsAllowed`).
- **Elevated starts skip the UAC prompt** for an account that is an administrator on the device. It is the same account that would confirm the prompt, and it signed in to the agent with its password. It still goes into the security review that agent-desktop-draft §4.4 asks for, and it can be switched off.
- **Program paths and arguments are untrusted input** to a LocalSystem service: checked as in §6.1, started without the shell, never logged.
- **Shortcuts are untrusted input** too: the service reads them without resolving them, caps every field and doesn't follow reparse points (§6.5). The list tells the admin which programs are installed in the Start menu – something an account with `Desktop` could see on the desktop anyway; it is refused where app tabs are.
- **Someone else's session is never used** (§4.1), so there is no consent or privacy question; the physical screen is covered (§4.2).

## 11. Plan

| Step | What | Rough effort |
|---|---|---|
| Spike (lab, before building) | The driver: adding and removing monitors one at a time while others are in use, a different mode per monitor, the most monitors it takes, and how windows on other monitors behave on each change. `MMTaskbarEnabled` taking effect without restarting Explorer. `SetForegroundWindow` from one helper while another tab's app has the foreground. Several connections to one session in the service. | 2 days |
| Phase 1 | Monitor leases in `VirtualDisplay`; `Start.App` in `DesktopEndpoint` (session rules, auto sign-in queue, start, job, audit); the Start menu list (§6.5, `AppList`); window placement and input in the helper; taskbar setting with record and put-back; policy and ADMX. RemoteHub: apps on the connection, the Start menu picker, presets, *Open app* and *More apps…*, the app tab. | 3–4 weeks |
| Phase 2 | Store apps by AppUserModelID – listed (from `shell:AppsFolder`, as the admin) and started; the tab shows the app's icon. | 1 week |

**Tests** (same PR as each phase):
- Unit: start validation (paths, environment expansion as the user, device paths, argument limit, policy allow-list and elevation); the session rules of §4.1 with fake sessions; window ownership (job, launcher hand-over, two tabs of one program); window placement decisions; shell-key filter; monitor leases (add, resize, remove, resume window, limit); taskbar setting record and put-back; the new protocol records. The Start menu list against a temporary folder of shortcuts: `.exe` and `.msc` targets, documents and `.url` skipped, a missing target, duplicates, a junction in the folder, a shortcut with an over-long path or arguments, the *Run as administrator* flag, the policy filter, the limits and `Truncated`.
- View (STA): the Apps section of the connection settings (presets, inheritance, *Add another app*), the Start menu picker (loading, failure, search, groups, "Added" rows, single-choice mode with *Keep it in the connection's apps*), the *Open app* menu, tab titles, the state panels.
- Lab (`D-APP`, cases in [test-lab-cases.md](test-lab-cases.md)), on W11 and Server 2025: the Start menu list with nobody signed in contains Event Viewer (`mmc.exe eventvwr.msc`) and a test program installed for the lab, and opening an entry from it starts that program; three apps in three tabs with nobody signed in (one automatic sign-in, three monitors, each tab shows only its app; a known second program on the primary monitor never appears); typing into tab 2 after clicking tab 1 reaches tab 2's app; resizing a tab resizes the app; *Run as administrator* starts elevated without a prompt and is refused with the policy off; closing a tab with unsaved changes shows the dialog in the tab; the last tab signs the session out; someone else signed in → refused; resume after a drop keeps the app; the taskbar setting is back after the service was killed.

## 12. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | End action when the last app tab of an automatically signed-in session ends? | SignOut (the apps were started for these tabs). `AutoSignInEndAction` from the policy wins, and RemoteHub's per-connection setting can choose Lock. |
| Q2 | Does closing a tab close the app? | Yes, politely (§4.3), with *Leave it running*. |
| Q3 | Scaling: the virtual monitor runs at 100 %; on an admin PC at 150 % the app looks small. | Spike: set the virtual monitor's scale to the admin's (sent with `Start`). If Windows doesn't allow that per monitor without a sign-out, offer a zoom in the tab instead. |
| Q4 | Extra sessions on Windows Server when someone else is on the console? | Later, through RDS where it is installed; not in this design. |
| Q5 | Should IT also publish apps through the device's policy, next to the ones on the connection? | Not in phase 1. A policy list `DesktopPublishedApps` that RemoteHub shows under *Open app* would suit IT-managed fleets – phase 2 if asked for. |
