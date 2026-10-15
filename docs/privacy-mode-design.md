# RemoteHub Agent – Privacy mode

Status: Draft v1 (2026-10-13), checked against the code of 0.18.0. Phases 1, 2 and 3 are implemented (§14) with the proposals in §12 as defaults (Q2 changed: input block on by default); the lab tests §5.7 and §6.6 and the Phase 3 lab cases have not run yet. The snip guard (§14.2) and the corrections in §5.2, §5.4, §5.5 and §8 were brought in line with 0.18.11 (2026-10-15).
Owner: Grischa Ernst

This covers the item "privacy mode" that [agent-desktop-draft.md](agent-desktop-draft.md) §2 (decision 8) left out of v1, and Phase 2 of [credential-provider-design.md](credential-provider-design.md) §12 ("black out the physical screen and block local input").

## 1. Goal

Privacy works in both directions during an agent desktop session.

| Phase | Who | What |
|---|---|---|
| **1 – Privacy screen** | Admin | Black out the device's physical screen, optionally with a message, while the admin keeps working. Local mouse and keyboard can optionally be blocked as well. |
| **2 – Hide my screen** | Person at the device | Hide the whole screen from the admin at any time: black it out (pause) or blur it. |
| **3 – Hide apps** | Person at the device | Pick running apps (more than one at a time) that the admin must not see. The choice holds through minimize, restore, move, new windows of the same app, and windows on other monitors. Apps can be picked in the consent question (Ask or PIN) before anything is sent, or later from the banner. |

The three phases share one foundation (§4): a privacy state in the helper, a hook in the frame loop, and one protocol message for each direction.

**Build order:** Phase 2 first. It is the smallest and builds most of the shared foundation. Phase 1 comes next and Phase 3 last.

### 1.1 Principles

1. **Privacy fails closed.** If the helper cannot work out what to hide (an error, a timeout, a state it doesn't know), it sends **no picture** rather than an unmasked one. A frozen or blank viewer is a bug report. A leaked window is an incident.
2. **The person at the device can always end the session.** No admin feature may take away the banner's **Disconnect**. This includes the privacy screen with local input blocked (§5.5).
3. **Hiding happens on the device.** Pixels that are hidden are never encoded or sent. RemoteHub cannot un-hide them, and neither can a modified RemoteHub.
4. **The admin always knows something is hidden, but not what.** The viewer shows that the screen or some regions are hidden. It never shows which apps were hidden or their window titles.
5. **Everything ends with the helper.** Overlays, input hooks and masks belong to the helper process. If the helper crashes or the session ends, the user gets their screen and input back without anyone cleaning up.

## 2. Non-goals

- **Hiding things on the secure desktop** (sign-in, lock screen, UAC) – **until the lab test in §5.7 says it works**. Until then the privacy screen does not cover it (§5.6), and the person at the device has no banner there (§6.5).
- **Leaks through other capture paths.** Another screen-sharing tool, a screenshot tool, or Teams sharing the screen at the same time is out of scope.
- **Hiding an app's sound, notifications sent to other devices, or its files.** This is about pixels on the screen only. Clipboard content from hidden apps is covered (§7.7).
- **Privacy mode in RDP sessions** opened with RemoteHub's RDP tab. This is about the agent desktop only.
- **Windows 10 before 2004 (build 19041)** for Phase 1. On those builds Windows has no way to hide a window from capture. Phases 2 and 3 work on every supported build.
- **Saving the hidden-app choice between connections.** The choice applies to one connection only (but see §12, Q4).

## 3. Threat model

| # | Who | Goal | Main defences |
|---|---|---|---|
| P1 | Admin with `Desktop` right | Seeing a screen or app the user hid | Masking happens in the helper before encoding (§4.2). Admin input into hidden regions and hidden apps is dropped (§6.4, §7.6). Clipboard from hidden apps is not sent (§7.7). |
| P2 | Admin | Locking the user out of their own device for good (privacy screen plus input block) | **Disconnect** stays reachable (§5.5). Ctrl+Alt+Del always works locally. Everything ends with the helper (§1.1 rule 5). A new right and policy (§8). Audit (§9). |
| P3 | Person at the device | Hiding the screen to sabotage support | Not a threat: the admin sees that the screen is hidden and can end the session. |
| P4 | Window timing (no attacker) | A hidden app shows for a frame while it moves, opens or restores | Masks are padded in time, and frames are held during animations (§7.4). |
| P5 | Windows shell (no attacker) | The hidden app's content shows in a thumbnail, Alt+Tab, Task View or a toast drawn by Explorer | Known shell surfaces are masked while any app is hidden (§7.5). What remains is documented in §11. |
| P6 | Local process without admin rights | Making an app look "hidden" or the overlay look "on" to deceive the admin or user | The helper runs as LocalSystem and works out process identity itself from image paths and package IDs (§7.2). Window titles and class names are not trusted for identity. |

## 4. Shared foundation

### 4.1 Privacy state in the helper

`DesktopHelperSession` owns one immutable `PrivacyState`, which is swapped atomically. The capture thread, the input sink and the UI thread read it.

```csharp
internal sealed record PrivacyState(
    ScreenHide User,                 // None | Black | Blur – Phase 2
    AdminScreen Admin,               // Off | Black | Message, + BlockLocalInput – Phase 1
    IReadOnlySet<AppId> HiddenApps); // Phase 3
```

### 4.2 The hook in the frame loop

The hook goes into `DesktopHelper.cs` `CaptureLoop`, after `screen.Capture(...)` and `IsBlack(frame)` (≈ line 633) and before `Downscale` and `encoder.Encode` (≈ line 666–685). Applying it after `IsBlack` keeps the "dark display – wake it / virtual monitor" logic (lines 539–563) working on the real picture.

```
capture ─▶ IsBlack(raw) ─▶ ApplyPrivacy(frame) ─▶ Downscale ─▶ Encode ─▶ send
                              │
                              ├ User == Black       → send nothing (see §6.2)
                              ├ User == Blur        → BlurInPlace(frame)
                              ├ HiddenApps ≠ ∅      → MaskTracker.Apply(frame, monitor) (§7)
                              └ any error           → treat as Black (§1.1 rule 1)
```

The mask is applied to the full-size frame **before** `Downscale`. That way no hidden pixel is blended into an edge pixel. A change to the privacy state sets a new `_privacyDirty` flag. That flag forces the next frame to be sent as a key frame even when DXGI reports `Unchanged`. Otherwise a mask that was just turned on would only take effect after the screen next changes.

### 4.3 Protocol

Two new JSON messages and three feature flags go in `DesktopProtocol.cs`. Older agents don't advertise the flags, so RemoteHub hides the buttons. Older RemoteHub versions ignore the messages; §10 covers what they show instead.

```csharp
// DesktopFeatures
public const string PrivacyScreen = "privacyScreen";   // Phase 1 (only advertised on build ≥ 19041, see §5.2)
public const string UserPrivacy   = "userPrivacy";     // Phase 2
public const string HideApps      = "hideApps";        // Phase 3

// DesktopMessage
/// <summary>RemoteHub → agent: turn the privacy screen on or off (<see cref="DesktopPrivacyScreen"/>).</summary>
PrivacyScreen = 13,
/// <summary>Agent → RemoteHub: what is hidden right now (<see cref="DesktopPrivacy"/>); sent on every change.</summary>
Privacy = 14,

public sealed record DesktopPrivacyScreen(bool On, string Style /* "Black" | "Message" */, bool BlockLocalInput);

/// <param name="User">"None" | "Black" | "Blur" – what the person at the device hid.</param>
/// <param name="HiddenApps">How many apps are hidden (never which).</param>
/// <param name="Regions">Hidden rectangles in picture pixels (after scaling), for the viewer's "Hidden" labels.</param>
/// <param name="Screen">State of the admin's privacy screen: "Off" | "Black" | "Message"; "" if it couldn't be shown (then <paramref name="Reason"/>).</param>
public sealed record DesktopPrivacy(string User, int HiddenApps, DesktopRect[] Regions, string Screen, bool LocalInputBlocked, string Reason = "");
```

On the helper side, `HelperMessage.PrivacyScreen = 109` carries the admin's command (service → helper). `DesktopMessage.Privacy` is added to the forward list in `DesktopEndpoint.HelperLoop` (≈ line 610).

## 5. Phase 1 – Privacy screen (admin)

### 5.1 Admin UI

This goes in the agent desktop tab header ([AgentDesktopView.cs](../src/RemoteHub/Views/AgentDesktopView.cs) `Header()`), directly after the Ctrl+Alt+Del button. It is built like the other tool buttons.

```
right (StackPanel, horizontal)
├ _viewOnlyBadge
├ _privacyBadge (new)    TextBlock "Privacy screen on" – same look as _viewOnlyBadge
├ _monitors · _quality · _infoButton · _keysButton · _fit · _winKey
├ _cad                    "Send Ctrl+Alt+Del"
└ _privacy (new)          RemoteToolChrome.Tool(SymbolRegular.EyeOff24, …)
     Appearance Primary while on (like _infoButton / _keysButton)
     click → ContextMenu (RemoteToolChrome.Item, like the tab context menu)
        ├ Black screen
        ├ Black screen with message
        ├ ─────────
        ├ ☐ Also block mouse and keyboard at the device   (only if the policy allows, §8)
        ├ ─────────
        └ Turn off privacy screen                          (only while on)
```

- **Visibility:** it follows `_cad`: `live && !ViewOnly`. It also needs `Features ∋ privacyScreen` and the `PrivacyScreen` right from `DesktopHello.Rights`.
- **Tooltip:** off: "Black out the device's screen – the person at the device can't see what you do". On: "The device's screen is blacked out (click to change or turn off)". When the agent sent `Screen == ""`, the tooltip is `Reason`.
- **Toolbar placement:** the privacy item is added in the same order to the detached-window tool list (`MainWindow.xaml.cs:376-394`) and the context menu (`:808-811`).

### 5.2 How the screen is hidden

Turning the monitor off (`SC_MONITORPOWER`) is not an option: it stops DXGI capture, and `KeepDisplayOn` actively prevents it. Instead, the helper covers every monitor with its own window that is **excluded from capture**. The banner already does this ([HelperWindows.cs:406](../src/RemoteHub.Agent/Desktop/HelperWindows.cs)).

- **New kind:** `FluentKind.Overlay`, one window per monitor (`EnumDisplayMonitors`) covering the whole monitor rect.
  - Styles: `WS_POPUP`, `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, no rounded corners, no border.
  - `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`. Afterwards the helper reads it back with `GetWindowDisplayAffinity`. If it doesn't read `0x11`, the overlay is destroyed **before it is shown** and the agent reports `Screen = ""` with a reason. A window that isn't excluded would black out the admin's picture as well.
  - Build < 19041: the feature is not advertised (§2).
- **Black:** the window is filled with `#000000`. No text; the cursor stays visible (the overlay doesn't handle `WM_SETCURSOR`, see §14).
- **Message:** a navy background (`Theme.OverlayBackground`) with a soft glow (`Theme.OverlayGlow`) behind the RemoteHub logo, and one centred block: the logo (220 px, drawn as vector shapes so it is sharp at any size; smaller on a small screen, so everything fits), the letter-spaced eyebrow "PRIVACY MODE ACTIVE" (`Theme.AccentLight`), the heading "Remote Support Session in Progress", and the body "Your screen is temporarily hidden while an authorized administrator assists your device." In a session automatic sign-in created for the admin (no banner there), the body is "DOMAIN\user is signed in remotely. The screen stays hidden while they work." Below it a short accent line and, next to a lock, "Secure, monitored session • Please do not power off your device".
- **Staying on top:** other topmost windows (Task Manager, a topmost app) can come above the overlay. The helper re-asserts `HWND_TOPMOST` every 500 ms and on `EVENT_SYSTEM_FOREGROUND`. The banner is re-asserted **above** the overlays (rule 2).
- **Monitor changes:** on `WM_DISPLAYCHANGE` the overlays are rebuilt. This includes the virtual monitor ([third-party/VirtualDisplayDriver](../third-party/VirtualDisplayDriver)).
- **The admin's picture is unchanged:** excluded windows aren't in DXGI or GDI captures. The admin sees the desktop. The local cursor shape is still sent separately, as it is today.

### 5.3 Flow

```
RemoteHub                DesktopEndpoint (service)                    Helper
  PrivacyScreen{On} ───▶ right PrivacyScreen? !viewOnly? policy? ───▶ HelperMessage.PrivacyScreen
                         audit (§9)                                   build overlays, verify affinity
                                                                     ◀── Privacy{Screen:"Black", …}
  ◀── Privacy  ◀──────── forward
```

The service refuses the command when the right or policy is missing. In that case it sends a `Privacy` message with `Reason` and no change, and the helper is never asked.

### 5.4 Blocking local input (optional)

`BlockInput` can't be used: it also blocks the helper's own `SendInput`. Instead:

- **Hooks:** the helper's UI thread (which already runs a message loop) installs `WH_KEYBOARD_LL` and `WH_MOUSE_LL`.
- **Tagging:** every event the agent injects is tagged with `dwExtraInfo = 0x52484131` ("RHA1") in [InputInjector.cs](../src/RemoteHub.Agent/Desktop/InputInjector.cs). Injected input is not tagged today. The hooks drop every event that doesn't carry the tag. Checking only `LLKHF_INJECTED` isn't enough, because on-screen keyboards and other tools inject too.
- **Fast callbacks:** the callback only compares a few fields and returns. Windows removes LL hooks that take longer than `LowLevelHooksTimeout`.
- **What stays local:**
  - Ctrl+Alt+Del. Windows handles it and it can't be hooked, so local input can never be locked completely. While input is blocked, the screen it opens offers only Task Manager and Cancel (§5.9).
  - Mouse clicks inside the banner's rect, so **Disconnect** keeps working (§5.5).
- **Visible to the user:** with Message style, the overlay adds the line "Mouse and keyboard are blocked." under the body.

### 5.5 Disconnect always works

- The banner stays above the overlays (§5.2) and does not collapse while the privacy screen is on. A collapsed pill is easy to miss on a black screen.
- With local input blocked, the mouse hook lets local clicks in the banner's rect through. The cursor stays visible on the overlay (§14). While input is blocked, the banner's heading ends in " · mouse and keyboard blocked" and its subtitle reads "Click "Disconnect" to end".
- Ctrl+Alt+Del → Lock or Sign out ends the admin's view of the user desktop, as it does today.

### 5.6 What the privacy screen does not cover

- **The secure desktop** (UAC, lock screen, Ctrl+Alt+Del screen) is a separate desktop (`Winlogon`), so the helper's windows on `Default` are not on it. If the admin triggers UAC or Ctrl+Alt+Del, the person at the device sees that screen. RemoteHub shows a one-time notice in the tab: "The privacy screen doesn't cover UAC prompts and the lock screen". The overlays come back when the default desktop is the input desktop again (`DesktopThread` already follows it). §5.7 tests whether this gap can be closed.
- **Monitors added during the session** are covered within one `WM_DISPLAYCHANGE`. For a moment they are not covered.

### 5.7 Lab test: covering the secure desktop

This is a time-boxed test (1 day) that runs **before** Phase 1 is built. It decides whether the privacy screen also covers UAC prompts, the Ctrl+Alt+Del screen and the lock screen. Its result replaces the first bullet of §5.6, either with "covered" or with "not covered – tested".

**Setup:** a throw-away test build of the helper. On a secure-desktop switch it opens the input desktop (`OpenInputDesktop`, as `DesktopThread` does), creates an overlay window there on a dedicated thread (`SetThreadDesktop` before the first window), and applies `WDA_EXCLUDEFROMCAPTURE`. It runs on the lab VMs from §13 with build ≥ 19041, plus 1809 as the negative control.

| # | Check | Pass if |
|---|---|---|
| L1 | Window on `Winlogon` | The helper (LocalSystem) can create and show a topmost window on the `Winlogon` desktop for a UAC prompt, the Ctrl+Alt+Del screen and the lock screen. |
| L2 | Covers locally | The device's console (Hyper-V console screenshot) shows the overlay, not the prompt, for all three screens. |
| L3 | Excluded from capture | The **received** picture in RemoteHub shows the prompt, not the overlay or a black area. This is checked for the GDI path the agent uses on the secure desktop, and for DXGI if it is available there. |
| L4 | Stays on top | The overlay stays above `consent.exe` / `LogonUI.exe` for 60 s, including after the admin clicks into the prompt and after a second UAC prompt. |
| L5 | Admin can act | The admin answers the UAC prompt and enters a PIN or password on the lock screen through RemoteHub while the overlay is up. |
| L6 | Fails safe | Killing the helper while the overlay is up gives the console back the normal prompt within 2 s. The same holds when the session ends, the connection drops, or the helper is stopped by its job object. Nothing stays behind on `Winlogon`. |
| L7 | Switching back | After the prompt closes, the overlay on `Default` is back and the one on `Winlogon` is gone. No flash of the user desktop shows on the console longer than one frame (screen recording of the VM console). |
| L8 | Old build (1809) | Exclusion is not available, so the test build must refuse to show the overlay (affinity read-back fails). A black picture for the admin fails the case. |

**Decision:**
- **All of L1–L7 pass on every build ≥ 19041:** the secure-desktop overlay goes into Phase 1, with the same affinity read-back as §5.2 and the same lifetime rules (§1.1 rule 5). It is added to the security review that [agent-desktop-draft.md](agent-desktop-draft.md) §4.4 asks for, because it draws over Windows' own security UI.
- **Any of L1–L7 fails:** §5.6 stays as written, and the result (which check failed and on which build) is recorded here.
- **L8 fails:** that is a bug in the test build, not a reason to stop. It must be fixed before any result counts.

Not in scope: `PromptOnSecureDesktop = 0`, which moves UAC to the normal desktop. The agent never changes security settings (same rule as for RDP settings).

### 5.8 Spike: shell surfaces above the overlay (UIAccess)

**Problem:** an ordinary `HWND_TOPMOST` window sits in the desktop z-band. Start, Search, Quick Settings / Action Center, Notification Center, toasts, taskbar thumbnails and the taskbar itself are drawn in the shell's own bands above it. While the admin works, those surfaces showed through the privacy screen at the device.

**Rejected:** an isolated desktop (`CreateDesktop` / `SwitchDesktop`). DXGI duplication, GDI capture and `SendInput` all work only on the input desktop, and `DesktopThread` follows the input desktop. Switching away would blank the admin's picture or break their control.

**Change:** the helper's token gets `TokenUIAccess = 1` (`SetTokenInformation(token, 26, 1)`) next to `TokenSessionId` in `WindowsHelperLauncher`. The service is LocalSystem with `SeTcbPrivilege`, so no manifest and no signing is needed. The overlays are created `WS_EX_TOPMOST`, so they and the banner land in `ZBID_UIACCESS` (band 2). Magnifier and the on-screen keyboard draw in the same band, above the immersive shell bands. Switch: `HKLM\SOFTWARE\Policies\RemoteHub\Agent` `HelperUIAccess` = 0 turns it off (DWORD; on when missing).

**Test:** lab case D-SHELL ([D-SHELL.Tests.ps1](../lab/tests/D-SHELL.Tests.ps1)). Privacy screen Black with local input blocked, then the same run with UIAccess off and on. The admin opens each surface through RemoteHub (injected keys). The toast is raised in the user's session, and the thumbnail is opened with Win+T and arrow keys. Per surface the test measures:
- **Console:** the share of bright pixels in the Hyper-V console screenshot below the banner strip. Covered means < 0.5 %.
- **Picture:** the share of pixels that changed in the admin's received frame. Shown means > 0.5 %.

The test also reads the window z-order, band and display affinity in the user's session. Last, the device's user clicks Disconnect with the console mouse.

| Surface | W11 26H2 console, off → on | W11 26H2 admin picture (on) | Server 2025 console, off → on | Server 2025 admin picture (on) |
|---|---|---|---|---|
| Start (Win) | 63.9 % → 0 % — **pass** | shown 11.2 % — **pass** | 63.9 % → 0 % — **pass** | shown 14.8 % — **pass** |
| Search (Win+S) | 75.7 % → 0 % — **pass** | shown 14.0 % — **pass** | 75.8 % → 0 % — **pass** | shown 20.3 % — **pass** |
| Quick Settings / Action Center (Win+A) | 24.2 % → 0 % — **pass** | shown 7.1 % — **pass** | 24.2 % → 0 % — **pass** | shown 9.8 % — **pass** |
| Notification Center (Win+N) | 16.0 % → 0 % — **pass** | shown 11.6 % — **pass** | 16.0 % → 0 % — **pass** | shown 17.8 % — **pass** |
| Taskbar thumbnail (Win+T) | 4.1 % → 0 % — **pass** | shown 1.7 % — **pass** | 4.0 % → 0 % — **pass** | shown 2.5 % — **pass** |
| Toast | not raised¹ → 0 % — **pass** | shown 4.6 % — **pass** | not raised¹ → 0 % — **pass** | shown 5.6 % — **pass** |
| Task View (Win+Tab) | n/a² | n/a² | n/a² | n/a² |
| Alt+Tab | n/a² | n/a² | n/a² | n/a² |

¹ With UIAccess off, Windows didn't show the toast at all (not on the console, not in the picture). With UIAccess on it showed, and only in the admin's picture.
² SnipGuard (`SnipGuard.cs`) drops Win+Tab and Alt+Tab while the privacy screen is up, so neither opens in either mode. The console stayed covered.

Other checks, both builds, UIAccess on:

| Check | W11 26H2 | Server 2025 |
|---|---|---|
| Helper token `TokenUIAccess` = 1 (0 with the switch off) | **pass** | **pass** |
| Overlays read back `WDA_EXCLUDEFROMCAPTURE` (0x11), band 2 | **pass** | **pass** |
| Banner above the overlays (z-order, band 2) | **pass** | **pass** |
| Local Disconnect click through the input block ends the session ("The user at the device ended the session.") | **pass** | **pass** |
| Agent log: no UIPI / access-denied / error lines | **pass** | **pass** |

Not tested: Windows 10 22H2 and Windows 11 23H2/24H2. The lab has no VMs for them (only W11 26H2 and Server 2025).

**Regression (UIAccess on, W11 26H2):** the helper's token now has UIAccess for every desktop connection, not only while the privacy screen is up. The suites that start the helper ran with the spike build. Each failing suite then ran again with a control build, identical except UIAccess is off. The run was stopped early, so the suites from D-CON onwards and all of Server 2025 were not run.

| Suite | UIAccess on | Control (off) | Verdict |
|---|---|---|---|
| D-PRIV / D-HIDE | D-HIDE-01, D-HIDE-02 fail | Same two fail | Not caused by UIAccess |
| D-BAN / D-VIEW | D-BAN-01/04/05/07, D-VIEW-02 fail (in-session OCR of the banner reads nothing) | D-BAN-07, D-VIEW-02 fail; the rest didn't run (`RhPerson` gave no answer) | 07 / VIEW-02 not caused by UIAccess; 01/04/05 not compared. D-SHELL checks the banner and Disconnect directly: pass |
| D-CLIP | 12 of 18 fail | Cancelled | Not settled in the lab; clipboard reported working in a manual test on a device |
| D-IN | D-IN-01 (text, one character differs), D-IN-03 (wheel) fail | Not run | Not settled in the lab; mouse wheel reported working in a manual test on a device |

**Manual test (owner, 2026-10-15):** the spike build on a device. Start and the other shell surfaces stay off the device's screen while the privacy screen is on. Clipboard and mouse wheel were reported working.

**Result:** the change is kept on `spike/uiaccess-overlay`, on by default. Before it ships:
- the remaining suites on both VMs, D-CLIP and D-IN against the control build;
- Windows 10 22H2 and Windows 11 23H2/24H2;
- a security review, because UIAccess also lets the helper drive windows of higher integrity (it is LocalSystem already).

### 5.9 The Ctrl+Alt+Del screen while input is blocked

Ctrl+Alt+Del can't be blocked (§5.4), and the screen it opens is on the secure desktop, which the overlay doesn't cover (§5.6). What the agent controls is what that screen offers. While the privacy screen blocks the device's mouse and keyboard, it offers only **Task Manager** and **Cancel**. Task Manager stays on purpose: it opens behind the overlay, and with input blocked it can't be used anyway. That covers the admin's privacy screen with "block local input" and the one automatic sign-in puts up, which always blocks input (§9.1).

| Hidden | Policy value | Where |
|---|---|---|
| Lock (also Win+L) | `DisableLockWorkstation` = 1 | session user: `HKU\<sid>\Software\Microsoft\Windows\CurrentVersion\Policies\System` |
| Change a password | `DisableChangePassword` = 1 | same |
| Sign out | `NoLogoff` = 1 | session user: `…\Policies\Explorer` |
| Power button | `NoClose` = 1 | same |
| Switch user | `HideFastUserSwitching` = 1 | machine: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` (Windows has no per-user value) |

Not hidden: the network and accessibility buttons in the corner. Windows has no policy for them on this screen.

**Putting them back** (`SasActions.cs`, run by the helper):
- **What gets recorded:** before setting a value, the helper writes what was there (the value, or "absent") to `sas-actions-<sid>.txt` next to agent.json. The machine value goes to `sas-actions-machine.txt`. A value that isn't a DWORD is left alone.
- **When:**
  - The block ends: the admin unblocks input or turns the privacy screen off.
  - The overlays come down (the monitors changed and they couldn't be rebuilt).
  - The helper ends with the connection.
- **Exactly as before:** an existing value comes back as it was, and an absent one is deleted.
- **Several connections:** each helper that hides them registers as an owner (`sas-owner-<sid>-<pid>-<id>.txt`). A user's values come back only when no other live owner for that user remains, and Switch user only when no live owner at all remains. An owner whose process is gone doesn't count.
- **One at a time:** a system mutex orders the helpers and the service. If it can't be taken within 5 s, nothing is changed and the step is logged.
- **Failures:** the helper registers as owner before it changes anything. If hiding fails part-way, it puts back its own part. If putting back fails, it stays the helper's job, and the next unblock or the helper's end tries again.
- **A helper that was killed:**
  - The next helper in that session puts its leftovers back as soon as the user's hive is loaded, even when that connection never blocks input. Anything another live helper still hides is left alone.
  - A helper that hides them again keeps the original record.
  - The service puts everything back when it starts. No helper runs then, so all records are leftovers.
  - A user's hive that isn't loaded at that moment (the user signed out) keeps its record until that user's next connection.
- **Log:** each step writes a line ("Ctrl+Alt+Del screen: …"). There is no audit event.

**Timing:** the user's hive must be loaded. On the auto sign-in screen the helper starts before the profile is, so it tries again every 500 ms (the overlay's keep-on-top timer) until it can.

**Lab:** D-CADHIDE ([D-CADHIDE.Tests.ps1](../lab/tests/D-CADHIDE.Tests.ps1)). Ctrl+Alt+Del is pressed at the Hyper-V console; the screen is read by OCR and the five values (and `DisableTaskMgr`, which must stay unset) are read from the registry.

| Case | W11 26H2 | Server 2025 |
|---|---|---|
| 01 Control, before any connection: values not set, the screen offers Lock and Task Manager | **pass** | **pass** |
| 02 Admin's privacy screen with input blocked: the five set, `DisableTaskMgr` not; the screen offers only Task Manager | **pass** | **pass** |
| 03 Input unblocked while still connected: put back; blocked again: hidden again | **pass** | **pass** |
| 04 Connection ends: put back as they were, no record left | **pass** | **pass** |
| 05 Service and helper killed while hidden: put back when the service starts (log: "actions an earlier helper hid are back") | **pass** | **pass** |
| 06 Automatic sign-in: hidden in the admin's new session, the screen offers only Task Manager; put back after | **pass** | **pass** |

Also seen: a helper killed on its own is started again by the service, and the new one keeps the original record and puts the values back when the connection ends.

## 6. Phase 2 – Hide my screen (person at the device)

### 6.1 Device UI

The banner ([HelperWindows.cs](../src/RemoteHub.Agent/Desktop/HelperWindows.cs) `ShowBanner`) gets more buttons. `FluentWindow` already supports a `Buttons` array and `Clicked(id)`. `BannerSize()` must sum all buttons instead of using `Buttons[0]` only.

```
Banner (FluentKind.Banner)
├ Heading     "Your screen is being viewed and controlled by CONTOSO\admin"
├ Subtitle    "RemoteHub"
├ [Hide screen]  Secondary, id 4          ← new
├ [Hide apps…]   Secondary, id 5          ← new, Phase 3
└ [Disconnect]   Danger,    id 3

Collapsed pill: ShortHeading + [Disconnect] only (as today); it opens on hover.
```

**Hide screen** opens a dialog (`FluentKind.Dialog`, same layout as the consent dialog):

```
Dialog
├ Subtitle  "Privacy"
├ Heading   "Hide your screen from CONTOSO\admin"
├ Body      "Black out: they see nothing until you show it again.
│            Blur: they see roughly what's on screen but can't read it.
│            They can't use mouse or keyboard while your screen is hidden."
└ Buttons   [Black out] Primary · [Blur] Secondary · [Cancel] Secondary   (Esc = Cancel)
```

**While hidden:**
- The banner turns into its own state:
  - heading "Your screen is hidden from CONTOSO\admin" ("… is blurred for …")
  - border in the accent colour instead of red
  - buttons [Show screen] Primary · [Disconnect] Danger
- It does not collapse while the screen is hidden, so the way back stays visible.

### 6.2 What the admin gets

| User state | Video | Cursor | Admin input |
|---|---|---|---|
| **Black** | One black key frame, then no frames until shown again (this also saves bandwidth) | No position or shape updates | All dropped in the helper (`IInputSink.Allowed` → false), including Ctrl+Alt+Del |
| **Blur** | Live, blurred on the device (below) | Sent | Dropped as well (see §12, Q3) |

**Blur** has to make text unreadable and must not be reversible. A light Gaussian blur or pixelation of text can be partly undone, so neither is enough on its own.
- **Method:** shrink the frame to 1/32 with an area average, then scale it back up bilinearly. That's ≈ 60×34 samples for 1920×1080. The cost is negligible next to encoding.
- **Rate:** blurred frames are sent at most 2 per second, so motion doesn't help reconstruct the content.

**Admin view:** the existing `_pausedPanel` pattern ([AgentDesktopView.cs:341](../src/RemoteHub/Views/AgentDesktopView.cs)) is generalised. The icon and heading come from the state, with the same dark card and white text:

```
_pausedPanel (Border, centred card)
├ SymbolIcon   EyeOff24            (PlugDisconnected24 for the existing "disconnected" case)
├ Heading      "The user has hidden their screen" | "The user has blurred their screen"
└ Text         "You'll see it again when they click "Show screen". Mouse and keyboard are paused."
```

- **Black:** the card covers the last frame, which is the black key frame anyway.
- **Blur:** the card sits at the bottom edge instead of the centre, so the blurred picture stays visible.
- **Toolbar:** `_cad`, `_winKey` and the keys toggle are disabled (not hidden) while the screen is hidden. Their tooltip says why.

### 6.3 Always available

The person at the device can always hide the screen. There is no right and no admin override. A policy can only add to this (§8). An admin who must see the screen can end the session and talk to the user.

### 6.4 Input while hidden

The helper drops everything in the input sink. The service does not have to know. That keeps a single place of truth on the device. A modified RemoteHub that keeps sending input gets nothing through.

### 6.5 The secure desktop while the screen is hidden

- **The picture stays hidden.** Black and Blur are applied in the frame loop (§4.2), so they also cover frames from the secure desktop. That includes the GDI path, so UAC prompts, the lock screen and the Ctrl+Alt+Del screen stay hidden or blurred for the admin. The lock screen ends the admin's view of the user desktop anyway, as it does today.
- **The banner is not there.** The banner lives on `Default`, so while a UAC prompt is up the person at the device has no "Show screen" or "Disconnect". They answer the prompt locally, and the banner is back once it closes.

### 6.6 Lab test: the secure desktop while the screen is hidden

This runs together with §5.7 (same test build, same VMs, half a day extra) and **before** Phase 2 is built.

| # | Check | Pass if |
|---|---|---|
| U1 | Black over UAC | With the screen hidden (Black), a UAC prompt raised by the user shows nothing new in the **received** stream. No frame and no cursor update arrive. |
| U2 | Blur over UAC | With the screen blurred, frames from the GDI path on the secure desktop are blurred the same way as on `Default`. The text of the prompt cannot be read in the recorded stream. |
| U3 | Switching | Ten fast switches between `Default` and `Winlogon` while hidden never let an unhidden frame through. Every frame of the recorded stream is checked. |
| U4 | Banner on `Winlogon` | The test build shows the banner with "Show screen" and "Disconnect" on the secure desktop. It must be clickable locally and excluded from capture (same checks as L1–L4, L6). |
| U5 | Input | With the screen hidden, admin input never reaches the UAC prompt, including Ctrl+Alt+Del from RemoteHub. |

**Decision:**
- **U1–U3, U5** are pass/fail for Phase 2 itself. A failure is a bug in the frame hook and blocks the phase.
- **U4 decides whether the banner gets a copy on the secure desktop.** If it passes (and the security review agrees, see §5.7), the person at the device can also show the screen or disconnect while a UAC prompt is up. If it fails, §6.5 stays as written.

## 7. Phase 3 – Hide apps

### 7.1 Device UI

The picker is a new window kind, `FluentKind.Picker`. It has the same chrome as the dialog (header 56, padding 24, footer 64), with a list between the body and the footer.

```
Picker (FluentKind.Picker, 480 wide, list height up to 8 rows then scrolls)
├ Subtitle  "Privacy"
├ Heading   "Hide apps from CONTOSO\admin"
├ Body      "Windows of the apps you select are blacked out for them – also when you open,
│            move or restore them later."
├ List (new, hand-drawn GDI+)
│   row 40 px: [☐ checkbox] [app icon 20] App name  ·  "2 windows" (small, secondary)
│   hover row highlight, click anywhere on the row toggles, Space toggles the focused row,
│   Up/Down move focus, Tab moves to the buttons
│   empty: "No apps with open windows"
└ Buttons   [Hide selected] Primary (disabled while nothing changed) · [Cancel] Secondary
```

The list is a new component with no equivalent in the helper today. It is drawn with the existing `Theme` colours, the existing `_font` / `_small` fonts and the accent colour for the checked state. It lives in [HelperWindows.cs](../src/RemoteHub.Agent/Desktop/HelperWindows.cs) next to `FluentWindow` and is not a separate component library.

**Where it opens:**
- **Banner → Hide apps…:** shows the current selection already checked.
- **Consent question (Ask):** a third button, [Allow and hide apps…] Secondary, sits between Allow and Deny.
  - Opening the picker **stops** the countdown. "Hide selected" in that context is labelled **Allow** and counts as Allow with the selection.
  - Cancel returns to the question with the countdown restarted at its full length.
- **PIN dialog:** a button [Hide apps…] Secondary next to Deny. The picker applies the selection while the PIN is still being read out, so it is in place before the first frame.
- Because the selection is in place before `HelperMessage.Begin`, the first frame the admin receives is already masked.

**While apps are hidden:** the banner heading gets a suffix, " · 2 apps hidden", and the pill shows the same.

### 7.2 What an "app" is

The selection holds **app identities**, not windows, so new windows of the same app are hidden too:

| Kind | Identity | Source |
|---|---|---|
| Desktop app | Full image path, case-insensitive | `GetWindowThreadProcessId` → `QueryFullProcessImageNameW` (the helper is LocalSystem, so elevated apps work as well) |
| Packaged / UWP app | AppUserModelID | `ApplicationFrameWindow` → child `Windows.UI.Core.CoreWindow` → its PID → `GetApplicationUserModelId` |
| Explorer | Only File Explorer windows (`CabinetWClass`), never the shell | class + image path `explorer.exe` |

**Name and icon:** the name comes from `FileDescription` in the version resource (fallback: file name), or the package display name. The icon comes from `WM_GETICON` / `GCLP_HICON`, with `SHGetFileInfo` as a fallback.

**Rows offered:** apps with at least one window that would appear in Alt+Tab:
- visible, not cloaked
- no `WS_EX_TOOLWINDOW`
- unowned, or owned with `WS_EX_APPWINDOW`

RemoteHub's own windows are never offered.

**Windows masked:** for a selected app, **every** top-level window of its processes is masked, including owned popups, menus (`#32768`), tooltips and splash screens. It's not only the Alt+Tab ones.

### 7.3 Computing the mask

`MaskTracker` keeps a window list. It is updated on the UI thread and read by the capture thread through an immutable snapshot.

- **Updates:** `SetWinEventHook(WINEVENT_OUTOFCONTEXT)` for:
  - `OBJECT_CREATE` / `DESTROY` / `SHOW` / `HIDE` / `LOCATIONCHANGE` / `CLOAKED` / `UNCLOAKED`
  - `SYSTEM_MINIMIZESTART` / `END`, `MOVESIZESTART` / `END`, `FOREGROUND`
  - **plus** a full `EnumWindows` rescan every second as a backstop
- **Per window:** `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, which includes the shadow-less real bounds. Each rect is inflated by 4 px. Minimized and cloaked windows contribute nothing.
- **Covering windows:** the helper walks the z-order from the top. An opaque, non-layered window of a non-hidden app **above** a hidden window removes its rect from the mask, so a hidden app behind a visible one doesn't black out the visible one. Layered or transparent windows never remove anything. When in doubt, the region stays masked.
- **Per frame:** the region is intersected with the captured monitor (or with the virtual screen for "all monitors"), translated to frame pixels, and filled.

### 7.4 Timing

The capture and the window geometry are not taken at the same instant. Rules:

1. **Union over time:** the mask applied to a frame is the union of the masks from the **current and previous** snapshot. A window that just moved is masked at both places for one frame.
2. **Hold during animations:** after `MINIMIZEEND` (restore), `SHOW`, `UNCLOAKED` or `MOVESIZEEND` of a hidden window, **no new frame is sent for 400 ms**. The viewer keeps the last clean frame. The restore and open animations are drawn by DWM from the window's content, between taskbar and final rect, so a rect mask can't follow them.
3. **Snapshot too old:** if the snapshot is older than 1.5 s (UI thread stuck), frames are held (§1.1 rule 1).
4. **Selection change:** a change to the selection sets `_privacyDirty` and forces a key frame (§4.2).

### 7.5 Shell surfaces that show app content

While **any** app is hidden, these Explorer/DWM surfaces are masked completely while visible. They show live thumbnails or text of any app, and the helper can't tell which part belongs to which app:

| Surface | Detected by |
|---|---|
| Taskbar hover preview | `TaskListThumbnailWnd` |
| Alt+Tab, Task View, Snap Assist | `XamlExplorerHostIslandWindow`, `MultitaskingViewFrame`, `ForegroundStaging` (class names checked per Windows build in the lab) |
| Toast notifications and Notification Center | `Windows.UI.Core.CoreWindow` owned by `ShellExperienceHost.exe` |

The class list is data in the helper, so a new Windows build only needs a new entry. Anything not in it is a residual risk (§11).

### 7.6 Admin input

- **Mouse:** a button, wheel or drag event whose position falls in the current mask is dropped. Moves are passed through so the cursor doesn't stick.
- **Keyboard:** dropped while the **foreground** window belongs to a hidden app. Otherwise the admin would be typing blind into it, for example into a password manager.
- **Admin view:** a dropped click briefly shows "This area is hidden by the user" next to the cursor. The helper reports a drop at most once per second through `Privacy.Reason`.

### 7.7 Clipboard

When the device's clipboard changes, the helper checks the owner: `GetClipboardOwner` → PID, with the foreground window's app as a fallback. If it is a hidden app, the change is **not** sent to RemoteHub. This applies to text, images and files.

Pasting from RemoteHub into a hidden app still works. It's the user's choice, and nothing leaves the device. The name of the program that pastes is not passed on for hidden apps.

### 7.8 Admin view

- Hidden regions arrive as `DesktopPrivacy.Regions`. The viewer draws each one as a hatched rectangle in `SessionAreaBrush` with the centred label "Hidden by the user" (a `Canvas` layer above `_screen`, scaled like `_cursorLayer`).
- The header gets `_privacyBadge` with the text "2 apps hidden by the user". There are no app names, and the regions carry no names.

## 8. Rights and policy

| Item | Kind | Default | Meaning |
|---|---|---|---|
| `AgentRight.PrivacyScreen` | Right (subkey `Rights`) | `config.AllowedGroups` (same as `Desktop`) | May turn on the privacy screen (Phase 1). Also needs `Desktop` (not view-only). |
| `PrivacyScreen` | DWORD | 2 (see §14, Q2) | 0 – privacy screen off; 1 – allowed, local input stays usable; 2 – allowed, including blocking local input |
| `PrivacyScreenMessage` | REG_SZ | (built-in text) | Replaces the heading on the Message overlay, e.g. "Contoso IT is working on this PC" |
| `HiddenApps` | REG_MULTI_SZ | – | Image paths or AUMIDs that are **always** hidden for every connection, e.g. a password manager. The user can't un-hide them, and they are shown checked and disabled in the picker. |

The person at the device can always hide the screen and pick apps (§6.3). There is deliberately no policy to turn that off (but see §12, Q5).

`AgentPolicy` doc comment, `From()` and `Refused()` get the new values. The ADMX template gets matching entries.

## 9. Audit

These events go through `o.Log.Audit` in the same format as the existing desktop events:

| Event | When | Text |
|---|---|---|
| `DesktopPrivacyScreen` | Admin turns it on or off | "Desktop: CONTOSO\admin from 10.0.0.5 turned the privacy screen on (black, local input blocked) in session 2" |
| `DesktopUserHid` | User hides or shows the screen, or changes hidden apps | "Desktop: the user in session 2 hid their screen (blur)" / "… hid 2 apps" – **count only, no app names** |
| `DesktopPrivacyRefused` | Command refused (right, policy, OS build, affinity check) | with reason |

## 10. Compatibility

| Agent | RemoteHub | Behaviour |
|---|---|---|
| new | old | The user can still hide the screen and apps, since all hiding happens on the device. The old viewer shows a frozen frame for Black, or a blurred or masked picture, with no explanation. Its input is dropped. Acceptable: nothing leaks. |
| old | new | No feature flags, so no privacy button and no panels. |
| new, build < 19041 | new | `privacyScreen` is not advertised. The button is shown disabled with the tooltip "Needs Windows 10 version 2004 or later on the device". Phases 2 and 3 work. |

## 11. Residual risks

- **Unknown shell surfaces** (§7.5) can show hidden app content until their class is added. Lab tests cover Windows 10 22H2, 11 23H2/24H2 and Server 2022/2025.
- **An app that draws into another app's window** (e.g. some plugins or embedded browsers) is masked only if its host is hidden.
- **The secure desktop** shows the user what the admin does there (§5.6), unless the lab test in §5.7 closes the gap. The admin sees UAC prompts raised by hidden apps (the app name only).
- **Timing:** rules §7.4 cover move, restore and open. Remaining exposure is bounded by one capture interval at the edge of a fast-moving window, inside the 4 px inflation.

## 12. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | Privacy screen style: black only, or also "with message"? | Both, chosen per use (menu §5.1). Default: with message. |
| Q2 | Block local input: default on or off? | Off. It's an extra checkbox and only allowed with policy value 2. **Changed: on by default (§14, `PrivacyScreen` default 2).** |
| Q3 | Blur: may the admin still control the device? | No, input is paused (safer: no clicking into something they can't read). |
| Q4 | Remember the hidden apps for the next connection? | Not in v1. Later: per user, in `HKCU` of the session's user, written by the helper on their behalf. |
| Q5 | May IT turn off the user's privacy controls by policy (kiosk or unattended support)? | No in v1. The user asked for "always". Revisit if a customer needs it. |
| Q6 | A hotkey for "Hide screen" on the device? | Not in v1, because global hotkeys clash with apps. The banner is always there. |

## 13. Effort and tests

| Phase | Agent | RemoteHub | Lab | Rough effort |
|---|---|---|---|---|
| Lab test (before 1 and 2) | Throw-away test build: overlay and banner on `Winlogon` (§5.7, §6.6) | – | Every VM with build ≥ 19041, plus 1809 as the negative control | 1.5 days |
| 2 – Hide my screen | Privacy state, frame hook, blur, banner buttons, dialog, input gate | `Privacy` message, paused panel generalised | Win 10 1809, 22H2, 11, Server 2025 | 3–4 days |
| 1 – Privacy screen | Overlay kind, affinity check, topmost, LL hooks, input tagging | Toolbar button, menu, badge, detached tools | + multi-monitor, virtual monitor, UAC, Ctrl+Alt+Del | 4–6 days |
| 3 – Hide apps | App identity, picker + list, `MaskTracker`, timing, shell surfaces, input and clipboard rules | Region overlay, badge | + Store apps, elevated apps, Alt+Tab/Task View/thumbnails/toasts per build | 2–3 weeks |

Lab cases are added to [test-lab-cases.md](test-lab-cases.md) per phase. Each case checks the **received** picture, not the device's screen. Any hidden pixel in a recorded stream fails the case.

## 14. Implementation notes (Phases 1 and 2)

Where the code differs from the text above, or settles something the text left open:

- **The overlay lets clicks through.** It is a layered, transparent window (`WS_EX_LAYERED | WS_EX_TRANSPARENT`), so the admin's injected clicks reach the windows below it and the pointer shape stays the one of the window below. Without this, every admin click would land on the overlay.
- **Local input block:** the low-level hooks run on their own thread (`InputBlocker`), not on the helper's UI thread – a paste can keep the UI thread waiting, and Windows removes a hook that doesn't answer in time. Local pointer **moves** go through (the person must be able to reach *Disconnect*); clicks and the wheel go through only on the banner; local keys never. The pointer itself stays visible on the device, so the person there sees where the admin moves it.
- **Injected input is tagged** with `dwExtraInfo = "RHA1"` (also the display-wake nudge), so the block lets it through.
- **Not built:** the policy value `PrivacyScreenMessage` (§8) – the message text is fixed for now.
- **Dropped connection:** what the user hid and the admin's privacy screen are kept with the resume token, so a continued connection starts with both (the screen only if the account still has the right and policy).
- **Restarted helper** (sign-in, console moved): the user's choice carries over only when no one new is asked; the privacy screen is sent to the new helper after `Begin`.
- **Defaults taken from §12:** Q1 both styles, the detached window and tab menu toggle use "with message"; Q3 no admin input while blurred; Q4–Q6 as proposed.
- **Q2 changed (2026-10-13, after testing):** the device's mouse and keyboard are blocked with the privacy screen by default. Policy `PrivacyScreen` now defaults to 2; 1 keeps them usable. The admin can untick it in the menu (remembered per tab).

### 14.1 Phase 3 (hide apps)

- **Window tracking without hooks.** Instead of `SetWinEventHook` (§7.3), the capture thread walks the window list (`GetTopWindow`/`GetWindow`, topmost first) after every capture while apps are hidden. That keeps one thread, no event ordering to get wrong, and positions that are never older than the picture. Process identities are cached for 5 s.
- **Hold instead of per-event rules (§7.4).** Any change of a hidden app's windows (opened, moved, resized, minimized, restored, closed) holds the picture for 400 ms; the viewer keeps the last covered frame and gets the current one after. Dragging a hidden window therefore freezes the admin's picture until it stops.
- **Fail closed:** an error while finding the windows sends no picture (logged once); an unresolved Store app frame is covered while any Store app is hidden; the foreground or clipboard app counts as hidden when it can't be found out.
- **Covering windows:** only windows that are not layered, not click-through and whose display affinity reads as normal remove area from the mask; everything else leaves the hidden area covered.
- **Admin input:** button presses and the wheel inside a covered area are dropped, moves and releases go through (no stuck buttons); key presses are dropped while a hidden app has the keyboard, releases go through.
- **Policy `HiddenApps`** (REG_MULTI_SZ, paths or `aumid:` + AppUserModelID) is implemented: ticked and locked in the picker.
- **The app list never reaches RemoteHub.** The helper sends it to the service (`HelperMessage.HiddenApps`), which keeps it for a new helper and the resume token; RemoteHub gets the count and the covered regions.
- **Consent:** in the question the picker keeps a countdown (started again at full length): no answer in time denies, Cancel goes back to the question. The service's own last-resort timeout for Ask grew by 180 s for that.
- **Not built yet:** the hatched fill (the agent fills with the console's dark colour; RemoteHub draws a frame and "Hidden by the user" on areas of at least 180×60), and the picker's "Hide selected" button isn't disabled while nothing changed.

### 14.2 Snip guard (`SnipGuard.cs`)

The overlay is kept out of every capture – also out of a screen snip – and Windows shows a snip's frozen picture full-screen above every topmost window. A snip at the device would therefore show the person there what the overlay hides. So while any privacy screen is up (the admin's, or the one automatic sign-in puts up):

- **Keys dropped:** Print Screen (with any modifier), Win+Shift+S, Alt+Tab and Win+Tab (their thumbnails are drawn above the overlay, too) – both the device's own keyboard and the admin's injected keys. Only the press is dropped; its release goes through, so nothing stays held. Win+S and Shift+Tab still work.
- **Snipping tools closed:** `SnippingTool.exe`, `ScreenClippingHost.exe` and `ScreenSketch.exe` are ended as soon as they show a window (started from the Start menu, by a timer, …). The agent's log gets "Desktop: session N: Privacy screen: ScreenClippingHost.exe was closed – a snip would show the screen at the device"; there is no audit event.
- A low-level keyboard hook and an out-of-context WinEvent hook (`EVENT_OBJECT_SHOW`) on their own thread, like `InputBlocker`; they start with the overlays and stop when the overlays come down or the helper ends. If a hook can't be installed, that goes to the log.
