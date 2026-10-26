# RemoteHub Agent – Specification

Status: Draft v2, brought in line with the implementation of 0.18.11 (2026-10-15). Replaces the draft v1 "RemoteHub Device Agent – Developer Specification".
Owner: Grischa Ernst

Changes since v1, agreed during review:
- **Transport:** a PowerShell tunnel instead of separate REST endpoints, which gives feature parity with PowerShell remoting.
- **Sign-in:** a Windows account checked on the device, instead of client certificates pinned per operator.
- **Install:** a generic MSI plus an enrollment file, instead of a separate MSI for each device. Since SEC-ENR-03 the file holds RemoteHub's enrollment secret instead of a device key; the device makes its own key (§5).

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

- **No NAT traversal, relay or cloud service.** RemoteHub needs a network path to the device, for example through VPN or routing – or through another device's agent used as a jump host (`/v1/tunnel`, §6).
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

- Each device has its own certificate, in `LocalMachine\My` with a non-exportable key. The device makes the key itself when it is enrolled (§5.3); it never leaves the device.
- RemoteHub stores the certificate's SHA-256 fingerprint for the device. The device list is part of the settings file, so a shared settings location gives every RemoteHub instance the same pins.
- RemoteHub learns the fingerprint once, by the claim (§5.5): the agent proves with RemoteHub's enrollment secret that it was enrolled from RemoteHub's file, bound to the certificate it presents. Trust on first use without that proof doesn't exist.
- After that, RemoteHub accepts the TLS connection **only if the fingerprint matches**. There is no CA and no name check, and a claim never replaces a pin by itself.
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
  - Deliberately more permissive than WinRM: WinRM gives local administrators other than the built-in Administrator only a filtered token (UAC remote restrictions) unless `LocalAccountTokenFilterPolicy=1`. The agent gives them full rights without that setting, so local accounts (LAPS) work for admin tasks out of the box.
- **Windows' answer** (`SignInRefusal.cs`): Windows refusing the account → 401 – errors 1326 (wrong password), 1317 (no such user), 1355 (no such domain), 1327 (account restriction, e.g. a blank password), 1328 (logon hours), 1329 (workstation), 1330 (password expired), 1907 (password must be changed), 1331 (disabled), 1793 (account expired) and 1909 (locked out). The account may not sign in this way → 403 – 1385 (logon type not granted) and 1935 (authentication firewall). Windows can't check the account → 500 and isn't counted by the throttle (§4.3) – 1311 (no domain controller), 1786–1789 (trust relationship) and any other error. Each refusal is audited as event 2000 with the reason and the Windows error number, never the password.
- **Allowed accounts:** each right is granted to Windows groups of the device (§7.1). Default: the groups in `allowedGroups` (local Administrators, `S-1-5-32-544`) get PowerShell, Desktop, SecureDesktop, PrivacyScreen and Tunnel; DesktopViewOnly, ConsentBypass and AutoSignIn go to nobody. The policy subkey `Rights` replaces the groups of a right. Entries are SIDs or names, resolved on the device. In RemoteHub's install dialog only the English name "Administrators" is turned into the SID (`AgentInstaller.ParseGroups`); a localized name ("Administratoren") is passed on as typed and must resolve on the device.
- **PowerShell runs as the signed-in user,** with their profile and environment, in a job object that is killed when the session ends. Rights and auditing are the same as with PowerShell remoting, except for the UAC point above.
- Kerberos and NTLM aren't used. Kerberos doesn't work across untrusted forests, and NTLM is deprecated by Microsoft.

### 4.3 Brute-force and limits

- After 5 failed sign-ins within 5 minutes from the same source address, that address is refused for 5 minutes (HTTP 429). Attempts still running count against the 5, so parallel requests get no more tries than sequential ones.
- A failure is every refusal of the sign-in: wrong credentials (401) and also a right password for an account without the right (403). A 500 (Windows error the agent doesn't map) doesn't count. A successful sign-in clears the address.
- At most 8 Windows sign-ins run at once, from all addresses together; no free slot within 30 s → HTTP 503.
- At most `maxSessions` sessions at once (default 20) – PowerShell, desktop and tunnel together – then HTTP 503.

## 5. Deployment

### 5.1 Packages

- **Agent MSI:** generic and identical for all devices, so it can be signed. Contents:
  - Windows service `RemoteHubAgent` running as LocalSystem, set to *Automatic (Delayed Start)*.
  - All three recovery actions set to "Restart the service".
  - Self-contained, so no .NET runtime is needed on the device.
- **Enrollment file `<label>.rhagent`:** created by RemoteHub (`AgentEnrollment.cs`, JSON, format 2). One file can serve any number of devices – Intune, software distribution, by hand. Contents:
  - RemoteHub's enrollment secret (32 random bytes) and its ID (§5.5) – no key and no device ID
  - what goes into `agent.json`: label (empty = the device's computer name), port, allowed groups – plus the firewall scope (remote addresses) and the virtual-display choice

  Each device that uses the file makes its own key and device ID (§5.3), so a copied file never gives a second device the first one's identity (SEC-ENR-03). The file is still kept out of reach and deleted after use: anyone with it can enroll a device of their own, which RemoteHub claims only at an address where an operator expects a device (§5.5). It isn't in the MSI because Windows keeps a copy of every installed MSI in `C:\Windows\Installer`. A format-1 file (before SEC-ENR-03: the device's PFX and its password) is refused with a message to save a new one.

### 5.2 Creating an enrollment (RemoteHub)

1. The operator chooses **Install RemoteHub Agent** on a connection (right-click) – or saves the file for Intune or software distribution in *Settings → Security & storage → Save enrollment file…* (this PC's install defaults, the device's computer name as label).
2. RemoteHub writes the `.rhagent` file with its current enrollment secret – made on first use and kept with the settings (§5.5) – and the chosen settings.
3. RemoteHub stores on the connection the port and the status "not installed" – no device ID and no fingerprint yet: both come with the claim (§5.5), which sets the status to "installed".

### 5.3 Installing

- **Update through the running agent:** same upload, but `msiexec` is started through WMI (`Win32_Process.Create`), outside the session's job object, because the agent restarts during the update. RemoteHub waits until the agent answers again with the expected certificate. With *Enroll again* (a new identity) it waits until the old certificate stops answering, then claims the new one (§5.5).
- **If PowerShell remoting works for the connection:**
  1. RemoteHub copies the MSI and the `.rhagent` file to a new temp folder on the device.
  2. It runs `msiexec /i … ENROLLMENT="<path>" /qn`.
  3. It reads the certificate the device made back over the signed-in WinRM session and pins it, checks that the agent answers with it over the network, then deletes both files.
- **Otherwise:** RemoteHub puts both files on the clipboard as a file list. The operator pastes them into the RDP session and runs the MSI there. The MSI picks up the `.rhagent` file next to it. RemoteHub claims the agent as soon as it answers (§5.5).
- **Save files:** the MSI and the `.rhagent` file go to a folder, for installing by hand or with software distribution; claimed the same way.
- MSI properties: `ENROLLMENT="<path>"` (default: the single `.rhagent` next to the MSI), `VIRTUALDISPLAY=0|1`, `AUTOSIGNIN=0|1` (only in MSIs built with the credential provider).
- **Enrollment – done by the MSI, not at the agent's first start:** the custom action `RemoteHubAgent.exe enroll` runs deferred, as LocalSystem, after the files are in place and before the service starts:
  1. Set the service to *Automatic (Delayed Start)*.
  2. Make the device's identity:
     - a new device ID (a GUID, unrelated to the anonymous telemetry install ID);
     - an RSA key (3072 bit) as a persisted CNG machine key that can't be exported (`RemoteHub Agent <device id>`);
     - its self-signed end-entity certificate: basic constraints no CA; CN = device ID, O = RemoteHub Agent; SAN `urn:remotehub:device:<device id>` (plus the label as DNS name when it is one); EKU server **and** client authentication; valid from 10 minutes before for 2 years.

     The certificate goes into `LocalMachine\My` (friendly name "RemoteHub Agent"); earlier agent certificates, and the keys the agent made for them, are removed.
  3. Write `agent.json`, with the enrollment secret and its ID; `%ProgramData%\RemoteHub\Agent` gets an ACL of SYSTEM and Administrators only (also as owner, also for what is already there).
  4. Create the firewall rule (below).
  5. Delete the `.rhagent` file (if that fails, the log says to delete it by hand).
  6. Virtual display and automatic sign-in as chosen (their failure doesn't fail the install).

  Without an enrollment file an already enrolled agent keeps its identity (plain update); a first install without one fails with a message saying where to put it. A first install that fails after enrolling runs `RemoteHubAgent.exe cleanup` as rollback; uninstall runs it too (firewall rule, certificate and its key, `agent.json` with the enrollment secret, with its backup `agent.json.bak` (§7.2), `autosignin.json`, the Ctrl+Alt+Del setting, the automatic sign-in policy values it set, the virtual display – logs stay).
- **Firewall:** inbound rule "RemoteHub Agent" for the agent program and its port (TCP, all profiles), limited to the remote addresses given in the enrollment, or Any if none are given.
- **Port in use:** the agent logs a clear error and doesn't start. It never moves silently to another port.

### 5.4 Certificate renewal and revocation

Out of scope for v1. The design keeps it possible:
- The fingerprint is a mutable field on the connection.
- A renewal is an install with *Enroll again*: the device makes a new key and RemoteHub claims it.

### 5.5 Claim: how RemoteHub learns the fingerprint (SEC-ENR-03)

- **Enrollment secrets (RemoteHub):** `AgentEnrollmentKeys` in the settings (`AppData.AgentEnrollment`). The secret is stored like a password, in the database's secrets table. It travels with a shared settings file and with backups, so every RemoteHub using those settings can claim the devices.

  *Settings → Security & storage* has three actions:
  - **Save enrollment file…**
  - **Replace enrollment secret…** – new files get a new secret. The earlier one is revoked at once, or stays accepted until it is revoked.
  - **Revoke earlier secrets…**

  A revoked secret is refused for new claims; agents already claimed keep their pin. Each change is audited (*Agent enrollment secret*).
- **The agent** keeps the secret and its ID in `agent.json` (SYSTEM and Administrators only). It answers `GET /v1/enrollment?nonce=<16–64 base64url characters>` without credentials with `{deviceId, fingerprint, keyId, computerName, dnsHostName, proof}`.
  - The proof is HMAC-SHA256 with the secret over `RemoteHub enrollment 2`, the key ID, device ID, fingerprint, computer name, DNS name and nonce, one per line (`EnrollmentProof`).
  - 404: the agent holds no secret (it was enrolled with a format-1 file). 400: a malformed nonce.
- **RemoteHub** sends a fresh nonce over TLS without a pin and records the certificate the connection presented. It pins only when all of these hold (`AgentInstaller.EvaluateClaim`):
  - the stated fingerprint is that certificate;
  - the key ID is one of its secrets and isn't revoked;
  - the proof checks out (fixed-time comparison);
  - the device is the host it asked: its computer or DNS name matches the connection's host name (the first part). A host given as an IP address can't be checked this way.

  A man in the middle without the secret can't make a proof for its own certificate, and a recorded answer doesn't fit a new nonce. Through a jump host the claim goes through the tunnel, like every agent connection.
- **When RemoteHub claims:**
  - after a silent install (cross-checked with the fingerprint read over WinRM);
  - after an update with *Enroll again*;
  - every 10 s for 30 minutes after a clipboard or saved-files install;
  - at start, for connections still waiting;
  - when such a connection is used;
  - at the first RDP sign-in of a connection without an agent, when a device installed with this RemoteHub's file answers on the default port (Intune, software distribution);
  - by hand: right-click → *RemoteHub Agent → Claim RemoteHub Agent…* on a connection, or *Claim RemoteHub Agents…* on a folder for all its connections.
- **What a claim never does:** an automatic claim pins only on a full match and never replaces a pin. A device enrolled again keeps answering with its old certificate until the new install has run: the connection remembers that certificate (`AgentDevice.ReplacesFingerprint`) and doesn't pin it. *Claim RemoteHub Agent again…* on an installed agent shows the pinned and the new fingerprint, and replaces the pin only when the operator confirms. A device whose name doesn't match can be claimed by hand after a question (an alias). Each claim is audited with the fingerprint and the device's computer name.
- **What a copied file can still do:**
  - It can't clone a device that is already claimed: the file holds no key, and pins aren't replaced.
  - A device of the attacker's, enrolled with the file, is pinned only if it answers at an address where an operator expects a device, before the real device's first claim.
  - Revoking the secret closes that for devices not yet claimed.

## 6. Wire protocol

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/v1/health` | none | `{product, version, protocol, deviceId, features}` – nothing sensitive. `features`: `powershell` always, `desktop` when the remote desktop is on (`DesktopEnabled`), `tunnel` when `TunnelEnabled` (older agents send none = PowerShell only). `instance` (since 0.21.4): a random id new at every start of the agent – RemoteHub tells by it that an update of the same build restarted the agent |
| GET | `/v1/enrollment?nonce=…` | none | The claim (§5.5): `{deviceId, fingerprint, keyId, computerName, dnsHostName, proof}` – the proof is only of use to whoever holds the enrollment secret. 404 without a secret, 400 for a malformed nonce |
| GET (WebSocket) | `/v1/powershell` | Basic (Windows account, right `PowerShell`) | One PowerShell session. Each text message is one line of the out-of-process remoting protocol, in both directions. |
| GET (WebSocket) | `/v1/desktop` | Basic (Windows account, right `Desktop` or `DesktopViewOnly`) | Remote desktop (§12, `Desktop/DesktopProtocol.cs`). 404 when `DesktopEnabled` = 0. |
| GET (WebSocket) | `/v1/tunnel` | Basic (Windows account, right `Tunnel`) | The device as a jump host: TCP streams from it to other devices, many in one connection (`TunnelProtocol.cs`; [jump-host-design.md](jump-host-design.md)). 404 when `TunnelEnabled` = 0. |

- Status codes on the WebSocket endpoints:

  | Code | Meaning |
  |---|---|
  | 400 | not a WebSocket request |
  | 401 | no or invalid Basic header, or Windows refused the credentials (§4.2) |
  | 403 | account has none of the groups of the right, or network logon denied |
  | 404 | `/v1/desktop` or `/v1/tunnel` turned off by policy |
  | 429 | too many failed sign-ins from this address |
  | 500 | PowerShell could not start; or Windows couldn't check the account (no domain controller, broken trust, an unknown error – §4.2) |
  | 503 | session limit reached, or no free sign-in slot within 30 s (8 sign-ins at once) |

- Messages are at most 16 MB. WebSocket keep-alive: a ping every 30 s; no answer within 30 s ends the connection (and its PowerShell).
- A PowerShell session ended by a drop is logged as "connection lost" (1001), whichever side of the relay notices it first: a receive cancelled by the keep-alive timeout or by the aborted request is a drop, not "closed by RemoteHub" or "PowerShell ended".
- Versioned through the path (`/v1`). The contract lives in `src/RemoteHub.Agent/AgentProtocol.cs`, which is compiled into both sides.

## 7. Configuration: `%ProgramData%\RemoteHub\Agent\agent.json`

```json
{
  "deviceId": "3f1a9e2c-7b44-4b2a-9e21-0c6a2f9b7e10",
  "deviceLabel": "WKS-ACCOUNTING-07",
  "listenPort": 58732,
  "certificateThumbprint": "<SHA-1 thumbprint of the device certificate in LocalMachine\\My>",
  "allowedGroups": ["S-1-5-32-544"],
  "virtualDisplay": "auto",
  "logRetentionDays": 30,
  "maxSessions": 20,
  "enrollmentKeyId": "<ID of RemoteHub's enrollment secret>",
  "enrollmentSecret": "<the enrollment secret, base64, for the claim (§5.5)>"
}
```

`enrollmentKeyId`, `enrollmentSecret`: from the `.rhagent` file, for the claim (§5.5); missing on devices enrolled with a format-1 file.

`broker` (since 0.22.0, only on a device that joined a RemoteHub Broker): the broker, the organisation and keys derived
from the device secret – the secret itself is never kept. Written by the MSI (`BROKER_URL`, `BROKER_SECRET`,
`BROKER_ONLY`) through `enroll`, or by `RemoteHubAgent.exe broker join <url>` with the secret on standard input;
`broker leave` removes it. A device without an enrollment file enrolls itself there (its own key, default port,
Administrators; no firewall rule with `BROKER_ONLY=1`). The service keeps one outbound connection to the broker; a
session through it goes into the agent's own HTTPS endpoint, so sign-in, rights and policy apply as for a direct
connection.

`virtualDisplay`: the virtual monitor for devices without one – `auto` (switched on for a desktop connection only when
the shown display offers less than the window and at most 1280×720, e.g. 640×480 without a monitor), `always`, `off`
(driver not installed). Chosen at install (install dialog, MSI property `VIRTUALDISPLAY`, or the `.rhagent` file;
default on). The driver – the third-party Virtual Display Driver (MIT, IddCx, signed by SignPath Foundation, shipped
unchanged and hash-checked) – is installed with the device switched off; the service switches it on at the viewer's
window size (driver settings `C:\VirtualDisplayDriver\vdd_settings.xml` – the folder is the agent's own, with a protected
ACL where only SYSTEM and Administrators write, and a link or foreign folder in its place is removed first – reloaded
through its control pipe on a resize, connected anonymously and only to the driver host `WUDFHost.exe`),
the helper makes it the primary monitor (SetDisplayConfig) and fits it, and the service switches it off when the last
connection that asked for it ends (and at service start). After a restart of the device Windows may no longer report
the switched-off device (pnputil: *Disconnected*, enabling fails with 1167 "not connected"); switching it on then
rescans the devices and, if that doesn't bring it back, removes the device node and creates it again from the
hash-checked files as at install, keeps the new instance ID in `virtual-display.txt` and logs what it repaired.
Switching off covers every device with the hardware ID `Root\MttVDD` that Windows reports, plus the one in the state
file, and counts only when Windows no longer shows it as OK (reported without a problem): while the ended connection
still holds it, it is tried again after 1, 2 and 3 s, then the device node is removed (created again by the next
connection). Every switch-off writes one log line – which devices were switched off, found off and in what state, or
why not – and a line for each failed try as it happens; an end that leaves it on for others says for how many. Each
connection is counted once the monitor is on for it, whichever way it was switched on (enabled, rescan, created again,
size kept), and released once at its end; a connection that ends while its monitor is being switched on switches it
off again. Counting has its own lock, so a connection's end never waits for another one's pnputil call or driver pipe;
the device lock is held only for each pnputil call or pipe request, not during the waits, and a connection that
switches the monitor on meanwhile ends the tries. At service start it is switched off in the background, without
holding up the service's start, and never removed there. Every pnputil call is stopped after 20 s. If the driver's
control pipe doesn't answer a resize within 3 s while the monitor is on for another connection, the new connection
still counts and the log says the monitor kept its size.
Uninstall removes device (also one Windows no longer reports), driver package, settings and the publisher trust
(Trusted Publishers – only if the agent added it).

Other files in the same folder (SYSTEM and Administrators only): `autosignin.json` – the sessions automatic sign-in
created (session, SID, account, end action; no secrets), so a session left open by a crash is dealt with at the next
service start; `autosignin.policy` – which policy values `auto-sign-in on` wrote, so `off` and uninstall remove exactly
those.

### 7.1 Policies: `HKLM\SOFTWARE\Policies\RemoteHub\Agent`

Read again for every connection (no restart needed); a value there wins over `agent.json`. If the key exists but can't
be read, nothing is allowed (no right, no desktop, no tunnel) and the log says so. Invalid values keep the default.

The names, types and ranges are in `PolicyCatalog.cs`, which `policy\RemoteHub.admx` (category *RemoteHub Agent*) is checked against in CI ([admx-design.md](admx-design.md)).

| Value | Type | Meaning (default) |
|---|---|---|
| `DesktopEnabled` | DWORD | 0 turns the remote desktop off (on) |
| `AllowedConsentModes` | MULTI_SZ | any of Ask, Pin, None (Ask, Pin; None also needs `ConsentBypass`) |
| `ConsentTimeoutSeconds` | DWORD | 5–600 (30) |
| `MaxFramesPerSecond`, `MaxQuality` | DWORD | upper limits, 1–60 (60) and 1–100 (100) |
| `DesktopClipboard` | DWORD | 0 off, 1 text, 2 text and images, 3 also files (3) |
| `DesktopIdleTimeoutMinutes` | DWORD | 0–1440, 0 = never (0) |
| `AuditToEventLog` | DWORD | 0: audit only in the log file (1) |
| `PrivacyScreen` | DWORD | 0 not allowed, 1 allowed, 2 allowed and may block the device's mouse and keyboard (2) |
| `HiddenApps` | MULTI_SZ | programs (full path) or Store apps (`aumid:` + AppUserModelID, or their program's path under `WindowsApps`, matched in any version of the package) never shown to an admin (none) |
| `TunnelEnabled` | DWORD | 0: never a jump host (on) |
| `TunnelTargets`, `TunnelPorts` | MULTI_SZ | where jump connections may go: ranges and names / ports (anywhere except the device's own loopback, link-local and multicast; every port) |
| `MaxTunnelStreams` | DWORD | 1–1024 streams per jump connection (64) |
| `TunnelOwnPorts` | MULTI_SZ | ports of the device itself a jump or broker session may open (`RDP` = the RDP port in the registry, or numbers) (RDP) – since 0.22.0 |
| `BrokerEnabled` | DWORD | 0: no RemoteHub Broker (on) – since 0.22.0 |
| `BrokerUrl` | SZ | the only broker the device may use (any) – since 0.22.0 |
| `BrokerConsoles` | MULTI_SZ | the console ids that may start a session through the broker (all of the organisation's) – since 0.22.0 |
| `BrokerProxy`, `BrokerKeepAliveSeconds` | SZ / DWORD | proxy for the broker connection (system); keep-alive 30–90 s (60) – since 0.22.0 |
| `BrokerDirect` | DWORD | direct connections through the broker: 2 over public and local addresses, 1 public only, 0 relayed only (2) – since 0.22.0 |
| `BrokerStun` | SZ | the STUN service for the direct path, `name:port` (the one the broker names) – since 0.22.0 |
| `NoUserBehavior` | SZ | `SignInScreen` or `AutoSignIn` (agent.json, else AutoSignIn – on by default) |
| `AutoSignInRequirePrivacyScreen` | DWORD | 1: no automatic sign-in where the privacy screen can't be shown (0) |
| `AutoSignInEndAction` | SZ | `Lock` or `SignOut`; wins over RemoteHub's setting (unset) |
| `Rights\<right>` | MULTI_SZ | groups for PowerShell, Desktop, DesktopViewOnly, SecureDesktop, ConsentBypass, PrivacyScreen, Tunnel, AutoSignIn (§4.2) |
| `AllowedGroups` | MULTI_SZ | the groups for every right not set on its own, instead of `allowedGroups` in agent.json; never view-only or ConsentBypass, and AutoSignIn only when the install didn't choose its own groups (agent.json) – since 0.18.12 |
| `PowerShellEnabled` | DWORD | 0: no PowerShell session, so no files, registry, services, processes or update through the agent; `/v1/powershell` answers 404 before any sign-in, and health leaves out `powershell` (on) – since 0.18.12 |
| `VirtualDisplay` | SZ | `auto`, `always`, `off` (agent.json) – since 0.18.12 |
| `PrivacyScreenMessage` | SZ | heading of the privacy screen, at most 120 characters (built-in text) – since 0.18.12 |
| `ListenPort` | DWORD | 1–65535, read at service start; the firewall rule moves with it (agent.json) – since 0.18.12 |
| `LogRetentionDays`, `MaxSessions` | DWORD | 1–3650 and 1–200 (agent.json) – since 0.18.12 |

An invalid value is logged once per service run. `RemoteHubAgent.exe policy` prints every value with its source (policy,
agent.json, default). Health (`/v1/health`) lists in `managed` which of RemoteHub's install options the policy decides
(`autoSignIn`, `virtualDisplay`, `port`) – names only.

Automatic sign-in as chosen at install is in agent.json (`noUserBehavior`, `autoSignInGroups`) since 0.18.12; the policy
values win. Older versions wrote it into this key (recorded in `autosignin.policy`); the service moves those values to
agent.json at its first start, unless IT changed them since.

### 7.2 Writing and damage

A device that loses power at the wrong moment must not end up unreachable (lab finding A-CFG-JSON: agent.json was
zero-filled after a hard power-off, and the service didn't start).

- **Writes are atomic and durable.** agent.json, `autosignin.json`, `virtual-display.txt` and the records in
  `rdp-minimized-*.txt` and `sas-actions-*.txt` are written to a temporary file in the same folder, flushed to the disk
  and only then put in place (`DurableFile`): a power loss leaves the old file or the new one, never a torn one. The
  temporary file inherits the folder's ACL (§5.3); an existing file is replaced with `ReplaceFile`, which keeps that
  file's own ACL.
- **A last good copy.** Every save of agent.json also writes `agent.json.bak`, once agent.json itself has been
  replaced. At every service start a readable and valid agent.json refreshes the backup, so a hand edit becomes the last
  good copy too.
- **At service start, a damaged agent.json** – one that isn't a configuration at all: empty, zero-filled, cut off, not
  JSON – is kept as `agent.json.damaged`, and:
  - with a readable backup, the service starts with the backup and writes it back to agent.json. Log and Application
    log: event 2300 (warning), saying what couldn't be read and that the backup was used.
  - without one, the service starts with a fresh install's defaults – never more than a fresh install allows: the
    device's own certificate (the single agent certificate in LocalMachine\My; its CN is the device ID), the port the
    agent's firewall rule opens (else the default port), allowed groups the local Administrators, automatic sign-in
    off, default limits. The defaults aren't saved, and automatic sign-in isn't read from the file again while the
    service runs. Event 3300 (error) says so at every start, until agent.json is repaired or the agent is installed
    again with the enrollment file. The Administrators get no access they couldn't give themselves – they can edit
    agent.json. Policy values still win over the defaults, as over agent.json. Without a single agent certificate the
    service doesn't start, and the log says to install it again.
- **Not treated as damage:** a file that reads but holds an invalid value (a port of 0, no allowed groups) is what
  someone wrote. Replacing it with an older copy could undo a restriction they made, so the service refuses to start
  and logs why, as before. A missing agent.json means the device isn't enrolled (uninstall deletes it).
- **Limit:** the backup can be older than a hand edit made since the last service start or save. Whoever narrows
  `allowedGroups` by hand restarts the service, which makes the edit the last good copy.

## 8. Logging

- Daily files in `%ProgramData%\RemoteHub\Agent\logs`, kept for `logRetentionDays` days.
- Logged: start and stop, port, certificate fingerprint, every sign-in and refusal (account, source address, reason), and session start and end (account, source, duration, PowerShell PID).
- Audit entries (sign-in refused, session start and end, desktop, tunnel, automatic sign-in) also go to the Windows Application log, source *RemoteHub Agent*, unless the policy `AuditToEventLog` = 0.
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
| Agent host: TLS, health, sign-in on upgrade, throttle, tunnel, log (`src/RemoteHub.Agent`) | done, tested with a test sign-in and in the lab (S-AUTH, S-THR) |
| Windows sign-in and start-as-user (`WindowsShell.cs`) | done, tested as LocalSystem in the lab (P-SES, S-AUTH); Windows errors mapped in `SignInRefusal.cs` (§4.2) |
| Connection setting "Connect through: PowerShell remoting (WinRM) / RemoteHub Agent" plus device data (`AgentDevice`) | done |
| Enrollment file (`AgentEnrollment.cs`, format 2), enrollment with the device's own key (`RemoteHubAgent.exe enroll`), claim (§5.5), cleanup on uninstall | done, tested (the key itself needs LocalSystem: lab SEC-ENR-03, A-HAND) |
| Agent MSI: service, delayed start, recovery, firewall rule | done |
| Right-click install / update: silent over remoting, through the agent, clipboard or saved files (`AgentInstaller.cs`, `AgentInstallDialog.cs`) | done; lab: A-INST, A-HAND, A-UPD, A-UPDFAIL, A-UNINST |
| Agent MSI in the build script and release workflow; bundled in RemoteHub | done |
| `RemoteHubAgent.exe selftest` (TLS, endpoints, sign-in refusal) – run by the build | done |
| Remote desktop (`/v1/desktop`, helper, consent, policy, RemoteHub tab) – §12 | done; lab: D-* cases on real devices |
| Jump host (`/v1/tunnel`, policy TunnelEnabled / TunnelTargets / TunnelPorts / MaxTunnelStreams) | done; lab: J-JUMP |
| RemoteHub Broker (agent.json `broker`, `broker join/leave/status`, MSI BROKER_*, policy Broker*, TunnelOwnPorts; audit 1300–1303, 2400, 3400) | done; lab: B-BRK (needs the lab's broker) |
| Automatic sign-in (credential provider, `AutoSignInBroker`, `autosignin.json`, `auto-sign-in on\|off`) – [credential-provider-design.md](credential-provider-design.md) | done; lab: C-* cases |

## 12. Remote desktop

Endpoint `/v1/desktop` (WebSocket, same TLS pin and sign-in). Contract: `Desktop/DesktopProtocol.cs`, compiled into both sides.

- **Rights** (policy key `Rights`, group SIDs/names, §7.1): `Desktop` (see and control), `DesktopViewOnly`, `SecureDesktop` (sign-in screen, lock screen, UAC), `ConsentBypass` (connect without asking), `PrivacyScreen` (black out the device's screen), `AutoSignIn`. `PowerShell`, `Desktop`, `SecureDesktop`, `PrivacyScreen` and `Tunnel` default to `allowedGroups`; the others to nobody. The sign-in requires `Desktop` or `DesktopViewOnly`; without `Desktop` the session is view-only, enforced in the service.
- **Flow:** Hello (account, rights, allowed consent modes, sessions, limits) → Start (session, consent mode, view-only, fps, quality) → Status (Starting / WaitingForUser / EnterPin / Connected / Denied / Failed / Ended) → Layout, Video, CursorShape, CursorPosition, Clipboard from the device; Ack, Mouse, Key, Clipboard, Settings, SecureAttention, KeyFrameRequest from RemoteHub.
- **Consent** only for an active, unlocked session: Ask (timeout = deny), Pin (6 digits, generated and checked in the service, 3 attempts, compared in constant time) or None (needs `ConsentBypass` and the policy). Sign-in screen and locked sessions: no question, but `SecureDesktop` is needed. The chosen mode then applies on unlock or sign-in: the helper runs with `HoldUserDesktop` (only the secure desktop is sent; when a user's desktop appears it stops sending and accepting input and reports `UserDesktop`), and the service asks the user now signed in before the picture continues; a refusal ends the session. Consent is per user: after a helper restart or console switch, anyone but the user who allowed it is asked.
- **Reconnect:** with Connected the agent sends a resume token (32 random bytes, in the service's memory only, single use, for the same account). Start with that token within a minute after the connection broke – or while the old one still looks alive, which it then ends – continues without a new question if the user who allowed it is still the one signed in. Ended on purpose (RemoteHub, the user, refused, idle) the token is void.
- **Clipboard** (policy `DesktopClipboard`): text (`Clipboard`), images and files (`TransferStart` JSON with the items and total, `TransferData` chunks of 256 KB in order, `TransferEnd` only to cancel or refuse). Limits 100 MB and 1000 items per copy, checked by the service in both directions and by the helper. On the device files are read and written impersonating the session's user, into their temp folder (`RemoteHub Clipboard`); paths are made relative and can't leave that folder.
- **All monitors:** monitor −1 is the bounding box of all monitors (GDI), scaled down by an integer factor when larger than 4096 × 2304; mouse and pointer are scaled back. No resolution change for it.
- **Stats / ping:** the helper sends `Stats` every 2 s (frames, quality, capture, size, last resolution change); `Ping` is answered by the service (round trip).
- **Idle timeout** (policy `DesktopIdleTimeoutMinutes`): no mouse or keyboard input for that long ends the session, with a notice shortly before.
- **Audit** to the Application log (source *RemoteHub Agent*, policy `AuditToEventLog`), event IDs as in the README.
- **Helper:** `RemoteHubAgent.exe desktop`, started by the service with its own LocalSystem token moved into the session (`winsta0\default`), in a job object; stdin/stdout carry length-prefixed messages. It never decides rights – the service relays only allowed messages (no input before consent or in view-only).
- **Picture:** DXGI Desktop Duplication (GDI fallback, initial frame by GDI), follows the input desktop; H.264 (Media Foundation, low latency, no B-frames, quality-based VBR, key frames on request; software by default, the GPU's encoder for "Smooth" above 30 fps when there is one – asynchronous, event-driven, never waiting more than a few ms, and replaced by the software encoder if it fails or stops delivering); BGRA↔NV12 BT.709 full range; ≤ 2 frames in flight; quality steps down when acks lag.
- **Device UI:** consent, code and banner drawn in RemoteHub's dark palette (dark only for now); the banner is excluded from capture and shown on the user's desktop – not on the sign-in screen or UAC prompts, where nobody is signed in to see it or Windows shows only its own window.
- **Audit:** start, refusal and end with account, source, session, consent mode, view-only and duration.
- **Automatic sign-in** (built, [credential-provider-design.md](credential-provider-design.md)): when nobody is signed in on the console, the account that signed in to the agent can be signed in there (policy `NoUserBehavior` = AutoSignIn and right `AutoSignIn`; also needs Desktop and SecureDesktop). The service hands a single-use ticket over the pipe `RemoteHubAgent.AutoSignIn` (created before the agent listens, SYSTEM only, only LogonUI.exe as client) to the agent's credential provider. When the connection ends the session is locked or signed out (`AutoSignInEndAction`); after a dropped connection it stays covered for 2 minutes, then is signed out.
- **Backlog:** WebRTC transport and relay, audio, file drag and drop.
