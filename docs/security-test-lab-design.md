# RemoteHub – Security test design: attack cases for the release lab

Status: v2 draft (2026-10-21 – adds TB7 consent and view-only (SEC-DSK), the helper's writes into the user's hive,
the console's own IPC, and how the gate runs before a release, §1.1), written against branch `credential-provider-by-default` as inventoried in
[threat-model-technologies.md](threat-model-technologies.md) and the threat catalogue derived from it
(`RemoteHub – Threat Model and Automated Security Testing`, draft of 2026-10-20). Scope: every attack the threat
catalogue names, turned into a test the Hyper-V lab ([test-lab-spec.md](test-lab-spec.md)) runs before every release,
plus the few checks that belong in the unit tests or CI instead. Owner: Grischa Ernst

The functional test sheet ([test-lab-cases.md](test-lab-cases.md)) already proves that each feature works. This
document adds the other half of the release gate: **for each attack surface, the lab plays the attacker and proves
the attack fails the way the design says it must.** Where an attack succeeds today, the case stays in the gate as a
*known gap* with the fix that will make it pass, so it can't be forgotten.

---

## 1. Rules of the security gate

1. **Every release.** The `SEC-*` files in `lab/tests/` run on every client VM of the gate (test-lab-cases §2) and on
   `Server-2025-en-US` as the device, in the same run as the functional sheet. The PR report (§9) carries a
   security section: one row per threat, the covering cases and their results.
2. **An attack case is written from the attacker's side.** Each case names the attacker's position (§2), what they
   hold, the exact action, and *pass when* in three parts: the attack is refused or has no effect; the product stays
   healthy (next legitimate action works, no crash event, memory bounded); and, where the design says so, the attempt
   is audited with account and source address. A case never passes because something "looks fine".
3. **Pass rule.** No `SEC-*` case may be *Failed* unless it carries the `KnownGap` tag and appears in the register
   (§7) with an issue and an owner. A `KnownGap` case that **passes** is reported as "gap closed – remove the tag", so
   a fix is never silently unverified. *Skipped* only with a reason the lab prints (missing VM, tool or feature).
4. **No real malware, no real exploits.** Corpora are malformed inputs the lab generates (truncated, oversized,
   inconsistent lengths, hostile names, AMSI test string). Nothing is downloaded during a run; every attacker payload
   is built by the test and lives under `$EvidenceDir` or the guest's `C:\ProgramData\RemoteHubLab\attack\`.
5. **Attacker code runs where the attacker would be:** on the network (the host's harness and raw probes), in
   `normaluser`'s session (`Invoke-LabInSession`, a scheduled task with the standard user's token), as a SYSTEM task
   in the guest (an attacker who already has SYSTEM – only to prove boundaries that must hold even then), on a second
   VM (rogue device, 2VM), or as a rogue server the harness runs on the host.
6. **Secrets.** Memory dumps and packet captures taken by a case may contain the lab password; they are searched in
   the guest and **deleted before** evidence is collected – only the search result (count of hits) goes to
   `$EvidenceDir`. The lab password is never written to evidence (lab instructions).
7. **Same conventions as the functional sheet:** case IDs are stable and never reused, every `It` name starts with
   its ID and carries it as a tag, each file takes `-VmName` and `-EvidenceDir`, starts from the checkpoint, waits
   with timeouts, stays ASCII-only, and sets `Set-LabVmDirty` when it changed ACLs, accounts, policy or the network.
   Every `SEC-*` `It` also carries the tag `Security`, so the whole gate can be selected with one tag.

### 1.1 Running the gate before a release

The release checklist runs the security gate on the release candidate's MSIs, after the functional sheet is green
and before the release tag is pushed:

```powershell
.\lab\Start-LabRun.ps1 -Tag Security
```

`Invoke-LabTests.ps1` matches tags exactly, so `Security` selects every attack case and nothing else; a single family
runs with its file's ID (`-Tag SEC-TUN-01,SEC-TUN-02` or the family's tags). The run is accepted when the report's
Security section (§9) shows no *Failed* case outside the known-gap register (§7), no *gap closed* case still tagged,
and every *Skipped* case has a printed reason. The release PR links the run's `report.md`, and every open Critical or High
entry of the register is named in the PR with its owner's decision (ship with the gap, or wait for the fix).

The unit, fuzz and CI gates of §8 run on every PR in CI and are therefore already green on the release commit; the
lab gate is the part that needs real devices, accounts, tokens, ACLs, LogonUI and the network.

## 2. Attacker models and attack surfaces

The trust boundaries TB1–TB15 of the inventory (§7 there) group into seven attacker positions. Every threat in the
catalogue belongs to one of them; every `SEC-*` family is written from one of them.

| Attacker | What they hold | Boundaries | Families |
|---|---|---|---|
| **A1 Network attacker** – reaches port 58732 (inside the firewall scope) but has no account | packets | TB1 listener, TB2 identity | SEC-NET |
| **A2 Credentialed operator with too few rights** – an account the agent accepts, but not every right; or an admin who wants to leave no trace | Basic credentials, RemoteHub's client code or a raw client | TB1b PowerShell, TB4, TB5 jump host, TB7 consent and view-only | SEC-PS, SEC-TUN, SEC-DSK, SEC-LOG |
| **A3 Standard user at the device** – `normaluser`, signed in, no admin | a session, user-writable folders and pipes, window messages | TB6 helper, TB9 provider pipe, TB10 VDD pipe and folder, TB13 policy, TB14 MSI | SEC-UI, SEC-CP, SEC-VDD, SEC-MSI, SEC-POL |
| **A4 Compromised device** – the device's agent is replaced by a rogue one (or the device is hostile) and attacks the administrator's PC | a listener the console connects to, a device key when stolen | TB8 console parsers, TB2 pin, TB3 enrollment | SEC-DEC, SEC-ENR, SEC-ID |
| **A5 Attacker on the path** – rogue server or man in the middle between the console and a target, a share or the vendor | DNS, a rogue listener, a certificate of their own | TB2.3 RDP / WinRM / shares, TB12 update channel | SEC-RDP, SEC-SH, SEC-UPD |
| **A6 Code in the administrator's user context** – an infostealer or another Windows user on the admin PC | the user's files, DPAPI as that user, HKCU, `%TEMP%`, the clipboard, local pipes | TB10 user context, TB11 settings and backups | SEC-DPAPI, SEC-BAK, SEC-CON, SEC-UPD-02 |
| **A7 Supply chain and pipeline** – a tag name, a PR, a dependency | GitHub events, package versions | TB15 | CI only (§8) |

## 3. Threat catalogue → cases: coverage matrix

The catalogue's threat IDs (T1.1 … T16.6), what the code shows today (verified for this document, cited by file),
the functional cases that already cover part of the threat, and the `SEC-*` case that completes it. "Gap" means the
attack succeeds today and the case is a `KnownGap` (§7).

### TB1 – Network → agent listener (A1)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T1.1 Health leaks device ID, version, features, managed state | By design; six fields only, no `Server` header | S-TLS-04 | SEC-NET-01 (unreachable outside the firewall scope) |
| T1.2 Brute force / credential stuffing | `SignInGate`: 5 failures per source address in 5 min → 429; 8 sign-ins at once (`AgentHost.cs:259`) | S-THR-01…07, S-AUTH-07 | – |
| T1.3 Throttle bypass by `X-Forwarded-For` or IPv6 rotation | The key is `ctx.Connection.RemoteIpAddress`; no forwarded-headers middleware, so headers can't change it. Each IPv6 address counts alone; expired entries are forgotten (`SignInGate.Forget`) | – | SEC-NET-02, SEC-NET-03 |
| T1.4 Full linked token on network logon | Deliberate (S-AUTH-09 asserts it); Windows' `SeDenyNetworkLogonRight` still applies (S-AUTH-06) | S-AUTH-06, S-AUTH-09 | SEC-NET-04 (the token is the account's, never the service's) |
| T1.5 Expired certificate served, pin only, no renewal | `validOnly:false`; re-pin path = new certificate on update (A-UPD-04, A-SAME-02) | A-UPD-04, A-SAME-02, S-TLS-03 | SEC-NET-05, SEC-NET-06 |
| T1.6 No mutual TLS | By design (Basic after the pin) | S-TLS-03 | – |
| T1.7 Smuggling / oversized frames | 16 MB caps; HTTP/1.1 only | S-AUTH-03, P-SES-05, D-ROB-05/08, J-29 | SEC-NET-07 |
| T1.8 `MaxSessions` exhaustion | 20, shared by all endpoints | S-LIM-01, J-14, P-SES-07 | SEC-NET-08 (idle connections without a session) |

### TB1b / TB4 – PowerShell over the agent (A2)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T2.1 Arbitrary commands as the account | By design; right `PowerShell` + `IsInRole` on the agent | S-AUTH-04, S-POL-03, P-ADMIN, P-USER | SEC-PS-01, SEC-PS-02 |
| T2.2 PowerShell 5.1 pin vs logging and AMSI | 5.1 has script-block logging and AMSI; the v2 engine isn't installed on supported Windows | – | SEC-PS-04 |
| T2.3 PSRP parser DoS | 16 MB per line | P-SES-05 | SEC-PS-05 (entity expansion) |
| T2.4 Rights checked once per connection | By design (like Windows) | S-POL-02 | SEC-PS-03 |

### TB2 / TB3 / TB14 – Identity, enrollment, MSI (A3, A4)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T3.1 Poisoned pin through the shared settings file | The file is AES-256-GCM under the shared password; a byte changed anywhere fails the tag | C-SHARE-01…07 | SEC-ID-01 |
| T3.2 No revocation | By design; re-pin on update | A-UPD-04 | – (documented in SEC-NET-05) |
| T4.1 `.rhagent` holds PFX + password in plaintext | Fixed: format 2 holds RemoteHub's enrollment secret, the device makes its own key (agent-spec.md §5.5) | A-INST-08 (partly), A-CLIP-02, A-HAND-05 | SEC-ENR-01, SEC-ENR-03 |
| T4.2 Failed delete only logged | True | – | SEC-ENR-02 |
| T12.1 `enroll` custom action trusts a file next to the MSI | True; RemoteHub compares the fingerprint read back with its own (A-INST-01) | A-HAND-02…05 | SEC-MSI-01 |
| T12.2 Firewall rule `any/any` | Only when the scope is empty; `InstallOverRemoting` and the install dialog use the admin PC's address | A-INST-06, A-INST-13, J-09 | SEC-MSI-02 |
| T12.3 Credential provider on by default | By design (features on by default); `AUTOSIGNIN=0` leaves it out | C-AUTO-07, C-PROV-05 | – |
| T12.4 Inherited Program Files ACLs | Inherited = SYSTEM/Admins write, users read | – | SEC-MSI-04 |
| T11.3 MSI repair as SYSTEM from a user path | Repair not offered (`NoRepair`), `REINSTALL` needs the package | A-REPAIR-01 | SEC-MSI-03 |

### TB5 – Jump host (A2)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T5.1 Any host and port by default | By design; special addresses excluded | J-05, J-27, J-28 | SEC-TUN-03 |
| T5.2 IPv4-mapped IPv6 bypass | **Not a bypass:** `TunnelRules.Plain` maps `::ffff:a.b.c.d` to IPv4 before classification (`TunnelRules.cs`); zone IDs are stripped. NAT64 / 6to4 / Teredo forms are ordinary IPv6 addresses the agent connects to as such – they reach loopback only through a translating gateway on the device's network | J-05 | SEC-TUN-01, SEC-TUN-02 |
| T5.3 CGNAT, ULA, site-local not denied | Correct – they are private networks like 10/8, allowed by default and narrowed by `TunnelTargets`; not special addresses of the device | J-05 | SEC-TUN-03 |
| T5.4 Cloud metadata 169.254.169.254 | Link-local is special; allowed only by an explicit entry | J-05 | SEC-TUN-01 |
| T5.5 NTLM fallback, `SkipCNCheck` | RDP certificate pinned by RemoteHub through the forwarder; WinRM session pinned to the thumbprint | J-01, J-02, UI-JUMP-02, W-HTTPS-04 | SEC-TUN-08 |
| T5.6 Unreadable policy fails closed | True (`TunnelRules.Parse`, `AgentPolicy`) | J-28, S-POL-01 | – |
| T5.7 `MaxTunnelStreams` | 64 | J-13 | SEC-TUN-05 |
| (new) DNS rebinding | Resolve → filter → connect by IP | J-30 | SEC-TUN-04 |
| (new) Jump host reads content | Inner TLS end to end | – | SEC-TUN-07 |
| (new) Forwarder reachable from elsewhere | 127.0.0.1 + owner PID check | J-07 | SEC-TUN-06 |

### TB6 / TB7 – Desktop helper (A3)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T6.1 Helper with UIAccess | SYSTEM token, UIAccess on unless `HelperUIAccess=0` (`DesktopServices.cs:197-223`) | D-SHELL-01, D-CON-10 | SEC-UI-01 |
| T6.2 Window-message injection (shatter) | UIPI: SYSTEM windows ignore lower-integrity messages | D-BAN-06 | SEC-UI-02 |
| T6.3 Banner excluded from capture | Design (privacy-mode-design.md); the person always sees the banner on the console | D-BAN-01…10, D-AUD-01 | – |
| T6.4 Helper handles and job | Explicit handle list, kill-on-close job | D-ROB-03, A-UNINST-13 | SEC-UI-03 |
| T6.5 `clipboard-files` output untrusted | User token, parsed as untrusted | D-CLIP-09/10/12 | – (SEC-DEC-03 covers the hostile-name path) |
| (new) SYSTEM writes into the user's hive (`KeepRdpDrawing`, Ctrl+Alt+Del screen items) – a registry link planted by the user redirects the write | Values written through `Registry.Users\<sid>`; restore records in the agent's own folder | D-SHELL-* | SEC-UI-04 |
| (new) `SoftwareSASGeneration` lowered for the session | Set to 1 (services) only when no policy sets it; marker file; removed at uninstall (`InputInjector.cs:166`) | A-INST-22, A-UNINST-10, D-SAS-* | SEC-UI-05 |

### TB7 – Remote admin ↔ person at the device (A2)

The functional sheet proves each rule with RemoteHub's own client (D-ASK, D-PIN, D-NONE, D-VIEW, D-LOCK, D-RES,
D-PRIV). The attack cases send what RemoteHub's client never would – a raw client that lies about what it is allowed
to do – and prove the **agent** enforces it (review rule: policy, consent and view-only on the agent side).

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| (new) Control or extras from a view-only connection | `_viewOnly = start.ViewOnly \|\| !control`; input, privacy screen and SAS only `when _live && !_viewOnly` (`DesktopEndpoint.cs:101, 754-795`) | D-VIEW-01/02, D-PRIV-07 | SEC-DSK-01 |
| (new) Consent skipped or answered by the client | Modes filtered by right and policy (`DesktopEndpoint.cs:58-59, 134`); input dropped while asking | D-NONE-01/02, D-ASK-04/06 | SEC-DSK-02 |
| (new) Guessing the consent code | 6 random digits, 3 tries per connection (`DesktopEndpoint.cs:305, 734`); a new connection gets a new code and a new question | D-PIN-02 | SEC-DSK-03 |
| (new) Secure desktop without the right | Locked / sign-in session refused without `SecureDesktop` (`DesktopEndpoint.cs:139`) | D-LOCK-*, D-VIEW-03 | SEC-DSK-04 |
| (new) Another user's or a made-up session | Session looked up in the device's list; disconnected refused (`DesktopEndpoint.cs:95-99`) | D-CON-* | SEC-DSK-05 |
| (new) Resume token guessed, stolen or reused | Single use, same account, one minute, revoked on deliberate end (`DesktopResume`) | D-RES-02…05 | SEC-DSK-06 |
| (new) Clipboard and files above the policy level | Level from policy; off in view-only | D-CLIP-* | SEC-DSK-07 |
| (new) Watching without the person knowing | Banner for every connected session (a session automatic sign-in created shows the cover instead, C-END-08); privacy screen needs its right | D-BAN-*, D-PRIV-01 | SEC-DSK-08 |

### TB9 – Credential provider pipe (A3)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T7.1 Ticket in plaintext, no MAC | Pipe is SYSTEM-only (`D:P(A;;GA;;;SY)`), remote rejected, nonce; an attacker on the pipe is already SYSTEM | C-AUTO-06, C-AUTO-19 | SEC-CP-01, SEC-CP-02 |
| T7.2 Pipe squatting | `FirstPipeInstance`; the agent fails and says so | C-AUTO-18 | SEC-CP-03 (the provider's side of the same attack) |
| T7.3 Server identity by SCM PID, no service SID | `Channel.cpp:109-123`: SCM PID + image path + SYSTEM | – | SEC-CP-03; PID reuse not testable deterministically – documented |
| T7.4 Signer without revocation | Design: the device may be offline (`AutoSignInBroker.cs:344`) | C-AUTO-12, C-AUTO-21 | – (accepted; DEP-GATE covers the certificate) |
| T7.5 Password pages not `VirtualLock`ed | `CryptProtectMemory` + `SecureZeroMemory` | – | SEC-CP-05 (memory after the sign-in) |
| T7.6 Log folder check-then-open | Folder is SYSTEM/Admins only, so the race needs an admin | A-INST-07, A-CFG-06 | SEC-CP-06 |

### TB10 – Virtual Display Driver (A3)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T8.1 `MTTVirtualDisplayPipe` server not verified | Fixed 2026-10-21: anonymous connection, the command goes only to `WUDFHost.exe` (`VirtualDisplay.Reload`) | – | SEC-VDD-01 |
| T8.2 `C:\VirtualDisplayDriver` with default ACLs | Fixed 2026-10-21: the agent's own folder (protected ACL, SYSTEM / Administrators write); links and foreign folders removed, a file there deleted rather than written through (`VirtualDisplay.PrepareSettingsFolder`) | A-INST-20, A-UNINST-11 | SEC-VDD-02 |
| T8.3 SignPath certificate in TrustedPublisher | By design; exact thumbprint; removed when the agent added it | A-INST-20/21, A-UNINST-11 | – |

### TB8 – Compromised device → console (A4)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T9.1 Crafted H.264 to Media Foundation in-process | Mitigated: decoded in `RemoteHubDecoder.exe`, in an AppContainer with no capabilities and a job; RemoteHub checks every answer, restarts a decoder that misbehaves, gives up after 3 restarts a minute and audits it; the picture size and message length are checked first (decoder-isolation-design.md §3, §12) | D-VID-* (well-formed only); SEC-DEC-06 (the sandbox on the installed build); unit tests `DecoderProcessTests` (incl. fuzzing the decoder process), `DeviceImageTests` | SEC-DEC-01 |
| T9.2 Crafted PNG to WPF `BitmapDecoder` | Mitigated: decoded in the same sandboxed process; PNG, BMP and JPEG only with their own decoders, the size read from the header by RemoteHub first (≤ 8192 × 8192), the clipboard data rebuilt from pixels (decoder-isolation-design.md §3.5, §3.6) | D-CLIP-22 (device side only); SEC-DEC-06; unit tests `DecoderProcessTests`, `DeviceImageTests`, `ClipboardOfferTests` | SEC-DEC-02 |
| T9.3 Clipboard path traversal on the console | `SafeRelativePath` + `GetFullPath` under `%TEMP%\RemoteHub\Clipboard` (`AgentDesktopSession.cs:667`) | D-CLIP-09/10 (device side) | SEC-DEC-03 |
| T9.4 Oversized / one-byte-at-a-time clipboard | 100 MB, 1000 items, 256 KB chunks | D-CLIP-08/20/21, D-ROB-08 | SEC-DEC-04 |
| (new) Hostile Hello / session list | – | – | SEC-DEC-05 |

### TB2.3 – Console → targets (A5)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T10.1 RDP server authentication *Warn* | `RdpSession.cs:191`: 0 only when RemoteHub checked the certificate itself | R-RDP-01, UI-JUMP-02 | SEC-RDP-01 |
| T10.2 Plain FTP allowed | The editor warns (network-shares.md) | S-FTP-03 | SEC-SH-01 |
| T10.3 WebDAV over HTTP | The editor warns; redirects off | S-DAV-02, S-DAV-04 | SEC-SH-01 |
| T10.4 SMBLibrary without explicit signing | Verified: SMBLibrary 1.5.8 signed only when the server required it. Fixed 2026-10-21: always signs, guest sessions refused (`ManagedSmbShare.RequireSigning`) | S-SMB-02 | SEC-SH-02 |
| T10.5 Certificate to CurrentUser\Root | Verified: any presented certificate could be added. Fixed 2026-10-21: only an end-entity certificate (`RemotingSetup.IsEndEntity`), behind the Windows confirmation | UI-TL-08, W-TRUST-01 | SEC-RDP-03 (unit test) |
| T10.6 `.rdp` import with `password 51:b` | Risk list before connecting (`RdpFileParser.Risks`) | R-RDP-08, C-MSI-03 | SEC-RDP-02 |
| (new) Host key / certificate changed on a share | Asked before anything is sent | S-SFTP-03, S-FTP-02 | SEC-SH-03 |
| (new, 0.22.0) Hostile SSH server output to the terminal emulator | Mitigated: RemoteHub's own parser, bounded (32 parameters, values ≤ 65535, repeats ≤ one screen, OSC/DCS/APC strings dropped unstored); no clipboard write (OSC 52), no title, no window reports (`CSI t`) – it answers only DA, DSR and the cursor position (network-shares.md §6.3) | S-SSH-01, S-SSH-03; unit tests `TerminalTests` (incl. 1.2 MB of escape-heavy random output, an endless OSC) | – (unit tests; a rogue SSH server case is a candidate for SEC-SHARE) |
| (new, 0.22.0) Paste into a terminal runs commands | A paste of several lines without bracketed paste is asked first; with bracketed paste ESC is taken out, so a pasted end marker can't end the paste | S-SSH-06; unit tests `TerminalTests` | – |
| (new, 0.22.0) SSH private keys at rest | Sealed in `secrets` like passwords, never in a record; the editors show only the fingerprint; a passphrase follows `AllowSavedPasswords` | S-SSH-04, S-SSH-05; unit tests `SshKeyTests` | SEC-BAK / SEC-DPAPI cover the store |

### TB12 – Update channel (A5, A6)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T11.1 MSI verified by SHA-256 and size only, no Authenticode | Fixed 2026-10-21: signed by the running build's publisher, chain trusted (`Authenticode.Refusal`); an unsigned build (test release) has only the checksum | C-UPD-02/03/06/07/08 | SEC-UPD-01 |
| T11.2 Script and MSI in user-writable `%TEMP%` before elevation | Fixed 2026-10-21: the elevated step copies the MSI to `%ProgramFiles%RemoteHub Updates` (SYSTEM / Administrators) and checks hash and signature there (`InstallUpdate.ps1`, started by RemoteHubUpdate.exe) | C-UPD-04 | SEC-UPD-02 |
| (new, 0.22.0) The approval names Windows PowerShell; the elevated step inherits what the user set in `HKCU\Environment` (a .NET profiler loads into it) | Fixed 2026-10-27: Windows asks approval for RemoteHubUpdate.exe (native, signed with the other programs, "RemoteHub Update" in its file properties, runs only elevated). It takes four checked values – a local `.msi` path, the SHA-256, a process id, an id – and runs only `InstallUpdate.ps1` from its own folder, and only when that folder is `%ProgramFiles%\RemoteHub` (fixed 2026-10-28: a copy elsewhere would have run a script a user planted next to it, elevated); the publisher the step checks against comes from its own signature, never from its command line; Windows PowerShell gets an environment of system folders only. A local build or a copy run from elsewhere still asks for Windows PowerShell | C-UPD-11 | SEC-UPD-04, SEC-UPD-05 |
| T11.4 Telemetry correlation / redaction | Redacted; random install ID; off by policy | C-TEL-01…03, C-CRASH-01 | – |
| (new) Rogue update server (TLS, payload size) | Host allow-list, HTTPS only | C-UPD-09 | SEC-UPD-03 |

### TB10 / TB11 / TB13 – Console user context, backups, policy (A6, A3)

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| T13.1 Backup envelope without associated data | Fixed 2026-10-21: version 4 seals each section with the format, version, section, salt and the list of sections (`Backup.AssociatedData`) | C-BAK-02/04 | SEC-BAK-01 |
| T13.2 Iteration count read from the file, 10k–10M | True (`CheckIterations`); RemoteHub writes 600k | C-BAK-03 | SEC-BAK-02 |
| T13.3 `.rdpmbackup` parsing | Imported by format | C-BAK-03 | SEC-BAK-01 corpus |
| T14.1 `db.key` under DPAPI CurrentUser | Design: same-user code can read it; the startup lock raises the bar | C-DB-01/02, C-LOCK-01/10 | SEC-DPAPI-01 |
| T14.2 Auto-lock is a UI lock | Design | C-LOCK-02 | SEC-DPAPI-02 (what must *not* stay in memory) |
| T14.3 Passwords as plain strings | Design; documented | – | SEC-DPAPI-03 (never on disk) |
| T14.4 Recordings unencrypted | Profile folder ACL | C-REC-* | SEC-DPAPI-04 |
| (new) Another user feeds the console's single-instance pipe (`.rdp` paths, arguments) | `PipeOptions.CurrentUserOnly` on both ends (`SingleInstance.cs:46, 68`) | C-MSI-03 | SEC-CON-01 |
| (new) Passwords left on the clipboard, in history or cloud clipboard | `SecureClipboard`: excluded formats, cleared after the timeout if still ours | – | SEC-CON-02 |
| T15.1 Console reads HKCU policy | True, HKLM wins per value (C-POL-07). Whether the user can write `HKCU\Software\Policies` is **unverified** – Windows protects that key by default | C-POL-07 | SEC-POL-01 |
| T15.2 `Connections` forced through reflection | – | POL-15 | SEC-POL-01 |
| T15.3 Agent policy fails closed | True | S-POL-01, J-28 | – |

### Logging

| Threat | Verified today | Existing cases | SEC case |
|---|---|---|---|
| Host names in `DiagLog` | Fixed 2026-10-21: the forwarder logs a hash of its route and the refusal's kind (`JumpForwarder.LogId`) | – | SEC-LOG-01 |
| Secrets or content in agent logs and events | Checked by the functional sheet | A-CFG-05, D-AUD-02, C-AUTO-06 | – |
| Audit written before the operator can act | – | D-AUD-01 | SEC-LOG-02 |

### TB15 – Build and signing pipeline (A7)

Not lab cases – CI gates, specified in §8:

| Threat | Verified today | Gate |
|---|---|---|
| T16.1 Signing secrets on the `java` command line | Fixed 2026-10-21: a Java argument file, deleted after use (`build/JavaArgFile.ps1`) | SEC-CI-01 |
| T16.2 `${{ github.ref_name }}` inlined into release scripts | Fixed 2026-10-21: through `env: TAG` | SEC-CI-02 |
| T16.3 `pull_request_target` with write, `bot*` wildcard in the CLA allow-list | Fixed 2026-10-22: the allow-list names its two bots exactly; `Test-Workflows.ps1` fails a `pull_request_target` workflow with a checkout. The write rights stay – the CLA action and the draft workflow need them | SEC-CI-03 |
| T16.4 ClamAV from apt, unpinned | Fixed 2026-10-22: Cisco-Talos' package of a pinned version (1.5.4), checked against its published SHA-256, the version checked after the install | SEC-CI-04 |
| T16.5 No SBOM or provenance; sidecar hashes in the same job | Fixed 2026-10-22: a CycloneDX SBOM and GitHub build provenance (`gh attestation verify`) for every release file. The `.sha256` files stay in the release job – the provenance is the proof, they only catch a broken download | SEC-CI-05 |
| T16.6 `credential-provider-design.md` §16 contradicts `release.yml` on signing | Fixed 2026-10-22: releases are signed since 0.20.2, the design says so | SEC-CI-06 |
| §2 of the catalogue: FluentFTP 55.0.0, .NET runtime < 10.0.13 | FluentFTP 55.1.0 since 2026-10-21 (its release notes: data connections no longer accept any certificate; the draft's advisory ID wasn't found on GitHub), held by the DEP-GATE (`build/minimum-versions.json`). The runtime finding doesn't apply as written: RemoteHub and the agent aren't self-contained (`SelfContained` false) – they run on the .NET 10 runtime installed on the PC, which Microsoft Update keeps current, and no build carries a runtime | DEP-GATE |

### 3.1 Corrections to the threat catalogue draft

Writing the cases against the code (and fixing the gaps, 2026-10-21) showed six places where the draft overstates or
misplaces a threat. The draft should be updated before sign-off:

- **T5.2 (Critical → Low).** IPv4-mapped IPv6 and scoped addresses are normalised before the filter
  (`TunnelRules.Plain`). The NAT64, 6to4 and Teredo prefixes are not translated by the device; they are plain IPv6
  destinations and reach an internal host only if the device's network translates them. SEC-TUN-02 pins that down
  and leaves "treat the well-known NAT64 prefix as special" as a product decision, not a gap.
- **T5.3 (High → design decision).** 100.64/10, fc00::/7 and fec0::/10 are private networks. The agent's deny list
  is about addresses that are *never a remote device*; private networks are what a jump host exists for, and
  `TunnelTargets` narrows them. SEC-TUN-03 proves the narrowing works.
- **T12.2.** The `any/any` rule only comes from an enrollment with an empty scope. RemoteHub's two install paths
  fill the scope with the admin PC's address (A-INST-06, J-09). SEC-MSI-02 asserts the dialog never leaves it empty
  by accident.
- **T1.3.** No forwarded-header middleware is registered, so `X-Forwarded-For` can't move the counter. Per-address
  counting of IPv6 is a design choice; SEC-NET-03 bounds its cost (memory, 4625 count) rather than demanding a /64
  rule.
- **T13.1.** Moving a section from one backup into another with the same password never worked: each file has its
  own salt, so its own key. What a section without associated data allowed was taking one *out* of a file (the
  notes or scripts dropped, the rest imported) and relabelling the version; version 4 binds them (SEC-BAK-01).
- **§2, FluentFTP.** The advisory ID the draft cites (GHSA-7vvr-w3q4-373p) isn't on GitHub's advisory database; the
  fix itself is real – FluentFTP 55.1.0's release notes say data connections no longer accept any certificate.

### 3.2 Completeness

Checked for v2 against the catalogue and the inventory:

- **Every catalogue threat** T1.1–T16.6 has a row above. Each one either has a `SEC-*` case, is fully covered by
  named functional cases, is a CI gate (§8), or is accepted by design with the reason in its row (T1.6, T3.2, T6.3,
  T7.4, T12.3, T15.3 and the library findings of the catalogue's §2, which DEP-GATE covers).
- **Every trust boundary** TB1–TB15 of the inventory §7 has at least one attack case or gate. TB7 (consent,
  view-only, privacy screen, secure desktop, resume) wasn't in the catalogue; SEC-DSK covers it.
- **Every point of the inventory §8** is a case: update MSI (SEC-UPD-01/02), `.rhagent` (SEC-ENR-01…03), firewall
  and tunnel defaults (SEC-MSI-02, SEC-TUN-03), UIAccess (SEC-UI-01/02), VDD (SEC-VDD-01/02), expired certificate
  (SEC-NET-05), clear-text shares (SEC-SH-01), `DiagLog` host names (SEC-LOG-01), HKCU policy (SEC-POL-01),
  provider memory and log (SEC-CP-05/06), pipeline secrets and tag injection (SEC-CI-01/02), doc drift (SEC-CI-06).
- **Every local IPC channel** of the inventory §4: provider pipe (SEC-CP-01…03), helper pipes (SEC-UI-03),
  `clipboard-files` (SEC-DEC-03, D-CLIP), VDD pipe (SEC-VDD-01), single-instance pipe (SEC-CON-01), clipboard
  (SEC-CON-02, SEC-DSK-07).

A new feature that adds a listener, a pipe, a file the agent reads, a parser of device data or a policy value adds its
row here and its attack case in the same PR as its functional cases.

## 4. The attack cases

Column meanings as in test-lab-cases §3 (**How**: PS, H, C, S, U, N, M, E; **P**; **St**: ○ planned until the
file exists). **Pos** is the attacker position of §2. Every case starts from the checkpoint with the agent installed
by `Install-LabAgent` (scope = the host) unless it says otherwise.

### 4.1 SEC-NET – the network attacker (`lab/tests/SEC-NET.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-NET-01 | Fingerprinting from outside the scope: the host connects to 58732 from a second address that isn't in the firewall scope (a second host adapter address, or the jump host VM's address) | TCP connect fails (no SYN-ACK); from the scoped address health still answers with only the six fields (S-TLS-04); the agent log has no line for the blocked attempt (the firewall dropped it) | H N PS | 1 | A1 |
| SEC-NET-02 | Throttle evasion by headers: 5 wrong passwords, each with a different `X-Forwarded-For`, `Forwarded: for=`, `X-Real-IP` and `True-Client-IP` value; then the right password | 401 ×5, then **429** – the headers didn't move the counter; event 2000 names the socket address, never a header value | H E | 1 | A1 |
| SEC-NET-03 | Throttle evasion by source rotation: the host adds 20 temporary IPv6 addresses on the VM's link and sends 5 wrong passwords from each (100 attempts), then the right password from each | each address is blocked on its own (20 × 429); Security log 4625 count ≤ 100 (never more than 5 per address reached Windows); the agent's working set grew < 20 MB; 6 minutes later every address is forgotten (the right password works, `SignInGate.Forget`) | H N PS E | 2 | A1 |
| SEC-NET-04 | The token is the account's, never the service's: sign in as a member of a custom allowed group (A-INST-17, `normaluser` in "RH Operators") and run `whoami /all`, try to read `agent.json` and to open the service process for write | the session runs as that account (Medium integrity, no `SeDebugPrivilege`, no `SeTcbPrivilege`); `agent.json` and the log folder are access denied; `OpenProcess(PROCESS_VM_WRITE)` on the service PID fails; the agent's own listener socket isn't in the session's handle table | H PS | 1 | A2 |
| SEC-NET-05 | Expired device certificate: move the guest clock 2 years + 1 day ahead (SMOKE-08 restored afterwards), restart the agent | the agent still serves the certificate and the pinned connection works (no outage by design); RemoteHub's *Test RemoteHub Agent* names the expiry and offers *Create a new device certificate* (A-UPD-04 is the re-pin path); the agent's start line logs the expiry | H PS U E | 2 | A1 |
| SEC-NET-06 | Rogue listener with the right name: a harness listener on 58732 of a second address with a certificate `CN=<deviceId>, O=RemoteHub Agent` and the same SAN, different key; RemoteHub's connection re-pointed there | RemoteHub stops at the pin (S-TLS-03): the rogue listener records a TLS handshake but **no HTTP request** and no Basic header; the UI says the pin doesn't match and offers nothing automatic | H PS | 1 | A4/A5 |
| SEC-NET-07 | HTTP and WebSocket abuse corpus (harness `probe --kind abuse --case ...`): two `Content-Length` headers; `Transfer-Encoding: chunked` plus `Content-Length`; chunk extensions and a chunk size of `0xFFFFFFFFFFFFFFFF`; a 64 KB header line and 1,000 header lines; a request line of 32 KB; a slow-loris that sends one header byte per second for 60 s; an HTTP/2 preface; `Upgrade: websocket` without a key; a WebSocket frame whose length field says 2^63; reserved bits set; an unmasked client frame; 10,000 pings in a second; a fragmented text frame never finished | each answered with 400/413/431 or closed within the Kestrel timeout (≤ 35 s); health from a clean client answers during and after; no event 3100; the service's working set grew < 50 MB; no second request was ever parsed from a smuggling body (the body carries a health request whose answer must **not** appear) | H E | 1 | A1 |
| SEC-NET-08 | Connection exhaustion without sessions: 200 TCP connections held open without TLS, then 50 TLS handshakes held without a request, for 90 s | a sign-in from a clean client completes within 10 s during the hold; after the hold the connection count is back to baseline (`Get-NetTCPConnection` on the guest); `MaxSessions` isn't consumed by idle connections (S-LIM-01 counts sessions only) | H PS | 2 | A1 |

### 4.2 SEC-PS – the operator with too few rights (`lab/tests/SEC-PS.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-PS-01 | Rights are enforced on the agent, not in RemoteHub's UI: a raw client (harness `probe --kind ws`) opens `/v1/powershell`, `/v1/desktop` and `/v1/tunnel` with an account that lacks the matching right (`Rights\PowerShell`, `Rights\Desktop`, `Rights\Tunnel` set to "RH Others") | 403 before any work: no `powershell.exe` child of the service, no helper started, no outbound connection (A-CFG-07); event 2000 per refusal with account, source and the right's name | H PS E | 1 | A2 |
| SEC-PS-02 | Escaping the account: through the agent, as the "RH Operators" member, try `Start-Process -Verb RunAs`, `psexec -s`-style service creation (`sc create`), writing `HKLM\SOFTWARE\Policies\RemoteHub\Agent`, replacing `RemoteHubAgent.exe`, and stopping the service | every attempt is access denied; the service keeps running; the session's token has Medium integrity (`whoami /groups`); nothing in the agent's folders changed (hashes before and after) | H PS | 1 | A2 |
| SEC-PS-03 | Rights change under an open session: open a PowerShell session as a group member, remove the member from the group (and separately: set `Rights\PowerShell` to another group) | the open session continues until it ends (Windows semantics, documented); a **new** connection is refused (403) at once (S-POL-02); the session's end is audited normally | H PS E | 2 | A2 |
| SEC-PS-04 | Hiding from logging: with `EnableScriptBlockLogging` and `EnableModuleLogging` on in the VM, run a marker script block through the agent; run the AMSI test string (`'AMSI Test Sample: 7e72c3ce-861b-4339-8740-0ac1484c1386'`); try `powershell.exe -Version 2` from inside the session | event 4104 (Microsoft-Windows-PowerShell/Operational) carries the marker; the AMSI string is blocked by Defender ("This script contains malicious content") and event 1116 is written; `-Version 2` fails ("not installed") – the 5.1 pin doesn't lose script-block logging or AMSI | H PS E | 1 | A2 |
| SEC-PS-05 | Parser bombs: a 16 MiB + 1 byte PSRP line (P-SES-05), a PSRP message carrying an XML entity-expansion payload ("billion laughs", 10 levels × 10), a deeply nested CLIXML object (5,000 levels), and 1,000 messages of 15 MB in a row | the session ends with "Message too large" or a protocol error, never a hang; the service's working set grows < 200 MB and returns; the next session works; no event 3100 | H PS E | 2 | A2 |

### 4.3 SEC-ID / SEC-ENR – identity and enrollment (`lab/tests/SEC-ENR.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos | Needs |
|---|---|---|---|---|---|---|
| SEC-ENR-01 | The secret leaves nothing behind: after `InstallOverRemoting`, a hand install (A-HAND-01) and a clipboard install (A-CLIP-01), search the device and the admin PC for the enrollment secret (Base64 and raw) and the `.rhagent` file | no hit outside `LocalMachine\My` and its key container: not in user or Windows temp, `C:\Windows\Installer`, the MSI log, the agent log, the event log, `%TEMP%\RemoteHub-Agent-*` on the admin PC, RDP clipboard history; the private key isn't exportable (A-INST-04) | PS U | 1 | A4 | 2VM |
| SEC-ENR-02 | The file can't be deleted: hold the `.rhagent` open with `FileShare.None` from another process during the hand install | the install succeeds; the warning reaches the operator (RemoteHub's install result / the MSI log line "could not be deleted") and the agent log; the test then deletes it and asserts nothing else references it | PS S E | 2 | A3 |
| SEC-ENR-03 | Stolen enrollment: copy the `.rhagent` before it is used, install it on the device **and** on a second VM | the two devices have different device IDs and certificates, neither key exportable, and the file holds no key; RemoteHub's claim pins each as itself; the second device's claim under the first one's name is refused (`OtherName`) and it doesn't answer to the first one's pin; after *Replace and revoke* neither can be claimed (`RevokedSecret`) | H PS | 1 | A4 | 2VM |
| SEC-ID-01 | Poisoned pin in the shared settings file: with settings on an SMB share (C-SHARE-01), change the device's pin bytes in the file without the password; then with the password (an insider) | without the password: RemoteHub refuses the file ("damaged or not from RemoteHub"), keeps its local copy, audits "shared file rejected", trusts nothing new; with the password: the pin changes – documented as the shared-password trust boundary, and the audit log on every PC that loads it records "pin changed for <device>" | U PS | 1 | A4/A6 | SMB |
| SEC-ID-02 | Replay of an old identity after re-pin: after A-UPD-04 (new certificate), a rogue listener presents the **old** certificate (from the test's own copy of the old enrollment) | RemoteHub refuses it (pin mismatch, nothing sent); the old pin is gone from the settings | H PS | 2 | A4 | |

### 4.4 SEC-MSI – the installer (`lab/tests/SEC-MSI.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-MSI-01 | Planted enrollment: `normaluser` drops a `.rhagent` of their own (made with `New-LabEnrollment`) into the folder the admin installs from; the admin's own file is given as `ENROLLMENT=<path>` in one run and not at all in another | with `ENROLLMENT=`: the admin's file wins, the planted one is ignored and left alone; without: the device enrolls the planted identity **and RemoteHub refuses to mark the connection installed** ("answers with another identity" – fingerprint read back ≠ pin, A-INST-01), so the operator never pins the attacker's key | S H PS | 1 | A3 |
| SEC-MSI-02 | Open firewall by accident: RemoteHub's install dialog with the scope field cleared; the hand-made enrollment with an empty scope | the dialog pre-fills this PC's address and requires an explicit confirmation ("Allow from any address?") before producing an empty scope; an empty scope produces `remoteip=any` (A-INST-13) and RemoteHub's connection shows "reachable from any address" in its details | U H PS | 2 | A3 |
| SEC-MSI-03 | Repair and reinstall from a user path: `normaluser` copies the cached MSI next to a planted `RemoteHubAgent.exe` and `.rhagent` in their temp and runs `msiexec /fa`, `/fv` and `/i ... REINSTALL=ALL` | every run fails without elevation (1925 / UAC refused – nothing runs as SYSTEM); the planted exe never ran (its marker file is absent); the installed exe's hash is unchanged; no enroll line in the log (A-REPAIR-01) | S PS E | 1 | A3 |
| SEC-MSI-04 | Program folder and service: as `normaluser`, write into `Program Files\RemoteHub Agent`, replace a DLL, change the service's binary path or stop it (`sc config`, `sc stop`), read `%ProgramData%\RemoteHub\Agent` | all access denied; `sc sdshow RemoteHubAgent` grants users at most query rights; the image path is quoted; the folder ACL is the inherited Program Files ACL (no user write ACE) | S PS | 1 | A3 |

### 4.5 SEC-TUN – the jump host as a pivot (`lab/tests/SEC-TUN.Tests.ps1`, needs the jump host)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-TUN-01 | Address-bypass matrix (data-driven, `lab/RemoteHub.TestLab/data/tunnel-targets.json`): for each special target – `127.0.0.1`, `127.1.2.3`, `::1`, `0.0.0.0`, `::`, `169.254.169.254`, the jump host's own adapter addresses, `224.0.0.1`, `ff02::1`, `255.255.255.255`, `fe80::1` – every spelling RemoteHub could send: dotted, `::ffff:a.b.c.d`, `::ffff:7f00:1`, bracketed, with zone (`fe80::1%12`), trailing dot, `127.1`, `0177.0.0.1`, `0x7f.0.0.1`, `2130706433`, and a hosts-file name resolving to it (A and AAAA) | every Open is refused with reason `policy` and audit 2200 "(own or special address)" **or** rejected as not an address; a listener the test runs on the jump host's loopback and link-local port **sees no connection** for any case; no policy set (defaults) | H PS E | 1 | A2 |
| SEC-TUN-02 | Translated prefixes: `64:ff9b::7f00:1` (NAT64), `2002:7f00:0001::` (6to4), `2001:0:...` (Teredo) for loopback and the metadata address; with no policy and with `TunnelTargets = 192.168.231.0/24` | with the IPv4 policy: refused (policy); without: the agent connects **outbound as IPv6** to that address (the attempt leaves the device – `pktmon` on the jump host shows a SYN to the IPv6 address, nothing on loopback) and fails as unreachable; result recorded as evidence for the product decision "treat the NAT64 well-known prefix as special" | H PS N | 2 | A2 |
| SEC-TUN-03 | Lateral movement narrowed by policy: `TunnelTargets = 192.168.231.0/24`, `TunnelPorts = 3389,5986`; Opens to `10.0.0.1`, `100.64.0.1`, `fc00::1`, `fec0::1`, `192.168.231.20:445`, `192.168.231.20:3389`, a name resolving into the range (J-30) | only the two in-range, in-port Opens connect; every other one is refused with `policy` and 2200; a refusal names the entry that applied | H E | 1 | A2 |
| SEC-TUN-04 | DNS rebinding: a hosts-file name with two addresses, allowed and `127.0.0.1`; and a name changed from allowed to loopback between RemoteHub's resolve and the agent's | the agent resolves itself, filters, and connects **only** to the allowed address (J-30); the loopback listener sees nothing; audit names the address connected to | H PS E | 1 | A2 |
| SEC-TUN-05 | Port scan through the tunnel: 1,000 Opens to closed ports on the inner network as fast as RemoteHub's client allows | never more than `MaxTunnelStreams` (64) pending at once (J-13); each refused with `unreachable` / `timeout`; **every** Open is in the jump host's audit (1200/2200 with target and port) – the scan is visible to the administrator of the jump host; the agent stays healthy | H E PS | 2 | A2 |
| SEC-TUN-06 | Forwarder from elsewhere: while an RDP forwarder is open on the admin VM, another VM connects to the admin VM's address on the forwarder's port; another process on the admin VM connects locally (J-07) | the listener is bound to `127.0.0.1` only (`netstat` on the admin VM – nothing on 0.0.0.0); the remote connect fails; the local foreign connect is refused and logged (`DiagLog`, without the host name – SEC-LOG-01) | H PS U | 1 | A5 |
| SEC-TUN-07 | The jump host reads the traffic: `pktmon` on the jump host while a WinRM-HTTPS tool session, an agent desktop and an SMB copy run through the tunnel; the capture is searched in the guest for the lab password, a marker command and a marker file's content | zero hits for the password and the command; the SMB marker is found **only** if the share was opened without encryption (SEC-SH-02 decides the expected value); capture deleted before evidence | PS N | 1 | A2/A5 |
| SEC-TUN-08 | Rogue inner server through the forwarder: swap the device behind the jump host for one with a different RDP certificate at the same inner address; the same for WinRM HTTPS | RDP: RemoteHub's forwarder pins the certificate it fetched (J-01) – the swapped server is refused before CredSSP sends credentials (no 4625/4624 on the rogue); WinRM: the session pinned to the thumbprint fails with the certificate error, nothing authenticated (`SkipCNCheck` never means "any certificate") | H PS E | 1 | A5 |

### 4.6 SEC-UI – the person at the device attacks the helper (`lab/tests/SEC-UI.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-UI-01 | Reaching the helper's process: as `normaluser` in the session, `OpenProcess(PROCESS_VM_READ / PROCESS_TERMINATE)` on the helper, `taskkill`, `CreateRemoteThread`, debugging it | all access denied; the session continues; the helper's token is SYSTEM, System integrity, `TokenUIAccess` as the policy says (D-SHELL-01), and `normaluser`'s token has no privilege that reaches it | S PS | 1 | A3 |
| SEC-UI-02 | Shatter: from a Medium-integrity process in the session, `SendMessage`/`PostMessage` to every helper window (banner, consent dialog, PIN dialog, privacy overlay): `WM_CLOSE`, `WM_DESTROY`, `WM_QUIT`, `WM_COMMAND` with the Allow / Disconnect IDs, `WM_LBUTTONDOWN` at the button's coordinates, `WM_SETTEXT`, `WM_COPYDATA`, `WM_TIMER` with a callback, `WM_HOTKEY`, `SetWindowPos`, `SetForegroundWindow`; `SendInput` while the privacy screen blocks input | the consent dialog stays unanswered (the connection is still Waiting after 10 s), no window closed or moved, the privacy screen stays, `SendInput` from the session doesn't reach the desktop; the helper never calls `ChangeWindowMessageFilterEx` to allow those messages (checked once in code review, asserted here by effect) | S H C | 1 | A3 |
| SEC-UI-03 | Orphans and handles: kill the service process while a session runs; inspect the helper's handles before (`NtQuerySystemInformation` through a guest script run as SYSTEM) | the helper ends within 5 s of the service (kill-on-close job, D-ROB-03); before that, the helper holds no handle to the listener socket, the device certificate's key or `agent.json` – only the two anonymous pipes to the service | PS S | 2 | A3 |
| SEC-UI-04 | Redirected hive writes: before connecting, `normaluser` replaces `HKCU\Software\Microsoft\Terminal Server Client` (and the key of the Ctrl+Alt+Del screen items) by a registry symbolic link (`REG_OPTION_CREATE_LINK`, `SymbolicLinkValue`) to `HKLM\SOFTWARE\RemoteHubLabTarget`, a key the test created with an Administrators-only ACL; then connect, disconnect, and restart the service | nothing is written under `HKLM\SOFTWARE\RemoteHubLabTarget` (Windows refuses links from a user hive into a machine hive, and the agent must not open the key in a way that follows one); the session still works or the setting is skipped with a log line; the restore record in the agent's folder stays readable only by SYSTEM and Administrators (`rdp-minimized-<sid>.txt`) | S PS E | 1 | A3 |
| SEC-UI-05 | Weakened Secure Attention Sequence: with no policy set, after a session, look at `SoftwareSASGeneration`; with the policy set to 3, then 0 | the agent sets 1 (services only), never 3 (which would let any UIAccess app, e.g. a user's, send Ctrl+Alt+Del); a policy value is never overwritten (A-INST-22); `normaluser` can't delete or edit the marker `sas.agent` to make uninstall remove a policy-set value (access denied); after uninstall the value is as before (A-UNINST-10) | PS S | 2 | A3 |

### 4.7 SEC-CP – the credential provider pipe (`lab/tests/SEC-CP.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-CP-01 | Who can open the pipe: `\\.\pipe\RemoteHubAgent.AutoSignIn` opened as `PSAdmin` (elevated, not SYSTEM), as `normaluser`, as SYSTEM from a scheduled task that isn't LogonUI, and from another VM (`\\<device>\pipe\...`) | DACL exactly `D:P(A;;GA;;;SY)`, owner SYSTEM, message mode; PSAdmin and normaluser get access denied (5); the SYSTEM task connects and is refused by the client check ("isn't LogonUI" / "session N isn't the console", C-AUTO-19) with a log line; the remote open fails; a later automatic sign-in still works | PS S E | 1 | A3 |
| SEC-CP-02 | Ticket replay and lifetime (with the provider test build of C-PROV-03): start an automatic sign-in, kill `LogonUI.exe` after *Pending* and before *Claim*; separately hold the ticket unclaimed for 61 s | the restarted LogonUI's provider gets nothing to claim (or a fresh ticket for the same request, never the old one); after 61 s the ticket is gone and the sign-in fails with 2104 and a clear message; the password isn't in the agent log, the provider log or the event log at any point (C-AUTO-06); the nonce from *Pending* must match *Claim* (a Claim with another nonce is refused) | H PS C E | 1 | A3 |
| SEC-CP-03 | Rogue pipe server seen by the provider: a SYSTEM task creates the pipe before the service starts (C-AUTO-18 covers the agent's side), then the sign-in screen loads the provider | the provider refuses the server (SCM PID ≠ server PID: `Channel.cpp:109`) – its trace says so, no *Claim* is sent, the sign-in screen stays usable (inert, C-PROV-02); PID reuse between the SCM query and the connect can't be forced deterministically and is documented as accepted with the image-path and SYSTEM checks as the second line | PS C E | 1 | A3 |
| SEC-CP-05 | The password in memory: 5 s after *SignedIn* (C-AUTO-05), take minidumps of `RemoteHubAgent.exe` (service), the helper and `LogonUI.exe` in the guest (`MiniDumpWriteDump` from an elevated PS Direct session) and search them for the lab password (UTF-16 and UTF-8) | zero hits in all three; dumps deleted before evidence; the count and the dump sizes are the evidence (`sec-cp-05.json`). A hit is a release blocker (`CryptProtectMemory` / `SecureZeroMemory` path broken) | PS E | 1 | A3/A6 |
| SEC-CP-06 | Log folder swapped: as an administrator, replace `%ProgramData%\RemoteHub\Agent\logs` by a junction to `C:\Windows\System32\config` before the sign-in screen loads the provider | the provider refuses the folder (its trace in the event log / no `provider-*.log` written through the junction, no new file in the target); the sign-in still works; the service also refuses to log through it and says so in the event log | PS E | 2 | A3 (as admin) |

SEC-CP-04 (revoked signer) is not a lab case: the lab has no CA with a CRL, and the design accepts no revocation
check for an offline device. C-AUTO-21 (another signer refused) and DEP-GATE (the release certificate) cover it.

### 4.8 SEC-VDD – the Virtual Display Driver (`lab/tests/SEC-VDD.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-VDD-01 | Rogue display-driver pipe: `normaluser` creates a server pipe named `MTTVirtualDisplayPipe` before the agent turns the virtual display on (D-VD-01), and calls `ImpersonateNamedPipeClient` when the agent connects | **target:** the agent verifies the server (the driver host's process) or connects with `TokenImpersonationLevel.Anonymous`, so the rogue server gets no usable token; the reload fails cleanly, is logged, and the virtual display stays off. **Today:** the agent connects to whatever serves the name; the rogue server receives `RELOAD_DRIVER` and a SYSTEM token at Identification level (Windows denies Impersonation level to a user without `SeImpersonatePrivilege`) | S PS E | 1 | A3, **KnownGap** |
| SEC-VDD-02 | Junction planted at the settings folder: before the install (and separately before a reload), `normaluser` creates `C:\VirtualDisplayDriver` as a junction to `C:\Windows\System32\drivers`; a second run plants a writable folder with a `vdd_settings.xml` of their own | **target:** the agent refuses a reparse point or a folder it didn't create with its own ACL (SYSTEM + Administrators, like `%ProgramData%\RemoteHub\Agent`), logs it, and leaves the virtual display off; nothing is written through the junction. **Today:** `Directory.CreateDirectory` follows the junction and the settings file is written as SYSTEM into the target | S PS E | 1 | A3, **KnownGap** |

### 4.9 SEC-DEC – the compromised device attacks the administrator's PC (`lab/tests/SEC-DEC.Tests.ps1`, 2VM)

RemoteHub runs in the client VM; the harness runs a **rogue agent** on the host (`RemoteHub.LabHarness.exe rogue
--kind ...`, §5.1): a WebSocket server with a certificate RemoteHub was made to pin, speaking `/v1/health` and
`/v1/desktop` well enough to get RemoteHub into a desktop tab, then sending the corpus.

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-DEC-01 | Malformed H.264: SPS declaring 16384 × 16384, zero and negative sizes, truncated NAL units, a frame whose length header says 16 MB with 10 bytes following, SEI floods, random bytes, a stream that changes resolution every frame, 200 key-frame requests answered with garbage | RemoteHub.exe survives every case (no Application Error 1000, the process ID unchanged), the tab shows "the picture can't be decoded" or reconnects, working set grows < 500 MB and returns after the tab closes; the next good session decodes | U H E | 1 | A4 |
| SEC-DEC-02 | Malformed clipboard images: PNG with IHDR 65535 × 65535 and a 1 KB body (decompression bomb), bad CRCs, truncated IDAT, zero-size, 300 MB declared; BMP with negative height and a bogus pixel offset – from the rogue agent and, through the real agent, placed on `normaluser`'s clipboard (`RhPerson.ps1 -Do clipboard`) | RemoteHub shows "image can't be shown", never crashes or hangs (UI responsive within 5 s), memory bounded; D-CLIP-22 (device side) stays green | U S H E | 1 | A4 |
| SEC-DEC-03 | Hostile file names from the device: `..\..\evil.txt`, `C:\evil.txt`, `\\?\C:\evil.txt`, `\\host\share\x`, `CON`, `aux.txt`, `a:ads`, `name.`, `name ` (trailing space), 300 characters, U+202E right-to-left override, a path whose folder part is an existing junction in `%TEMP%\RemoteHub\Clipboard` made by the test, 10,000 nested folders | everything written lands under `%TEMP%\RemoteHub\Clipboard\<timestamp>\` with a sanitised name ("Unusable file name" for the rest); nothing outside it (file-system watcher on the profile), no write through the junction, the tab reports what was dropped | H U PS | 1 | A4 |
| SEC-DEC-04 | Floods: text one byte per frame for 60 s, 1,001 offered items, 101 MB in one offer, data frames out of order, a 16 MiB + 1 frame, frames after Close | each refused as in D-CLIP-08/11/20/21 and D-ROB-08 **on the console side**; RemoteHub's clipboard isn't replaced by partial content; the tab ends on the protocol error and the rest of RemoteHub keeps working | H U | 2 | A4 |
| SEC-DEC-06 | The decoder sandbox on the installed build: `RemoteHub.exe --decoder-check` run by `normaluser` on Windows 11 and Server (built – `SEC-DEC.Tests.ps1`, no rogue agent needed) | `RemoteHubDecoder.exe` runs in an AppContainer from Program Files, decodes an H.264 test stream and PNG/BMP/JPEG, and its probes find network, loopback, the profile, RemoteHub's database, writing, HKCU, the clipboard, new processes, RemoteHub's process and other windows blocked | PS U | 1 | A4 |
| SEC-DEC-05 | Hostile Hello and session list: label of 1 MB and with control characters / RTL override, `deviceId` that isn't a GUID, version `999.0.0`, unknown features, 10,000 sessions, user names with `%` and `\n`, a `managed` list of 100,000 entries | RemoteHub shows a truncated, sanitised label; the tree, the connect page and the session picker stay responsive; nothing is interpreted as a format string or a path; the update bar isn't triggered by the device's version | H U | 2 | A4 |

### 4.10 SEC-RDP / SEC-SH – rogue targets and shares (`lab/tests/SEC-RDP.Tests.ps1`, `SEC-SHARE.Tests.ps1`, 2VM)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-RDP-01 | RDP server replaced: the device's RDP certificate is renewed (new self-signed key) after RemoteHub connected once; connect again with the default (Warn) and with `ServerAuthentication = Require` | Warn: mstscax's certificate warning appears **before** credentials are delegated – no 4624/4625 for the operator on the device until the warning is accepted; Require: the connection is refused without a prompt; through a jump host RemoteHub's own pin decides (UI-JUMP-02) | U PS E | 1 | A5 |
| SEC-RDP-02 | Rogue `.rdp` file: `full address` to the attacker's VM, `password 51:b:` blob, drives, clipboard, smart cards and printers on, `authentication level:i:0`, `enablecredsspsupport:i:0`, `gatewayhostname`, `alternate shell`, `remoteapplicationmode:i:1`, opened by double-click (C-MSI-03) | the risk list names every one of them (RdpFileParser.Risks – extend for gateway and alternate shell); *Cancel* makes **no** TCP connection to the host; the blob is decrypted only for the current user and never shown; the imported connection keeps redirections off until the user turns them on | U PS N | 1 | A5 |
| SEC-RDP-03 | Trusting a CA by accident: the remoting test fetches a certificate with `CA = true` (the device presents a self-made CA instead of an end-entity certificate) | RemoteHub refuses to offer *Trust on this PC* for a CA certificate ("would trust everything it issues") and shows the pin-only route (`--cert`) instead; for an end-entity certificate the Windows confirmation is still required (UI-TL-08) | U H | 1 | A5, **KnownGap** until verified |
| SEC-SH-01 | Clear-text shares: a share saved with FTP security *none* and WebDAV *HTTP*; `pktmon` on the device during a copy | the editor's warning is shown when saving (network-shares.md); the share's tab shows "not encrypted"; the capture **contains** the password (proving the warning is true) and the test records it only as a count; the same copy over TLS / HTTPS shows zero hits | U PS N | 1 | A5 |
| SEC-SH-02 | SMB signing and encryption with RemoteHub's own client (S-SMB-02): the server set to `RequireSecuritySignature = $true`, then `EncryptData = $true`, then both off | with signing required the connection works (the client signs) or RemoteHub reports "the server requires signing" – never a silent downgrade; with encryption required the same; with both off, SEC-TUN-07's capture shows the marker (documented: SMBLibrary doesn't force signing – product decision whether to require it) | PS H N | 1 | A5, **KnownGap** if the client can't sign |
| SEC-SH-03 | Changed server identity on every share kind: SFTP host key (S-SFTP-03), FTPS certificate, WebDAV certificate (and a FluentFTP data-connection certificate that differs from the control connection's, once the lab FTP server can do that) | each asks before sending anything: the rogue server records no authentication attempt; *No* ends it, *Yes* re-pins and is audited | H PS E | 1 | A5 |

### 4.11 SEC-UPD – the update channel (`lab/tests/SEC-UPD.Tests.ps1`, mock server)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-UPD-01 | Unsigned MSI with the right hash: the mock announces a higher version whose MSI is unsigned (or signed by the lab certificate) with correct `sha256` and `size` | **target:** RemoteHub refuses before the UAC prompt – "not signed by RemoteHub's publisher" – nothing started, the file deleted. **Today:** the download is accepted and `msiexec` runs; Windows' UAC prompt shows "Unknown publisher" and the lab declines it (Alt+N) so nothing installs | U M C PS | 1 | A5, **KnownGap** |
| SEC-UPD-02 | Swap after verification: a task in the administrator's own session watches `%TEMP%\RemoteHub\Updates` and replaces the verified MSI (and in a second run `install-update.cmd`) between *Install* and `msiexec` | **target:** the swap is detected (the file is re-verified at the point of use by an elevated step, or staged where the user can't write) and nothing of the swapped file runs. **Today:** the swapped MSI is what `msiexec` elevates; the lab's swapped MSI is a harmless package that writes a marker file – the marker's presence is the failure | S U M C PS | 1 | A6, **KnownGap** |
| SEC-UPD-03 | Rogue update server: the mock's certificate not trusted in the VM; `latest` answers 10 MB of JSON, 5,000 nested objects, a version string of 1 MB, `download.url` of `http://`, `file://`, a UNC path, and `https://remotehub.dev.attacker.test` | each refused with a clear message (C-UPD-09 texts), nothing downloaded or written under `Updates`, RemoteHub responsive; the host allow-list treats `remotehub.dev.attacker.test` as untrusted (suffix match on a label boundary) | U M | 1 | A5 |
| SEC-UPD-04 | The user's environment in the elevated step: `COR_ENABLE_PROFILING`, `COR_PROFILER`, `COR_PROFILER_PATH` in the console account's `HKCU\Environment` (a .NET profiler for every program of theirs) before an update | no .NET Runtime event names the step's process as loading it; a Windows PowerShell started with the same values does (the check can see it) | U M C PS E | 1 | A6 |
| SEC-UPD-05 | RemoteHubUpdate.exe as a signed tool for something else: run with values it doesn't take (an extra `--publisher`, a stream, a UNC path, `..`, a short checksum), then with well-formed values for a package whose signature doesn't match; then a copy in a user-writable folder with a planted `InstallUpdate.ps1` | the first start nothing and write no outcome (exit code 0); the second starts the step, which refuses the package (`refused signature`); the copy starts nothing and the planted script doesn't run | PS M | 1 | A6 |

### 4.12 SEC-BAK / SEC-DPAPI / SEC-CON – the administrator's user context (`lab/tests/SEC-DATA.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-BAK-01 | Tampered backups (corpus built from one good `.rhbackup` and one `.rdpmbackup`): one byte flipped in the ciphertext, the salt, the nonce, the tag; iteration count set to 9,999, 10,000, 10,000,001 and a valid other value; version field changed; the JSON truncated; the notes section of backup A put into backup B (same password); 100 MB of padding | every variant is refused ("damaged or not from RemoteHub" / wrong password / not a backup) with nothing imported and the tree unchanged – **except the section swap, which imports B with A's notes today** (no associated data binds the sections – target: AAD = version + section name + the settings' nonce) | U PS | 1 | A6, **KnownGap** (section swap) |
| SEC-BAK-02 | Weak key derivation: inspect the iteration count RemoteHub writes; import a file at the 10,000 floor | exported `.rhbackup` and the shared settings file carry exactly 600,000; a file at 10,000 still opens (compatibility with `.rdpmbackup`) and RemoteHub re-saves the shared file with its own count only when the user changes the password (documented); below 10,000 refused | U PS | 2 | A6 |
| SEC-DPAPI-01 | Reading the vault as someone else: copy `remotehub.db`, `db.key`, `keys.dat` and `lock.json` to `normaluser`'s profile on the same VM (C-DB-02) and to the same account name and password on a second VM; with the startup lock on, as the same user without the password | none of them opens the database (DPAPI is bound to the user's SID and the machine); the startup lock asks and refuses a wrong password; no plaintext of the test credential in any copied file (byte search) | PS U | 1 | A6 |
| SEC-DPAPI-02 | What stays in memory: minidump of RemoteHub.exe 10 s after unlocking with the startup password, and while auto-locked (C-LOCK-02) | the startup password is not in either dump (UTF-16/UTF-8); documented and asserted as *present* while locked: the database key (UI lock by design – the test records it so a future "evict keys on lock" option can flip the assertion); dumps deleted before evidence | PS U | 2 | A6 |
| SEC-DPAPI-03 | Secrets on disk: after a session with every tab type, a backup export, an `.rdp` export without password, a recording, a crash report and an auto-lock, search `%LOCALAPPDATA%\RemoteHub`, `%APPDATA%\RemoteHub`, `%TEMP%`, the recordings folder, DiagLog, the pagefile-free locations and the clipboard history for the test credential's password | zero hits outside the encrypted database and the encrypted backup; the `.rdp` export has no `password 51:b` line unless the user ticked it (R-RDP-08) | U PS | 1 | A6 |
| SEC-DPAPI-04 | Recordings readable by others: `normaluser` opens `Admin`'s `%USERPROFILE%\Videos\RemoteHub`; a policy `RecordingFolder` pointing at a world-writable folder | the profile folder is access denied for other users (inherited profile ACL); for the policy folder RemoteHub shows "other users of this PC can read recordings here" once (product decision whether to warn – recorded as evidence) | PS U | 3 | A6 |
| SEC-CON-01 | Another user drives the console: while `Admin` runs RemoteHub, `normaluser` (a second session on the same VM, `Invoke-LabInSession`) finds the `RemoteHub-*` single-instance pipe and writes a forwarded command line with a planted `.rdp` path; the same from `Admin`'s own session at Low integrity | the other user's connect fails (`CurrentUserOnly`: access denied, nothing arrives); RemoteHub shows no import and no connection; the Low-integrity writer is refused too. From `Admin`'s own Medium session the forwarded file is handled like a double-click – the risk list of SEC-RDP-02 comes first | S U PS | 2 | A6 |
| SEC-CON-02 | Reading copied passwords: copy a connection's password with *Copy password* (the Windows credential prompt route); then read Win+V history, `Get-Clipboard` after the timeout, and cloud clipboard sync on | the history has no entry for the password; after the timeout the clipboard is empty – **unless** the user copied something else meanwhile, which stays; RemoteHub closed while it holds the secret → cleared at exit; the password isn't in DiagLog | U S PS | 2 | A6 |

### 4.13 SEC-POL – policy sources (`lab/tests/SEC-POL.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-POL-01 | Self-granted policy: as `normaluser` and as `Admin`'s **non-elevated** token, create `HKCU\Software\Policies\RemoteHub` and set `DisableRecording = 0`, `Connections\...` values, `DisableAgentAutoSignIn = 0`; read the key's ACL | the write fails with access denied for both (Windows protects `HKCU\Software\Policies` by default – the empirical answer to inventory §8); the ACL grants the user read only. **If** a write succeeds, the case fails and the console must read security-relevant values from HKLM only (C-POL-07 changes) | S PS | 1 | A3/A6 |
| SEC-POL-02 | Agent policy from below: `normaluser` writes `HKLM\SOFTWARE\Policies\RemoteHub\Agent\Rights\PowerShell`; an operator with the PowerShell right (admin) deletes the whole policy key through the agent | the user's write is denied; the admin's delete succeeds (Windows admin model – documented) **and** the agent logs "policy key removed" / health shows no `managed` entries at the next connection, so the change is visible in the audit; S-POL-01 (unreadable = nothing) still holds | S H PS E | 2 | A3/A2 |

### 4.14 SEC-LOG – leaving no trace, leaking no secret (`lab/tests/SEC-LOG.Tests.ps1`)

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-LOG-01 | DiagLog on the admin PC after a jump session, a forwarder refusal (J-07), a share, an RDP tab and a failed agent test: search `%APPDATA%\RemoteHub\logs` for the device's host names, addresses, account names and the lab password | zero hits. **Today:** `JumpForwarder` writes the connection's name (`JumpForwarder.cs:108`), which is usually the host name – target: log the connection's ID | U PS | 1 | A6, **KnownGap** |
| SEC-LOG-02 | Erasing the audit from inside: an operator with the PowerShell right runs, through the agent, `wevtutil cl Application`, deletes the agent's log file, sets `AuditToEventLog = 0` by policy, then opens a desktop session | the audit lines for the session that did it (1100/1101 with account and source) were written **before** the first command ran and are in the Security log's own trail (4624 type 3 for the account); the log file is recreated at the next write and starts with "log file recreated"; the policy change is logged as a policy change (SEC-POL-02); the lab reports the operator's actions from its own Security-log extract (`Save-LabEvidence`) – the point is that the trail outside the agent's control stays | H PS E | 1 | A2 |

### 4.15 SEC-DSK – the operator who lies to the agent (`lab/tests/SEC-DSK.Tests.ps1`)

Every case uses a raw desktop client (`probe --kind desktop --raw`, §5.1) that builds `Start` and every later
message by hand, so nothing RemoteHub's UI would hide or refuse stands in the way. The marker app of D-ASK-06 runs in
`normaluser`'s session and counts every key, click and clipboard change that reaches it.

| ID | Attack | Pass when | How | P | Pos |
|---|---|---|---|---|---|
| SEC-DSK-01 | Control from a view-only right: an account only in `Rights\DesktopViewOnly` (D-VIEW-02's `viewer`) sends `Start` with `ViewOnly = false`, then Mouse, Key, SecureAttention, PrivacyScreen, Clipboard text, a file offer, a fit size and an automatic sign-in request | Status "Connected (view only)"; the marker app counts 0 inputs and the device clipboard is unchanged; no Ctrl+Alt+Del screen, no privacy screen, no sign-in; event 1100 says view-only; the session stays up (ignored, not a crash) | H S E | 1 | A2 |
| SEC-DSK-02 | Consent skipped or forged: `Start` with `Consent = None` without `ConsentBypass` (with and without `AllowedConsentModes` containing None); with Ask, a `Pin` message and every helper-to-service message type the client should never send (an "allowed" answer), input frames and a clipboard offer while *WaitingForUser*; `Start` naming a mode the policy doesn't allow | None refused (2100) as in D-NONE-01/02, or replaced by an allowed mode that asks; a client message never answers the question – the session stays *WaitingForUser* until the person decides or the timeout denies; nothing reaches the marker app before Allow; unknown message types end the session with a protocol error | H S E | 1 | A2 |
| SEC-DSK-03 | Guessing the code: Pin mode, 3 wrong codes per connection, 30 connections in a row as fast as the agent allows | each connection ends after the third wrong code (2100 "entered wrong too often"); every connection shows the person a **new** dialog with a **new** code (OCR: 30 distinct codes) – a guess can't be made without the person seeing it; 30 × 2100 with account and source. Evidence for the product decision "back off an account after N denied requests" (90 guesses against 10^6 codes, each visible) | H S E | 2 | A2 |
| SEC-DSK-04 | The secure desktop without the right: `Start` to a locked session and to the sign-in screen without `SecureDesktop`; connected to an active session, the person presses Win+L and a UAC prompt appears | the locked / sign-in `Start` is refused before any frame; in the active session no frame of the lock screen or the UAC prompt reaches the client (the picture pauses at the last user frame, D-LOCK-02) and Mouse / Key / SecureAttention sent meanwhile don't reach the secure desktop; a key held down is released (D-VIEW-03) | H S C | 1 | A2 |
| SEC-DSK-05 | Choosing someone else's session: on `Server-2025-en-US` with `normaluser` signed in over RDP beside the console session, `Start` with that session's ID, an ID that doesn't exist, 0, -1, `int.MaxValue`, and a disconnected session's ID | the RDP user is the one asked (never the console user, never nobody); the others are refused "can't be shown" before a helper starts; no helper in session 0 (D-CON-02) | H S PS | 1 | A2 |
| SEC-DSK-06 | Resume tokens: 1,000 random tokens; a real token presented by another account (D-RES-03); by the same account from a second source address; after the session ended on purpose (D-RES-05); twice in parallel | random and foreign tokens are ignored – normal consent; the same account from another address resumes (by design, the token is the account's) and event 1103 names the **new** address; an ended or used token asks again; of two parallel uses at most one resumes | H N E | 1 | A2 |
| SEC-DSK-07 | Clipboard above the policy: the agent policy `DesktopClipboard` 0, 1 and 2; the raw client sends text, an image and a file offer regardless, and requests a device file | at each level only what it allows reaches the device (marker app, `normaluser`'s temp for files); refused items are logged without content; a requested device file above the level is never read (the file's last-access time and a handle watcher on it) | PS H S | 1 | A2 |
| SEC-DSK-08 | Watching unnoticed: through the session, every message the protocol knows with every flag combination (fit, quality, monitor, hide flags), and a `PrivacyScreen` request from an operator without `Rights\PrivacyScreen` | the banner (or the cover of a session automatic sign-in created) stays on the device's screen for the whole connection – checked by a SYSTEM-side window enumeration and by OCR every 5 s; the privacy request is refused with 2103 | H S C E | 1 | A2 |

## 5. What the lab needs

### 5.1 Harness (`tests/RemoteHub.LabHarness`)

| Addition | Used by |
|---|---|
| `probe --kind abuse --case <name>` – the raw HTTP/WebSocket corpus of SEC-NET-07, each case a function that writes bytes on a raw TLS stream and reports what came back and whether the connection closed; `--case list` prints the corpus | SEC-NET-07 |
| `probe --kind ws` gains `--header name=value` (repeatable) and `--source <ipv6>` (binds the client socket to one of the host's addresses) | SEC-NET-02/03 |
| `probe --kind hold` gains `--raw 1` (TCP only, no TLS) and `--tls-only 1` (handshake, no request) | SEC-NET-08 |
| `probe --kind psrp --case <name>` – PSRP lines built by hand: oversized, entity expansion, deep nesting, burst | SEC-PS-05 |
| `rogue --kind listener --cert <subject>` – a TLS listener that records whether an HTTP request arrived | SEC-NET-06, SEC-ID-02 |
| `rogue --kind agent --corpus <h264|png|names|flood|hello> --case <n>` – the rogue agent of §4.9: health + desktop protocol, then the corpus | SEC-DEC-* |
| `rogue --kind share --kind <sftp|ftps|webdav> --changed-identity 1` – share servers that present a different key or certificate and record authentication attempts (extends `Enable-LabShareServers`) | SEC-SH-03 |
| `jump --kind matrix --file <json>` – runs every entry of the address matrix and reports per entry | SEC-TUN-01/02/03 |
| `update --unsigned 1` / `--swap-after-verify <msi>` – drives the mock and the swap task for SEC-UPD-01/02 | SEC-UPD |
| `tool fetch` reports `isCa` of the fetched certificate | SEC-RDP-03 |
| `probe --kind desktop --raw --script <json>` – a desktop client that sends a scripted list of messages (any `Start` fields, any message type and flag, timed) and records every Status; the existing desktop harness without its client-side checks | SEC-DSK-* |
| `auto-sign-in --kill-logonui-at pending|claim` and `--hold-ticket-seconds n` (test build of the provider, C-PROV-03 infrastructure) | SEC-CP-02 |

### 5.2 Module (`lab/RemoteHub.TestLab`)

| Function | Does |
|---|---|
| `Invoke-LabAttack -Name <vm> -As User|Admin|System -Op <op> [-Params]` | runs `lab/guest/RhAttack.ps1` in the right context: `-As User` through `Invoke-LabInSession` (normaluser's token), `-As Admin` through PS Direct, `-As System` through a one-off scheduled task with the SYSTEM principal. Ops: `pipe-open`, `pipe-squat`, `window-messages`, `open-process`, `plant-junction`, `plant-file`, `plant-reglink`, `acl-probe`, `reg-write`, `msiexec`, `watch-and-swap`, `net-add-addresses`, `pipe-write`, `clipboard-read` |
| `Get-LabProcessDump -Name <vm> -Process <name> -Search <string[]>` | minidump in the guest (`MiniDumpWriteDump` from an elevated session), searches UTF-8 and UTF-16, **deletes the dump**, returns the hit count per string |
| `Start-LabCapture` / `Stop-LabCapture -Search <string[]>` | `pktmon` in the guest, filter by port; search the `.etl` converted to text in the guest, delete it, return hit counts |
| `Get-LabPipeSecurity -Name <vm> -Pipe <name>` | SDDL, owner, mode and instances of a named pipe (via `GetNamedPipeHandleState` and `GetSecurityInfo` in the guest) |
| `New-LabTamperedBackup -Source <file> -Variant <name>` | the SEC-BAK-01 corpus from one good file (runs on the host) |
| `New-LabMediaCorpus -Kind h264|png|bmp -Case <name>` | the SEC-DEC-01/02 inputs, generated – nothing downloaded |
| `Set-LabGuestClock -Name <vm> -Offset <timespan>` / `-Restore` | SEC-NET-05 (and SMOKE-08 afterwards) |
| `Set-LabAgentPolicy -Rights @{ Tunnel = ... }` already exists; add `-Values @{ TunnelTargets = @(...); TunnelPorts = @(...) }` as REG_MULTI_SZ | SEC-TUN |
| `Get-LabFailedLogon`, `Get-LabAgentEvent`, `Get-LabAgentLog` – existing; add `Get-LabPowerShellEvent -Id 4104` and `Get-LabDefenderEvent -Id 1116` | SEC-PS-04 |

`lab/guest/RhAttack.ps1` is the attacker's toolbox in the guest: ASCII-only, Windows PowerShell 5.1, every op prints
a JSON result (what was attempted, Win32 error, what changed). It never carries a payload from outside the lab.

### 5.3 VMs and host

- **No new VM.** Device cases run on each client VM; the 2VM cases use the client VM as the admin PC and
  `Server-2025-en-US` (or the jump host) as the device, like the functional sheet. The rogue agent, rogue listener
  and rogue share servers run on the host (the harness), reachable from the VMs like the mock update server.
- **The jump host VM** for SEC-TUN, as in J-*.
- **Host addresses:** SEC-NET-01 and SEC-NET-03 need a second address on the host adapter towards the VM (added and
  removed by the test; `labop` is not an administrator, so the lab task's install step adds a `RHLAB` address pool
  once – `Install-LabTask.ps1`) or use the jump host VM as the "outside" source.
- **Provider test build** (`tests/RemoteHub.CredentialProvider.TestServer`, used by C-PROV-03) for SEC-CP-02.
- **Lab signing certificate** (`lab/New-LabSigningCertificate.ps1`) to sign the "wrong publisher" MSI of SEC-UPD-01.

### 5.4 Runtime budget

Per client VM the SEC files add roughly: SEC-NET 6 min (the 6-minute forget window of SEC-NET-03 runs alongside
SEC-NET-04…08), SEC-PS 4, SEC-ENR 5 (2VM), SEC-MSI 4, SEC-TUN 8 (jump host revert included), SEC-UI 3, SEC-CP 6,
SEC-VDD 3, SEC-DEC 10 (2VM), SEC-RDP/SH 8 (2VM), SEC-UPD 5, SEC-DATA 8, SEC-POL 2, SEC-LOG 4, SEC-DSK 8 – about 85 minutes on
top of the functional sheet. Runtime doesn't gate the release (test-lab-cases §1); the 2VM families run only on the
VMs marked as admin PCs in the run (`-Role console`), the device families on every VM.

## 6. Writing a `SEC-*` file

Same shape as `S-THR.Tests.ps1`, with two additions. The header names the attacker position and the threats:

```powershell
# SEC-TUN - the jump host as a pivot (attacker A2: an operator with the Tunnel right). Threats T5.1-T5.7 of the
# threat catalogue; docs/security-test-lab-design.md section 4.5. Every Open that must be refused is also checked on
# the jump host: a listener on the loopback / link-local port must see no connection.
# LabRole: device
# LabNeeds: jumphost
```

A known gap is a normal `It` with the `KnownGap` tag and a `-Because` that names the issue; the assertion is the
**target** behaviour, so the case starts failing and turns green with the fix:

```powershell
It 'SEC-VDD-02 refuses a planted junction at C:\VirtualDisplayDriver' -Tag 'SEC-VDD-02', 'Security', 'KnownGap' {
    # Known gap #<issue>: Directory.CreateDirectory follows the junction today (VirtualDisplay.cs:543)
    $r.writtenThroughJunction | Should -BeFalse -Because 'known gap #<issue>: the settings file must never follow a reparse point'
}
```

`Invoke-LabTests.ps1` learns the tag: a failed `KnownGap` case is reported in the "Known gaps" table instead of
failing the run; a **passed** one is reported as "gap closed – remove the tag" and fails the run until the tag is
removed (so the register and the code never disagree).

## 7. Known-gap register (state on 2026-10-21)

No open gaps.

**Closed on 2026-10-21** (the cases assert the fixed behaviour, without the `KnownGap` tag; the lab cases are in
[test-lab-cases.md](test-lab-cases.md) §13):

| Case | Gap | Fix |
|---|---|---|
| SEC-UPD-01 | Update MSI installed without an Authenticode check | `Authenticode.Refusal`: signed, chain trusted (`WinVerifyTrust`, no revocation check), signer subject = the running build's (a renewed certificate keeps its subject). An unsigned running build (test release, local build) can't name a publisher and keeps the checksum only |
| SEC-UPD-02 | MSI and `install-update.cmd` swappable in `%TEMP%` before elevation | The elevated step (Windows PowerShell given as its command line) copies the MSI into `%ProgramFiles%\RemoteHub Updates` (SYSTEM / Administrators only – under Program Files rather than `%ProgramData%`, where users may create folders), checks hash and signature of that copy and installs it; the user's helper only restarts RemoteHub, which shows the step's outcome |
| SEC-VDD-01 | `MTTVirtualDisplayPipe` server not verified; client allows impersonation | `TokenImpersonationLevel.Anonymous`; the command only goes to `%SystemRoot%\System32\WUDFHost.exe` |
| SEC-VDD-02 | Settings folder follows a planted junction; default ACL at the drive root | The folder stays where the driver reads it, made the agent's own: created with a protected ACL; a link, a file or a folder with other permissions in its place is removed (a link itself) and made again; the settings file deleted before it is written (no hard link written through) |
| SEC-BAK-01 | A section could be taken out of a backup (and the version relabelled) unnoticed | Version 4: AES-GCM associated data = format, version, section, salt, list of sections; versions 1–3 and `.rdpmbackup` still import |
| DEP-GATE | FluentFTP 55.0.0 accepts any certificate on FTPS data connections | 55.1.0; a data connection whose certificate isn't the control connection's (or trusted) is refused without asking (`FtpShare.Decide`) |
| SEC-LOG-01 | Connection names (host names) in `DiagLog` | The forwarder logs an 8-character hash of its route and only the kind of a refusal |
| SEC-RDP-03 | Verified: any certificate could be added to the trusted roots | Only an end-entity certificate (`RemotingSetup.IsEndEntity`); a CA certificate is refused with an explanation |
| SEC-SH-02 | Verified: SMBLibrary signed only when the server required it | Signing switched on after every sign-in (`ManagedSmbShare.RequireSigning`); guest sessions refused |
| SEC-ENR-03 | A copied `.rhagent` enrolled a second device with the same identity | Enrollment format 2 (agent-spec.md §5.5): the file carries RemoteHub's enrollment secret, not a PFX; each device makes its own non-exportable key and device ID at enrollment; RemoteHub pins the certificate by a claim proven with the secret (HMAC over the certificate, the device and a nonce), never replaces a pin by itself, and can replace and revoke the secret. Agreed with the owner without a time limit on the file, so MDM and software-distribution packages stay valid |
| SEC-CI-01 / 02 | eSigner credentials on the `java` command line; the tag inlined into release scripts | A Java argument file, deleted after use (`build/JavaArgFile.ps1`, `BuildScriptTests`); `env: TAG` |

Accepted by design, documented in the cases and not in the register: expired certificate still served (SEC-NET-05);
no revocation of pins or signers (SEC-ID-02, SEC-CP-04); per-address throttling (SEC-NET-03); rights checked per
connection (SEC-PS-03); full token for network logon (SEC-NET-04); UI-only auto-lock (SEC-DPAPI-02); the shared
settings password as the trust boundary (SEC-ID-01); plain FTP / HTTP WebDAV behind a warning (SEC-SH-01).

## 8. Outside the lab: unit tests and CI gates

These belong with the code, not in the VM lab, and the release checklist (§9) requires them green:

| Gate | Where | What |
|---|---|---|
| **DEP-GATE** | `build/Test-DependencyVersions.ps1` + `build/minimum-versions.json`, `ci.yml` | Built (2026-10-22): every `PackageReference` (`.csproj`, `.vcxproj`, `.wixproj`) checked against a table of versions with a known security fix (FluentFTP ≥ 55.1.0); below it, or without a fixed version, the build fails. ClamAV is pinned in its own step (SEC-CI-04); the .NET runtime isn't part of the build (framework-dependent) |
| **Unit: TunnelRules matrix** | `tests/RemoteHub.Tests` | The SEC-TUN-01/02 address matrix against `TunnelRules.IsSpecial` / `Allowed` – fast, no VM; the lab proves the connect side |
| **Unit: SafeRelativePath matrix** | `tests/RemoteHub.Tests` | The SEC-DEC-03 names against `DesktopWire.SafeRelativePath` and the console's receive path |
| **Unit: backup corpus** | `tests/RemoteHub.Tests` | SEC-BAK-01's variants against `BackupFile.Import` (the lab adds the UI and the file system) |
| **Unit: protocol parsers** | `tests/RemoteHub.Tests` | The SEC-DEC-04/05 and SEC-PS-05 inputs against `DesktopProtocol`, `AgentProtocol`, `TunnelProtocol` parsing |
| **Fuzz: `Wire.cpp`** | `build/build-credential-provider.ps1 -FuzzSeconds 60` | Existing; extend the corpus with the SEC-CP-02 nonce / ticket cases |
| **SEC-CI-01** | `build/Sign-Files.ps1` | Built (2026-10-21): the eSigner credentials reach CodeSignTool through a Java argument file, deleted after use; `BuildScriptTests` proves the file's arguments arrive unchanged |
| **SEC-CI-02** | `ci.yml` job `workflow-lint`: actionlint + `build/Test-Workflows.ps1` | Built (2026-10-22): actionlint (pinned, hash-checked) fails on untrusted input in scripts; the project check fails on any `${{ github.* }}` or `${{ inputs.* }}` in a `run:` script and on an action not pinned to a commit SHA with its version |
| **SEC-CI-03** | `cla.yml`, `Test-Workflows.ps1` | Built (2026-10-22): no wildcard in the CLA allow-list; a `pull_request_target` workflow with `actions/checkout` fails the check |
| **SEC-CI-04** | `ci.yml` | Built (2026-10-22): ClamAV 1.5.4 from Cisco-Talos' release, SHA-256 checked, version checked after the install |
| **SEC-CI-05** | `release.yml` | Built (2026-10-22): CycloneDX SBOM of the NuGet packages and GitHub build provenance for every release file |
| **SEC-CI-06** | docs | Done (2026-10-22): `credential-provider-design.md` says releases are signed since 0.20.2 |

## 9. Report and docs in step

- **Report.** `New-LabReport.ps1` adds a *Security* section to `report.md`: one row per threat ID (§3) with the
  covering cases and their result per VM, then the known-gap table (§7) with each case's state ("open", "closed –
  tag to remove"). A release PR must carry it; the pass rule of §1 applies.
- **Sheets.** The `SEC-*` cases go into [test-lab-cases.md](test-lab-cases.md) as a new §13 "Security – attack
  cases (SEC)" with the columns of §4 here, and into [test-lab-test-document.md](test-lab-test-document.md) with
  preconditions, steps and expected bullets, in the same PR as each test file.
- **Threat model.** `threat-model-technologies.md` §8 and the catalogue draft take the corrections of §3.1 and link
  each threat to its case ID; the known-gap register (§7) is the single list of open security gaps – issues are
  opened from it.
- **Design docs.** Fixes from the register update their design doc (`jump-host-design.md` for tunnel rules,
  `credential-provider-design.md` §7 for the pipe, `agent-spec.md` for enrollment and the update channel).

## 10. Order of work

1. **Infrastructure** (§5): `RhAttack.ps1`, `Invoke-LabAttack`, `Get-LabProcessDump`, `Start-LabCapture`, the
   `KnownGap` tag in `Invoke-LabTests.ps1` and the report section. Everything else builds on these.
2. **Device families with no new harness code:** SEC-MSI, SEC-POL, SEC-UI, SEC-CP-01/03/05/06, SEC-VDD, SEC-PS-01…04,
   SEC-LOG-02 – they find the two VDD gaps and settle the HKCU question.
   Then SEC-DSK with the raw desktop client (`probe --kind desktop --raw`) – the agent-side consent and view-only
   checks are the core of TB7 and need no new VM.
3. **Network and tunnel:** the abuse corpus (SEC-NET-07), the address matrix (SEC-TUN-01…04), then the rest of
   SEC-NET / SEC-TUN.
4. **Rogue servers:** `rogue --kind agent` and SEC-DEC, then SEC-NET-06, SEC-ID-02, SEC-SH-03.
5. **Console families (2VM):** SEC-UPD, SEC-DATA, SEC-RDP, SEC-SH, SEC-LOG-01, SEC-ENR.
6. **Register fixes**, highest severity first (§7), each with its case turning green and its tag removed.
