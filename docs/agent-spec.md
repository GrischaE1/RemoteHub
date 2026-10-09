# RemoteHub Agent – Specification

Status: Draft v2. Replaces the draft v1 "RemoteHub Device Agent – Developer Specification".
Owner: Grischa Ernst

Changes since v1, agreed during review:
- **Transport:** a PowerShell tunnel instead of separate REST endpoints, which gives feature parity with PowerShell remoting.
- **Sign-in:** a Windows account checked on the device, instead of client certificates pinned per operator.
- **Install:** a generic MSI plus an enrollment file for each device, instead of a separate MSI for each device.

## 1. Purpose

RemoteHub manages devices over PowerShell remoting (WinRM). Some environments don't allow WinRM.

The agent is a small Windows service that gives RemoteHub the same PowerShell session through its own TLS connection. Every RemoteHub tool works unchanged and with full parity:
- files (all drives, starting on `C:\`)
- registry
- processes
- services
- PowerShell console

WinRM can stay disabled on the device.

## 2. Non-goals (v1)

- **No NAT traversal, relay or cloud service.** RemoteHub needs a network path to the device, for example through VPN or routing.
- ~~No screen or input.~~ Changed: the agent also offers its own remote desktop for devices where RDP is off – see [agent-desktop-draft.md](agent-desktop-draft.md) and §12.
- **No own command set.** The agent never interprets commands. It authenticates the account, starts PowerShell as that account and forwards the protocol.

## 3. Architecture

```
RemoteHub (operator PC)                               Device
┌───────────────────────────┐   TLS 1.2/1.3, port 58732   ┌────────────────────────────────┐
│ PowerShell SDK runspace    │ ─────────────────────────▶ │ RemoteHubAgent (LocalSystem)   │
│  └ AgentConnectionInfo     │   WebSocket /v1/powershell  │  ├ Windows sign-in (LogonUser) │
│     (custom transport)     │ ◀───────────────────────── │  └ powershell.exe -s as user   │
└───────────────────────────┘   PSRP lines, 1 per message └────────────────────────────────┘
```

- **Client side:** RemoteHub implements PowerShell's public custom-transport API (`RunspaceConnectionInfo` and `ClientSessionTransportManagerBase`). The rest of RemoteHub only ever sees a normal `Runspace`.
- **Device side:** the agent starts Windows PowerShell 5.1 in server mode (`powershell.exe -Version 5.1 -s -NoLogo -NoProfile`). This is the out-of-process protocol PowerShell itself uses for `Start-Job -PSVersion 5.1`.
- **Proven so far:** a full session, 2 MB arguments, and the process ending when the session ends. Covered by `tests/RemoteHub.Tests/AgentTests.cs`.

## 4. Trust and authentication

### 4.1 Device identity (server)

- Each device has its own certificate, in `LocalMachine\My` with a non-exportable key.
- RemoteHub stores the certificate's SHA-256 fingerprint for the device. The device list is part of the settings file, so a shared settings location gives every RemoteHub instance the same pins.
- RemoteHub accepts the TLS connection **only if the fingerprint matches**. There is no CA, no name check and no trust on first use.
- On a mismatch RemoteHub stops before sending any credentials and shows a warning that can't be clicked through.

### 4.2 Operator sign-in

- Operators sign in with a Windows account on the device: a local account (LAPS works) or an account of the device's own domain or forest.
- **No trust is needed between the operator's environment and the device.** That covers no domain, or a separate forest without trust.
- RemoteHub sends the connection's saved credentials as an HTTP Basic header on the WebSocket upgrade. It does this only inside the pinned TLS connection.
- **Windows sign-in:** the agent signs the account in on the device as a *network* logon (`LogonUser`, `LOGON32_LOGON_NETWORK`).
  - Policies such as "Deny access to this computer from the network" apply, and are deliberately not bypassed.
  - Windows account lockout applies.
  - Events 4624 and 4625 are written to the Security log.
- **UAC:** for an administrator, the agent uses the full linked token. LocalSystem is allowed to obtain it.
- **Allowed accounts:** members of local Administrators (`S-1-5-32-544`) by default, or the groups listed in `allowedGroups`.
- **PowerShell runs as the signed-in user,** with their profile and environment, in a job object that is killed when the session ends. Rights and auditing are the same as with PowerShell remoting.
- Kerberos and NTLM aren't used. Kerberos doesn't work across untrusted forests, and NTLM is deprecated by Microsoft.

### 4.3 Brute-force and limits

- After 5 failed sign-ins within 5 minutes from the same source address, that address is refused for 5 minutes (HTTP 429).
- At most `maxSessions` sessions at once (default 20), then HTTP 503.

## 5. Deployment

### 5.1 Packages

- **Agent MSI:** generic and identical for all devices, so it can be signed. Contents:
  - Windows service `RemoteHubAgent` running as LocalSystem, set to *Automatic (Delayed Start)*.
  - All three recovery actions set to "Restart the service".
  - Self-contained, so no .NET runtime is needed on the device.
- **Enrollment file `<label>.rhagent`, one per device:** created by RemoteHub. Contents:
  - the device certificate and private key, as a PFX with a random one-time password
  - `agent.json`: device ID, label, port, allowed groups

  The PFX is in the enrollment file and not in the MSI, because Windows keeps a copy of every installed MSI in `C:\Windows\Installer`. The enrollment file is a secret: it is only moved over encrypted channels and deleted after import.

### 5.2 Creating an enrollment (RemoteHub)

1. The operator chooses **Install RemoteHub Agent** on a connection (right-click).
2. RemoteHub creates:
   - a device ID (GUID), unrelated to the anonymous telemetry install ID
   - an RSA key pair
   - a certificate: CN = device ID, server authentication, valid for 2 years
   - the `.rhagent` file
3. RemoteHub stores on the connection: the device ID, the SHA-256 fingerprint, the port and the status "not installed". The status changes to "installed" after the first successful connection.

### 5.3 Installing

- **Update through the running agent:** same upload, but `msiexec` is started through WMI (`Win32_Process.Create`), outside the session's job object, because the agent restarts during the update. RemoteHub waits until the agent answers again with the expected certificate.
- **If PowerShell remoting works for the connection:**
  1. RemoteHub copies the MSI and the `.rhagent` file to a new temp folder on the device.
  2. It runs `msiexec /i … ENROLLMENT="<path>" /qn`.
  3. It reads the agent's fingerprint back to confirm it, then deletes both files.
- **Otherwise:** RemoteHub puts both files on the clipboard as a file list. The operator pastes them into the RDP session and runs the MSI there. The MSI picks up the `.rhagent` file next to it.
- **First start of the agent:**
  1. Import the PFX into `LocalMachine\My` with a non-exportable key.
  2. Delete the PFX and the `.rhagent` file.
  3. Write `agent.json`.
- **Firewall:** the MSI creates an inbound rule for the agent program and its port, limited to the remote address range given in the enrollment, or Any if none is given.
- **Port in use:** the agent logs a clear error and doesn't start. It never moves silently to another port.

### 5.4 Certificate renewal and revocation

Out of scope for v1. The design keeps it possible:
- The fingerprint is a mutable field on the connection.
- A renewal can push a new PFX through the existing PowerShell session.

## 6. Wire protocol

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/health` | none | `{product, version, protocol, deviceId}` – nothing sensitive |
| GET (WebSocket) | `/v1/powershell` | Basic (Windows account) | One PowerShell session. Each text message is one line of the out-of-process remoting protocol, in both directions. |

- Status codes on `/v1/powershell`:

  | Code | Meaning |
  |---|---|
  | 400 | not a WebSocket request |
  | 401 | wrong user name or password |
  | 403 | account not in an allowed group, or network logon denied |
  | 429 | too many failed sign-ins |
  | 500 | PowerShell could not start |
  | 503 | session limit reached |

- Messages are at most 16 MB. WebSocket keep-alive every 30 s.
- Versioned through the path (`/v1`). The contract lives in `src/RemoteHub.Agent/AgentProtocol.cs`, which is compiled into both sides.

## 7. Configuration: `%ProgramData%\RemoteHub\Agent\agent.json`

```json
{
  "deviceId": "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10",
  "deviceLabel": "WKS-ACCOUNTING-07",
  "listenPort": 58732,
  "certificateThumbprint": "<SHA-1 thumbprint of the device certificate in LocalMachine\\My>",
  "allowedGroups": ["S-1-5-32-544"],
  "logRetentionDays": 30,
  "maxSessions": 20
}
```

## 8. Logging

- Daily files in `%ProgramData%\RemoteHub\Agent\logs`, kept for `logRetentionDays` days.
- Logged: start and stop, port, certificate fingerprint, every sign-in and refusal (account, source address, reason), and session start and end (account, source, duration, PowerShell PID).
- Never logged: passwords and command content.
- Nothing leaves the device. The agent sends no telemetry and no crash reports.

## 9. Updates

- **v1:** the agent is updated by installing a newer agent MSI, either through **Install / update RemoteHub Agent** or by hand.
- **Later:** the update server would need to tell agent releases from app releases (for example `?product=agent`). This is not available yet and needs a server change.

## 10. Requirements and known limits

- Windows 10 1809 / Server 2019 or later, with Windows PowerShell 5.1 (present by default).
- **PowerShell must be usable on the device.** If AppLocker or WDAC forces Constrained Language Mode or blocks `powershell.exe`, the agent behaves like PowerShell remoting would under those rules.
- Double hop (reaching network resources from the session) isn't possible, as with WinRM using default authentication.

## 11. Implementation status

| Part | Status |
|---|---|
| Custom transport in RemoteHub (`Remoting/AgentTransport.cs`) | done, tested |
| Agent host: TLS, health, sign-in on upgrade, throttle, tunnel, log (`src/RemoteHub.Agent`) | done, tested with a test sign-in |
| Windows sign-in and start-as-user (`WindowsShell.cs`) | written; needs a test as LocalSystem on a real device |
| Connection setting "Connect through: PowerShell remoting (WinRM) / RemoteHub Agent" plus device data (`AgentDevice`) | done |
| Enrollment generator (`AgentEnrollment.cs`), `.rhagent` import (`RemoteHubAgent.exe enroll`), cleanup on uninstall | done, tested (import itself needs LocalSystem) |
| Agent MSI: service, delayed start, recovery, firewall rule | done |
| Right-click install / update: silent over remoting, through the agent, or clipboard (`AgentInstaller.cs`, `AgentInstallDialog.cs`) | done; end-to-end test against a remote device pending |
| Agent MSI in the build script and release workflow; bundled in RemoteHub | done |
| `RemoteHubAgent.exe selftest` (TLS, endpoints, sign-in refusal) – run by the build | done |
| Remote desktop (`/v1/desktop`, helper, consent, policy, RemoteHub tab) – §12 | done, tested with a generated screen and as the current user; needs a test as LocalSystem on a real device |

## 12. Remote desktop

Endpoint `/v1/desktop` (WebSocket, same TLS pin and sign-in). Contract: `Desktop/DesktopProtocol.cs`, compiled into both sides.

- **Rights** (policy key `Rights`, group SIDs/names): `Desktop` (see and control), `DesktopViewOnly`, `SecureDesktop` (sign-in screen, lock screen, UAC), `ConsentBypass` (connect without asking). `PowerShell` defaults to `allowedGroups`. The sign-in requires `Desktop` or `DesktopViewOnly`; without `Desktop` the session is view-only, enforced in the service.
- **Flow:** Hello (account, rights, allowed consent modes, sessions, limits) → Start (session, consent mode, view-only, fps, quality) → Status (Starting / WaitingForUser / EnterPin / Connected / Denied / Failed / Ended) → Layout, Video, CursorShape, CursorPosition, Clipboard from the device; Ack, Mouse, Key, Clipboard, Settings, SecureAttention, KeyFrameRequest from RemoteHub.
- **Consent** only for an active, unlocked session: Ask (timeout = deny), Pin (6 digits, generated and checked in the service, 3 attempts, compared in constant time) or None (needs `ConsentBypass` and the policy). Sign-in screen and locked sessions: no question, but `SecureDesktop` is needed. The chosen mode then applies on unlock or sign-in: the helper runs with `HoldUserDesktop` (only the secure desktop is sent; when a user's desktop appears it stops sending and accepting input and reports `UserDesktop`), and the service asks the user now signed in before the picture continues; a refusal ends the session. Consent is per user: after a helper restart or console switch, anyone but the user who allowed it is asked.
- **Reconnect:** with Connected the agent sends a resume token (32 random bytes, in the service's memory only, single use, for the same account). Start with that token within a minute after the connection broke – or while the old one still looks alive, which it then ends – continues without a new question if the user who allowed it is still the one signed in. Ended on purpose (RemoteHub, the user, refused, idle) the token is void.
- **Clipboard** (policy `DesktopClipboard`): text (`Clipboard`), images and files (`TransferStart` JSON with the items and total, `TransferData` chunks of 256 KB in order, `TransferEnd` only to cancel or refuse). Limits 100 MB and 1000 items per copy, checked by the service in both directions and by the helper. On the device files are read and written impersonating the session's user, into their temp folder (`RemoteHub Clipboard`); paths are made relative and can't leave that folder.
- **All monitors:** monitor −1 is the bounding box of all monitors (GDI), scaled down by an integer factor when larger than 4096 × 2304; mouse and pointer are scaled back. No resolution change for it.
- **Stats / ping:** the helper sends `Stats` every 2 s (frames, quality, capture, size, last resolution change); `Ping` is answered by the service (round trip).
- **Idle timeout** (policy `DesktopIdleTimeoutMinutes`): no mouse or keyboard input for that long ends the session, with a notice shortly before.
- **Audit** to the Application log (source *RemoteHub Agent*, policy `AuditToEventLog`), event IDs as in the README.
- **Helper:** `RemoteHubAgent.exe desktop`, started by the service with its own LocalSystem token moved into the session (`winsta0\default`), in a job object; stdin/stdout carry length-prefixed messages. It never decides rights – the service relays only allowed messages (no input before consent or in view-only).
- **Picture:** DXGI Desktop Duplication (GDI fallback, initial frame by GDI), follows the input desktop; H.264 (Media Foundation, low latency, no B-frames, quality-based VBR, key frames on request); BGRA↔NV12 BT.709 full range; ≤ 2 frames in flight; quality steps down when acks lag.
- **Device UI:** consent, code and banner drawn in RemoteHub's palette (light/dark from the session user's setting); the banner is excluded from capture.
- **Audit:** start, refusal and end with account, source, session, consent mode, view-only and duration.
- **Backlog:** automatic sign-in with stored credentials (credential provider), WebRTC transport and relay, audio, file drag and drop.
