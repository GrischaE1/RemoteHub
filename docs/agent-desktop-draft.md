# RemoteHub Agent – Remote desktop without RDP (draft)

Status: Draft v3 – v1 is implemented (see `agent-spec.md` §12 and the README); brought in line with the code of 0.18.11 (2026-10-15). Automatic sign-in is implemented too, through the agent's credential provider ([credential-provider-design.md](credential-provider-design.md)); privacy mode is described in [privacy-mode-design.md](privacy-mode-design.md). What is planned but not built is listed in §7.
Owner: Grischa Ernst

## 1. Why

On client devices, RDP is often turned off by policy. Where the agent is installed, the agent gives screen and input instead, through its own encrypted connection and with the same trust model as the PowerShell tunnel.

This reverses the current non-goal "No screen or input" in `agent-spec.md` §2. That section has to change when this goes ahead.

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Network reach | Start with **direct connections** (LAN/VPN, as today). A **client–server model** (broker or relay) comes later, so every part must be designed so a server can sit in between without a protocol change. |
| 2 | Consent | **Before connecting**, while a user is signed in, the admin chooses: **Ask user** (Allow/Deny), **PIN** (the device shows a PIN to the user, the admin must enter it) or **No prompt**. **The agent decides which modes are allowed**, configured by registry policy so ADMX templates can follow. |
| 3 | Banner | **Mandatory** while a user is signed in: "Your screen is being viewed and controlled by DOMAIN\user" (or "viewed by") with Disconnect, visible while connected. The only exception is a session that automatic sign-in created for the admin: nobody is at the device there, so it gets the privacy screen instead of a banner (§4.4). |
| 4 | Secure desktop | **Always supported**: the sign-in screen, lock screen and UAC prompts can be seen and controlled. Who may use it is controlled by a right (see 9). |
| 5 | No user signed in | A setting chooses the behaviour: **show the sign-in/lock screen** (no prompt), or **sign in automatically** with the connection's credentials (the account that signed in to the agent), through a credential provider in the agent. |
| 6 | Multi-session devices | The **admin picks a session** (console or any user session). |
| 7 | Transport | **TLS/WebSocket over the existing agent connection first.** The frame protocol and signalling stay transport-neutral. WebRTC is added later as a second transport, mainly for the client–server phase. TCP stays as the fallback. |
| 8 | Scope v1 | Screen, mouse, keyboard, **clipboard** (text, images and files), **multiple monitors**, **view-only mode**. Privacy mode followed after v1 ([privacy-mode-design.md](privacy-mode-design.md)). Not built: audio, file drag and drop. |
| 9 | Rights | **Rights on the device side now, ready for RBAC later.** The agent checks a Windows group per right (`PowerShell`, `Desktop`, `DesktopViewOnly`, `SecureDesktop`, `ConsentBypass`, `PrivacyScreen`, `Tunnel`, `AutoSignIn`), set by registry policy. The rights are named so that a later RBAC server for multi-admin enterprise setups can issue the same rights. |
| 10 | RDP | **RDP stays.** Default: RDP for connections without an agent, the agent desktop for connections with an agent. The admin can always **fall back to plain RDP**. The agent never changes the device's RDP settings (it only keeps RDP *client* windows in the shown session drawing while connected, §4.4). |
| 11 | Quality | Default "**admin work**": 30 fps, quality 60, low latency, usable at about 1 Mbit/s VPN, software encoding. An **admin setting for frame rate and quality** goes up to "as fast and as good as possible" (60 fps, hardware encoder above 30 fps). Automatic adaptation (of the quality) stays on, so slow VPNs keep working. |

## 3. What RustDesk does (design reference only)

RustDesk is licensed AGPL-3.0, so we take ideas from it only. **No code is copied or linked.**

- **WebRTC is only a transport there:** one data channel (webrtc-rs) that carries their own video frames. The SDP offer and answer, plus trickle ICE candidates, go through their rendezvous server. WebRTC races a TCP or relay connection, and that connection is the fallback.
- **The device signs its DTLS fingerprint** with its identity key. For us, that fingerprint is sent inside the pinned TLS channel.
- **A SYSTEM service follows the active session** and starts a helper process in it, using a SYSTEM token so the secure desktop can be captured and controlled. Service and helper talk over a named pipe that checks which session the caller belongs to.
- **Capture:** DXGI Desktop Duplication with dirty rects, a GDI fallback, and `OpenInputDesktop` / `SetThreadDesktop` to switch desktops.
- **Adaptive frame rate and bitrate** from round-trip-time probes (`video_qos`). Frames are acknowledged so no backlog builds up.
- **Input** through `SendInput` with scancodes. Ctrl+Alt+Del goes through the service.
- **Consent:** a connection-manager process in the user session shows the accept prompt (password, click, or both).

## 4. Architecture

```
RemoteHub (operator PC)                             Device
┌─────────────────────────────┐  TLS, port 58732  ┌───────────────────────────────────────┐
│ AgentDesktopSession          │  WebSocket        │ RemoteHubAgent service (LocalSystem)   │
│  ├ control                   │ ────────────────▶ │  ├ sign-in, rights, policy (registry)  │
│  ├ H.264 decode (MF)         │  /v1/desktop      │  ├ consent (Ask / PIN / none), audit   │
│  └ input, clipboard, monitor │ ◀──────────────── │  ├ session list, helper launch         │
│                              │  later: WebRTC    │  └ auto sign-in broker ⇄ credential    │
│                              │  or server relay  │        │   provider (named pipe)       │
└─────────────────────────────┘                   │        │ stdin/stdout pipes, job object│
                                                  │  RemoteHubAgent.exe desktop           │
                                                  │   in the selected session              │
                                                  │   ├ Desktop Duplication / GDI          │
                                                  │   ├ H.264 encoder (MF, low latency)    │
                                                  │   ├ SendInput, clipboard, cursor       │
                                                  │   └ banner, consent/PIN window,        │
                                                  │     privacy screen                     │
                                                  └───────────────────────────────────────┘
```

### 4.1 Transport-neutral design (for WebRTC and client–server later)

Today everything – control, input, clipboard and video – goes over the one WebSocket. The frame protocol itself has no transport-specific parts, so another transport can carry it unchanged. The separate pieces below are **not built yet** (§7):

- **A channel interface** (`IDesktopChannel`) with two channels:
  - **reliable:** control, input, clipboard;
  - **lossy:** video, where frames may be dropped.

  Implementations: WebSocket now; WebRTC data channels or a server relay later.
- **Signalling as a separate message set** (`Offer`, `Answer`, `Candidate`, `Bye`), later run through a broker server.

What already holds:
- **Identity never depends on the transport.** The device certificate (pinning) and the rights check are always end-to-end between RemoteHub and the agent, even through a relay, so a server only forwards traffic and can't see or change it.
- **Rights come in as a list.** Today the agent derives them from Windows groups; later an RBAC server could issue a signed token holding the same rights.

### 4.2 Connection flow

1. RemoteHub connects to `wss://device:58732/v1/desktop`: pinned TLS, Basic sign-in, `SignInGate`. The policy is read again for every connection.
2. The agent replies with `Hello`:
   - protocol version, device name, the signed-in account, agent version;
   - the account's **rights** (control, secure desktop, consent bypass, privacy screen, privacy input block, automatic sign-in);
   - the **allowed consent modes** (`None` only with the `ConsentBypass` right) and the consent timeout;
   - the **sessions** (id, user, state: `Active`, `Locked`, `SignIn` or `Disconnected`; console yes or no; client name for RDP sessions);
   - the upper limits for frame rate and quality, the clipboard level and the **features** (`allMonitors`, `stats`, `ping`, `resume`, `transfer`, `transferReceipt`, `transferOnPaste`, `userPrivacy`, `hideApps`, `dropFiles`, plus `privacyScreen` and `autoSignIn` where they are available).
3. Before connecting, RemoteHub shows a dialog to choose the session, the consent mode (allowed ones only), view-only, and frame rate and quality. It sends `Start`.
4. Consent – only when the chosen session is `Active` (a user is signed in and it is unlocked):
   - **Ask:** an Allow / **Allow and hide apps…** / Deny prompt in the user session with a countdown ("Denied automatically in N s"). It denies when `ConsentTimeoutSeconds` (default 30, 5–600) runs out; picking apps restarts the countdown. The service gives up by itself only after the timeout + 190 s, as a last resort.
   - **PIN:** the device shows a 6-digit random PIN with **Hide apps…** and **Deny**, and the admin enters it in RemoteHub. 3 attempts, 3 minutes.
   - **None:** only with the `ConsentBypass` right and if the policy allows it. The admin's **own** signed-in session (same account, checked by SID) is connected to with `None` without asking, whatever the policy allows.
   - **Locked or sign-in screen:** nobody is asked – nothing private is shown there, and the `SecureDesktop` right is needed. The chosen mode is kept as the **follow-up**: when someone unlocks or signs in during the connection, they are asked first (the picture is held until they answer). The user who allowed it before, or the admin signing in with their own account, is not asked.
   - A `Disconnected` session (no screen) is refused.
5. No user signed in on the console:
   - **Sign-in screen:** the helper runs on the secure desktop.
   - **Auto sign-in** (`Start.AutoSignIn`, only on the console in the `SignIn` state, not view-only; see [credential-provider-design.md](credential-provider-design.md) §6): the agent sends `SignInReady`, RemoteHub sends the password as a binary `SignInRequest` within 30 s, the agent reports `SigningIn`, and the **credential provider** signs the account in. The new console session must belong to exactly the account that signed in to the agent (by SID) – otherwise it is signed out at once. Then `SignedIn` (with the end action), and the helper follows into the new session.
6. The helper starts in the session and the banner appears (not in a session automatic sign-in created). Video, input and clipboard begin. `Connected` carries a **resume token** (§4.6).
7. At the end, the helper stops, the banner disappears and an audit entry is written.

### 4.3 Frame protocol (binary, shared in `DesktopProtocol.cs`)

Each message is a type byte followed by a payload (JSON or binary):
- JSON: `Hello` / `Start` / `Status` (state, message, PIN attempts left, resume token, end action), `Pin`, `Layout` (monitors, shown monitor, size, secure desktop, view-only), `Settings` (monitor, frame rate, quality, fit size), `Clipboard` (text), `Stats` (every 2 s), `TransferStart` / `TransferEnd` / `TransferOffer` / `TransferRequest` (clipboard images and files), `PrivacyScreen`, `Privacy`
- Binary: `Video` (frame id, size, keyframe flag, H.264 access unit), `Ack`, `KeyFrameRequest`, `Mouse` (absolute), `Key` (scancode plus extended and up flags), `SecureAttention` (Ctrl+Alt+Del), `CursorShape`, `CursorPosition`, `Ping`/`Pong`, `TransferData`, `SignInRequest` (password for automatic sign-in, never JSON)

### 4.4 Device side

- **Helper launch.**
  - The service lists the sessions with `WTSEnumerateSessions` and checks every 2 seconds whether the console moved to another session (after a sign-in or a user switch); the helper follows it.
  - The agent duplicates its SYSTEM token, sets the session id, then calls `CreateProcessAsUser` with `winsta0\default`. Service and helper talk over the helper's standard input and output; the helper runs in a job object.
  - Desktop switches (lock screen, UAC) are followed through `OpenInputDesktop` / `SetThreadDesktop`. A helper that keeps failing (5 restarts in a minute) ends the connection.
  - After an unlock or a sign-in within the session, a new helper is started and the person now signed in is asked first (§4.2 step 4).
- **Capture.** Desktop Duplication with dirty rects, so frames are sent only on change. When the interface is lost, it is recreated. GDI is used for the secure desktop, for the picture of all monitors, where Desktop Duplication isn't available (VMs without a GPU), and when the Desktop Duplication picture **stands still** while GDI sees the screen change (seen on servers without a monitor) – then GDI for the rest of the connection, including the helpers started after a sign-in. While connected the display is kept on (`ES_DISPLAY_REQUIRED`); a picture that stays black is woken up to 3 times, and on a device with a virtual monitor it can take over.
- **Resolution.** The admin can set the shown monitor's resolution (not view-only, not for all monitors) – to the RemoteHub window, or to a fixed size from the toolbar's menu (1920×1080, 2560×1440, 3840×2160 or custom), larger than the window too; the viewer scales the picture down to fit. Both go as `FitWidth`/`FitHeight` in `Settings`; the agent ignores them for view-only and all monitors and limits them (`DesktopProtocol.ValidFitSize`: below 320×200 means the device's own, above 7680×4320 is cut to it, even sizes), in the service and again in the helper; the nearest mode the display has is used. Windows' own setting comes back at the end. On headless devices the optional virtual monitor (third-party Indirect Display Driver) is switched on at the window's size and off again with the last connection that needed it; the service switches it off when it starts (a crash may have left it on). A device that Windows no longer reports after a restart (enabling fails with "not connected") is brought back by a device rescan or created again as at install (`agent-spec.md`).
- **Encoding.** The H.264 MFT is used directly with `CODECAPI_AVLowLatencyMode`. The hardware MFT is used only above 30 fps ("smooth"), and falls back to software by itself; otherwise software. Frame rate and quality follow the admin's setting (capped by the policy). When acks come late (a quarter of a 2 s window or more spent waiting), one step goes down every 2 s: the **quality** by 10 to 50, then the **frame rate** by a third to 5 fps, then the quality by 10 to 20 – an admin reads text, so a sharp picture that moves less often comes first. When the acks keep up, one step goes back up in the reverse order (quality +5, frame rate +half), up to what the admin chose. The encoder keeps the chosen frame rate (fed fewer frames, it sends fewer bits; no key frame per step). `Stats` carry `frameRateLimit` while the frame rate is lowered, and RemoteHub's connection info says so.
- **Flow control.** At most two frames may be in flight without an ack. Frames are not dropped: the capture waits for an ack (if none comes within a second, it goes on). This keeps latency low on slow VPNs.
- **Input.** `SendInput` with scancodes, tagged so the privacy screen's input block lets it through. View-only mode is enforced **in the agent**, not only in RemoteHub.
- **Ctrl+Alt+Del.** On the console the service calls `SendSAS` itself; in another session (RDP) the helper in that session does. Windows accepts it only when `SoftwareSASGeneration` is 1 (services) or 3 (services and Ease of Access); 0 and 2 are refused with a message. If no policy configures the value, the agent sets it to 1 while it is installed (a marker file records that, and uninstalling removes it); a configured policy is left alone.
- **RDP windows keep drawing.** While the admin looks at a session, the user's `RemoteDesktop_SuppressWhenMinimized` is set to 2, so a minimized RDP window in that session keeps its remote session drawn (otherwise an agent connection to that server shows black). The user's own value is put back when the connection ends (kept in a file until then).
- **Idle timeout.** With policy `DesktopIdleTimeoutMinutes`, a connection without mouse or keyboard input ends after that time, with a warning about a minute before.
- **Credential provider** (details in [credential-provider-design.md](credential-provider-design.md)).
  - A native COM component (`src\RemoteHub.CredentialProvider`), registered by the MSI only in its opt-in feature `CredentialProvider`.
  - It only accepts sign-ins that the agent service hands it over a local named pipe (the broker), never anything from outside.
  - It is used only when policy and rights allow auto sign-in (`NoUserBehavior` = `AutoSignIn`, rights `AutoSignIn`, `Desktop` and `SecureDesktop`).
  - A session it created starts covered by the privacy screen with the device's mouse and keyboard blocked (where the privacy screen is available; `AutoSignInRequirePrivacyScreen` = 1 refuses automatic sign-in elsewhere). When the connection ends on purpose, the session is locked or signed out (`AutoSignInEndAction`, default Lock). When it drops, the screen stays covered for 2 minutes so RemoteHub can continue, then the session is signed out.
  - **Sensitive:** it needs a separate security review.
- **Banner.** A topmost window in the user session, on the user's desktop (not on the sign-in, lock or UAC screens), kept out of the captured picture (Windows 10 2004 and later). Admins can't hide it.
  - Heading "Your screen is being viewed and controlled by DOMAIN\user" (or "viewed by"), buttons **Hide screen**, **Hide apps…** and **Disconnect**.
  - After 8 s it collapses to a pill ("Controlled by DOMAIN\user" / "Viewed by DOMAIN\user") with Disconnect, and opens on hover. It never collapses while the user hides something or the privacy screen is on.
  - The admin's pointer over the banner makes it click-through (`WS_EX_TRANSPARENT`), so the admin's clicks reach the window below; the user's own mouse makes it clickable again at once. Only a physical mouse counts: injected input – the agent's, or a full-screen RDP client such as Hyper-V's VM connection placing the cursor – and a move to exactly where the admin's pointer is don't.
  - **No banner** in a session automatic sign-in created for this admin (`SignedInRemotely`, since 0.18.10): only the privacy screen's cover, which says "DOMAIN\user is signed in remotely".

### 4.5 Policy (registry, ADMX-ready)

The policy lives in `HKLM\SOFTWARE\Policies\RemoteHub\Agent`. A value there wins over `agent.json` and the `.rhagent` enrollment file. It is read again for every connection; a key that exists but can't be read allows nothing. The client side keeps using `HKLM/HKCU\SOFTWARE\Policies\RemoteHub`.

| Value | Type | Meaning |
|---|---|---|
| `DesktopEnabled` | DWORD | 0 turns off the agent desktop |
| `AllowedConsentModes` | REG_MULTI_SZ | `Ask`, `Pin`, `None` (default: `Ask`, `Pin`; `None` also needs the `ConsentBypass` right) |
| `ConsentTimeoutSeconds` | DWORD | Timeout of the Ask prompt, 5–600 (default 30); it denies when the time runs out |
| `MaxFramesPerSecond`, `MaxQuality` | DWORD | Upper limits on what the admin can choose (1–60, 1–100) |
| `DesktopClipboard` | DWORD | 0 off, 1 text, 2 text and images, 3 text, images and files (default) |
| `DesktopIdleTimeoutMinutes` | DWORD | A connection without mouse or keyboard input ends after this long (0 = never, default; up to 1440) |
| `AuditToEventLog` | DWORD | 0: audit entries only in the agent's log file, not in the Windows Application log |
| `PrivacyScreen` | DWORD | 0: no privacy screen; 1: allowed, the device's mouse and keyboard stay usable; 2 (default): allowed, and they can be blocked with it |
| `HiddenApps` | REG_MULTI_SZ | Apps never shown to an admin: full program paths, or `aumid:` + a Store app's AppUserModelID |
| `NoUserBehavior` | REG_SZ | `SignInScreen` (default) or `AutoSignIn` |
| `AutoSignInRequirePrivacyScreen` | DWORD | 1: no automatic sign-in where the privacy screen can't be shown |
| `AutoSignInEndAction` | REG_SZ | `Lock` or `SignOut` when the connection of an automatically signed-in session ends; wins over RemoteHub's setting |
| `TunnelEnabled`, `TunnelTargets`, `TunnelPorts`, `MaxTunnelStreams` | | The device as a jump host ([jump-host-design.md](jump-host-design.md)) |
| `Rights\PowerShell`, `Rights\Desktop`, `Rights\DesktopViewOnly`, `Rights\SecureDesktop`, `Rights\ConsentBypass`, `Rights\PrivacyScreen`, `Rights\Tunnel`, `Rights\AutoSignIn` | REG_MULTI_SZ | Group SIDs or names for each right |

Defaults: the groups in `agent.json` (`AllowedGroups`, Administrators) get `PowerShell`, `Desktop`, `SecureDesktop`, `PrivacyScreen` and `Tunnel`; nobody gets `DesktopViewOnly`, `ConsentBypass` or `AutoSignIn`.

### 4.6 Continuing after a dropped connection

- `Connected` carries a **resume token**: random, single-use, for the same Windows account, in the service's memory only. After a drop it can be used for **1 minute** (2 minutes for a session automatic sign-in created). Presented with `Start.Resume`, the new connection continues without asking again while the user who allowed it is still the one signed in; what the user hid and the admin's privacy screen carry over (`DesktopResumed`, 1103).
- If the old connection still looks alive on the device (a dead network isn't noticed at once), the new one **takes it over**: the old one is ended and doesn't write its own end entry.
- A connection that ends on purpose (RemoteHub, the user, idle, refused) or with a **protocol error** (a message that makes no sense) revokes the token **at once** – before the `Ended`, `Denied` or `Failed` status is sent, not after the close handshake. A new connection that presents the token afterwards is asked again (`WaitingForUser`). A takeover that has begun when the end on purpose comes is refused too: the end wins.
- A drop counts as a drop whichever side notices it first. When the agent notices it (the keep-alive timeout, or TCP giving up), its pending receive is cancelled rather than failing – that, too, is "connection lost" (1101): the token is kept for its window, and a session automatic sign-in created gets its 2-minute cover. Only a takeover cancels the receive on purpose, and it continues the session as well.

### 4.7 Clipboard

- Levels from policy `DesktopClipboard` (0–3); view-only connections get none. The service checks every transfer in both directions, not only the helper.
- **Closed while the session is locked:** while Windows' lock, sign-in or Ctrl+Alt+Del screen is up (LogonUI runs in the session, as for the session list), the service passes no clipboard text either way, and refuses a copy offered or asked for with "Nothing is copied while the device shows its lock or sign-in screen – copy again once it is unlocked." Nothing copied at the admin's PC lands on the locked user's clipboard, and nothing copied in the locked session goes out.
- **Text** both ways; text over 1,000,000 characters is cut on the device and not sent from RemoteHub.
- **Images and files:** at most 100 MiB and 1000 files per copy, sent in 256 KiB chunks. Copies are **paste-on-demand** (`transferOnPaste`): only names and sizes are offered (`TransferOffer`), and the data is sent when someone pastes (`TransferRequest`). A transfer to the device is confirmed with a **receipt** (`TransferEnd` with `ToDevice`, `Ok`) once it is on the device's clipboard.
- **Files copied at the device:** the helper reads the file list (`CF_HDROP`) itself, within the bytes Windows hands over, and keeps only fully qualified paths – no device paths (`\\.\`, `\\?\`), at most 1000 (the rest of a longer list is left out). A file list wins over text that a program puts next to it. The clipboard is read 200 ms after the last change, once the copy has settled, and only while the copying program answers: data a program hasn't rendered yet is rendered on request, and a busy program would leave the helper waiting with the clipboard open – every other program's copy would fail meanwhile. A busy program or a clipboard held elsewhere is tried again every 100 ms for up to 3 s. On Windows 11 26H2 the LocalSystem helper is shown only "DataObject" for a file copy that the user's own programs read as FileDrop; then (and when a listed file list can't be read) the list is read in the session as the signed-in user by `RemoteHubAgent.exe clipboard-files` – started with the user's token, no window, its own job, no input, ended after 3 s. It reads CF_HDROP through OLE and writes "files" and one path per line (at most 1 MB). The helper uses only the paths that pass the checks above, at most 1000, and only if nothing newer was copied meanwhile; the files are then read as the user as always. A copy that can't be read, or isn't sent, leaves a line in the agent's log (counts and format numbers only, no names). Everything is read as the signed-in user, never with the helper's LocalSystem rights: a file the user may not read is refused when RemoteHub asks for it ("The copied files can't be read on the device"), and reparse points (junctions, links) inside a copied folder are skipped. The limits are checked on the device before anything is offered ("Too much to copy …") and again by the service.
- Copies made in an app the user hid are not sent ([privacy-mode-design.md](privacy-mode-design.md) §7.7).
- **Files dropped on the tab** (`dropFiles`; transfer kind `drop`): sent right away – never offered for pasting – and saved on the signed-in user's Desktop in **From RemoteHub**, or in a folder named after the date and time inside it when something there already has one of the dropped names (nothing is overwritten). They need the same right as copying files (`DesktopClipboard` 3), and the lock screen closes them like the clipboard. The helper finds the Desktop as LocalSystem (also a redirected one) and writes **as the signed-in user**: a junction the user puts in the folder's place leads only where they may write anyway. An unfinished drop removes only the files and folders it created, never what was there before. The receipt names the folder, RemoteHub shows it ("Saved 3 files on the device's Desktop, in “From RemoteHub”."), and the service audits it (1108, counts and size only). The device never sends a `drop`, and an offer of one is refused.

## 5. Changes in RemoteHub

| Area | Change |
|---|---|
| `agent-spec.md` | Revise §2. New sections: desktop, helper, consent, credential provider, policy, rights. Raise the protocol version. |
| `AgentHealth` | Adds features (`powershell`, `desktop`) so RemoteHub only offers what the device can do. |
| Agent | New: `DesktopEndpoint`, `IDesktopSessions` / `IDesktopHelperLauncher` (session list, helper launch), `desktop` command (capture, encoder, input, banner, consent window, privacy screen), `AgentPolicy` (registry), rights mapping, audit entries, auto sign-in broker. Credential provider as a separate native project. |
| Agent installer | Registers and unregisters the credential provider (opt-in feature `CredentialProvider`, MSI property `AUTOSIGNIN`). ADMX/ADML templates come later. |
| `RdpOptions` + editor | "Remote desktop through" (`RemoteDesktopVia`): **Automatic** (the agent if one is installed, otherwise RDP), **Agent** or **RDP**. Defaults to Automatic, inherited like `UseAgent`. Plus frame rate and quality, defaulting to "Admin work", and automatic sign-in (`AgentAutoSignIn`, `AgentAutoSignInEndAction`, default Lock). |
| Open dialog | Session list, consent mode, view-only, quality, and a "Use RDP instead" button. |
| `SessionBase` and `is RdpSession` checks | Review `IsRdp`/`IsPowerShell`, `SelectedIsRdp`, `FindSession`, full screen, and the full-screen path of the recorder for the agent desktop. |
| New | `AgentDesktopSession : SessionBase`, `AgentDesktopView : IRecordableView` (recording works automatically), PIN entry, monitor switch, view-only toggle, Ctrl+Alt+Del button. |
| `AppPolicy` | `DisableAgentAutoSignIn`. |
| Tests | Frame protocol round trip; fake capture plus encode/decode loop; ack and flow control; consent Ask/PIN/None with denial, timeout and wrong PIN; rights matrix; policy overrides `agent.json`; view-only enforced in the agent; session and helper switching and credential provider on a real device. |

## 6. Security and privacy

- **Trust.** Pinned device certificate and Windows sign-in; each right is checked separately on the device.
- **Consent.** Ask or PIN when the policy requires it. The PIN proves someone is at the device. No prompt only with the right *and* the policy allowing it – or in the admin's own session.
- **Banner visible while a user is signed in.** Disconnect on the device always works, also with the privacy screen up and the device's input blocked. A session automatic sign-in created has no banner; it is covered by the privacy screen with the device's mouse and keyboard blocked.
- **Credentials for auto sign-in** go only over the pinned channel (`SignInRequest`, read straight into protected memory), are never stored on the device, and are passed only to the agent's own credential provider.
- **Audit log:** start, end, session, consent mode and result, view-only, user and source IP. No screen content and no input are logged. Entries go to the agent's log and, unless `AuditToEventLog` = 0, to the Windows Application log (source "RemoteHub Agent"):

| Event | Name | When |
|---|---|---|
| 1100 | `DesktopStarted` | A connection started (with its consent mode) |
| 1101 | `DesktopEnded` | It ended (duration, reason) |
| 1102 | `DesktopUserAllowed` | Someone who signed in or unlocked during the connection allowed it – or was connected to without asking (the admin's own account, or mode None) |
| 1103 | `DesktopResumed` | Continued after a dropped connection |
| 1104 | `DesktopPrivacyScreen` | The admin turned the privacy screen on or off |
| 1105 | `DesktopUserHid` | The user hid or showed their screen, or changed the hidden apps (count only) |
| 1106 | `AutoSignInSucceeded` | Automatic sign-in signed the account in on the console |
| 1107 | `AutoSignInEnded` | A session automatic sign-in created was locked or signed out |
| 1108 | `DesktopFilesSaved` | Files the admin dropped on the tab were saved on the signed-in user's Desktop (count, size and folder – no file names) |
| 2100 | `DesktopRefused` | Refused before anything was shown (policy, right, session) or denied by the user |
| 2101 | `DesktopUserRefused` | Someone who signed in or unlocked during the connection denied it; it ended |
| 2102 | `DesktopIdleEnded` | Ended by `DesktopIdleTimeoutMinutes` |
| 2103 | `DesktopPrivacyRefused` | A privacy screen request was refused or couldn't be shown |
| 2104 | `AutoSignInRefused` | Automatic sign-in refused before Windows was asked |
| 2105 | `AutoSignInFailed` | Windows refused the sign-in |
| 3100 | `DesktopError` | The helper couldn't start, or an unexpected error |
| 3101 | `AutoSignInMismatch` | Another account than the one asked for ended up on the console – signed out |
| 3102 | `AutoSignInUnavailable` | Automatic sign-in can't work on this device (pipe, registration, signature check) |

## 7. Still open (later, not blocking v1)

- **Transport-neutral pieces** (§4.1): the `IDesktopChannel` interface with a reliable and a lossy channel, and the signalling set `Offer` / `Answer` / `Candidate` / `Bye`.
- **WebRTC library:**
  - SIPSorcery (licence to check: *assumption*, it may have non-standard additions);
  - or a native libwebrtc wrapper.
- **Client–server model:** who runs the broker or relay, and how devices register and stay reachable (an outbound connection from the agent). Designed in [broker-design.md](broker-design.md).
- **RBAC server:** how rights are managed, the token format, and how several admins and tenants are separated.
- Audio; dragging files out of the tab onto this PC (dropping onto the tab is built, §4.7).

Done since this list was written: the ADMX/ADML templates (one for RemoteHub and the agent, [admx-design.md](admx-design.md)); turning the agent desktop off in RemoteHub (`AllowAgentDesktop` = 0); the frame rate adapts to a slow network (§4.4); the resume issue of §4.6 (fixed in #34).
