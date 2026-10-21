# RemoteHub – technology inventory for threat modelling

State of branch `credential-provider-by-default` (including uncommitted changes) on 20 October 2026. It lists the
technologies, protocols and cryptography each component uses, the trust boundaries between them and the points that
deserve a closer look. Sources are the code and the design documents in `docs/`.

## 1. Components

| Component | Runs as | Technology |
|---|---|---|
| **RemoteHub console** (`src/RemoteHub`) | Interactive user | WPF + WinForms, .NET 10, x64, WPF-UI 4.3, CommunityToolkit.Mvvm |
| **RemoteHub Agent** (`src/RemoteHub.Agent`) | **LocalSystem** service, auto start | .NET 10, Kestrel (ASP.NET Core), WebSockets |
| **Desktop helper** (`RemoteHubAgent.exe desktop`) | SYSTEM token in the user's session, **UIAccess on** | DXGI, GDI, Media Foundation, `SendInput` |
| **Credential provider** (C++ DLL) | Inside LogonUI.exe (SYSTEM) | COM `ICredentialProvider` / `ICredentialProviderCredential2` |
| **Installers** | msiexec, SYSTEM custom actions | WiX v5.0.2 |

Third-party libraries (see `THIRD-PARTY-NOTICES.md`): PowerShell SDK 7.6.6, Microsoft.Data.Sqlite 10 with
SQLite3 Multiple Ciphers (SQLite3MC.PCLRaw.bundle 2.4.0), SSH.NET 2026.0.0, FluentFTP 55.0.0, SMBLibrary 1.5.8.1
(LGPL), Markdig 0.44.0, AvalonEdit 6.3.1, WPF-UI 4.3.0, CommunityToolkit.Mvvm 8.4.2, Virtual Display Driver 24.12.24
(signed driver, MIT).

## 2. Network protocols and transport security

### 2.1 Console → agent

- **Transport:** Kestrel HTTPS on port **58732** (policy `ListenPort`), all addresses (IPv4 and IPv6), HTTP/1.1
  only, no `Server` header.
- **TLS:** 1.2 and 1.3. Cipher suites are the SChannel defaults; nothing is configured.
- **Certificate:** self-signed RSA-3072 / SHA-256, valid 2 years. The agent serves it even after it expires
  (`validOnly:false`).
- **Server authentication:** the console pins the SHA-256 of the whole certificate. Chain and host name are ignored,
  and there is no trust on first use. No mutual TLS, although the certificate has the Client Auth EKU.
- **Client authentication:** HTTP Basic credentials on the WebSocket upgrade.
  - The agent signs the account in with `LogonUser(LOGON32_LOGON_NETWORK)` and checks the right for each feature with
    `IsInRole`.
  - For administrators under UAC it uses the **full linked token**, which deliberately bypasses
    `LocalAccountTokenFilterPolicy`.
  - No Kerberos, NTLM or Negotiate on the agent's endpoint.
- **Throttling** (`SignInGate`):
  - 5 failed sign-ins per source IP in 5 minutes block that IP for 5 minutes (HTTP 429).
  - At most 8 sign-ins run at once.
  - `MaxSessions` defaults to 20 across PowerShell, desktop and tunnel.
- **Endpoints:**
  - `/v1/health` has **no authentication**. It returns product, version, protocol, device ID, features and whether
    the device is managed.
  - `/v1/powershell` carries the PowerShell remoting protocol (PSRP) as XML, one message per line, up to 16 MB, and
    pipes it into `powershell.exe -Version 5.1`. The agent doesn't filter commands.
  - `/v1/tunnel` is a binary multiplexer: `[type u8][stream u32 BE][payload ≤ 64 KB]` with a 256 KB credit window
    per stream. Message types: Open, Opened, Refused (JSON), Data, Credit, Close, Reset.
  - `/v1/desktop` is a type byte plus a JSON or binary payload, up to 16 MB. The password for automatic sign-in
    travels in a binary `SignInRequest` frame read into a pinned buffer.
- **Keep-alive:** a WebSocket ping every 30 s on both sides.

### 2.2 Jump host (the agent as a relay)

- The console opens a WebSocket (`/v1/tunnel`) to the jump host's agent and multiplexes TCP streams over it. TLS to
  an agent behind the jump host runs end to end inside the tunnel.
- On the console's side, a `127.0.0.1` listener on an ephemeral port accepts only connections owned by RemoteHub's
  own process ID (checked with `GetExtendedTcpTable`). It carries RDP, WinRM, SFTP and SMB; agent and WebDAV traffic
  go through the tunnel in-process.
- **On the agent, the target is resolved, then filtered by IP, then connected to by that IP**, so DNS rebinding
  can't bypass the filter.
  - With no policy it allows **any host and any port** except special addresses: loopback, the device's own
    addresses, 0/8, 169.254/16, multicast and above, IPv6 link-local and multicast.
  - Special addresses are allowed only by an explicit IP range, never by a name pattern.
  - `TunnelTargets`, `TunnelPorts` and `MaxTunnelStreams` (default 64) narrow it. A policy value that is set but
    unreadable allows nothing.
- Through a jump host, RDP and WinRM fall back to **NTLM** (there is no `TERMSRV/127.0.0.1` SPN), WinRM uses
  `SkipCNCheck`, and RemoteHub pins the RDP certificate itself.
- The jump host sees connection metadata, not content where the inner protocol is encrypted.

### 2.3 Console → targets directly

- **RDP:** the `mstscax` ActiveX control (`MsRdpClient9NotSafeForScripting`) through hand-written COM interop.
  - NLA (CredSSP) is on by default.
  - Server authentication defaults to **Warn**.
  - The password goes in through `IMsTscNonScriptable.SetClearTextPassword`.
  - Clipboard redirection is on; drives, printers and smart cards are off by default.
  - No RD Gateway, Restricted Admin or Remote Credential Guard.
  - Exported `.rdp` files may contain a `password 51:b:` DPAPI blob; import can decrypt it.
- **PowerShell remoting:** the in-process PowerShell 7.6.6 SDK, WSMan over HTTP 5985 or HTTPS 5986, Negotiate (or
  Kerberos as the current user).
  - No explicit CredSSP and no SSH remoting.
  - `TrustedHosts` is never changed by the app.
  - The remoting setup script creates a self-signed certificate on the target and scoped firewall rules. The console
    can add the target's certificate to **CurrentUser\Root** (behind a Windows confirmation).
- **Network shares:**
  - **SFTP** – SSH.NET. Password or keyboard-interactive only; the host key is pinned by SHA-256 on first use, and a
    changed key is asked about.
  - **FTP/FTPS** – FluentFTP. None, explicit or implicit TLS; **plain FTP is allowed** (credentials in clear).
    An untrusted certificate is pinned once the user accepts it. Refused through a jump host.
  - **SMB through Windows** – `WNetAddConnection2W` without a drive letter; Windows negotiates signing and encryption.
  - **SMB through SMBLibrary** – NTLMv2 only, signing and encryption not set explicitly. Used for a second account
    and behind a jump host. Reaches into a private method and field through reflection.
  - **WebDAV** – `HttpClient` with Basic, Digest, NTLM or Negotiate and `PreAuthenticate`. **Plain HTTP is allowed**
    (Basic credentials in clear). Redirects off; certificates pinned on first use.

### 2.4 Console → vendor

- **Update check:** `GET https://update.remotehub.dev/latest` (optionally `?channel=beta`).
  - The MSI may only come from remotehub.dev or GitHub release assets of the project's repository; redirect hosts are
    restricted.
  - **It is checked only against a SHA-256 and size from the same server; its Authenticode signature is not checked.**
  - The install runs through `install-update.cmd` written to `%TEMP%\RemoteHub\Updates` (user-writable), then
    `msiexec /i … /passive`.
  - Policies `DisableUpdateCheck` and `UpdateChannel`.
- **Telemetry and crash reports:** `POST https://update.remotehub.dev/telemetry` with a random install ID, counts,
  version, OS and architecture; crash messages and stacks are redacted. Policy `DisableTelemetry`.

## 3. Cryptography and key management

### 3.1 Console

| Asset | Protection |
|---|---|
| Database `%LOCALAPPDATA%\RemoteHub\remotehub.db` (and 7 automatic backups) | SQLite3 Multiple Ciphers, **ChaCha20-Poly1305** (sqleet), random key with `kdf_iter=1`; `secure_delete=ON`, `trusted_schema=OFF`, no extension loading |
| Key file `db.key` | Database key + field key (64 random bytes), **DPAPI CurrentUser** with extra entropy; additionally sealed by the startup lock when it is on |
| Passwords in the database | A second layer: **AES-256-GCM** with table, row and column as associated data |
| Startup lock (optional) | **PBKDF2-SHA256**, 600k iterations, then AES-GCM; optionally **Windows Hello** (TPM-backed key signs a challenge, SHA-256 of the signature wraps the key) |
| Shared settings file (OneDrive or policy folder) | AES-256-GCM, PBKDF2 key; iteration count read from the file, bounds-checked to 10k–10M; derived key cached in `keys.dat` under DPAPI |
| Backup `.rhbackup` (imports `.rdpmbackup` too) | PBKDF2-SHA256, 600k iterations, AES-GCM; no associated data or version binding in the envelope |
| Audit log | In the database, append-only through triggers, hash chain per device – shows tampering, doesn't prevent it |
| Session recordings (MP4/H.264, Media Foundation) | **Not encrypted**; policy folder, else `%USERPROFILE%\Videos\RemoteHub` |

In memory, passwords are plain `string`s and are decrypted at load time; key bytes are wiped with
`CryptographicOperations.ZeroMemory`. A `SecureString` exists only briefly for a `PSCredential`. `SecureClipboard`
keeps passwords out of clipboard history and the cloud clipboard and clears them after a timeout. Auto-lock hides the
windows and wipes the settings key but keeps the database key and sessions – it is a UI lock.

Legacy data: `%APPDATA%\RdpManager` is copied, old DPAPI entropy and `RDPM` file headers are still read, and
`settings.dat` is migrated into the database.

### 3.2 Agent

- **Device identity:** RSA-3072 / SHA-256 certificate, `CN=<deviceId>`, not a CA. Private key in `LocalMachine\My`,
  not exportable.
- **Session secrets:** 6-digit consent PIN (3 attempts) and a 32-byte resume token (single use, same account,
  1–2 minutes), both from `RandomNumberGenerator`. The PIN is compared in constant time.
- **Automatic sign-in password:** protected in memory with `CryptProtectMemory(SAME_PROCESS)` and wiped after use.
- **Enrollment file `.rhagent`:** **plaintext JSON that holds the PFX and its password together.** It travels over
  PowerShell remoting, the RDP clipboard or a saved folder, and is deleted after import (best effort – a failed
  delete only logs a warning).
- **Config folder** `%ProgramData%\RemoteHub\Agent`: SYSTEM and Administrators only, inheritance cut, owner
  Administrators, written atomically (`DurableFile`).
- No DPAPI, AES or HMAC on the agent side.

### 3.3 Credential provider

- The password is copied into `VirtualAlloc` pages (not `VirtualLock`ed), protected with
  `CryptProtectMemory(SAME_PROCESS)`, unprotected once inside `GetSerialization` and wiped with `SecureZeroMemory`.
- The logon is serialised as `KERB_INTERACTIVE_UNLOCK_LOGON` through the Negotiate package, in a `CoTaskMemAlloc`
  buffer that LogonUI owns from then on (plaintext).

## 4. Local IPC

| Channel | Protection |
|---|---|
| Agent ↔ credential provider: `\\.\pipe\RemoteHubAgent.AutoSignIn` | DACL `D:P(A;;GA;;;SY)`, owner SYSTEM, `FirstPipeInstance`, remote clients rejected, 4 KB message mode.<br>The agent checks the client: console session, `%SystemRoot%\System32\LogonUI.exe`, SYSTEM.<br>The provider checks the server: the PID the SCM reports for `RemoteHubAgent`, `RemoteHubAgent.exe` next to the DLL, SYSTEM; opened with `SECURITY_IDENTIFICATION`. No service-SID check.<br>Exchange `Pending → Claim → Ticket → Result` with a 16-byte nonce. The **ticket holds the plaintext password** and has no MAC; it lives at most 60 s.<br>Before offering a ticket the agent checks the provider's registration, that it isn't excluded by policy, and that the DLL's Authenticode signer matches the agent exe (no revocation check). |
| Service ↔ desktop helper | Anonymous stdio pipes, length-prefixed, explicit inherited-handle list, kill-on-close job. The service decides what to relay: input only when live and not view-only, clipboard level, consent. |
| Helper → `RemoteHubAgent.exe clipboard-files` | Started with the user's token (`WTSQueryUserToken`); its output is treated as untrusted. |
| Agent → Virtual Display Driver: `MTTVirtualDisplayPipe` | **The server is not verified**, and the client allows impersonation (fixed payload `RELOAD_DRIVER`). |
| Console, single instance | `Local\RemoteHub-*` mutex and a named pipe with `CurrentUserOnly`; forwards command-line arguments such as `.rdp` paths. |
| Clipboard | Passwords (copied for the Windows credential prompt), device files and images (delayed rendering). |

## 5. Remote desktop stack (agent)

- **Helper process:** the service's LocalSystem token, duplicated into the user's session on `winsta0\default`,
  **TokenUIAccess on** unless `HKLM\SOFTWARE\Policies\RemoteHub\Agent\HelperUIAccess = 0` (undocumented).
- **Capture:** DXGI Desktop Duplication through raw COM, falling back to GDI `BitBlt` (`CAPTUREBLT`) for the secure
  desktop; follows the input desktop.
- **Encoding:** H.264 through a Media Foundation encoder, software by default, hardware above 30 fps. The console
  decodes with Media Foundation and decodes clipboard images (PNG) with WPF `BitmapDecoder`.
- **Input:** `SendInput` with scan codes and tagged input. Ctrl+Alt+Del goes through `SendSAS`; the agent sets
  `SoftwareSASGeneration=1` when no policy configures it.
- **Low-level hooks:** `WH_KEYBOARD_LL` / `WH_MOUSE_LL` for the privacy-screen input block; a keyboard hook for
  SnipGuard.
- **Banner and privacy overlay:** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- **Consent:** Ask, PIN or None. None needs both the `ConsentBypass` right and policy. The locked or sign-in screen
  needs the `SecureDesktop` right. View-only is forced without the `Desktop` right.
- **File transfer** (through the clipboard, policy levels 0–3):
  - Limits: 100 MB, 1000 items, 256 KB chunks.
  - The device side impersonates the session user and skips reparse points.
  - Received paths are checked with `SafeRelativePath` + `GetFullPath`. On the console they land in
    `%TEMP%\RemoteHub\Clipboard\<timestamp>`.
- **Registry writes during a session:** per-user `HKU\<sid>` values (Ctrl+Alt+Del screen items,
  `RemoteDesktop_SuppressWhenMinimized`), restored from marker files and at service start.
- Not built: audio, drag and drop, recording on the agent.

## 6. Policy, logging, installation, supply chain

### 6.1 Policy

- **Agent:** `HKLM\SOFTWARE\Policies\RemoteHub\Agent`, read for every connection, wins over `agent.json`. If the key
  exists but can't be read, everything is turned off. Rights under `Rights\<right>` (PowerShell, Desktop,
  DesktopViewOnly, SecureDesktop, ConsentBypass, PrivacyScreen, Tunnel, AutoSignIn). DesktopViewOnly, ConsentBypass
  and AutoSignIn default to nobody; the others to `AllowedGroups`.
- **Console:** `Software\Policies\RemoteHub` is read from **HKLM and HKCU**; HKCU is writable by the user. The
  `Connections` subkey forces RDP options through reflection.
- **Templates:** `policy/RemoteHub.admx` and `policy/en-US/RemoteHub.adml` (design: `docs/admx-design.md`).

### 6.2 Logging

- **Agent:** daily files in `%ProgramData%\RemoteHub\Agent\logs` (CR/LF stripped) and the **Windows Application
  log**, source "RemoteHub Agent", event IDs 1000–3300; `AuditToEventLog=0` turns the latter off. Accounts and
  source addresses are logged; passwords and command content are not. No telemetry.
- **Console:** `DiagLog`, plain text in `%APPDATA%\RemoteHub\logs`, about 1 MB with one rotation. **The jump
  forwarder writes host names to it**, which the review rules don't allow.
- **Credential provider:** plain-text `provider-yyyyMMdd.log` in the agent's log folder (not ETW). It refuses a
  missing or reparse-point folder, but there is a gap between that check and opening the file.

### 6.3 Installers

- **Agent MSI** (per-machine, `Program Files\RemoteHub Agent`):
  - Service `RemoteHubAgent`, LocalSystem, auto start (delayed after enrollment), restart on failure.
  - Custom action `enroll` runs as SYSTEM and reads `ENROLLMENT`, by default the `.rhagent` next to the MSI.
  - The firewall rule is created with `netsh` as **`profile=any`, `remoteip=any`** unless enrollment restricts it.
  - The **credential provider feature is installed by default** (Level 1) in builds that include it; `AUTOSIGNIN=0`
    leaves it out. It registers the CLSID `{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}` under `Credential Providers`.
  - File ACLs are inherited from Program Files (no `PermissionEx`).
  - Virtual Display Driver: SHA-256 checks on its files; the agent **adds the SignPath certificate to
    TrustedPublisher**, creates `Root\MttVDD` with `pnputil`, and writes settings to `C:\VirtualDisplayDriver\` with
    default ACLs.
- **Console MSI** (per-machine): no services or custom actions; registers the `.rdp` file association, which imports
  and connects after showing a risk list; embeds the agent MSI.

### 6.4 Build hardening

- **Credential provider:** `/sdl /GS /W4 /WX /guard:cf /guard:ehcont /Qspectre`, linked with
  `/DYNAMICBASE /NXCOMPAT /CETCOMPAT /HIGHENTROPYVA`, static CRT. No `/analyze`, no `/INTEGRITYCHECK`.
- **Fuzzing:** libFuzzer with AddressSanitizer on the `Wire.cpp` parser (parse → write round trip), 60 s in CI.
- **.NET:** the build must have no warnings; nullable reference types are on.

### 6.5 CI and release

- GitHub Actions are pinned to commit SHAs. `ci.yml` has `contents: read`; it scans the outputs with Defender and
  ClamAV (installed from apt **without a pinned version**).
- `release.yml` runs on version tags with `contents: write`:
  - Signing uses SSL.com eSigner behind the `release-signing` environment approval; test tags (containing `-`) use
    `release-test`, unsigned.
  - CodeSignTool v1.3.2 is downloaded and checked against a pinned SHA-256.
  - Signing credentials (`-password=`, `-totp_secret=`) are passed to Java **on the command line**.
  - `${{ github.ref_name }}` is inlined into the release scripts.
  - After signing, the signer's thumbprint is checked against `SIGNING_CERTIFICATE_THUMBPRINT`.
  - SHA-256 sidecar files are made in the same job; no SBOM or provenance.
  - The credential provider is built only when signing is enabled.
- `cla.yml` and `drafts.yml` run on `pull_request_target` with write access but never check out PR code. The CLA
  allow-list contains the wildcard `bot*`.

## 7. Trust boundaries

1. **Network → agent listener** – unauthenticated health endpoint, Basic sign-in, rights per feature, throttling per
   IP.
2. **Console ↔ agent identity** – a certificate pin with no revocation or renewal; the pins sync through the settings
   file.
3. **`.rhagent` in transit** – the device's private key.
4. **Agent (SYSTEM) → PowerShell with the user's full token** – PSRP content isn't inspected.
5. **Agent → internal network** as a jump host – lateral movement, limited only by policy.
6. **Service → desktop helper** – SYSTEM with UIAccess inside the user's session.
7. **Remote admin ↔ person at the device** – consent, view-only, privacy screen, secure desktop, resume token.
8. **Compromised device → console** – H.264, PNG, clipboard text and file names reach parsers on the admin's PC.
9. **Service ↔ LogonUI pipe** – plaintext password; DLL signer checked against the agent.
10. **The console's own user context** – clipboard, local forwarder, `%TEMP%` update script, DPAPI.
11. **Console ↔ synced settings and backups** – password-derived keys, PBKDF2 parameters taken from the file.
12. **Vendor update and telemetry channel.**
13. **Registry policy** – HKLM is authoritative for the agent; HKCU also counts for the console.
14. **MSI custom actions as SYSTEM** – take the enrollment file from the command line or the MSI's folder.
15. **Build and signing pipeline.**

## 8. Points worth a closer look

- The update MSI is installed without an Authenticode check, through a script in a user-writable folder.
- `.rhagent` holds the private key and its password in plaintext.
- The default firewall rule (`any`/`any`) and the default tunnel rule (any host and port) are wide open.
- The helper runs with UIAccess on, behind an undocumented "spike" switch.
- The Virtual Display Driver pipe isn't verified, and its settings folder at the root of `C:\` uses default ACLs.
- The agent serves an expired certificate; pins have no renewal or revocation.
- WebDAV over plain HTTP and plain FTP are allowed.
- Host names reach `DiagLog`.
- The console reads policy from HKCU, which the user can write.
- The credential provider's password pages aren't `VirtualLock`ed, and its log has a check-then-open gap.
- Signing secrets appear on the `java` command line, and the tag name is inlined into the release scripts.
- `docs/credential-provider-design.md` §16 still says there is no release signing certificate; `release.yml` already
  uses eSigner.
