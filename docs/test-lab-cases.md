# RemoteHub Test Lab – End-to-end test sheet: Windows client and desktop

Status: v1 (2026-10-12), written against RemoteHub / RemoteHub Agent **0.16.5**. Scope: everything RemoteHub and the
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
| Recording of each tab type | – | C-REC-* |

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
| **St** | ✅ automated · ○ planned |
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
| SMOKE-08 | Clock | guest time within 2 min of the host (certificate validity, log times) | PS | 2 | ○ |
| SMOKE-09 | No pending reboot / update | no `RebootPending`, TiWorker idle within 5 min; warning in the report otherwise | PS | 3 | ○ |
| SMOKE-10 | Keyboard layout known | the report records the layout; `Send-LabText` round-trips `a-zA-Z0-9 !@#$%` into Notepad | S C | 2 | ○ |

## 5. Agent – install, update, uninstall (A)

### 5.1 First install

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-INST-01 | Install over remoting (`InstallOverRemoting`) | msiexec exit 0/3010; fingerprint read back = pin; deviceId and port match | H | 1 | ✅ |
| A-INST-02 | Service | `RemoteHubAgent` running, LocalSystem, Automatic (Delayed Start) | PS | 1 | ✅ |
| A-INST-03 | Health | `/v1/health` over TLS pinned: product, version = MSI version, protocol 1, deviceId, features `powershell`,`desktop` | H | 1 | ✅ |
| A-INST-04 | Certificate | LocalMachine\My, SHA-256 = pin, key not exportable, `CN=<deviceId>, O=RemoteHub Agent`, FriendlyName "RemoteHub Agent", RSA 3072, valid 2 years, EKU server auth | PS | 1 | ✅ |
| A-INST-05 | agent.json | deviceId, label, port, thumbprint, allowedGroups = S-1-5-32-544, virtualDisplay, logRetentionDays 30, maxSessions 20 | PS | 1 | ✅ |
| A-INST-06 | Firewall | exactly one rule "RemoteHub Agent": inbound, allow, TCP 58732, program = RemoteHubAgent.exe, remote address = host, all profiles | PS | 1 | ✅ |
| A-INST-07 | Folder ACL | `%ProgramData%\RemoteHub\Agent`: owner Administrators, protected, only SYSTEM + Administrators | PS | 1 | ✅ |
| A-INST-08 | No secrets left | no `.rhagent`, no PFX, no `RemoteHub-Agent-*` staging folder in the user's or Windows temp; `C:\Windows\Installer` copy contains no PFX | PS | 1 | ✅ (partly) |
| A-INST-09 | Event source | "RemoteHub Agent" registered; start line in the log with port, device and certificate | PS E | 2 | ✅ |
| A-INST-10 | Recovery actions | `sc qfailure`: restart ×3, 10 s, reset after 1 day; kill the process → service back within 30 s | PS | 2 | ○ |
| A-INST-11 | Service survives a reboot | after `Restart-LabVm` health answers within 3 min (delayed start) with the same certificate | PS H | 1 | ○ |
| A-INST-12 | Nothing extracted | no `.net` folder in `C:\Windows\Temp` or the SYSTEM profile; native DLLs only in Program Files | PS | 2 | ○ |
| A-INST-13 | Firewall scope Any | install with empty scope → rule remote address Any | H PS | 2 | ○ |
| A-INST-14 | Firewall scope list | scope `10.0.0.0/8, 192.168.5.10-192.168.5.20, <host>` → rule has exactly those; connection from the host works | H PS | 2 | ○ |
| A-INST-15 | Other port | port 58800 → service listens there, rule for 58800, health works on 58800, nothing on 58732 | H PS | 2 | ○ |
| A-INST-16 | Allowed groups by name | dialog value "Administrators" → stored as S-1-5-32-544 (also on de-DE, where the group is "Administratoren") | H PS | 1 | ○ |
| A-INST-17 | Custom allowed group | allowedGroups = a local group "RH Operators" → member normaluser can sign in, PSAdmin (not a member) gets 403 | H PS | 2 | ○ |
| A-INST-18 | Label with spaces / umlauts | label `Büro PC 7` → enrollment works, label in agent.json and Hello | H | 3 | ○ |
| A-INST-19 | Virtual display off at install | `VIRTUALDISPLAY=0` → no `Root\MttVDD` device, no driver package, no SignPath trust | H PS | 2 | ○ |
| A-INST-20 | Virtual display on at install | device installed and **disabled**, driver files hash-checked, SignPath thumbprint `3CF8…7D76` in TrustedPublisher, `virtual-display.txt` records `trustAdded` | H PS | 2 | ○ |
| A-INST-21 | Trust already present | thumbprint in TrustedPublisher before install → `trustAdded=false`; uninstall leaves it | PS | 3 | ○ |
| A-INST-22 | SoftwareSASGeneration set | no policy before → value 1 after service start, marker `sas.agent` present | PS | 1 | ○ |
| A-INST-23 | SAS policy respected | value 0 set by "policy" before install (no marker) → stays 0, Ctrl+Alt+Del in the desktop tab explains it (D-SAS-04) | PS H | 2 | ○ |
| A-INST-24 | selftest on the device | `RemoteHubAgent.exe selftest` → "Self-test passed.", exit 0, nothing changed (no log line, no cert) | PS | 2 | ○ |
| A-INST-25 | Port in use | another listener on 58732, then install → install fails cleanly or service doesn't start; log says "could not start", no silent port change | PS H | 2 | ○ |
| A-INST-26 | Install log for failures | force a failure (corrupt `.rhagent`) → RemoteHub's error shows the `WixQuietExec`/`Error` lines from the msiexec log | H | 2 | ○ |

### 5.2 Other install paths

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| A-HAND-01 | MSI by hand next to the `.rhagent` | copy both to a folder, run msiexec as admin in Admin's session → installed; `WaitForManualInstall` sees it within 10 s polling | H S | 1 | ○ | |
| A-HAND-02 | Two `.rhagent` files in the folder | install fails with "More than one .rhagent file", nothing enrolled, rollback clean | PS | 2 | ○ | |
| A-HAND-03 | No `.rhagent` and no config | install fails, nothing left (rollback runs `cleanup`) | PS | 1 | ○ | |
| A-HAND-04 | `ENROLLMENT=<path>` property | file in another folder → used; file deleted afterwards | PS | 2 | ○ | |
| A-HAND-05 | Enrollment format 2 / bad GUID / port 0 / empty groups | each refused with a clear message, install rolled back | PS | 2 | ○ | |
| A-HAND-06 | Interactive install (UI, not /qn) | install dialog driven by UIA; virtual-display checkbox respected | S | 3 | ○ | |
| A-CLIP-01 | Clipboard install from RemoteHub | **2VM**: RemoteHub puts MSI + `.rhagent` on the clipboard, pasted into the RDP session, MSI run there → connection marked installed on first health | U | 2 | ○ | 2VM |
| A-CLIP-02 | Staging cleanup on the admin's PC | `%TEMP%\RemoteHub-Agent\<id>` gone after RemoteHub exits and after the next start (crash case) | U PS | 1 | ○ | 2VM |
| A-CLIP-03 | Ask before sending the key through a desktop tab | agent desktop tab to another device open → RemoteHub asks before the clipboard install (was D-DESKCLIP) | U | 1 | ○ | 2VM |

### 5.3 Update, repair, downgrade

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-UPD-01 | Update through the agent (`StartUpdateThroughAgent`) | previous release installed, update to the build under test → agent down within 90 s, back within 3 min with the new version, same certificate | H | 1 | ○ |
| A-UPD-02 | Temp folder removed | `RemoteHub-Agent-<hex>` folder gone after msiexec ends (within 3 min); `%TEMP%\RemoteHub-Agent-update.log` of the signing-in account stays and says the install succeeded (RemoteHub points to it on failures) | PS | 2 | ○ |
| A-UPD-03 | Identity kept | deviceId, thumbprint, port, firewall scope, allowedGroups unchanged; no duplicate certificates | PS | 1 | ○ |
| A-UPD-04 | New device certificate on update | "Create a new device certificate" → new pin works, old certificate gone, old pin refused | H PS | 1 | ○ |
| A-UPD-05 | Update over remoting | same as A-UPD-01 but through `InstallOverRemoting` | H | 2 | ○ |
| A-UPD-06 | Update keeps policies and logs | policy key and old log files untouched | PS | 2 | ○ |
| A-UPD-07 | Update with virtual display change | `VIRTUALDISPLAY=0` on update removes the driver; empty keeps it | H PS | 3 | ○ |
| A-UPD-08 | Update during an open PowerShell session | the session open during the update has ended, update completes, a new session (new PowerShell process) works | H | 2 | ○ |
| A-UPD-09 | Update during an open desktop session | desktop session ends, helper process gone, banner gone, new session works | H S | 2 | ○ |
| A-UPDFAIL-01 | Broken MSI through the agent | package with a false launch condition (same version) → RemoteHub reports "didn't install … still runs <old>" (not "updated"); old agent answers with the old certificate | H | 1 | ○ |
| A-UPDFAIL-02 | Upgrade that fails in enroll | damaged `.rhagent` sent with the update → enroll fails after the old version was removed, upgrade rolls back: old version running, old enrollment kept (rollback `cleanup` not run on upgrade), no staging folder | PS H | 1 | ○ |
| A-UPDFAIL-03 | Update that never comes back | block the new service (port in use) → RemoteHub times out after 3 min and says so | H | 2 | ○ |
| A-UPDFAIL-04 | File that isn't a package | truncated MSI through the agent → reported as failed, never as "updated" (the version can't be read from it); old agent keeps running | H | 1 | ○ |
| A-SAME-01 | Reinstall the same version | through the agent → reported updated, identity kept | H | 3 | ○ |
| A-SAME-02 | Same version with a new device certificate | "Install again with Create a new device certificate" (RemoteHub's advice on a pin mismatch) → new certificate answers, old one refused, one certificate left | H | 2 | ○ |
| A-DOWN-01 | Downgrade | older MSI over newer → refused: "A newer version of the RemoteHub Agent is already installed.", newer still running | PS | 2 | ○ |
| A-REPAIR-01 | `msiexec /fa` | repair succeeds, identity kept, service running | PS | 3 | ○ |
| A-ROLLBACK-01 | First install fails after enrolling | forced failure after AgentEnroll → no rule, no certificate, no agent.json, no service | PS | 1 | ○ |

### 5.4 Uninstall

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-UNINST-01 | Installed before | health answers | H | 1 | ✅ |
| A-UNINST-02 | `msiexec /x /qn` | exit 0 / 3010 | PS | 1 | ✅ |
| A-UNINST-03 | Product, service, folder | all gone | PS | 1 | ✅ |
| A-UNINST-04 | Firewall rule | gone | PS | 1 | ✅ |
| A-UNINST-05 | Certificates | no `O=RemoteHub Agent` certificate in LocalMachine\My | PS | 1 | ✅ |
| A-UNINST-06 | agent.json | gone | PS | 1 | ✅ |
| A-UNINST-07 | Logs kept | log folder stays, ends with "Uninstalled: firewall rule, certificate … removed" | PS | 2 | ✅ |
| A-UNINST-08 | Event source | gone | PS | 2 | ✅ |
| A-UNINST-09 | Port | nothing answers on 58732 | H | 1 | ✅ |
| A-UNINST-10 | SoftwareSASGeneration | back to the value before install (deleted when the agent set it, kept when a policy set it) | PS | 1 | ✅ |
| A-UNINST-11 | Virtual display removed | device, driver package, `C:\VirtualDisplayDriver`, `virtual-display.txt` gone; SignPath trust removed only if the agent added it | PS | 2 | ○ |
| A-UNINST-12 | Uninstall from Settings → Apps | UIA uninstall in Admin's session → same result as A-UNINST-02…11 | S | 3 | ○ |
| A-UNINST-13 | Uninstall while a desktop session is open | session ends, helper gone, display restored, no banner left | H S | 2 | ○ |
| A-UNINST-14 | Reinstall after uninstall | new enrollment works, no duplicate certs or rules | H | 2 | ○ |

### 5.5 Configuration and service behaviour

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| A-CFG-01 | Invalid agent.json (port 0, empty groups, maxSessions 0, virtualDisplay `x`) | service doesn't start, log says why; no fallback to defaults | PS E | 2 | ○ |
| A-CFG-02 | agent.json with comments / trailing commas | accepted | PS | 3 | ○ |
| A-CFG-03 | Certificate missing / key missing | delete the cert → service refuses to start and logs it | PS E | 2 | ○ |
| A-CFG-04 | Log rotation | log files dated 31+ days old (faked names) deleted at the first write of the day; `logRetentionDays 1` respected | PS | 3 | ○ |
| A-CFG-05 | Log content | sign-in, refusal (account, IP, reason), session start/end with PID and duration – and **never** the password or command text (search the log for the test password and a marker command) | H PS | 1 | ○ |
| A-CFG-06 | Logs not readable by users | `normaluser` can't read the log folder | PS | 1 | ○ |
| A-CFG-07 | No outbound traffic | during a full scenario the agent opens no outbound connection (`Get-NetTCPConnection -OwningProcess`) | PS | 1 | ○ |
| A-CFG-08 | Event log writes off | policy `AuditToEventLog=0` → audit only in the file | PS E | 3 | ○ |

## 6. Agent – sign-in and transport security (S)

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| S-TLS-01 | TLS versions | TLS 1.2 and 1.3 accepted; TLS 1.0/1.1 refused (host `SslStream` with forced protocols) | H | 1 | ○ |
| S-TLS-02 | No plain HTTP | `http://vm:58732/v1/health` fails | H | 1 | ○ |
| S-TLS-03 | Pin mismatch | client with a wrong pin stops before sending credentials: no 4625, no sign-in line in the agent log | H PS E | 1 | ○ |
| S-TLS-04 | Health reveals nothing | health JSON has only product, version, protocol, deviceId, features; no `Server` header | H | 1 | ○ |
| S-AUTH-01 | Wrong password | 401, event 2000, Security 4625 | H E | 1 | ○ |
| S-AUTH-02 | Missing / malformed Basic header | 401 with `WWW-Authenticate: Basic realm="RemoteHub Agent"`, not counted as a failure | H | 2 | ○ |
| S-AUTH-03 | Not a WebSocket request | 400 | H | 3 | ○ |
| S-AUTH-04 | Standard user | `normaluser` → 403 "not a member of an allowed group", 2000 | H E | 1 | ○ |
| S-AUTH-05 | User name formats | `PSAdmin`, `.\PSAdmin`, `<COMPUTER>\PSAdmin` all work; `PSAdmin@<computer>` handled as the code says | H | 2 | ○ |
| S-AUTH-06 | Deny network logon | "Deny access to this computer from the network" for PSAdmin (secedit) → 403 (1385), not bypassed | H PS | 1 | ○ |
| S-AUTH-07 | Account lockout | lockout threshold 3 → after 3 wrong passwords Windows locks the account; agent refuses the right password too (401) | H PS | 2 | ○ |
| S-AUTH-08 | Disabled / expired account | 401, logged | H PS | 2 | ○ |
| S-AUTH-09 | Non-RID-500 admin gets a full token | `lapsadmin` (created by the test, Administrators) → `whoami /groups` High integrity, can write HKLM, without `LocalAccountTokenFilterPolicy` | H | 1 | ○ |
| S-AUTH-10 | Empty password account | refused by Windows (network logon with blank password), 401 | H PS | 3 | ○ |
| S-THR-01 | Throttle | 5 wrong passwords → 6th try 429 even with the right password; event 2000 "too many failed sign-ins" | H E | 1 | ○ |
| S-THR-02 | Parallel attempts | 6 wrong passwords in parallel → at most 5 reach Windows (4625 count ≤ 5) | H E | 1 | ○ |
| S-THR-03 | Block ends | after 5 min the right password works again | H | 2 | ○ |
| S-THR-04 | Success clears | 4 wrong, 1 right, 4 wrong → no 429 | H | 3 | ○ |
| S-THR-05 | Restart resets | blocked, restart service → right password works | H PS | 3 | ○ |
| S-LIM-01 | Session limit | `maxSessions 2` → 3rd session (PowerShell or desktop) 503; log line | H | 2 | ○ |
| S-POL-01 | Unreadable policy key | deny SYSTEM read on `HKLM\SOFTWARE\Policies\RemoteHub\Agent` → everything refused, health features `powershell` only, log "can't be read" | PS H | 1 | ○ |
| S-POL-02 | Policy read live | change a policy value, next connection uses it without a service restart | PS H | 2 | ○ |
| S-POL-03 | `Rights\PowerShell` | set to a group without PSAdmin → PowerShell 403, desktop still works (Rights\Desktop default) | PS H | 1 | ○ |

## 7. Agent – PowerShell and remote tools (P, T)

The tool cases run twice: **over the agent** and **over WinRM HTTPS** (after W-HTTPS-01), through RemoteHub's own
classes (`RemoteFs`, `FileTransfer`, `RemoteRegistry`, `RemoteServices`, `RemoteProcesses`) in the harness.

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| P-SES-01 | Session | runspace opens, `$PSVersionTable.PSVersion` 5.1, `whoami` = PSAdmin, event 1000 with PID | H E | 1 | ✅ |
| P-SES-02 | Profile + environment | `$env:USERPROFILE` = PSAdmin's profile, HKCU writable | H | 2 | ✅ |
| P-SES-03 | End kills the tree | start `notepad`/`ping -t` in the session, close it → powershell.exe and children gone within 5 s, event 1001 | H PS E | 1 | ✅ |
| P-SES-04 | Large arguments | 2 MB argument and 10 MB output round-trip | H | 2 | ○ |
| P-SES-05 | Message > 16 MiB | session ends with "Message too large", agent stays healthy | H | 3 | ○ |
| P-SES-06 | Network drop | `Disconnect-VMNetworkAdapter` during a session → agent ends it within ~60 s (keep-alive 30+30), powershell.exe gone, 1001 "connection lost" | N PS E | 2 | ○ |
| P-SES-07 | 20 sessions in parallel | all work; 21st 503 | H | 3 | ✅ |
| P-SES-08 | Read-Host / Get-Credential prompts | answered through the host UI callbacks | H | 3 | ○ |
| P-SES-09 | Tab completion | `TabExpansion2` results for `Get-Ser`, `-Na`, `C:\Win` | H | 3 | ✅ |
| P-CLM-01 | Constrained Language Mode | on CLM VM: session opens, `$ExecutionContext.SessionState.LanguageMode` ConstrainedLanguage; tools needing FullLanguage report it clearly | H | 2 | ○ |
| T-FS-01 | List drives / folders | drives incl. C:, hidden files flagged, `%TEMP%` expands remotely | H | 2 | ✅ |
| T-FS-02 | Upload small files | 300 files → all arrive, SHA-256 equal, no `.remotehub-part` left | H PS | 1 | ✅ |
| T-FS-03 | Upload large file | 200 MB compressible + 200 MB random → equal hashes; compressed one sent gzipped; written uncompressed | H PS | 1 | ✅ |
| T-FS-04 | Upload folder tree | nested folders with Unicode names (`Ünïcödé 文件`), long paths (> 260) | H PS | 2 | ○ |
| T-FS-05 | Skip existing / overwrite | existing files not sent with skip; replaced with overwrite | H PS | 2 | ✅ |
| T-FS-06 | Cancel upload | cancel mid-file → no partial file, no `.remotehub-part` | H PS | 1 | ✅ |
| T-FS-07 | Download | file and folder download, hashes equal | H | 1 | ✅ |
| T-FS-08 | Rename / delete / new folder | done remotely; drive root delete refused | H PS | 2 | ✅ |
| T-FS-09 | Locked file | upload over a file opened exclusively → clear error, original untouched | H PS | 3 | ○ |
| T-FS-10 | Defender real-time on | large tree upload still verified (rename scanning) | H | 3 | ○ |
| T-REG-01 | Browse | `HKLM\SOFTWARE`, all path spellings (`HKLM:\`, `Registry::`, `Computer\`) | H | 2 | ✅ |
| T-REG-02 | Value types | create, read, modify, delete String, ExpandString (stays unexpanded), Multi, DWORD, QWORD, Binary; check with `reg query` | H PS | 2 | ✅ |
| T-REG-03 | Key rename keeps ACL | rename a key with a custom ACL → ACL kept | H PS | 3 | ○ |
| T-REG-04 | Export | `.reg` file arrives and matches `reg export` | H | 3 | ✅ |
| T-REG-05 | Hive protection | delete/rename `HKLM\SOFTWARE` refused | H | 1 | ✅ |
| T-SVC-01 | List | matches `Get-CimInstance Win32_Service` (name, state, start mode, account, PID) | H PS | 2 | ✅ |
| T-SVC-02 | Start / stop / restart | a test service (`Spooler`) changes state within 60 s | H PS | 2 | ✅ |
| T-SVC-03 | Startup type | Automatic, Automatic (delayed), Manual, Disabled set and verified | H PS | 2 | ✅ |
| T-PROC-01 | List | contains the tab's own process, user names shown for an admin | H | 2 | ✅ |
| T-PROC-02 | End process / tree | test process tree ended completely | H PS | 2 | ✅ |
| T-PROC-03 | Critical processes | ending lsass, csrss, winlogon, own remoting process refused | H | 1 | ✅ |

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
| W-HTTPS-06 | Reject from elsewhere | connection from an address outside the list fails (second NIC / other VM) | N | 2 | ○ | |
| W-TWICE-01 | Run twice | same thumbprint (reused while valid > 30 days), one rule | PS | 2 | ✅ | |
| W-PRE-01 | Built-in rule on before | stays on, script warns | PS | 2 | ✅ | |
| W-LISTENER-01 | Existing HTTPS listener | replaced, warning printed | PS | 2 | ✅ | |
| W-DRY-01 | `$DryRun = $true` | prints changes, changes nothing (listeners, rules, certs compared before/after) | PS | 2 | ✅ | |
| W-LEGACY-01 | Old rule name | `RdpManager-WinRM-HTTPS-In` present → removed | PS | 3 | ✅ | |
| W-LATFP-01 | LocalAccountTokenFilterPolicy option | set only when chosen | PS | 2 | ✅ | |
| W-NONADMIN-01 | Non-RID-500 admin over WinRM without LATFP | filtered token (documents the difference to the agent, S-AUTH-09) | H | 3 | ○ | |
| W-HTTP-01 | HTTP + Kerberos by name | session works, 5985 only from given addresses | H PS | 2 | ○ | Domain |
| W-TRUST-01 | Trust prompt on the admin's PC | **2VM**: "Test again" puts "RemoteHub – WinRM <host>" in CurrentUser\Root after the Windows prompt (UIA clicks Yes), connection switches to HTTPS | U | 2 | ○ | 2VM |

## 9. Agent remote desktop (D)

Desktop cases need a **headless desktop client in the harness** (RemoteHub's `AgentDesktopSession` without the WPF
view: Hello → Start → Status, decodes frames to PNG, sends input/clipboard/Pin). The person at the device is
simulated with **S** (UIA on the agent's consent/PIN/banner windows) and **C** (console keyboard).
"Picture shows X" = the decoded frame matches a reference area or contains a marker window with a known colour/text.

### 9.1 Connect, Hello, sessions

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-CON-01 | Hello | account, rights {control, secureDesktop, consentBypass=false}, modes Ask+Pin, sessions, max fps 60 / quality 100, timeout 30, clipboard 3, agent version | H | 1 | ○ |
| D-CON-02 | Session list | console first; Admin's active console, normaluser's disconnected session listed as Disconnected; session 0 never | H PS | 2 | ○ |
| D-CON-03 | Disconnected session refused | Start on a disconnected session → Failed "can't be shown", event 2100 | H E | 2 | ○ |
| D-CON-04 | No Start within 5 min | connection closed by the agent | H | 3 | ○ |
| D-CON-05 | First message not Start | connection closed | H | 3 | ○ |
| D-CON-06 | Desktop off by policy | `DesktopEnabled=0` → `/v1/desktop` 404, health features `powershell` only | PS H | 1 | ○ |
| D-CON-07 | Rights\Desktop without PSAdmin | 403 on desktop, PowerShell still works | PS H | 1 | ○ |
| D-CON-08 | Frames arrive | after Connected: key frame, decodes to the guest resolution, picture matches a console screenshot (≥ 95 % similar) | H C | 1 | ○ |
| D-CON-09 | End by RemoteHub | close → event 1101 with duration, helper process gone within 5 s, banner gone | H S E | 1 | ○ |
| D-CON-10 | Helper runs as SYSTEM in the session | `RemoteHubAgent.exe desktop` in the chosen session ID, in a job object | PS | 2 | ○ |

### 9.2 Consent

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-ASK-01 | Ask → Allow | normaluser signed in; dialog "PSAdmin wants to see and control your screen" appears (UIA), Allow → Connected, event 1100 consent Ask | H S E | 1 | ○ |
| D-ASK-02 | Ask → Deny | Denied "didn't allow it", event 2100 | H S E | 1 | ○ |
| D-ASK-03 | Ask → no answer | Denied after ConsentTimeoutSeconds (30) – "No answer in time" by +10 s | H S | 1 | ○ |
| D-ASK-04 | Esc / close = deny, Enter never allows | Enter sent to the dialog → still waiting; Esc → Denied | H S C | 1 | ○ |
| D-ASK-05 | Timeout policy | `ConsentTimeoutSeconds=5` → countdown 5 s; values 4 and 601 ignored (default 30) | PS H S | 2 | ○ |
| D-ASK-06 | No input before consent | mouse/keys sent while WaitingForUser don't reach the session (marker app counts input) | H S | 1 | ○ |
| D-ASK-07 | View-only text | view-only Start → dialog says "see" not "control" | H S | 3 | ○ |
| D-PIN-01 | PIN right | 6-digit code read by UIA, sent → Connected | H S | 1 | ○ |
| D-PIN-02 | PIN wrong ×3 | attemptsLeft 2,1, then Denied "entered wrong too often", event 2100 | H S E | 1 | ○ |
| D-PIN-03 | PIN with spaces / dash | "123 456" accepted (non-digits stripped) | H S | 3 | ○ |
| D-PIN-04 | PIN dialog Deny | Denied "refused" | H S | 2 | ○ |
| D-PIN-05 | PIN timeout | no entry for 3 min → Denied | H S | 3 | ○ |
| D-NONE-01 | None refused by default | None not in Hello modes; Start with None → refused, 2100 | H E | 1 | ○ |
| D-NONE-02 | None with policy only | `AllowedConsentModes` incl. None but no ConsentBypass right → still refused | PS H | 1 | ○ |
| D-NONE-03 | None with policy + right | both set → connected without a dialog, banner shown | PS H S | 1 | ○ |
| D-NONE-04 | Own session | PSAdmin connects to PSAdmin's own session → no question (logged 1102 own account) | H S | 2 | ○ |
| D-MODES-01 | Policy as REG_SZ list / invalid list | `"Pin;Ask"` works; `"Foo"` → defaults Ask+Pin | PS H | 3 | ○ |
| D-MODES-02 | Only Pin allowed | Ask refused; Pin works | PS H | 2 | ○ |

### 9.3 Banner and user at the device

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-BAN-01 | Banner shown | topmost "Your screen is being viewed and controlled by PSAdmin" with Disconnect (UIA), on the console screenshot | S C | 1 | ○ | |
| D-BAN-02 | Banner not in the picture | decoded frame has no banner (build ≥ 19041) | H C | 2 | ○ | |
| D-BAN-03 | Banner in the picture on 1809 | on W10-1809 the banner **is** in the frame, not black | H | 2 | ○ | 1809 |
| D-BAN-04 | Collapse to pill | after 8 s the banner is the small pill; hover expands it | S | 3 | ○ | |
| D-BAN-05 | Disconnect by the user | UIA clicks Disconnect → Ended "The user at the device ended the session.", resume token void | H S | 1 | ○ | |
| D-BAN-06 | Can't be closed otherwise | Alt+F4 / taskkill by the user (no admin) → banner stays | S | 2 | ○ | |
| D-BAN-07 | View-only text | "viewed by" without "controlled" | S | 3 | ○ | |

### 9.4 View-only and rights

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-VIEW-01 | View-only by choice | mouse, keys, Ctrl+Alt+Del, clipboard dropped (marker app sees nothing); fit disabled | H S | 1 | ○ |
| D-VIEW-02 | View-only by right | account only in `Rights\DesktopViewOnly` (`viewer`, created by the test) → Hello control=false, forced view-only even if Start asks for control | PS H S | 1 | ○ |
| D-VIEW-03 | Key release still passes | key held down before switching to view-only is released (no stuck key) | H S | 3 | ○ |
| D-RIGHT-01 | No SecureDesktop right on the lock screen | Start on a locked session → Failed, 2100 | PS H E | 1 | ○ |
| D-RIGHT-02 | No SecureDesktop right during UAC | UAC opens while connected → no frames sent, input blocked (prompt still open after Enter) | PS H S | 1 | ○ |

### 9.5 Secure desktop, sign-in, lock, user switch

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-LOCK-01 | Connect to a locked session | lock screen in the picture without a question | H S | 1 | ○ |
| D-LOCK-02 | User unlocks while connected | picture holds at the desktop (`UserDesktop`), user asked (Ask), Allow → continues, event 1102 | H S C E | 1 | ○ |
| D-LOCK-03 | User unlocks and refuses | Denied, event 2101, session ends | H S C E | 1 | ○ |
| D-LOCK-04 | Lock while connected (Win+L by the user) | connection stays, lock screen shown | H S | 2 | ○ |
| D-LOCK-05 | Win11 lock screen on the Default desktop | held while waiting for Ctrl+Alt+Del; no user desktop leaked before consent | H C | 1 | ○ |
| D-SIGNIN-01 | Nobody signed in | sign-in screen shown | H C | 1 | ○ |
| D-SIGNIN-02 | Admin signs in through the picture | Ctrl+Alt+Del button + typed password through the desktop session → signed in, no consent for own account | H | 1 | ○ |
| D-SIGNIN-03 | Someone else signs in at the console | console keyboard signs in normaluser → helper follows into the new session, normaluser asked, Allow → continues | H C S | 1 | ○ |
| D-SIGNIN-04 | Sign out while connected | follows to the sign-in screen | H S | 2 | ○ |
| D-UAC-01 | UAC with SecureDesktop | `Start-Process cmd -Verb RunAs` in normaluser's session → prompt in the picture (GDI), usable: credentials typed through the desktop session elevate | H S | 1 | ○ |
| D-UAC-02 | UAC prompt not leaked to view-only users | see D-RIGHT-02 | | | |
| D-FUS-01 | Fast user switching | Switch user to Admin while connected → follows the console, Admin asked | H S C | 2 | ○ |
| D-FUS-02 | Different user = new consent after helper restart | consent from normaluser does not carry over to Admin | H S | 1 | ○ |
| D-DISC-01 | Session disconnected while connected | `tsdiscon` → Status Paused; reconnect it → "has a screen again" | H PS | 2 | ○ |
| D-RDPSES-01 | Connect to a user's RDP session | **2VM** or host mstsc: RDP session listed with client name, connect works | H | 2 | ○ |

### 9.6 Ctrl+Alt+Del

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-SAS-01 | Console session | button → security screen in the picture (LogonUI), log line with SAS value 1 | H E | 1 | ○ |
| D-SAS-02 | RDP session | SAS goes to **that** session, not the console | H | 2 | ○ |
| D-SAS-03 | Without SecureDesktop right | "Your account may not use the sign-in screen on this device." | PS H | 2 | ○ |
| D-SAS-04 | Policy 0 | "turned off by a policy on this device", nothing happens | PS H | 2 | ○ |
| D-SAS-05 | Policy 3 (services + ease of access) | works | PS H | 3 | ○ |
| D-SAS-06 | **On the lock screen** | session locked (normaluser) → Ctrl+Alt+Del through the desktop session shows the password field; password typed through the desktop session unlocks; picture then waits for normaluser's consent (D-LOCK-02) | H S C | 1 | ○ |
| D-SAS-07 | On the sign-in screen (nobody signed in) | Ctrl+Alt+Del → credential field; sign in as PSAdmin through the desktop session → desktop, no consent for the own account | H C | 1 | ○ |
| D-SAS-08 | On the desktop | security options screen (Lock, Switch user, Sign out, Task Manager) in the picture; Esc returns to the desktop | H S | 1 | ○ |
| D-SAS-09 | Lock through Ctrl+Alt+Del | choose *Lock* by mouse → session locked, connection stays, lock screen shown | H S | 2 | ○ |
| D-SAS-10 | Task Manager through Ctrl+Alt+Del | choose *Task Manager* → Taskmgr opens in the session (process check) | H PS | 3 | ○ |
| D-SAS-11 | Repeated presses | 10 × Ctrl+Alt+Del / Esc → every press works, each logged | H E | 3 | ○ |

### 9.7 Input

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-IN-01 | Keyboard by scan code | text typed into a marker app arrives as on the device's layout (en-US and de-DE: `z`/`y`, `ä`, AltGr `@`) | H S | 1 | ○ | |
| D-IN-02 | Extended keys | arrows, Home/End, Del, right Ctrl, numpad Enter | H S | 2 | ○ | |
| D-IN-03 | Mouse | move, left/right/middle/X1/X2 down/up, wheel, horizontal wheel reach the marker app at the right coordinates (±2 px) | H S | 1 | ○ | |
| D-IN-04 | Win key combos | Win+R, Alt+Tab sent through the protocol work on the device | H S | 2 | ○ | |
| D-IN-05 | No stuck keys | connection dropped while a key/button is down → released (ReleaseAll) | H S N | 2 | ○ | |
| D-IN-06 | Input flood | 10,000 mouse moves → no crash, keys never lost | H S | 3 | ○ | |
| D-IN-07 | DPI 150 % | coordinates still exact on a scaled desktop | H S | 2 | ○ | |

### 9.8 Picture, encoder, resolution, monitors

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| D-VID-01 | Presets | 15 / 30 / 60 fps requested → Stats fps ≤ requested, quality default 60 | H | 2 | ○ | |
| D-VID-02 | Policy limits | `MaxFramesPerSecond=10`, `MaxQuality=40` → clamped in Start and Settings | PS H | 2 | ○ | |
| D-VID-03 | Changes only | still screen → (almost) no frames after the refinement frames; moving marker → frames | H S | 3 | ○ | |
| D-VID-04 | Key frame request | KeyFrameRequest → next frame is a key frame | H | 3 | ○ | |
| D-VID-05 | Slow network | throttled link (host QoS / VM bandwidth limit 1 Mbit/s) → quality steps down, ≤ 2 frames in flight, still usable | H N | 2 | ○ | |
| D-VID-06 | Capture method | Stats show Desktop Duplication or GDI; secure desktop always GDI | H | 3 | ○ | |
| D-VID-07 | No GPU encoder on a VM | 60 fps → software encoder, no error | H | 2 | ○ | |
| D-VID-08 | GPU encoder | "Smooth" on a GPU-P VM → "GPU encoder" in Stats; forced failure falls back to software with a key frame | H | 2 | ○ | GPU-P |
| D-VID-09 | N edition | no H.264 → clear "Media Feature Pack" error, event 3100, no crash | H E | 2 | ○ | N |
| D-VID-10 | Dark display | monitor off (power plan 1 min) → agent wakes it, picture within ~6 s | H PS | 2 | ○ | |
| D-FIT-01 | Fit to window | Settings Fit 1600×900 → guest resolution 1600×900 (or nearest listed), restored on disconnect | H PS | 2 | ○ | |
| D-FIT-02 | Unsupported size | 1234×777 → same shape smaller or nearest mode, Stats.fit says which | H | 3 | ○ | |
| D-FIT-03 | Restore after a crash | kill the helper → resolution restored | H PS | 2 | ○ | |
| D-MON-01 | Two monitors | virtualDisplay `always` + Hyper-V display → Layout lists 2, primary first; switching monitor changes the picture | H PS | 2 | ○ | |
| D-MON-02 | All monitors | monitor −1 → bounding box picture; mouse lands on the right monitor | H S | 2 | ○ | |
| D-MON-03 | Monitor added / removed while connected | Layout resent, index clamped, no crash | H PS | 3 | ○ | |
| D-VD-01 | Virtual display auto | `auto`, guest at 640×480 (basic display) → virtual monitor switched on at the window size, primary; off after disconnect | H PS | 2 | ○ | |
| D-VD-02 | Virtual display off | `off` → never switched on | H PS | 3 | ○ | |
| D-VD-03 | Two connections | second connection keeps the virtual display until the last one ends (reference count) | H PS | 3 | ○ | |
| D-VD-04 | Resize | Fit changes → driver reloaded through its pipe, new size within 5 s | H PS | 3 | ○ | |

### 9.9 Clipboard

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-CLIP-01 | Text to the device | Clipboard message → normaluser's clipboard has it (read in session) | H S | 1 | ○ |
| D-CLIP-02 | Text from the device | copy in session → Clipboard message arrives; Unicode, 1,000,000 chars | H S | 1 | ○ |
| D-CLIP-03 | No echo | text set by the agent isn't sent back | H S | 3 | ○ |
| D-CLIP-04 | Image both ways | PNG and BMP → device has PNG + CF_DIB; from device arrives as PNG | H S | 2 | ○ |
| D-CLIP-05 | Files to the device | 3 files + a folder → pasted in Explorer in normaluser's session land in `%LOCALAPPDATA%\Temp\RemoteHub Clipboard\<stamp>`, owned by normaluser, hashes equal; TransferEnd receipt | H S | 1 | ○ |
| D-CLIP-06 | Files from the device | offer has names/sizes; contents sent only on TransferRequest; hashes equal | H S | 1 | ○ |
| D-CLIP-07 | Paste on demand | nothing transferred until something pastes; rdpclip / clipboard history get nothing | H S | 2 | ○ |
| D-CLIP-08 | Limits | 101 MB or 1001 items → "Too much at once", both directions; nothing written | H S | 1 | ○ |
| D-CLIP-09 | Path escape | items named `..\..\evil.txt`, `C:\x`, `CON`, absolute paths → refused / sanitised; nothing outside the clipboard folder | H S | 1 | ○ |
| D-CLIP-10 | Reparse points | junction / symlink in a copied folder on the device → skipped | H S | 2 | ○ |
| D-CLIP-11 | Out of order / incomplete | TransferData out of order or short → "incomplete", partial files removed | H | 1 | ○ |
| D-CLIP-12 | Files read with the user's rights | file only readable by Admin, copied in normaluser's session → refused, the agent's SYSTEM rights never used | H S | 1 | ○ |
| D-CLIP-13 | Nobody signed in | files to the device on the sign-in screen → "Nobody is signed in on the device." | H | 2 | ○ |
| D-CLIP-14 | Policy levels | `DesktopClipboard` 0/1/2/3 → only the allowed kinds pass, refusal texts as in the code | PS H S | 1 | ○ |
| D-CLIP-15 | View-only has no clipboard | level 0 in view-only | H S | 1 | ○ |
| D-CLIP-16 | Newer copy cancels | second copy while the first transfers → first cancelled "A newer copy replaced it." | H S | 3 | ○ |
| D-CLIP-17 | Abort leaves nothing | connection cut during a file copy → no partial files on either side | H S N | 1 | ○ |
| D-CLIP-18 | Old folders purged | clipboard folders older than today removed at next transfer | H PS | 3 | ○ |

### 9.10 Idle, reconnect, robustness

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-IDLE-01 | Idle timeout | `DesktopIdleTimeoutMinutes=1` → warning at ~30 s, Ended at 60 s, event 2102 | PS H E | 2 | ○ |
| D-IDLE-02 | Input resets | a key every 20 s → never ends | PS H | 3 | ○ |
| D-IDLE-03 | View-only counts | view-only session also ends | PS H | 3 | ○ |
| D-RES-01 | Resume after a drop | cut the network 10 s, resume with the token → no new consent, event 1103 | H N S E | 1 | ○ |
| D-RES-02 | Token single use | second resume with the same token → normal consent | H | 1 | ○ |
| D-RES-03 | Token from another account | presented by `lapsadmin` → ignored, still valid for PSAdmin | H | 1 | ○ |
| D-RES-04 | Token after 1 min | expired → consent again | H N | 2 | ○ |
| D-RES-05 | Token void after deliberate end | after Ended by RemoteHub / user / idle / refusal → consent again | H S | 1 | ○ |
| D-RES-06 | Different user signed in meanwhile | resume → asked again | H S C | 1 | ○ |
| D-RES-07 | Old connection still alive | resume while old looks alive → old one ends "continued in a new connection" | H | 3 | ○ |
| D-ROB-01 | Helper killed | `taskkill` the helper → restarted, "screen changed … reconnecting", no new consent for the same user | H PS | 2 | ○ |
| D-ROB-02 | Helper keeps failing | 6 kills within a minute → Ended "kept failing" | H PS | 3 | ○ |
| D-ROB-03 | Service restart while connected | session ends cleanly, no orphan helper, display restored | H PS | 2 | ○ |
| D-ROB-04 | 8 h soak | one session for 8 h with input every minute → no leak (agent working set growth < 50 MB), still connected | H PS | 3 | ○ |
| D-ROB-05 | Fuzzed messages | random/invalid type bytes and JSON → session ends, agent healthy, no 3100 crash loop | H | 2 | ○ |
| D-ROB-06 | Ping | Ping answered with the same 8 bytes | H | 3 | ○ |

### 9.11 Audit

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| D-AUD-01 | Event IDs | across the scenarios above, each of 1000, 1001, 1100, 1101, 1102, 1103, 2000, 2100, 2101, 2102 seen with account, source IP, session, consent mode, view-only, duration | E | 1 | ○ |
| D-AUD-02 | No content | no screen content, keystrokes or clipboard text in log or events (search for marker strings typed and copied) | E | 1 | ○ |

## 10. RemoteHub on a Windows client (C, R)

RemoteHub itself installed in a client VM, driven by UI Automation (FlaUI) in `normaluser`'s or `Admin`'s session.
Data checks read the files and the database through RemoteHub's own `LocalDb` classes in the harness where needed.

### 10.1 Installer

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| C-MSI-01 | Silent install | `msiexec /i /qn` exit 0; `C:\Program Files\RemoteHub\RemoteHub.exe` version = MSI; Start menu shortcut; `Agent\RemoteHub-Agent-x64.msi` bundled and identical to the agent MSI | PS | 1 | ○ |
| C-MSI-02 | Starts without .NET installed | first start shows the main window (no runtime prompt) | U | 1 | ○ |
| C-MSI-03 | .rdp association | RemoteHub in "Open with" and Default apps; mstsc still the default | PS | 2 | ○ |
| C-MSI-04 | Upgrade from the previous release | settings, connections, credentials, audit log intact; one ARP entry | PS U | 1 | ○ |
| C-MSI-05 | Upgrade from RDP Manager 2.x | same upgrade code replaced; `%APPDATA%\RdpManager` copied to `RemoteHub`, old folder kept | PS U | 2 | ○ |
| C-MSI-06 | Downgrade | refused | PS | 2 | ○ |
| C-MSI-07 | Uninstall | program folder, shortcut, ProgId gone; user data kept (documented leftovers only) | PS | 2 | ○ |
| C-MSI-08 | Install as standard user | refused / UAC prompt (per-machine) | S | 3 | ○ |

### 10.2 Storage, protection, audit

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-DB-01 | New database | `%LOCALAPPDATA%\RemoteHub\remotehub.db` not readable as plain SQLite; `db.key` DPAPI-protected; the test password not in any file (byte search) | U PS | 1 | ○ | |
| C-DB-02 | Other Windows user | normaluser can't open Admin's database (DPAPI) | PS | 1 | ○ | |
| C-DB-03 | Migration from settings.dat | 0.13 `settings.dat` → database, field-by-field equal, renamed `.migrated` | PS U | 1 | ○ | |
| C-DB-04 | Daily backup | `backup\` gets one copy per day, 7 kept (clock moved forward) | PS U | 2 | ○ | |
| C-DB-05 | Corrupt database | flipped byte → RemoteHub offers the latest backup, restores it, damaged file kept as `.unreadable-<stamp>` | PS U | 1 | ○ | |
| C-LOCK-01 | Startup password | on → restart asks; wrong password refused; right one opens; db re-encrypted (old `db.key` copy opens nothing) | U PS | 1 | ○ | |
| C-LOCK-02 | Auto-lock | 5 min idle → windows hidden, password asked; sessions keep running | U | 2 | ○ | |
| C-LOCK-03 | Windows Hello PIN | VM with vTPM + Hello PIN: unlock with Hello | U S | 3 | ○ | vTPM |
| C-LOCK-04 | Turn off | password removed, database re-keyed, old copies removed | U PS | 2 | ○ | |
| C-SHARE-01 | Shared folder on SMB | settings on a host share, password asked once, `keys.dat` remembers it | U | 2 | ○ | SMB |
| C-SHARE-02 | Sync conflict | file changed from a second VM while edited → Reload / Keep mine offered, nothing overwritten silently | U | 2 | ○ | SMB, 2VM |
| C-SHARE-03 | Share unavailable at start | share offline → starts with local settings, shared file untouched | U N | 2 | ○ | SMB |
| C-AUD-01 | Audit entries | connect, edit, delete, copy password, transfer, record → entries without values or passwords | U | 2 | ○ | |
| C-AUD-02 | Append-only | direct SQL update/delete of an entry refused; Verify integrity finds a changed entry | PS U | 1 | ○ | |
| C-AUD-03 | Retention policy | `AuditRetentionDays=1` → older entries pruned | PS U | 3 | ○ | |
| C-AUD-04 | CSV export | formula-like values defused (`=cmd` → `'=cmd`) | U | 2 | ○ | |
| C-BAK-01 | Backup round trip | export `.rhbackup` with password, import on a fresh profile (normaluser) as new folder and as replace → identical incl. credentials | U | 1 | ○ | |
| C-BAK-02 | Wrong backup password | refused, nothing imported | U | 2 | ○ | |
| C-BAK-03 | Old `.rdpmbackup` | still imports | U | 3 | ○ | |

### 10.3 Policies, updates, telemetry, crash reports

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-POL-01 | `DisableTelemetry` | no request to `/telemetry` (mock sees none), shown as managed | PS U M | 1 | ○ | |
| C-POL-02 | `DisableUpdateCheck` | no request to `/latest` | PS U M | 1 | ○ | |
| C-POL-03 | `UpdateChannel` | `stable` → no `?channel=beta`; channel can't be changed | PS U M | 2 | ○ | |
| C-POL-04 | `SettingsFolder` | location fixed, can't be changed | PS U | 2 | ○ | SMB |
| C-POL-05 | `DisableRecording` | Record button hidden in every tab type | PS U | 2 | ○ | |
| C-POL-06 | `RecordingFolder` with `%USERPROFILE%` | recordings land there, can't be changed | PS U | 2 | ○ | |
| C-POL-07 | HKCU vs HKLM, legacy `RdpManager` key | both read, HKLM first | PS U | 3 | ○ | |
| C-UPD-01 | Update offered | mock announces a higher version → bar shown; lower / equal → nothing | U M | 2 | ○ | |
| C-UPD-02 | Checksum mismatch | download deleted, not installed | U M | 1 | ○ | |
| C-UPD-03 | Untrusted host | download URL outside GitHub / remotehub.dev → refused | U M | 1 | ○ | |
| C-UPD-04 | Install | UAC approved by UIA in the secure desktop (console) → MSI installs, RemoteHub restarts with the new version | U C M | 2 | ○ | |
| C-UPD-05 | Mandatory / skip | skipped version hidden; mandatory can't be skipped | U M | 3 | ○ | |
| C-TEL-01 | Telemetry content | `app_start`: exactly `event, version, channel, os` (Windows 10 / 11 correct per VM), `arch`, `installId` and the counts `connections, agents, shares, folders` (match the tree); `app_session` after a second start adds only `runSeconds`; nothing else | U M | 1 | ○ | |
| C-TEL-02 | Off deletes install ID | `install-id` file gone | U PS | 2 | ○ | |
| C-CRASH-01 | .NET crash | forced crash → file in `crash\`, sent at next start as `app_crash`, redacted (no user, host, path, IP) | U M | 2 | ○ | |
| C-CRASH-02 | Unexpected exit | killed in Task Manager → `UnexpectedExit` next start | U M | 3 | ○ | |

The mock (M) is a small HTTPS server on the host with a lab root certificate trusted in the VM and the
`update.remotehub.dev` name pointed at it through the VM's hosts file. It records every request for the checks.

### 10.4 RDP through RemoteHub (2VM)

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| R-RDP-01 | Connect with saved credentials | session to the device VM reaches its desktop (device-side: new session for PSAdmin) | U PS | 1 | ○ | 2VM |
| R-RDP-02 | Local account `.\user` | signs in as the device's local account | U PS | 2 | ○ | 2VM |
| R-RDP-03 | NTLM saved-credential policy message | non-domain target without the delegation policy → Windows' message, RemoteHub explains it | U | 3 | ○ | 2VM |
| R-RDP-04 | Password prompt helper | device with "Always prompt" → password on the clipboard, excluded from history, cleared after 30 s or after login | U PS | 2 | ○ | 2VM |
| R-RDP-05 | Clipboard / drive redirection | text both ways; drive redirection shows `\\tsclient\C` only when on | U PS | 2 | ○ | 2VM |
| R-RDP-06 | Dynamic resolution | resize the tab → device session resolution follows | U PS | 3 | ○ | 2VM |
| R-RDP-07 | Ctrl+Alt+Del, full screen, detach/dock | each works, keyboard focus frame shown | U | 3 | ○ | 2VM |
| R-RDP-08 | .rdp import/export | export with password, import on normaluser → connects; risks dialog lists redirections | U | 2 | ○ | 2VM |
| R-RDP-09 | Single instance | opening a `.rdp` while running → same window, entry reused | U | 3 | ○ | 2VM |
| R-AGENT-01 | Full agent flow from the UI | Install RemoteHub Agent… over remoting from the UI, then PowerShell, file transfer, registry, services, processes and agent desktop tabs work | U | 1 | ○ | 2VM |
| R-AGENT-02 | Automatic → agent desktop | "Remote desktop through: Automatic" opens the agent desktop; *Use RDP instead* opens RDP | U | 2 | ○ | 2VM |
| R-AGENT-03 | Own RDP session warning | picking the session connected by RDP from this PC warns | U | 3 | ○ | 2VM |

### 10.5 End to end through RemoteHub's window (2VM)

The admin's side is RemoteHub in the client VM, driven by FlaUI in `Admin`'s session.
- The keyboard and clipboard on the admin's PC are real: Ctrl+C / Ctrl+V go to RemoteHub's window through the Hyper-V console keyboard or SendInput in that session.
- The device side is checked with PS Direct and in-session helpers on the device VM.
- Each case runs against two devices: a **client device** (the second client VM, e.g. W10 against W11) and `Server-2025-en-US`.

**Agent connection**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-AG-01 | Install from the UI | right-click → *Install RemoteHub Agent…* (silent over remoting) → dialog shows success, connection gets the agent shield with the version, audit "agent installed" | U PS | 1 | ○ |
| UI-AG-02 | Test RemoteHub Agent | right-click → *Test RemoteHub Agent* → OK with version | U | 1 | ○ |
| UI-AG-03 | Wrong pin | change the device certificate (re-enroll from another "RemoteHub") → warning that can't be clicked through; no 4625 / sign-in line on the device | U PS E | 1 | ○ |
| UI-AG-04 | Wrong password / not allowed | credential with wrong password → clear message; normaluser → "not allowed" message | U | 2 | ○ |
| UI-AG-05 | Agent stopped | stop the service → tabs report the connection failure, no hang; start → reconnects on retry | U PS | 2 | ○ |
| UI-AG-06 | Update from the UI | *Install / update RemoteHub Agent…* with the agent installed → through the agent, new version shown on the shield | U PS | 1 | ○ |

**Agent remote desktop**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-AD-01 | Open | *Remote desktop* opens the agent tab, session picker lists the device's sessions, Connected; tab picture equals a console screenshot of the device | U C | 1 | ○ |
| UI-AD-02 | Control | click into the tab and type → Notepad on the device gets the text (incl. German layout case) | U S | 1 | ○ |
| UI-AD-03 | Monitor / All monitors / Fit to window | each choice changes the picture and the device resolution as in D-MON / D-FIT; resolution restored on disconnect | U PS | 2 | ○ |
| UI-AD-04 | Key combinations | Alt+Tab and Win+R in the tab go to the device; Win+L stays local | U S | 2 | ○ |
| UI-AD-05 | Ask | user Allows (UIA on the device) → tab connects; Denies → tab shows the refusal | U S | 1 | ○ |
| UI-AD-06 | Show them a code | code read on the device, typed into RemoteHub's PIN box → connected; wrong ×3 → refused | U S | 1 | ○ |
| UI-AD-07 | View-only | toggle → typing doesn't reach the device, clipboard off | U S | 1 | ○ |
| UI-AD-08 | Use RDP instead | button opens an RDP tab to the same device | U | 2 | ○ |

**Copy and paste**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-CP-01 | Text admin → device (agent) | Ctrl+C in Notepad on the admin's PC, Ctrl+V in Notepad on the device through the tab → same text (Unicode, multi-line) | U S | 1 | ○ |
| UI-CP-02 | Text device → admin (agent) | copy on the device, paste on the admin's PC → same text | U S | 1 | ○ |
| UI-CP-03 | Image both ways (agent) | Paint / screenshot copied → pasted on the other side, pixels equal | U S | 2 | ○ |
| UI-CP-04 | Files admin → device (agent) | copy 3 files + a folder in Explorer, paste in Explorer on the device → arrive, hashes equal, progress shown in the tab, device confirmation | U S | 1 | ○ |
| UI-CP-05 | Files device → admin (agent) | copy in Explorer on the device, paste in Explorer on the admin's PC → arrive, hashes equal | U S | 1 | ○ |
| UI-CP-06 | Too much | 101 MB → RemoteHub says to use the file transfer; nothing partial | U S | 2 | ○ |
| UI-CP-07 | Policy text only | `DesktopClipboard=1` → text works, files refused with the device's message | U S PS | 2 | ○ |
| UI-CP-08 | Copy on the lock screen | session locked → no clipboard to or from the device until unlocked and allowed | U S | 1 | ○ |
| UI-CP-09 | Text both ways (RDP) | as UI-CP-01/02 through an RDP tab | U S | 1 | ○ |
| UI-CP-10 | Files both ways (RDP) | Explorer copy/paste through an RDP tab, hashes equal | U S | 2 | ○ |
| UI-CP-11 | Image both ways (RDP) | pixels equal | U S | 3 | ○ |
| UI-CP-12 | Across sessions | copy in an RDP tab, paste in an agent tab to another device → arrives | U S | 3 | ○ |

**Ctrl+Alt+Del**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-SAS-01 | Agent tab, desktop | Ctrl+Alt+Del button → security options in the tab; Esc back | U | 1 | ○ |
| UI-SAS-02 | Agent tab, **lock screen** | device locked → button → password field; password typed in the tab unlocks; tab then waits for the user's consent (or continues for the own account) | U S C | 1 | ○ |
| UI-SAS-03 | Agent tab, sign-in screen | nobody signed in → button → sign in as PSAdmin through the tab → desktop | U C | 1 | ○ |
| UI-SAS-04 | Agent tab, UAC | UAC prompt on the device → visible in the tab, approved with credentials typed in the tab | U S | 1 | ○ |
| UI-SAS-05 | Physical Ctrl+Alt+Del stays local | Ctrl+Alt+Del on the admin's PC (console keyboard) → admin's own security screen, nothing sent to the device | U C | 2 | ○ |
| UI-SAS-06 | RDP tab, desktop | Ctrl+Alt+Del action → security screen in the RDP session | U | 1 | ○ |
| UI-SAS-07 | RDP tab, **lock screen** | RDP session locked → Ctrl+Alt+Del action → password field, unlock by typing in the tab | U S | 1 | ○ |

**Tools and network**

| ID | Test | Pass when | How | P | St |
|---|---|---|---|---|---|
| UI-TL-01 | PowerShell tab | `Get-Service` runs, Tab completion list opens, `exit` closes – over the agent and over WinRM HTTPS | U | 1 | ○ |
| UI-TL-02 | File transfer tab | drag a folder from local to remote side → arrives (hash check on the device); download back | U PS | 1 | ○ |
| UI-TL-03 | Registry tab | create, edit, delete a value under `HKLM\SOFTWARE\RemoteHubLab` → `reg query` on the device agrees | U PS | 2 | ○ |
| UI-TL-04 | Services tab | stop / start Spooler → state on the device agrees | U PS | 2 | ○ |
| UI-TL-05 | Processes tab | end a notepad on the device; lsass refused | U PS | 2 | ○ |
| UI-TL-06 | Set up PowerShell remoting | *Set up on the target…* → script saved, run on the device (PS Direct), *Test again* → available over HTTPS | U PS | 2 | ○ |
| UI-NET-01 | Agent tab reconnect | cut the device's network 10 s → tab reconnects within 30 s without asking the user again | U N | 1 | ○ |
| UI-NET-02 | RDP auto-reconnect | cut 10 s → RDP reconnects | U N | 2 | ○ |

### 10.6 Recordings

| ID | Test | Pass when | How | P | St | Needs |
|---|---|---|---|---|---|---|
| C-REC-01 | Record each tab type | RDP, agent desktop, PowerShell, file transfer, registry, services, processes → MP4 in `Videos\RemoteHub`, named `<connection> - <tab> - <date time>.mp4`, plays (Media Foundation reads duration > 0) | U PS | 2 | ○ | 2VM |
| C-REC-02 | Settings | 30 fps / Full HD → file metadata matches | U PS | 3 | ○ | 2VM |
| C-REC-03 | GPU capture | on Win11 no yellow border; Win10 records too | U | 3 | ○ | 2VM |
| C-REC-04 | Unexpected stop | kill RemoteHub while recording → file still plays (fragmented MP4) | U PS | 2 | ○ | 2VM |
| C-REC-05 | Stop time | last frame not after the stop (fix of 0.14.2) | U PS | 3 | ○ | 2VM |
| C-REC-06 | N edition | clear message, no crash | U | 3 | ○ | N |

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
