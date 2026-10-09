# RemoteHub Agent – Remote desktop without RDP (draft)

Status: Draft v3 – v1 is implemented (see `agent-spec.md` §12 and the README). Automatic sign-in moved to the backlog: when nobody is signed in, the sign-in screen is shown.
Owner: Grischa Ernst

## 1. Why

On client devices, RDP is often turned off by policy. Where the agent is installed, the agent gives screen and input instead, through its own encrypted connection and with the same trust model as the PowerShell tunnel.

This reverses the current non-goal "No screen or input" in `agent-spec.md` §2. That section has to change when this goes ahead.

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Network reach | Start with **direct connections** (LAN/VPN, as today). A **client–server model** (broker or relay) comes later, so every part must be designed so a server can sit in between without a protocol change. |
| 2 | Consent | **Before connecting**, while a user is signed in, the admin chooses: **Ask user** (Accept/Deny), **PIN** (the device shows a PIN to the user, the admin must enter it) or **No prompt**. **The agent decides which modes are allowed**, configured by registry policy so ADMX templates can follow. |
| 3 | Banner | **Mandatory.** "Screen being viewed by DOMAIN\user" with Disconnect, always visible while connected. |
| 4 | Secure desktop | **Always supported**: the sign-in screen, lock screen and UAC prompts can be seen and controlled. Who may use it is controlled by a right (see 9). |
| 5 | No user signed in | A setting chooses the behaviour: **show the sign-in/lock screen** (no prompt), or **sign in automatically** with the credentials stored in the connection, through a credential provider in the agent. |
| 6 | Multi-session devices | The **admin picks a session** (console or any user session). |
| 7 | Transport | **TLS/WebSocket over the existing agent connection first.** The frame protocol and signalling stay transport-neutral. WebRTC is added later as a second transport, mainly for the client–server phase. TCP stays as the fallback. |
| 8 | Scope v1 | Screen, mouse, keyboard, **text clipboard**, **multiple monitors**, **view-only mode**. Not in v1: audio, file drag and drop, privacy mode. |
| 9 | Rights | **Rights on the device side now, ready for RBAC later.** The agent checks a Windows group per right (`PowerShell`, `Desktop`, `DesktopViewOnly`, `SecureDesktop`, `ConsentBypass`), set by registry policy. The rights are named so that a later RBAC server for multi-admin enterprise setups can issue the same rights. |
| 10 | RDP | **RDP stays.** Default: RDP for connections without an agent, the agent desktop for connections with an agent. The admin can always **fall back to plain RDP**. The agent never changes RDP settings. |
| 11 | Quality | Default "**admin work**": up to 30 fps, low latency, usable at about 1 Mbit/s VPN, software encoding on VMs. An **admin setting for frame rate and quality** goes up to "as fast and as good as possible" (60 fps, hardware encoder). Automatic adaptation stays on, so slow VPNs keep working. |

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
│  ├ control + signalling      │ ────────────────▶ │  ├ sign-in, rights, policy (registry)  │
│  ├ H.264 decode (MF)         │  /v1/desktop      │  ├ consent (Ask / PIN / none), audit   │
│  └ input, clipboard, monitor │ ◀──────────────── │  ├ session list, helper launch         │
│                              │  later: WebRTC    │  └ credential provider (auto sign-in)  │
│                              │  or server relay  │        │ named pipe (SYSTEM only + secret)
└─────────────────────────────┘                   │  RemoteHubAgent.exe desktop           │
                                                  │   in the selected session              │
                                                  │   ├ Desktop Duplication / GDI          │
                                                  │   ├ H.264 encoder (MF, low latency)    │
                                                  │   ├ SendInput, clipboard, cursor       │
                                                  │   └ banner + consent/PIN window        │
                                                  └───────────────────────────────────────┘
```

### 4.1 Transport-neutral design (for WebRTC and client–server later)

- **The frame protocol knows nothing about the transport.** It is an interface `IDesktopChannel` with two channels:
  - **reliable:** control, input, clipboard;
  - **lossy:** video, where frames may be dropped.

  Implementations: WebSocket now; WebRTC data channels or a server relay later.
- **Signalling is a separate message set** (`Offer`, `Answer`, `Candidate`, `Bye`). Today it runs over the agent WebSocket; later the same set runs through a broker server.
- **Identity never depends on the transport.** The device certificate (pinning) and the rights check are always end-to-end between RemoteHub and the agent, even through a relay, so a server only forwards traffic and can't see or change it.
- **Rights come in as a list.** Today the agent derives them from Windows groups; later an RBAC server could issue a signed token holding the same rights.

### 4.2 Connection flow

1. RemoteHub connects to `wss://device:58732/v1/desktop`: pinned TLS, Basic sign-in, `SignInGate`.
2. The agent replies with `Capabilities`:
   - protocol version, monitors, codecs;
   - the user's **rights**;
   - the **allowed consent modes**;
   - the **sessions** (id, user, state: active, locked or disconnected; console yes or no);
   - the no-user behaviour.
3. Before connecting, RemoteHub shows a dialog to choose the session, the consent mode (allowed ones only), view-only, and frame rate and quality.
4. Consent:
   - **Ask:** an Accept/Deny prompt in the user session. It denies on timeout (the default timeout comes from the policy).
   - **PIN:** the device shows a 6-digit random PIN, and the admin enters it in RemoteHub. A limited number of attempts and a timeout apply.
   - **None:** only with the `ConsentBypass` right and if the policy allows it.
5. No user signed in (or a disconnected session):
   - **Sign-in screen:** the helper runs on the secure desktop.
   - **Auto sign-in:** RemoteHub sends the stored credentials over the pinned channel. The agent's **credential provider** signs in, then the helper follows into the new session.
6. The helper starts in the session and the banner appears. Video, input and clipboard begin.
7. At the end, the helper stops, the banner disappears and an audit entry is written.

### 4.3 Frame protocol (binary, shared in `AgentProtocol.cs`)

Each message is a type byte followed by a payload:
- `Hello` / `Capabilities` / `Select` (session, monitor, mode, quality)
- `VideoFrame`: monitor, frame id, timestamp, keyframe flag, H.264 access unit
- `FrameAck`: frame id plus decode time, which drives flow control and adaptation
- `Mouse` (absolute, normalised), `Key` (scancode plus extended flag), `Sas` (Ctrl+Alt+Del)
- `Clipboard` (text), `CursorShape`, `CursorPosition`, `MonitorLayout`, `SwitchMonitor`
- `ConsentState`, `PinEntry`, `Ping`/`Pong`, `Bye`

### 4.4 Device side

- **Helper launch.**
  - Session tracking through `SERVICE_CONTROL_SESSIONCHANGE` and `WTSEnumerateSessions`.
  - The agent duplicates the SYSTEM token, sets the session id, then calls `CreateProcessAsUser` with `winsta0\default` or `winlogon`.
  - Desktop switches (lock screen, UAC) are followed through `OpenInputDesktop` / `SetThreadDesktop`.
- **Capture.** Desktop Duplication with dirty rects, so frames are sent only on change. When the interface is lost, it is recreated. GDI is the fallback for VMs without a GPU.
- **Encoding.** The H.264 MFT is used directly with `CODECAPI_AVLowLatencyMode`. Hardware MFT when available, otherwise software. Quality and frame rate follow the admin's setting and are lowered automatically when acks come late.
- **Flow control.** At most two frames may be in flight without an ack; otherwise frames are dropped. This keeps latency low on slow VPNs.
- **Input.** `SendInput` with scancodes. Ctrl+Alt+Del through `SendSAS` from the service. View-only mode is enforced **in the agent**, not only in RemoteHub.
- **Credential provider.**
  - A native COM component that has to be registered.
  - It only accepts sign-ins that the agent service hands it over a local channel, never anything from outside.
  - It is used only when policy allows auto sign-in.
  - **Sensitive:** it needs a separate security review.
- **Banner.** A topmost window in the user session, on every desktop the helper captures. Admins can't hide it.

### 4.5 Policy (registry, ADMX-ready)

The policy lives in `HKLM\SOFTWARE\Policies\RemoteHub\Agent`. A value there wins over `agent.json` and the `.rhagent` enrollment file. The client side keeps using `HKLM/HKCU\SOFTWARE\Policies\RemoteHub`.

| Value | Type | Meaning |
|---|---|---|
| `DesktopEnabled` | DWORD | 0 turns off the agent desktop |
| `AllowedConsentModes` | REG_MULTI_SZ | `Ask`, `Pin`, `None` |
| `ConsentTimeoutSeconds` | DWORD | Timeout of the Ask prompt; it denies when the time runs out |
| `NoUserBehavior` | REG_SZ | `SignInScreen` or `AutoSignIn` |
| `Rights\PowerShell`, `Rights\Desktop`, `Rights\DesktopViewOnly`, `Rights\SecureDesktop`, `Rights\ConsentBypass` | REG_MULTI_SZ | Group SIDs for each right |
| `MaxFramesPerSecond`, `MaxQuality` | DWORD | Upper limits on what the admin can choose |

The existing `AllowedGroups` setting becomes the default for `Rights\PowerShell`, for compatibility.

## 5. Changes in RemoteHub

| Area | Change |
|---|---|
| `agent-spec.md` | Revise §2. New sections: desktop, helper, consent, credential provider, policy, rights. Raise the protocol version. |
| `AgentHealth` | Adds features (`powershell`, `desktop`) so RemoteHub only offers what the device can do. |
| Agent | New: `DesktopEndpoint`, `IDesktopChannel` (WebSocket implementation), `SessionTracker`, `DesktopHelperLauncher`, `desktop` command (capture, encoder, input, banner, consent window), `AgentPolicy` (registry), rights mapping, audit entries. Credential provider as a separate native project. |
| Agent installer | Registers and unregisters the credential provider. ADMX/ADML templates come later. |
| `RdpOptions` + editor | "Remote desktop through": **Automatic** (the agent if one is installed, otherwise RDP), **Agent** or **RDP**. Defaults to Automatic, inherited like `UseAgent`. Plus frame rate and quality, defaulting to "Admin work". |
| Open dialog | Session list, consent mode, view-only, quality, and a "Use RDP instead" button. |
| `SessionBase` and `is RdpSession` checks | Introduce `IsDesktop`. Review `IsRdp`/`IsPowerShell`, `SelectedIsRdp`, `FindSession`, full screen, and the full-screen path of the recorder. |
| New | `AgentDesktopSession : SessionBase`, `AgentDesktopView : IRecordableView` (recording works automatically), PIN entry, monitor switch, view-only toggle, Ctrl+Alt+Del button. |
| `AppPolicy` | `DisableAgentDesktop`. |
| Tests | Frame protocol round trip; fake capture plus encode/decode loop; ack and flow control; consent Ask/PIN/None with denial, timeout and wrong PIN; rights matrix; policy overrides `agent.json`; view-only enforced in the agent; session and helper switching and credential provider on a real device. |

## 6. Security and privacy

- **Trust.** Pinned device certificate and Windows sign-in; each right is checked separately on the device.
- **Consent.** Ask or PIN when the policy requires it. The PIN proves someone is at the device. No prompt only with the right *and* the policy allowing it.
- **Banner always visible.** Disconnect on the device always works.
- **Credentials for auto sign-in** go only over the pinned channel, are never stored on the device, and are passed only to the agent's own credential provider.
- **Audit log:** start, end, session, consent mode and result, view-only, user and source IP. No screen content and no input are logged.

## 7. Still open (later, not blocking v1)

- **WebRTC library:**
  - SIPSorcery (licence to check: *assumption*, it may have non-standard additions);
  - or a native libwebrtc wrapper.
- **Client–server model:** who runs the broker or relay, and how devices register and stay reachable (an outbound connection from the agent).
- **RBAC server:** how rights are managed, the token format, and how several admins and tenants are separated.
- **Privacy mode**, audio, file drag and drop.
