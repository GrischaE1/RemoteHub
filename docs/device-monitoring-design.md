# RemoteHub – Device monitoring and inventory (DEX)

Status: Draft v1 (2026-11-09), a proposal – nothing is built yet. Written against main as of 0.22.x (agent 0.22, broker
protocol `remotehub-broker/1`). The open questions are in §19, the decisions taken so far in §20, §21 weighs a second database (ClickHouse, DuckDB)
against the encrypted SQLite the console has, and §22 puts numbers on data volumes and Cloudflare costs at 1, 4 and
8 hour intervals.
Owner: Grischa Ernst

## 1. Goal

RemoteHub so far shows a device only while an admin looks at it: a tab reads the device's processes, services or events
when it is open and forgets them when it closes. This design gives RemoteHub a **memory of every device** and a view
that puts what happened on a device in order – so that "the Intel graphics driver was updated on Tuesday, and since
then Zoom crashes twice a day" or "Teams updated itself at 09:12, and the CPU has been above 80 % since" can be seen
rather than guessed. That is the digital employee experience (DEX) approach: inventory, events and performance of one
device on one timeline.

| Part | What |
|---|---|
| **Collection on the device** | The RemoteHub Agent collects an **inventory** (installed apps, Windows updates, drivers, hardware, operating system, users and groups, configuration – Group Policy and MDM/CSP, services, security state) on a schedule: every **60 minutes** by default, configurable to any longer interval, never shorter (§6.1). |
| **Events** | The agent watches the device for things that matter and sends the part of the inventory they changed at once, not at the next interval: an app installed, updated or removed; a Windows update or a driver installed; an app crash or hang; a blue screen or an unexpected restart; a service that died; disk and hardware errors; Defender detections (§5.2). |
| **Performance and thresholds** | The agent samples CPU, memory, disk and network every 10 s, keeps 1- and 5-minute summaries, and raises an **alert** when a value stays above a threshold for a time – by default CPU at or above 80 % for 5 minutes. Thresholds, windows and the metrics watched are set by the admin (§5.4). An alert sends the recent samples and the processes behind them. |
| **Delivery** | The agent keeps everything in a local store with a bounded retention (§8). Where the device has a RemoteHub Broker, the agent **uploads** each bundle to the broker, sealed so only the organisation's consoles can read it, and consoles collect it from there (§9.3). Without a broker, consoles **fetch** from the agent on the same schedule whenever they run (§9.2). Either way nothing is lost while no console is running. |
| **In the console** | A **Device** tab per connection: an overview with the current health, then inventory sections, alerts and events – readable also while the device is off, from what was last received (§11.1). |
| **Timeline** | The DEX view in that tab: performance curves, alerts, changes (apps, updates, drivers, configuration) and problems (crashes, blue screens, service failures) on one time axis, zoomable from an hour to 90 days, with the changes that precede a problem pointed out (§11.2). |
| **Later** | A **Monitoring** view on the view rail for the whole fleet: dashboards (devices with open alerts, most crashing apps, pending restarts, disk space) and reports (§11.4, phase 5). |

### 1.1 Principles

1. **The agent collects, it never acts.** Monitoring reads the device: registry, WMI/CIM, the event logs, performance
   counters, the Windows Update history. It changes nothing on the device, installs nothing, kills nothing. The agent
   keeps its rule from [agent-spec.md](agent-spec.md) §2 – no command set of its own.
2. **Data goes only to the organisation.** Bundles go to the organisation's own consoles, directly or through its own
   RemoteHub Broker, sealed so the broker can't read them. Nothing goes to the vendor; the agent still sends no
   telemetry ([agent-spec.md](agent-spec.md) §8 changes its wording, §18).
3. **No secrets, and identities only where IT allows them.** Bundles never contain passwords, keys, product keys,
   recovery keys, file contents, command lines, window titles or clipboard. Account names (signed-in users, local
   accounts, group members, the MDM enrolment's user) are collected by default, because a DEX view is about a person's
   device, but the policy `MonitoringIncludeUsers` = 0 replaces them with *a user* (§12).
4. **Whatever comes from a device is untrusted.** A compromised device must not be able to harm the console through a
   bundle (threat boundary TB8, [security-test-lab-design.md](security-test-lab-design.md)): every bundle is parsed
   against limits, every string is clipped and shown as text, nothing in it is a link, a path to open or HTML (§14).
5. **The device must not notice.** Collection runs below normal priority, is spread out and stops when it takes too
   long. It must not itself be the reason the CPU is at 80 % (§15.2).
6. **Built on what is there.** The agent's TLS pin, Windows sign-in, rights and policy; the broker's seals; the
   console's encrypted database; the tool tabs' chrome and lists. Policy values go into `PolicyCatalog.cs` and the ADMX
   template like every other ([admx-design.md](admx-design.md)).
7. **Configured means locked.** Thresholds and the interval are device settings an admin changes from the console, but
   a policy value wins and shows locked ([admx-design.md](admx-design.md) P1).

## 2. Non-goals (v1)

- **Remediation.** No automatic restart of a crashing service, no uninstall of an app that crashes, no scripts run on an
  alert. An alert can offer *Run script…* and the device actions as a next step, each confirmed by the admin as today.
- **A server of RemoteHub's own.** Consoles keep what they collect in their own database; the broker stores sealed
  bundles for a limited time (§9.3) and doesn't read them. Fleet-wide history shared between admins waits for a server;
  the bundle format is made so a server can take it unchanged (§7).
- **Per-user experience scores, baselines and anomaly detection.** V1 has fixed thresholds the admin sets. A learned
  baseline ("this device is slower than usual") is listed in §17 as later work.
- **Software metering** (how long which app was in the foreground), browsing, keystrokes, screenshots. Not DEX as
  RemoteHub means it, and a privacy problem.
- **Patching and app deployment.** RemoteHub shows what is installed and what is pending; it doesn't install.
- **Agent-less devices.** Inventory needs the agent. A device reached over WinRM only can still be inspected with
  the remote tools and the script library's INV scripts as today; nothing is collected for it.
- **Temperatures and fan speeds.** Windows gives no reliable, vendor-independent reading. GPU load is listed as later
  work (§17).

## 3. What is at stake

| Asset | Threat | Where it is handled |
|---|---|---|
| The inventory of a device (apps, patch level, users, configuration) – a map for an attacker | Read by someone without the right; read in transit; read on the broker | Right `Monitoring` (§9.1), the agent's TLS pin and sign-in, seals on the broker (§9.3), the console's encrypted DB (§10) |
| The console | A device sends a bundle made to crash or exploit the console, or to mislead the admin | Limits and clipping (§14), bundles are data never instructions, the device's identity is its pinned certificate or its broker record |
| The device | Monitoring slows it down, fills its disk, or opens a new way in | Budget and retention (§8, §15.2); no new listener, no new input from the network except the settings endpoint, which needs a right (§9.1) |
| People at the device | Surveillance beyond what IT needs | §1.1 principle 3, `MonitoringIncludeUsers`, `MonitoringSections`, the Windows Application log records that monitoring runs (§13) |
| The broker | A device floods it, or a console reads another organisation's bundles | Per-device quota on the broker, organisation-scoped paths with the existing device and console proofs (§9.3) |

## 4. Architecture

```
Device                                                  RemoteHub Broker (optional)          Console(s)
┌─────────────────────────────────────────────┐        ┌──────────────────────────┐        ┌──────────────────────────┐
│ RemoteHubAgent (LocalSystem)                 │        │ per device: a mailbox of  │        │ MonitoringClient         │
│  Monitoring/                                 │ upload │ sealed bundles, 7 days    │ fetch  │  ├ from the agent (§9.2) │
│  ├ Collectors  – inventory sections (§5.1)   │ ─────▶ │ or 64 MB, whichever first │ ─────▶ │  └ from the broker (§9.3)│
│  ├ Watchers    – event logs, registry (§5.2) │        │ (the broker only stores)  │        │ MonitoringStore (SQLite, │
│  ├ Sampler     – PDH counters, 10 s (§5.3)   │        └──────────────────────────┘        │  encrypted; §10)         │
│  ├ Thresholds  – alerts with hysteresis(§5.4)│                                            │  ├ snapshots + diff →    │
│  ├ Bundler     – full / partial bundles (§7) │        direct, or a broker session:         │  │   change events        │
│  └ Store       – %ProgramData%\…\monitoring  │ ◀───── GET /v1/monitoring/…  (right         │  ├ events, alerts        │
│                  (§8)                        │        Monitoring)                          │  └ performance rollups   │
│  /v1/monitoring/{status,bundles,settings}    │ ◀───── PUT /v1/monitoring/settings (right   │ Device tab · Timeline    │
│                                              │        MonitoringSettings)                  │ Monitoring view (later)  │
└─────────────────────────────────────────────┘                                            └──────────────────────────┘
```

- **One source of truth on the device.** Every bundle is written to the device's store first (§8). The upload to the
  broker and the fetch by a console both read from that store, so the two paths deliver the same bundles and a console
  that missed some catches up from either side by sequence number.
- **Two delivery paths, one format.** The user's requirement – the device *sends* every 60 minutes – holds where a
  broker exists: the device uploads without any console being on. Without a broker there is nothing to send to: a
  console is a desktop app with no listener, often off, and there may be several. So consoles fetch on the same
  schedule when they run, and the device's store bridges the gaps. The console shows which path a device uses.
- **The console computes the differences.** The device sends inventory sections whole; the console keeps the last
  snapshot of each section and turns the difference into change events (*Zoom 6.2.1 → 6.3.0*, *driver igdkmd64 updated*,
  *GPO "Default Domain Policy" version 42 → 43*). The device does the same diff locally only to decide whether a change
  happened at all and to name it in the triggered bundle (§6.2), so both sides agree.
- **Through a jump host or the broker,** the agent's endpoints are reached as every agent endpoint is: inside the
  pinned TLS connection, with the connection's Windows credentials. No new trust path.

## 5. What is collected

### 5.1 Inventory sections

Each section is collected whole, as a JSON object, and sent whole. The sources are listed so the collector can be
reviewed against them; where a source needs confirming on real devices it says *lab*.

| Section | Contents | Source | Limits |
|---|---|---|---|
| `os` | Edition, version, build and UBR, display version (24H2), install date, last boot time, uptime, architecture, locale, time zone, activation state (licensed or not – never the key), pending restart (CBS `RebootPending`, WU `RebootRequired`, `PendingFileRenameOperations` present), Windows Update settings (active hours, pause state), Fast Startup on or off | Registry (`CurrentVersion`), `Win32_OperatingSystem`, `SLGetWindowsInformationDWORD` | – |
| `hardware` | Manufacturer, model, serial number, system SKU, chassis type; BIOS/UEFI vendor, version, date; Secure Boot on or off; TPM present, version, ready; processor (name, cores, threads, max clock, virtualisation on); memory (total, module count, size and speed per module, slots); disks (model, size, bus, media type SSD/HDD, SMART predicts failure, firmware); volumes (letter, label, file system, size, free, BitLocker protection status – never a key); graphics (name, driver version and date, VRAM); displays (count, resolutions); network adapters (name, MAC, speed, driver, DHCP, addresses, DNS servers); battery (present, design and full-charge capacity → health %, cycle count where reported); last connected monitor and dock (lab) | `Win32_ComputerSystem(Product)`, `Win32_BIOS`, `Win32_Processor`, `Win32_PhysicalMemory`, `MSFT_Disk`/`MSFT_Volume` (`root\Microsoft\Windows\Storage`), `MSStorageDriver_FailurePredictStatus`, `Win32_EncryptableVolume` (`root\CIMV2\Security\MicrosoftVolumeEncryption`), `Win32_VideoController`, `Win32_NetworkAdapter(Configuration)`, `Win32_Tpm` (`root\CIMV2\Security\MicrosoftTpm`), `Win32_Battery` + `BatteryFullChargedCapacity` (`root\WMI`), `UEFI SecureBoot` registry | 64 disks, 64 volumes, 64 adapters |
| `apps` | Installed programs: name, version, publisher, install date, size, scope (machine, or per user – user named only with `MonitoringIncludeUsers`), kind (MSI, EXE installer, Store), uninstall key path, whether a system component; Store apps: package family name, version, publisher, per user or provisioned | `HKLM\…\Uninstall` (64 and 32 bit), `HKU\<sid>\…\Uninstall` for every loaded user hive, the AppX package database through `PackageManager` (`Windows.Management.Deployment`, as `StoreApps.cs` already uses) | 10,000 entries; strings 256 characters |
| `updates` | Windows Update history (title, KB number, result, when, classification – security, feature, driver, definition), installed hotfixes, pending updates (title, KB, severity, whether a restart is needed), the last successful search and install times, the update source (Windows Update, WSUS server name, Intune) | Windows Update Agent COM (`IUpdateSession.CreateUpdateSearcher().QueryHistory`, `Search("IsInstalled=0")` with a 60 s stop), `Win32_QuickFixEngineering`, `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` | History 500 entries, pending 200 |
| `drivers` | Signed drivers: device class, device name, driver provider, version, date, INF name, signer, whether the device is working (problem code) | `Win32_PnPSignedDriver`, `Win32_PnPEntity` (problem codes) | 2,000 entries |
| `services` | Name, display name, start type, state, account kind (LocalSystem, NetworkService, a user – named only with `MonitoringIncludeUsers`), path (program only, no arguments), delayed start, triggers present | `QueryServiceConfig`, `EnumServicesStatusEx` (no WMI – faster) | 1,000 entries |
| `users` | Local users (name, SID kind, enabled, password required, last sign-in, in Administrators), local groups and their members (name, kind: local, domain, Entra ID), who is signed in now (session id, account, state, since, console or RDP), the device's domain or Entra ID join state (joined, hybrid, registered; tenant id; never a token). With `MonitoringIncludeUsers` = 0: counts only, members left out | `NetUserEnum`, `NetLocalGroupEnum`/`GetMembers`, `WTSEnumerateSessions`, `NetGetJoinInformation`, `dsregcmd`-equivalent registry (`HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin`) | 500 users, 200 groups, 2,000 members |
| `config` | **Group Policy:** applied GPOs for the computer (and the signed-in user with `MonitoringIncludeUsers`): name, GUID, version, link, enforced, last processing time and result; the effective policy values under `HKLM\SOFTWARE\Policies` and `HKCU\…` of the signed-in user (key, name, type, value – strings clipped to 256 characters; a value whose name contains *password*, *secret*, *key* or *token* is sent as `<left out>`). **MDM/CSP:** enrolment present, provider id (Intune, other), enrolment type, device id at the MDM, last sync time and result; the effective CSP policy values (`PolicyManager\current\device\<area>\<policy>`) and the user's; applied configuration profiles' ids where the CSP records them (lab). **Other:** Defender state (real-time protection, signature version and age, last scan, tamper protection), firewall profiles on or off, UAC level, RDP enabled, WinRM listeners, power plan, time source | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History` (+ the user's), `root\rsop\computer` only when the history is empty, `HKLM\SOFTWARE\Microsoft\Enrollments`, `HKLM\SOFTWARE\Microsoft\PolicyManager`, `MSFT_MpComputerStatus` (`root\Microsoft\Windows\Defender`), `MSFT_NetFirewallProfile`, registry | 5,000 policy values, 2,000 CSP values |
| `agent` | The agent's own version, policy values in force and their source (as `RemoteHubAgent.exe policy` prints – names and values, never the enrolment secret), rights in force (group names), monitoring settings in force, store size, last upload | `AgentConfig`, `AgentPolicy` | – |

Not collected, on purpose: certificates and their keys, Wi-Fi profiles, saved credentials, browser data, document
names, printer jobs, scheduled task actions' arguments, environment variables, the contents of any file.

### 5.2 Events watched on the device

The agent subscribes to the event logs with `EventLogWatcher` (a bookmark per log, kept in the store, so events while
the service was down are read at the next start – at most the last 24 hours) and to registry changes with
`RegNotifyChangeKeyValue`. Each watched event becomes a **monitoring event** of a fixed kind, with the fields the
timeline needs pulled out of the Windows event, plus the Windows record id so the admin can open the full event in the
Event Viewer tab. A kind that changes an inventory section also triggers that section (§6.2).

| Kind | Lane | Windows source and ids | Fields kept | Triggers |
|---|---|---|---|---|
| `app.installed`, `app.updated`, `app.removed` | Changes | Registry change on the Uninstall keys (machine and loaded user hives) and the AppX database – the agent re-reads `apps`, diffs it against its last copy and names what changed; MsiInstaller 1033/1034/1035/1036 and AppXDeploymentServer 854 only confirm the source (MSI, Store) | App name, old and new version, publisher, scope, how (MSI, Store, installer) | `apps` |
| `update.installed`, `update.failed` | Changes | System: Microsoft-Windows-WindowsUpdateClient 19, 20; Setup log (servicing) 1, 2; a feature update is noticed as a changed OS build at the next `os` collection and as a `device.restarted` after setup | Title, KB, result code, whether a restart is pending | `updates`, `os` – except definition updates (Defender signatures arrive several times a day): recorded, not triggered |
| `driver.installed`, `driver.failed` | Changes | System: Microsoft-Windows-UserPnp 20001 (install done), 20003; Microsoft-Windows-Kernel-PnP 400/410/411; the agent diffs `drivers` to name old and new version | Device, provider, old and new version, INF | `drivers` |
| `config.gpo` | Changes | Microsoft-Windows-GroupPolicy/Operational 8004–8007 (processing done), 1085/1125/1129 (failed); then a diff of the `config` GPO list and policy values | Scope (computer, user), result, which GPOs and how many values changed | `config` |
| `config.mdm` | Changes | DeviceManagement-Enterprise-Diagnostics-Provider/Admin sync-completed and failed events (ids confirmed in the lab, M-CFG), then a diff of the CSP values | Result, values changed | `config` |
| `config.security` | Changes | Windows Defender/Operational 5001/5010/5012 (protection turned off), 2000/2001 (signatures); firewall profile changes (Windows Firewall With Advanced Security/Firewall 2003/2004/2005 as confirmed in the lab) | What changed | `config` |
| `app.crash`, `app.hang` | Problems | Application: Application Error 1000, Application Hang 1002, Windows Error Reporting 1001 (bucket, for grouping), .NET Runtime 1026 | Program (name and version), faulting module and version, exception code, session's user (with `MonitoringIncludeUsers`) | – |
| `os.bluescreen`, `os.unexpectedRestart` | Problems | System: BugCheck 1001 (code and parameters, dump path – the path only, never the dump), Kernel-Power 41 (bug check code), EventLog 6008; a new file under `C:\Windows\Minidump` corroborates | Code, parameters, probable driver where BugCheck names one | `os` |
| `service.failed` | Problems | System: Service Control Manager 7031, 7034 (terminated), 7000, 7009, 7011, 7023, 7024 (start failed) | Service, what Windows did (restart, nothing) | – |
| `disk.error`, `hardware.error` | Problems | System: Disk 7, 51, 153, 157; NTFS 55, 98 (volume health), 140; storahci/stornvme 129; WHEA-Logger 17, 18, 19, 20, 47 (corrected and uncorrected hardware errors, source component) | Disk or component, counts (the same event within an hour is counted, not repeated) | `hardware` |
| `memory.low` | Problems | Microsoft-Windows-Resource-Exhaustion-Detector 2004 | Top consumers the event names (program names) | performance samples |
| `security.detection` | Problems | Windows Defender/Operational 1116, 1117, 1118, 1119 | Threat name, severity, action, path's folder only (no file name) | – |
| `device.restarted`, `device.shutDown`, `device.started` | Sessions | System: User32 1074 (who asked and the reason), Kernel-General 12/13, EventLog 6005/6006, Kernel-Boot (fast startup) | Reason, process that asked, initiating user (with `MonitoringIncludeUsers`), boot duration from Diagnostics-Performance/Operational 100 (`MainPathBootTime` + `BootPostBootTime`), shutdown duration from 200 | `os` |
| `session.signIn`, `session.signOut`, `session.lock`, `session.unlock`, `session.connect`, `session.disconnect` | Sessions | Microsoft-Windows-Winlogon 7001/7002, TerminalServices-LocalSessionManager/Operational 21–25, 39/40; sign-in duration measured by the agent from 7001 to the shell's first window (lab) | Session id, account (with `MonitoringIncludeUsers`), console or RDP, duration | none – a device sees dozens a day; they go with the next full bundle, and `users` is re-read only there |
| `time.changed` | Sessions | Kernel-General 1 (system time changed), with the old and new time | Delta | – |
| `agent.*` | Sessions | The agent's own audit events as they happen (sessions, refusals, broker) | Event id and the message | – |

Security 4624/4625 aren't watched: they are high-volume, and the agent already audits its own sign-ins. Everything not
listed is left to the Event Viewer tab, which stays as it is.

### 5.3 Performance samples

Every **10 s** the sampler reads these counters through PDH (`PdhCollectQueryData`; one query, opened once, no
`PerformanceCounter` – it enumerates the registry on every instance and is slow on first use):

| Metric | Counter | Kept as |
|---|---|---|
| `cpu` | `\Processor Information(_Total)\% Processor Utility` (falls back to `% Processor Time` on Windows 10 1809 where Utility is unreliable) | % |
| `cpuQueue` | `\System\Processor Queue Length` | count |
| `memory` | 100 − `\Memory\Available MBytes` / total | % used |
| `memoryCommit` | `\Memory\% Committed Bytes In Use` | % |
| `disk` | `\PhysicalDisk(_Total)\% Idle Time` inverted → active time; `\PhysicalDisk(_Total)\Avg. Disk Queue Length`; `\PhysicalDisk(_Total)\Avg. Disk sec/Transfer` | %, count, ms |
| `diskFree` | Free space of every fixed volume (`GetDiskFreeSpaceEx`), once a minute | % and GB per volume |
| `network` | `\Network Interface(*)\Bytes Total/sec`, `Packets Outbound Errors`, `Packets Received Errors` summed over physical adapters | Mbit/s, errors/min |
| `gpu` | Later (§17): `\GPU Engine(*)\Utilization Percentage` needs per-process instance handling |
| `processes` | Top 5 by CPU and top 5 by working set, from `NtQuerySystemInformation(SystemProcessInformation)` deltas – program name and PID only, never command lines | per 1-minute summary |
| `sessions` | Number of active user sessions | count |

Summaries: the 10 s samples are folded into **1-minute** summaries (average, maximum, top processes) kept for 48 hours
on the device, and **5-minute** summaries (average, maximum, minimum) kept for the store's retention. The hourly bundle
carries the hour's 5-minute summaries; an alert bundle carries the 1-minute summaries of the 15 minutes before and the
10 s samples of the alert's window, so the console can show the exact shape of the spike. Nothing is sampled per user.

Samples carry the device's clock (UTC) and the monotonic uptime, so a `time.changed` event doesn't tear the curve: the
console places samples by the monotonic offset from the bundle's own time stamp when the two disagree.

### 5.4 Thresholds and alerts

An **alert rule** is a metric, a threshold, a window and a severity. The defaults:

| Rule | Default | Range |
|---|---|---|
| CPU | ≥ 80 % for 5 min | 50–100 %, 1–60 min |
| Memory used | ≥ 90 % for 5 min | 50–100 %, 1–60 min |
| Disk active time | ≥ 90 % for 5 min, or queue length ≥ 2 for 5 min | 50–100 %, 1–60 min |
| Free space (any fixed volume) | < 10 % or < 5 GB | 1–50 %, 1–100 GB |
| Network errors | ≥ 100 errors/min for 5 min | 1–10,000, 1–60 min |
| Boot duration | > 120 s | 30–900 s |
| Sign-in duration | > 60 s | 10–600 s |
| App crashes | ≥ 3 crashes of the same program within 24 h | 1–100, 1–168 h |
| Service failures | any `service.failed` of an automatic service | on or off |
| Blue screen, hardware error, Defender detection | always an alert | – |

Each rule can be turned off. The admin sets them per device from the console (§11.3), or IT sets them by policy for
every device (§12); the agent evaluates them, so an alert is raised whether or not a console is running.

**How a sustained rule is evaluated** (`ThresholdState`, one per rule, unit-tested with a fake clock):

- The 10 s samples feed a rolling **average over the window**. The alert **opens** when the average is at or above the
  threshold and at least 90 % of the window's samples are above it – a single 10 s spike in a quiet 5 minutes doesn't
  count, and neither does a busy 4 minutes.
- It **closes** when the average falls below the threshold minus a hysteresis of 10 points (CPU 80 → below 70) for a
  full window, so a value hovering around the threshold doesn't open and close an alert every minute.
- Opening sends an **alert bundle** at once (§6.2); closing sends an **alert closed** event with the duration, peak and
  average, and the processes that were on top most often. While open, no second bundle for the same rule; a
  **cool-down of 15 minutes** after closing before the same rule can open again.
- The first 5 minutes after boot and the first window after the service starts don't open alerts (Windows is busy then
  by itself); boot duration has its own rule.
- An alert the agent raises goes into the device's own Application log as event 2500 (§13), so Intune or a SIEM can
  pick it up without RemoteHub.

### 5.5 Never collected

Passwords, hashes, tickets, keys of any kind (BitLocker recovery, Wi-Fi, product, API), certificates' private parts,
file contents, command lines, environment variables, window titles, clipboard, keystrokes, screenshots, browser data,
e-mail, document names, per-user application usage time. The agent's own enrolment secret and broker keys are never in
`agent`. A collector that would need any of these to work is a design error, not a configuration.

## 6. Schedules and triggers

### 6.1 The interval

- `monitoring.intervalMinutes` (agent.json) or the policy `MonitoringIntervalMinutes`: **60 by default, 60 to 10,080**
  (a week). A value below 60 is invalid and keeps 60 – an hour is the floor the requirement set, and the collectors'
  budget (§15.2) is made for it.
- Each device adds a fixed **jitter** (0–10 minutes, derived from the device id) so a fleet installed by Intune at the
  same minute doesn't upload to the broker at the same second.
- The first inventory runs **5 minutes after the service starts** (or 15 minutes after boot, whichever is later), then
  every interval. A missed run (the device slept) runs on resume, after the same 5 minutes.
- The hourly bundle is **full**: every enabled section (`MonitoringSections`), the hour's 5-minute summaries, the
  events of the hour that weren't sent by a trigger, and the open alerts. A section that didn't change still goes, as a
  hash only (§7): the console knows it is unchanged without receiving it again, and a console that never saw it asks
  for it (§9.2).

### 6.2 Triggered bundles

- A watched event (§5.2) that names a section makes the agent **re-collect that section after a 30 s debounce** (an
  installer writes several keys; a Windows Update installs several packages), diff it against the last copy, and send a
  **partial bundle**: the event(s), the changed section whole, and nothing else. An event without a section (a crash)
  is sent in a partial bundle with the last 15 minutes of 1-minute summaries, so the timeline shows the load around it.
- An alert opening sends the alert, the samples of §5.3 and the current `processes`. An alert closing sends the closed
  alert.
- **Flood control:** at most **30 triggered bundles per hour** per device; beyond that, events are kept in the store
  and go with the next hourly bundle, and the hourly bundle says how many were held back. Repeated identical events
  (the same crash of the same program, the same disk error) within 10 minutes are counted into one event with a count.
- Nothing is triggered during the first 5 minutes after boot; those events go with the first hourly bundle.

### 6.3 Sequence numbers

Every bundle gets the next number of a **per-device monotonic sequence** (persisted in the store before the bundle is
written, as `agent.json` is written: temp file, flush, replace). The console asks for "everything after N" on both
paths, so nothing is received twice and a gap is visible. A device enrolled again (new identity, [agent-spec.md](agent-spec.md)
§5.3) starts at 1 with a new `storeId`; the console keeps the old history under the connection and marks the cut.

## 7. Bundles

A bundle is a UTF-8 JSON document, **gzip-compressed**, with the envelope:

```json
{
  "format": 1,
  "deviceId": "3f1a9e2c-…",
  "storeId": "a1b2…",            // new at each enrolment; the console tells a re-enrolled device by it
  "seq": 4711,
  "kind": "full" | "partial" | "alert",
  "time": "2026-11-09T09:00:03Z",  // the device's clock, UTC
  "uptimeMs": 1234567,              // monotonic, for placing samples when the clock jumped
  "agentVersion": "0.23.0",
  "intervalMinutes": 60,
  "sections": {
    "apps":     { "hash": "sha256-…", "collectedAt": "…", "items": [ … ] },
    "drivers":  { "hash": "sha256-…" },                      // unchanged: hash only
    …
  },
  "events":   [ { "id": "…", "kind": "app.crash", "time": "…", "recordId": 12345, "log": "Application", … } ],
  "alerts":   [ { "id": "…", "rule": "cpu", "state": "open" | "closed", "openedAt": "…", "closedAt": null, "peak": 97.2, "average": 88.1, "top": [ … ] } ],
  "performance": { "step": 300, "from": "…", "series": { "cpu": { "avg": [ … ], "max": [ … ] }, … }, "samples": { "step": 10, … } },
  "heldBack": 0
}
```

- **Limits** (checked by the console before parsing further, and by the agent before writing): compressed ≤ 4 MB,
  uncompressed ≤ 32 MB, nesting depth ≤ 8, strings ≤ 1,024 characters (section-specific limits in §5.1), arrays as
  in §5.1, at most 500 events and 50 alerts per bundle. A bundle over a limit is refused whole and counted
  (`MonitoringStore.Refused`); the agent never produces one (it clips while collecting), so a refused bundle means a
  changed device.
- **Unknown fields and sections are ignored**, not refused: an older console reads a newer agent's bundle and shows
  what it knows. `format` changes only when the envelope changes incompatibly.
- Section hashes are SHA-256 over the canonical JSON of `items` (sorted keys, as `BrokerProtocol.Canonical`), so the
  device and every console agree on "unchanged".
- The format is what a RemoteHub Server would store later: self-describing, per device, ordered, with the device's own
  time. Nothing in it depends on the broker or on a console.

## 8. The device store

`%ProgramData%\RemoteHub\Agent\monitoring\` – inside the agent's folder, so it has the folder's ACL (SYSTEM and
Administrators only, [agent-spec.md](agent-spec.md) §5.3). No database: the agent has no SQLite dependency, and a
LocalSystem service shouldn't gain a native one for this. Files, written with `DurableFile`:

| File | Contents |
|---|---|
| `state.json` | `storeId`, next `seq`, event-log bookmarks, last hash per section, the thresholds' state, last upload per broker, settings in force |
| `bundles/<seq>.rhmon` | One bundle each (gzip JSON, §7). Kept for `MonitoringRetentionDays` (default 30, 1–90) or until the store reaches **256 MB**, oldest first – the bundles a console hasn't fetched yet are the last to go, and a device with no console for a month still has its last full bundle |
| `sections/<name>.json` | The last copy of each section, for the diffs of §6.2 |
| `perf/<yyyyMMdd>.bin` | The day's 1-minute summaries, a fixed binary record per minute (about 200 bytes), 48 hours kept; the 5-minute summaries live in the bundles |
| `upload.json` | Which bundles the broker has acknowledged (§9.3) |

- Uninstall (`RemoteHubAgent.exe cleanup`) removes the folder. `MonitoringEnabled` = 0 stops collecting and uploading
  and deletes the store at the next service start, so turning monitoring off by policy leaves no inventory behind.
- A damaged `state.json` (as `agent.json` in [agent-spec.md](agent-spec.md) §7.2) restarts the store with a new
  `storeId`; bundles that are there stay readable. Nothing here can stop the service from starting – a monitoring error
  is logged (3500) and monitoring retries at the next interval.

## 9. Wire

### 9.1 The agent's endpoints

Same listener, same TLS pin, same HTTP Basic Windows sign-in and `SignInGate` as `/v1/powershell`
([agent-spec.md](agent-spec.md) §4, §6). New rights in `Rights`: **`Monitoring`** (read) and **`MonitoringSettings`**
(change thresholds and the interval), both defaulting to `allowedGroups` like `PowerShell`.

| Method | Path | Right | Purpose |
|---|---|---|---|
| GET | `/v1/monitoring/status` | Monitoring | `{enabled, storeId, lastSeq, lastFull, nextFull, intervalMinutes, settings, settingsSource (policy/device/default), openAlerts, storeBytes, broker: {uploads: true/false, lastUpload, acknowledgedSeq}, time}` – `time` is the device's clock, so the console can note a skew |
| GET | `/v1/monitoring/bundles?after=<seq>&max=<n>` | Monitoring | The bundles after `seq`, oldest first, as one gzip stream of length-prefixed bundles, at most `n` (default 50) or 16 MB; the response says whether more follow. A new console (`after=0`) gets the newest **full** bundle first and then only bundles after it – it needs no history to show the device as it is now, and can ask for older ones with `before=<seq>` to fill the timeline |
| GET | `/v1/monitoring/sections?names=apps,drivers` | Monitoring | The last copy of the named sections, whole – for a console whose last snapshot is missing when it receives a hash-only section |
| GET | `/v1/monitoring/now` | Monitoring | Collects the named sections now and the current 10 s samples, without writing a bundle (*Refresh now* in the Device tab, at most once a minute per account) |
| PUT | `/v1/monitoring/settings` | MonitoringSettings | The device settings of §12.2 as JSON. Values a policy configures are refused with 409 and the policy's name; invalid values 400 with the reason. Written to `agent.json`, audited (1402) |
| GET | `/v1/monitoring/settings` | Monitoring | The settings in force and their source |

- Health (`/v1/health`) lists the feature **`monitoring`** when `MonitoringEnabled` is on, so a console knows before
  signing in; an older agent lists nothing and the Device tab says the agent needs updating.
- Status codes as in [agent-spec.md](agent-spec.md) §6: 401 and 403 from the sign-in, 404 when `MonitoringEnabled` = 0,
  429 from the gate. A session here counts against `maxSessions` while the request runs, not longer.
- Through a jump host and through the broker these requests travel as every agent request does – the console opens
  them through the same transport it uses for the PowerShell endpoint, so routing, pinning and *Reach* need no change.

### 9.2 Console fetch (direct, jump host or broker session)

`MonitoringClient` in the console, one scheduler for all connections with an agent that offers `monitoring`:

- **When:** at the console's start (spread over the first 5 minutes, at most 8 devices at once), then **every
  `intervalMinutes` of the device** (it learns it from `status`), when the connection is used (a tab opens), when the
  broker reports a device online that was offline, and on *Refresh now*. A device that doesn't answer is retried at
  the next interval, not sooner – a fetch never wakes a VPN or shows a sign-in error; it fails quietly and the Device
  tab shows *Last received: 3 h ago*.
- **With which credentials:** the connection's saved credentials, as the other tools do. A connection without saved
  credentials isn't fetched in the background (no prompt from a background task); its Device tab says so and offers
  *Fetch now…* with a prompt.
- **What it asks:** `bundles?after=<last seq the console has>`; on `storeId` change, `after=0` and the cut is marked.
  A hash-only section whose snapshot the console lacks is followed by `sections?names=…`.
- **Cost:** a device with nothing new answers `status` only (one small request per interval). The console never
  fetches `now` in the background.

### 9.3 Through the RemoteHub Broker: the device uploads

This path needs a broker change (the broker is a service of its own; its repository is outside this one). It is listed
here as the contract the agent and the console are written to, in the broker's own terms (`BrokerProtocol.cs`):

- **A mailbox per device:** `PUT /v1/o/{org}/devices/{deviceId}/monitoring/{seq}` with the device's existing
  registration proof; the body is the **sealed bundle**. The broker keeps a device's bundles for **7 days or 64 MB**,
  whichever is reached first, oldest first, and answers with the highest `seq` it holds (the agent's
  acknowledgement). A quota of 60 uploads per device per hour, 4 MB each; more is 429 and the agent waits for the
  next interval.
- **Where the broker keeps them:** in its existing D1 database, a table of its own (`org, device_id, seq, time, size,
  body BLOB`) with the retention run by the broker's scheduled worker. D1's row limit is 2 MB, so a bundle over 1.9 MB
  is stored in numbered chunks and reassembled on read – rare, since a full bundle is a few KB (§22). §22 shows D1 is
  the cheaper store up to the 10 GB a D1 database may hold (about 9,000 devices in one organisation at the 7-day
  retention); an organisation beyond that gets its mailbox in R2 instead, behind the same paths. Nothing on the agent
  or console side depends on which.
- **Consoles read:** `GET /v1/o/{org}/devices/{deviceId}/monitoring?after=<seq>` with the console's proof, for every
  device in the organisation's synced folder the console has; the broker also pushes a `monitoring` notice on the
  console's existing connection when a device uploads, so the console fetches within seconds while it runs. A console
  that was off for a week asks the device itself for what the broker dropped (§9.2), through the broker session.
- **The seal:** a key for monitoring is derived from the organisation's **device secret** like the other keys
  (`BrokerProtocol.DeriveKey`, new `KeyLabel.Monitoring`), so the device has it, every console has it (the keyring gives
  consoles the device secrets – `OpenKeyring`), and the broker never does: it gets only the enrol and join keys. The
  bundle is encrypted with **AES-256-GCM** under that key, nonce random 12 bytes, associated data
  `remotehub-broker/1|<org>|monitoring|<deviceId>|<storeId>|<seq>`, so a bundle can't be moved to another device or
  sequence number by whoever holds the broker. The broker sees sizes and times only. A device secret that is retired
  retires its monitoring key with it; bundles sealed under it are still readable by consoles whose keyring still holds
  the secret, as records are.
- **Which path a device uses:** `monitoring.uploadToBroker` (default on) on a device that has joined a broker. The
  console fetches from the broker's mailbox first and only then from the device, and shows *through RemoteHub Broker*
  on the Device tab's last-received line. A device with `BrokerDirect` = 0 or `ListenEnabled` = 0 is served entirely
  by the mailbox and the broker session.
- **Until the broker has it,** the agent keeps uploading nothing (the broker answers 404 for the path) and logs it
  once per service run; consoles fetch through the broker session. The console shows the device as *fetched*, not
  *sent*.

## 10. Console storage

New tables in the encrypted local database ([local-db-design.md](local-db-design.md) §6), schema version +1, in
`Data/MonitoringStore.cs`:

```sql
device_monitoring (connection_id TEXT PK, device_id TEXT, store_id TEXT, last_seq INT, last_full_utc INT,
                   last_received_utc INT, path TEXT, interval_minutes INT, clock_skew_s INT, refused INT)
device_sections   (connection_id TEXT, name TEXT, hash TEXT, collected_utc INT, received_utc INT, data BLOB,
                   PRIMARY KEY (connection_id, name, collected_utc))        -- gzip JSON; one row per change, the newest is current
device_events     (id TEXT PK, connection_id TEXT, time_utc INT, kind TEXT, lane TEXT, severity INT, subject TEXT,
                   title TEXT, detail TEXT, count INT, record_id INT, log TEXT, seq INT, source TEXT)
                                                                            -- source: 'device' (watched) or 'diff' (the console's comparison)
device_alerts     (id TEXT PK, connection_id TEXT, rule TEXT, opened_utc INT, closed_utc INT, peak REAL, average REAL,
                   detail TEXT, acknowledged_utc INT)
device_perf       (connection_id TEXT, hour_utc INT, step INT, data BLOB,
                   PRIMARY KEY (connection_id, step, hour_utc))               -- one row per device and hour: every metric's points packed
                                                                            -- (float32 avg/max/min, top processes), gzip; step 300 for the
                                                                            -- retention, 60 and 10 for 7 days
device_perf_days  (connection_id TEXT, day_utc INT, metric TEXT, avg REAL, max REAL, p95 REAL, above_threshold_min INT,
                   PRIMARY KEY (connection_id, metric, day_utc))             -- maintained on import; what fleet dashboards read
device_apps       (connection_id TEXT, key TEXT, name TEXT, version TEXT, publisher TEXT, scope TEXT,
                   PRIMARY KEY (connection_id, key))                         -- the current apps section, one row per app, for
                                                                            -- "which devices have Zoom 6.2.1" without opening blobs
```

- **Retention** (*Settings → Monitoring*, console policy `MonitoringRetentionDays` 7–3650, default **90**): sections
  keep every change, events and alerts and 5-minute performance for the retention, finer performance for 7 days. A
  deleted connection takes its monitoring rows with it to *Recently deleted* and for good when that is emptied.
- **The diff engine** (`SectionDiff`, pure and unit-tested): for each section a key per item (`apps`: scope + uninstall
  key path; `drivers`: device instance + INF; `updates`: KB + title; `services`: name; `users`: SID; `config`: GPO GUID,
  policy value path) and a list of fields whose change is worth an event. Added, removed and changed items become
  `device_events` with `source = 'diff'`, with the **time of the device's own event when one matches** (the watched
  `app.installed` from §5.2 for the same key within the debounce) and otherwise the section's `collectedAt` – the event
  then says *between 08:00 and 09:00* in the timeline.
- **Sizes, honestly estimated** (§21.1 has the arithmetic): about 80 KB a day per device for the 90-day history
  (5-minute performance about 15 KB, events about 25 KB, a section change about 40 KB), plus about 200 KB a day per
  device for the 1-minute performance kept 7 days. 50 devices for 90 days are about 0.4 GB; **500 devices about
  4 GB**, of which 0.7 GB is the 1-minute history. That is within SQLite's range but beyond what
  [local-db-design.md](local-db-design.md) §7 measured, so phase 1 measures import and query times at 500 devices and
  the retention defaults (§12.1) are confirmed against them. The performance rows are packed per hour and the daily
  summaries are kept separately, so the timeline reads a few hundred rows and a fleet dashboard reads the summary
  table, never the packed points (§21.2).
- **Full-text search** (`device_sections_fts` over app names, publishers, KB numbers, driver names) feeds the
  navigation search later (*which devices have Zoom 6.2.1?* – phase 5).

## 11. The console

No design (picture or ticket) came with the request, and no existing page shows a device's history, so the pages below
are proposed as trees of the project's own pieces, in the order they are read. The tool tabs (Event Viewer, Scheduled
tasks) are the nearest pages and give the chrome, lists and filters; the Notes and Scripts views give the cards and
splitters. The one new piece the request names is the **timeline**; §11.2 says where it stands and what it is built
from.

### 11.1 The Device tab

Opened with right-click → **Device** on a connection with an agent (also from the session tab's menu, and as the first
tab when *Open the Device tab with a connection* is on in *Settings → Monitoring*). Gated by the console feature
`AllowMonitoring` (§12.1). It reads the console's store, so it opens at once and works while the device is off.

```
┌ ● NUC13  ·  Device  ·  nuc13.lab  [RemoteHub Agent] ──────────────────────────── Last received 09:00 (through RemoteHub Broker) ↻ ┐
│ Overview   │ ┌ CPU ────────┐ ┌ Memory ─────┐ ┌ Disk C: ────┐ ┌ Uptime ──────┐ ┌ Updates ─────┐ ┌ Alerts ──────┐            │
│ Timeline   │ │ 23 %  ▁▂▃▂▁ │ │ 61 %  ▃▃▄▃▃ │ │ 71 % used   │ │ 3 d 4 h      │ │ 2 pending    │ │ 1 open       │            │
│ Alerts  ●1 │ │ max 97 % ⚠  │ │             │ │ 54 GB free  │ │ restart due  │ │ KB5064081    │ │ CPU ≥ 80 %   │            │
│ Apps       │ └─────────────┘ └─────────────┘ └─────────────┘ └──────────────┘ └──────────────┘ └──────────────┘            │
│ Updates    │                                                                                                                 │
│ Drivers    │ Recent                                                                                                          │
│ Hardware   │ ⚠ 08:41  CPU at or above 80 % for 5 min – Teams.exe, msedge.exe                                 open 19 min    │
│ OS         │ ↑ 08:37  Microsoft Teams 24295 → 24335                                                                          │
│ Users      │ ✖ Yesterday 17:02  Zoom.exe crashed (ntdll.dll, 0xc0000005) – 3rd time since the display driver update          │
│ Config     │ ↑ Tue 11:30  Intel Graphics 32.0.101.6078 → 32.0.101.6129                                                        │
│ Events     │                                                                                                                 │
│ Settings   │                                                                                                                 │
└────────────┴─────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The tree of roles and the pieces at each:

| Role | Piece | Notes |
|---|---|---|
| Header | `RemoteToolChrome.Header` – stripe, icon `SymbolRegular.Pulse24`, connection name, *Device*, host, transport badge | The right end gets the **last-received line** and a *Refresh now* button (`SymbolRegular.ArrowSync24`), as the lists have F5 |
| Section list | A `ListView` of sections on the left, like the Scheduled tasks tab's folder pane (`RemoteListView` side pane) | Width saved in `UiState.DeviceSectionsWidth`; a count badge on *Alerts* in `SessionStateToBrush.RedBrush`/`AmberBrush` by the highest open severity |
| Overview | `Card`s in a `WrapPanel`: six **stat tiles** (value, one-line caption, a 24-point sparkline of the last 2 hours) and a *Recent* list of the last 10 events and alerts | Tiles use the theme's text brushes; the sparkline is the timeline control (§11.2) in its smallest form, so there is one way to draw a curve |
| Timeline | §11.2 | |
| Alerts | `RemoteListView`-style list: state dot, opened, rule, duration, peak, top processes; the selected alert's detail below (samples as a small timeline, *Acknowledge*, *Open timeline here*, *Monitoring settings…*) | Acknowledging is the console's own note (`acknowledged_utc`); it changes nothing on the device |
| Apps, Updates, Drivers, Services, Users | `RemoteListView` lists with the filter box, sortable columns, and a **changes** toggle (*Show changes in the last 7 days*) that marks added, changed and removed rows with the colour dots the tree uses | Columns per section follow §5.1; a right-click offers *Copy*, *Show in timeline*, and for an app *Processes* (opens the Processes tab filtered to its program) |
| Hardware, OS, Config | A two-column property list in `Card`s per group (as *Settings → Policies* lists values), with *Copy as text*; `config` has three groups – Group Policy, MDM/CSP, Security – each with its own filter box | Values that changed in the last 7 days get the change dot and a tooltip with the old value and when |
| Events | The watched events (§5.2), newest first, with the lane filter (*Changes*, *Problems*, *Sessions*) and the Event Viewer's level dots; *Open in Event Viewer* opens that tab at the record | |
| Settings | §11.3 | |
| Empty states | `EmptyHint` as in Notes: *Nothing received yet – the agent collects its first inventory 5 minutes after it starts*; *Monitoring is turned off on this device (policy)*; *This agent is older than 0.23 and doesn't collect*; *No saved credentials – Fetch now…* | Each names what to do, never a code |

Everything shown is text from the store, clipped as it was received (§14): no cell is a hyperlink, no path opens
anything.

### 11.2 The timeline

The new piece: `Views/DeviceTimeline.cs`, a `FrameworkElement` drawn with `DrawingContext` (no charting library – none
is in the project, and the lists and the recording toolbar draw themselves the same way), used at three sizes: the
tiles' sparkline, the alert detail, and the full Timeline section.

```
┌ Timeline ────────────────────────── [1 h] [6 h] [24 h] [7 d] [30 d] [90 d]  ◀ ▶  Today ─ ⊕ ⊖ ─────────────────────┐
│ CPU %      100 ┤                      ╭──╮                              ╭─────────────                                 │
│             80 ┤·····················╭╯··╰╮····························╭╯·········································· │
│              0 ┼───────────────────╯      ╰────────────────────────────╯                                                │
│ Memory %   100 ┤──────────────────────────────────────────────────────────────────────────                              │
│ Disk %     100 ┤        ╭╮                                                                                              │
│ Alerts         │        ▓▓▓▓ Disk ≥ 90 %                                ▓▓▓▓▓▓▓▓▓▓▓▓ CPU ≥ 80 % (open)                 │
│ Changes        │  ↑ Intel Graphics driver           ↑ Teams 24335        ↑ KB5064081                                     │
│ Problems       │                     ✖ Zoom.exe   ✖ Zoom.exe     ✖ Zoom.exe   ⚠ WHEA corrected error ×12                │
│ Sessions       │  ⟳ restart (update)   ● sign-in j.doe                                  ○ lock           ● unlock         │
│                └──┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────┬──┘
│                 Mon 10 Nov  06:00      08:00      10:00      12:00      14:00      16:00      18:00      20:00      22:00   │
│ Since the Intel Graphics driver update (Tue 11:30): 3 crashes of Zoom.exe – none in the 7 days before.  [Show in Apps]      │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Lanes**, top to bottom: the performance curves (CPU, memory, disk; network and queue length can be switched on),
  then **Alerts** as bands, **Changes**, **Problems** and **Sessions** as markers. Lanes can be hidden; the choice is
  kept in `UiState`. The dotted line is the rule's threshold.
- **Zoom** from 1 hour to 90 days with the buttons, Ctrl+wheel or a drag on the axis; pan with the arrows, a drag, or
  Shift+wheel. The curve's resolution follows the zoom: 10 s samples where the console has them (alerts), 1-minute for
  up to 24 hours, 5-minute beyond. A gap in the data (device off, nothing received) is a gap in the curve, never
  interpolated; a `time.changed` event is a mark on the axis.
- **Hover** shows a tooltip with the value or the event's title and time; **click** selects the event or alert and
  shows its detail in a pane below (the same detail the Alerts and Events sections show); double-click a change opens
  its section with the item selected.
- **Correlation, kept honest:** when a change is selected, the footer counts the problems of the same subject after
  it against the same span before it (*3 crashes of Zoom.exe since – none in the 7 days before*); when a problem is
  selected, the footer lists the changes in the 7 days before it, nearest first. It is a count, labelled as one –
  RemoteHub doesn't claim a cause. The subject of a change is its program (an app's uninstall entry and its crashing
  executable are matched by publisher and install folder from `apps`) or, for a driver, the device class; where no
  match exists the footer says *no related problems recorded*.
- **Drawing:** brushes from the theme only – the text and stroke brushes for the axis and grid, the accent for the
  selected item, `SessionStateToBrush` red, amber, green and grey for severities and states, the connection's colour
  for the curve's fill. Both themes are checked (the lab's U cases screenshot the tab in light and dark). Text in the
  lanes is clipped to the lane; the full title is in the tooltip.
- **Keyboard:** Left and Right move between events, Home and End to the ends, +/− zoom, Enter opens the detail; the
  control is one `AutomationPeer` with the events as children, so FlaUI (the lab) and a screen reader see them.
- **Recording** (`IRecordableView`) includes the tab, as every tab.

### 11.3 Monitoring settings

- **Per device:** the Device tab's *Settings* section shows the interval, the sections collected, the upload switch and
  every rule of §5.4 as a row with on/off, threshold and window (`NumberBox`es with the ranges of §5.4), with the value's
  source (*device*, *policy*, *default*). *Apply* sends `PUT /v1/monitoring/settings`; a value the policy configures is
  greyed with the lock and *Set by your organisation*, as everywhere ([admx-design.md](admx-design.md) P2). Needs the
  right `MonitoringSettings`; without it the section is read-only and says so.
- **For many devices:** right-click a folder → *Monitoring settings…* opens the same form with *Apply to every device in
  this folder* (and its subfolders), runs device by device like *Claim RemoteHub Agents…*, and reports which took it,
  which refused (policy) and which weren't reached. Nothing is inherited or stored on the console: the device holds
  its settings, so two consoles can't disagree.
- **Console defaults** (*Settings → Monitoring*): the values pre-filled for a device that has none yet, offered by the
  install dialog and written into the `.rhagent` enrolment file (`monitoring` object, format 2 stays – unknown fields
  were always ignored by older agents, to confirm in A-HAND cases) so Intune installs start with them; the retention
  (§10); *Open the Device tab with a connection*; *Show alerts in the navigation*.
- **The navigation:** with that setting on, a connection with an open alert shows the alert dot on its icon (the
  broker state dot's position; red for a blue screen, hardware error, detection or a crash alert, amber for a
  threshold), and a folder shows the count of such devices in its caption. Turning it off keeps the Device tab's badge.

### 11.4 The Monitoring view (phase 5)

A fourth view on the rail – `RailView("Monitoring", SymbolRegular.DataUsage24, "Monitoring", "Ctrl+4")`, gated by
`AllowMonitoring` like Notes and Scripts ([notes-view-design.md](notes-view-design.md) §3.8). Three cards as in Scripts:
a list of **dashboards and reports** on the left, the chosen one in the middle, its filters on the right.

Built-in dashboards (tables and tiles from the store; each row opens the device's tab at the right section):
*Open alerts*, *Devices not heard from* (longer than 2 intervals), *Pending restarts*, *Disk space*, *Crashing apps
(last 7 days)*, *Blue screens and hardware errors*, *Recent changes*, *Patch level* (the newest cumulative update per
build and who lacks it), *Drivers by version*, *Defender state*. **Reports** are the same tables for a chosen folder
and period, exported as CSV or an HTML file (with the timeline as an SVG), through *Save as…*, never written to a
temp file. Which of the request's "dashboards and reports" come first is an open question (§19).

## 12. Policy and settings

### 12.1 Console

| Value (`HKLM|HKCU\SOFTWARE\Policies\RemoteHub`) | Type | Meaning (default) |
|---|---|---|
| `AllowMonitoring` | DWORD | 0: no Device tab, no fetching, no Monitoring view, and the store is emptied at the next start (1). Below `AllowAgent`, like the other agent tools |
| `MonitoringRetentionDays` | DWORD 7–3650 | How long the console keeps history (90) |
| `MonitoringFetch` | DWORD | 0: fetch only when a Device tab is open, never in the background (1) |

### 12.2 Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`, ADMX category *RemoteHub Agent → Monitoring*)

The same names, without the prefix, are the `monitoring` object in `agent.json` (written by the enrolment file and by
`PUT /v1/monitoring/settings`); the policy wins and is shown locked (P1). Read when used, as the other agent policies.

| Value | Type | Meaning (default) |
|---|---|---|
| `MonitoringEnabled` | DWORD | 0: nothing is collected or served; `/v1/monitoring/*` answers 404, health leaves out `monitoring`, the store is deleted at the next service start (1) |
| `MonitoringIntervalMinutes` | DWORD 60–10080 | The full inventory's interval (60) |
| `MonitoringSections` | MULTI_SZ | Which sections are collected: any of `os`, `hardware`, `apps`, `updates`, `drivers`, `services`, `users`, `config`, `agent` (all) |
| `MonitoringIncludeUsers` | DWORD | 0: no account names anywhere – sessions, local users, group members, crash and sign-in events say *a user*; per-user app entries and the user's GPOs and CSP values are left out (1) |
| `MonitoringEvents` | DWORD | 0: no event watching, only the interval (1) |
| `MonitoringPerformance` | DWORD | 0: no sampling, no threshold alerts (1) |
| `MonitoringUploadToBroker` | DWORD | 0: never upload; consoles fetch (1) |
| `MonitoringRetentionDays` | DWORD 1–90 | The device store's retention (30) |
| `MonitoringCpuPercent`, `MonitoringCpuMinutes` | DWORD | 0 turns the rule off; else 50–100 and 1–60 (80, 5) |
| `MonitoringMemoryPercent`, `MonitoringMemoryMinutes` | DWORD | (90, 5) |
| `MonitoringDiskPercent`, `MonitoringDiskMinutes` | DWORD | Disk active time (90, 5) |
| `MonitoringDiskFreePercent`, `MonitoringDiskFreeGB` | DWORD | Below either opens the alert; 0 turns that half off (10, 5) |
| `MonitoringNetworkErrorsPerMinute`, `MonitoringNetworkMinutes` | DWORD | (100, 5) |
| `MonitoringBootSeconds`, `MonitoringSignInSeconds` | DWORD | 0 off (120, 60) |
| `MonitoringCrashCount`, `MonitoringCrashHours` | DWORD | (3, 24) |
| `MonitoringServiceFailures` | DWORD | 0: no alert for a failed service, the event is still recorded (1) |

Rights (subkey `Rights`): `Monitoring`, `MonitoringSettings` (§9.1). `RemoteHubAgent.exe policy` prints them all;
*Settings → Policies* lists them from `PolicyCatalog.cs`; the ADMX test checks the template against it.

Enrolment file (`.rhagent`, format 2): an optional `monitoring` object with the same names, applied by `enroll` to
`agent.json` as the install's defaults; the install dialog gets a *Monitoring* group with the console defaults (§11.3).
MSI property `MONITORING=0|1` for the switch alone, like `VIRTUALDISPLAY`.

## 13. Audit and logging

Agent (`AuditEvent`, the Application log unless `AuditToEventLog` = 0):

| Id | Event | Recorded |
|---|---|---|
| 1400 `MonitoringStarted` | Monitoring runs (once per service run) | Interval, sections, whether uploads go to a broker |
| 1401 `MonitoringRead` | A console read bundles or sections | Account, source address, `after`, how many bundles – as PowerShell sessions are audited; never the content |
| 1402 `MonitoringSettingsChanged` | Settings changed through the endpoint | Account, source, which values and the new values (no secrets exist here) |
| 1403 `MonitoringUploaded` | The first successful upload to a broker per service run, and again after a failure | Broker, `seq` |
| 2500 `MonitoringAlert` | An alert opened or closed | Rule, value, duration, top programs – also for Intune or a SIEM |
| 2501 `MonitoringRefused` | A read or write refused (right, policy) | Account, source, reason |
| 3500 `MonitoringError` | A collector, the store or an upload failed | Which and `ex.Message`; the next interval retries |

The agent's log file additionally records each collection's duration and size (*inventory: 9 sections, 184 KB, 6.2 s*)
so a slow device can be diagnosed. Never in any log: section contents, event messages, account names other than the
audited reader's.

Console: `DiagLog` records fetches and refusals as counts and durations – *monitoring: 12 devices fetched, 1 refused
bundle, 2 not reached* – never names, hosts or contents. The console's audit log (`AuditEvents`) gets
`MonitoringSettingsChanged` (which connection, which values) and `MonitoringFetchedNow` for *Refresh now*.

## 14. Security and privacy

- **Reading needs a right, through a pinned connection.** No anonymous endpoint; `health` only gains the feature name.
  Through the broker, the mailbox is readable only with a console proof of the organisation and only sealed.
- **The console treats bundles as hostile** (TB8): the gzip is read through a size-limited stream (compressed and
  uncompressed limits of §7 enforced while reading, not after), `System.Text.Json` with `MaxDepth` 8 and no comments,
  every string clipped to its limit and stripped of control characters and bidirectional overrides before storing,
  numbers range-checked, dates parsed as UTC or dropped. A refused bundle is counted and the device is marked in the
  Device tab (*The device sent data RemoteHub refused*); the next bundles are still read. Nothing from a bundle is ever
  a path the console opens, a URL it shows as a link, a command it runs, or markup it renders – text only, in
  `TextBlock`s and cells.
- **Identity of the data:** direct and through a session, the bundle comes from the pinned certificate's device; from
  the mailbox, the seal binds it to the device id, store id and sequence. The console stores where each bundle came
  from (`device_monitoring.path`).
- **The device gives up nothing new to the network:** monitoring adds no listener, no helper, no pipe. The settings
  endpoint accepts a small fixed JSON object, range-checked, and nothing else.
- **People at the device:** `MonitoringIncludeUsers`, `MonitoringSections` and `MonitoringEvents` narrow what IT
  collects; the Application log shows monitoring runs (1400) and every read (1401), so the collection is visible on the
  device; `PRIVACY.md` and the README state what the agent collects and for whom (§18). Nothing goes to the vendor.
- **Secrets in configuration values:** `config` leaves out any policy or CSP value whose name says *password*, *secret*,
  *key*, *token*, *credential* or *pin*, and every REG_BINARY over 64 bytes. Paths are kept (they are configuration),
  but a path under a user's profile is shortened to `%USERPROFILE%\…` with `MonitoringIncludeUsers` = 0.
- **On the console** the data lives in the encrypted database under the same key hierarchy as passwords and notes
  ([local-db-design.md](local-db-design.md) §4); exports (§11.4) are an explicit *Save as…* by the admin and audited.
- **Resource exhaustion of the console:** bundles are processed one device at a time on a background queue with the
  limits above; the UI shows per-device results as they arrive. A device uploading 60 bundles an hour costs the
  console at most 240 MB an hour to read and far less to store; the broker's quota (§9.3) caps it.

## 15. Compatibility and budget

### 15.1 Compatibility

- Windows 10 1809 / Server 2019 and later, as the agent ([agent-spec.md](agent-spec.md) §10). Sources that an old
  build lacks (`% Processor Utility`, `MSFT_Volume` on 1809 without the Storage module, Diagnostics-Performance on
  Server Core) are skipped with the section saying `"unavailable": ["…"]`, never failing the collection.
- Server Core and devices without a user: `users` has no sessions, sign-in rules never fire; everything else works.
- WDAC/Constrained Language Mode and `PowerShellEnabled` = 0 don't matter: the collectors are native code in the
  agent, not PowerShell – which is why they are (§1.1).
- An **older console** ignores unknown sections and kinds; an **older agent** has no `monitoring` feature and the
  Device tab says so. The broker without the mailbox: §9.3.
- The agent is published trimmed (`PublishTrimmed`, partial): WMI through `Microsoft.Management.Infrastructure` (the
  CIM client PowerShell itself uses; trim-safe with the usual root descriptors) rather than `System.Management`; the
  Windows Update Agent and `PackageManager` through the interop already in the agent (`StoreApps.cs`) or source-generated
  COM. New packages get their entry in `THIRD-PARTY-NOTICES.md`.

### 15.2 Budget on the device

| Part | Budget | How it is kept |
|---|---|---|
| Sampler | < 0.1 % CPU, < 10 MB | One PDH query, 10 s; process list from one `NtQuerySystemInformation` call |
| Hourly inventory | < 30 s CPU, < 100 MB peak, finished within 5 minutes | Sections collected one after another on a thread at `BelowNormal` priority, each with its own stop (WMI 30 s, WU search 60 s); a section that overruns is skipped this hour and reported in `agent` |
| Event watching | < 0.1 % CPU | `EventLogWatcher` subscriptions with XPath filters per id, no polling |
| Store | ≤ 256 MB, about 20 MB in practice for 30 days | §8 |
| Uploads | ≤ 2 MB an hour in practice | Full bundles with hash-only sections; gzip |

The lab measures these (M-PERF-05, M-PERF-06) on the slowest VM (W10-1809) and the result goes into this section.

## 16. Tests

**Unit tests** (`tests/RemoteHub.Tests`, one class per area with a `/// <summary>`):

- `ThresholdStateTests` – a fake clock: opens at 90 % of samples above, not at 4 of 5 minutes, hysteresis, cool-down,
  the boot grace period, a `time.changed` jump doesn't open or close anything.
- `SectionDiffTests` – added, removed, changed apps; the same app in two scopes; a version that goes backwards; a
  driver's INF rename; 10,000 items in under a second.
- `MonitoringBundleTests` – round trip; every limit of §7 refused (a 4 MB + 1 byte bundle, depth 9, a 1,025-character
  name clipped, 501 events refused, an unknown section ignored); a gzip bomb (1 GB of zeros) stopped at 32 MB; control
  and bidirectional characters stripped.
- `MonitoringSealTests` – the broker protocol's seal and open with test vectors, a moved sequence number refused,
  a retired secret still opens, a console secret can't open.
- `MonitoringClientTests` – the fetch schedule (spread, interval learned from status, retry only at the next
  interval, no prompt without saved credentials); a `storeId` change marks a cut and asks from 0; a hash-only section
  without a snapshot asks for the section.
- `EventKindTests` – each Windows event of §5.2 (recorded XML from the lab) maps to its kind and fields; an event with
  a user name maps to *a user* with `MonitoringIncludeUsers` off.
- `MonitoringPolicyTests` – `PolicyCatalog` entries, the ADMX template check, settings refused with 409 when configured.
- `DeviceTimelineTests` – bucketing to the zoom, the correlation counts, a gap stays a gap; rendered to a
  `RenderTargetBitmap` in both themes with no exception (`[SkippableFact]` when there is no desktop).
- The collectors run on the Windows CI runner against the runner itself (`MonitoringCollectorTests`): each section
  parses, meets its limits, finishes within its stop, and contains none of §5.5 (a scan for `password`, the runner's
  known secrets' shapes).

**Lab** (`docs/test-lab-cases.md` gets a section *Agent – monitoring (M)*; `lab/tests/M-*.Tests.ps1`, `# LabQuick`
where a case applies to Server too):

| ID | Case | Pass when | How | P |
|---|---|---|---|---|
| M-INV-01 | First inventory | within 10 minutes after install, `/v1/monitoring/status` shows `lastSeq` ≥ 1 and a full bundle with every section; health lists `monitoring` | H | 1 |
| M-INV-02 | Sections' truth | the apps the VM has (the agent MSI, a test MSI), its KBs, its drivers, local users, the lab's GPO and the Defender state appear with the right values; nothing of §5.5 in the bundle (scan) | H PS | 1 |
| M-INV-03 | Interval | with `MonitoringIntervalMinutes` = 60 the second full bundle comes 60 ± 10 minutes after the first; 30 is refused (invalid, logged once) and 60 stays | H PS | 2 |
| M-INV-04 | Right | an account without `Monitoring` gets 403 on every monitoring path and 2501 is audited; without `MonitoringSettings`, PUT is 403 but GET works | H | 1 |
| M-INV-05 | Policy off | `MonitoringEnabled` = 0 → 404, no `monitoring` in health, the store folder is gone after a service restart | H PS | 1 |
| M-INV-06 | Users off | `MonitoringIncludeUsers` = 0 → no account name anywhere in a full bundle (scan for the VM's known accounts), counts still there | H | 1 |
| M-EVT-01 | App installed | install a test MSI → a partial bundle with `app.installed` and the `apps` section within 90 s; uninstall → `app.removed` | H PS | 1 |
| M-EVT-02 | Store app and EXE installer | `Add-AppxPackage` and an Inno Setup test installer → events within 90 s, kind says Store / installer | H S | 2 |
| M-EVT-03 | Driver and update | install a test driver (the lab's signed null driver) → `driver.installed`; install a `.msu` → `update.installed` and `os` pending restart | H PS | 2 |
| M-EVT-04 | Crash | a test program that faults → `app.crash` with module and code within 60 s, 3 faults → the crash alert; `app.hang` from a program that stops pumping messages | H S | 1 |
| M-EVT-05 | Blue screen | `NotMyFault`-style crash through the lab's driver (or a forced bug check in the guest) → after the restart `os.bluescreen` with the code, `device.started` with the boot duration | H PS C | 1 |
| M-EVT-06 | Service failure | kill a test service's process → `service.failed` and the alert; the agent's own service isn't listed as failed after a lab-initiated restart | H PS | 2 |
| M-EVT-07 | Group Policy | change the lab GPO and `gpupdate` → `config.gpo` with the changed values named within 2 minutes | H PS | 2 |
| M-EVT-08 | Flood | 100 crashes in a minute → identical events counted, at most 30 triggered bundles an hour, the rest in the next full bundle with `heldBack` | H S | 2 |
| M-PERF-01 | CPU alert | a burner at 100 % on all cores for 6 minutes → the alert opens between 5:00 and 5:30 with the burner on top, the alert bundle holds the 10 s samples; the burner stops → closes within 6 minutes with duration and peak | H S | 1 |
| M-PERF-02 | No false alert | 4 minutes at 100 %, then idle → no alert; 10 minutes alternating 30 s at 100 % and 30 s idle → no alert | H S | 1 |
| M-PERF-03 | Thresholds from the console | set CPU 50 % / 2 min through PUT → status shows `device` as source and the alert opens after 2 minutes of a burner; set `MonitoringCpuPercent` by policy → PUT answers 409, status says `policy` | H PS | 1 |
| M-PERF-04 | Memory and disk | fill memory to 95 % / hammer the disk for 6 minutes → the alerts; free space under 5 GB → the free-space alert once, not every minute | H S | 2 |
| M-PERF-05 | Budget | over 2 hours on W10-1809 the agent's CPU time grows by less than 60 s and its working set stays under 120 MB; the inventory's log line says under 60 s | PS | 1 |
| M-PERF-06 | Boot and sign-in | restart; sign in as normaluser → `device.started` with a boot duration, `session.signIn` with a duration (confirms the measurement) | H C S | 2 |
| M-CON-01 | Device tab | **2VM**, RemoteHub in the client VM: the tab shows the inventory, the events of M-EVT and the alert of M-PERF-01 on the timeline; screenshots in both themes | U | 1 |
| M-CON-02 | Diff events | install a test app between two full bundles without the watcher (`MonitoringEvents` = 0) → the console shows *between HH:MM and HH:MM* for the change | U H | 2 |
| M-CON-03 | Catch-up | the console is closed for 3 intervals → at start it fetches `after=<seq>` and the timeline has no gap; a re-enrolled device → the cut is marked and history kept | U H | 1 |
| M-BRK-01 | Through the broker | **needs the lab broker with the mailbox**: the device uploads within the interval, the console gets it from the mailbox without a device session, sealed (the broker's log shows the size, not the content); `MonitoringUploadToBroker` = 0 stops uploads | H | 1 |
| M-BRK-02 | Mailbox quota and retention | 61 uploads in an hour → 429 and the agent waits; bundles older than 7 days are gone from the broker, still on the device | H | 2 |
| SEC-MON-01 | Hostile bundle | **2VM**: a device replaced by a test server that answers bundles over the limits (size, depth, a gzip bomb, strings with control and RTL characters, a `file://` and an `http://` string, a 100,000-item apps list) → the console refuses or clips each, stays responsive, shows text only, nothing is opened or fetched | U H | 1 |
| SEC-MON-02 | Wrong device | a bundle with another device's id or `storeId` over a pinned connection → stored under the connection it came from with the mismatch marked, never under the other device | U H | 1 |
| SEC-MON-03 | Broker can't read | the lab broker's stored bundle isn't readable without the device secret; a console secret alone can't open it; a bundle re-filed under another `seq` is refused by the console | H | 1 |

A failing collector on a lab VM is a finding for §15.1's skip list, not a reason to lower a limit.

## 17. Phases

| Phase | Content | Done when |
|---|---|---|
| **1 – Inventory** | Agent: collectors (§5.1), store (§8), full bundles on the interval (§6.1), `/v1/monitoring/status`, `/bundles`, `/sections`, `/now`, rights, `MonitoringEnabled`, `…IntervalMinutes`, `…Sections`, `…IncludeUsers`, `…RetentionDays`, audit 1400/1401/2501/3500, cleanup. Console: `MonitoringStore`, `MonitoringClient` fetching directly and through jump hosts and broker sessions (§9.2), `SectionDiff`, the Device tab with Overview (no sparklines yet), the inventory sections and Events (diff events only), `AllowMonitoring`, retention | M-INV-*, M-CON-02/03 pass; §15.2 measured |
| **2 – Events and alerts** | Agent: watchers (§5.2), triggered bundles (§6.2), sampler (§5.3), thresholds (§5.4), settings endpoint, the remaining policies, 1402/2500. Console: Alerts section, Events with lanes, settings section, folder apply, navigation dots, *Settings → Monitoring* | M-EVT-*, M-PERF-* pass |
| **3 – Timeline** | `DeviceTimeline`, the Timeline section, sparklines on the tiles, correlation footer, keyboard and automation | M-CON-01, the U screenshots reviewed |
| **4 – Broker mailbox** | The broker's part (its own repository), agent uploads with seals, console reads the mailbox and the notice, `MonitoringUploadToBroker`, 1403 | M-BRK-*, SEC-MON-03 pass |
| **5 – Monitoring view** | The rail view, dashboards, reports and export, full-text search over sections in the navigation search | The first three dashboards agreed in §19 ship |
| Later | GPU load; per-process network; a learned baseline per device and *unusual for this device* marks; software metering only if a privacy case for it is made; a RemoteHub Server as the home of the history, fed with the same bundles; alerts as toast notifications in the console; a Windows event forwarding guide so SIEMs read 2500 | – |

Phases 1 and 2 touch the agent and the console together; each ships behind `MonitoringEnabled` and `AllowMonitoring`
so a release in between changes nothing for a device without them. Phase 4 can run in parallel with 3.

## 18. Docs in step

When phase 1 lands:

- [agent-spec.md](agent-spec.md): §2 gets a bullet for monitoring (as §2 has for the desktop); §6 the endpoints and the
  `monitoring` feature; §7 the `monitoring` object and §7.1 the policies and rights; §8 changes *Nothing leaves the
  device* to *The agent sends no telemetry and no crash reports to anyone. With monitoring on it uploads inventory
  bundles to the organisation's own RemoteHub Broker, sealed for the organisation's consoles*; §11 the row.
- README: the *RemoteHub Agent* section gets *Device monitoring* (what is collected, where it goes, the policy
  switch), *Remote tools* the Device tab, and line "The agent sends nothing anywhere" is qualified as above.
- `PRIVACY.md`: the sentence *The RemoteHub Agent on your servers sends nothing at all* becomes *sends nothing to us.
  With monitoring turned on by your organisation it sends an inventory of the device to your organisation's own
  RemoteHub consoles or broker – see the README*; the list of what is and isn't collected (§5.1, §5.5) is linked.
- [admx-design.md](admx-design.md) §9 gets the *Monitoring* category and the two rights; §5 `AllowMonitoring`.
- [threat-model-technologies.md](threat-model-technologies.md): the new endpoints and the mailbox under §2.1 and a
  *Console ← device data* boundary; [security-test-lab-design.md](security-test-lab-design.md) §3 TB8 gets SEC-MON.
- [local-db-design.md](local-db-design.md) §6: the tables of §10 and the schema version.
- `docs/test-lab-cases.md` §new *M* and `test-lab-test-document.md`, with `lab\Update-LabCaseStatus.ps1` after the
  first run.

## 19. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | **Account names by default?** §1.1 collects them unless IT turns them off. The alternative is off by default and IT turns them on. | On by default: a DEX view without the person is half a view, and the Application log shows that monitoring runs. The ADMX explain text says plainly what the value collects. |
| Q2 | **Policy values in `config`** – sending every value under `SOFTWARE\Policies` and `PolicyManager` can be thousands of entries. Keep the full set, or only the GPO/profile list and let the admin open the registry tab for values? | Full set with the filter of §14 and the 5,000 cap; it is what "which setting changed on this device" needs, and it is sent only when its hash changes. |
| Q3 | **The free-space rule's unit** – percent, GB, or both? | Both with *either opens*, as proposed; an admin who wants one sets the other to 0. |
| Q4 | **Which dashboards first** (phase 5)? | *Open alerts*, *Crashing apps*, *Pending restarts* – the three the timeline can't answer across devices. |
| Q5 | **The mailbox's retention on the broker** – 7 days and 64 MB per device is a guess at the broker's storage costs. | Confirm with the broker's hosting; the agent and console don't depend on the numbers. |
| Q6 | **Should an alert be able to run a script** (remediation) in a later phase? | Not in this design; if ever, through the Scripts view's confirmation and audit, never unattended. |
| Q7 | **Install default for `MonitoringEnabled`** – on for every new agent, or chosen in the install dialog like the virtual display? | On, with the dialog's *Monitoring* group to turn it off and set the defaults; an upgrade of an installed agent turns it on too (nothing is sent anywhere a console doesn't already reach, and IT has the policy). To confirm with the owner. |
| Q9 | **Scale targets and a server.** How many devices should one console hold (50? 500? 5,000?), how long a history, and is a RemoteHub Server on the roadmap – one that may *read* the data, which ends the end-to-end seal of §9.3? These three answers decide §21. | Design for 500 devices and 90 days per console on SQLite; a server with ClickHouse when a fleet or a shared history is wanted (§21.4). |
| Q8 | **Sign-in duration measurement** (§5.2) – 7001 to the shell's first window is the agent's own measurement and needs the helper's session knowledge. Acceptable, or boot duration only in v1? | Try it in the lab (M-PERF-06); fall back to boot duration only if it isn't reliable. |

## 20. Decided

What settled each decision, so a reviewer sees where the request, the project's patterns and a judgement call were
the deciding voice:

| Decision | Settled by |
|---|---|
| Collection is native in the agent, not PowerShell scripts | The project: `PowerShellEnabled` = 0 and WDAC must not stop monitoring; the agent's own principle of no command set stays |
| "Send every 60 minutes" is an upload to the organisation's broker; without a broker, consoles fetch at the interval | The architecture: consoles have no listener; the device store makes both paths lossless |
| The interval's floor is 60 minutes | The request ("configurable to any higher number") and the budget |
| Full inventory on the interval, changed sections on a trigger, the console computes diffs | The request ("send the selective information") and robustness – a snapshot can't be out of step |
| Thresholds live on the device, set from the console per device or folder, policy wins | The project's P1 and P6 (the device is the boundary); two consoles can't disagree about a device |
| Alerts need 90 % of samples above the threshold and close with hysteresis | Judgement: the request's "over 80 % for 5 min" read as sustained, not averaged, so a spike doesn't alert and a hover doesn't flap |
| Account names collected by default, policy to turn off | Judgement, open as Q1 |
| No SQLite on the device | The agent's dependency footprint as a LocalSystem service; files with `DurableFile` suffice |
| The timeline is drawn by the project itself, from theme brushes | The project has no charting library and draws its own lists and toolbars; a library would be third-party code to notice and trim |
| Correlation is a count, labelled as one | Honesty: RemoteHub shows the admin the order of things; it doesn't decide the cause |
| Dashboards and reports are phase 5 | The request ("later in dashboards and reports") |

## 21. A second database? ClickHouse, DuckDB and SQLite weighed

The request raises whether monitoring data needs a database of its own, such as ClickHouse. The honest answer has
three parts: how much data there is, what is asked of it, and where it is – on an admin's PC, on the broker, or on a
server that doesn't exist yet.

### 21.1 How much data

Per device and day, as the design stands:

| Data | Rows or points | Stored (compressed) |
|---|---|---|
| 5-minute performance, 12 metrics × avg/max/min, plus top processes | 288 points × 12 metrics | about 15 KB |
| 1-minute performance (7 days only) | 1,440 × 12 | about 200 KB |
| Events (watched and diff), 50 a day | 50 | about 25 KB |
| Section changes, about one a day (Windows apps update themselves) | 1 snapshot | 20–60 KB |
| **Total for the 90-day history** | | **about 80 KB a day** |

| Devices per console | 90 days | with 7 days of 1-minute data | Rows in `device_perf` (hourly, packed) | Rows if one row per metric and point |
|---|---|---|---|---|
| 50 | 0.36 GB | 0.43 GB | 108,000 | 15.6 million |
| 500 | 3.6 GB | 4.3 GB | 1.08 million | 156 million |
| 5,000 | 36 GB | 43 GB | 10.8 million | 1.56 billion |

The right-hand column is why the naive layout (a row per metric and point) was replaced in §10: it is the layout that
makes SQLite look slow and a column store look necessary. Packed per hour, the row count is small, the blobs compress
well, and the daily summary table carries what dashboards need.

### 21.2 What is asked of it

| Question | Who asks | Rows touched (500 devices, packed layout) |
|---|---|---|
| One device's timeline for a day / 90 days | Device tab | 24 / 2,160 `device_perf` rows plus its events |
| One device's inventory and changes | Device tab | tens of rows |
| Which devices have app X, driver Y, KB missing | navigation search, dashboards | 150,000 `device_apps` rows with an index – milliseconds |
| Devices with open alerts, not heard from, pending restart | dashboards | 500 rows |
| Average or p95 CPU per device over 30 days, the worst 20 | dashboards | 180,000 `device_perf_days` rows – well under a second |
| Crashes per app across the fleet, 7 days | dashboards | 175,000 event rows with an index on kind and time |
| Import of one hour's bundles | background | 500 inserts and 500 summary updates |

None of these is an analytical scan over raw points. The one query that would be – *the fleet's CPU curve at
1-minute resolution for a month* – is not in the design, and a desktop console isn't where it belongs.

### 21.3 The candidates for the console

| | Encrypted SQLite (today) | DuckDB (embedded, columnar) | ClickHouse (server) or chDB (embedded ClickHouse) |
|---|---|---|---|
| Fit for the console's queries (§21.2) | Good with the packed layout and summary tables | Very good for aggregations; no better for point lookups | Over-sized: a server process on an admin's PC, or chDB's several-hundred-MB library |
| Encryption at rest under the console's key hierarchy | Done ([local-db-design.md](local-db-design.md) §4) | DuckDB added database encryption in 1.4 (2025) – a second key to wrap and rotate with the startup lock; to be verified against the current release | None in chDB; a ClickHouse server encrypts its disks, not per user |
| Footprint | Already shipped | A second native engine, about 40–60 MB, with trimming and MSI work; MIT licence, a notice entry | chDB about 10× that and Linux-first; ClickHouse not installable per user |
| Operations | None | None | A service to install, patch and back up |
| Security surface | Known; `trusted_schema` off, no extensions | A second SQL engine with extensions to lock down | A listening server on the admin's PC |
| Where it wins | Up to a few thousand devices with the layout of §10 | From about 5,000 devices per console, or when ad-hoc analytical queries over raw points are wanted in the console | Only on a server |

**Decision for the console: stay with the encrypted SQLite**, and spend the effort on the layout instead – packed hourly
rows, the daily summary table, the normalised current apps table, indexes on kind and time, and import in one
transaction per device. Phase 1 measures 500 devices (import of an hour's bundles, the timeline queries, each
dashboard of §11.4) on the lab's client VMs, and §21.1's numbers are replaced by measured ones. If a query doesn't make
its budget (timeline under 200 ms, a dashboard under 2 s) at 500 devices, the first answer is a summary table, the
second is DuckDB for the performance tables alone, with the sections, events and alerts staying where they are.
The bundles (§7) are the source of truth on the device for the retention, so a console can rebuild its tables from the
devices, and a different engine later is an import, not a migration.

### 21.4 A server, and the broker

- **The broker can't be the database.** It stores bundles it can't read (§9.3) – that is the point of the seal. Any
  store that *queries* monitoring data must hold the organisation's keys, so a central database is first a trust
  decision (does the organisation accept that its server reads device inventories?), then a technology choice. The
  broker on Cloudflare also has no place to run ClickHouse; its storage is for sealed blobs with a short retention.
- **If a RemoteHub Server comes** – for a shared fleet history, several admins, more than a few thousand devices, or a
  history longer than a year – **ClickHouse is the right engine for the events and performance tables**: MergeTree
  with a TTL for retention, compression that makes the 1-minute history affordable, materialised views for the
  dashboards, and bundles as the ingestion format unchanged. Inventory sections and the current state fit PostgreSQL
  better (point lookups, per-row updates, constraints); a server would likely have both, or PostgreSQL with
  TimescaleDB alone below about 10,000 devices to keep one engine. That is a server design of its own, outside this
  document; what this document guarantees is that nothing in the bundle format or the console's tables stands in its
  way, and that the device keeps its own store so a server can backfill.
- **Thresholds for revisiting:** a console asked to hold more than about 2,000 devices, a retention over a year, an
  admin team that needs one shared view, or analytical questions over raw performance points. Any one of them is a
  server conversation, not a console database swap.

## 22. Data volumes and Cloudflare costs at 1, 4 and 8 hours

The question: what does the broker (Cloudflare Workers with D1, as the lab spec and `BrokerProtocol.cs` have it) and the
console's database carry if the full inventory goes every hour, every 4 hours or every 8 hours? Prices are Cloudflare's
published ones at the time of writing (Workers Paid $5 a month with 10 million requests and 30 million CPU-ms included,
then $0.30 and $0.02 per million; D1 5 GB, 25 billion rows read and 50 million rows written included, then $0.75 per
GB-month, $0.001 and $1.00 per million; R2 10 GB-months, 1 million class A and 10 million class B included, then $0.015
per GB-month, $4.50 and $0.36 per million; no charge for traffic in or out). Check them again before a budget.

### 22.1 Assumptions per device

| Item | Value | Why |
|---|---|---|
| Envelope and hash-only sections | 1.5 KB | §7 |
| 5-minute performance summaries | 1.7 KB per hour covered | 12 metrics × avg/max/min × 12 points, top processes, gzip |
| Section changes | 40 KB a day | about one `apps` change a day (Edge, Teams and Store apps update themselves); other sections rarely |
| Triggered bundles | 3 a day, 15 KB on average | app changes, crashes, updates, alerts; session events don't trigger (§5.2) |
| Consoles reading the mailbox | 2 | each fetches once per upload notice |
| Worker CPU per request | 5 ms | pass-through of a sealed body, a D1 insert or select |
| Broker retention | 7 days | §9.3 |
| Console history | 90 days, plus 7 days of 1-minute data | §10 |

### 22.2 Per device

| Interval | Full bundle | On the wire a day | Uploads a day | Uploads a month | Held on the broker (7 days) | Snapshot-only variant, a day |
|---|---|---|---|---|---|---|
| 1 h | 3.2 KB | 162 KB | 27 | 810 | 1.1 MB | 119 KB |
| 4 h | 8.4 KB | 135 KB | 9 | 270 | 0.9 MB | 88 KB |
| 8 h | 15.2 KB | 131 KB | 6 | 180 | 0.9 MB | 83 KB |

Two things the table shows:

- **The interval saves requests, not bytes.** Three quarters of the bytes are section changes and triggered bundles,
  which happen when they happen; the performance summaries cover the same hours whichever bundle carries them. Going
  from 1 to 8 hours cuts uploads by 4.5× and bytes by 20 %.
- **The snapshot-only variant** – no continuous sampling, one reading per interval – saves about a quarter of the bytes
  and loses every threshold alert: *CPU at or above 80 % for 5 minutes* can't be seen in a reading every hour. Sampling
  stays on the device (it costs the device nothing measurable, §15.2); the interval only decides how often the
  summaries travel. Longer intervals do make the timeline later: a 4-hour interval shows the morning's load at noon,
  unless an alert sent it earlier.

### 22.3 Per fleet and month

Requests are uploads plus two consoles' reads. D1 rows written are one per upload with its index, plus the retention
delete. The R2 column is the alternative store for the same bundles (one class A put and one delete per upload, one
class B get per console read). The console column is one console holding every device.

| Devices | Interval | Worker requests | Workers | D1 rows written | Broker store | D1 cost | R2 cost | Traffic in | Console DB (90 days) |
|---|---|---|---|---|---|---|---|---|---|
| 100 | 1 h | 0.24 M | $5.00 (plan) | 0.2 M | 0.11 GB | $0 | $0 | 0.5 GB | 0.8 GB |
| 100 | 4 h | 0.08 M | $5.00 | 0.1 M | 0.09 GB | $0 | $0 | 0.4 GB | 0.8 GB |
| 100 | 8 h | 0.05 M | $5.00 | 0.1 M | 0.09 GB | $0 | $0 | 0.4 GB | 0.8 GB |
| 1,000 | 1 h | 2.4 M | $5.00 | 2.4 M | 1.1 GB | $0 | $4.50 | 4.6 GB | 8.2 GB |
| 1,000 | 4 h | 0.8 M | $5.00 | 0.8 M | 0.9 GB | $0 | $0 | 3.9 GB | 8.2 GB |
| 1,000 | 8 h | 0.5 M | $5.00 | 0.5 M | 0.9 GB | $0 | $0 | 3.7 GB | 8.2 GB |
| 10,000 | 1 h | 24.3 M | $11.12 | 24.3 M | 10.8 GB | $4.37 | $74.53 | 46 GB | 82 GB |
| 10,000 | 4 h | 8.1 M | $5.21 | 8.1 M | 9.0 GB | $3.02 | $22.50 | 39 GB | 82 GB |
| 10,000 | 8 h | 5.4 M | $5.00 | 5.4 M | 8.7 GB | $2.79 | $13.50 | 37 GB | 82 GB |

### 22.4 What follows

- **The broker's cost is noise at every size.** Up to 1,000 devices everything is inside the $5 plan at any interval.
  At 10,000 devices and 1 hour it is about $15 a month on D1. The interval matters only for R2, whose price is per
  put: there 1 hour costs five times 8 hours. So **D1, the database the broker has, is the right mailbox store**, and
  the interval can be chosen for freshness, not for cost.
- **D1's hard limit is size, not money:** 10 GB per database, which at the 7-day retention is about 9,000 devices at
  1 hour (11,000 at 8 hours) in one organisation's database. Options in that order: a shorter mailbox retention (3 days
  halves it – the console still catches up from the device, §9.2), a mailbox database per organisation, R2 for that
  organisation (§9.3). None changes the agent or the console.
- **The console is the real limit, and the interval doesn't move it.** One console holding 1,000 devices has an 8 GB
  database after 90 days, 10,000 devices 82 GB – whatever the interval, because the console keeps the same
  summaries, events and changes. That confirms §21.3: a console is designed for about 500 devices and 90 days (4 GB);
  a fleet beyond about 2,000 devices or a shared history is a server (§21.4). The levers that do shrink the console
  are the retention (§12.1), the 1-minute history (7 days, 0.7 GB per 500 devices), and the summaries' step (15-minute
  summaries cut the performance part by two thirds and are still enough for a 90-day timeline).
- **Traffic is free on Cloudflare, but not on the device's link:** 160 KB a day per device at 1 hour is 5 MB a month
  – a 4-hour interval saves 1 MB of that. Metered links don't need a longer interval; the summaries and section
  hashes keep the hourly bundle at 3 KB.
- **Recommendation:** keep **60 minutes as the default**, because the cost case for a longer interval doesn't exist
  and the hourly bundle is what makes the timeline current without an alert. Offer 4 and 8 hours (and up to a week)
  in the settings for organisations that want less chatter – the policy range of §12.2 already allows it.

The figures come from the assumptions of §22.1; phase 1 replaces them with measured bundle sizes from the lab (M-INV-01
records the first full bundle's size per VM), and §22.2–22.3 are recomputed then.

