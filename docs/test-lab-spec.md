# RemoteHub Test Lab – Specification

Status: v2.1 (2026-10-11) – SMOKE passes on `TestAutomation - W11 - 26H2 - en-US`. Scope: a scripted Hyper-V lab that
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
│ tests\RemoteHub.LabTests (xUnit, C#, later)  │ ────────────▶ │                                          │
└──────────────────────────────────────────────┘               └──────────────────────────────────────────┘
```

- **Control plane (host → VM, no network needed):** PowerShell Direct (`Invoke-Command -VMName`, `New-PSSession
  -VMName`, `Copy-Item -ToSession/-FromSession` for files – no Guest Service Interface needed), and the Hyper-V WMI
  provider (`root\virtualization\v2`) for the console: screenshots
  (`Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage`) and keyboard input (`Msvm_Keyboard`:
  `TypeText`, `TypeKey`, `TypeCtrlAltDel`). This is "the person at the device".
- **Data plane (host → VM over the network):** what RemoteHub really uses – agent TLS on 58732, WinRM 5985/5986, RDP
  3389. The VM's address is read from Hyper-V (`Get-VMNetworkAdapter … .IPAddresses`), so the lab doesn't care which
  switch the VM is on, as long as the host can reach it.
- **Two test layers:** the PowerShell module prepares and inspects the VMs (Pester); a C# project (later) drives
  RemoteHub's own client code against them (xUnit), so the real transport, pinning and protocol are tested.

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
| .NET | .NET 10 SDK (builds RemoteHub, runs `RemoteHub.LabTests` later) |
| Elevated once | Only `Install-LabTask.ps1`: batch logon right, `LabRoot` ACL, register + start the task. Run it again after changing the `labop` password. |

## 4. Network

The lab uses whatever network the provided VM is connected to; the host must reach the VM's IPv4 address. No switch,
NAT or DNS is created by the lab. The VM's address is read from Hyper-V at run time (KVP), never configured.

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
   **newest** checkpoint, whatever its name – refreshing the image = taking a new checkpoint.

Unactivated is fine (KMS client key). The "Activate Windows" watermark is part of every screenshot; template matching
for the consent dialog only looks at the dialog's area.

Known on the current W11 image: `normaluser` has a disconnected session in the checkpoint (the lab looks for LogonUI
per session because of it), and the keyboard layout is German – `TypeText` of `y`, `z` and symbols other than `$` may
come out wrong until typing is layout-aware or the image uses a US layout.

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
  "Vms": []
}
```

`Vms` is optional: an entry with a VM's full `Name` overrides any value for that VM (e.g. other account names on a
German image).

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
| Answer the consent dialog | screenshot → find the dialog (template match, or UI Automation from a task in the user's session) → `TypeKey` Enter / Esc, or click via UI Automation |
| Read the PIN the device shows | UI Automation from a task in the user's session (`AutomationElement` by AutomationId), or OCR on the screenshot |
| Open a UAC prompt | task in the user's session: `Start-Process cmd -Verb RunAs` |
| Start a second session | RDP from another lab VM (`mstsc /v:` with a saved `.rdp`, credentials via `cmdkey`), or `query session` to check |
| Disconnect an RDP session | `tsdiscon <id>` via PS Direct |

Running something **in the user's session** (not session 0): `Invoke-LabInSession` registers a one-off scheduled task
in the guest with that user as principal (`LogonType Interactive`), runs it, waits, returns its output.

## 7. Secrets

One lab password for every account (§6.1), in `lab/lab.local.json` next to `lab.json`, ignored by git and copied to
`LabRoot` with the rest of `lab\`. `lab.local.example.json` shows the format. Agent enrollment files (`.rhagent`)
created by tests are deleted after use, like in production.

## 8. PowerShell module `RemoteHub.TestLab`

Location `lab/RemoteHub.TestLab/` (Windows PowerShell 5.1 and 7, `Set-StrictMode -Version Latest`,
`$ErrorActionPreference = 'Stop'`).

| Area | Functions |
|---|---|
| Config | `Get-LabConfig` (lab.json + lab.local.json), `Get-LabVm`, `Get-LabCredential` |
| VM | `Reset-LabVm` (revert to checkpoint, start, wait), `Stop-LabVm` (turn lab VMs off), `Wait-LabVm` (heartbeat + PS Direct answers), `Restart-LabVm` (guest restart, waits for the new boot), `Get-LabVmAddress` |
| Guest | `Invoke-LabGuest` (PS Direct, admin account by default), `Copy-LabFile` / `Get-LabFile` (PS Direct session, creating folders) |
| Console | `Get-LabScreenshot` (PNG at the guest's resolution, reports a blank screen), `Send-LabText`, `Send-LabKey`, `Send-LabCtrlAltDel` |
| Sessions | `Set-LabAutoLogon`, `Clear-LabAutoLogon`, `Wait-LabDesktop` (user's explorer running + screenshot not blank), `Invoke-LabInSession`, `Lock-LabSession`, `Unlock-LabSession` |
| RemoteHub | `Get-LabHostAddress` (host's source address towards the VM, also across a router), `Install-LabAgent` (harness: `AgentEnrollment.Create` + `AgentInstaller.Prepare` + `InstallOverRemoting` over PowerShell Direct; returns deviceId, pin, installed.*), `Get-LabAgentHealth` (harness: `HealthAsync`, TLS pinned), `Get-LabAgentState` (service, agent.json, certificate incl. key exportability, folder ACL, firewall rule, leftovers, log lines), `Uninstall-LabAgent` (msiexec /x, returns exit code + guest log path), `Save-LabAgentEvidence` |
| RemoteHub (next) | `Update-LabAgent`, `Set-LabAgentPolicy` (registry under `HKLM\SOFTWARE\Policies\RemoteHub\Agent`), `Invoke-LabSetupScript` |
| Evidence (next) | `Save-LabEvidence` → `results\<run>\<vm>\`: agent logs, Application/Security event log extracts (RemoteHub Agent source, 4624/4625), MSI logs, screenshots, `query session` output |

Rules: every function is idempotent where it can be; every wait has a timeout and a clear error ("RH-W11: no
desktop for labuser after 180 s"); waits poll a condition, never a fixed `Start-Sleep` alone.

## 9. Test scenarios

Pester files in `lab/tests/*.Tests.ps1`, each with `param($VmName, $EvidenceDir)`; `Invoke-LabTests.ps1` runs every
file once per VM. Each file: revert → set up → act → check → collect evidence. The parts that need RemoteHub's client
code call `dotnet test tests/RemoteHub.LabTests --filter "Lab=<id>"` with the VM's address and the agent fingerprint as
environment variables (later). Scenarios that need a VM that isn't provided yet (domain, RH-S22, RH-CLM) are skipped
until it exists.

| ID | Scenario | Checks |
|---|---|---|
| **SMOKE** | Revert, console, accounts, auto-logon | PS Direct answers with a full admin token; `UserAccount` not in Administrators; IPv4 address known; screenshot not blank; auto-logon reaches the desktop; code runs in the user's session |
| **W-HTTPS** | WinRM setup script, HTTPS | script runs without errors; prints a thumbprint; `FetchCertificateAsync` sees the same; certificate is end-entity, server auth only, key not exportable; HTTPS session pinned to it works; Windows' own HTTP rule off; our rule only for the given addresses |
| W-HTTP | WinRM setup script, HTTP, domain VM by name (needs domain) | Kerberos session works; built-in rule off; 5985 only from the given addresses |
| W-PRE | Built-in WinRM rule enabled before | script leaves it on and warns |
| W-TWICE | Script run twice | second run reuses the certificate (same thumbprint) |
| **A-INST** ✅ | Agent install with the client's `InstallOverRemoting` (runspace: PowerShell Direct; the WinRM transport itself is covered by W-*) | fingerprint read back = pinned; service `RemoteHubAgent` running, LocalSystem, automatic (delayed); `/v1/health` over TLS pinned from the host; certificate in LocalMachine\My, SHA-256 = pin, key not exportable; `agent.json` identity; one inbound rule for `RemoteHubAgent.exe`, TCP 58732, remote address = the host only; `%ProgramData%\RemoteHub\Agent` owner Administrators, protected, only SYSTEM + Administrators; no `.rhagent` or staging folder left; event source registered, enrollment logged |
| A-HAND | Install by hand (MSI next to `.rhagent`, run as the admin in the session) | the client's `WaitForManualInstall` marks it installed |
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
| D-DESKCLIP | Clipboard install while a desktop tab to another VM is open | client asks before copying the device key |

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
  `D:\RemoteHubLab\msi\`; the version in the report is read from the file name.
- Requests run one after another, and so do the VMs within a run. Only the VM under test is on: a run first turns off every lab VM (`Stop-LabVm`, hard off - every scenario reverts anyway), then turns each VM off again after its scenarios, also when they fail. Only VMs matching the lab name pattern are touched.
- Measured: SMOKE on W11-26H2-en-US ≈ 110 s. Target: one VM × all scenarios < 30 min.

## 11. Open questions

1. GPU partitioning: is there a host GPU that supports GPU-P? Otherwise D-GPU runs only on a physical test PC.
2. Domain VM: needed for W-HTTP and the domain case of P-ADMIN – provide one later, or drop those cases?
3. Should the lab also run in CI (self-hosted runner on the host, nightly), or only on demand and through the task?
4. Windows Insider / next Windows version as an extra VM for early warnings?
