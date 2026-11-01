# RemoteHub Test Lab – End-to-end test sheet: Windows client and desktop

Status: v3 (2026-10-15), checked against RemoteHub / RemoteHub Agent **0.18.11** (jump hosts, automatic sign-in, privacy screen, SnipGuard; code review of every family). Scope: everything RemoteHub and the
agent do on a **Windows client** (Windows 10 / 11), as the managed device and as the admin's PC, run fully automated
in the Hyper-V lab ([test-lab-spec.md](test-lab-spec.md)). Server-only cases (multi-session RDP hosts, Server 2016/19
banner paths, domain controllers) come in a separate sheet.
Owner: Grischa Ernst

## 1. Release gate

**Before a release, every case in this sheet runs on every client VM in §2, and the PR carries the result.**

- A release PR (version bump or tag) must include the lab report (§11): one row per case, one column per VM, the
  agent and client MSI under test (file name + SHA-256), the run ID and the checkpoint date of each VM.
- **Pass rule:** no case is *Failed*. *Skipped* is allowed only with a reason the lab prints (e.g. "needs GPU-P VM",
  "agent doesn't change SoftwareSASGeneration before 0.15.8"). A case that is still *Planned* (§3) shows as
  *not automated* and counts against the release until it is automated or the owner signs it off in the PR.
- Runtime doesn't matter: one run = all VMs × all cases, one VM at a time (the lab turns the others off).
- A failed case is rerun once on its own (`-Tag <case>`). If it passes on the rerun, the report lists it as
  **flaky**, and the PR has to say why. Flaky twice in a row = failed.

### 1.1 Capability checklist

Each capability is tested twice:
- **at the protocol level:** the harness on the host checks exactly what the device does;
- **end to end through the RemoteHub window:** RemoteHub in a client VM is driven by UI Automation, the way an admin uses it.

A capability counts as release-ready only when both columns pass.

| Capability | Protocol level (device truth) | End to end in RemoteHub's window |
|---|---|---|
| Agent connection (install, test, connect, pin, sign-in) | A-INST-*, S-TLS-*, S-AUTH-*, D-CON-* | UI-AG-01…06, R-AGENT-01 |
| Agent remote desktop: see and control | D-CON-08, D-IN-*, D-VID-* | UI-AD-01…04 |
| Copy / paste via agent desktop (text, image, files, both ways) | D-CLIP-* | UI-CP-01…08 |
| Copy / paste via RDP (text, image, files, both ways) | – (Windows' rdpclip) | UI-CP-09…12, R-RDP-05 |
| Ctrl+Alt+Del: desktop, lock screen, sign-in screen, UAC (agent) | D-SAS-*, D-LOCK-*, D-SIGNIN-*, D-UAC-* | UI-SAS-01…05 |
| Ctrl+Alt+Del: RDP session, incl. its lock screen | – | UI-SAS-06…07, R-RDP-07 |
| Consent at the device (Ask / code / none) | D-ASK-*, D-PIN-*, D-NONE-* | UI-AD-05…07 |
| RDP connection (credentials, local account, redirection, resolution, full screen) | – | R-RDP-* |
| PowerShell, file transfer, registry, services, processes (agent and WinRM) | P-*, T-* | UI-TL-01…05 |
| WinRM setup on the target | W-* | W-TRUST-01, UI-TL-06 |
| Reconnect after a network drop (agent and RDP) | D-RES-*, P-SES-06 | UI-NET-01…02 |
| Jump hosts: everything through another device's agent | J-* | J-11, UI-JUMP-01 |
| Automatic sign-in on the sign-in screen (credential provider) | C-PROV-*, C-AUTO-* | UI-AG-07…08, C-POL-08 |
| Privacy screen, hidden apps, user privacy | D-PRIV-*, D-HIDE-* | – |
| Recording of each tab type | – | C-REC-* |
| Policies of both products, the Group Policy template | POL-01…09 | POL-11…13, C-POL-* |

## 2. Client VMs

All provided as in test-lab-spec §5.1 (PSAdmin, normaluser, Admin signed in, newest checkpoint).

| VM (Id) | Why it is in the gate | Status |
|---|---|---|
| `W11-26H2-en-US` | Current Windows 11; new lock screen (held on the Default desktop) | running |
| `W11-24H2-en-US` | Windows 11 still in support for enterprises | to provide |
| `W11-26H2-de-DE` | German UI + German keyboard: localized group names ("Administratoren"), localized msiexec log ("Fehler"), `<` switch key, date formats | to provide |
| `W10-22H2-en-US` | Most common client; yellow capture border for GPU recording; no `IsBorderRequired` | to provide |
| `W10-1809-en-US` (LTSC 2019) | Minimum supported version (10.0.17763): banner can't be excluded from capture (< 19041), no GPU pointer capture, oldest WTS behaviour | to provide |
| `W11-26H2-N-en-US` (N edition, no Media Feature Pack) | H.264 missing: agent desktop and recordings must explain it, not crash | to provide, optional |
| `W11-26H2-CLM-en-US` | WDAC policy forcing Constrained Language Mode | to provide, optional |

Two-VM cases (RemoteHub **in** a VM connecting **to** another VM, marked **2VM** below) use the client VM as the
admin's PC and `Server-2025-en-US` as the device unless the case says otherwise.

## 3. How to read the tables

| Column | Meaning |
|---|---|
| **ID** | Stable case ID. The Pester `It` name starts with it (`It 'A-INST-03 runs the service …'`), so the report can map results to this sheet. Never reuse an ID; retire it with ~~strike-through~~. |
| **Pass when** | What the lab checks. Everything is checked by a script – no "looks fine". |
| **How** | Automation building blocks (§10): **PS** PowerShell Direct in the guest · **H** lab harness on the host (RemoteHub's own client code: install, health, PowerShell over the agent, desktop client) · **C** Hyper-V console (screenshot, keyboard, Ctrl+Alt+Del) · **S** code in a user's session (scheduled task; UI Automation of the agent's dialogs) · **U** UI Automation of RemoteHub.exe in a VM session (FlaUI) · **N** network control (cut / restore the VM's adapter, second NIC) · **M** mock update/telemetry server · **E** event log / log file check |
| **P** | **1** = release blocker (security, install, data loss) · **2** = core function · **3** = edge case / comfort. All of them run before a release; P only orders the triage. |
| **St** | ✅ automated · ◐ test written, not yet run in the lab · ○ planned |
| **Needs** | Extra VM or host feature: 2VM, GPU-P, CLM, N, de-DE, 1809, SMB (share on the host), Domain |

Every scenario file starts with `Reset-LabVm` (revert to the newest checkpoint). Unless noted, the agent is
installed with `Install-LabAgent` (RemoteHub's `InstallOverRemoting`, scope = the host's address) and the operator
account is `PSAdmin`.

---

## 4. Lab self-check

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| SMOKE-01 | Console account after revert | `Admin` owns an explorer in an active session | PS | 1 | ✅ |
| SMOKE-02 | Admin token | PS Direct as `PSAdmin`: Administrators enabled, High integrity | PS | 1 | ✅ |
| SMOKE-03 | Standard user | `normaluser` not in S-1-5-32-544 | PS | 1 | ✅ |
| SMOKE-04 | Address | Hyper-V reports an IPv4 address; host reaches it (TCP 445 or ICMP) | PS | 1 | ✅ |
| SMOKE-05 | Auto-logon | `normaluser` reaches the desktop, screenshot not blank | PS C | 1 | ✅ |
| SMOKE-06 | Code in the user's session | task output shows the user's session ID | S | 1 | ✅ |
| SMOKE-07 | Lock / unlock | LogonUI appears in the user's session, gone after typing the password | S C | 1 | ✅ |
| SMOKE-08 | Clock | guest time within 2 min of the host (certificate validity, log times) | PS | 2 | ✅ |
| SMOKE-09 | No pending reboot / update | no `RebootPending`, TiWorker idle within 5 min; warning in the report otherwise | PS | 3 | ✅ |
| SMOKE-10 | Keyboard layout known | every account and the sign-in screen have only the US keyboard (German on a de-DE image), listed in `smoke-layout.json`; `Send-LabText` round-trips `a-zA-Z0-9 !@#$%` into Notepad | S C | 2 | ✅ |

## 5. Agent – install, update, uninstall (A)

### 5.1 First install

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-INST-01 | Install over remoting (`InstallOverRemoting`) | msiexec exit 0/3010; fingerprint read back = pin; deviceId and port match | H | 1 | ✅ |
| A-INST-02 | Service | `RemoteHubAgent` running, LocalSystem, Automatic (Delayed Start) | PS | 1 | ✅ |
| A-INST-03 | Health | `/v1/health` over TLS pinned: product, version = MSI version, protocol 1, deviceId, features `powershell`,`desktop`,`tunnel` (jump hosts are on by default) | H | 1 | ✅ |
| A-INST-04 | Certificate | LocalMachine\My, SHA-256 = pin, key not exportable, `CN=<deviceId>, O=RemoteHub Agent`, FriendlyName "RemoteHub Agent", RSA 3072, valid from creation −10 min for 2 years, EKU server + client auth, end-entity (CA=false), SAN `urn:remotehub:device:<deviceId>` | PS | 1 | ✅ |
| A-INST-05 | agent.json | deviceId, label, port, thumbprint, allowedGroups = S-1-5-32-544, virtualDisplay, logRetentionDays 30, maxSessions 20 | PS | 1 | ✅ |
| A-INST-06 | Firewall | exactly one rule "RemoteHub Agent": inbound, allow, TCP 58732, program = RemoteHubAgent.exe, remote address = host, all profiles | PS | 1 | ✅ |
| A-INST-07 | Folder ACL | `%ProgramData%\RemoteHub\Agent`: owner Administrators, protected, only SYSTEM + Administrators | PS | 1 | ✅ |
| A-INST-08 | No secrets left | no `.rhagent`, no PFX, no `RemoteHub-Agent-*` staging folder in the user's or Windows temp; `C:\Windows\Installer` copy contains no PFX | PS | 1 | ✅ (partly) |
| A-INST-09 | Event source | "RemoteHub Agent" registered; start line in the log with port, device and certificate | PS E | 2 | ✅ |
| A-INST-10 | Recovery actions | `sc qfailure`: restart ×3, 10 s, reset after 1 day; kill the process → service back within 30 s | PS | 2 | ✅ |
| A-INST-11 | Service survives a reboot | after `Restart-LabVm` health answers within 3 min (delayed start) with the same certificate | PS H | 1 | ✅ |
| A-INST-12 | Nothing extracted | no `.net` folder in `C:\Windows\Temp` or the SYSTEM profile; native DLLs only in Program Files | PS | 2 | ✅ |
| A-INST-13 | Firewall scope Any | install with empty scope → rule remote address Any | H PS | 2 | ✅ |
| A-INST-14 | Firewall scope list | scope `10.0.0.0/8, 192.168.5.10-192.168.5.20, <host>` → rule has exactly those; connection from the host works | H PS | 2 | ✅ |
| A-INST-15 | Other port | port 58800 → service listens there, rule for 58800, health works on 58800, nothing on 58732 | H PS | 2 | ✅ |
| A-INST-16 | Allowed groups by name | dialog value "Administrators" → stored as S-1-5-32-544 (also on de-DE, where the group is "Administratoren") | H PS | 1 | ✅ |
| A-INST-17 | Custom allowed group | allowedGroups = a local group "RH Operators" → member normaluser can sign in, PSAdmin (not a member) gets 403 | H PS | 2 | ✅ |
| A-INST-18 | Label with spaces / umlauts | label `Büro PC 7` → enrollment works, label in agent.json and Hello | H | 3 | ✅ |
| A-INST-19 | Virtual display off at install | `VIRTUALDISPLAY=0` → no `Root\MttVDD` device, no driver package, no SignPath trust | H PS | 2 | ✅ |
| A-INST-20 | Virtual display on at install | device installed and **disabled**, driver files hash-checked, SignPath thumbprint `3CF8…7D76` in TrustedPublisher, `virtual-display.txt` records `trustAdded` | H PS | 2 | ✅ |
| A-INST-21 | Trust already present | thumbprint in TrustedPublisher before install → `trustAdded=false`; uninstall leaves it | PS | 3 | ✅ |
| A-INST-22 | SoftwareSASGeneration set | no policy before → value 1 after service start, marker `sas.agent` present | PS | 1 | ✅ |
| A-INST-23 | SAS policy respected | value 0 set by "policy" before install (no marker) → stays 0, Ctrl+Alt+Del in the desktop tab explains it (D-SAS-04) | PS H | 2 | ✅ |
| A-INST-24 | selftest on the device | `RemoteHubAgent.exe selftest` → "Self-test passed.", exit 0, nothing changed (no log line, no cert) | PS | 2 | ✅ |
| A-INST-25 | Port in use | another listener on 58732, then install → install fails cleanly or service doesn't start; log says "could not start", no silent port change | PS H | 2 | ✅ |
| A-INST-26 | Install log for failures | force a failure (corrupt `.rhagent`) → RemoteHub's error shows the `WixQuietExec`/`Error` lines from the msiexec log | H | 2 | ✅ |
| A-INST-27 | Folder made before the install | normaluser creates `%ProgramData%\RemoteHub\Agent\logs\x.log` (and a subfolder) before the install → afterwards every entry owned by Administrators, only inherited ACEs (SYSTEM, Administrators), normaluser can neither read nor write | PS | 1 | ✅ |
| A-INST-28 | `AUTOSIGNIN=1` on a package without the provider | install succeeds without DLL and CLSID keys; Hello `autoSignIn` false; automatic sign-in not allowed – neither in `agent.json` (`noUserBehavior`, `autoSignInGroups`) nor in the policy key. Today the install's choice is still recorded (`agent.json` since 0.18.12, the policy key before; "Automatic sign-in allowed on this device for Administrators.") – product gap: `auto-sign-in=[AUTOSIGNIN]` outside the `CredentialProviderDll` condition | PS H | 3 | ✅ |

### 5.2 Other install paths

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| A-HAND-01 | MSI by hand next to the `.rhagent` | copy both to a folder, run msiexec as admin in Admin's session → installed; `WaitForClaim` claims it within 10 s polling: device ID and pin from the claim are the device's own | H S | 1 | ✅ | |
| A-HAND-02 | Two `.rhagent` files in the folder | install fails with "More than one .rhagent file", nothing enrolled, rollback clean | PS | 2 | ✅ | |
| A-HAND-03 | No `.rhagent` and no config | install fails, nothing left (rollback runs `cleanup`) | PS | 1 | ✅ | |
| A-HAND-04 | `ENROLLMENT=<path>` property | file in another folder → used (the claim names the device ID and certificate it made); file deleted afterwards | PS | 2 | ✅ | |
| A-HAND-05 | Enrollment format 3 / a format-1 file (with a device key) / bad key ID / bad secret / port 0 / empty groups / bad firewall addresses | each refused with a clear message ("…made by an older RemoteHub…", "The enrollment has no valid secret.", "Firewall addresses '…' are not valid.", …), install rolled back | PS | 2 | ✅ | |
| A-HAND-06 | Interactive install (not /qn) | the package has no dialogs of its own: Windows Installer's progress window (/qb) in the console session (its title noted by the session that runs msiexec, while it runs), installed and enrolled; VIRTUALDISPLAY is only a property | S | 3 | ✅ | |
| A-HAND-07 | Provider added or removed on an installed agent | `msiexec /i <msi> ADDLOCAL=CredentialProvider` / `/i {ProductCode} REMOVE=CredentialProvider` → 0/3010, DLL and both keys added / removed; same service PID, agent.json unchanged (its `noUserBehavior` = AutoSignIn and `autoSignInGroups` from the `AUTOSIGNIN=1` install stay – only the enroll step and RemoteHub's `auto-sign-in on/off` set them), no new enroll line; the policy key is not touched (empty: the install doesn't write it since 0.18.12) | PS | 2 | ✅ | |
| A-CLIP-01 | Clipboard install from RemoteHub | **2VM**: RemoteHub puts MSI + `.rhagent` on the clipboard, pasted into the RDP session, MSI run there → connection marked installed on first health | U | 2 | ✅ | 2VM |
| A-CLIP-02 | Staging cleanup on the admin's PC | `%TEMP%\RemoteHub-Agent\<id>` gone after RemoteHub exits and after the next start (crash case) | U PS | 1 | ✅ | 2VM |
| A-CLIP-03 | Ask before sending the key through a desktop tab | agent desktop tab to another device open → RemoteHub asks before the clipboard install (was D-DESKCLIP) | U | 1 | ✅ | 2VM |

### 5.3 Update, repair, downgrade

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-UPD-01 | Update through the agent (`StartUpdateThroughAgent`) | previous release installed, update to the build under test → agent down within 90 s, back within 3 min with the new version, same certificate | H | 1 | ✅ |
| A-UPD-02 | Temp folder removed | `RemoteHub-Agent-<hex>` folder gone after msiexec ends (within 3 min); `%TEMP%\RemoteHub-Agent-update.log` of the signing-in account stays and says the install succeeded (RemoteHub points to it on failures) | PS | 2 | ✅ |
| A-UPD-03 | Identity kept | deviceId, thumbprint, port, firewall scope, allowedGroups unchanged; no duplicate certificates | PS | 1 | ✅ |
| A-UPD-04 | New device certificate on update | "Enroll again" → the device makes a new certificate, RemoteHub claims it; new pin works, old certificate gone, old pin refused | H PS | 1 | ✅ |
| A-UPD-05 | Update over remoting | same as A-UPD-01 but through `InstallOverRemoting` | H | 2 | ✅ |
| A-UPD-06 | Update keeps policies and logs | policy key and old log files untouched | PS | 2 | ✅ |
| A-UPD-07 | Update with virtual display change | `VIRTUALDISPLAY=0` on update removes the driver; empty keeps it | H PS | 3 | ✅ |
| A-UPD-08 | Update during an open PowerShell session | the session open during the update has ended, update completes, a new session (new PowerShell process) works | H | 2 | ✅ |
| A-UPD-09 | Update during an open desktop session | desktop session ends, helper process gone, banner gone, new session works | H S | 2 | ✅ |
| A-UPDFAIL-01 | Broken MSI through the agent | package with a false launch condition (same version) → RemoteHub reports "didn't install … still runs <old>" (not "updated"); old agent answers with the old certificate | H | 1 | ✅ |
| A-UPDFAIL-02 | Upgrade that fails in enroll | damaged `.rhagent` sent with the update → enroll fails after the old version was removed, upgrade rolls back: old version running, old enrollment kept (rollback `cleanup` not run on upgrade), no staging folder | PS H | 1 | ✅ |
| A-UPDFAIL-03 | Update that never comes back | block the new service (port in use) → RemoteHub times out after 3 min and says so | H | 2 | ✅ |
| A-UPDFAIL-04 | File that isn't a package | truncated MSI through the agent → reported as failed, never as "updated" (the version can't be read from it); old agent keeps running | H | 1 | ✅ |
| A-SAME-01 | Reinstall the same version | through the agent → reported updated, identity kept | H | 3 | ✅ |
| A-SAME-02 | Same version with a new device certificate | "Install again with Enroll again" (RemoteHub's advice on a pin mismatch) → new certificate answers, old one refused, one certificate left | H | 2 | ✅ |
| A-DOWN-01 | Downgrade | older MSI over newer → refused: "A newer version of the RemoteHub Agent is already installed.", newer still running | PS | 2 | ✅ |
| A-REPAIR-01 | Repair from the MSI | Programs and Features offers no Repair (`NoRepair` = 1, `ARPNOREPAIR`): RemoteHub deletes the folder it installed from and the cached package has no files, so `msiexec /fa` – by product code or with a copy of the MSI – only looks in the registered source list and ends with 1706 / 1603. `msiexec /i <package> REINSTALL=ALL REINSTALLMODE=amus` (the package under its registered name) repairs: 0/3010, identity kept, service running; the enrollment step doesn't run (no `ENROLLMENT` given) | PS | 3 | ✅ |
| A-ROLLBACK-01 | First install fails after enrolling | forced failure after AgentEnroll → no rule, no certificate, no agent.json, no service | PS | 1 | ✅ |

### 5.4 Uninstall

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-UNINST-01 | Installed before | health answers | H | 1 | ✅ |
| A-UNINST-02 | `msiexec /x /qn` | exit 0 / 3010 | PS | 1 | ✅ |
| A-UNINST-03 | Product, service, folder | all gone | PS | 1 | ✅ |
| A-UNINST-04 | Firewall rule | gone | PS | 1 | ✅ |
| A-UNINST-05 | Certificates | no `O=RemoteHub Agent` certificate in LocalMachine\My | PS | 1 | ✅ |
| A-UNINST-06 | agent.json | `agent.json` and `autosignin.json` gone | PS | 1 | ✅ |
| A-UNINST-07 | Logs kept | log folder stays, ends with "Uninstalled: firewall rule, certificate … removed" | PS | 2 | ✅ |
| A-UNINST-08 | Event source | gone | PS | 2 | ✅ |
| A-UNINST-09 | Port | nothing answers on 58732 | H | 1 | ✅ |
| A-UNINST-10 | SoftwareSASGeneration | back to the value before install (deleted when the agent set it, kept when a policy set it) | PS | 1 | ✅ |
| A-UNINST-11 | Virtual display removed | device, driver package, `C:\VirtualDisplayDriver`, `virtual-display.txt` gone; SignPath trust removed only if the agent added it | PS | 2 | ✅ |
| A-UNINST-12 | Uninstall from Settings → Apps | UIA uninstall in Admin's session (search, then *More options* in RemoteHub Agent's own row – never the first app's) → same result as A-UNINST-02…11 | S | 3 | ✅ |
| A-UNINST-13 | Uninstall while a desktop session is open | session ends, helper gone, display restored, no banner left | H S | 2 | ✅ |
| A-UNINST-14 | Reinstall after uninstall | new enrollment works, no duplicate certs or rules | H | 2 | ✅ |
| A-UNINST-15 | Uninstall after automatic sign-in | after an `AUTOSIGNIN=1` install (`agent.json` `noUserBehavior` = AutoSignIn, `autoSignInGroups` with Administrators; policy key empty, no `autosignin.policy` – docs/admx-design.md §11.3): provider DLL and both keys gone, `agent.json` and with it the install's choice removed, the policy key still empty | PS | 1 | ✅ |
| A-UNINST-16 | The install's choice, IT's policy values and an older version's values | after an `AUTOSIGNIN=1` install (its choice in `agent.json`, policy key empty) IT sets `Rights\AutoSignIn` → uninstall removes `agent.json`, IT's value stays; IT sets `NoUserBehavior=SignInScreen` → `AUTOSIGNIN=0` (update through the agent) writes `noUserBehavior` = `SignInScreen` and clears `autoSignInGroups` in `agent.json` (not set would mean allowed – on by default), IT's value stays. Migration (version-independent, planted): the policy key and `autosignin.policy` as a version before 0.18.12 leaves them – `NoUserBehavior` unchanged, `Rights\AutoSignIn` changed by IT – then a service restart → `NoUserBehavior` moved to `agent.json`, `Rights\AutoSignIn` stays in the policy key, the marker deleted, the log says "moved NoUserBehavior" and "Rights\AutoSignIn changed since the install – left in the policy key" | PS | 3 | ✅ |

### 5.5 Configuration and service behaviour

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-CFG-01 | Invalid agent.json (port 0, empty groups, maxSessions 0, virtualDisplay `x`) | service doesn't start, log says why; no fallback to defaults | PS E | 2 | ✅ |
| A-CFG-02 | agent.json with comments / trailing commas | accepted | PS | 3 | ✅ |
| A-CFG-03 | Certificate missing / key missing | delete the cert → service refuses to start and logs it | PS E | 2 | ✅ |
| A-CFG-04 | Log rotation | log files dated 31+ days old (faked names) deleted at the first write of the day; `logRetentionDays 1` respected | PS | 3 | ✅ |
| A-CFG-05 | Log content | sign-in, refusal (account, IP, reason), session start/end with PID and duration – and **never** the password or command text (search the log for the test password and a marker command) | H PS | 1 | ✅ |
| A-CFG-06 | Logs not readable by users | `normaluser` can't read the log folder | PS | 1 | ✅ |
| A-CFG-07 | No outbound traffic | during a PowerShell session the agent opens no outbound connection (`Get-NetTCPConnection -OwningProcess`) – only jump connections RemoteHub asks for go out (J-* in jump-host-design §11) | PS | 1 | ✅ |
| A-CFG-08 | Event log writes off | policy `AuditToEventLog=0` → audit only in the file | PS E | 3 | ✅ |

## 6. Agent – sign-in and transport security (S)

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| S-TLS-01 | TLS versions | TLS 1.2 and 1.3 accepted; TLS 1.0/1.1 refused (host `SslStream` with forced protocols) | H | 1 | ✅ |
| S-TLS-02 | No plain HTTP | `http://vm:58732/v1/health` fails | H | 1 | ✅ |
| S-TLS-03 | Pin mismatch | client with a wrong pin stops before sending credentials: no 4625, no sign-in line in the agent log | H PS E | 1 | ✅ |
| S-TLS-04 | Health reveals nothing | health JSON has only product, version, protocol, deviceId, features, managed (only the names `autoSignIn`, `virtualDisplay`, `port` – never values) and instance (a random 32-hex id per agent start); no `Server` header | H | 1 | ✅ |
| S-AUTH-01 | Wrong password | 401, event 2000, Security 4625 | H E | 1 | ✅ |
| S-AUTH-02 | Missing / malformed Basic header | 401 with `WWW-Authenticate: Basic realm="RemoteHub Agent"`, not counted as a failure | H | 2 | ✅ |
| S-AUTH-03 | Not a WebSocket request | 400 | H | 3 | ✅ |
| S-AUTH-04 | Standard user | `normaluser` → 403 "not a member of an allowed group", 2000 | H E | 1 | ✅ |
| S-AUTH-05 | User name formats | `PSAdmin`, `.\PSAdmin`, `<COMPUTER>\PSAdmin` all work; `PSAdmin@<computer>` handled as the code says | H | 2 | ✅ |
| S-AUTH-06 | Deny network logon | "Deny access to this computer from the network" for PSAdmin (secedit) → 403 (1385), not bypassed | H PS | 1 | ✅ |
| S-AUTH-07 | Account lockout | lockout threshold 3 → after 3 wrong passwords Windows locks the account; agent refuses the right password too (401) | H PS | 2 | ✅ |
| S-AUTH-08 | Disabled / expired account | 401, logged | H PS | 2 | ✅ |
| S-AUTH-09 | Non-RID-500 admin gets a full token | `lapsadmin` (created by the test, Administrators) → `whoami /groups` High integrity, can write HKLM, without `LocalAccountTokenFilterPolicy` | H | 1 | ✅ |
| S-AUTH-10 | Empty password account | refused by Windows (network logon with blank password), 401 | H PS | 3 | ✅ |
| S-THR-01 | Throttle | 5 wrong passwords → 6th try 429 even with the right password; event 2000 "too many failed sign-ins" | H E | 1 | ✅ |
| S-THR-02 | Parallel attempts | 6 wrong passwords in parallel → at most 5 reach Windows (4625 count ≤ 5) | H E | 1 | ✅ |
| S-THR-03 | Block ends | after 5 min the right password works again | H | 2 | ✅ |
| S-THR-04 | Success clears | 4 wrong, 1 right, 4 wrong → no 429 | H | 3 | ✅ |
| S-THR-05 | Restart resets | blocked, restart service → right password works | H PS | 3 | ✅ |
| S-THR-06 | What counts toward the block | 5 refused sign-ins with the right password from one address (normaluser 403, a deny-network-logon account) block it, the 6th gets 429; unmapped Windows errors (HTTP 500) don't count | H E | 2 | ✅ |
| S-THR-07 | Shared sign-in slots | at most 8 Windows sign-ins at once across all addresses; no slot within 30 s → 503 (needs ≥ 2 source addresses) | H | 3 | ✅ |
| S-LIM-01 | Session limit | `maxSessions 2` → 3rd session (PowerShell, desktop or jump connection) 503; log line | H | 2 | ✅ |
| S-POL-01 | Unreadable policy key | deny SYSTEM read on `HKLM\SOFTWARE\Policies\RemoteHub\Agent` → everything refused, health features empty (an unreadable key allows nothing, so not even `powershell` – since `PowerShellEnabled`), log "can't be read" | PS H | 1 | ✅ |
| S-POL-02 | Policy read live | change a policy value, next connection uses it without a service restart | PS H | 2 | ✅ |
| S-POL-03 | `Rights\PowerShell` | set to a group without PSAdmin → PowerShell 403, desktop still works (Rights\Desktop default) | PS H | 1 | ✅ |

## 7. Agent – PowerShell and remote tools (P, T)

The tool cases run twice: **over the agent** and **over WinRM HTTPS** (after W-HTTPS-01), through RemoteHub's own
classes (`RemoteFs`, `FileTransfer`, `RemoteRegistry`, `RemoteServices`, `RemoteProcesses`) in the harness.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| P-SES-01 | Session | runspace opens, `$PSVersionTable.PSVersion` 5.1, `whoami` = PSAdmin, event 1000 with PID | H E | 1 | ✅ |
| P-SES-02 | Profile + environment | `$env:USERPROFILE` = PSAdmin's profile, HKCU writable | H | 2 | ✅ |
| P-SES-03 | End kills the tree | start `cmd /k`/`ping -t` in the session, close it → powershell.exe and children gone within 5 s, event 1001 | H PS E | 1 | ✅ |
| P-SES-04 | Large arguments | 2 MB argument and 10 MB output round-trip | H | 2 | ✅ |
| P-SES-05 | Message > 16 MiB | session ends with "Message too large", agent stays healthy | H | 3 | ✅ |
| P-SES-06 | Network drop | `Disconnect-VMNetworkAdapter` during a session → agent ends it within ~60 s (keep-alive 30+30), powershell.exe gone, 1001 "connection lost" | N PS E | 2 | ✅ |
| P-SES-07 | 20 sessions in parallel | all work; 21st 503 | H | 3 | ✅ |
| P-SES-08 | Read-Host / Get-Credential prompts | answered through the host UI callbacks | H | 3 | ✅ |
| P-SES-09 | Tab completion | `TabExpansion2` results for `Get-Ser`, `-Na`, `C:\Win` | H | 3 | ✅ |
| P-SES-10 | PowerShell can't start | e.g. an IFEO `Debugger` on powershell.exe pointing to a missing file → 500, log "Could not start PowerShell for <acct>: …", no event 1000, the session slot released | H PS E | 3 | ✅ |
| P-CLM-01 | Constrained Language Mode | on CLM VM: session opens, `$ExecutionContext.SessionState.LanguageMode` ConstrainedLanguage; tools needing FullLanguage report it clearly | H | 2 | ✅ |
| T-FS-01 | List drives / folders | drives incl. C:, hidden files flagged, `%TEMP%` expands remotely | H | 2 | ✅ |
| T-FS-02 | Upload small files | 300 files → all arrive, SHA-256 equal, no `.remotehub-part` left | H PS | 1 | ✅ |
| T-FS-03 | Upload large file | 200 MB compressible + 200 MB random → equal hashes; compressed one sent gzipped; written uncompressed | H PS | 1 | ✅ |
| T-FS-04 | Upload folder tree | nested folders with Unicode names (`Ünïcödé 文件`), long paths (> 260) | H PS | 2 | ✅ |
| T-FS-05 | Skip existing / overwrite | existing files not sent with skip; replaced with overwrite | H PS | 2 | ✅ |
| T-FS-06 | Cancel upload | cancel mid-file → no partial file, no `.remotehub-part` | H PS | 1 | ✅ |
| T-FS-07 | Download | file and folder download, streamed: hashes equal, every item "SHA-256 verified", the 200 MB text "sent compressed", the 200 MB random file not; no `.remotehub-part` left; time in the evidence; a whole drive refused | H | 1 | ✅ |
| T-FS-08 | Rename / delete / new folder | done remotely; drive root delete refused | H PS | 2 | ✅ |
| T-FS-09 | Locked file | upload over a file opened exclusively → clear error, original untouched | H PS | 3 | ✅ |
| T-FS-10 | Defender real-time on | large tree upload still verified (rename scanning) | H | 3 | ✅ |
| T-FS-11 | Cancel download | cancel a 200 MB download after 20 MB → item `Cancelled`, no file and no `.remotehub-part` on this PC | H | 1 | ◐ |
| T-FS-12 | Download: skip existing / overwrite | the 300 files again without overwrite → all skipped; with overwrite → a file changed on this PC is replaced | H | 2 | ◐ |
| T-REG-01 | Browse | `HKLM\SOFTWARE`, all path spellings (`HKLM:\`, `Registry::`, `Computer\`) | H | 2 | ✅ |
| T-REG-02 | Value types | create, read, modify, delete String, ExpandString (stays unexpanded), Multi, DWORD, QWORD, Binary; check with `reg query` | H PS | 2 | ✅ |
| T-REG-03 | Key rename keeps ACL | rename a key with a custom ACL → ACL kept | H PS | 3 | ✅ |
| T-REG-04 | Export | `.reg` file arrives and matches `reg export` | H | 3 | ✅ |
| T-REG-05 | Hive protection | delete/rename `HKLM\SOFTWARE` refused | H | 1 | ✅ |
| T-SVC-01 | List | matches `Get-CimInstance Win32_Service` (name, state, start mode, account, PID) | H PS | 2 | ✅ |
| T-SVC-02 | Start / stop / restart | a test service (`Spooler`) changes state within 60 s | H PS | 2 | ✅ |
| T-SVC-03 | Startup type | Automatic, Automatic (delayed), Manual, Disabled set and verified | H PS | 2 | ✅ |
| T-PROC-01 | List | contains the tab's own process, user names shown for an admin | H | 2 | ✅ |
| T-PROC-02 | End process / tree | test process tree ended completely | H PS | 2 | ✅ |
| T-PROC-03 | Critical processes | ending System, Idle, Registry, smss, csrss, wininit, winlogon, services, lsass, lsaiso (PID 0 / 4) refused: "<name> (PID n) is a critical Windows process and can't be ended from here." (the own remoting process is refused only by the Processes tab, UI-TL-05) | H | 1 | ✅ |

### 7.1 Network shares of a connection (S-SHARE)

RemoteHub's `ShareConnector` + `ShareTransfer` (harness "share") against shares the server VM offers (`Enable-LabShareServers`): SMB, SFTP on port 2222, FTP with TLS required, WebDAV over HTTP and HTTPS. Each transfer is a 3 MB file and a small one, compared by SHA-256.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| S-SMB-01 | SMB through Windows | opens the share through Windows' client, copies both ways, hashes equal | H PS | 1 | ✅ | Server |
| S-SMB-02 | Second account, same server | RemoteHub's own SMB client for the second account (Windows allows one account per server), writes there | H | 2 | ✅ | Server |
| S-SMB-03 | Standard user | normaluser copies both ways on the share it may use | H PS | 2 | ✅ | Server |
| S-SMB-04 | Wrong password | reported as such | H | 2 | ✅ | Server |
| S-SMB-05 | No account → Windows account | SMB without credentials connects as the signed-in Windows user; description "SMB · Windows · your Windows account", tab source "your Windows account (no account set)" | H | 3 | ✅ | Server |
| S-SMB-06 | Windows connection lifetime | no drive letter; two tabs on one share → one connection; closing the last tab removes it (`net use` empty); a connection that existed before is used and stays; RemoteHub exiting removes its own | H PS | 2 | ✅ | Server |
| S-SMB-07 | Port / name rules | port ≠ 445 → "SMB always uses port 445 – remove the port from the share."; empty share name → "The share has no share name – edit it and enter the name …"; a pasted `\\host\share\folder` fills in host and path in the editor | H U | 3 | ✅ | Server |
| S-SMB-08 | Access denied for a local admin | a share allowing only Administrators, opened as a local admin → Windows' message plus "… A local administrator signing in over the network gets no administrator rights there (Windows' remote UAC) …" | H PS | 3 | ✅ | Server |
| S-SFTP-01 | SFTP | asks about the host key once, copies both ways | H PS | 1 | ✅ | Server |
| S-SFTP-02 | Key accepted | connects without asking again | H | 2 | ✅ | Server |
| S-SFTP-03 | Key changed | a key that differs from the accepted one is refused when the user says no | H | 1 | ✅ | Server |
| S-SFTP-04 | SFTP needs an account | no account → "<name> (SFTP) needs an account. Right-click it → Edit… and set one …"; editor "SFTP needs a user name."; without a key, password and keyboard-interactive only (a key-only server refuses it like a wrong password; keys: S-SSH-04, S-SSH-05) | U H | 2 | ✅ | Server |
| S-SFTP-05 | Wrong password | "The user name or password is incorrect (or the server doesn't allow password sign-in)." | H | 2 | ✅ | Server |
| S-FTP-01 | FTP with TLS | asks about the self-signed certificate once, copies both ways over TLS | H PS | 2 | ✅ | Server |
| S-FTP-02 | Certificate refused | nothing sent | H | 1 | ✅ | Server |
| S-FTP-03 | FTP without TLS | security None → description "FTP · not encrypted", address `ftp://`, the editor warns "Not encrypted: the password and every file go over the network in plain text …" | H U | 3 | ✅ | Server |
| S-FTP-04 | Implicit TLS / anonymous | implicit → port 990; no account → user "anonymous", tab source "anonymous sign-in (no account set)"; wrong password → "The user name or password is incorrect." | H | 3 | ✅ | Server |
| S-DAV-01 | WebDAV over HTTPS | copies both ways, Windows authentication | H PS | 2 | ✅ | Server |
| S-DAV-02 | WebDAV over HTTP | copies both ways | H PS | 3 | ✅ | Server |
| S-DAV-03 | WebDAV wrong password | reported as such | H | 2 | ✅ | Server |
| S-DAV-04 | No account / redirect / status codes | no account → the Windows credentials; 301/302/307/308 → "The server redirects to <Location> – use that address for the share."; 403/404/409/423/507 → their messages | H | 3 | ✅ | Server |
| S-SHR-01 | Password not stored | prompt "No password is stored for <user>. Enter it to open <server › share> – it is kept in memory only."; Reconnect reuses it without asking; nothing written to the stored secrets | U | 2 | ✅ | Server |
| S-SHR-02 | Trust dialog / Forget | first key or certificate → "Trust this server?" (*Trust and connect*); a changed key → "Server key changed"; the accepted value is kept; *Forget* in the editor → asked again next time; answered after 30 s (no time limit on the question) it still connects, or is refused with its message | U | 2 | ✅ | Server |
| S-SHR-03 | Idle close | after 5 min idle "Idle – the connection to the share was closed and opens again when you use it."; the next listing reconnects without asking | H U | 3 | ✅ | Server |
| S-SHR-04 | Transfer safety | uploads go to `<name>.remotehub-part`, size checked, then renamed; existing file without overwrite → "Skipped – the file already exists"; same-name folder → "Skipped – a folder has that name"; size mismatch → "… the transfer was incomplete." and the part file removed | H | 2 | ✅ | Server |
| S-SHR-05 | Browse actions | deleting the share root → "The share itself can't be deleted."; renaming onto an existing name → "\"<n>\" already exists." | H U | 3 | ✅ | Server |
| S-SHR-06 | Shares on their own | New → New connection → SMB: host required ("Enter the host the share is on (name or address)."), inherits the folder's account, Duplicate / Move under in its menu; Remove: "Nothing on the share itself is deleted." and its open tabs closed | U | 2 | ✅ | Server |

### 7.1a SSH connections and private keys (S-SSH)

RemoteHub's SSH tab code (`SshSession`, `SshShell`, the terminal emulator – harness "ssh") and SFTP with a key (harness "share --key") against the server VM's OpenSSH on port 2222 (`Enable-LabShareServers`), with keys made for the run (`New-LabSshKey`) and authorized for its administrators (`Set-LabSshAuthorizedKeys`); the UI case in RemoteHub's window on the admin PC. `S-SSH.Tests.ps1`, design: network-shares.md §6.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| S-SSH-01 | Terminal over SSH | password sign-in on port 2222; "Trust this server?" asked once (SSH host key) and the fingerprint kept; the tab's status "Connected – SSH · OpenSSH…"; `hostname` typed into the terminal prints the device's name; `exit` → "The session ended." | H | 1 | ◐ | Server |
| S-SSH-02 | Host key | an accepted key → no question; refused → "The server's key wasn't accepted – nothing was sent."; a different key than the accepted one → asked as "SSH host key (changed!)" and, refused, the same message | H | 1 | ◐ | Server |
| S-SSH-03 | Terminal size | the shell gets the tab's size; after a resize to 100×30 `mode con` shows 100 columns | H | 2 | ◐ | Server |
| S-SSH-04 | Private key sign-in | an ed25519 key with a passphrase in `administrators_authorized_keys` signs in without a password; a wrong passphrase → "The passphrase is wrong …"; none → "… protected by a passphrase …"; a key the server doesn't know → "The server didn't accept the private key for this user (is its public key in the user's authorized_keys?)"; that key and the password → signs in (the key first, then the password) | H | 1 | ◐ | Server |
| S-SSH-05 | SFTP with a private key | SFTP signs in with the key alone and copies both ways; the host key SSH accepted is the SFTP share's too (no question) | H PS | 2 | ◐ | Server |
| S-SSH-06 | SSH in RemoteHub's window | New → New connection → SSH opens "New SSH connection" (no start folder, *Import key…* in the account); *Add*, *Open* → "Trust this server?" → *Trust and connect*; `hostname` typed into the terminal shows the device's name (read through the terminal's UI Automation value); Ctrl+W goes to the shell (the tab stays); `exit` → "The session ended." | U | 2 | ◐ | Server |
| S-SSH-07 | Nothing identifying in DiagLog | after the SSH sessions this PC's `remotehub.log` has "SSH: connected" and names neither the device, its address nor the account | H E | 1 | ◐ | Server |

### 7.2 Running scripts (S-SCRIPT)

The Scripts view's runs ([scripts-view-design.md](scripts-view-design.md) §6) through RemoteHub's `ScriptRunner` in the
harness (`tool --op script-run`, `script-info`): once over the agent with the standard user signed in on the console
(`-AutoLogon User`), once over WinRM HTTPS with nobody signed in. What a run leaves on the device – scheduled tasks
under `\RemoteHub\`, folders under `%ProgramData%\RemoteHub\Runs` – is read over PowerShell Direct. A non-administrator
account isn't covered here: the agent admits only administrators by default (the message is a unit test).
`S-SCRIPT.Tests.ps1`.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| S-SCRIPT-01 | System run (agent) | `whoami` prints `nt authority\system` from a 64-bit process, *Succeeded*, exit code 0; no task and no run folder left | H PS | 1 | ✅ |
| S-SCRIPT-02 | User run (agent) | `script-info` lists the signed-in standard user as the console user; a User run prints that user, writes to their own HKCU (read under `HKEY_USERS\<SID>`), not SYSTEM; no task left | H PS | 1 | ✅ |
| S-SCRIPT-03 | As me (agent) | prints the connection's account (PSAdmin) | H | 2 | ✅ |
| S-SCRIPT-04 | Exit codes and parameters | `exit 3010` → *Succeeded – restart needed*; `throw` → *Failed*, exit code 1, the error in the output; `-Name`, `-Days`, `-Apply` from the form arrive as typed | H | 2 | ✅ |
| S-SCRIPT-05 | Cancel | *Cancel* 5 s after the first output → *Cancelled* within 2 min; no task, no run folder, no powershell.exe from the run left | H PS | 1 | ✅ |
| S-SCRIPT-06 | Large output | 2.4 MB of output → the run ends *Succeeded*, the output is cut with "Output cut off after 1 MB" | H | 3 | ✅ |
| S-SCRIPT-07 | System run (WinRM) | as S-SCRIPT-01 over WinRM HTTPS, nothing left behind | H PS | 1 | ✅ |
| S-SCRIPT-08 | User run with nobody signed in | refused: "No user is signed in on the device." | H | 2 | ✅ |
| S-SCRIPT-09 | GPO-set execution policy | `AllSigned` set by policy (HKLM\SOFTWARE\Policies\Microsoft\Windows\PowerShell) → refused: "The device's group policy only allows signed scripts."; no task created | H PS | 1 | ✅ |

### 7.3 Device actions (T-DEV)

A connection's Device actions (README → Remote tools) through RemoteHub's `DeviceActions` in the harness
(`tool --op dev-users`, `dev-action`): once over the agent, once over WinRM HTTPS, each time with the standard user
signed in on the console (`-AutoLogon User`). Sessions are read over PowerShell Direct as WTS lists them
(`Get-LabSessions`), the message on the console screen (OCR), the shut-down VM on the host. Automatic sign-in may sign
the user in again after a sign-out or restart: the old session's programs are what must end. `T-DEV.Tests.ps1`.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| T-DEV-01 | Who is signed in | the console user with the console session's id; no `DWM-n` / `UMFD-n`, nothing in session 0 | H PS | 2 | ◐ |
| T-DEV-02 | Send message | answered "Message shown on <device> to …"; the text appears on the console within 60 s | H C | 2 | ◐ |
| T-DEV-03 | Lock | the user's session *Disconnected* within 60 s with the same explorer.exe; the console shows the sign-in screen | H PS | 1 | ◐ |
| T-DEV-04 | Sign out | answered "Signed out on <device>: …"; the session's explorer.exe gone within 90 s | H PS | 1 | ◐ |
| T-DEV-05 | Update Group Policy | "Group Policy was updated on …"; a manual run (event 4004 / 4005) in Group Policy's log after the request | H PS E | 2 | ◐ |
| T-DEV-06 | Sync with Intune | on a device without an MDM enrollment: "isn't enrolled in Intune"; with one, the sync task started | H PS | 3 | ◐ |
| T-DEV-07 | No administrator's rights (agent) | an account with the agent's PowerShell right but not an administrator: "RemoteHub can't see who is signed in: the account isn't an administrator on the device" – not "nobody is signed in" | H PS | 2 | ◐ |
| T-DEV-08 | Restart | a new boot time within 10 min; event 1074 with "Requested from RemoteHub"; the agent answers again | H PS E | 1 | ◐ |
| T-DEV-09 | Shut down | the VM is off within 5 min | H | 1 | ◐ |

## 8. WinRM setup script (W)

The script RemoteHub generates, run in the guest by PS Direct (`Invoke-LabSetupScript`), then RemoteHub's own
remoting code connects from the host.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| W-HTTPS-01 | HTTPS setup | runs without errors; prints a thumbprint; listener on 5986 bound to it | PS | 1 | ✅ | |
| W-HTTPS-02 | Certificate | end-entity, server auth only, RSA 3072, key not exportable, SAN = IP or name, 2 years | PS | 1 | ✅ | |
| W-HTTPS-03 | Fetched certificate matches | `FetchCertificateAsync` sees the same thumbprint | H | 1 | ✅ | |
| W-HTTPS-04 | Session pinned | HTTPS session from the host works with the certificate trusted | H | 1 | ✅ | |
| W-HTTPS-05 | Firewall | `RemoteHub-WinRM-HTTPS-In` only for the given addresses; built-in `WINRM-HTTP-In-TCP` off | PS | 1 | ✅ | |
| W-HTTPS-06 | Reject from elsewhere | connection from an address outside the list fails (second NIC / other VM) | N | 2 | ✅ | |
| W-TWICE-01 | Run twice | same thumbprint (reused while valid > 30 days), one rule | PS | 2 | ✅ | |
| W-PRE-01 | Built-in rule on before | stays on, script warns | PS | 2 | ✅ | |
| W-LISTENER-01 | Existing HTTPS listener | kept, only its certificate set, warning "An HTTPS listener already exists - its certificate is replaced." | PS | 2 | ✅ | |
| W-DRY-01 | `$DryRun = $true` | prints changes, changes nothing (listeners, rules, certs compared before/after) | PS | 2 | ✅ | |
| W-LEGACY-01 | Old rule name | `RdpManager-WinRM-HTTPS-In` present → removed | PS | 3 | ✅ | |
| W-LATFP-01 | LocalAccountTokenFilterPolicy option | set only when chosen | PS | 2 | ✅ | |
| W-NONADMIN-01 | Non-RID-500 admin over WinRM without LATFP | filtered token (documents the difference to the agent, S-AUTH-09) | H | 3 | ✅ | |
| W-HTTP-01 | HTTP + Kerberos by name | session works, 5985 only from given addresses | H PS | 2 | ✅ | Domain |
| W-TRUST-01 | Trust prompt on the admin's PC | **2VM**: "Test again" puts "RemoteHub – WinRM <host>" in CurrentUser\Root after the Windows prompt (UIA clicks Yes), connection switches to HTTPS | U | 2 | ✅ | 2VM |
| W-NAME-01 | Setup by computer name | certificate CN=<name>, SAN `DNS=<fqdn>` and `DNS=<short>`; reuse needs the same CN; a name with other characters refused by RemoteHub "… isn't a computer name or IP address." | PS | 3 | ✅ | |

## 9. Agent remote desktop (D)

Desktop cases need a **headless desktop client in the harness** (RemoteHub's `AgentDesktopSession` without the WPF
view: Hello → Start → Status, decodes frames to PNG, sends input/clipboard/Pin). The person at the device is
simulated with **S** (UIA on the agent's consent/PIN/banner windows) and **C** (console keyboard).
"Picture shows X" = the decoded frame matches a reference area or contains a marker window with a known colour/text.

### 9.1 Connect, Hello, sessions

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-CON-01 | Hello | account, rights {control, secureDesktop, consentBypass=false, privacyScreen, privacyInputBlock, autoSignIn}, modes Ask+Pin, sessions, max fps 60 / quality 100, timeout 30, clipboard 3, features (incl. `privacyScreen`, `autoSignIn` where allowed), agent version | H | 1 | ✅ |
| D-CON-02 | Session list | console first; Admin's active console, normaluser's disconnected session listed as Disconnected; session 0 never | H PS | 2 | ✅ |
| D-CON-03 | Disconnected session refused | Start on a disconnected session → Failed "can't be shown", event 2100 | H E | 2 | ✅ |
| D-CON-04 | No Start within 5 min | connection closed by the agent | H | 3 | ✅ |
| D-CON-05 | First message not Start | connection closed | H | 3 | ✅ |
| D-CON-06 | Desktop off by policy | `DesktopEnabled=0` → `/v1/desktop` 404, health features `powershell`,`tunnel` (no `desktop`) | PS H | 1 | ✅ |
| D-CON-07 | Rights\Desktop without PSAdmin | 403 on desktop, PowerShell still works | PS H | 1 | ✅ |
| D-CON-08 | Frames arrive | after Connected: key frame, decodes to the guest resolution, picture matches a console screenshot (≥ 95 % similar) | H C | 1 | ✅ |
| D-CON-09 | End by RemoteHub | close → event 1101 with duration, helper process gone within 5 s, banner gone | H S E | 1 | ✅ |
| D-CON-10 | Helper runs as SYSTEM in the session | `RemoteHubAgent.exe desktop` in the chosen session ID (the active console session for the console), as SYSTEM, in a job object | PS | 2 | ✅ |
| D-CON-11 | Unrequested sign-in request | a SignInRequest (type 31) RemoteHub wasn't asked for closes the connection; during a session it is a protocol error and the resume token is void | H E | 3 | ✅ |

### 9.2 Consent

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-ASK-01 | Ask → Allow | normaluser signed in; dialog "PSAdmin wants to see and control your screen" with Allow / Allow and hide apps… / Deny (read by OCR – the agent's windows have no UIA), Allow → Connected, event 1100 consent Ask | H S E | 1 | ✅ |
| D-ASK-02 | Ask → Deny | Denied "didn't allow it", event 2100 | H S E | 1 | ✅ |
| D-ASK-03 | Ask → no answer | the dialog denies after ConsentTimeoutSeconds (30): Denied "The user at the device didn't allow it." (the service's own fallback is +190 s) | H S | 1 | ✅ |
| D-ASK-04 | Esc / close = deny, Enter never allows | Enter sent to the dialog → still waiting; Esc → Denied | H S C | 1 | ✅ |
| D-ASK-05 | Timeout policy | `ConsentTimeoutSeconds=5` → countdown 5 s; values 4 and 601 ignored (default 30) | PS H S | 2 | ✅ |
| D-ASK-06 | No input before consent | mouse/keys sent while WaitingForUser don't reach the session (marker app counts input) | H S | 1 | ✅ |
| D-ASK-07 | View-only text | view-only Start → dialog says "see" not "control" | H S | 3 | ✅ |
| D-ASK-08 | RemoteHub closes while asking | 2100 "…: The connection ended before access was granted." | H E | 3 | ✅ |
| D-ASK-09 | Hide apps from the question | *Allow and hide apps…* opens the app picker; the countdown restarts; the chosen apps hidden from the start | H S | 2 | ✅ |
| D-PIN-01 | PIN right | 6-digit code read by UIA, sent → Connected | H S | 1 | ✅ |
| D-PIN-02 | PIN wrong ×3 | attemptsLeft 2,1, then Denied "entered wrong too often", event 2100 | H S E | 1 | ✅ |
| D-PIN-03 | PIN with spaces / dash | "123 456" accepted (non-digits stripped) | H S | 3 | ✅ |
| D-PIN-04 | PIN dialog Deny | Denied "refused" | H S | 2 | ✅ |
| D-PIN-05 | PIN timeout | no entry for 3 min → Denied | H S | 3 | ✅ |
| D-PIN-06 | PIN dialog choices | the code window has *Hide apps…* and *Deny*; Deny → Denied | H S | 3 | ✅ |
| D-NONE-01 | None refused by default | None not in Hello modes; Start with None → refused, 2100 | H E | 1 | ✅ |
| D-NONE-02 | None with policy only | `AllowedConsentModes` incl. None but no ConsentBypass right → still refused | PS H | 1 | ✅ |
| D-NONE-03 | None with policy + right | both set → connected without a dialog, banner shown | PS H S | 1 | ✅ |
| D-NONE-04 | Own session | PSAdmin connects to PSAdmin's own session with None → no question, event 1100 "consent None" (1102 is only written when the own account signs in during a running session) | H S | 2 | ✅ |
| D-NONE-05 | Own session with Ask / Pin | the own session started with Ask or Pin → the dialog is shown all the same | H S | 3 | ✅ |
| D-MODES-01 | Policy as REG_SZ list / invalid list | `"Pin;Ask"` works; `"Foo"` → defaults Ask+Pin | PS H | 3 | ✅ |
| D-MODES-02 | Only Pin allowed | Ask refused; Pin works | PS H | 2 | ✅ |
| D-MODES-03 | No mode left | `ConsentModes` allowing nothing usable → Hello consentModes [], Failed "No way to connect is allowed on this device (policy).", 2100 | PS H E | 2 | ✅ |
| D-MODES-04 | Mode on a locked / sign-in session | a mode the policy doesn't allow isn't refused there: it runs as None, the follow-up mode applies when someone unlocks or signs in | PS H | 3 | ✅ |

### 9.3 Banner and user at the device

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-BAN-01 | Banner shown | topmost "Your screen is being viewed and controlled by PSAdmin" with Hide screen / Hide apps… / Disconnect (read by OCR), on the console screenshot; not in a session automatic sign-in created (D-BAN-10) | S C | 1 | ✅ | |
| D-BAN-02 | Banner not in the picture | decoded frame has no banner (build ≥ 19041) | H C | 2 | ✅ | |
| D-BAN-03 | Banner in the picture on 1809 | on W10-1809 the banner **is** in the frame, not black | H | 2 | ✅ | 1809 |
| D-BAN-04 | Collapse to pill | after 8 s the banner is the small pill "Controlled by / Viewed by <account>"; hover expands it | S | 3 | ✅ | |
| D-BAN-05 | Disconnect by the user | the user opens the banner (hover) and clicks Disconnect (console mouse) → Ended "The user at the device ended the session.", resume token void | H S | 1 | ✅ | |
| D-BAN-06 | Can't be closed otherwise | Alt+F4 / taskkill by the user (no admin) → banner stays | S | 2 | ✅ | |
| D-BAN-07 | View-only text | "viewed by" without "controlled" | S | 3 | ✅ | |
| D-BAN-08 | Never collapsed while hiding | something hidden → the banner stays open with " · mouse and keyboard blocked" and 'Click "Disconnect" to end' | S C | 3 | ✅ | |
| D-BAN-09 | Clicks pass through | the admin's clicks on the banner's area reach the window below (`WS_EX_TRANSPARENT`) | H S | 3 | ✅ | |
| D-BAN-10 | No banner after automatic sign-in | a session automatic sign-in created shows only the cover, no banner | H C | 1 | ✅ | |
| D-BAN-11 | Click-through kept against programmatic cursor moves | while a program in the user's session moves the cursor itself (SetCursorPos to the admin's spot, SendInput moves – as a full-screen RDP / Hyper-V client does), the banner stays `WS_EX_TRANSPARENT` and the admin's click on its area reaches the window below; the console mouse (real input) still makes it clickable again | H S C | 2 | ✅ | |

### 9.4 View-only and rights

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-VIEW-01 | View-only by choice | mouse, keys, Ctrl+Alt+Del, clipboard dropped (marker app sees nothing); fit disabled | H S | 1 | ✅ |
| D-VIEW-02 | View-only by right | account only in `Rights\DesktopViewOnly` (`viewer`, created by the test) → Hello control=false, forced view-only even if Start asks for control | PS H S | 1 | ✅ |
| D-VIEW-03 | Key release still passes | a key held down when input stops being allowed (secure desktop without the SecureDesktop right; view-only itself is fixed at Start) is released – no stuck key | H S | 3 | ✅ |
| D-RIGHT-01 | No SecureDesktop right on the lock screen | Start on a locked session → Failed, 2100 | PS H E | 1 | ✅ |
| D-RIGHT-02 | No SecureDesktop right during UAC | UAC opens while connected → no frames sent, input blocked (prompt still open after Enter) | PS H S | 1 | ✅ |

### 9.5 Secure desktop, sign-in, lock, user switch

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-LOCK-01 | Connect to a locked session | lock screen in the picture without a question | H S | 1 | ✅ |
| D-LOCK-02 | User unlocks while connected | picture holds at the desktop (`UserDesktop`), user asked (Ask), Allow → continues, event 1102 | H S C E | 1 | ✅ |
| D-LOCK-03 | User unlocks and refuses | Denied, event 2101, session ends | H S C E | 1 | ✅ |
| D-LOCK-04 | Lock while connected (Win+L by the user) | connection stays, the switch passes through the secure desktop, the lock screen (not the user's desktop) shown | H S | 2 | ✅ |
| D-LOCK-05 | Win11 lock screen on the Default desktop | held while waiting for Ctrl+Alt+Del; no user desktop leaked before consent | H C | 1 | ✅ |
| D-SIGNIN-01 | Nobody signed in | sign-in screen shown | H C | 1 | ✅ |
| D-SIGNIN-02 | Admin signs in through the picture | Ctrl+Alt+Del button + typed password through the desktop session → signed in, no consent for own account | H | 1 | ✅ |
| D-SIGNIN-03 | Someone else signs in at the console | console keyboard signs in normaluser → helper follows into the new session, normaluser asked, Allow → continues | H C S | 1 | ✅ |
| D-SIGNIN-04 | Sign out while connected | follows to the sign-in screen | H S | 2 | ✅ |
| D-SIGNIN-05 | Own account by SID | the account signing in at the device is recognised as the admin's own by its SID (not the name) → continued without asking | H S | 3 | ✅ |
| D-UAC-01 | UAC with SecureDesktop | `Start-Process cmd -Verb RunAs` in normaluser's session → prompt in the picture (GDI), usable: the password of the administrator the prompt offers (Admin) typed through the desktop session elevates | H S | 1 | ✅ |
| D-UAC-02 | UAC prompt not shown without the SecureDesktop right | as D-RIGHT-02 (the same run): no picture of the prompt, Enter doesn't reach it; view-only with the right does see it | PS H S | 1 | ✅ |
| D-FUS-01 | Fast user switching | Switch user to Admin while connected → follows the console, Admin asked | H S C | 2 | ✅ |
| D-FUS-02 | Different user = new consent after helper restart | consent from normaluser does not carry over to Admin | H S | 1 | ✅ |
| D-DISC-01 | Session disconnected while connected | `tsdiscon` of the console → Status Paused, then the service follows the console; "has a screen again" only for RDP sessions | H PS | 2 | ✅ |
| D-RDPSES-01 | Connect to a user's RDP session | **2VM** or host mstsc: RDP session listed with client name, connect works | H | 2 | ✅ |

### 9.6 Ctrl+Alt+Del

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-SAS-01 | Console session | button → security screen in the picture (LogonUI), log line with SAS value 1 | H E | 1 | ✅ |
| D-SAS-02 | RDP session | SAS goes to **that** session, not the console | H | 2 | ✅ |
| D-SAS-03 | Without SecureDesktop right | "Your account may not use the sign-in screen on this device." | PS H | 2 | ✅ |
| D-SAS-04 | Policy 0 | SoftwareSASGeneration 0 or 2 → "turned off by a policy on this device", nothing happens (only 1 and 3 work) | PS H | 2 | ✅ |
| D-SAS-05 | Policy 3 (services + ease of access) | works | PS H | 3 | ✅ |
| D-SAS-06 | **On the lock screen** | session locked (normaluser) → Ctrl+Alt+Del through the desktop session shows the password field; password typed through the desktop session unlocks; picture then waits for normaluser's consent (D-LOCK-02) | H S C | 1 | ✅ |
| D-SAS-07 | On the sign-in screen (nobody signed in) | Ctrl+Alt+Del → credential field; sign in as PSAdmin through the desktop session → desktop, no consent for the own account | H C | 1 | ✅ |
| D-SAS-08 | On the desktop | security options screen (Lock, Switch user, Sign out, Task Manager) in the picture; Esc returns to the desktop | H S | 1 | ✅ |
| D-SAS-09 | Lock through Ctrl+Alt+Del | choose *Lock* by mouse → session locked, connection stays, lock screen shown | H S | 2 | ✅ |
| D-SAS-10 | Task Manager through Ctrl+Alt+Del | choose *Task Manager* → Taskmgr opens in the session (process check) | H PS | 3 | ✅ |
| D-SAS-11 | Repeated presses | 10 × Ctrl+Alt+Del / Esc → every press works, each logged | H E | 3 | ✅ |
| D-SAS-12 | Screen hidden by the user | Ctrl+Alt+Del while the user hides the screen → "The user at the device has hidden their screen – Ctrl+Alt+Del isn't sent until they show it again." | H S | 3 | ✅ |

### 9.7 Input

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-IN-01 | Keyboard by scan code | text typed into a marker app arrives as on the session's keyboard layout – US and German layouts have an expectation, chosen by the layout, not the Windows language (an en-US VM with a German keyboard types `"§&/()=?` for `@#^&*()_`); the marker is clicked first so it has the keyboard focus (also for D-IN-02, 05, 06) | H S | 1 | ✅ | |
| D-IN-02 | Extended keys | arrows, Home/End, Del, right Ctrl, numpad Enter | H S | 2 | ✅ | |
| D-IN-03 | Mouse | move, left/right/middle/X1/X2 down/up, wheel, horizontal wheel reach the marker app at the right coordinates (±2 px); wheel turns a second apart (Windows merges turns still queued into one with the sum) | H S | 1 | ✅ | |
| D-IN-04 | Win key combos | Win+R, Alt+Tab sent through the protocol work on the device | H S | 2 | ✅ | |
| D-IN-05 | No stuck keys | connection dropped while a key/button is down → released (ReleaseAll) | H S N | 2 | ✅ | |
| D-IN-06 | Input flood | 10,000 mouse moves → no crash, keys never lost | H S | 3 | ✅ | |
| D-IN-07 | DPI 150 % | coordinates still exact on a scaled desktop | H S | 2 | ✅ | |
| D-IN-08 | No input into hidden apps | mouse down / wheel over a hidden app and key-down while it has the focus are dropped | H S | 2 | ✅ | |
| D-IN-09 | Input stops when the user hides | the user hides the screen → held keys released, no input passes | H S | 2 | ✅ | |

### 9.8 Picture, encoder, resolution, monitors

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-VID-01 | Presets | 15 / 30 / 60 fps requested → Stats fps ≤ requested, quality default 60 | H | 2 | ✅ | |
| D-VID-02 | Policy limits | `MaxFramesPerSecond=10`, `MaxQuality=40` → clamped in Start and Settings | PS H | 2 | ✅ | |
| D-VID-03 | Changes only | still screen → (almost) no frames after the refinement frames; a window changing colour all the time → frames (pointer moves alone change no frame) | H S | 3 | ✅ | |
| D-VID-04 | Key frame request | KeyFrameRequest → next frame is a key frame | H | 3 | ✅ | |
| D-VID-05 | Slow network | throttled link (VM bandwidth limit 2 Mbit/s) and a window of random pixels changing all the time → quality steps down, ≤ 2 frames in flight, still usable | H N | 2 | ✅ | |
| D-VID-06 | Capture method | Stats show Desktop Duplication or GDI; secure desktop always GDI | H | 3 | ✅ | |
| D-VID-07 | No GPU encoder on a VM | 60 fps → software encoder, no error | H | 2 | ✅ | |
| D-VID-08 | GPU encoder | "Smooth" on a GPU-P VM → "GPU encoder" in Stats; forced failure falls back to software with a key frame | H | 2 | ✅ | GPU-P |
| D-VID-09 | N edition | no H.264 → clear "Media Feature Pack" error, event 3100, no crash | H E | 2 | ✅ | N |
| D-VID-10 | Dark display | monitor off (power plan 1 min) → agent wakes it, picture within ~6 s | H PS | 2 | ✅ | |
| D-VID-11 | RDP windows keep drawing | while connected `RemoteDesktop_SuppressWhenMinimized` = 2, restored afterwards (`rdp-minimized-<sid>.txt`), log line | H PS | 3 | ✅ | |
| D-VID-12 | GPU picture stands still | Desktop Duplication frozen while the screen changes → GDI for the rest of the connection, log "… using GDI for this connection" | H PS | 3 | ✅ | |
| D-VID-13 | Display kept on | the display doesn't turn off while connected | H PS | 3 | ✅ | |
| D-VID-14 | Frame rate on a slow link | the D-VID-05 session (2 Mbit/s, random pixels, 30 fps and quality 80 asked): Stats carry `frameRateLimit` within the 30 s; every stat with a limit has quality ≤ 50 (the readable level comes first) and a limit between 5 and 29 fps | H N | 2 | ✅ |
| D-FIT-01 | Fit to window | Settings Fit 1600×900 → guest resolution 1600×900 (or nearest listed), restored on disconnect | H PS | 2 | ✅ | |
| D-FIT-02 | Unsupported size | 1234×777 → same shape smaller or nearest mode, Stats.fit says which | H | 3 | ✅ | |
| D-FIT-03 | Restore after a crash | kill the helper → resolution restored, then fitted again by the restarted helper | H PS | 2 | ✅ | |
| D-FIT-04 | Fit bounds | below 320×200 ignored, capped at 7680×4320 | H | 3 | ✅ | |
| D-MON-01 | Two monitors | virtualDisplay `always` + Hyper-V display → Layout lists 2, primary first; switching monitor changes the picture | H PS | 2 | ✅ | |
| D-MON-02 | All monitors | monitor −1 → bounding box picture; mouse lands on the right monitor | H S | 2 | ✅ | |
| D-MON-03 | Monitor added / removed while connected | Layout resent, index clamped, no crash | H PS | 3 | ✅ | |
| D-VD-01 | Virtual display auto | `auto`, the guest's display offers less than the window (Hyper-V video limited to 1024×768) → virtual monitor switched on at the window size, primary; off after disconnect – the device is read once the agent has logged what it did at the end (within 120 s: switching it off can take tries), and the failure says how long that took | H PS | 2 | ✅ | |
| D-VD-02 | Virtual display off | `off` → never switched on | H PS | 3 | ✅ | |
| D-VD-03 | Two connections | second connection keeps the virtual display until the last one ends (reference count; read after the agent's "stays on for 1 other connection(s)" and, at the last end, its switch-off line) | H PS | 3 | ✅ | |
| D-VD-04 | Resize | Fit changes → driver reloaded through its pipe, new size within 5 s | H PS | 3 | ✅ | |
| D-VD-05 | Off after a service restart | the virtual display, on while connected, is switched off when the service starts again | H PS | 3 | ✅ | |

### 9.9 Clipboard

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-CLIP-01 | Text to the device | Clipboard message → normaluser's clipboard has it (read in session) | H S | 1 | ✅ |
| D-CLIP-02 | Text from the device | copy in session → Clipboard message arrives; Unicode, 1,000,000 chars | H S | 1 | ✅ |
| D-CLIP-03 | No echo | text set by the agent isn't sent back | H S | 3 | ✅ |
| D-CLIP-04 | Image both ways | PNG and BMP → device has PNG + CF_DIB; from the device offered, and on request arrives as PNG when the copying program put PNG, else as BMP | H S | 2 | ✅ |
| D-CLIP-05 | Files to the device | 3 files + a folder → pasted in Explorer in normaluser's session land in `%LOCALAPPDATA%\Temp\RemoteHub Clipboard\<stamp>`, owned by normaluser, hashes equal; TransferEnd receipt | H S | 1 | ✅ |
| D-CLIP-06 | Files from the device | offer has names/sizes; contents sent only on TransferRequest; hashes equal | H S | 1 | ✅ |
| D-CLIP-07 | Paste on demand | nothing transferred until something pastes; rdpclip / clipboard history get nothing | H S | 2 | ✅ |
| D-CLIP-08 | Limits | 101 MB or 1001 items → to the device "Too much at once", from it "Too much to copy through the remote desktop … use the file transfer instead."; nothing written | H S | 1 | ✅ |
| D-CLIP-09 | Path escape | items named `..\..\evil.txt`, `C:\x`, `CON`, absolute paths → refused / sanitised; nothing outside the clipboard folder | H S | 1 | ✅ |
| D-CLIP-10 | Reparse points | junction / symlink in a copied folder on the device → skipped | H S | 2 | ✅ |
| D-CLIP-11 | Out of order / incomplete | TransferData out of order or short → "incomplete", partial files removed | H | 1 | ✅ |
| D-CLIP-12 | Files read with the user's rights | file only readable by Admin, copied in normaluser's session → refused, the agent's SYSTEM rights never used | H S | 1 | ✅ |
| D-CLIP-13 | Nobody signed in | files to the device on the sign-in screen → "Nobody is signed in on the device." | H | 2 | ✅ |
| D-CLIP-14 | Policy levels | `DesktopClipboard` 0/1/2/3 → only the allowed kinds pass, refusal texts as in the code | PS H S | 1 | ✅ |
| D-CLIP-15 | View-only has no clipboard | level 0 in view-only | H S | 1 | ✅ |
| D-CLIP-16 | Newer copy cancels | second copy while the first transfers → first cancelled "A newer copy replaced it." | H S | 3 | ✅ |
| D-CLIP-17 | Abort leaves nothing | connection cut during a file copy → no partial files on either side | H S N | 1 | ✅ |
| D-CLIP-18 | Old folders purged | clipboard folders older than today removed at next transfer | H PS | 3 | ✅ |
| D-CLIP-19 | Newer copy cancels the first | a second copy to the device while the first is still offered replaces it silently | H S | 3 | ✅ |
| D-CLIP-20 | Long text | text over 1,000,000 characters truncated silently | H S | 3 | ✅ |
| D-CLIP-21 | Too many files | 1001 single files → only 1000 offered, no message | H S | 3 | ✅ |
| D-CLIP-22 | Bad image | a broken BMP to the device: receipt ok, the device's clipboard unchanged | H S | 3 | ✅ |
| D-DROP-01 | Files dropped on the tab | a file and a folder (`dropFiles`) → saved in normaluser's `Desktop\From RemoteHub` with the same SHA-256, owner normaluser (written as the user); receipt folder "From RemoteHub"; event 1108 "saved 2 file(s)" without file names | H PS E | 2 | ✅ |
| D-DROP-02 | Same names again | the same file dropped again → a folder `From RemoteHub\<yyyy-MM-dd HH.mm.ss>`; the first copy unchanged | H PS | 3 | ✅ |
| D-DROP-03 | Junction in the folder's place | normaluser replaces `From RemoteHub` by a junction to `C:\Windows\System32\drivers\etc` → the next drop fails, nothing written there | H PS | 1 | ✅ |
| D-DROP-04 | Policy | `DesktopClipboard` = 2 → a drop is refused with "Copying files isn't allowed on this device." | H PS | 2 | ✅ |

### 9.10 Idle, reconnect, robustness

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-IDLE-01 | Idle timeout | `DesktopIdleTimeoutMinutes=1` → warning at ~30 s, Ended at 60 s, event 2102 | PS H E | 2 | ✅ |
| D-IDLE-02 | Input resets | a key every 20 s → never ends | PS H | 3 | ✅ |
| D-IDLE-03 | View-only counts | view-only session also ends | PS H | 3 | ✅ |
| D-IDLE-04 | Clipboard counts as activity | clipboard and transfers reset the idle time | PS H | 3 | ✅ |
| D-RES-01 | Resume after a drop | cut the network 10 s, resume with the token → no new consent, event 1103 | H N S E | 1 | ✅ |
| D-RES-02 | Token single use | second resume with the same token → normal consent | H | 1 | ✅ |
| D-RES-03 | Token from another account | presented by `lapsadmin` → ignored, still valid for PSAdmin | H | 1 | ✅ |
| D-RES-04 | Token after 1 min | expired → consent again (the window is 1 min; 2 min for a session automatic sign-in created) | H N | 2 | ✅ |
| D-RES-05 | Token void after deliberate end | after Ended by RemoteHub / user / idle / refusal → consent again | H S | 1 | ✅ |
| D-RES-06 | Different user signed in meanwhile | resume → asked again | H S C | 1 | ✅ |
| D-RES-07 | Old connection still alive | resume while the old connection looks alive → the old one is closed by the agent, the new one gets 1103; the old one gets no Status and no 1101 | H | 3 | ✅ |
| D-RES-08 | Token void after an invalid message | a protocol error ends the session and its resume token | H | 3 | ✅ |
| D-RES-09 | No token after a service restart | a resume after a service restart → consent again | H PS | 3 | ✅ |
| D-RES-10 | The agent notices the drop first | network cut ~75 s (beyond the 30 s keep-alive), resumed within 1 min → 1101 "(connection lost …)", 1103, no new question. Fails today: the end is taken as "closed by RemoteHub" and the token revoked (`DesktopEndpoint.cs:220-230`) | H N PS E | 1 | ✅ |
| D-ROB-01 | Helper killed | `taskkill` the helper → restarted, "screen changed … reconnecting", no new consent for the same user | H PS | 2 | ✅ |
| D-ROB-02 | Helper keeps failing | 6 kills within a minute → Ended "kept failing" | H PS | 3 | ✅ |
| D-ROB-03 | Service restart while connected | session ends cleanly, no orphan helper, display restored | H PS | 2 | ✅ |
| D-ROB-04 | 8 h soak | one session for 8 h with input every minute → no leak (agent working set growth < 50 MB), still connected | H PS | 3 | ✅ |
| D-ROB-05 | Fuzzed messages | random/invalid type bytes and JSON → session ends, agent healthy, no 3100 crash loop | H | 2 | ✅ |
| D-ROB-06 | Ping | Ping answered with the same 8 bytes | H | 3 | ✅ |
| D-ROB-07 | Unrequested sign-in request | "invalid message from RemoteHub: A sign-in request the agent didn't ask for." / "Sign-in request too large." | H E | 3 | ✅ |
| D-ROB-08 | Message over 16 MiB | 1101 "(invalid message from RemoteHub: Message too large.)" | H E | 3 | ✅ |

### 9.11 Audit

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-AUD-01 | Event IDs | across the scenarios: 1000, 1001, 1100–1105, 2000, 2100–2103 always; with automatic sign-in 1106, 1107, 2104, 2105; with jump hosts 1200–1202, 2200; the errors 3100–3102 only where forced – each with account, address and session | E | 1 | ✅ |
| D-AUD-02 | No content | no screen content, keystrokes or clipboard text in log or events (search for marker strings typed and copied) | E | 1 | ✅ |

### 9.11a Privacy screen, hidden apps, user privacy

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-PRIV-01 | Privacy screen | right PrivacyScreen, policy `PrivacyScreen` 0 / 1 / 2 → Hello `privacyScreen` / `privacyInputBlock` and feature `privacyScreen` match; turned on → console screenshot black while frames still arrive, event 1104; refused → 2103 | PS H C E | 1 | ✅ | |
| D-PRIV-02 | Local input blocked | level 2: the device's own keyboard and mouse don't reach the session (marker app); level 1: they do, and a request with blocking is refused with 2103 "Blocking the mouse and keyboard at the device isn't allowed on this device (policy)." | H S C | 1 | ✅ | |
| D-PRIV-03 | Not available before 2004 | on W10-1809 no `privacyScreen` feature, a request refused with 2103 | H E | 2 | ✅ | 1809 |
| D-PRIV-04 | Kept after a resume | privacy screen still on after D-RES-01 | H N | 2 | ✅ | |
| D-PRIV-05 | Snips and switching blocked | while any privacy screen is up Print Screen, Win+Shift+S, Alt+Tab and Win+Tab are dropped at the device and from the admin; Win+S and Shift+Tab work; switching works again when it is off | H S C | 1 | ✅ | |
| D-PRIV-06 | Snipping tools closed | SnippingTool / ScreenClippingHost / ScreenSketch closed when they show a window; agent log "… was closed – a snip would show the screen at the device", no event | H S PS | 2 | ✅ | |
| D-PRIV-07 | View-only has none | a privacy screen request on a view-only connection is ignored | H | 3 | ✅ | |
| D-PRIV-08 | Monitors change | monitors change while it is on → overlays rebuilt, or 2103 "… turned off because the device's monitors changed" | H PS E | 3 | ✅ | |
| D-HIDE-01 | Hidden apps by policy | `HiddenApps` (full path and `aumid:`) → those windows never in the frames, whatever the user chooses | PS H S | 1 | ✅ | |
| D-HIDE-02 | The user hides | the user at the device hides the screen or chosen apps ("Allow and hide apps…") → event 1105 and a Privacy message; kept after a resume without a new question; cleared when another user signs in | H S E | 1 | ✅ | |
| D-HIDE-03 | Policy apps aren't the user's | today policy `HiddenApps` count as user-hidden: the first Privacy message has hiddenApps=1, the banner "1 app hidden", 1105 "the user … hides 1 app(s)" – product finding | PS H S E | 3 | ✅ | |

### 9.11b Shell surfaces and the Ctrl+Alt+Del screen over the privacy screen

The helper's token gets UIAccess (policy `HelperUIAccess`, DWORD, on unless 0), so the overlays sit above Start, Search and the other shell surfaces ([privacy-mode-design.md](privacy-mode-design.md) §5.8, `lab/tests/D-SHELL.Tests.ps1`). While local input is blocked, the Ctrl+Alt+Del screen offers only Task Manager and Cancel, and its other actions come back afterwards (§5.9, `lab/tests/D-CADHIDE.Tests.ps1`; the MSI must be built with the credential provider). Both ran on W11 26H2 and Server 2025.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-SHELL-01 | Helper token UIAccess switch | helper token TokenUIAccess 1 by default, 0 with `HelperUIAccess`=0 | PS H | 2 | ✅ |
| D-SHELL-02 | Shell surfaces stay covered | with UIAccess and `PrivacyScreen`=2: Start, Search, Quick Settings, Notification Center, a toast, a taskbar thumbnail → the console below the banner strip < 0.5 % bright (Alt+Tab / Task View are dropped by SnipGuard, D-PRIV-05) | H C S | 1 | ✅ |
| D-SHELL-03 | Overlays still excluded from capture | overlays keep `WDA_EXCLUDEFROMCAPTURE` (0x11) with UIAccess | H S | 1 | ✅ |
| D-SHELL-04 | Banner above the overlays | the banner's z-order above the overlays | S | 2 | ✅ |
| D-SHELL-05 | Disconnect still works | the device's user ends the session with *Disconnect* through the input block → Ended "The user at the device ended the session." | H S | 1 | ✅ |
| D-SHELL-06 | The admin still sees them | with UIAccess: Start, Search, Quick Settings, Notification Center, the thumbnail and the toast are in the admin's picture (> 0.5 % of the pixels changed) | H | 1 | ✅ |
| D-CADHIDE-01 | Control | before any connection: none of the values set; Ctrl+Alt+Del at the console offers Lock and Task Manager | PS C | 2 | ✅ |
| D-CADHIDE-02 | Hidden while input is blocked | privacy screen with input blocked: `DisableLockWorkstation`, `DisableChangePassword`, `NoLogoff`, `NoClose` (session user) and `HideFastUserSwitching` (machine) = 1, `DisableTaskMgr` not set; the Ctrl+Alt+Del screen offers only Task Manager | PS C | 1 | ✅ |
| D-CADHIDE-03 | Unblocked while connected | input unblocked → put back; blocked again → hidden again | PS | 1 | ✅ |
| D-CADHIDE-04 | Put back at the end | the connection ends → the values as before, no `sas-actions-*.txt` left | PS | 1 | ✅ |
| D-CADHIDE-05 | Put back after a crash | service and helper killed while hidden → still hidden; the service's start puts them back, log "actions an earlier helper hid are back" | PS H | 1 | ✅ |
| D-CADHIDE-06 | Automatic sign-in | in the admin's new session (input always blocked) → hidden, Ctrl+Alt+Del offers only Task Manager; put back when the connection ends on purpose (the harness closes it) | PS C H | 1 | ✅ |

### 9.12 Automatic sign-in – credential provider (C-PROV)

Phase 1 of [credential-provider-design.md](credential-provider-design.md) §13: the provider itself. The agent's broker owns the pipe now, so the Lab DLL run (C-PROV-03) stops the service while the test server has it. The agent MSI is built with `build-installer.ps1 -LabSigning -CredentialProvider`, and the Lab DLL and test server with `build-credential-provider.ps1 -Configuration Lab`. The full list for the finished feature is in the design's §14.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-PROV-01 | Install with `AUTOSIGNIN=1` | msiexec exit 0/3010; `RemoteHubCredentialProvider.dll` in Program Files, signature valid (lab certificate trusted on the VM); both keys for CLSID `{718FFFD2-…}`, `ThreadingModel` Apartment | PS | 1 | ✅ |
| C-PROV-02 | Inert without a ticket | with the provider registered and no sign-in pending, signing out and in again with Ctrl+Alt+Del and the password reaches the desktop | C PS | 1 | ✅ |
| C-PROV-03 | Sign-in through the provider | Lab DLL + `RemoteHubCpTestServer.exe` (SYSTEM): client LogonUI accepted, `Result` status 0, the console account's desktop, the ticket file deleted | C PS | 1 | ✅ |
| C-PROV-04 | Update keeps it | a rebuild of the same version (new ProductCode, a major upgrade) without `AUTOSIGNIN` → DLL and keys still there | PS | 1 | ✅ |
| C-PROV-05 | `AUTOSIGNIN=0` removes it | DLL and both keys gone; `agent.json` `noUserBehavior` = SignInScreen (turned off – not set would mean on) and `autoSignInGroups` cleared, the policy key stays empty | PS | 1 | ✅ |
| C-PROV-06 | RemoteHub's *Install credential provider* | `AgentInstaller.ChangeCredentialProvider` over the agent → installed, DLL signed, both keys; `agent.json` `noUserBehavior` = AutoSignIn and `autoSignInGroups` = S-1-5-32-544, the policy key not written (since 0.18.12); message "Automatic sign-in allowed on this device" | H PS | 1 | ✅ |
| C-PROV-07 | RemoteHub's *Remove credential provider* | removed, DLL and keys gone, `agent.json` `noUserBehavior` = SignInScreen again, the policy key still empty; the agent's service process is the same as before both actions and health answers with the same certificate | H PS | 1 | ✅ |
| C-PROV-08 | Another build on the device | install refused with "a different build … Update the agent first", nothing installed | H PS | 1 | ✅ |
| C-PROV-09 | No agent / restart needed | `ChangeCredentialProvider` without an installed agent → "isn't installed"; a 3010 exit → the "after the device restarts" message | H PS | 3 | ✅ |

Phases 2 and 3 (lab/tests/C-AUTO.Tests.ps1): the agent service, the provider and RemoteHub's desktop client together, signed in to the agent as the console account.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-AUTO-01 | Turned off by IT | installed with `AUTOSIGNIN=1` (`agent.json` `noUserBehavior` = AutoSignIn, `autoSignInGroups` = Administrators; policy key and marker not written – since 0.18.12), then `NoUserBehavior=SignInScreen` set as IT would → `Rights.AutoSignIn` false; a request → SignInFailed "turned off". The value deleted again ("Not configured") → the install's choice applies again (the rest of C-AUTO signs in on it) | H | 1 | ✅ |
| C-AUTO-02 | View-only | policy and right set, view-only start → SignInFailed, Windows never asked | H | 1 | ✅ |
| C-AUTO-03 | No password sent | SignInReady, nothing sent for 30 s → SignInFailed "didn't send the sign-in in time" | H | 2 | ✅ |
| C-AUTO-04 | Wrong password | SigningIn → SignInFailed "password was wrong"; no desktop; event 2105 | H PS E | 1 | ✅ |
| C-AUTO-05 | Right password | after closing the failed attempt's message: SignInReady → SignedIn; the console account's desktop; event 1106, no 3101/3102 | H C PS E | 1 | ✅ |
| C-AUTO-06 | No password in logs | the lab password in neither the agent's log files nor the Application log | PS E | 1 | ✅ |

Phase 4 – after the sign-in (lab/tests/C-END.Tests.ps1):

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-END-01 | Covered from the start | after SignedIn: a screenshot of the console shows the privacy screen message; keys typed at the console don't reach the desktop; RemoteHub's picture shows the desktop (keys are counted by the marker window, which must have the keyboard – `Start-LabMarker` waits for it and otherwise names the window that kept it) | H C PS | 1 | ✅ |
| C-END-02 | Lock on disconnect | connection closed → the session is disconnected from the console (`WTSDisconnectSession`, on client and Server alike), its programs keep running; event 1107 "… locked (closed by RemoteHub)" | H PS E | 1 | ✅ |
| C-END-03 | Sign out on disconnect | end action SignOut (connection or policy) → the session is gone; event 1107 "signed out" | H PS E | 1 | ✅ |
| C-END-04 | Policy wins | policy `AutoSignInEndAction = Lock`, RemoteHub sends SignOut → locked | H PS | 2 | ✅ |
| C-END-05 | Network drop and resume | the VM's adapter cut for 30 s → the overlay stays, the desktop is untouched; reconnected within 2 min → same session, no sign-out | H N C | 1 | ✅ |
| C-END-06 | Network drop without resume | adapter cut for 3 min → signed out after 2 min; event 1107. Fails today when the agent notices the cut first (D-RES-10) | H N PS E | 1 | ✅ |
| C-END-07 | Agent restart | service killed while connected → at the next start the session gets its end action (signed out when it was dropped). The "dropped" half fails today when the agent takes the drop for a close (D-RES-10) | H PS E | 1 | ✅ |
| C-END-08 | Cover ends after a drop | a session automatic sign-in created has no banner (since 0.18.10), only the cover; the cover's helper ending during the 2 minutes → signed out at once, event 1107 "… (the screen cover ended after the connection dropped)". Fails today when the agent takes the drop for a close (D-RES-10) | H N C | 2 | ✅ |
| C-END-09 | Input blocked at level 1 too | `PrivacyScreen`=1 → after SignedIn the console's own keys still don't reach the session | H C PS | 1 | ✅ |
| C-END-10 | No privacy screen available | `PrivacyScreen`=0 (`AutoSignInRequirePrivacyScreen` 0) → signed in, 1106 ", no privacy screen on this device", the console shows the desktop, no banner | H C E | 2 | ✅ |
| C-END-11 | Ctrl+Alt+Del at the device during the hold | after a drop Ctrl+Alt+Del at the console → the screen offers only Task Manager and Cancel (no Lock, privacy-mode-design §5.9); not signed out at once; signed out when the 2 min run out | H N C E | 3 | ✅ |
| C-END-12 | Resume brings the cover back | an operator without `Rights\PrivacyScreen` resumes → the service's privacy screen is back. Fails today when the agent takes the drop for a close (D-RES-10) | H N C | 2 | ✅ |
| C-END-13 | Failed sign-in removes the cover | wrong password → the service's privacy screen dropped, a later manual sign-in isn't covered | H C | 3 | ✅ |
| C-END-14 | Who turned the cover on | 1104 for the service's cover reads "the service (automatic sign-in of <admin> from <source>) turned the privacy screen on …", never "<admin> from <source> turned …" – the admin didn't ask for it | E | 3 | ✅ |
| C-END-15 | The agent notices the drop first | cut ≥ 75 s, resumed within 2 min → same session, no lock; fails today (D-RES-10) | H N PS E | 1 | ✅ |

More refusals and limits (lab/tests/C-AUTO2.Tests.ps1), decided by the agent before Windows is asked:

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-AUTO-07 | On without the option, off with `AUTOSIGNIN=0` | automatic sign-in is on by default: installed without `AUTOSIGNIN` → provider DLL and keys installed, no `noUserBehavior` / `autoSignInGroups` in `agent.json`, no `NoUserBehavior` policy value, Hello `autoSignIn` true with the `autoSignIn` feature, a request signs the console account in; installed with `AUTOSIGNIN=0` → no provider, `agent.json` `noUserBehavior` = SignInScreen, Hello `autoSignIn` false, no `autoSignIn` feature, a request → "turned off on this device (policy NoUserBehavior)" | H PS | 1 | ✅ |
| C-AUTO-08 | Rights | `Rights\AutoSignIn` without the account → "may not sign in automatically on this device (right AutoSignIn)"; without SecureDesktop the Start on the sign-in screen is refused first: Failed "Nobody is signed in or the session is locked – and your account may not see the sign-in screen on this device.", 2100, Hello `autoSignIn` false, log "Desktop: automatic sign-in not offered to <acct>: … needs the rights Desktop and SecureDesktop …" | PS H | 1 | ✅ |
| C-AUTO-09 | Privacy screen required | `AutoSignInRequirePrivacyScreen=1` with `PrivacyScreen=0` → refused, the policy named | PS H | 2 | ✅ |
| C-AUTO-10 | Session state | someone signed in on the console → "Someone is signed in on the console now" (the account's own disconnected session is reconnected since 0.22 – C-UNLOCK-05; one in use elsewhere is refused "already has a session on the device", covered by the unit tests) | H S | 1 | ✅ |
| C-AUTO-11 | Rate limit | the 4th automatic sign-in of an account within 15 minutes → "Too many automatic sign-ins", event 2104 | H E | 1 | ✅ |
| C-AUTO-12 | Provider unusable | `ExcludedCredentialProviders` lists the CLSID / InprocServer32 outside the agent folder / DLL not signed → Hello `autoSignIn` false, refused with that reason | PS H | 1 | ✅ |
| C-AUTO-13 | IT's policy kept | `NoUserBehavior=SignInScreen` set before an `AUTOSIGNIN=1` install → left as it is; the install records its own choice in `agent.json` and the log says the policy decides ("… it decides, so automatic sign-in stays off"); still there after the uninstall | PS | 1 | ✅ |
| C-AUTO-14 | Sign-in not confirmed | the console's new session has another SID → signed out, 3101 "… the console session N has user <sid>, not <acct> … – signed out", Ended "The automatic sign-in couldn't be confirmed, so it was undone …" (needs a provider test build) | H PS E | 2 | ✅ |
| C-AUTO-15 | Console changed before the ticket | someone signs in at the console between SignInReady and the ticket → SignInFailed "Someone is signed in on the console now …", 2104 SessionChanged, no ticket sent | H C E | 2 | ✅ |
| C-AUTO-16 | Provider never loads | LogonUI doesn't load the provider → SignInFailed "The device's sign-in screen didn't load the RemoteHub credential provider." (2104 NoProvider); result timeout → "The sign-in screen didn't take the sign-in. …" | H PS E | 2 | ✅ |
| C-AUTO-17 | Second sign-in at once | two connections ask at the same time → the second gets "Another automatic sign-in is running on this device." | H | 3 | ✅ |
| C-AUTO-18 | Pipe squatted | `\\.\pipe\RemoteHubAgent.AutoSignIn` created by another process before the service → 3102 "Automatic sign-in unavailable: the pipe … couldn't be created (…)", Hello `autoSignIn` false | PS H E | 2 | ✅ |
| C-AUTO-19 | Foreign pipe client refused | a client that isn't LogonUI / SYSTEM / the console connects → log "Automatic sign-in: pipe client refused (<why>)", a later automatic sign-in still works; an RDP session's LogonUI on Server refused ("session N isn't the console") | PS | 2 | ✅ |
| C-AUTO-20 | Windows refusals | account locked / disabled / expired password / "Deny log on locally" → SignInFailed with Windows' reason ("The account is locked." …), 2105 with the status (Windows 11 leaves locked and disabled accounts off the sign-in screen: the provider reports "not offered" after 10 s and the agent asks Windows itself (LogonUserW) – 2105 "didn't offer the account; checked with Windows: error N (0x…)", no 50 s wait); evidence `c-auto3-refusals-logs` with the provider's trace (`provider-*.log`, which calls LogonUI made per refusal – credential-provider-design.md §8.6) | H PS E | 2 | ✅ |
| C-AUTO-21 | DLL signed by another certificate | trusted, but not signed by the agent's certificate → "… isn't signed by the same certificate as the agent", Hello `autoSignIn` false | PS H | 3 | ✅ |
| C-UNLOCK-01 | App tab unlocks the own session | the console account's session locked (LockWorkStation): an app tab → "SignedIn: Unlocked as …" (`existing`), the app runs; the same session, its explorer.exe kept, no second session; 1106 "unlocked console session" | H S E | 1 | ✅ |
| C-UNLOCK-02 | Locked again after the app tab | the last app tab ending → 1107 "… locked (…)", never "signed out"; the session still there with its explorer.exe | H S E | 1 | ✅ |
| C-UNLOCK-03 | Desktop tab unlocks the own session | the session locked, a desktop tab with automatic sign-in and end action SignOut → "Ready to unlock as", "Unlocked as", end action Lock (`existing`); the same session on the console's desktop; the console shows "signed in remotely"; 1106 "unlocked console session … privacy screen on" | H S E | 1 | ✅ |
| C-UNLOCK-04 | Locked again, never signed out | closing that desktop tab → 1107 "… locked (…)", never "signed out", although RemoteHub asked for a sign-out; the session still there with its explorer.exe | H S E | 1 | ✅ |
| C-UNLOCK-05 | Disconnected session reconnected | the lock left the session disconnected: the next automatic sign-in → "Signed in to your session as" (`existing`), the same session back on the console with its explorer.exe; 1106 "… disconnected session, reconnected"; closing → 1107 "locked" | H S E | 1 | ✅ |


### 9.13 Jump hosts (J)

From [jump-host-design.md](jump-host-design.md) §11 (J0…J12 there). `lab/tests/J-JUMP.Tests.ps1` moves the VM under test behind the jump host VM (lab.json `JumpHost`: `RemoteHub-JumpHost`, private switch `RemoteHub-Jump`, 192.168.231.0/24, the test VM gets .10 + its index) at run time; the revert undoes it, the jump VM is turned off afterwards. Everything goes through RemoteHub's client code with the route set (`AgentEndpoint.Via`, `PsTarget.Via`, harness `--jump`).

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| J-00 | Isolation | this PC reaches the jump host; RDP, WinRM and the agent port of the device behind it fail directly | H | 1 | ✅ | Jump |
| J-01 | RDP through the forwarder | the device's RDP certificate (CN = computer name) comes back through the tunnel; the full sign-in and RemoteHub's "Trust this RDP certificate?" question are UI-JUMP-01 | H | 1 | ✅ | Jump |
| J-02 | WinRM through the forwarder | HTTPS to 127.0.0.1 with the name not checked opens a session (HTTP to 127.0.0.1 / localhost needs TrustedHosts on the admin's PC – not changed on the lab host) | H | 1 | ✅ | Jump |
| J-03 | The agent through the jump host | health, PowerShell, files both ways, registry, services, processes, the remote desktop (picture decoded); jump host audit 1200 / 1201 with the device's address and port; the device's audit shows the jump host as the source | H PS E | 1 | ✅ | Jump |
| J-04 | Without the agent | PowerShell over WinRM through the jump host | H | 1 | ✅ | Jump |
| J-05 | Policy | `TunnelEnabled=0` (and no `tunnel` in health), `TunnelTargets` without the device, a port outside `TunnelPorts`, right `Tunnel` removed (PowerShell still works) – each refused with its message: `TunnelEnabled=0` → `/v1/tunnel` 404 before sign-in, no audit; right `Tunnel` removed → 403 at sign-in, audit 2000 "not a member of an allowed group"; TunnelTargets / TunnelPorts → audit 2200 "(TunnelTargets)" / "(TunnelPorts)"; loopback and 169.254.169.254 refused by default, a name never opens them, `127.0.0.0/8` listed does | PS H E | 1 | ✅ | Jump |
| J-06 | Tunnel drop | the jump host's side towards this PC off for 30 s → the desktop resumes without a new question, tools connect again | H N | 1 | ✅ | Jump |
| J-07 | Forwarder only for RemoteHub | another program on this PC connecting to the forwarder's local port gets nothing; RemoteHub's own connection works | H | 1 | ✅ | Jump |
| J-08 | Throughput | 2 GB to the device arrive unchanged while its remote desktop keeps sending pictures | H | 2 | ✅ | Jump |
| J-09 | Install and update through it | the scope offered is the jump host's address (`LocalAddressAsync`: a probe – the agent only resolves and checks the target, answers Refused `probe` "<host>:<port> is allowed; not connected (probe).", no TCP connection to the device), install over WinRM through the jump host, firewall rule scoped to the jump host, update through the agent through it | H PS | 1 | ✅ | Jump |
| J-10 | Shares of a device behind it | SMB (always RemoteHub's own client: "SMB · RemoteHub client"), SFTP and WebDAV copy both ways; FTP refused ("FTP can't go through a jump host …", J-22) | H PS | 2 | ✅ | Jump, Server |
| J-11 | Moving in the tree | connection / folder moved between jump host, folder under it, another jump host and a plain folder → the route follows; the drops of §4.3 refused with "<target> is reached through <outer>. Only one jump host is supported for now." / "<j> is a jump host itself …" / "<inner> in this folder is a jump host itself …"; *Move under* lists only connections with an agent or children (at most 40); settings inherited past the jump host | U | 2 | ✅ | Jump |
| J-12 | Older RemoteHub | the previous RemoteHub opens the database with the jump host and the items below it – or, when the database has a newer schema than it knows (no downgrades before GA, local-db-design §9), says the database is from a newer version and offers the automatic backup | U PS | 2 | ✅ | Jump |
| J-13 | Other refusals | a name that doesn't resolve → `dns`; a closed port → `unreachable` / `timeout`; more than `MaxTunnelStreams` → `limit`; no 2200 for these – unreachable only in the agent log "Jump connection <acct> from <src>: <host:port> unreachable (…)" | PS H | 2 | ✅ | Jump |
| J-14 | Limits shared with sessions | jump connections count toward `maxSessions`; an unreadable policy key → `/v1/tunnel` 404 | PS H | 3 | ✅ | Jump |
| J-15 | Indentation | under a jump host its connections, folders and shares sit one step in beside its guide line, like a folder's items (no extra indent) | U | 3 | ✅ | Jump |
| J-16 | Route shown | tree "via <jump>" beside the address, tooltip "Reached through <jump> (its RemoteHub Agent is the jump host) …"; editor "Reached through <jump> – its RemoteHub Agent is the jump host …"; share tab "<description> · via <jump>"; tooltip location "… › <jump> › <device>" | U | 2 | ✅ | Jump |
| J-17 | Jump host without agent | connecting a child → "<c> is reached through <j>, but <j> has no RemoteHub Agent. Install it there …, or move <c> out of <j> to connect directly."; tree tooltip "<j> has no RemoteHub Agent – install it there to reach the items below it." | U | 2 | ✅ | Jump |
| J-18 | Jump host credentials | none → "No credentials are set for the jump host <j> …"; no stored password → asked once ("… kept in memory only."), then reused for every child and tab, or taken from an open agent tab of <j> | U | 2 | ✅ | Jump |
| J-19 | Deleting a jump host | "Delete connection "<n>"? … <k> item(s) are reached through it …" with *Keep them* (moved next to it, reached directly) or *Delete all* | U | 2 | ✅ | Jump |
| J-20 | Duplicate a jump host | copy "<n> (copy)" without the items under it; its agent and RDP certificate pin cleared | H | 3 | ✅ | Jump |
| J-21 | Move changes credentials | moving into or out of a jump host when the inherited credentials change → "Move – credentials change", target 'connection "<j>" (reached through it)'; the jump host's own credentials are never inherited | U | 2 | ✅ | Jump |
| J-22 | FTP share behind a jump host | menu *Open* off, tooltip "Reached through <j>. FTP can't go through a jump host – its data connections go to addresses the server announces. SMB, SFTP and WebDAV can."; opened anyway → the same text | U H | 2 | ✅ | Jump |
| J-23 | Share editor host hints | a jump host's share on another host → "Reached directly, like <s> itself. …"; a child's share → "Another device, reached through <j> like <s>." | U | 3 | ✅ | Jump |
| J-24 | Drops beside §4.3 | a share on its own dropped onto a connection lands next to it; *Move under* puts it under a jump host; an .rdp file dropped on a jump host is imported under it, on a child next to that child (under the same jump host) | U | 3 | ✅ | Jump |
| J-25 | Install below a jump host without WinRM | the install dialog defaults to "Save the installer and the enrollment file to a folder…", hints "<c> is reached through <j> … allow connections from <j>'s address …", hides "Use the agent … afterwards" | U | 2 | ✅ | Jump |
| J-26 | Agent refusals of /v1/tunnel | 404 → "The RemoteHub Agent on <h:p> can't be used as a jump host: jump connections are turned off by a policy on the device (TunnelEnabled), or the agent there is older …"; 403 → "… may not use … as a jump host … (right Tunnel – by default the administrators)."; both as "Can't use the jump host <j>: …" | H | 2 | ✅ | Jump |
| J-27 | The jump host's own address | its own adapter address refused by default, also when its subnet is in TunnelTargets; allowed only by an entry naming that single address; reason `policy` "… is this jump host itself or a special address …", 2200 "(own or special address)" | PS H E | 2 | ✅ | Jump |
| J-28 | Unreadable policy entries | TunnelTargets / TunnelPorts with only invalid entries (`not a host!`, `0`, `99999`) refuse every Open with `policy`, never "any" | PS H | 3 | ✅ | Jump |
| J-29 | Bad tunnel traffic | more than 256 KB unacknowledged on a stream resets it; a text or garbled frame or a duplicate stream id ends the tunnel, 1202 "(protocol error: …)"; a target not closing within 60 s after Close is reset | H E | 3 | ✅ | Jump |
| J-30 | A name with allowed and disallowed addresses | one name, two addresses (hosts file), one in TunnelTargets → connects to the allowed one (each address checked on its own; the design said all must be allowed) | PS H | 3 | ✅ | Jump |

### 9.14 RemoteHub Broker (B)

The agent's side of the RemoteHub Broker and RemoteHub's way to a device through it (the broker's own design and tests live with the broker). `lab/tests/B-BRK.Tests.ps1` needs the lab's broker – `Broker` and `BrokerToken` in lab.local.json ([test-lab-spec.md](test-lab-spec.md) §5.3, §7); without them every case is skipped with that reason. Each run makes its own organisation through the broker's admin API (harness `broker --kind org`) and deletes it afterwards. RemoteHub's side runs through its own client code in the harness (`broker --kind health`: Connect with the console secret, the device's sealed record, `AgentClientTransport.ThroughBroker`); the settings page, the synced folder and the menu are covered by `BrokerUiTests` (STA) and the broker tests in `tests/RemoteHub.Tests`.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| B-BRK-01 | MSI with the broker's properties | `BROKER_URL`, `BROKER_SECRET_FILE`, `BROKER_ONLY=1` on a device without an enrollment file: installed (0 / 3010), no inbound firewall rule, the device enrolls itself, registers with the device secret's id and shows online at the broker with its computer name and a fingerprint; agent.json has the organisation; audit 1303 (registered) and 1300 (connected) | PS E | 1 | ◐ | Broker |
| B-BRK-02 | Through the broker | RemoteHub's way: Connect with the console secret, the device's record sealed, the agent's health through a relayed session pinned to the record's fingerprint answers the device's id; audit 1301 (session started) | H E | 1 | ◐ | Broker |
| B-BRK-03 | Device policy | `BrokerEnabled=0` → the device goes offline at the broker; the policy cleared → online again | PS | 1 | ◐ | Broker |
| B-BRK-05 | Direct on the lab network | The device's firewall on: the agent's health through the broker answers over the direct path (`DirectLocal`: QUIC on a punched port, the host candidates of the lab network); audit 1301 says "direct local"; the firewall rule the agent opened for the try ("RemoteHub Agent direct path") is gone within 30 s of the session ending | H PS E | 1 | ◐ | Broker |
| B-BRK-06 | Relayed by device policy | `BrokerDirect=0`: the same request answers relayed; audit 1301 says "relayed" | H PS E | 1 | ◐ | Broker |
| B-BRK-07 | UDP blocked | Inbound and outbound UDP of the agent blocked on the device: the request answers relayed, within 10 s, without an error | H PS E | 2 | ◐ | Broker |
| B-BRK-08 | A connection that silently stops carrying | keep-alive 30 s (`BrokerKeepAliveSeconds`); the next hop to the broker pointed at a MAC nobody has (a permanent neighbour entry: no close, no network change): within 110 s the agent log says the connection ended – its keep-alive's "… the connection was given up – nothing from the broker for … s", or Windows resetting it first (about 35 s, when the keep-alive's retransmissions find no way out); the entry removed: "… connected again", online at the broker, and a request through the broker answers | PS E | 1 | ◐ | Broker |
| B-BRK-09 | A broker-only device | installed with BROKER_ONLY=1 (no inbound rule): the harness as RemoteHub with Reach Automatic (`-DirectFirst`) gets the device's own IPv4 addresses from the broker and that it takes no direct connections, and answers through the broker path (direct local or relayed) | H PS | 2 | ◐ | Broker |
| B-BRK-10 | Reached at its own address | the agent's inbound rule added and the agent restarted (it tells the broker it takes direct connections): the harness answers with path `Network` – the device's name or one of its reported addresses – and no BrokerSessionStarted (1301) event follows | H PS E | 1 | ◐ | Broker |
| B-BRK-11 | A new address reported | a second address (10.250.x.y/24) added to the device's adapter: within 45 s the broker has it among the device's addresses; removed: it goes again | H PS | 2 | ◐ | Broker |
| B-BRK-12 | Listener off by policy | `ListenEnabled = 0` and a restart: nothing listens on 58732, the agent's inbound rule is there but switched off, the harness (`-DirectFirst`) answers through the broker path and the broker has no addresses and `inbound` false; `ListenEnabled = 1` and a restart: the port listens, the rule is on, path `Network` | H PS | 1 | ◐ | Broker |
| B-BRK-04 | Leave and join | `broker leave` → "Left the RemoteHub Broker …", offline, `broker status` "No RemoteHub Broker."; `broker join <url>` with the device secret on standard input (what *Assign to RemoteHub Broker* runs) → "Joined …", online; the console secret refused ("… console secret …", exit 1) | PS | 2 | ◐ | Broker |
| SEC-BRK-01 | The device secret is kept nowhere | on no command line while the MSI runs (`BROKER_SECRET_FILE`), the file deleted once registered, not in the installer log, not in agent.json (only the derived enroll and record keys), not in the agent's logs or the Application log, not in `broker status` | PS E | 1 | ◐ | Broker |

### 9.15 App tabs (D-APP, D-APPSPIKE, D-APPSTORE)

Apps from a connection, each started in the admin's own session on a virtual monitor of its own and shown in its own tab ([app-tabs-design.md](app-tabs-design.md)). The stand-in app is `RhLabApp.exe` (`lab/guest/RhLabApp.cs`, compiled in the guest by `Install-LabTestApp`): a window of one colour that logs where it was started, whether elevated, and what was typed into it.

`lab/tests/D-APPSPIKE.Tests.ps1` is the spike of the design's §11: it checks the agent's virtual display driver and Windows before anything is built, and runs only when asked for (`# LabRuns: on request`: `-Tag AppSpike` or its case IDs). Its results are in the design's §5.1. D-APPSPIKE-02 and -03 fail because of what they found, not because of the test: a reload of the driver minimises windows on every virtual monitor, and a reload with the monitors in their own modes leaves the device failed. Before each context the test restarts a failed device, so one failure doesn't fail the cases after it. `lab/tests/D-APP.Tests.ps1` checks the feature; every case is skipped while the agent doesn't offer app tabs (no `apps` feature in Hello). Its harness steps (`start` with `app`, `appList`, `appClose`, `appRestart`) come with the feature. `lab/tests/D-APPSTORE.Tests.ps1` is the spike for Store apps (the design's §6.6, phase 2): Windows alone, no agent, and also only on request (`-Tag AppStoreSpike`); the stand-in runs as the console account from a scheduled task.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-APPSPIKE-01 | Monitors one at a time | the driver's count 2 → 3 → 2 → 1 (settings + `RELOAD_DRIVER`, a device restart only when the reload isn't enough): the console account's session shows that many monitors each time; the way that worked is in the evidence | PS S | 1 | ✅ | – |
| D-APPSPIKE-02 | Windows stay when another monitor goes | three test app windows on the three virtual monitors; the count goes 3 → 2: the windows on the monitors that stay keep their monitor and position | PS S | 2 | ✅ | – |
| D-APPSPIKE-03 | A mode per monitor | two virtual monitors at 1600×900 and 1024×768 at once; 1500×800 (not listed) can be set after it was added to the list and the driver reloaded | PS S | 1 | ✅ | – |
| D-APPSPIKE-04 | Eight monitors | count 8 → eight virtual monitors in the session | PS S | 2 | ✅ | – |
| D-APPSPIKE-05 | Taskbar on other monitors | `MMTaskbarEnabled` 1 → `Shell_SecondaryTrayWnd` on the other monitors; 0 and `WM_SETTINGCHANGE` "TraySettings" → none, without restarting Explorer; 1 → back | S | 1 | ✅ | – |
| D-APPSPIKE-06 | Foreground between two apps | a process in the session clicks into app A (SendInput), then `SetForegroundWindow(B)` succeeds and typed text reaches B, not A (the stand-in runs as the user, not as the LocalSystem helper) | S | 1 | ✅ | – |
| D-APPSPIKE-07 | Two connections to one session | a second desktop connection to the console account's own session while the first is up: both get pictures; the first ending (closed by the harness) leaves the second getting pictures | H | 1 | ✅ | – |
| D-APP-01 | Start menu list | nobody signed in: `appList` lists Event Viewer as `mmc.exe … eventvwr.msc` with its icon, the test app once (a duplicate shortcut is dropped), its group "RH Lab" and *Run as administrator*; a document, a `.url`, a missing target and a shortcut behind a junction are not listed; no session was created | H PS | 1 | ◐ | – |
| D-APP-02 | List and policy | `DesktopAppsAllowed` = the test app → only it; `DesktopAppsElevated` = 0 → no entry elevated; `DesktopApps` = 0 → refused (Failed) | H PS | 1 | ◐ | – |
| D-APP-03 | Three apps, three tabs | red, green and blue test apps in three tabs started together with nobody signed in: one automatic sign-in (one 1106), all three Connected; each tab's picture > 90 % its own colour, < 0.1 % of the other two and of the magenta marker on the primary monitor; three different monitors | H PS S E | 1 | ◐ | – |
| D-APP-04 | Keyboard follows the tab | a click in red's tab, then text typed in green's tab with the marker window in front before: green's app has the text, red's and the marker none | H S | 1 | ◐ | – |
| D-APP-05 | Tab size | blue's tab resized to 1600×900: picture, monitor and the maximised app window are 1600×900 | H S | 2 | ◐ | – |
| D-APP-06 | Shell keys dropped | Win+R in an app tab opens no Run dialog | H S | 2 | ◐ | – |
| D-APP-07 | Closing with unsaved changes | `appClose` on an app that asks: status AppClosing, the question (`#32770`) on the tab's monitor and in its picture; `appClose` with force ends the tab and the app keeps running | H S | 2 | ◐ | – |
| D-APP-08 | Last tab signs out | the last of the three tabs ends → the console has nobody signed in within 2 minutes | H PS | 1 | ◐ | – |
| D-APP-09 | Audit | three 1109 events with the program's path; no audit entry contains the arguments | E | 1 | ◐ | – |
| D-APP-10 | Run as administrator | an elevated entry starts with an elevated token and no `consent.exe`; with `DesktopAppsElevated` = 0 refused and nothing started | H PS | 1 | ◐ | – |
| D-APP-11 | Someone else signed in | normaluser on the console: an app tab of the console account is refused naming normaluser, nothing started | H PS | 1 | ◐ | – |
| D-APP-12 | Resume | dropped and continued with the resume token: Connected again, the app started only once, a picture after | H | 2 | ◐ | – |
| D-APP-13 | Taskbar setting after a crash | while a tab is open the account's `MMTaskbarEnabled` is 0; the service killed and started: the account's value is back, no record left | PS | 1 | ◐ | – |
| D-APP-14 | MaxAppTabs | `MaxAppTabs` = 1 and one tab open: a second tab is refused, nothing started | H | 2 | ◐ | – |
| D-APP-15 | No Store apps signed out | nobody signed in: the list comes in one part, says *Store apps are listed once you're signed in on …* and has no Store apps | H | 2 | ◐ | – |
| D-APP-16 | Store apps listed | signed in: a second part (the first has `more`) with the account's Store apps in the group *Store apps* – Settings as `shell:AppsFolder\<AUMID>`, not elevated, with its icon | H | 1 | ◐ | – |
| D-APP-17 | A Store app in a tab | Settings (a UWP app, drawn by ApplicationFrameHost outside the tab's job) started in a tab: Connected; its `ApplicationFrameWindow` is maximised on a monitor that isn't the primary one; one 1109 with `shell:AppsFolder\<AUMID>` | H S E | 1 | ◐ | – |
| D-APP-18 | Closing a Store app | `appClose` on the Settings tab: the tab ends (AppClosed or Ended) and SystemSettings is no longer running within 30 s | H PS | 1 | ◐ | – |
| D-APP-19 | The admin's scale | a tab of 1920×1080 at 150 %: the monitor the app's window is on has 144 dpi, the primary monitor 96; the connection info says *scale 150 %* | H S | 1 | ◐ | – |
| D-APP-20 | Scale limits | the same tab at 1280×720 and 150 %: the monitor runs at most at 150 %, and the connection info names the scale it runs at (and *150 % asked* when Windows allows less); at 100 % again it has 96 dpi | H S | 2 | ◐ | – |
| D-APP-21 | Scale put back | the tab ends at 150 % (the profile's `PerMonitorSettings\MTT1337…` `DpiValue` 2 while it runs): after the sign-out no virtual monitor's entry has a scale | H PS | 1 | ◐ | – |
| D-APPSTORE-01 | Store apps listed | the console account's `shell:AppsFolder` has entries whose path is an AppUserModelID, Calculator or Settings among them; the time the list took is in the evidence | S | 1 | ✅ | – |
| D-APPSTORE-02 | Started by AppUserModelID | `IApplicationActivationManager::ActivateApplication` from a process with no window of its own (a scheduled task as the user) starts the UWP app and Windows Terminal (where present) and returns a process ID | S | 1 | ✅ | – |
| D-APPSTORE-03 | Process in a job | the returned process can be added to a new job object (nested jobs) | S | 1 | ✅ | – |
| D-APPSTORE-04 | The app's windows | within 20 s a visible window: for the UWP app ApplicationFrameHost's `ApplicationFrameWindow` with the app's `PKEY_AppUserModel_ID`; for Windows Terminal a window of the started process | S | 1 | ✅ | – |
| D-APPSTORE-05 | Closed by WM_CLOSE | `WM_CLOSE` to those windows: none with the app's ID is left within 20 s | S | 1 | ✅ | – |

## 10. RemoteHub on a Windows client (C, R)

RemoteHub itself installed in a client VM, driven by UI Automation (FlaUI) in `normaluser`'s or `Admin`'s session.
Data checks read the files and the database through RemoteHub's own `LocalDb` classes in the harness where needed.

### 10.1 Installer

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-MSI-01 | Silent install | `msiexec /i /qn` exit 0; `C:\Program Files\RemoteHub\RemoteHub.exe` version = MSI; Start menu shortcut to the exe, with the exe's icon (not one under `C:\Windows\Installer`, gone after the next upgrade); `Agent\RemoteHub-Agent-x64.msi` bundled and identical to the agent MSI | PS | 1 | ✅ |
| C-MSI-02 | Starts without .NET installed | first start shows the main window (no runtime prompt) | U | 1 | ✅ |
| C-MSI-03 | .rdp association | RemoteHub in "Open with" and Default apps; mstsc still the default | PS | 2 | ✅ |
| C-MSI-04 | Upgrade from the previous release | settings, connections, credentials, audit log intact; one ARP entry; the shortcut's icon file exists | PS U | 1 | ✅ |
| C-MSI-05 | Upgrade from RDP Manager 2.x | a direct install over 2.x is refused ("A newer version of RemoteHub is already installed.": 2.x is numbered higher); after uninstalling 2.x one Apps entry "RemoteHub"; on the first start `%APPDATA%\RdpManager` is copied to `%APPDATA%\RemoteHub` (only if that doesn't exist), the old folder kept | PS U | 2 | ✅ |
| C-MSI-06 | Downgrade | refused | PS | 2 | ✅ |
| C-MSI-07 | Uninstall | program folder, shortcut, ProgId gone; user data kept (documented leftovers only) | PS | 2 | ✅ |
| C-MSI-08 | Install as standard user | refused / UAC prompt (per-machine) | S | 3 | ✅ |
| C-MSI-09 | Same-version reinstall | a rebuilt MSI with the same version replaces the installed one (`AllowSameVersionUpgrades`), exit 0, one Apps entry | PS | 3 | ✅ |

### 10.2 Storage, protection, audit

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-DB-01 | New database | `%LOCALAPPDATA%\RemoteHub\remotehub.db` not readable as plain SQLite; `db.key` DPAPI-protected; the test password not in any file (byte search) | U PS | 1 | ✅ | |
| C-DB-02 | Other Windows user | normaluser can't open Admin's database (DPAPI) | PS | 1 | ✅ | |
| C-DB-03 | Migration from settings.dat | 0.13 `settings.dat` → database, field-by-field equal, renamed `.migrated` | PS U | 1 | ✅ | |
| C-DB-04 | Daily backup | `backup\remotehub-<UTC yyyyMMdd-HHmmss>.db`: one per day, taken in the background at start only (newest ≥ 24 h old, after `PRAGMA quick_check`), 7 kept (clock moved forward) | PS U | 2 | ✅ | |
| C-DB-05 | Corrupt database | byte 2048 (first page) flipped → RemoteHub offers the latest backup, restores it, damaged file kept as `remotehub.db.damaged-<yyyyMMdd-HHmmss>` (local time) with `-wal` / `-shm`; *No* leads to C-DB-06 | PS U | 1 | ✅ | |
| C-DB-06 | Unreadable database, no usable backup | "RemoteHub's database can't be opened: <reason> Start with empty settings? The unreadable database is kept in <dir> …" (OK / Cancel); OK → `remotehub.db`, `-wal`, `-shm`, `db.key` renamed `.damaged-<stamp>`, `backup\` → `backup.damaged-<stamp>`, a new empty database; Cancel → RemoteHub ends, nothing changed | PS U | 2 | ✅ | |
| C-DB-07 | Older backup when the newest fails | restoring a backup fails → the offer comes again for the next older one | PS U | 3 | ✅ | |
| C-DB-08 | db.key missing | reason "The database key file (db.key) is missing." | PS U | 3 | ✅ | |
| C-DB-09 | Database of a newer version | reason "The database was created by a newer version of RemoteHub (schema N)." | PS U | 3 | ✅ | |
| C-DB-10 | Daily check finds damage | no backup made, the existing ones kept; audit "Database check failed": "The database check found a problem; the existing backups were kept." | PS U | 2 | ✅ | |
| C-DB-11 | Migration fails | the new database deleted, `settings.dat` stays in use; a `settings.dat` that reappears next to a database with data is ignored | PS | 3 | ✅ | |
| C-DB-12 | `.migrated` clean-up | 30 days after the migration `settings.dat.migrated` and `settings.dat.bak` deleted at start | PS | 3 | ✅ | |
| C-DB-14 | No stored password in memory | RemoteHub started again with a connection whose password is stored (C-DB-01), not used: a full memory dump of `RemoteHub.exe` holds the password neither as UTF-8 nor as UTF-16; the connection's host name is found (the control) – local-db-design.md §17 | PS U | 1 | ✅ | |
| C-DB-13 | Unreadable `settings.dat` | a copy `settings.dat.unreadable-<stamp>`; "The settings file could not be read (<err>). A copy was saved to: <path> …"; starts empty | PS U | 3 | ✅ | |
| C-LOCK-01 | Startup password | on (Windows Hello unticked) → restart asks ("RemoteHub – locked"); wrong password refused; right one opens; database re-encrypted with a new key (old `db.key` copy opens nothing), old backups deleted and exactly one new one written, `settings.dat.migrated` / `.bak` deleted; audit "Unlock failed" "Wrong startup password (attempt n)" written after the next successful unlock | U PS | 1 | ✅ | |
| C-LOCK-02 | Auto-lock | "Lock when you're away" *After 1 minute* (the shortest choice; saved at once, no password needed; default Never) → after 1:00–1:15 idle the windows hidden, password asked; sessions keep running | U | 2 | ✅ | |
| C-LOCK-03 | Windows Hello PIN | VM with vTPM + Hello PIN: unlock with Hello | U S | 3 | ✅ | vTPM |
| C-LOCK-04 | Turn off | password removed, database re-keyed, old backups removed, exactly one new backup (made by the re-encryption) | U PS | 2 | ✅ | |
| C-LOCK-05 | Change the startup password | needs the current one; a new key (an old `lock.json` copy opens nothing), database re-keyed, Hello set up again if it was; "The startup password was changed." | U PS | 2 | ✅ | |
| C-LOCK-06 | Password rules | "Use at least 8 characters.", "The passwords do not match.", "The current password is wrong.", empty unlock → "Enter the startup password." | U | 3 | ✅ | |
| C-LOCK-07 | Exit while auto-locked | *Exit RemoteHub* ("Disconnects all sessions and closes RemoteHub") ends it without showing the windows again | U | 2 | ✅ | |
| C-LOCK-08 | No auto-lock under a dialog | a modal dialog open → no lock; it locks after the dialog closes | U | 3 | ✅ | |
| C-LOCK-09 | Old RDP Manager folder | with `%APPDATA%\RdpManager` present, turning on offers "Delete the old, unprotected copy of the RDP Manager settings (<path>)"; ticked → the folder deleted | U PS | 3 | ✅ | |
| C-LOCK-10 | keys.dat sealed | with protection on `keys.dat` starts with `RHK2` | PS | 3 | ✅ | |
| C-SHARE-01 | Shared folder on SMB | settings on a share (`RemoteHub.settings`), the password set once when creating the file (*Protect shared settings*, ≥ 8 characters), `keys.dat` remembers it – never asked again on that PC | U | 2 | ✅ | SMB |
| C-SHARE-02 | Sync conflict | file changed from a second VM while edited → Reload / Keep mine offered, nothing overwritten silently | U | 2 | ✅ | SMB, 2VM |
| C-SHARE-03 | Share unavailable at start | share offline (or the password prompt cancelled: "Password entry was cancelled.") → starts with local settings, "Settings: this PC (shared file unavailable – changes stay on this PC)", shared file untouched | U N | 2 | ✅ | SMB |
| C-SHARE-04 | Change seen with nothing pending | "Settings updated": "The settings file was updated on another device. Load the new version now?" (*Reload*) | U | 2 | ✅ | SMB, 2VM |
| C-SHARE-05 | Join an existing shared file | "Open shared settings": "This folder already contains a shared settings file. Enter its password … The connections currently on this PC are replaced by the shared ones …" (*Open*) | U | 2 | ✅ | SMB |
| C-SHARE-06 | Back to this PC only | "Use this PC only" … "The shared file itself is not deleted." (*Switch*); audit "Settings location changed" "Settings: this PC" | U | 3 | ✅ | SMB |
| C-SHARE-07 | Forget remembered passwords | `keys.dat` deleted, "Done – the password will be asked again at the next start.", "Unlock shared settings" at the next start | U | 3 | ✅ | SMB |
| C-AUD-01 | Audit entries | connect, edit, delete, password copied for the Windows password prompt (detail "For the Windows password prompt (cleared after 30 s)"), transfer, record → entries without values or passwords | U | 2 | ✅ | |
| C-AUD-02 | Append-only | direct SQL update/delete of an entry refused; Verify integrity finds a changed entry | PS U | 1 | ✅ | |
| C-AUD-03 | Retention policy | `AuditRetentionDays=1` → older entries pruned | PS U | 3 | ✅ | |
| C-AUD-04 | CSV export | formula-like values defused (`=cmd` → `'=cmd`) | U | 2 | ✅ | |
| C-AUD-05 | Retention in Settings → Logs | choices 30 days … 10 years, default "1 year (default)", "Older entries are removed the next time RemoteHub starts."; audit "Audit log retention changed" "Entries kept for <x> (before: <y>)"; under the policy the box is off, "Set by your organisation." | U | 2 | ✅ | |
| C-AUD-06 | Newest entries removed | Verify: "The newest entries were removed: the log ends at entry X, but entry Y was written."; intact → "All N entries are unchanged and complete since they were written." | PS U | 2 | ✅ | |
| C-AUD-07 | More events | "Added", "Connection failed", "Startup protection changed" ("Startup password" / "… and Windows Hello" / "Off"), "Settings location changed", "Backup exported" ("<n> folders and connections, <m> saved credentials, <p> pages") | U | 3 | ✅ | |
| C-AUD-08 | Bulk save summary | more than 50 changes in one save → one "Changed" entry, target "<n> items", detail "<a> added, <c> changed, <d> deleted" | PS U | 3 | ✅ | |
| C-BAK-01 | Backup round trip | export `.rhbackup` with password, import on a fresh profile (normaluser) as new folder and as replace → identical incl. credentials | U | 1 | ✅ | |
| C-BAK-02 | Wrong backup password | refused, nothing imported | U | 2 | ✅ | |
| C-BAK-03 | Old `.rdpmbackup` | still imports (accepted by its format `rdpmanager-backup`, not the extension; 10,000–10,000,000 PBKDF2 iterations) | U | 3 | ✅ | |
| C-BAK-04 | Not a backup file | "Import failed": "Not a RemoteHub backup file." | U | 3 | ✅ | |

### 10.3 Policies, updates, telemetry, crash reports

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-POL-01 | `DisableTelemetry` | no request to `/telemetry` (mock sees none), shown as managed | PS U M | 1 | ✅ | |
| C-POL-02 | `DisableUpdateCheck` | no request to `/latest` | PS U M | 1 | ✅ | |
| C-POL-03 | `UpdateChannel` | `stable` → no `?channel=beta`; channel can't be changed | PS U M | 2 | ✅ | |
| C-POL-04 | `SettingsFolder` | a policy folder that holds `RemoteHub.settings` (or only `RdpManager.settings`) → that file is used, its password asked once; *Change location…* disabled, Settings shows "(set by your organisation)", the overview "Shared folder (set by your IT policy)"; a policy folder without the file → "The shared settings file could not be opened …" and local settings for that session | PS U | 2 | ✅ | SMB |
| C-POL-05 | `DisableRecording` | Record button hidden in every tab type | PS U | 2 | ✅ | |
| C-POL-06 | `RecordingFolder` with `%USERPROFILE%` | recordings land there, can't be changed | PS U | 2 | ✅ | |
| C-POL-07 | HKCU vs HKLM, legacy `RdpManager` key | per value HKLM wins, also with 0 (HKLM `DisableRecording`=0 beats HKCU 1); the legacy `Policies\RdpManager` key is read only for `SettingsFolder` (HKLM\RemoteHub, HKLM\RdpManager, HKCU\RemoteHub, HKCU\RdpManager), its other values ignored | PS U | 3 | ✅ | |
| C-POL-08 | `DisableAgentAutoSignIn` | the editor rows "Sign in automatically when nobody is signed in" and "When an automatic sign-in's connection ends" shown but greyed out ("Turned off by your organisation"); the agent desktop's connect page never offers automatic sign-in, even with the stored option On; the menu entry *Install credential provider* and the install dialog's "Allow automatic sign-in" stay | PS U | 2 | ✅ | |
| C-POL-09 | `UpdateChannel` values and types | `ga` / `release` read as stable, `prerelease` / `pre-release` as beta (any case); an unknown value (`nightly`) ignored – the user's channel applies, the channel buttons stay on; a DWORD policy stored as REG_SZ (`DisableTelemetry`="1") ignored | PS U M | 3 | ✅ | |
| C-UPD-01 | Update offered | mock announces a higher version → bar shown; lower / equal → nothing | U M | 2 | ✅ | |
| C-UPD-02 | Checksum mismatch | download deleted, not installed | U M | 1 | ✅ | |
| C-UPD-03 | Untrusted host | download URL outside GitHub / remotehub.dev → refused | U M | 1 | ✅ | |
| C-UPD-04 | Install | UAC approved on the secure desktop with the console keyboard (Alt+Y) → MSI installs, RemoteHub restarts with the new version | U C M | 2 | ✅ | |
| C-UPD-05 | Mandatory / skip | skipped version hidden; mandatory (or below `minSupportedVersion`) can't be skipped and is offered even when that version was skipped before | U M | 3 | ✅ | |
| C-UPD-06 | No valid checksum | `download.sha256` missing, not 64 characters or not hex → "The update has no valid SHA-256 checksum, so it can't be verified."; nothing requested from the download URL | U M | 1 | ✅ | |
| C-UPD-07 | Size mismatch | more than `download.size` → "The download is larger than announced."; less → "The download is incomplete (x of y bytes)."; neither `.partial` nor `.msi` kept | U M | 3 | ✅ | |
| C-UPD-08 | Untrusted redirect | an allowed URL redirecting to a host other than github.com / githubusercontent.com / remotehub.dev (and subdomains) → "The download was redirected to an address that isn't trusted." | U M | 2 | ✅ | |
| C-UPD-09 | Update server errors | *Check for updates now*: 404 on stable → "No release in this channel yet" / "There is no GA (stable) release yet. Switch to the Beta channel…", on beta → "The update server has no release for this channel."; 500 → "Couldn't check for updates"; bad JSON → "…an answer RemoteHub doesn't understand."; no answer in 15 s → "The update server didn't answer in time."; a lower version → "RemoteHub is up to date" (only updates to a higher version) | U M | 3 | ✅ | |
| C-UPD-10 | Install declined at the open-sessions question | "Install cancelled. The verified download is kept until the next update."; no install started (no msiexec other than the Windows Installer service, no `install-update.cmd`); *Download and install* enabled again | U M | 3 | ✅ | |
| C-UPD-11 | The approval names RemoteHub | `C:\Program Files\RemoteHub\RemoteHubUpdate.exe` is installed next to `InstallUpdate.ps1`, its file description is "RemoteHub Update" and it is signed (valid) by the certificate that signed `RemoteHub.exe` – what Windows shows as the program and its publisher (the prompt's screenshot in the evidence); after *Yes* the step runs as `powershell.exe … -File "C:\Program Files\RemoteHub\InstallUpdate.ps1" … -Signer "C:\Program Files\RemoteHub\RemoteHubUpdate.exe"`, not as an encoded command (`C-POLUPD2.Tests.ps1`) | U M C PS | 1 | ◐ | lab signing certificate |
| C-TEL-01 | Telemetry content | `app_start`: exactly `event, version, channel, os` (Windows 10 / 11 correct per VM), `arch`, `installId` and the counts `connections, agents, shares, ssh, folders` (match the tree); `app_session` after a second start: the six base keys and `runSeconds` (only when the previous session ran > 0 s; its `version` is the previous session's); `channel` is the effective one (`beta` for 0.x); nothing else | U M | 1 | ✅ | |
| C-TEL-02 | Off deletes install ID | `install-id` file gone | U PS | 2 | ✅ | |
| C-TEL-03 | Off discards local records | telemetry off (setting or policy) → at the next start pending `crash\crash-*.json` deleted and never sent, `run-time.json` removed and `install-id` deleted – with the policy as with the setting (telemetry fully off) | PS U M | 2 | ✅ | |
| C-CRASH-01 | .NET crash | forced crash → file in `crash\`, sent at next start as `app_crash`, redacted (no user, host, path, IP) | U M | 2 | ✅ | |
| C-CRASH-02 | Unexpected exit | killed in Task Manager → `UnexpectedExit` next start | U M | 3 | ✅ | |
| C-CRASH-03 | Native crash | unclean exit with an Application Error 1000 for RemoteHub.exe → `app_crash` with `errorType` `NativeCrash`, message "Exception 0x… in <module> <ver>", stack "<module>+0x<offset>", `version` of the crashed session | U E M | 3 | ✅ | |
| C-CRASH-04 | Crash queue limits | at most the newest 5 reports sent; reports older than 30 days deleted; a report the server refuses stays in `crash\` and goes at the next start; a handled "RemoteHub – unexpected error" recorded at most 3 times per session | PS U M | 3 | ✅ | |

The mock (M) is a small HTTPS server on the host with a lab root certificate trusted in the VM and the
`update.remotehub.dev` name pointed at it through the VM's hosts file. It records every request for the checks.

### 10.4 RDP through RemoteHub (2VM)

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| R-RDP-01 | Connect with saved credentials | session to the device VM reaches its desktop (device-side: new session for PSAdmin) | U PS | 1 | ✅ | 2VM |
| R-RDP-02 | Local account `.\user` | signs in as the device's local account | U PS | 2 | ✅ | 2VM |
| R-RDP-03 | Saved credentials without the NTLM delegation policy | non-domain target without the delegation policy → the stored password still signs in (RemoteHub hands it to the RDP control, not through Credential Manager): the tab connects, a new session on the device, no Windows prompt or message about saved credentials | U PS | 3 | ✅ | 2VM |
| R-RDP-04 | Password prompt helper | device with "Always prompt" → password on the clipboard, excluded from history, cleared after 30 s or after login | U PS | 2 | ✅ | 2VM |
| R-RDP-05 | Clipboard / drive redirection | text both ways; drive redirection shows `\\tsclient\C` only when on | U PS | 2 | ✅ | 2VM |
| R-RDP-06 | Dynamic resolution | resize the tab → device session resolution follows | U PS | 3 | ✅ | 2VM |
| R-RDP-07 | Ctrl+Alt+Del, full screen, detach/dock | each works, keyboard focus frame shown | U | 3 | ✅ | 2VM |
| R-RDP-08 | .rdp import/export | export with password, import on normaluser → connects; risks dialog lists redirections | U | 2 | ✅ | 2VM |
| R-RDP-09 | Single instance | opening a `.rdp` while running → same window, entry reused | U | 3 | ✅ | 2VM |
| R-RDP-10 | Password toast *Clear now* | 'Stored password for "<name>" copied' / "Press Ctrl+V in the password field, then OK." with *Clear now* (clipboard emptied at once); closes when Windows' dialog closes or the clipboard changes | U | 3 | ✅ | 2VM |
| R-RDP-11 | Password prompt "Do nothing" | *When Windows asks for the password* = Do nothing → clipboard untouched, no toast, no "Password copied" audit | U PS | 2 | ✅ | 2VM |
| R-RDP-12 | Switch hotkey inside RDP | Ctrl+Alt+< → next tab, Ctrl+Alt+Shift+< → previous (full screen left first); on de-DE AltGr+< types the pipe character in the session instead | U C | 3 | ✅ | 2VM |
| R-RDP-13 | Additional RDP session | with an RDP tab open *Open additional session* (tree) / *Additional remote desktop session* (tab) → a second tab and a second session on the device | U PS | 3 | ✅ | 2VM |
| R-AGENT-01 | Full agent flow from the UI | Install RemoteHub Agent… over remoting from the UI, then PowerShell, file transfer, registry, services, processes and agent desktop tabs work | U | 1 | ✅ | 2VM |
| R-AGENT-02 | Automatic → agent desktop | "Remote desktop through: Automatic" opens the agent desktop; *Use RDP instead* opens RDP | U | 2 | ✅ | 2VM |
| R-AGENT-03 | Own RDP session warning | picking the session connected by RDP from this PC warns | U | 3 | ✅ | 2VM |

### 10.5 End to end through RemoteHub's window (2VM)

The admin's side is RemoteHub in the client VM, driven by FlaUI in `Admin`'s session.
- The keyboard and clipboard on the admin's PC are real: Ctrl+C / Ctrl+V go to RemoteHub's window through the Hyper-V console keyboard or SendInput in that session.
- The device side is checked with PS Direct and in-session helpers on the device VM.
- Each case runs against two devices: a **client device** (the second client VM, e.g. W10 against W11) and `Server-2025-en-US`.

**Agent connection**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-AG-01 | Install from the UI | right-click → RemoteHub Agent → *Install RemoteHub Agent…* (silent over remoting) → rows end "Installed – service Running." and "RemoteHub Agent <v> answers on port 58732 with the expected certificate.", *Done*; the shield's tooltip "RemoteHub Agent <version>: active – …"; audit "Agent installed" "Installed – Remoting" | U PS | 1 | ✅ |
| UI-AG-02 | Test RemoteHub Agent | right-click → Tools → *Test RemoteHub Agent* (offered until the agent is known to work; then the tools are offered instead) → "The RemoteHub Agent works (PowerShell <x.y> on <COMPUTERNAME>)." | U | 1 | ✅ |
| UI-AG-03 | Wrong pin | change the device certificate (re-enroll from another "RemoteHub") → warning that can't be clicked through; no 4625 / sign-in line on the device | U PS E | 1 | ✅ |
| UI-AG-04 | Wrong password / not allowed | credential with wrong password → clear message; normaluser → "not allowed" message | U | 2 | ✅ |
| UI-AG-05 | Agent stopped | stop the service → tabs report the connection failure, no hang; start → reconnects on retry | U PS | 2 | ✅ |
| UI-AG-06 | Update from the UI | *RemoteHub Agent → Update RemoteHub Agent…* with the agent installed → through the agent, new version shown on the shield | U PS | 1 | ✅ |
| UI-AG-07 | Allow automatic sign-in in the install dialog | checkbox shown only when the package contains the provider, enabled only for remoting and through the agent (shown but off for clipboard / save to folder, nothing passed); starts ticked (on by default) unless RemoteHub's record says the provider was removed; ticked → `AUTOSIGNIN=1`; unticked → `AUTOSIGNIN=0` (the MSI installs the provider by default otherwise) | U PS | 2 | ✅ |
| UI-AG-08 | Credential provider from the UI | right-click → *RemoteHub Agent* → *Install credential provider* (only with the agent installed and the provider in RemoteHub's package) → confirmed, "The credential provider is installed and registered.", audit `agent.credential_provider` "Installed – credential provider"; the entry then reads *Remove credential provider*; with "Sign in automatically when nobody is signed in" On the sign-in screen is signed in, audit `agent.auto_sign_in` "Requested" then "Signed in automatically on the console" | U PS | 2 | ✅ |
| UI-AG-09 | Connect page offers automatic sign-in | checkbox "Sign in automatically as <account>" only for the console session at the sign-in screen, when the device reports `autoSignIn` and `Rights.AutoSignIn` and `DisableAgentAutoSignIn` isn't set; ticked when the connection option is On; *View only* switches it off; hidden for any other session | U S | 2 | ✅ |
| UI-AG-10 | Sign out asks first | end action *Sign out*: the notice after SignedIn ends "When you disconnect, the session is signed out."; Disconnect, closing the tab (x, middle click, Ctrl+W) or the group asks "Sign out on the device?" (*Disconnect* / Cancel keeps it) – the tab's own x shows only with more than one tab of the connection, with one tab the group's x closes it; with Lock, or the device's `AutoSignInEndAction` = Lock, "… is locked." and nothing asked | U S | 2 | ✅ |
| UI-AG-11 | 2-minute reconnect after automatic sign-in | after SignedIn the device's network cut 60 s → the tab shows "Attempt n – trying for up to 120 s." and resumes the same session; past 120 s: "The connection dropped and couldn't be restored within 120 s: …"; a normal session keeps 30 s (UI-NET-01) | U N | 2 | ✅ |
| UI-AG-12 | Password checks before sending | no stored password → "Automatic sign-in needs a stored password for this connection – sign in on the device's screen instead."; over 256 characters → "The password is too long for the automatic sign-in – …"; nothing sent, no "Requested" audit entry | U | 3 | ✅ |
| UI-AG-13 | Agent test fails → offer to install | agent stopped, *Test RemoteHub Agent* → "The RemoteHub Agent on <name> could not be used. <reason>" with *Install / update the agent…*; the entry then reads "Test RemoteHub Agent again", tooltip "Last result: <reason>" | U PS | 2 | ✅ |
| UI-AG-14 | Install dialog checks its fields | port 0 / x → "The port must be a number from 1 to 65535."; scope "abc" → "\"Allow connections from\" must be IP addresses or ranges …"; empty label → "Enter a device label."; the dialog stays open | U | 3 | ✅ |
| UI-AG-15 | Installed but this PC can't reach it | scope leaving out the admin's PC → after 5 tries "Installed, but this PC can't reach the agent on <host>:<port> (<error>). Check firewalls …"; still marked installed | U PS | 2 | ✅ |
| UI-AG-16 | Throttled sign-in in the UI | after repeated wrong passwords the tab shows "The RemoteHub Agent on <host>:58732 refuses sign-ins from this PC for a few minutes after too many failed attempts." | U | 3 | ✅ |
| UI-AG-17 | Uninstall from the UI | right-click → RemoteHub Agent → *Uninstall RemoteHub Agent…* → the question names "its credential provider, certificate and firewall rule" → *Uninstall* → "Uninstalled – <name> no longer runs the RemoteHub Agent.", *Done*; on the device no service, no Apps entry, no firewall rule, no credential provider, `%SystemRoot%\Temp\RemoteHub-Agent-uninstall.log`; the menu offers *Install RemoteHub Agent…* again (no *Update* / *Uninstall*) | U PS | 2 | ✅ |
| UI-AG-18 | Remove here only | the uninstall can't reach the agent (a certificate RemoteHub didn't make) → "The agent couldn't be uninstalled … Remove it from this connection in RemoteHub only? The agent stays on the device …" → *Remove here*: the connection forgets the agent, the device still runs it | U PS | 3 | ✅ |
| UI-JUMP-01 | RDP tab through a jump host | **2VM** + jump host: connection under the jump host → RemoteHub asks "Trust this RDP certificate?" ("<name> is reached through <jump> …", issued to the device's computer name, thumbprint) → *Trust and connect* → RDP signs in with NTLM through the forwarder (4624 NTLM), no certificate warning from the RDP client | U | 1 | ✅ |
| UI-JUMP-02 | RDP certificate pin | the second connect asks nothing; a different certificate → "RDP certificate changed" / *Trust the new one*; Cancel → no tab; closing the tab leaves no tab and RemoteHub answering | U | 2 | ✅ |

**Agent remote desktop**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-AD-01 | Open | *Remote desktop* opens the agent tab, session picker lists the device's sessions, Connected; tab picture equals a console screenshot of the device | U C | 1 | ✅ |
| UI-AD-02 | Control | click into the tab and type → Notepad on the device gets the text (incl. German layout case) | U S | 1 | ✅ |
| UI-AD-03 | Monitor / All monitors / Resolution menu | each choice changes the picture and the device resolution as in D-MON / D-FIT: *Fit to this window* follows the tab, *The device's own resolution* keeps it, *3840 × 2160* makes it larger than the tab (scaled down); resolution restored on disconnect | U PS | 2 | ✅ |
| UI-AD-04 | Key combinations | Alt+Tab and Win+R in the tab go to the device; Win+L stays local | U S | 2 | ✅ |
| UI-AD-05 | Ask | user Allows (OCR + console mouse on the device) → tab connects; Denies → "The user at the device didn't allow it." | U S | 1 | ✅ |
| UI-AD-06 | Show them a code | code read on the device, typed into RemoteHub's PIN box → connected; wrong → "That code isn't right. Try again." with "2 attempt(s) left." / "1 attempt(s) left.", then "The code was entered wrong too often." | U S | 1 | ✅ |
| UI-AD-07 | View-only | *View only – no mouse and keyboard* ticked on the connect page (no toggle once connected) → badge "View only", typing doesn't reach the device, no clipboard, Ctrl+Alt+Del off | U S | 1 | ✅ |
| UI-AD-08 | Use RDP instead | button opens an RDP tab to the same device | U | 2 | ✅ |
| UI-AD-09 | No usable session | nobody signed in, no SecureDesktop right → entries end " – your account may not see it" and are off, "Nobody is using a session right now, and your account may not see the sign-in or lock screen …", *Connect* off | U PS | 2 | ✅ |
| UI-AD-10 | Consent choices per session | own signed-in session: "Connect without asking (they see a banner)" offered even when the policy lacks None; locked / sign-in session: "When someone unlocks or signs in: ask them / show them a code / don't ask (they see a banner)" | U | 3 | ✅ |

**Copy and paste**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-CP-01 | Text admin → device (agent) | Ctrl+C in Notepad on the admin's PC, Ctrl+V in Notepad on the device through the tab → same text (Unicode, multi-line) | U S | 1 | ✅ |
| UI-CP-02 | Text device → admin (agent) | copy on the device, paste on the admin's PC → same text | U S | 1 | ✅ |
| UI-CP-03 | Image both ways (agent) | Paint / screenshot copied → pasted on the other side, pixels equal; from the device it is fetched on paste: "Pasted the image from the device." | U S | 2 | ✅ |
| UI-CP-04 | Files admin → device (agent) | copy 3 files + a folder in Explorer, paste in Explorer on the device → arrive, hashes equal; "Copying <n> files to the device" for 1 MB or more (the admin's PC's link limited to 20 Mbit/s so the progress stays up – Hyper-V limits only what a VM sends); after the device's receipt "Copied <n> files to the device – paste it there." | U S | 1 | ✅ |
| UI-CP-05 | Files device → admin (agent) | copy in Explorer on the device, paste in Explorer on the admin's PC → fetched on paste, "Pasted <n> files from the device.", hashes equal | U S | 1 | ✅ |
| UI-CP-06 | Too much | 101 MiB (the limit is 100 MiB) copied while connected → RemoteHub says to use the file transfer, nothing offered, nothing partial | U S | 2 | ✅ |
| UI-CP-07 | Policy text only | `DesktopClipboard=1` → text works, files refused with RemoteHub's notice "Copying images and files isn't allowed on this device – only text." | U S PS | 2 | ✅ |
| UI-CP-08 | Copy on the lock screen | session locked → no clipboard to or from the device until unlocked and allowed (the agent's service closes it while the lock, sign-in or Ctrl+Alt+Del screen is up; a copy offered then: "Nothing is copied while the device shows its lock or sign-in screen – copy again once it is unlocked.") | U S | 1 | ✅ |
| UI-CP-09 | Text both ways (RDP) | as UI-CP-01/02 through an RDP tab | U S | 1 | ✅ |
| UI-CP-10 | Files both ways (RDP) | Explorer copy/paste through an RDP tab, hashes equal | U S | 2 | ✅ |
| UI-CP-11 | Image both ways (RDP) | pixels equal | U S | 3 | ✅ |
| UI-CP-12 | Across sessions | copy in an RDP tab, paste in an agent tab to another device → arrives | U S | 3 | ✅ |
| UI-CP-13 | DesktopClipboard=2 (images) | an image reaches the device; a file copy is refused with "Copying files isn't allowed on this device." (the agent's wording; RemoteHub said "Copying images and files isn't allowed on this device – only text." up to 0.20.3) | U S PS | 3 | ✅ |
| UI-CP-14 | Copies from before connecting are quiet | a too-big or refused copy already on the clipboard when the tab goes live → no notice; the same copy while connected → the notice | U S | 3 | ✅ |
| UI-CP-15 | Cancel a copy to the device | 1 MB or more (99 MB, the admin's PC's link limited to 10 Mbit/s): "Copying <what> to the device", cancel → "Cancelled – nothing was copied to the device."; no receipt within 30 s → "Failed – the device didn't confirm the copy of <what>; it may not have arrived." | U S | 3 | ✅ |

**Ctrl+Alt+Del**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-SAS-01 | Agent tab, desktop | Ctrl+Alt+Del button → security options in the tab; Esc back | U | 1 | ✅ |
| UI-SAS-02 | Agent tab, **lock screen** | device locked → button → password field; password typed in the tab unlocks; the user who allowed the connection before isn't asked again – the tab continues (as for the own account; someone else is asked, D-LOCK-02) | U S C | 1 | ✅ |
| UI-SAS-03 | Agent tab, sign-in screen | nobody signed in → button → sign in as PSAdmin through the tab → desktop | U C | 1 | ✅ |
| UI-SAS-04 | Agent tab, UAC | UAC prompt on the device → visible in the tab, approved with an administrator's password typed in the tab (the prompt offers the console account with its password field) | U S | 1 | ✅ |
| UI-SAS-05 | Physical Ctrl+Alt+Del stays local | Ctrl+Alt+Del on the admin's PC (console keyboard) → admin's own security screen, nothing sent to the device | U C | 2 | ✅ |
| UI-SAS-06 | RDP tab, desktop | Ctrl+Alt+Del action → security screen in the RDP session | U | 1 | ✅ |
| UI-SAS-07 | RDP tab, **lock screen** | RDP session locked → Ctrl+Alt+Del action → password field, unlock by typing in the tab | U S | 1 | ✅ |

**Tools and network**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-TL-01 | PowerShell tab | `Get-Service` runs, Tab completion list opens, `exit` closes – over the agent and over WinRM HTTPS | U | 1 | ✅ |
| UI-TL-02 | File transfer tab | drag a folder from local to remote side → arrives (hash check on the device); download back | U PS | 1 | ✅ |
| UI-TL-03 | Registry tab | create, edit, delete a value under `HKLM\SOFTWARE\RemoteHubLab` → `reg query` on the device agrees | U PS | 2 | ✅ |
| UI-TL-04 | Services tab | stop / start Spooler → state on the device agrees | U PS | 2 | ✅ |
| UI-TL-05 | Processes tab | end a notepad on the device; lsass refused | U PS | 2 | ✅ |
| UI-TL-06 | Set up PowerShell remoting | *Set up on the target…* → script copied (*Copy script*) or saved (*Save as .ps1…*), run on the device (PS Direct), *Test again* → available over HTTPS | U PS | 2 | ✅ |
| UI-TL-07 | WinRM account refused | "PowerShell remoting answers on <name>, but the account was refused. <msg>", no *Set up on the target…* | U | 3 | ✅ |
| UI-TL-08 | Untrusted HTTPS from the remoting test | "<name> answers over HTTPS, but this PC doesn't trust its certificate yet. …" → *Check the certificate…* → "Trust this certificate?" / *Same – trust it*; declined → "Not trusted. Nothing was changed on this PC." | U | 2 | ✅ |
| UI-TL-09 | Copy script | *Test again* only after Copy or Save; "Copied. Open an elevated PowerShell on <host>, paste … Then click \"Test again\" …"; clipboard kept out of history, cleared after 120 s | U | 3 | ✅ |
| UI-TL-10 | File pane filter | *Filter This PC* "UpFol" → only matching rows, the first (UpFolder) selected; Enter opens it; the next folder starts unfiltered | U | 2 | ✅ |
| UI-TL-11 | Ctrl+F in a file pane | focus on the device's side, Ctrl+F → typing goes into that side's filter, not this PC's and not the navigation search | U | 3 | ✅ |
| UI-TL-12 | Registry value filter | *Filter values* "FromUi" selects the value; Enter opens its edit dialog with its data | U | 2 | ✅ |
| UI-TL-13 | Services filter | typing in the services filter selects the first match (the list allows one selection) – RemoteHub keeps running, the service's buttons are on | U | 1 | ✅ |
| UI-NET-01 | Agent tab reconnect | cut the device's network 10 s → the tab is back within 30 s without asking the user again (a short cut usually keeps the TCP connection; a dead one is noticed through the 30 s keep-alive and resumed, event 1103) | U N | 1 | ✅ |
| UI-NET-02 | RDP auto-reconnect | cut 10 s → RDP reconnects ("Connection lost – reconnecting (<n>/20)…" in the tab's tooltip), same session on the device | U N | 2 | ✅ |
| UI-NET-03 | Agent reconnect gives up | cut longer than 30 s (normal session) → "Connection lost – reconnecting…", "Attempt n – trying for up to 30 s.", then "The connection dropped and couldn't be restored within 30 s: <error>"; *Stop reconnecting* ends it at once ("Disconnected.", not the 30 s message; the lab allows 10 s after the click for its own UI round trips) | U N | 2 | ✅ |

### 10.6 Recordings

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-REC-01 | Record each tab type | RDP, agent desktop, PowerShell, file transfer, registry, services, processes → MP4 in `Videos\RemoteHub`, or the policy / settings folder, named `<connection> - <tab> - <yyyy-MM-dd HH-mm-ss>.mp4` (local time, ` (2)` on a clash), plays (Media Foundation reads duration > 0) | U PS | 2 | ✅ | 2VM |
| C-REC-02 | Settings | 30 fps / Full HD → file metadata matches | U PS | 3 | ✅ | 2VM |
| C-REC-03 | GPU capture | on Win11 no yellow border; Win10 records too | U | 3 | ✅ | 2VM |
| C-REC-04 | Unexpected stop | kill RemoteHub while recording → file still plays (fragmented MP4) | U PS | 2 | ✅ | 2VM |
| C-REC-05 | Stop time | last frame not after the stop (fix of 0.14.2) | U PS | 3 | ✅ | 2VM |
| C-REC-06 | N edition | clear message, no crash | U | 3 | ✅ | N |
| C-REC-07 | Record a network-share tab | `<connection> - <share name> - <date time>.mp4` | U PS | 3 | ✅ | 2VM |
| C-REC-08 | Tab hidden while recording | every frame of the hidden period shows "Tab not visible – recording continues when it is shown again" – not the remote screen | U PS | 3 | ✅ | 2VM |
| C-REC-09 | Normal exit while recording | recordings finish (up to 10 s), the file plays, no dialog | U PS | 2 | ✅ | 2VM |
| C-REC-10 | "Recording saved" | file, `mm:ss · size`, folder; Play / Show in folder / Close / *Delete recording…* (to the Recycle Bin) | U | 3 | ✅ | 2VM |
| C-REC-11 | Unsafe connection names | invalid characters → `_`, cut at 80 characters, a second file ` (2)` | PS | 3 | ✅ | |

### 10.7 Policies of both products and the Group Policy template (POL)

The template (`policy\RemoteHub.admx`, `en-US\RemoteHub.adml`) and the agent's phase 2 values
([admx-design.md](admx-design.md) §16, §17). The values are written to the registry the way the template writes them
(`Set-LabAgentPolicy`, or the C-POLUPD `Policy` helper for the console). The lab has no domain, so only POL-01 opens
the Group Policy editor. Agent cases run on every VM (`POL-AGENT.Tests.ps1`), and console cases on client VMs with
RemoteHub and the agent on the same VM (`POL-CLIENT.Tests.ps1`).

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| POL-01 | Template in the Group Policy editor | `RemoteHub.admx` and `en-US\RemoteHub.adml` from the build copied into `%WINDIR%\PolicyDefinitions`: `gpedit.msc` opens without an *Administrative Templates* error dialog, and *Computer Configuration → Administrative Templates* lists *RemoteHub* with both *RemoteHub Console* and *RemoteHub Agent* | PS S | 1 | ✅ | |
| POL-02 | Both products on one VM: each reads only its own key | agent `DesktopEnabled` = 0 → the desktop *to* this VM is refused (404, "turned off by a policy on the device"), and RemoteHub on the same VM still lists `DesktopEnabled` only under *RemoteHub Agent on this PC* on Settings → Policies; console `DisableRecording` = 1 at `…\Policies\RemoteHub` → the agent's `policy` output doesn't list it and the agent still allows everything | PS H U | 1 | ✅ | |
| POL-03 | `RemoteHubAgent.exe policy` | with `MaxSessions` = 50, `PowerShellEnabled` = 0, `ConsentTimeoutSeconds` = 9999 set: `MaxSessions 50 [policy]`, `PowerShellEnabled Off [policy]`, `ListenPort 58732 [agent.json]`, `ConsentTimeoutSeconds 30 [default]` plus "INVALID in the policy: 9999 is outside 5–600 – ignored"; every agent value of the template is listed; the policy key unreadable for SYSTEM (deny ACE) → "THE POLICY KEY CAN'T BE READ" | PS | 2 | ✅ | |
| POL-04 | New agent values apply without a restart | `MaxSessions` = 1: a second PowerShell session while one is held → 503 "limit of simultaneous sessions", and the log says "session limit (1)"; `MaxSessions` = 0 → ignored (default), "Policy MaxSessions: 0 is outside 1–200 – ignored" logged exactly once after two connections; `PrivacyScreenMessage` = "Contoso IT is working on this PC" → the device's screen (console screenshot, OCR) shows it on the privacy screen with message, and without the policy "Remote Support Session in Progress" | PS H C E | 2 | ✅ | |
| POL-05 | `PowerShellEnabled` = 0 | health's features have no `powershell`; a PowerShell session through the agent is refused before any sign-in (404, no failed-logon event 4625 for the try), the error says "turned off … by a policy on the device (PowerShellEnabled)", the log says "Refused PowerShell from … (policy PowerShellEnabled)"; the remote desktop still connects; policy removed → PowerShell works again without a restart | PS H E | 1 | ✅ | |
| POL-06 | `AllowedGroups` | `AllowedGroups` = a new local group with a new user in it: that user gets PowerShell and the desktop; PSAdmin (not in it) is refused (403) for both; `Rights\Desktop` = Administrators set as well → PSAdmin gets the desktop again but not PowerShell; `DesktopViewOnly`, `ConsentBypass` stay nobody; `AutoSignIn` follows the group like the other rights (automatic sign-in is on by default) | PS H | 1 | ✅ | |
| POL-07 | `ListenPort` and the firewall rule | `ListenPort` = 58800, service restarted: the agent listens on 58800 only, health with the pin answers there, the rule *RemoteHub Agent* has local port 58800 and the same remote address scope as before, the log says "moved to port 58800"; policy removed and restarted → 58732 again, rule back; `ListenPort` = 0 → ignored, 58732 | PS H E | 1 | ✅ | |
| POL-08 | Automatic sign-in moves out of the policy key on update | previous release installed with `AUTOSIGNIN=1` (it wrote `NoUserBehavior` and `Rights\AutoSignIn` into the policy key and `autosignin.policy`; once the previous release is 0.18.12 or later the test leaves those values and the marker itself), updated to the build under test: both values gone from the policy key, `agent.json` has `noUserBehavior` = AutoSignIn and `autoSignInGroups`, `autosignin.policy` gone, the log says "moved NoUserBehavior and Rights\AutoSignIn"; `policy` shows both as `[agent.json]`; automatic sign-in still works (harness `auto-sign-in`). Variant: `NoUserBehavior` set to SignInScreen by IT before the update → it stays in the policy key, the log says "left in the policy key", automatic sign-in is refused | PS H E | 1 | ✅ | |
| POL-09 | What the device's policy decides, in health | `ListenPort`, `VirtualDisplay` and `NoUserBehavior` set: health's `managed` = `autoSignIn`, `virtualDisplay`, `port` – nothing else, no values; one invalid (`ListenPort` = 0) → not named; none set → no `managed` | PS H | 2 | ✅ | |
| POL-10 | Intune import | the ADMX imports in Intune (*Devices → Configuration → Import ADMX*) without a prerequisite, and a profile from it writes the values to a test device | – | 2 | ○ | Intune tenant (manual) |
| POL-11 | Settings → Policies | HKLM `DisableTelemetry` = 1, HKCU `AuditRetentionDays` = 0, agent `DesktopClipboard` = 1: the page says "2 settings of RemoteHub on this PC are set by your organisation (1 for the computer, 1 for your user account)" (the invalid one counts), lists *Turn off usage data and crash reports: Turned off* with "Computer · HKLM\SOFTWARE\Policies\RemoteHub\DisableTelemetry", shows "AuditRetentionDays 0 is outside 1–36500 – ignored", and lists *Clipboard: Text* under *RemoteHub Agent on this PC*; *Copy as text* puts the same on the clipboard; a value changed while the page is open shows after *Refresh*; nothing set → "No policies are set for RemoteHub on this PC"; *Settings overview* has the *Policies* section and *Show policies…* opens the page | PS U | 2 | ✅ | |
| POL-12 | Set by policy = greyed out, not hidden | with `DisableTelemetry`, `UpdateChannel`, `AuditRetentionDays` = 90 and `RecordingFolder` set: the usage data toggle, the GA channel button, *Keep entries for* (showing the policy's 90 days) and *Change folder…* are there but disabled; without the policies they are there and enabled | PS U | 2 | ✅ | |
| POL-13 | What the device's policy decides, in RemoteHub's install dialog | the device's `ListenPort` set: RemoteHub's *Update RemoteHub Agent…* for it shows "Set by the device's policy (ListenPort)" under *Port*, the field greyed out even with *Enroll again* ticked; `VirtualDisplay` / `NoUserBehavior` set: the hints of *Virtual display* and *Allow automatic sign-in* say the device's policy decides when they're used, both still changeable. Needs RemoteHub to have installed the agent itself (the UI-AGENT flow) | U H | 2 | ○ | 2VM |
| POL-14 | Console features: hidden and refused (admx-design.md §5, phase 3) | HKLM `AllowAgent` = 0 with a connection that has the agent: no *Remote desktop via RemoteHub Agent*, *Install / Update RemoteHub Agent…* or credential provider entry in the tree and *+* menus; *Connect through* and *Remote desktop through* greyed out with "Turned off by your organisation"; the tools go over WinRM (*Tools* offers *Test PowerShell remoting*). `AllowPowerShellRemoting` = 0 (agent allowed): no WinRM tools, test or setup entries for a WinRM connection, the agent's tools still offered. `AllowAgentFileTransfer` = 0: *File transfer* gone for an agent connection, still there over WinRM. `AllowRdp`, `AllowNetworkShares`, `AllowRdpFileImport`, `AllowBackupExport` = 0 hide their entries (*Connect*, *Add network share…* / the share kinds under *New → New connection* – *SSH* stays, it has a switch of its own –, *Import .rdp files…*, *Export backup…* – *Import backup…* stays); an RDP tab opened before the policy refuses *Reconnect* in the tab, and quick connect with `AllowQuickConnect` = 0 shows "Quick connect is turned off by your organisation". Settings → Policies marks `AllowAgentDesktop` = 1 with *Off because "Allow the RemoteHub Agent" is off*. All read while RemoteHub runs | U | 1 | ✅ | |
| POL-15 | Settings and connection settings by policy (admx-design.md §6, §7, phase 4) | HKLM `Connections\RedirectDrives` = 0 with a connection that has *Local drives* on: *Local drives* shows *Off*, greyed out with "Set by your organisation", in the connection's editor, a folder's and the global defaults; the `.rdp` file RemoteHub writes for it has no drives (`drivestoredirect:s:` empty – the RDP session uses the same resolved settings; its drives aren't looked at, one VM has no RDP target); saving the connection keeps its own *On* (without the policy the editor shows *On* and the file shares drives again). `Theme` = Light: Settings → General shows *Light* greyed out and the window is light. `DisableTelemetry` = 0 with usage data off: the switch is on and greyed out. `AgentDesktopViewOnly` = 1: the start panel's *View only* is ticked and greyed out, and the session shows *View only* without Ctrl+Alt+Del. `AgentInstall\Methods` = Clipboard, SaveFiles: *Install silently over PowerShell remoting* is there but greyed out. `Connections\RemoteDesktopVia` = Agent with `AllowAgentDesktop` = 0: Settings → Policies shows the conflict ("…so there is no remote desktop"; the amber isn't checked), and *Connect* says the agent desktop is turned off – no tab | U | 1 | ✅ | |
| POL-16 | New console behaviour by policy (admx-design.md §5.4, §5.6, §6.3–§6.5, phase 5) | `AllowConnectionEditing` = 0: no *Edit*, *Duplicate*, *Delete*, *Move under* or *New* in the tree menu, no *New* button, F2 and Del say "Changing folders, connections and shares is turned off by your organisation", a drag onto a folder is refused, connecting still works. `AllowSavedPasswords` = 0 with a connection that has a stored password: at start "… 1 were removed", the password box is greyed out, the RDP sign-in asks for the password. `StartupProtection` = Required without protection: at start only *Startup protection* opens; closing it ends RemoteHub; with a password set, *Turn off* is greyed out. `AllowWindowsHello` = 0: the unlock window has no *Windows Hello*. `DiagnosticLog` = 0: `remotehub.log` gets no new lines; Settings → Logs says "Turned off by your organisation" | U | 1 | ○ | |

### 10.8 Notes (NOTE)

The Notes view ([notes-view-design.md](notes-view-design.md)) in RemoteHub's window on a client VM, driven by UI
Automation in the console account's session; the clipboard is set and read in that session (`clip-set`, `clip-get`).
One VM, no device. The cases build on each other in `UI-NOTES.Tests.ps1`.

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| UI-NOTE-01 | View rail | *Notes* on the rail shows the Notes view (*Add page*), *Remote Control* the connection list again, *Notes* the notebooks again | U | 2 | ✅ | – |
| UI-NOTE-02 | New page | *Add page* in the first section (*Quick Notes*), the title typed into *Page title* → the page is in the section's list | U | 2 | ✅ | – |
| UI-NOTE-03 | Paste into a script block | *Script block* from the format bar, a PowerShell line on the clipboard, Ctrl+V in the block → the block has the line (the page's own paste handling leaves it alone) | U | 2 | ✅ | – |
| UI-NOTE-04 | View Markdown | *View Markdown* on → the *Markdown* text has the page as Markdown, the script in a ```` ``` ```` fence; *Bold* is greyed out; the toggle reads On | U | 2 | ✅ | – |
| UI-NOTE-05 | Edit in View Markdown | `## Lab heading` added to the *Markdown* text, *View Markdown* off → the formatted page has "Lab heading" as text (no `##`); the toggle reads Off | U | 2 | ✅ | – |
| UI-NOTE-06 | Copy page as Markdown | *Copy page as Markdown* → the clipboard holds Markdown text starting `# <title>`, with the script | U | 3 | ✅ | – |
| UI-NOTE-07 | Kept after a restart | RemoteHub closed and started → Notes shows again (the last view), the page is listed, *Search notes* "Spooler" finds it, the script block still has the script, View Markdown is off | U | 2 | ✅ | – |

End to end, with what is stored, on the Console VM as the console account (C-NOTE; it overlaps UI-NOTE-01 and -02):
typing through UI Automation values, the session's clipboard and SendKeys in the session; the stored notes read in that
session with RemoteHub's own `sqlite3mc.dll` (`Invoke-LabClientSql`). `C-NOTES.Tests.ps1`.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-NOTE-01 | View rail | a new profile starts in Remote Control; rail with Remote Control, Notes, Settings; the rail, Ctrl+1 and Ctrl+2 switch views (the other one hidden); first notebook "Notes" with "Quick Notes"; after a restart Notes is shown again (`LastView`) | U | 1 | ✅ |
| C-NOTE-02 | Notebook, section, page | made through *New notebook*, *+ New section* and *Add page*; stored under each other; saved without a Save button; `note.added` audited by id; listed again after a restart | U S | 1 | ✅ |
| C-NOTE-03 | Typed formats and autosave | `- `, `# ` at a line start and Ctrl+B become a list, a heading and bold; stored as `- …`, `# …`, `**…**` a second after typing; the search text has no Markdown markers; *Copy page as Markdown* = `# title` + what is stored | U S | 2 | ✅ |
| C-NOTE-04 | Round trip of every element | *Paste Markdown* of a table (with alignment), bold, italic, strikethrough, inline code, link, check list, quote, SQL block, numbered list, plus HTML as a browser copies it (bold and table kept; style, script and remote image dropped, its alt text kept); after a restart the Markdown is byte-identical, the editor shows it and gives it back; no page is written by the restart or by opening it | U S | 1 | ✅ |
| C-NOTE-05 | Script block, documentation only | pasted code becomes a block "Language: PowerShell", fenced `powershell`, stored and copied exactly (also after the restart); no *Run* / *Send to* in the block's or the page's menu; the block's command was never run (its marker file doesn't exist) | U S PS | 2 | ✅ |
| C-NOTE-06 | Images | a picture pasted from the clipboard and a JPEG pasted as a file stored in `note_files` as PNG with their sizes, the JPEG's comment segment gone; the page refers to both by id (`Pasted image`, `photo-c6`); shown again after a restart | U S | 2 | ✅ |
| C-NOTE-07 | Notes at rest | no title, section name, text, table cell, script or pasted HTML word (UTF-8 / UTF-16) in any file under `%LOCALAPPDATA%\RemoteHub`, `%APPDATA%\RemoteHub` or `%TEMP%`, while RemoteHub runs (WAL included) and after it closed; the database file is not plain SQLite; the audit log has none of them | PS S | 1 | ✅ |
| C-NOTE-08 | Delete, Undo, Recently deleted | "Page deleted" with *Undo* brings the page back; the toast goes after about 8 s; *Recently deleted → Restore* brings it back; `note.deleted` / `note.restored` audited by id | U S | 2 | ✅ |
| C-NOTE-09 | A page deleted before its section | the page keeps its own, earlier stamp; *Delete now* on the section removes the section and its own pages only; the earlier page is still listed and restores into a live section (PR #37) | U S | 2 | ✅ |
| C-NOTE-10 | Search | a table cell's word, its prefix and a word in a script block each find their page ("1 page found"); a deleted page's word and a typed `"` find nothing ("No pages found"), without an error | U | 3 | ✅ |
| C-NOTE-11 | `AllowNotes` policy | set while RemoteHub runs: when the window is next activated the Notes item and Ctrl+2 are gone and an open Notes view leaves; after a restart Remote Control; notes kept; without the policy the saved last view (Notes) comes back | U PS | 2 | ✅ |
| C-NOTE-12 | Notes in a backup | the `.rhbackup` holds none of the note words; "Backup exported" counts the pages; imported on a fresh profile with *Add as folder*: notebooks "<name> (c-notes)" with every page's Markdown and the images (same SHA-256); "Backup imported" "Added as a folder, and N pages" | U C S | 2 | ✅ |

### 10.9 First start and navigation

RemoteHub alone in the client VM (no device), driven by UI Automation in `Admin`'s session.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-FIRST-01 | Welcome tour on a new install | the first start of a new install opens "Welcome to RemoteHub" (its first page has no *Back*) | U | 2 | ✅ |
| C-FIRST-02 | Tour pages | Next goes through 5 pages (welcome, *Add your connections*, *The RemoteHub Agent*, *Notes*, *Keep it safe*); the last button says *Get started* and closes it | U | 2 | ✅ |
| C-FIRST-03 | Shown once | the next start opens neither the tour nor What's new | U | 2 | ✅ |
| C-FIRST-04 | From Help & about | *Settings → Help & about → Welcome tour…* opens the tour, *What's new…* opens "What's new in RemoteHub <newest entry>" with "Installed: <version>" under it (a patch without an entry of its own shows the last one there was) | U | 3 | ✅ |
| C-FIRST-05 | Pages left out by policy | `AllowNotes` = 0 and `AllowAgent` = 0 → the tour has 3 pages, without Notes and the agent | U PS | 3 | ✅ |
| C-FIRST-06 | What's new after an upgrade | the previous release with a connection, then this one: the first start shows What's new (not the tour) for the entries newer than that release – "What's new in RemoteHub <newest entry>" and *All release notes* – or, with none newer (previous 0.21.0, newest entry 0.21.0), nothing opens | U PS | 2 | ✅ |
| C-FIRST-07 | What's new once | the next start shows nothing | U | 2 | ✅ |
| C-FAV-01 | Pin a connection | a connection in folder *Lab* → right-click → *Add to favourites*: a *Favourites* group at the top with a link (a chip); the connection stays in *Lab* (its name shows twice) | U | 2 | ✅ |
| C-FAV-02 | The link's menu and Edit | the link's menu offers *Remove from favourites*, no *Delete* / *Duplicate*; F2 on the link opens *Edit connection* for the connection itself | U | 2 | ✅ |
| C-FAV-03 | Kept after a restart | the Favourites group is there after RemoteHub restarts | U | 2 | ✅ |
| C-FAV-04 | Del unpins | Del on the link removes it without a question; the connection stays | U | 2 | ✅ |
| C-FAV-05 | Deleted with its folder | deleting *Lab* removes the link too | U | 3 | ✅ |
| C-NAV-01 | One-line rows | a connection's row takes one line (name, address, the last use short: "never"); *Compact rows* off in the filter menu: the address and the full last use ("never connected") on a second line; kept after a restart; on again: one line | U | 2 | ◐ |
| C-NAV-02 | A submenu that opens upwards | the connection moved 150 px above the bottom of the screen, right-click → *New* → *Connection*: the kinds (Remote Control, SSH, SFTP, FTP / FTPS, SMB, WebDAV) open upwards and their last entry ends within 16 px of *Connection*'s bottom, beside it (before the fix 50–60 px higher; `RhPerson.ps1 -Do uia -Value place:New>Connection`) | U | 2 | ◐ |
| C-RAIL-01 | The rail's version | clicking the version on the rail opens *About RemoteHub* (not the update window) | U | 3 | ✅ |

## 11. Report in the PR

`lab\New-LabReport.ps1` reads `run.json` (packages with SHA-256, each VM's checkpoint) and every `pester.xml` of a
run plus this sheet, and writes `artifacts\lab\<run>\report.md`, pasted into (or attached to) the PR. `Start-LabRun.ps1`
runs it after every run; by hand: `.\lab\New-LabReport.ps1 [-Run <id>] [-AllowNotAutomated]`.

```markdown
### Lab report – run 20261012-125322
Agent: RemoteHub-Agent-0.16.5-x64.msi (SHA-256 1A2B…) · Client: RemoteHub-0.16.5-x64.msi (SHA-256 3C4D…)
VMs: W11-26H2-en-US (checkpoint 2026-10-11 14:02) · W10-22H2-en-US (…)

| Area | Cases | W11-26H2-en-US | W10-22H2-en-US | … |
|---|---|---|---|---|
| A – install / update / uninstall | 68 | 68 ✅ | 67 ✅ 1 ⏭ | |
| D – remote desktop | 140 | … | … | |

Failed: none · Flaky: D-RES-04 (W10, passed on rerun – network cut took 70 s) · Skipped: D-VID-08 (needs GPU-P VM)
Not automated: 12 (listed below)
```

- One line per area, then the full case × VM table collapsed (`<details>`).
- The report fails (exit ≠ 0) when the gate in §1 isn't met, so it can later become a required check. Results whose
  test name has no case ID, or an ID that isn't in this sheet, also fail it – the sheet and the tests stay in step.
- Not yet: flaky detection (the rerun of a failed case, §1) – rerun by hand with `-Tag <case>` for now.
- `summary.json` and the NUnit files stay in `artifacts\lab\<run>\` as raw evidence.

## 12. What the lab needs to automate all of this

| Building block | Used by | Effort |
|---|---|---|
| Case IDs in every `It` name, `-Tag <ID>` per case | all | small |
| `New-LabReport.ps1` (§11) | gate | small |
| `Set-LabAgentPolicy` / `Clear-LabAgentPolicy` (policy key + Rights) | S, D | small |
| `Update-LabAgent` (harness `StartUpdateThroughAgent`), previous-release MSI in `LabRoot\msi\previous` | A-UPD | small |
| Harness: PowerShell over the agent (`AgentTransport` runspace) + raw HTTP/WebSocket probes (status codes, TLS versions, bad headers) | S, P, T | medium |
| Harness: remote tools classes over agent and WinRM | T | medium |
| `Invoke-LabSetupScript` (generate with `RemotingSetup`, run in the guest) | W | small |
| Harness: headless desktop client (`AgentDesktopSession` without WPF, frames → PNG, input, clipboard, Pin, resume) | D | large |
| In-session helpers: UIA reader/clicker for the consent, PIN and banner windows; marker app that records keys, mouse and clipboard | D | medium |
| Network cut / throttle (`Disconnect-VMNetworkAdapter`, bandwidth limit) | P, D | small |
| Event log / Security log collector (`Save-LabEvidence`) | all | small |
| FlaUI driver for RemoteHub.exe in a VM session | C, R | large |
| Mock update / telemetry server + lab root CA + hosts entry | C-UPD, C-TEL, C-CRASH | medium |
| SMB share on the host for the lab | C-SHARE | small |
| More VMs (§2) | matrix | owner |

Suggested order: report + IDs → policy/update helpers → harness PowerShell/probes (S, P, T, W) → headless desktop
client + in-session helpers (D) → FlaUI + mock server (C, R).

## 13. Security – attack cases (SEC)

The lab plays the attacker ([security-test-lab-design.md](security-test-lab-design.md) §4). These are the cases whose fix is in the
code; the design's other `SEC-*` cases follow with their test files. Unit tests cover the same fixes without a VM
(`UpdateTelemetryTests`, `VirtualDisplayTests`, `AgentEnrollmentTests`, `CoreTests`, `JumpHostTests`, `RemotingSetupTests`, `BuildScriptTests`,
`DecoderProcessTests`, `DeviceImageTests`).

| ID | Case | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| SEC-UPD-01 | Update with the right checksum, broken signature | the mock announces 99.0.0 with a rebuilt MSI (`New-LabRebuiltMsi`: right `sha256` and `size`, signature no longer valid) → "The downloaded installer has a signature that doesn't match the file or that this PC doesn't trust. It was deleted and won't be installed."; Windows never asks for approval; no `.msi` left in `%TEMP%\RemoteHub\Updates` (`C-POLUPD2.Tests.ps1`) | U M PS | 1 | ✅ | lab signing certificate |
| SEC-UPD-02 | Download swapped during the approval prompt | while Windows asks for approval, the MSI in the user's `%TEMP%` is replaced by the rebuild; after *Yes*: nothing installed (version unchanged), RemoteHub starts again and says "…the downloaded file changed after RemoteHub had checked it…", the step's outcome is `refused changed`, no copy left in `C:\Program Files\RemoteHub Updates`, whose permissions are SYSTEM and Administrators only (not inherited). Without a swap the step installs its own copy (`installed 0`) and RemoteHub starts again without a message (`C-POLUPD2.Tests.ps1`) | U M C PS | 1 | ✅ | lab signing certificate |
| SEC-UPD-04 | The user's environment and the elevated step | `COR_ENABLE_PROFILING`, `COR_PROFILER` and `COR_PROFILER_PATH` set in the console account's `HKCU\Environment` before RemoteHub starts (a .NET profiler for every program of theirs, elevated ones too): after *Yes* no .NET Runtime event names the step's process as trying to load it – RemoteHubUpdate.exe gives Windows PowerShell an environment of system folders only; a Windows PowerShell started with the same values does write that event (the check can see it) (`C-POLUPD2.Tests.ps1`) | U M C PS E | 1 | ◐ | lab signing certificate |
| SEC-UPD-05 | RemoteHubUpdate.exe called directly | run by an administrator with values it doesn't take – a checksum one character short, an extra `--publisher`, a path with a stream (`a:b.msi`), a UNC path, a `..` segment – it ends with exit code 0, starts nothing and writes no outcome; with well-formed values for the rebuilt MSI it starts the step (its exit code is the step's process id), whose outcome is `refused signature`, and no copy is left; a copy in `C:\Users\Public` with a planted `InstallUpdate.ps1` next to it ends with exit code 0 and the planted script doesn't run (`C-POLUPD2.Tests.ps1`) | PS M | 1 | ◐ | lab signing certificate |
| SEC-VDD-01 | Rogue display-driver pipe | normaluser serves `MTTVirtualDisplayPipe` (4 instances) while the virtual display is on; a second connection with another size makes the agent reload the driver: no rogue instance receives a byte, none can act as SYSTEM (anonymous connection); a refused reload says "served by another program" in the agent log (`SEC-VDD.Tests.ps1`) | H S PS E | 1 | ✅ | normaluser signed in |
| SEC-VDD-02 | Settings folder at the drive root | `C:\VirtualDisplayDriver` after the first connection: no reparse point, permissions not inherited, owner SYSTEM or Administrators, only they may write; normaluser can neither remove it nor create a file in it; a junction planted in its place (as an older build could leave it) gets nothing written through it and is replaced by the agent's own folder with the next connection's settings (`SEC-VDD.Tests.ps1`) | H PS | 1 | ✅ | |
| SEC-SH-02 | SMB signing with RemoteHub's own client | the server set to `RequireSecuritySignature = $false`: the second account (RemoteHub's client) still copies (14 files) – its requests are signed and answers without a valid signature are dropped, so it works only because the server signs (`S-SHARE.Tests.ps1`) | H PS | 1 | ✅ | Server |
| SEC-LOG-01 | Nothing identifying in DiagLog | after the jump-host file's sessions, forwarders and shares, this PC's `remotehub.log` names neither the device, its address, the jump host's address, the account nor the password; the forwarders log by an 8-character ID (`J-JUMP.Tests.ps1`) | H E | 1 | ✅ | jump host |
| SEC-DEC-06 | The decoder sandbox on the installed build | `RemoteHub.exe --decoder-check` run by normaluser: `RemoteHubDecoder.exe` starts from Program Files in an AppContainer, shows the pictures of an H.264 test stream, at most one of 30 only with the next (mean difference < 5) and decodes a PNG, BMP and JPEG to their 64 × 48 pixels; its probes find network, loopback, the profile, RemoteHub's database folder, writing, HKCU, the clipboard, a new process, RemoteHub's process and other windows all blocked – decoder-isolation-design.md (`SEC-DEC.Tests.ps1`) | PS U | 1 | ◐ | normaluser signed in |
| SEC-ENR-03 | A copied enrollment file | one `.rhagent`, copied before it was used, installed on the VM and on a second device: different device IDs and certificates, neither key exportable, no `pfx` or `deviceId` in the file; the claim (harness `claim`, `AgentInstaller.ClaimAsync`) pins each as itself; the second device under the first one's computer name → `OtherName`, the first one's pin refused by the second; after *Replace and revoke* both → `RevokedSecret` (`SEC-ENR.Tests.ps1`) | H PS | 1 | ✅ | second device |
