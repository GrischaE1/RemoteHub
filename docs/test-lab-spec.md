# RemoteHub Test Lab – Specification

Status: v2.1 (2026-10-11) – SMOKE passes on `TestAutomation - W11 - 26H2 - en-US`; lab mechanics (UI driver, jump
host, harness) brought in line with 0.18.11 (2026-10-15). Scope: a scripted Hyper-V lab that
tests RemoteHub and the RemoteHub Agent end to end on several Windows versions, without anyone clicking. Not in scope:
the RemoteHub UI itself (covered by the unit tests and by hand), building or installing VMs (they are provided, §5).
Owner: Grischa Ernst

Changes from v1: VMs are provided ready-made with a checkpoint and found by name – no image build, no unattend, no
domain controller, no lab network setup. One shared password for every lab account. The lab runs as the standard user
`labop` inside a scheduled task that watches a queue; nobody signs in as `labop` and nothing needs elevation after the
one-time install. First target: Windows 11 26H2 English.

## 1. Goals

- **Real devices, every OS we support:** the agent paths that unit tests can't cover – LocalSystem sign-in, helper in
  a session, secure desktop, UAC, MSI install/update/rollback, firewall, WinRM setup – run on real Windows installs.
- **Fully scripted:** one command runs a scenario set on one or all VMs; results come back as test results plus
  collected logs.
- **Users at the device are simulated:** signed-in users (auto-logon), locking, unlocking, Ctrl+Alt+Del, answering
  the consent dialog, typing the PIN – driven from the host.
- **Repeatable:** every scenario file starts by reverting the VM to its newest checkpoint; nothing from a previous run
  leaks into the next.
- **Easy:** adding an OS = provide a VM named by the convention (§5.2) with a checkpoint. No code or config change.

Non-goals: performance benchmarks (VM timing is too noisy), anything reachable from the internet, building VMs.

## 2. Architecture

```
Host HV01 (Server 2025, Hyper-V)                                  Provided VMs (any switch the VM already uses)
┌──────────────────────────────────────────────┐               ┌──────────────────────────────────────────┐
│ Y:\...\RDP-Manager  (repo, network share)    │               │ TestAutomation - W11 - 26H2 - en-US      │
│  └ lab\Start-LabRun.ps1 ──┐ request file     │               │ TestAutomation - <OS> - <ver> - <lang> … │
│                           ▼                  │  PowerShell   │                                          │
│ D:\RemoteHubLab   (LabRoot, used by labop)   │  Direct, WMI  │ each: PSAdmin (admin), normaluser,       │
│  ├ queue\  ◀── task 'RemoteHub Lab' watches  │ ────────────▶ │       Admin signed in at the checkpoint, │
│  ├ lab\RemoteHub.TestLab, lab\tests\*.Tests  │               │       same lab password, patched         │
│  ├ msi\       (the agent MSI under test)     │  TLS 58732,   │                                          │
│  └ results\<run>\  ──▶ copied to artifacts\  │  WinRM, RDP   │                                          │
│ harness\RemoteHub.LabHarness.exe (C#)        │ ────────────▶ │                                          │
└──────────────────────────────────────────────┘               └──────────────────────────────────────────┘
```

- **Control plane (host → VM, no network needed):** PowerShell Direct (`Invoke-Command -VMName`, `New-PSSession
  -VMName`, `Copy-Item -ToSession/-FromSession` for files – no Guest Service Interface needed), and the Hyper-V WMI
  provider (`root\virtualization\v2`) for the console: screenshots
  (`Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage`), keyboard input (`Msvm_Keyboard`:
  `TypeText`, `TypeKey`, `PressKey` / `ReleaseKey`, `TypeCtrlAltDel`) and mouse input (`Msvm_SyntheticMouse`:
  `SetAbsolutePosition`, `SetButtonState`). This is "the person at the device".
- **Data plane (host → VM over the network):** what RemoteHub really uses – agent TLS on 58732, WinRM 5985/5986, RDP
  3389. The VM's address is read from Hyper-V (`Get-VMNetworkAdapter … .IPAddresses`), so the lab doesn't care which
  switch the VM is on, as long as the host can reach it.
- **Two test layers:** the PowerShell module prepares and inspects the VMs (Pester); the C# lab harness
  (`tests\RemoteHub.LabHarness`, built to `LabRoot\harness`) drives RemoteHub's own client code against them, so the
  real transport, pinning and protocol are tested. One command per call, JSON answer (`Invoke-LabHarness`): `install`,
  `enrollment`, `health`, `update`, `run`, `copy`, `cad`, `credential-provider`, `auto-sign-in`, `tool`, `probe`
  (raw requests), `share`, `seed`, `jump`, and `desktop` – a scripted remote desktop viewer (`DesktopHarness.cs`:
  RemoteHub's desktop client, clipboard transfer and H.264 decoder run from a JSON steps file, every frame acknowledged).
  RemoteHub's own UI, installed in the VM, is driven by UI Automation (§6.3).

## 3. Host

| Item | Requirement |
|---|---|
| OS | Windows Server 2025 / Windows 11 with Hyper-V (HV01 today: Server 2025, 12 cores, 64 GB) |
| Account | Local standard user **`labop`**, member of **Hyper-V Administrators** only – **not** Administrators. With UAC on, an admin account gets a filtered token in which Hyper-V Administrators is missing, so Hyper-V cmdlets fail unless elevated. A standard user has no filtered token, so the group always applies. |
| How it runs | The scheduled task **`RemoteHub Lab`** runs as `labop` (stored password, batch logon), starts at boot and keeps running: it watches `LabRoot\queue` and runs each request in its own PowerShell process (`Invoke-LabQueue.ps1`). `Start-LabRun.ps1` / `Invoke-AsLabop.ps1` only drop a request file and wait for the answer. Nobody starts the task – a start from a non-elevated session is silently ignored by Task Scheduler, and `labop` may not sign in interactively (so `Start-Process -Credential` fails too). No schedule; runs are on demand. |
| Logon right | `labop` needs **"Log on as a batch job"** (on Server only Administrators, Backup Operators and Performance Log Users have it). `Install-LabTask.ps1` adds it. |
| Repo | Stays on the network share (`Y:\Transfer\Development\RDP-Manager`). Elevated shells and `labop` can't see that drive, so everything the lab needs is copied to `D:\RemoteHubLab` by `Start-LabRun.ps1`, which runs in the normal (non-elevated) shell: `lab\`, the newest agent MSI from `artifacts\`, and the lab harness built from the repo's sources. |
| Working folder | `D:\RemoteHubLab` (`LabRoot` in `lab.json`): `lab\` (copied from the repo before every request), `msi\` (newest `RemoteHub-Agent-*-x64.msi` from `artifacts\`), `modules\` (Pester), `queue\`, `results\<run>\` (copied back to `artifacts\lab\<run>\`) |
| PowerShell | Windows PowerShell 5.1 (built in). The module also runs on PowerShell 7, but doesn't need it. Lab scripts stay ASCII-only: 5.1 reads BOM-less UTF-8 as ANSI, and an en dash then ends a string. |
| Modules | `Hyper-V` (built in), `Pester` 5.7.1 in `D:\RemoteHubLab\modules\Pester\5.7.1` (the built-in Pester 3.4 is not used) |
| .NET | .NET 10 SDK (builds RemoteHub and the lab harness) |
| Elevated once | Only `Install-LabTask.ps1`: batch logon right, `LabRoot` ACL, register + start the task. Run it again after changing the `labop` password. |

## 4. Network

The lab uses whatever network the provided VM is connected to; the host must reach the VM's IPv4 address. No switch,
NAT or DNS is created by the lab. The VM's address is read from Hyper-V at run time (KVP), never configured.

**Jump host topology** (lab.json `JumpHost`, used by J-JUMP and the jump case of UI-RDP): a provided VM
(`RemoteHub-JumpHost`) with one adapter where the host reaches it and one on the private switch `RemoteHub-Jump`
(`192.168.231.1/24` in its checkpoint). `Start-LabJumpHost` reverts it and installs the agent. `Move-LabVmBehindJump`
moves the test VM's adapters onto that switch, **untagged** (an access VLAN of the VM's own network is cleared, else it
couldn't reach the jump host), with a static address `<prefix>.<FirstTarget + VM index>`, no gateway, no DNS – so the host
reaches it only through the jump host's agent. The old switches and VLANs are written to `LabRoot\network\<Id>.json`;
`Reset-LabVm` puts them back (`Restore-LabVmNetwork`). `Use-LabJump` makes every harness call go through the jump host.
The jump host is never part of a run's VM list (`-Vm JumpHost` only by name).

## 5. Provided VMs

### 5.1 What a VM must look like

Provided by the owner, not built by the lab:

1. Named `TestAutomation - <OS> - <feature version> - <language>` (§5.2), Generation 2, integration services on.
2. Windows installed and patched. Three local accounts with the **lab password**:
   - `PSAdmin` – **member of Administrators**; all configuration and PowerShell Direct run as this account;
   - `normaluser` – standard user, the person at the device;
   - `Admin` – administrator, **signed in at the console** when the checkpoint is taken.
   All have signed in once, so the first-sign-in screens (privacy settings, "Hi") never appear during a test.
3. Windows Update automatic restarts off (results must not depend on updates running during a test).
4. Power plan: never sleep, never turn off the display (the screen must stay capturable).
5. A **Standard** checkpoint taken while running (saved state keeps `Admin`'s session). The lab always reverts to the
   **newest** checkpoint, whatever its name – refreshing the image = taking a new checkpoint. Names starting with
   `RHLAB ` are the lab's own run checkpoints (§10) and are never reverted to as the base – don't use that prefix.

Unactivated is fine (KMS client key). The "Activate Windows" watermark is part of every screenshot; template matching
for the consent dialog only looks at the dialog's area.

Known on the current W11 image: `normaluser` has a disconnected session in the checkpoint (the lab looks for LogonUI
per session because of it).

On the en-US images, every account that signs in (PSAdmin, Admin, normaluser), the sign-in screen and new accounts have
only the US keyboard (00000409): the lab types at the console by US key positions (shifted symbols, Alt+Y in the UAC
prompt). SMOKE-10 lists every account's layouts and fails on any other one.

### 5.2 Naming and OS matrix

Every Hyper-V VM matching `TestAutomation - * - * - *` is a lab VM. The three parts give its **Id**, used for `-Vm`
and folder names: `TestAutomation - W11 - 26H2 - en-US` → `W11-26H2-en-US`.

| VM (Id) | OS | Status | Why |
|---|---|---|---|
| W11-26H2-en-US | Windows 11 26H2, English | **running** | Current client, Windows Hello paths, new LogonUI |
| W11-…-de-DE | Windows 11, **German** UI | later | Localized group names ("Administratoren"), localized MSI log text |
| W10-22H2-… | Windows 10 22H2 | later | Most common client |
| W10-1809-… | Windows 10 Enterprise LTSC 2019 | later | Minimum supported version; no SystemTemp, old WTS flags, no `WDA_EXCLUDEFROMCAPTURE` |
| S19-… | Windows Server 2019 (Desktop Experience) | later | Oldest server |
| S22-… | Windows Server 2022 | later | Multi-session RDP (§9 D-RDP) |
| Server-2025-en-US | Windows Server 2025 Datacenter, English | **running** | Newest server (2 vCPU; Windows Update still works in its checkpoint - a run takes ~3 min alone, ~11 min next to a running W11 VM) |
| CLM | Windows 11 + WDAC policy enforcing Constrained Language Mode | later | PowerShell restrictions (§9 P-CLM) |
| DC + domain VM | AD DS, `lab.test` | later, optional | Domain accounts, Kerberos WinRM (W-HTTP, P-ADMIN domain case) |

Adding a VM: provide it as in §5.1, then `Start-LabRun.ps1 -Vm <Id> -Tag Smoke`.

### 5.3 `lab/lab.json`

```json
{
  "LabRoot": "D:\\RemoteHubLab",
  "HostAccount": "labop",
  "VmNamePattern": "TestAutomation - * - * - *",
  "Accounts": { "AdminAccount": "PSAdmin", "UserAccount": "normaluser", "ConsoleAccount": "Admin" },
  "Vms": [],
  "JumpHost": { "Name": "RemoteHub-JumpHost", "Switch": "RemoteHub-Jump", "Address": "192.168.231.1",
                "PrefixLength": 24, "FirstTarget": 10, "AdminAccount": "Admin" }
}
```

`Vms` is optional: an entry with a VM's full `Name` overrides any value for that VM (e.g. other account names on a
German image).

`Broker` is optional too: the RemoteHub Broker the B-BRK cases use (test-lab-cases.md §9.14), with its admin token
`BrokerToken` – both in `lab.local.json` (§7), so a real broker's address stays out of the repository (`lab.json` may
name one too; `lab.local.json`'s wins). Either a deployed broker – `"Broker": { "Url": "https://<broker>" }` with that
broker's admin token; the lab VMs need HTTPS to it – or one on the host: `"Url": "http://{host}:8787"` with
`npx wrangler dev --ip 0.0.0.0 --port 8787` in the broker's repository (after `npx wrangler d1 migrations apply
remotehub-broker --local`; `BrokerToken` is `ADMIN_TOKEN` from its `.dev.vars`), where `{host}` stands for the host's
address towards the VM and the network must let the VMs reach that port. Without either the B-BRK cases are skipped and
say why. Every run makes its own organisation there and deletes it again; a run that stops before the end can leave
one behind (`rhbroker org delete`).

## 6. Accounts and sign-ins

### 6.1 Accounts

| Account | Where | Used for |
|---|---|---|
| `labop` | host | runs the lab (Hyper-V Administrators, standard user, batch logon) |
| `PSAdmin` (`AdminAccount`, `-Account Admin`) | VM, provided | PowerShell Direct, all configuration, installing |
| `normaluser` (`UserAccount`, `-Account User`) | VM, provided | the person at the device (consent, lock screen) |
| `Admin` (`ConsoleAccount`, `-Account Console`) | VM, provided | the administrator already at the console after a revert |
| `lapsadmin`, `viewer`, … | VM, created by the scenario that needs it | UAC remote restrictions (non-RID-500 admin), `DesktopViewOnly` – gone again with the next revert |

All accounts use the **same lab password**, kept in `lab/lab.local.json` (`{ "Password": "…" }`, not in git).
This is a throw-away internal lab; the password is valid nowhere else.

### 6.2 Auto-logon (simulated people)

- **Why:** consent, lock screen, unlock, "user signs in while connected", switching users – all need a real interactive
  session, started without anyone typing.
- **How:** `Set-LabAutoLogon -VMName … -Account User|Admin` writes the built-in Winlogon values (`AutoAdminLogon`,
  `DefaultUserName`, `DefaultPassword`, `DefaultDomainName`) via PS Direct, restarts the guest and waits for the
  desktop. No extra tool; works the same on every Windows version. Undone by `Clear-LabAutoLogon` or the next revert.

### 6.3 Simulating what the person does

| Action | How (from the host) |
|---|---|
| Sign in at the sign-in screen | `TypeCtrlAltDel`, then `TypeText` the password via `Msvm_Keyboard` (console input – exactly like a person) |
| Lock / unlock | lock: task in the user's session running `rundll32 user32.dll,LockWorkStation`; unlock: keyboard as above |
| Answer the consent dialog | `lab\guest\RhPerson.ps1` in the user's session (`Invoke-LabPerson`) finds the agent's window by its class and reads it with Windows OCR (`Windows.Media.Ocr`) – the agent draws its dialogs and banner itself, there is no UI Automation tree; the button is clicked by its label with the **console mouse** (`Send-LabMouse`), because the dialog belongs to SYSTEM and input from the user's session doesn't reach it (UIPI) |
| Read the PIN the device shows | `RhPerson.ps1 -Do pin` (OCR of the PIN dialog) |
| Use RemoteHub's own UI in the VM | UI Automation (`System.Windows.Automation`) through `RhPerson.ps1 -Do uia` / `Invoke-LabUi`: find by name or AutomationId, click, toggle, set text, read. No FlaUI |
| Open a UAC prompt | task in the user's session: `Start-Process cmd -Verb RunAs` |
| Start a second session | RDP from another lab VM (`mstsc /v:` with a saved `.rdp`, credentials via `cmdkey`), or `query session` to check |
| Disconnect an RDP session | `tsdiscon <id>` via PS Direct |

Running something **in the user's session** (not session 0): `Invoke-LabInSession` registers a one-off scheduled task
in the guest with that user as principal (`LogonType Interactive`), runs it, waits, returns its output.

## 7. Secrets

One lab password for every account (§6.1), in `lab/lab.local.json` next to `lab.json`, ignored by git and copied to
`LabRoot` with the rest of `lab\`. `lab.local.example.json` shows the format. Agent enrollment files (`.rhagent`)
created by tests are deleted after use, like in production. The optional `BrokerToken` there is the lab broker's admin
token (§5.3); like the password it is never logged or written to evidence. The organisations B-BRK makes, and their
secrets, exist for one run only.

## 8. PowerShell module `RemoteHub.TestLab`

Location `lab/RemoteHub.TestLab/` (Windows PowerShell 5.1 and 7, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`).

| Area | Functions |
|---|---|
| Config | `Get-LabConfig` (lab.json + lab.local.json), `Get-LabVm`, `Get-LabCredential` |
| VM | `Reset-LabVm` (revert to the VM's own checkpoint, start, wait), `Initialize-LabVm` (reuse or set up a state, run checkpoints – §10), `Remove-LabRunCheckpoints`, `Stop-LabVm` (turn lab VMs off), `Wait-LabVm` (heartbeat + PS Direct answers), `Restart-LabVm` (guest restart, waits for the new boot), `Get-LabVmAddress` |
| Guest | `Invoke-LabGuest` (PS Direct, admin account by default), `Copy-LabFile` / `Get-LabFile` (PS Direct session, creating folders) – one session per VM and account is kept for the test file and reset before each call (variables, functions, preferences, environment, location, strict mode back to a new session's), so a call takes tens of ms instead of 0.5–2 s; dropped when the module reverts, restarts, stops or checkpoints the VM, and never re-sent once a block may have run. `RHLAB_SESSION_CACHE=0` or `Invoke-LabGuest -NewSession`: a session per call |
| Console | `Get-LabScreenshot` (PNG at the guest's resolution, reports a blank screen), `Send-LabText`, `Send-LabKey`, `Send-LabKeyCombo`, `Send-LabMouse`, `Send-LabCtrlAltDel` |
| Person / UI | `Invoke-LabPerson` (RhPerson.ps1: windows, read, click, pin, clipboard, screen OCR, uia), `Get-LabOcr`, `Start-LabMarker` / `Get-LabMarker` (what reached the device), `Install-LabClient` / `Start-LabClient` / `Invoke-LabUi` (RemoteHub in the VM) |
| Jump host | `Start-LabJumpHost`, `Stop-LabJumpHost`, `Move-LabVmBehindJump`, `Restore-LabVmNetwork`, `Use-LabJump`, `Invoke-LabJump` |
| Desktop | `Invoke-LabDesktop` / `Start-LabDesktop` / `Receive-LabDesktop` (harness `desktop`) |
| Sessions | `Set-LabAutoLogon`, `Clear-LabAutoLogon`, `Wait-LabDesktop` (user's explorer running + screenshot not blank), `Invoke-LabInSession`, `Lock-LabSession`, `Unlock-LabSession` |
| RemoteHub | `Get-LabHostAddress` (host's source address towards the VM, also across a router), `Install-LabAgent` (harness: `AgentEnrollment.Create` + `AgentInstaller.Prepare` + `InstallOverRemoting` over PowerShell Direct; returns deviceId, pin, installed.*), `Get-LabAgentHealth` (harness: `HealthAsync`, TLS pinned), `Get-LabAgentState` (service, agent.json, certificate incl. key exportability, folder ACL, firewall rule, leftovers, log lines), `Uninstall-LabAgent` (msiexec /x, returns exit code + guest log path), `Save-LabAgentEvidence` |
| RemoteHub (updates, policy) | `Get-LabAgentMsi [-Previous]` (build under test / release before it), `Update-LabAgent` (harness: `StartUpdateThroughAgent` + `WaitForRestartAsync` and RemoteHub's verdict), `Invoke-LabAgentCommand` (PowerShell through the agent), `New-LabBrokenMsi` (false launch condition / truncated), `Set-LabAgentPolicy` / `Get-LabAgentPolicy` (`HKLM\SOFTWARE\Policies\RemoteHub\Agent` and `Rights`) |
| RemoteHub (next) | `Invoke-LabSetupScript` |
| Evidence (next) | `Save-LabEvidence` → `results\<run>\<vm>\`: agent logs, Application/Security event log extracts (RemoteHub Agent source, 4624/4625), MSI logs, screenshots, `query session` output |

Rules: every function is idempotent where it can be; every wait has a timeout and a clear error ("RH-W11: no
desktop for labuser after 180 s"); waits poll a condition, never a fixed `Start-Sleep` alone.

## 9. Test scenarios

The full release test sheet for Windows clients (every case, its checks and how it is automated) is [test-lab-cases.md](test-lab-cases.md); the table below is the scenario overview.

Pester files in `lab/tests/*.Tests.ps1`, each with `param($VmName, $EvidenceDir)`; `Invoke-LabTests.ps1` runs every
file once per VM. Each file: revert → set up → act → check → collect evidence. The parts that need RemoteHub's client
code call the lab harness (§2) with the VM's address and the agent's pin. Scenarios that need a VM that isn't provided yet (domain, RH-S22, RH-CLM) are skipped
until it exists.

| ID | Scenario | Checks |
|---|---|---|
| **SMOKE** | Revert, console, accounts, auto-logon | PS Direct answers with a full admin token; `UserAccount` not in Administrators; IPv4 address known; screenshot not blank; auto-logon reaches the desktop; code runs in the user's session |
| **W-HTTPS** | WinRM setup script, HTTPS | script runs without errors; prints a thumbprint; `FetchCertificateAsync` sees the same; certificate is end-entity, server auth only, key not exportable; HTTPS session pinned to it works; Windows' own HTTP rule off; our rule only for the given addresses |
| W-HTTP | WinRM setup script, HTTP, domain VM by name (needs domain) | Kerberos session works; built-in rule off; 5985 only from the given addresses |
| W-PRE | Built-in WinRM rule enabled before | script leaves it on and warns |
| W-TWICE | Script run twice | second run reuses the certificate (same thumbprint) |
| **A-INST** ✅ | Agent install with the client's `InstallOverRemoting` (runspace: PowerShell Direct; the WinRM transport itself is covered by W-*) | fingerprint read back = pinned; service `RemoteHubAgent` running, LocalSystem, automatic (delayed); `/v1/health` over TLS pinned from the host; certificate in LocalMachine\My, SHA-256 = pin, key not exportable; `agent.json` identity; one inbound rule for `RemoteHubAgent.exe`, TCP 58732, remote address = the host only; `%ProgramData%\RemoteHub\Agent` owner Administrators, protected, only SYSTEM + Administrators; no `.rhagent` or staging folder left; event source registered, enrollment logged |
| A-HAND | Install by hand (MSI next to `.rhagent`, run as the admin in the session) | the client's `WaitForClaim` claims it and marks it installed |
| A-UPD | Update through the agent (`StartUpdateThroughAgent`) | new version answers; temp folder removed after msiexec ended |
| A-UPDFAIL | Update with a broken MSI | reported as failed with the old version, not "updated" |
| A-ROLLBACK | First install that fails after enrolling (forced via a failing custom action / `msiexec` with a bad property) | no firewall rule, no certificate, no config left |
| A-UNINST ✅ | Install like RemoteHub, uninstall like Windows (`msiexec /x <product code> /qn`, runs `RemoteHubAgent.exe cleanup`) | exit 0; product, service and program folder gone; firewall rule gone; no `O=RemoteHub Agent` certificate left; `agent.json` gone; logs stay and end with "Uninstalled: …"; event source gone; nothing answers on 58732; `SoftwareSASGeneration` back as before the install (skipped while the agent doesn't change it – up to 0.15.5) |
| A-EXTRACT | Agent start on RH-W10LT / RH-S19 | nothing extracted to `C:\Windows\Temp` (`.net` folders), only Program Files |
| **P-ADMIN** | PowerShell via agent as `AdminAccount`, `lapsadmin` (created by the scenario), domain admin (needs domain) | session works, full token (`whoami /groups` shows High integrity) |
| P-USER | PowerShell via agent as `UserAccount` | refused 403 (not in allowed groups) |
| P-THROTTLE | 6 wrong passwords in parallel | at most 5 attempts reach Windows (Security log 4625 count), then 429 |
| P-DROP | Cut the VM's network adapter during a session (`Disconnect-VMNetworkAdapter`) | agent ends the session within ~60 s (log), `powershell.exe` gone |
| P-CLM | PowerShell via agent on RH-CLM | tools that need FullLanguage report it clearly (registry editor known limit) |
| **D-ASK** | Desktop, consent Ask, user signed in | dialog appears (screenshot), accept → frames arrive; deny → refused + audit 2101 |
| D-PIN | Consent PIN | PIN read in the session, typed wrong ×3 → refused; right → connected |
| D-NONE | Consent None | refused by default; allowed only with policy `AllowedConsentModes` incl. None **and** `Rights\ConsentBypass` |
| D-LOCK | Session locked, then unlocked by the user | lock screen visible without asking; after unlock the picture holds until the user allows |
| D-UAC | UAC prompt while connected | visible and usable with `SecureDesktop`; without the right: no picture **and** no input reaches it (prompt still open after sending Enter) |
| D-SIGNIN | No one signed in | sign-in screen shown; the user signs in (console keyboard) → asked before the picture continues |
| D-RDP | RDP session of the user on RH-S22 + console session | both listed; connecting to the RDP session works; Ctrl+Alt+Del goes to **that** session (open question from 0.15.0) |
| D-CLIP | Clipboard text, image (PNG and BMP), files both ways | content arrives; limits (101 MB, 1001 files) refused on both sides; an aborted file copy leaves no partial files |
| D-IDLE | Policy `DesktopIdleTimeoutMinutes = 1` | warning, then ended + audit 2102 |
| D-RESUME | Drop the connection, reconnect within 60 s | no new consent; a resume token presented by another account doesn't spoil it |
| D-GPU | "Smooth" on a VM with a GPU-P adapter | connection info shows "GPU encoder"; frames decode; forced failure falls back to software |
| D-DESKCLIP | Clipboard install while a desktop tab to another VM is open | client asks before copying the enrollment secret |

The client-side unit tests stay as they are; the lab adds the device-side truth.

## 10. Running it

```
# once, elevated: batch logon right for labop, register + start the watcher task
& D:\RemoteHubLab\lab\Install-LabTask.ps1     # the published copy - an elevated shell can't see Y:

# any time, from any shell (no elevation): copy lab + newest agent MSI to D:\RemoteHubLab, queue, wait, copy results back
& Y:\Transfer\Development\RDP-Manager\lab\Start-LabRun.ps1                          # all VMs, all scenarios
& Y:\Transfer\Development\RDP-Manager\lab\Start-LabRun.ps1 -Vm W11-26H2-en-US -Tag Smoke

# one command as labop, e.g. to look at a VM by hand
& Y:\Transfer\Development\RDP-Manager\lab\Invoke-AsLabop.ps1 { Get-LabVm | Format-Table Id, Name }
```

- Output: Pester NUnit XML per VM, `summary.json` and a summary table, `console.log`, evidence (screenshots) – in
  `D:\RemoteHubLab\results\<run>\<Id>\`, copied to `artifacts\lab\<run>\` by `Start-LabRun.ps1`.
- A run uses the newest `RemoteHub-Agent-<version>-x64.msi` from `artifacts\` (the build being tested), copied to
  `D:\RemoteHubLab\msi\`, and the release before it in `msi\previous\` (the update scenarios start from it); the
  version in the report is read from the file name. `run.json` records both with their SHA-256 and each VM's
  checkpoint.
- Every test name starts with its case ID from [test-lab-cases.md](test-lab-cases.md) and carries it as a tag
  (`-Tag A-UPD-04` runs one case). After the run `Start-LabRun.ps1` writes the PR report `report.md`
  (`New-LabReport.ps1`).
- `Get-LabFailures.ps1 [-Run <id>]` sums a finished run up in about 1 KB: one line per VM, one per failed case with
  the start of its message (a failed setup's error from the file's console log) and the evidence folders. It reads
  `artifacts\lab\` by default, `-Root D:\RemoteHubLab\results` reads the lab's own copy. `Start-LabRun.ps1` runs it
  at the end of every run and keeps it as `artifacts\lab\<run>\failures.txt` – what an AI session reads instead of
  the XMLs and logs (`.github/instructions/lab.instructions.md`).
- **Second attempt and flaky cases** (`lab\LabRetry.ps1`): a file that fails, or runs past its time limit, runs once
  more in the same run, in full, from its VM's checkpoint (its evidence under `<vm>\retry\`). Its last attempt counts;
  the first one's results move to `<kind>\attempt1\attempt1-<file>.xml` (with its summary and console log), names the
  readers of `pester-*.xml` don't take. A case that failed the first time and passed the second is **flaky**: listed in
  the run's `flaky.json` and as `FLAKY` lines in `failures.txt`, counted per VM in `summary.json`, and it doesn't fail
  the run - a run is green when every case passed, second attempts included. A real bug fails both times. The console
  log says `retry` instead of `done` for a first attempt that runs again, so the lab app counts the file run once. A
  second attempt that never runs (the run drained or ended) gives the first results back. Off:
  `Start-LabRun.ps1 -NoRetry`, and with `-FailFast`. Why: of the 229 cases that failed in the seven full runs of
  21-26 October, 155 failed in one run only and none in more than four - most failures were the lab's timing, not
  the product.
- `Get-LabFlaky.ps1 [-Root ...] [-Runs 30] [-Top 40]` is the work list for that: per case how often it failed (in the
  last attempt) and how often it was flaky over the last finished runs, in how many runs it ran, and the last time
  with its message, the most frequent first. A case leaves it once it passes first time in every run.
- **RemoteHub Lab** (`lab\LabConsole`, WPF like RemoteHub; `LabConsole.cmd` is the older PowerShell version of it):
  `build\build-lab-tool.ps1` builds it into one file, `lab\RemoteHubLab.exe` (git-ignored; again after changes to
  `lab\LabConsole`). Its tests and lab scripts always come from main: it keeps its own checkout of origin/main,
  `C:\rh-lab-main` (a detached worktree, made the first time), and brings it to origin/main when it opens and before
  every run, also each run of a repeat – nobody pulls. Before each run it also copies the newest agent build of any
  worktree (with its RemoteHub package, the one before it for the update tests, the credential provider's lab build)
  into that checkout's `artifacts\`, and the git-ignored `lab.local.json` / `lab-signing.cer` from the checkout it was
  started from (the one the exe sits in, else the one from last time in `%LOCALAPPDATA%\RemoteHubLab\checkout.txt`, else
  it asks). A branch's own tests run in the lab once merged, or with `Start-LabRun.ps1` from that branch. It shows the tests as group > file > case with check boxes and a search box, read from
  `lab\tests\` (a new file or case shows up by itself; the group is the first bare tag of the file's `Describe`), the
  VM kinds, the MSI under test and an optional build of new MSIs, *Start run* / *Stop* (`Stop-LabRun.ps1`), a
  progress bar with the time left (from what each file took before), the file runs, the output, and `failures.txt`
  when it ends. *Failed in last run* ticks just the cases that failed in the newest run; *Changed since full run*
  ticks the files that what changed since the last full run can affect (git's changes from the commit that run
  recorded – an area the app doesn't know selects everything). *Quick build check* and *Fail fast* are
  `Start-LabRun.ps1 -Quick` / `-FailFast` (below); *Run again when it ends* repeats until stopped and picks up new
  cases when everything is ticked. What is ticked becomes `-Tag`: a group's tag when the whole group is ticked and
  no other file carries it, else the case IDs; everything ticked is a full run. Each case has a pill for its latest
  lab result or its state in the run going on – passed, failed (the tooltip names the run and the VMs), skipped, not
  run, pending, running – and files and groups the worst of theirs with the number failed; *Show* filters by it.
- `Get-LabCaseStatus.ps1` gives that latest result per case, from every finished run in `LabRoot\results` (cached
  per run in `%LOCALAPPDATA%\RemoteHubLab`, so only new runs are read).
- `Update-LabCaseStatus.ps1 [-WhatIf]` keeps the documents' status in step with the lab: a case with a lab test is
  ✅ in [test-lab-cases.md](test-lab-cases.md) and "✅ Automated – `<file>`" in the
  [test document](test-lab-test-document.md) once it has run, ◐ until then, and it regenerates the test document's
  "Written, not yet run in the lab" list. Cases without a lab test are left as they are. Run it before a PR that
  adds or changes lab tests.
- Within a run every VM works through files in parallel with the others (lane 1 "… - en-US", lane 2 "… - 2", lane 3
  "… - 3": VMs of the same kinds). Of what can start, the file expected to take longest starts first (its median in
  the last 10 runs). A file that uses another VM (`LabNeeds: lane`, `Get-LabDevice`) gets a free one of that kind –
  its own lane's first, else another lane's (`RHLAB_DEVICES` tells `Get-LabDevice` which). A file's time limit is
  twice the longest it took before, at least 20 minutes and never above its own (`TimeLimitMin`, else 60), so a file
  that hangs is ended early. Replaying the runs of 16 and 19 October shows the Server VMs are what bounds a full run
  (their own files plus the time Console files hold them): a third lane takes a full run from 8.3 h to 5.6 h (6.1 to
  4.0 h), the quick build check to 5.4 / 3.4 h with two lanes and 3.7 / 2.3 h with three.
- `Start-LabRun.ps1 -Quick` is the build check: device files run on one device kind (a client Windows one, as the
  Server VMs are the busiest), except files whose header says `# LabQuick: every kind` (they have parts for Server
  only); Console files run as usual. `-FailFast`: a `Describe` block skips its other cases after its first failure
  (Pester's `SkipRemainingOnFailure = Block`), and no more files start once 4 in a row passed nothing. Both reach the
  run through `results\<run>\options.json` (the queue passes only `-Vm` and `-Tag`), which also records the commit
  the run was started from; `run.json` repeats all three.
- Each file run writes `pester-<file>.xml.timing.json`: the minutes in the module's slow helpers (VM resets and
  installs, UI steps, guest calls – the outermost call counts) and the file's own time besides.
  `Get-LabTimings.ps1 [-Run <id>]` sums a run up: per helper, then the longest files with their top helpers – where
  to look before making tests faster.
- Requests run one after another. Only the VM under test is on (plus the jump host while a jump case runs): a run first turns off every lab VM (`Stop-LabVm`, hard off - every scenario reverts anyway), then turns each VM off again after its scenarios, also when they fail. Only VMs matching the lab name pattern are touched.
- Reuse between files: a file that needs "agent and/or RemoteHub installed, an account signed in" calls
  `Initialize-LabVm`, which keeps the VM when it is already in that state (`LabRoot\state\<Id>.json` plus a token in
  the guest, checked before reuse) and otherwise reverts and installs. The first time a VM is set up for a state in a
  run, it also takes a **run checkpoint** of the running VM, `RHLAB <run id> <state key>` (the key covers the options,
  the MSIs and the VM's own checkpoint); its state record goes to `LabRoot\state\run-checkpoints\<Id>\`. A later file
  that needs the same state – after a failed file, `Set-LabVmDirty` or an install test – reverts to it instead of
  installing again (minutes saved per file), then the guest clock is synced and the reuse check runs; a checkpoint that
  fails it is deleted and the state built the full way. `Reset-LabVm` and the install/upgrade tests never start from a
  run checkpoint. The runner removes them at the end of the run and at the start of the next (a cancelled run leaves
  them; `Remove-LabRunCheckpoints` by hand). Off: `Invoke-LabTests.ps1 -NoSnapshots` or `RHLAB_SNAPSHOTS=0`;
  `-NoReuse` turns off both.
- Measured: SMOKE on W11-26H2-en-US ≈ 110 s. Target: one VM × all scenarios < 30 min.

## 11. Open questions

1. GPU partitioning: is there a host GPU that supports GPU-P? Otherwise D-GPU runs only on a physical test PC.
2. Domain VM: needed for W-HTTP and the domain case of P-ADMIN – provide one later, or drop those cases?
3. Should the lab also run in CI (self-hosted runner on the host, nightly), or only on demand and through the task?
4. Windows Insider / next Windows version as an extra VM for early warnings?
