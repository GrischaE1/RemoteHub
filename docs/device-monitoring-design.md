# RemoteHub – Device monitoring and inventory (DEX)

Status: Draft v2 (2026-11-09), a proposal – nothing is built yet. Written against main as of 0.22.x (agent 0.22, broker
protocol `remotehub-broker/1`). v2 takes in the answers to v1's open questions (§20): the history lives in a
**RemoteHub-hosted, multi-tenant monitoring service** sized for hundreds of thousands of devices, not in the console;
monitoring is **activated from the console** under the tenant's licence; devices behind **jump hosts** upload through
them; the console **queries** the service. §19 has what is still open, §21 the database choice, §22 volumes and costs.
Owner: Grischa Ernst

## 1. Goal

RemoteHub so far shows a device only while an admin looks at it: a tab reads the device's processes, services or events
when it is open and forgets them when it closes. This design gives RemoteHub a **memory of every device** and a view
that puts what happened on a device in order – so that "the Intel graphics driver was updated on Tuesday, and since
then Zoom crashes twice a day" or "Teams updated itself at 09:12, and the CPU has been above 80 % since" can be seen
rather than guessed. That is the digital employee experience (DEX) approach: inventory, events and performance of one
device on one timeline – and, across a tenant's devices, dashboards and reports.

| Part | What |
|---|---|
| **Collection on the device** | The RemoteHub Agent collects an **inventory** (installed apps, Windows updates, drivers, hardware, operating system, users and groups, configuration – Group Policy and MDM/CSP, services, security state) on a schedule: every **60 minutes** by default, configurable to any longer interval, never shorter (§6.1). It collects only once the console has **activated** monitoring on it (§6.4). |
| **Events** | The agent watches the device for things that matter and sends the part of the inventory they changed at once, not at the next interval: an app installed, updated or removed; a Windows update or a driver installed; an app crash or hang; a blue screen or an unexpected restart; a service that died; disk and hardware errors; Defender detections (§5.2). |
| **Performance and thresholds** | The agent samples CPU, memory, disk and network every 10 s, keeps 1- and 5-minute summaries, and raises an **alert** when a value stays above a threshold for a time – by default CPU at or above 80 % for 5 minutes. Thresholds, windows and the metrics watched are set by the admin (§5.4). An alert sends the recent samples and the processes behind them. |
| **Delivery** | The agent **uploads** every bundle to the **RemoteHub monitoring service** – the broker's front door, with a column store behind it (§9, §10). A device with its own broker connection uploads directly; a device behind a jump host uploads through the jump host's agent, and so on along the chain, as long as one device in it reaches the broker (§9.3). The agent keeps an outbox for the days it can't (§8). |
| **The service** | Multi-tenant (a tenant is a broker organisation), a **region per tenant** (EU, US), tenant id on every row, retention by the tenant's tier – **30 days** by default, 60 or 90 by licence (§10). It holds the inventory history, events, alerts and performance, computes the dashboards and reports, and answers the console's queries. It is run by RemoteHub. |
| **In the console** | A **Device** tab per connection: an overview with the current health, then inventory sections, alerts and events – answered by the service, with a small cache so the last state shows while offline (§11.1). **Activation** of monitoring per tenant, folder or device from the console, under the tenant's entitlement (§11.3). |
| **Timeline** | The DEX view in that tab: performance curves, alerts, changes (apps, updates, drivers, configuration) and problems (crashes, blue screens, service failures) on one time axis, zoomable from an hour to the tenant's retention, with the changes that precede a problem pointed out (§11.2). |
| **Dashboards and reports** | A **Monitoring** view on the view rail for the whole tenant: every dashboard of §11.4 (open alerts, crashing apps, pending restarts, patch level, disk space, devices not heard from …), the customer picks which to show; reports over a folder and period, exported (phase 5). |
| **Later** | Remediation: an alert runs a deployed script version on the device, with its own approval and audit design (§17). |

### 1.1 Principles

1. **The agent collects, it never acts.** Monitoring reads the device: registry, WMI/CIM, the event logs, performance
   counters, the Windows Update history. It changes nothing on the device, installs nothing, kills nothing. The agent
   keeps its rule from [agent-spec.md](agent-spec.md) §2 – no command set of its own.
2. **The data goes to the tenant's service, and the service reads it.** Unlike broker sessions, which the broker relays
   sealed, monitoring data is stored and queried by RemoteHub's service on the tenant's behalf: that is what fleet-wide
   dashboards and a timeline over a quarter need. It is said plainly in the README, `PRIVACY.md` and the activation
   dialog, the tenant chooses its region, its data is isolated by tenant id and deleted with the tenant, and nothing is
   collected before the tenant's admin activates it (§6.4, §14).
3. **No secrets, and identities only where IT allows them.** Bundles never contain passwords, keys, product keys,
   recovery keys, file contents, command lines, window titles or clipboard. Account names (signed-in users, local
   accounts, group members, the MDM enrolment's user) are collected by default, because a DEX view is about a person's
   device, but the policy `MonitoringIncludeUsers` = 0 replaces them with *a user* (§12).
4. **Whatever comes from a device is untrusted.** A compromised device must not be able to harm the service or the
   console through a bundle (threat boundary TB8, [security-test-lab-design.md](security-test-lab-design.md)): every
   bundle is parsed against limits at the service, every string is clipped and shown as text, nothing in it is a link,
   a path to open or HTML (§14).
5. **The device must not notice.** Collection runs below normal priority, is spread out and stops when it takes too
   long. It must not itself be the reason the CPU is at 80 % (§15.2).
6. **Built on what is there.** The agent's TLS pin, Windows sign-in, rights and policy; the broker's tenants, device
   and console identities and proofs; the tool tabs' chrome and lists. Policy values go into `PolicyCatalog.cs` and the
   ADMX template like every other ([admx-design.md](admx-design.md)).
7. **Activated from the console, locked by policy.** The tenant's entitlement is on the service; the admin activates
   monitoring per tenant, folder or device and sets thresholds from the console; the agent takes what the console
   gives it. A device policy can forbid monitoring or fix a value, and then shows locked ([admx-design.md](admx-design.md) P1).

## 2. Non-goals (v1)

- **Remediation in v1.** No automatic restart of a crashing service, no uninstall of an app that crashes, no scripts
  run on an alert. An alert offers *Run script…* and the device actions as a manual next step, each confirmed by the
  admin as today. Automatic remediation is a planned later phase (§17), with its own approval and audit design.
- **A customer-hosted service.** V1 is RemoteHub's hosted service. The service's image and the bundle format are made
  so a self-hosted deployment is possible later, but installing and operating it at a customer is not designed here.
- **Downloading the agent's monitoring binaries on demand** (only to activated devices). Out of scope for this
  design; the collectors ship in the agent, inactive until activated.
- **Per-user experience scores, baselines and anomaly detection.** V1 has fixed thresholds the admin sets. A learned
  baseline ("this device is slower than usual") is later work (§17).
- **Software metering** (how long which app was in the foreground), browsing, keystrokes, screenshots. Not DEX as
  RemoteHub means it, and a privacy problem.
- **Patching and app deployment.** RemoteHub shows what is installed and what is pending; it doesn't install.
- **Agent-less devices.** Inventory needs the agent. A device reached over WinRM only can still be inspected with
  the remote tools and the script library's INV scripts as today; nothing is collected for it.
- **Devices with no path to the broker at all** – no broker connection of their own and no jump host in their chain
  that has one. They show *not monitored – no path to the RemoteHub monitoring service*.
- **Temperatures and fan speeds.** Windows gives no reliable, vendor-independent reading. GPU load is later work (§17).

## 3. What is at stake

| Asset | Threat | Where it is handled |
|---|---|---|
| The inventory of a device (apps, patch level, users, configuration) – a map for an attacker | Read by someone without the right; read in transit; read by another tenant; read at RemoteHub beyond what the service needs | Rights on the device (§9.1), TLS everywhere, device and console proofs of the broker protocol, tenant id on every row and every query (§10.3), region per tenant, access at RemoteHub limited to operations (§14) |
| The service | A device floods it or sends bundles made to crash the parser; a tenant reads another's rows | Quotas per device and tenant, limits on every field at ingestion, tenant-scoped queries only, the device's identity on every upload (§9.2, §14) |
| The console | A device's data misleads the admin or exploits the console's rendering | Everything shown is text, clipped at the service and again in the console; nothing from a bundle is a link, a path or markup (§14) |
| The device | Monitoring slows it down, fills its disk, or opens a new way in | Budget and outbox limits (§8, §15.2); no new listener; the settings endpoint needs a right; the relay endpoint on a jump host forwards only to the service, bounded (§9.3) |
| People at the device | Surveillance beyond what IT needs | §1.1 principle 3, `MonitoringIncludeUsers`, `MonitoringSections`, activation visible on the device (Application log 1404), what is collected listed in `PRIVACY.md` (§18) |
| A jump host used as a relay | Used as an open proxy, or to read the bundles it forwards | It forwards only to the service's fixed path, rate-limited per source; bundles are signed by the device and TLS runs end to end from the device to the service (§9.3) |

## 4. Architecture

```
Device                                   Jump host (optional)            RemoteHub monitoring service                Console(s)
┌──────────────────────────────────┐    ┌──────────────────────────┐    ┌──────────────────────────────────────┐    ┌───────────────────────┐
│ RemoteHubAgent (LocalSystem)     │    │ RemoteHubAgent           │    │ Front door: the broker's Workers       │    │ MonitoringService     │
│  Monitoring/                     │    │  /v1/monitoring/forward  │    │  POST /v1/o/{org}/monitoring/upload    │◀───│  (queries, cache)     │
│  ├ Activation (from the console) │    │  → the service, bounded, │    │  GET  /v1/o/{org}/monitoring/…  (query)│    │ Device tab · Timeline │
│  ├ Collectors  – sections (§5.1) │ ──▶│  forwards bytes only     │───▶│  activation, entitlement, tenant meta  │    │ Monitoring view       │
│  ├ Watchers    – events (§5.2)   │    └──────────────────────────┘    │  (D1)                                  │    │ Activate… (§11.3)     │
│  ├ Sampler     – PDH, 10 s (§5.3)│  or directly, with its own         │ Store: ClickHouse, region per tenant   │    └───────────────────────┘
│  ├ Thresholds  – alerts (§5.4)   │  broker connection                 │  sections, events, alerts, performance │              │
│  ├ Bundler     – bundles (§7)    │ ────────────────────────────────▶ │  TTL by the tenant's tier (30/60/90 d) │              │ pinned agent connection
│  └ Outbox      – until uploaded  │                                    │  dashboards as materialised views      │              ▼
│  /v1/monitoring/{status,now,     │ ◀──────────────────────────────────────────────────────────────────────────────── PUT /v1/monitoring/activation
│   settings,activation}           │                                                                                     PUT /v1/monitoring/settings
└──────────────────────────────────┘                                                                                     (right MonitoringSettings)
```

- **One store, at the service.** The device keeps an outbox only until the service has acknowledged a bundle (§8);
  the console keeps a small cache (§11.1). History, search and dashboards are the service's, so every console of a
  tenant sees the same thing and nothing depends on a console being on.
- **The front door is the broker.** It already knows tenants (organisations), devices (registration proofs with the
  device's key), consoles (their keys), regions and quotas, and runs on Cloudflare Workers with D1. Monitoring adds an
  upload endpoint that verifies the device and writes to ClickHouse, a query API for consoles, and the activation and
  entitlement records in D1 (§10).
- **Two ways up, one identity.** A device with its own broker connection uploads straight to the front door. A device
  without one uploads to its jump host's agent, which forwards the bytes to the front door (or to its own jump host –
  the chain); the upload is signed by the device for the service either way, so the relay adds nothing and can change
  nothing (§9.3). The console decides the route by the tree, as for everything else ([jump-host-design.md](jump-host-design.md)).
- **The service computes the differences.** The device sends inventory sections whole (or their hash when unchanged);
  the service keeps the last snapshot of each section and turns the difference into change events (*Zoom 6.2.1 →
  6.3.0*, *driver igdkmd64 updated*, *GPO "Default Domain Policy" version 42 → 43*). The device does the same diff
  locally only to decide whether a change happened and to name it in the triggered bundle (§6.2), so both agree.
- **The console talks to the device only to activate and configure**, over the pinned agent connection with the
  connection's Windows credentials, directly, through a jump host or through a broker session, as every agent request.

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
| `config` | **Group Policy:** applied GPOs for the computer (and the signed-in user with `MonitoringIncludeUsers`): name, GUID, version, link, enforced, last processing time and result; the effective policy values under `HKLM\SOFTWARE\Policies` and `HKCU\…` of the signed-in user (key, name, type, value – strings clipped to 256 characters; a value whose name contains *password*, *secret*, *key*, *token*, *credential* or *pin* is sent as `<left out>`). **MDM/CSP:** enrolment present, provider id (Intune, other), enrolment type, device id at the MDM, last sync time and result; the effective CSP policy values (`PolicyManager\current\device\<area>\<policy>`) and the user's; applied configuration profiles' ids where the CSP records them (lab). **Other:** Defender state (real-time protection, signature version and age, last scan, tamper protection), firewall profiles on or off, UAC level, RDP enabled, WinRM listeners, power plan, time source | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History` (+ the user's), `root\rsop\computer` only when the history is empty, `HKLM\SOFTWARE\Microsoft\Enrollments`, `HKLM\SOFTWARE\Microsoft\PolicyManager`, `MSFT_MpComputerStatus` (`root\Microsoft\Windows\Defender`), `MSFT_NetFirewallProfile`, registry | 5,000 policy values, 2,000 CSP values |
| `agent` | The agent's own version, policy values in force and their source (as `RemoteHubAgent.exe policy` prints – names and values, never the enrolment secret or keys), rights in force (group names), monitoring settings and activation state, outbox size, last upload and its path (direct, through which jump host) | `AgentConfig`, `AgentPolicy` | – |

The full set of configuration values is collected on purpose (v1 Q2): "which setting changed on this device" is what
the timeline is for, and a section travels only when its hash changes.

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
| `config.gpo` | Changes | Microsoft-Windows-GroupPolicy/Operational 8004–8007 (processing done), 1085/1125/1129 (failed); then a diff of the `config` GPO list and policy values – a bundle only when something changed | Scope (computer, user), result, which GPOs and how many values changed | `config` |
| `config.mdm` | Changes | DeviceManagement-Enterprise-Diagnostics-Provider/Admin sync-completed and failed events (ids confirmed in the lab, M-CFG), then a diff of the CSP values | Result, values changed | `config` |
| `config.security` | Changes | Windows Defender/Operational 5001/5010/5012 (protection turned off), 2000/2001 (signatures); firewall profile changes (Windows Firewall With Advanced Security/Firewall 2003/2004/2005 as confirmed in the lab) | What changed | `config` |
| `app.crash`, `app.hang` | Problems | Application: Application Error 1000, Application Hang 1002, Windows Error Reporting 1001 (bucket, for grouping), .NET Runtime 1026 | Program (name and version), faulting module and version, exception code, session's user (with `MonitoringIncludeUsers`) | – |
| `os.bluescreen`, `os.unexpectedRestart` | Problems | System: BugCheck 1001 (code and parameters, dump path – the path only, never the dump), Kernel-Power 41 (bug check code), EventLog 6008; a new file under `C:\Windows\Minidump` corroborates | Code, parameters, probable driver where BugCheck names one | `os` |
| `service.failed` | Problems | System: Service Control Manager 7031, 7034 (terminated), 7000, 7009, 7011, 7023, 7024 (start failed) | Service, what Windows did (restart, nothing) | – |
| `disk.error`, `hardware.error` | Problems | System: Disk 7, 51, 153, 157; NTFS 55, 98 (volume health), 140; storahci/stornvme 129; WHEA-Logger 17, 18, 19, 20, 47 (corrected and uncorrected hardware errors, source component) | Disk or component, counts (the same event within an hour is counted, not repeated) | `hardware` |
| `memory.low` | Problems | Microsoft-Windows-Resource-Exhaustion-Detector 2004 | Top consumers the event names (program names) | performance samples |
| `security.detection` | Problems | Windows Defender/Operational 1116, 1117, 1118, 1119 | Threat name, severity, action, path's folder only (no file name) | – |
| `device.restarted`, `device.shutDown`, `device.started` | Sessions | System: User32 1074 (who asked and the reason), Kernel-General 12/13, EventLog 6005/6006, Kernel-Boot (fast startup) | Reason, process that asked, initiating user (with `MonitoringIncludeUsers`), boot duration from Diagnostics-Performance/Operational 100 (`MainPathBootTime` + `BootPostBootTime`), shutdown duration from 200 | `os` |
| `session.signIn`, `session.signOut`, `session.lock`, `session.unlock`, `session.connect`, `session.disconnect` | Sessions | Microsoft-Windows-Winlogon 7001/7002, TerminalServices-LocalSessionManager/Operational 21–25, 39/40; sign-in duration measured by the agent from 7001 to the shell's first window (lab; boot duration alone if it proves unreliable) | Session id, account (with `MonitoringIncludeUsers`), console or RDP, duration | none – a device sees dozens a day; they go with the next full bundle, and `users` is re-read only there |
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
on the device, and **5-minute** summaries (average, maximum, minimum) that travel in the bundles. The full bundle
carries the interval's 5-minute summaries; an alert bundle carries the 1-minute summaries of the 15 minutes before and
the 10 s samples of the alert's window, so the timeline can show the exact shape of the spike. Nothing is sampled per
user.

Samples carry the device's clock (UTC) and the monotonic uptime, so a `time.changed` event doesn't tear the curve: the
service places samples by the monotonic offset from the bundle's own time stamp when the two disagree.

### 5.4 Thresholds and alerts

An **alert rule** is a metric, a threshold, a window and a severity. The defaults:

| Rule | Default | Range |
|---|---|---|
| CPU | ≥ 80 % for 5 min | 50–100 %, 1–60 min |
| Memory used | ≥ 90 % for 5 min | 50–100 %, 1–60 min |
| Disk active time | ≥ 90 % for 5 min, or queue length ≥ 2 for 5 min | 50–100 %, 1–60 min |
| Free space (any fixed volume) | < 10 % or < 5 GB – either opens it | 1–50 %, 1–100 GB; 0 turns that half off |
| Network errors | ≥ 100 errors/min for 5 min | 1–10,000, 1–60 min |
| Boot duration | > 120 s | 30–900 s |
| Sign-in duration | > 60 s | 10–600 s |
| App crashes | ≥ 3 crashes of the same program within 24 h | 1–100, 1–168 h |
| Service failures | any `service.failed` of an automatic service | on or off |
| Blue screen, hardware error, Defender detection | always an alert | – |

Each rule can be turned off. The admin sets them from the console per tenant (the defaults new activations get), per
folder or per device (§11.3), or IT fixes them by policy on the device (§12); the agent evaluates them, so an alert is
raised whether or not a console is running.

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
e-mail, document names, per-user application usage time. The agent's own enrolment secret, broker keys and monitoring
key are never in `agent`. A collector that would need any of these to work is a design error, not a configuration.

## 6. Schedules, triggers and activation

### 6.1 The interval

- `monitoring.intervalMinutes` (agent.json, set from the console) or the policy `MonitoringIntervalMinutes`: **60 by
  default, 60 to 10,080** (a week). A value below 60 is invalid and keeps 60 – an hour is the floor the requirement
  set, and the collectors' budget (§15.2) is made for it. §22 shows a longer interval saves requests, not bytes, so the
  default stays at an hour for a current timeline.
- Each device adds a fixed **jitter** (0–10 minutes, derived from the device id) so a fleet activated at the same
  minute doesn't upload at the same second.
- The first inventory runs **5 minutes after activation or after the service starts** (or 15 minutes after boot,
  whichever is later), then every interval. A missed run (the device slept) runs on resume, after the same 5 minutes.
- The full bundle is **full**: every enabled section (`MonitoringSections`), the interval's 5-minute summaries, the
  events that weren't sent by a trigger, and the open alerts. A section that didn't change still goes, as a hash only
  (§7): the service knows it is unchanged without receiving it again, and asks for it (`sections` in the upload's
  answer, §9.2) when it has no copy.

### 6.2 Triggered bundles

- A watched event (§5.2) that names a section makes the agent **re-collect that section after a 30 s debounce** (an
  installer writes several keys; a Windows Update installs several packages), diff it against the last copy, and send a
  **partial bundle**: the event(s), the changed section whole, and nothing else. An event without a section (a crash)
  is sent in a partial bundle with the last 15 minutes of 1-minute summaries, so the timeline shows the load around it.
- An alert opening sends the alert, the samples of §5.3 and the current `processes`. An alert closing sends the closed
  alert.
- **Flood control:** at most **30 triggered bundles per hour** per device; beyond that, events are kept in the outbox
  and go with the next full bundle, and the full bundle says how many were held back. Repeated identical events
  (the same crash of the same program, the same disk error) within 10 minutes are counted into one event with a count.
- Nothing is triggered during the first 5 minutes after boot; those events go with the first full bundle.

### 6.3 Sequence numbers

Every bundle gets the next number of a **per-device monotonic sequence** (persisted in the outbox before the bundle is
written, as `agent.json` is written: temp file, flush, replace). The service acknowledges by sequence number and
rejects a repeat, so nothing is stored twice and a gap is visible. A device enrolled again (new identity,
[agent-spec.md](agent-spec.md) §5.3) is a new device at the service; the console shows the old history under the
connection and marks the cut.

### 6.4 Activation

Nothing is collected until the console activates the device (v1 Q7: the agent takes the licence and the choice from
the console, not from its install).

1. **Entitlement.** RemoteHub sets on the tenant's record at the service: monitoring licensed, the tier (30, 60 or
   90 days), the device cap, the region. The console reads it when it connects to the broker and shows it under
   *Settings → RemoteHub Broker* and *Settings → Monitoring*.
2. **Activate.** The admin chooses *Monitoring → Activate…* on the tenant's synced folder, a folder or a device
   (§11.3). For each device the console asks the service for an **activation**: a token signed by the service with the
   tenant, the device id, the device's certificate fingerprint and a 24-hour expiry. The console sends it to the agent
   with `PUT /v1/monitoring/activation` (right `MonitoringSettings`) over the pinned connection, with the settings
   (§11.3) and, for a device reached through a jump host, the **upstream** (§9.3).
3. **Register.** The agent presents the activation to the service (directly or through its upstream) and registers
   the public key of its **device certificate** – the same RSA key the TLS pin and the broker registration use, which
   never leaves the device – signing the registration with it (PS256, as `BrokerProtocol` does). From then on every
   upload is signed with that key, and the service knows which tenant the device belongs to. A device that already has
   a broker registration in the same tenant is recognised by its key.
4. **Collect.** The agent writes the activation state to `agent.json` (`monitoring.activated`, the service address,
   the tenant id, the upstream), audits 1404 and starts the first inventory after 5 minutes.
5. **Deactivate.** `DELETE /v1/monitoring/activation` from the console (or *Deactivate* at the service for the whole
   tenant, which the agent learns from the next upload's answer) stops collection, deletes the outbox and the
   sections, audits 1405, and the service marks the device inactive; its rows age out with the tier or are deleted at
   once with *Delete this device's data…*. The policy `MonitoringEnabled` = 0 refuses an activation and deactivates an
   active device at the next policy read.

The service refuses uploads from a device whose tenant isn't licensed, is over its cap or whose activation was
revoked (403 with the reason); the agent backs off to the next interval, logs 3500 once per service run, and the Device
tab shows the reason.

## 7. Bundles

A bundle is a UTF-8 JSON document, **gzip-compressed**, signed by the device for the service (§9.2), with the envelope:

```json
{
  "format": 1,
  "deviceId": "3f1a9e2c-…",
  "tenant": "o_…",
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

- **Limits** (checked by the service before parsing further, and by the agent before writing): compressed ≤ 4 MB,
  uncompressed ≤ 32 MB, nesting depth ≤ 8, strings ≤ 1,024 characters (section-specific limits in §5.1), arrays as
  in §5.1, at most 500 events and 50 alerts per bundle. A bundle over a limit is refused whole and counted for the
  device; the agent never produces one (it clips while collecting), so a refused bundle means a changed device.
- **Unknown fields and sections are ignored**, not refused: an older service reads a newer agent's bundle and stores
  what it knows. `format` changes only when the envelope changes incompatibly.
- Section hashes are SHA-256 over the canonical JSON of `items` (sorted keys, as `BrokerProtocol.Canonical`), so the
  device and the service agree on "unchanged".
- The format is self-describing, per device, ordered, with the device's own time: what a self-hosted service would
  ingest unchanged later.

## 8. The device outbox

`%ProgramData%\RemoteHub\Agent\monitoring\` – inside the agent's folder, so it has the folder's ACL (SYSTEM and
Administrators only, [agent-spec.md](agent-spec.md) §5.3). No database: the agent has no SQLite dependency, and a
LocalSystem service shouldn't gain a native one for this. Files, written with `DurableFile`:

| File | Contents |
|---|---|
| `state.json` | Activation (service address, tenant, upstream), next `seq`, event-log bookmarks, last hash per section, the thresholds' state, last upload and acknowledged `seq`, settings in force |
| `outbox/<seq>.rhmon` | One bundle each (gzip JSON, §7) until the service acknowledges it. Kept at most **7 days or 64 MB**, oldest first – a device without a path for a week keeps its newest full bundle and the console sees a gap, not an outage |
| `sections/<name>.json` | The last copy of each section, for the diffs of §6.2 |
| `perf/<yyyyMMdd>.bin` | The day's 1-minute summaries, a fixed binary record per minute (about 200 bytes), 48 hours kept |

- Uninstall (`RemoteHubAgent.exe cleanup`) and deactivation (§6.4) remove the folder.
- A damaged `state.json` (as `agent.json` in [agent-spec.md](agent-spec.md) §7.2) keeps the activation from
  `agent.json` and restarts the sequence at the service's acknowledged number plus one. Nothing here can stop the
  service from starting – a monitoring error is logged (3500) and monitoring retries at the next interval.

## 9. Wire

### 9.1 The agent's endpoints (console → device)

Same listener, same TLS pin, same HTTP Basic Windows sign-in and `SignInGate` as `/v1/powershell`
([agent-spec.md](agent-spec.md) §4, §6). New rights in `Rights`: **`Monitoring`** (read) and **`MonitoringSettings`**
(activate, deactivate, change thresholds and the interval), both defaulting to `allowedGroups` like `PowerShell`.

| Method | Path | Right | Purpose |
|---|---|---|---|
| GET | `/v1/monitoring/status` | Monitoring | `{enabled, activated, tenant, path (direct / through <upstream>), lastSeq, acknowledgedSeq, lastUpload, lastFull, nextFull, intervalMinutes, settings, settingsSource (policy/device/default), openAlerts, outboxBytes, lastRefusal, time}` – `time` is the device's clock, so the console can note a skew |
| GET | `/v1/monitoring/now?sections=apps,drivers` | Monitoring | Collects the named sections now and the current 10 s samples and returns them to the console without writing a bundle (*Refresh now* in the Device tab, at most once a minute per account); the console shows them and the service isn't involved |
| PUT / DELETE | `/v1/monitoring/activation` | MonitoringSettings | The activation of §6.4 (token, service address, upstream) / deactivation. 409 with the policy's name when `MonitoringEnabled` = 0; 400 for a token that doesn't name this device |
| PUT / GET | `/v1/monitoring/settings` | MonitoringSettings / Monitoring | The device settings of §12.2 as JSON. Values a policy configures are refused with 409 and the policy's name; invalid values 400 with the reason. Written to `agent.json`, audited (1402) |
| POST | `/v1/monitoring/forward` | none – devices, not consoles (§9.3) | A relay: the body is another device's signed upload, forwarded to the service unchanged |

- Health (`/v1/health`) lists the feature **`monitoring`** when `MonitoringEnabled` is on, so a console knows before
  signing in; an older agent lists nothing and the Device tab says the agent needs updating. It never says whether the
  device is activated – that needs the right.
- Status codes as in [agent-spec.md](agent-spec.md) §6: 401 and 403 from the sign-in, 404 when `MonitoringEnabled` = 0,
  429 from the gate.
- Through a jump host and through the broker these requests travel as every agent request does.

### 9.2 Device → service: the upload

- `POST https://<broker>/v1/o/{org}/monitoring/upload` with the gzip bundle as the body and the device's signature
  over `remotehub-broker/1|<org>|monitoring|<deviceId>|<seq>|SHA-256(body)` in a header (PS256 with the device
  certificate's key, §6.4 step 3 – the signature the broker's registration already uses, so the Workers verify it
  with the stored public key). TLS to the front door as the broker connection uses it (`BrokerHttpProxy` applies).
- The answer: `{acknowledged: <seq>, sections: ["apps", …], settings?: {…}, deactivated?: true, reason?: "…"}` –
  `sections` names hash-only sections the service has no copy of, which the agent sends whole in the next bundle;
  `settings` are the tenant's defaults the service asks the device to adopt when the console set none (the console's
  own `PUT` wins); `deactivated` ends collection (§6.4).
- Retry: an upload that fails (network, 5xx, 429) stays in the outbox and is retried with backoff (1, 2, 4 … up to
  60 minutes), oldest first, at most 5 in flight; a 403 (not licensed, over cap, revoked) stops uploads until the next
  interval and is shown in status.
- Quota at the service: 60 uploads per device per hour, 4 MB each; per tenant, the entitlement's device cap and
  200 MB an hour; beyond that 429. The console never uploads on a device's behalf.

### 9.3 Through a jump host: the relay

A device whose agent has no broker connection uploads through the device it is reached by, as long as one device in
the chain has one (v1 Q3 follow-up). The route is the tree, as for every jump-host connection
([jump-host-design.md](jump-host-design.md) §1.1): the console, which knows the chain, gives the device its **upstream**
with the activation – the jump host's address, port and certificate fingerprint. The jump host's own upstream, if it has
one, is the next hop; the device with a broker connection posts to the front door.

- The device opens TLS to its upstream's agent and **checks the pin** it was given – the same check the console makes.
  It posts its signed upload to `/v1/monitoring/forward`. No Windows sign-in: a device has no account on its jump
  host, and the body carries its identity for the service.
- The jump host's agent **forwards bytes only**: the body, as is, to its own upstream or to the front door's fixed
  path for its own tenant (`/v1/o/{org}/monitoring/upload`), and returns the answer unchanged. It can't read the bundle
  beyond its size – it is gzip signed for the service, and TLS from the jump host to the front door is the jump
  host's; the device's signature makes any change visible to the service. It never forwards anywhere else, so it is
  not a proxy.
- **Limits on the relay:** policy `MonitoringRelay` (default on when `TunnelEnabled` is on; §12.2); a body at most
  4 MB; 60 forwards an hour per source address and 600 an hour per jump host, else 429; sources must be in
  `TunnelTargets` when that is set, since the devices behind a jump host are the targets it is allowed to reach – the
  same filter in the other direction; special addresses refused as for tunnels. A forward counts as a session for
  `maxSessions` only while it runs.
- **Audit on the jump host:** 1406 `MonitoringForwarded` per source device and hour (source address, count, bytes –
  the device id is inside the signed body and not read), 2502 `MonitoringForwardRefused` with the reason.
- **Chains:** the console builds the upstream list from the tree – a device three levels down gets the next device
  up; each hop forwards one step. The agent's `status` says *through JUMP01 → JUMP00*. A chain whose head loses its
  broker connection fills outboxes down the chain until it is back; nothing is lost for 7 days (§8).
- A device that has both a broker connection and a jump host above it uploads directly; the console gives it no
  upstream.

### 9.4 Console → service: queries and activation

The console talks to the service with its **console key** of the broker protocol (the registered console's PS256 or
ES256 signature over each request), tenant-scoped:

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/o/{org}/monitoring/entitlement` | Licensed, tier, cap, region, devices active |
| POST | `/v1/o/{org}/monitoring/activations` | An activation token for a device id and fingerprint (§6.4) |
| GET | `/v1/o/{org}/monitoring/devices/{id}/overview` | The tiles of the Device tab: current health, open alerts, last received, last 2 hours of 5-minute summaries |
| GET | `…/devices/{id}/sections/{name}[?at=<time>]` | The current (or a past) copy of a section, and `…/changes?from&to` the diff events |
| GET | `…/devices/{id}/events?from&to&lanes` · `…/alerts?open` · `…/performance?from&to&step&metrics` | The timeline's data, at the step the zoom needs |
| POST | `…/devices/{id}/alerts/{alertId}/acknowledge` · `DELETE …/devices/{id}` | Acknowledge; delete a device's data |
| GET | `/v1/o/{org}/monitoring/dashboards/{name}?folder&from&to` · `…/reports/{name}` | The dashboards and reports of §11.4, computed at the service, paged |
| GET | `/v1/o/{org}/monitoring/search?q=` | Devices by app, version, KB, driver (§11.4) |
| PUT | `/v1/o/{org}/monitoring/defaults` | The tenant's default settings for new activations |

Answers are JSON with the same limits as bundles; the console clips again when it shows them (§14). Nothing is
queried in the background except the overview of devices with an open Device tab and, with *Show alerts in the
navigation*, one call per minute for the tenant's open alerts.

## 10. The service

### 10.1 Shape

| Part | What | Where |
|---|---|---|
| **Front door** | The broker's Cloudflare Workers: verifies device and console signatures, enforces entitlement and quotas, validates bundles against §7, writes to the store, answers queries, runs activation | Cloudflare, every region |
| **Metadata** | Tenants, entitlements (tier, cap, region), activations, device → tenant and key, console keys, quotas' counters | The broker's D1 (today's database) |
| **Store** | Sections (current and history), events, alerts, performance; materialised views for the dashboards | **ClickHouse** – one cluster per region (EU, US), shared by the region's tenants, tenant id on every row (§21) |
| **Jobs** | Diffs of sections into change events at ingestion; retention by TTL; nightly materialised-view refreshes where a view isn't incremental | ClickHouse and a Workers cron |

Ingestion path: the Worker verifies the signature and quota, decompresses within the limits, validates, computes the
section diffs against the device's current rows, and inserts rows into ClickHouse over its HTTP interface with
**asynchronous inserts** (the Worker never waits for a merge). An upload is acknowledged when the insert is accepted.

### 10.2 Tables (ClickHouse, sketched)

```sql
-- every table: tenant String, device UUID, region fixed by the cluster; retention_days from the tenant's tier
events      (tenant, device, time DateTime64(3), kind LowCardinality(String), lane, severity UInt8, subject, title,
             detail String /* JSON, clipped */, count UInt32, record_id Int64, log, seq UInt64, source Enum('device','diff'),
             retention_days UInt16)
  ENGINE MergeTree PARTITION BY toYYYYMM(time) ORDER BY (tenant, device, time)
  TTL time + toIntervalDay(retention_days)
alerts      (tenant, device, alert_id UUID, rule, opened DateTime64, closed Nullable(DateTime64), peak, average, detail, acknowledged …)
  ENGINE ReplacingMergeTree(version) ORDER BY (tenant, device, alert_id)
perf_5m     (tenant, device, time DateTime, metric LowCardinality(String), avg Float32, max Float32, min Float32, top String, retention_days)
  ENGINE MergeTree PARTITION BY toYYYYMM(time) ORDER BY (tenant, device, metric, time) TTL time + toIntervalDay(retention_days)
perf_fine   (… step UInt16, values Array(Float32) …)     -- 1-minute and 10 s samples around alerts, TTL 7 days
sections    (tenant, device, name, collected DateTime, hash FixedString(32), data String /* gzip JSON */, retention_days)
  ENGINE MergeTree ORDER BY (tenant, device, name, collected) TTL collected + toIntervalDay(retention_days)
sections_current  (tenant, device, name, hash, collected, data) ENGINE ReplacingMergeTree(collected) ORDER BY (tenant, device, name)
apps_current (tenant, device, key, name, version, publisher, scope …) ENGINE ReplacingMergeTree ORDER BY (tenant, device, key)
device_state (tenant, device, last_seen, last_full, interval, path, agent_version, pending_restart, open_alerts, …)
  ENGINE ReplacingMergeTree(last_seen) ORDER BY (tenant, device)
-- materialised views: perf_days (per device, metric, day: avg, max, p95, minutes above threshold),
-- crashes_by_app_7d, pending_restarts, patch_level (newest cumulative update per build), disk_space, not_heard_from
```

- **Retention per tenant** is a column, so one table serves every tier: `TTL time + toIntervalDay(retention_days)`.
  A tier change rewrites the column for the tenant's rows (`ALTER … UPDATE`, background). The 1-minute and 10 s data
  keep 7 days on every tier.
- **Isolation:** every query the Worker issues starts with `WHERE tenant = {tenant}` from the verified console key;
  there is no query path without it (a unit test asserts it on every statement template). Row policies in ClickHouse
  (`CREATE ROW POLICY … USING tenant = currentUser()`-style per-tenant users) are the second fence for the dashboards'
  views. A tenant's region is fixed at creation; its rows never leave the region's cluster.
- **Deletion:** a device's or a tenant's rows are deleted with lightweight deletes and the metadata removed; backups
  age out with their 7-day window. A tenant export is the same tables filtered, as Parquet, on request.
- **Encryption at rest** is the cluster's (ClickHouse Cloud encrypts volumes; a self-managed cluster uses the
  provider's disk encryption); in transit TLS everywhere, including Worker → ClickHouse.

### 10.3 What the service never does

It never connects to a device, never runs anything on one, and never reads a bundle's content beyond validation and
the diffs. Operations at RemoteHub see tenant ids, sizes, counts and errors; opening a tenant's rows needs that
tenant's support consent and is logged (§14).

## 11. The console

No design (picture or ticket) came with the request, and no existing page shows a device's history, so the pages below
are proposed as trees of the project's own pieces, in the order they are read. The tool tabs (Event Viewer, Scheduled
tasks) are the nearest pages and give the chrome, lists and filters; the Notes and Scripts views give the cards and
splitters. The one new piece the request names is the **timeline**; §11.2 says where it stands and what it is built
from.

### 11.1 The Device tab

Opened with right-click → **Device** on a connection with an agent (also from the session tab's menu, and as the first
tab when *Open the Device tab with a connection* is on in *Settings → Monitoring*). Gated by the console feature
`AllowMonitoring` (§12.1). It queries the service (§9.4) and shows the **cache** – the last overview and the last 24
hours of timeline data for devices whose tab was opened on this PC, in the encrypted database – while offline, marked
*Last received …* and *offline – showing what this PC saw last*.

```
┌ ● NUC13  ·  Device  ·  nuc13.lab  [RemoteHub Agent] ─────────────────────────────── Last received 09:00 (through JUMP01) ↻ ┐
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
| Header | `RemoteToolChrome.Header` – stripe, icon `SymbolRegular.Pulse24`, connection name, *Device*, host, transport badge | The right end gets the **last-received line** (with the path: direct, through which jump host) and a *Refresh now* button (`SymbolRegular.ArrowSync24`) that calls the agent's `now` for the shown section |
| Section list | A `ListView` of sections on the left, like the Scheduled tasks tab's folder pane (`RemoteListView` side pane) | Width saved in `UiState.DeviceSectionsWidth`; a count badge on *Alerts* in `SessionStateToBrush.RedBrush`/`AmberBrush` by the highest open severity |
| Overview | `Card`s in a `WrapPanel`: six **stat tiles** (value, one-line caption, a 24-point sparkline of the last 2 hours) and a *Recent* list of the last 10 events and alerts | Tiles use the theme's text brushes; the sparkline is the timeline control (§11.2) in its smallest form, so there is one way to draw a curve |
| Timeline | §11.2 | |
| Alerts | `RemoteListView`-style list: state dot, opened, rule, duration, peak, top processes; the selected alert's detail below (samples as a small timeline, *Acknowledge*, *Open timeline here*, *Monitoring settings…*, *Run script…*, the device actions) | Acknowledging is recorded at the service for every console of the tenant; *Run script…* and the device actions are the manual remediation of §2 |
| Apps, Updates, Drivers, Services, Users | `RemoteListView` lists with the filter box, sortable columns, and a **changes** toggle (*Show changes in the last 7 days*) that marks added, changed and removed rows with the colour dots the tree uses | Columns per section follow §5.1; a right-click offers *Copy*, *Show in timeline*, *Which other devices have this version* (§11.4 search) and for an app *Processes* (opens the Processes tab filtered to its program) |
| Hardware, OS, Config | A two-column property list in `Card`s per group (as *Settings → Policies* lists values), with *Copy as text*; `config` has three groups – Group Policy, MDM/CSP, Security – each with its own filter box | Values that changed in the last 7 days get the change dot and a tooltip with the old value and when |
| Events | The watched events (§5.2), newest first, with the lane filter (*Changes*, *Problems*, *Sessions*) and the Event Viewer's level dots; *Open in Event Viewer* opens that tab at the record | |
| Settings | §11.3 | |
| Empty states | `EmptyHint` as in Notes: *Not activated – Activate monitoring on this device…*; *Nothing received yet – the agent collects its first inventory 5 minutes after activation*; *Monitoring is turned off on this device (policy)*; *This agent is older than 0.23 and doesn't collect*; *Not monitored – no path to the RemoteHub monitoring service*; *This tenant isn't licensed for monitoring*; *Offline – showing what this PC saw last* | Each names what to do, never a code |

Everything shown is text from the service, clipped as it was received (§14): no cell is a hyperlink, no path opens
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
- **Zoom** from 1 hour to the tenant's retention (30, 60 or 90 days – the buttons beyond the tier are greyed with the
  tier named) with the buttons, Ctrl+wheel or a drag on the axis; pan with the arrows, a drag, or Shift+wheel. The
  curve's resolution follows the zoom and the service answers at that step: 10 s samples where they exist (alerts),
  1-minute for up to 24 hours, 5-minute beyond. A gap in the data (device off, nothing received) is a gap in the curve,
  never interpolated; a `time.changed` event is a mark on the axis.
- **Hover** shows a tooltip with the value or the event's title and time; **click** selects the event or alert and
  shows its detail in a pane below (the same detail the Alerts and Events sections show); double-click a change opens
  its section with the item selected.
- **Correlation, kept honest:** when a change is selected, the footer counts the problems of the same subject after
  it against the same span before it (*3 crashes of Zoom.exe since – none in the 7 days before*); when a problem is
  selected, the footer lists the changes in the 7 days before it, nearest first. It is a count, labelled as one –
  RemoteHub doesn't claim a cause. The subject of a change is its program (an app's uninstall entry and its crashing
  executable are matched by publisher and install folder from `apps`) or, for a driver, the device class; where no
  match exists the footer says *no related problems recorded*. The counts come from the service, so the fleet view
  (§11.4) can show the same correlation across devices (*this driver version: 41 devices, crashes of Zoom.exe up on
  29 of them*).
- **Drawing:** brushes from the theme only – the text and stroke brushes for the axis and grid, the accent for the
  selected item, `SessionStateToBrush` red, amber, green and grey for severities and states, the connection's colour
  for the curve's fill. Both themes are checked (the lab's U cases screenshot the tab in light and dark). Text in the
  lanes is clipped to the lane; the full title is in the tooltip.
- **Keyboard:** Left and Right move between events, Home and End to the ends, +/− zoom, Enter opens the detail; the
  control is one `AutomationPeer` with the events as children, so FlaUI (the lab) and a screen reader see them.
- **Recording** (`IRecordableView`) includes the tab, as every tab.

### 11.3 Activation and settings

- **Entitlement:** *Settings → Monitoring* shows, per connected tenant, licensed or not, the tier, devices activated
  of the cap, the region, and the tenant's **defaults** (interval, sections, the rules of §5.4) with *Save defaults*
  (`PUT …/defaults`). An unlicensed tenant shows how to license it and nothing else is offered.
- **Activate:** right-click a device, a folder or the tenant's synced folder → *Monitoring → Activate…*. The dialog
  (built like `AgentInstallDialog`) names the tenant and region the data goes to, the tier, what is collected (a link
  to the README's list) and whether account names are included (the policy's state if set), shows the devices it will
  activate and which have no path (§2) or an older agent, and runs device by device like *Claim RemoteHub Agents…*,
  reporting which took it, which refused (policy, right) and which weren't reached. *Deactivate…* is the same dialog
  with the data question (*Keep the history until it ages out* / *Delete this device's data now*). Needs the right
  `MonitoringSettings` on each device, and `AllowMonitoring` on this PC.
- **Per device:** the Device tab's *Settings* section shows the activation state and path, the interval, the sections
  collected, and every rule of §5.4 as a row with on/off, threshold and window (`NumberBox`es with the ranges of §5.4),
  with the value's source (*device*, *tenant default*, *policy*). *Apply* sends `PUT /v1/monitoring/settings`; a value
  the policy configures is greyed with the lock and *Set by your organisation*, as everywhere
  ([admx-design.md](admx-design.md) P2). Without the right the section is read-only and says so.
- **For many devices:** right-click a folder → *Monitoring settings…* opens the same form with *Apply to every device in
  this folder* (and its subfolders), runs device by device and reports. The device holds its settings, so two consoles
  can't disagree about a device; the tenant's defaults apply only where no console set a value.
- **Console settings** (*Settings → Monitoring*): *Open the Device tab with a connection*; *Show alerts in the
  navigation*; the cache's size (default 50 devices).
- **The navigation:** with that setting on, a connection with an open alert shows the alert dot on its icon (the
  broker state dot's position; red for a blue screen, hardware error, detection or a crash alert, amber for a
  threshold), and a folder shows the count of such devices in its caption. Turning it off keeps the Device tab's badge.
  The console asks the service once a minute for the tenant's open alerts.

### 11.4 The Monitoring view (phase 5)

A fourth view on the rail – `RailView("Monitoring", SymbolRegular.DataUsage24, "Monitoring", "Ctrl+4")`, gated by
`AllowMonitoring` like Notes and Scripts ([notes-view-design.md](notes-view-design.md) §3.8). Three cards as in Scripts:
a list of **dashboards and reports** on the left, the chosen one in the middle, its filters on the right (tenant,
folder, period).

Dashboards are computed at the service (§10.2's materialised views) and shown as tables and tiles; each row opens the
device's tab at the right section. **All of them ship in phase 5, and the customer picks which to show** (v1 Q4): the
list's *Show* toggles, kept in `UiState`. The set:

*Open alerts* · *Devices not heard from* (longer than 2 intervals) · *Pending restarts* · *Disk space* · *Crashing
apps (last 7 days)* · *Blue screens and hardware errors* · *Recent changes* · *Patch level* (the newest cumulative
update per build and who lacks it) · *Drivers by version* (with the correlation of §11.2 across devices) · *Defender
state* · *Boot and sign-in duration* · *Configuration drift* (devices whose GPO or CSP values differ from the folder's
most common).

**Reports** are the same tables for a chosen folder and period, exported as CSV or an HTML file (with the timeline as
an SVG), through *Save as…*, never written to a temp file; a report export is audited in the console. **Search**
(`…/search`) answers *which devices have Zoom 6.2.1*, *which lack KB5064081* from the navigation search box with a
`monitoring:` prefix, and from a row's *Which other devices have this version*.

## 12. Policy and settings

### 12.1 Console

| Value (`HKLM|HKCU\SOFTWARE\Policies\RemoteHub`) | Type | Meaning (default) |
|---|---|---|
| `AllowMonitoring` | DWORD | 0: no Device tab, no activation, no Monitoring view, and the cache is emptied at the next start (1). Below `AllowAgent` and `AllowBroker`, since the data path is the broker's |
| `AllowMonitoringActivation` | DWORD | 0: this PC may look but not activate, deactivate or change settings (1) |
| `AllowMonitoringExport` | DWORD | 0: no report export (1) |

### 12.2 Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`, ADMX category *RemoteHub Agent → Monitoring*)

The same names, without the prefix, are the `monitoring` object in `agent.json` (written by the console's activation
and `PUT /v1/monitoring/settings`); the policy wins and is shown locked (P1). Read when used, as the other agent
policies.

| Value | Type | Meaning (default) |
|---|---|---|
| `MonitoringEnabled` | DWORD | 0: the device can't be activated and an active device is deactivated at the next read; `/v1/monitoring/*` answers 404, health leaves out `monitoring`, the outbox is deleted (1 – *may be activated*; nothing is collected until the console activates) |
| `MonitoringService` | SZ | The only service address the agent may upload to (any the activation names). For tenants that pin their broker with `BrokerUrl`, the same host |
| `MonitoringRelay` | DWORD | 0: this device never forwards other devices' uploads (§9.3) (1 when `TunnelEnabled` is on) |
| `MonitoringIntervalMinutes` | DWORD 60–10080 | The full inventory's interval (60) |
| `MonitoringSections` | MULTI_SZ | Which sections are collected: any of `os`, `hardware`, `apps`, `updates`, `drivers`, `services`, `users`, `config`, `agent` (all) |
| `MonitoringIncludeUsers` | DWORD | 0: no account names anywhere – sessions, local users, group members, crash and sign-in events say *a user*; per-user app entries and the user's GPOs and CSP values are left out (1) |
| `MonitoringEvents` | DWORD | 0: no event watching, only the interval (1) |
| `MonitoringPerformance` | DWORD | 0: no sampling, no threshold alerts (1) |
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

No install-time switch and no enrolment-file field: activation comes from the console (§6.4). The MSI and the
`.rhagent` file stay as they are.

## 13. Audit and logging

Agent (`AuditEvent`, the Application log unless `AuditToEventLog` = 0):

| Id | Event | Recorded |
|---|---|---|
| 1400 `MonitoringStarted` | Collection runs (once per service run while activated) | Interval, sections, path (direct or upstream) |
| 1401 `MonitoringRead` | A console read status, settings or `now` | Account, source address, what – never the content |
| 1402 `MonitoringSettingsChanged` | Settings changed through the endpoint | Account, source, which values and the new values (no secrets exist here) |
| 1403 `MonitoringUploaded` | The first successful upload per service run, and again after a failure | Service, `seq`, path |
| 1404 `MonitoringActivated` / 1405 `MonitoringDeactivated` | Activation changed | Account, source, tenant id, service address, upstream; on deactivation whether the data was asked to be deleted |
| 1406 `MonitoringForwarded` | This device forwarded other devices' uploads (per source and hour) | Source address, count, bytes |
| 2500 `MonitoringAlert` | An alert opened or closed | Rule, value, duration, top programs – also for Intune or a SIEM |
| 2501 `MonitoringRefused` | A read, write or activation refused (right, policy, token) | Account, source, reason |
| 2502 `MonitoringForwardRefused` | A forward refused (policy, limit, source) | Source address, reason |
| 2503 `MonitoringUploadRefused` | The service refused this device's uploads (licence, cap, revoked) | Reason, until when uploads pause |
| 3500 `MonitoringError` | A collector, the outbox or an upload failed | Which and `ex.Message`; the next interval retries |

The agent's log file additionally records each collection's duration and size (*inventory: 9 sections, 184 KB, 6.2 s*)
so a slow device can be diagnosed. Never in any log: section contents, event messages, account names other than the
audited reader's.

Console: `DiagLog` records queries and refusals as counts and durations – *monitoring: 12 queries, 1 refused, 2 not
reached* – never names, hosts or contents. The console's audit log (`AuditEvents`) gets `MonitoringActivated`,
`MonitoringDeactivated` (which connection, data kept or deleted), `MonitoringSettingsChanged`, `MonitoringExport`
(which report, period, folder) and `MonitoringDataDeleted`.

Service: an audit log per tenant (activations, deactivations, deletions, exports, entitlement changes, support
access) readable by the tenant's consoles under *Settings → Monitoring → Service audit*.

## 14. Security and privacy

- **The trust statement.** Monitoring data is stored and queried by RemoteHub's service on the tenant's behalf. This is
  a different model from broker sessions, which are sealed end to end, and it is said plainly in the README,
  `PRIVACY.md` and the activation dialog: what is collected (§5.1), what never (§5.5), where it is kept (the tenant's
  region), for how long (the tier), who at RemoteHub can see it (operations: ids, sizes, counts, errors; a tenant's rows
  only with that tenant's support consent, logged in the tenant's service audit), and how it is deleted.
- **Identity on every upload.** A bundle is signed with the device's certificate key, registered under the tenant at
  activation; the front door verifies it before anything else, and a relay can't alter it. Consoles sign their queries
  with their registered key; every query is tenant-scoped by that key, never by a parameter.
- **Tenant isolation at the store:** tenant id on every row, in every `ORDER BY`, in every `WHERE` the Worker issues
  (tested), row policies as the second fence, a region per tenant with no cross-region queries.
- **The service treats bundles as hostile** (TB8): the gzip is read through a size-limited stream (compressed and
  uncompressed limits of §7 enforced while reading, not after), the JSON parsed with depth 8 and no comments, every
  string clipped to its limit and stripped of control characters and bidirectional overrides before storing, numbers
  range-checked, dates parsed as UTC or dropped. A refused bundle is counted for the device and shown in the Device tab
  (*The device sent data the service refused*); later bundles are still read. The console clips again what it shows and
  renders text only: nothing from a device is a path it opens, a URL it shows as a link, a command it runs, or markup.
- **Quotas** per device (60 uploads an hour, 4 MB), per tenant (cap, 200 MB an hour), per relay source and jump host
  (§9.3), per console (queries a minute) – all answered with 429, logged, never blocking another tenant.
- **The device gives up nothing new to the network:** monitoring adds no listener, no helper, no pipe. The settings
  and activation endpoints accept small fixed JSON objects, range-checked, behind a right. The relay endpoint accepts a
  bounded body and forwards it to one fixed path.
- **Keys:** the device's monitoring identity is its existing non-exportable certificate key; the console's is its
  broker console key; the activation token lives 24 hours and names one device. No new secret is stored anywhere.
- **People at the device:** `MonitoringIncludeUsers`, `MonitoringSections` and `MonitoringEvents` narrow what IT
  collects; the Application log shows activation (1404) and every collection run (1400), so the collection is visible on
  the device.
- **Secrets in configuration values:** `config` leaves out any policy or CSP value whose name says *password*,
  *secret*, *key*, *token*, *credential* or *pin*, and every REG_BINARY over 64 bytes. Paths are kept (they are
  configuration), but a path under a user's profile is shortened to `%USERPROFILE%\…` with `MonitoringIncludeUsers` = 0.
- **On the console** the cache lives in the encrypted database under the same key hierarchy as passwords and notes
  ([local-db-design.md](local-db-design.md) §4) and is small (50 devices); exports (§11.4) are an explicit *Save as…*
  by the admin and audited.
- **Compliance groundwork** for the service, outside this document but named so it isn't forgotten: a data processing
  agreement naming the sub-processors (Cloudflare, the ClickHouse host), the region choice, deletion within 30 days
  of a tenant's end, backups' retention, and a security review of the Workers' code paths (§19).

## 15. Compatibility and budget

### 15.1 Compatibility

- Windows 10 1809 / Server 2019 and later, as the agent ([agent-spec.md](agent-spec.md) §10). Sources that an old
  build lacks (`% Processor Utility`, `MSFT_Volume` on 1809 without the Storage module, Diagnostics-Performance on
  Server Core) are skipped with the section saying `"unavailable": ["…"]`, never failing the collection.
- Server Core and devices without a user: `users` has no sessions, sign-in rules never fire; everything else works.
- WDAC/Constrained Language Mode and `PowerShellEnabled` = 0 don't matter: the collectors are native code in the
  agent, not PowerShell – which is why they are (§1.1).
- An **older agent** has no `monitoring` feature and the Device tab says so; an **older jump host** has no relay
  endpoint, so a device behind it has no path until the jump host is updated – the activation dialog says which.
- The agent is published trimmed (`PublishTrimmed`, partial): WMI through `Microsoft.Management.Infrastructure` (the
  CIM client PowerShell itself uses; trim-safe with the usual root descriptors) rather than `System.Management`; the
  Windows Update Agent and `PackageManager` through the interop already in the agent (`StoreApps.cs`) or source-generated
  COM. New packages get their entry in `THIRD-PARTY-NOTICES.md`.

### 15.2 Budget on the device

| Part | Budget | How it is kept |
|---|---|---|
| Sampler | < 0.1 % CPU, < 10 MB | One PDH query, 10 s; process list from one `NtQuerySystemInformation` call |
| Full inventory | < 30 s CPU, < 100 MB peak, finished within 5 minutes | Sections collected one after another on a thread at `BelowNormal` priority, each with its own stop (WMI 30 s, WU search 60 s); a section that overruns is skipped this time and reported in `agent` |
| Event watching | < 0.1 % CPU | `EventLogWatcher` subscriptions with XPath filters per id, no polling |
| Outbox | ≤ 64 MB, a few hundred KB in practice | §8 |
| Uploads | ≤ 2 MB an hour in practice | Full bundles with hash-only sections; gzip (§22) |
| Relay on a jump host | ≤ 600 forwards an hour, bytes in and out | §9.3 |

The lab measures these (M-PERF-05, M-PERF-06) on the slowest VM (W10-1809) and the result goes into this section.

## 16. Tests

**Unit tests** (`tests/RemoteHub.Tests`, one class per area with a `/// <summary>`):

- `ThresholdStateTests` – a fake clock: opens at 90 % of samples above, not at 4 of 5 minutes, hysteresis, cool-down,
  the boot grace period, a `time.changed` jump doesn't open or close anything.
- `SectionDiffTests` – added, removed, changed apps; the same app in two scopes; a version that goes backwards; a
  driver's INF rename; 10,000 items in under a second (shared with the service's implementation as test vectors).
- `MonitoringBundleTests` – round trip; every limit of §7 refused (a 4 MB + 1 byte bundle, depth 9, a 1,025-character
  name clipped, 501 events refused, an unknown section ignored); a gzip bomb (1 GB of zeros) stopped at 32 MB; control
  and bidirectional characters stripped; the signature over the body verified and a changed byte refused.
- `MonitoringActivationTests` – a token for another device or fingerprint refused (400), an expired one refused, the
  policy's 409, deactivation deletes the outbox and sections, `MonitoringEnabled` = 0 deactivates.
- `MonitoringRelayTests` – a forward goes to the fixed path and nowhere else; the pin check against the upstream; the
  limits (size, per source, per host) answer 429; a source outside `TunnelTargets` refused; a chain of two forwards.
- `MonitoringServiceClientTests` – the console's queries are signed and tenant-scoped; the cache is used when offline
  and marked; nothing is queried in the background without an open tab or the navigation setting; answers are clipped.
- `EventKindTests` – each Windows event of §5.2 (recorded XML from the lab) maps to its kind and fields; an event with
  a user name maps to *a user* with `MonitoringIncludeUsers` off.
- `MonitoringPolicyTests` – `PolicyCatalog` entries, the ADMX template check, settings refused with 409 when configured.
- `DeviceTimelineTests` – bucketing to the zoom, the correlation counts, a gap stays a gap; rendered to a
  `RenderTargetBitmap` in both themes with no exception (`[SkippableFact]` when there is no desktop).
- The collectors run on the Windows CI runner against the runner itself (`MonitoringCollectorTests`): each section
  parses, meets its limits, finishes within its stop, and contains none of §5.5 (a scan for `password`, the runner's
  known secrets' shapes).

**Service tests** (the broker's repository): every query template carries the tenant predicate (a test walks them);
a console of tenant A can't read tenant B's device by id (404, not 403); an upload with a bad signature, a repeated
`seq`, a revoked activation or an over-cap tenant is refused with the right status; a bundle over each limit is refused
and counted; TTL by tier; deletion of a device and a tenant; the materialised views against fixtures; ingestion of
1,000 bundles a second on the test cluster.

**Lab** (`docs/test-lab-cases.md` gets a section *Agent – monitoring (M)*; `lab/tests/M-*.Tests.ps1`, `# LabQuick`
where a case applies to Server too; the lab broker gains the service with a local ClickHouse in the same
`wrangler dev` setup, test-lab-spec §5.3):

| ID | Case | Pass when | How | P |
|---|---|---|---|---|
| M-ACT-01 | Activation | the harness activates the VM through the agent → `status` shows activated, path direct, 1404 audited; within 10 minutes the service has a full bundle with every section; health lists `monitoring` | H | 1 |
| M-ACT-02 | Not before activation | a fresh install collects nothing for 30 minutes: no outbox, no 1400, no upload at the lab service | H PS | 1 |
| M-ACT-03 | Deactivation and policy | `DELETE` → collection stops, outbox gone, 1405; `MonitoringEnabled` = 0 on an active device → deactivated at the next read, 404, health without `monitoring` | H PS | 1 |
| M-ACT-04 | Rights and tokens | an account without `MonitoringSettings` can't activate (403, 2501) but reads `status`; a token for another device id or fingerprint → 400; an expired token → 400 | H | 1 |
| M-ACT-05 | Licence at the service | the lab tenant set over its cap or unlicensed → uploads 403, 2503 audited, `status.lastRefusal` names it, the Device tab shows it; licensed again → uploads resume within the interval | H | 1 |
| M-INV-01 | Sections' truth | the apps the VM has (the agent MSI, a test MSI), its KBs, its drivers, local users, the lab's GPO and the Defender state appear with the right values at the service; nothing of §5.5 in the bundle (scan) | H PS | 1 |
| M-INV-02 | Interval | with `MonitoringIntervalMinutes` = 60 the second full bundle arrives 60 ± 10 minutes after the first; 30 is refused (invalid, logged once) and 60 stays | H PS | 2 |
| M-INV-03 | Users off | `MonitoringIncludeUsers` = 0 → no account name anywhere in a full bundle (scan for the VM's known accounts), counts still there | H | 1 |
| M-INV-04 | Hash-only sections | the second full bundle carries unchanged sections as hashes; the service asked for none; a section deleted at the service is asked for and arrives whole with the next bundle | H | 2 |
| M-EVT-01 | App installed | install a test MSI → a partial bundle with `app.installed` and the `apps` section at the service within 90 s; uninstall → `app.removed` | H PS | 1 |
| M-EVT-02 | Store app and EXE installer | `Add-AppxPackage` and an Inno Setup test installer → events within 90 s, kind says Store / installer | H S | 2 |
| M-EVT-03 | Driver and update | install a test driver (the lab's signed null driver) → `driver.installed`; install a `.msu` → `update.installed` and `os` pending restart; a definition update → recorded, no triggered bundle | H PS | 2 |
| M-EVT-04 | Crash | a test program that faults → `app.crash` with module and code within 60 s, 3 faults → the crash alert; `app.hang` from a program that stops pumping messages | H S | 1 |
| M-EVT-05 | Blue screen | a forced bug check in the guest → after the restart `os.bluescreen` with the code, `device.started` with the boot duration | H PS C | 1 |
| M-EVT-06 | Service failure | kill a test service's process → `service.failed` and the alert; the agent's own service isn't listed as failed after a lab-initiated restart | H PS | 2 |
| M-EVT-07 | Group Policy | change the lab GPO and `gpupdate` → `config.gpo` with the changed values named within 2 minutes; `gpupdate` without a change → no bundle | H PS | 2 |
| M-EVT-08 | Flood | 100 crashes in a minute → identical events counted, at most 30 triggered bundles an hour, the rest in the next full bundle with `heldBack`; 20 locks and unlocks → no triggered bundle | H S | 2 |
| M-PERF-01 | CPU alert | a burner at 100 % on all cores for 6 minutes → the alert opens between 5:00 and 5:30 with the burner on top, the alert bundle holds the 10 s samples; the burner stops → closes within 6 minutes with duration and peak | H S | 1 |
| M-PERF-02 | No false alert | 4 minutes at 100 %, then idle → no alert; 10 minutes alternating 30 s at 100 % and 30 s idle → no alert | H S | 1 |
| M-PERF-03 | Thresholds from the console | set CPU 50 % / 2 min through PUT → status shows `device` as source and the alert opens after 2 minutes of a burner; set `MonitoringCpuPercent` by policy → PUT answers 409, status says `policy`; the tenant default applies where neither is set | H PS | 1 |
| M-PERF-04 | Memory and disk | fill memory to 95 % / hammer the disk for 6 minutes → the alerts; free space under 5 GB → the free-space alert once, not every minute | H S | 2 |
| M-PERF-05 | Budget | over 2 hours on W10-1809 the agent's CPU time grows by less than 60 s and its working set stays under 120 MB; the inventory's log line says under 60 s | PS | 1 |
| M-PERF-06 | Boot and sign-in | restart; sign in as normaluser → `device.started` with a boot duration, `session.signIn` with a duration (confirms the measurement) | H C S | 2 |
| M-RELAY-01 | Through a jump host | **2VM**, the target without a broker connection: activation with the jump host as upstream → uploads arrive at the service signed by the target; the jump host audits 1406; `status` says *through JUMP01*; the jump host's log has no section contents | H | 1 |
| M-RELAY-02 | Relay limits | `MonitoringRelay` = 0 → 404 and the target's outbox fills, then drains when it is 1 again; a body over 4 MB → 413; 61 forwards in an hour from one address → 429 and 2502; a source outside `TunnelTargets` → 403 | H | 1 |
| M-RELAY-03 | Chain and loss | jump host behind a jump host: two hops, uploads arrive; the head's broker connection cut for 20 minutes → outboxes fill, nothing lost, all arrive within the interval after it is back | H N | 2 |
| M-CON-01 | Device tab | **2VM**, RemoteHub in the client VM: the tab shows the inventory, the events of M-EVT and the alert of M-PERF-01 on the timeline from the lab service; offline → the cache is shown and marked; screenshots in both themes | U | 1 |
| M-CON-02 | Diff events | install a test app between two full bundles without the watcher (`MonitoringEvents` = 0) → the tab shows *between HH:MM and HH:MM* for the change | U H | 2 |
| M-CON-03 | Dashboards and search | the lab's devices appear in *Open alerts*, *Pending restarts*, *Crashing apps*; `monitoring:` search finds the test app's version; a CSV export has the rows and is audited | U | 2 |
| SEC-MON-01 | Hostile bundle at the service | uploads from a test client with the VM's key: over the limits (size, depth, a gzip bomb, strings with control and RTL characters, a `file://` and an `http://` string, a 100,000-item apps list) → each refused or clipped at the service, counted; the console shows text only, nothing is opened or fetched | H U | 1 |
| SEC-MON-02 | Wrong identity | a bundle signed by device A naming device B, a repeated `seq`, a bundle after deactivation, a token issued for another tenant → refused with the right status, nothing stored under B | H | 1 |
| SEC-MON-03 | Tenant isolation | the lab makes two tenants: console A's queries for B's device id → 404; A's dashboards never list B's devices; a relay on A's jump host forwards a B device's upload and the service files it under B (identity is the signature, not the path) | H | 1 |
| SEC-MON-04 | The relay is not a proxy | a request to `/v1/monitoring/forward` whose body isn't a signed upload is refused without being sent anywhere (the lab service sees nothing); the jump host forwards only to its configured service address | H N | 1 |

A failing collector on a lab VM is a finding for §15.1's skip list, not a reason to lower a limit.

## 17. Phases

| Phase | Content | Done when |
|---|---|---|
| **1 – Inventory and the service** | Agent: collectors (§5.1), outbox (§8), full bundles on the interval (§6.1), activation (§6.4), the direct upload (§9.2), endpoints `status`, `now`, `settings`, `activation`, rights, `MonitoringEnabled`, `…Service`, `…IntervalMinutes`, `…Sections`, `…IncludeUsers`, audit 1400–1405, 2501, 2503, 3500, cleanup. Service: entitlement and activation in D1, the upload with signature and quotas, validation, ClickHouse tables for sections, events (diff), device state, the overview and section queries, TTL by tier, deletion. Console: `MonitoringServiceClient`, the cache, the Device tab with Overview (no sparklines yet), the inventory sections and Events (diff events only), *Activate…* and *Deactivate…*, `AllowMonitoring`, the entitlement in Settings | M-ACT-*, M-INV-*, M-CON-02, SEC-MON-01–03 pass; §15.2 measured |
| **2 – Events and alerts** | Agent: watchers (§5.2), triggered bundles (§6.2), sampler (§5.3), thresholds (§5.4), the remaining policies, 1402, 2500. Service: alerts, performance tables, the timeline queries, tenant defaults. Console: Alerts section, Events with lanes, settings section, folder apply, navigation dots, tenant defaults in Settings | M-EVT-*, M-PERF-* pass |
| **3 – Timeline** | `DeviceTimeline`, the Timeline section, sparklines on the tiles, correlation footer (service-side counts), keyboard and automation | M-CON-01, the U screenshots reviewed |
| **4 – Relay through jump hosts** | Agent: `/v1/monitoring/forward`, upstreams, `MonitoringRelay`, 1406, 2502; console: upstreams from the tree in the activation | M-RELAY-*, SEC-MON-04 pass |
| **5 – Monitoring view** | The rail view, every dashboard of §11.4 with the customer's choice, reports and export, search, the cross-device correlation, the service audit page | M-CON-03 |
| **Later – remediation** | An alert runs a deployed script version (scripts-view-design.md) on the device: which alerts, which scripts, rings and stop conditions, approval, audit – a design of its own, decided for (v1 Q6) | – |
| Later | GPU load; per-process network; a learned baseline per device and *unusual for this device* marks; alerts as toast notifications in the console and as e-mail from the service; a Windows event forwarding guide so SIEMs read 2500; a self-hosted service image; on-demand download of the agent's monitoring binaries | – |

Phases 1 and 2 touch the agent, the service and the console together; each ships behind `MonitoringEnabled`,
`AllowMonitoring` and the entitlement, so a release in between changes nothing for a device or tenant without them.
Phase 4 can run in parallel with 3.

## 18. Docs in step

When phase 1 lands:

- [agent-spec.md](agent-spec.md): §2 gets a bullet for monitoring (as §2 has for the desktop); §6 the endpoints and the
  `monitoring` feature; §7 the `monitoring` object and §7.1 the policies and rights; §8 changes *Nothing leaves the
  device* to *The agent sends no telemetry and no crash reports to anyone. Once monitoring is activated from the
  console, it uploads inventory bundles to the tenant's RemoteHub monitoring service*; §11 the row.
- README: the *RemoteHub Agent* section gets *Device monitoring* (what is collected, where it goes, the activation, the
  policy switch), *Remote tools* the Device tab, *RemoteHub Broker* the service, and the line "The agent sends nothing
  anywhere" is qualified as above.
- `PRIVACY.md`: the sentence *The RemoteHub Agent on your servers sends nothing at all* becomes *sends nothing to us
  unless your organisation activates device monitoring; then it sends an inventory of the device to the RemoteHub
  monitoring service in your organisation's region, where it is stored for your organisation – see the README*; the
  list of what is and isn't collected (§5.1, §5.5), the retention tiers and the deletion are stated.
- [admx-design.md](admx-design.md) §9 gets the *Monitoring* category and the two rights; §5 `AllowMonitoring`,
  `AllowMonitoringActivation`, `AllowMonitoringExport`.
- [threat-model-technologies.md](threat-model-technologies.md): the new endpoints, the relay and the service under
  §2 and a *Device → service* and *Console ← service* boundary; [security-test-lab-design.md](security-test-lab-design.md)
  §3 TB8 gets SEC-MON.
- [jump-host-design.md](jump-host-design.md) §6: `MonitoringRelay` and the forward endpoint.
- [local-db-design.md](local-db-design.md) §6: the cache table and the schema version.
- `docs/test-lab-cases.md` §new *M* and `test-lab-test-document.md`, with `lab\Update-LabCaseStatus.ps1` after the
  first run; `test-lab-spec.md` §5.3 the service in the lab broker.

## 19. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | **ClickHouse Cloud or self-managed?** Cloud: no operations, encryption and backups included, about $440 a month for the smallest two-replica Scale service and compute-driven beyond (§22.4). Self-managed on three VMs per region: cheaper at 200,000 devices, but patching, backups and upgrades are RemoteHub's. | Cloud for v1 in both regions; revisit when a region's compute passes about $3,000 a month. |
| Q2 | **Where does the ingestion run** – the Worker inserts into ClickHouse directly (simple, one hop) or through a queue (Cloudflare Queues) that smooths bursts and survives a ClickHouse outage? | Direct with asynchronous inserts in phase 1; the outbox on the device already smooths; a queue when the measured p99 of an upload passes 2 s. |
| Q3 | **Tier change and data:** when a tenant moves from 90 to 30 days, is the older data deleted at once (TTL rewrite) or kept until it would have aged out? | Deleted at the next TTL merge (within a day), said in the tier dialog. |
| Q4 | **Support access:** who at RemoteHub may open a tenant's rows, with what consent and for how long? | Only after the tenant's admin grants *support access* in *Settings → Monitoring* for 7 days; every read logged in the tenant's service audit. Needs the operations design. |
| Q5 | **The console key for queries** – reuse the broker console key as proposed, or a separate monitoring key the tenant admin can revoke without ending broker access? | Reuse: one identity per console, revoked at *Remove* on the broker page. |
| Q6 | **Alert notifications** from the service (e-mail, webhook, Teams) – phase 5 or later? | Later; phase 5 is dashboards. |
| Q7 | **Upstream changes:** when a device is moved in the tree so its jump host changes, the console must re-send the upstream. Automatically when the console notices the move, or as part of the next *Monitoring settings…* on it? | Automatically at the move, like the credentials question the tree already asks, with a line in the move's confirmation. |
| Q8 | **Sign-in duration measurement** (§5.2) – 7001 to the shell's first window is the agent's own measurement. Acceptable, or boot duration only in v1? | Try it in the lab (M-PERF-06); fall back to boot duration only if it isn't reliable. |

## 20. Decided

What settled each decision, so a reviewer sees where the request, the project's patterns, the owner's answers and a
judgement call were the deciding voice:

| Decision | Settled by |
|---|---|
| Collection is native in the agent, not PowerShell scripts | The project: `PowerShellEnabled` = 0 and WDAC must not stop monitoring; the agent's own principle of no command set stays |
| The history lives in a RemoteHub-hosted, multi-tenant service, not in consoles | The owner (v1 Q9): one environment holds a couple of hundred thousand devices across tenants, so the dedicated service is designed now rather than retrofitted |
| The service reads the data (no end-to-end seal for monitoring); region per tenant, shared cluster, tenant id on rows | The owner: hosted SaaS, EU/US per tenant |
| The console queries the service and keeps a small cache | The owner |
| Monitoring is activated from the console under the tenant's entitlement; nothing is collected before | The owner (v1 Q7): the agent takes licence and choice from the console; entitlement on the tenant at the service |
| Devices behind jump hosts upload through the chain as long as one device reaches the broker | The owner |
| Retention tiers 30 (default), 60, 90 days | The owner |
| All dashboards ship, the customer picks | The owner (v1 Q4) |
| Remediation is a planned later phase | The owner (v1 Q6) |
| Account names collected by default, policy to turn off; the full configuration value set | The owner (v1 Q1, Q2) |
| "Send every 60 minutes" is an upload to the service; the interval's floor is 60 minutes | The request and the budget; §22 shows the interval saves requests, not bytes |
| Full inventory on the interval, changed sections on a trigger, the service computes diffs | The request ("send the selective information") and robustness – a snapshot can't be out of step |
| Thresholds live on the device, set from the console per tenant default, folder or device, policy wins | The project's P1 and P6 (the device is the boundary); two consoles can't disagree about a device |
| Alerts need 90 % of samples above the threshold and close with hysteresis | Judgement: "over 80 % for 5 min" read as sustained, not averaged |
| Session events and definition updates don't trigger bundles | The cost calculation (§22): dozens a day per device |
| No SQLite on the device | The agent's dependency footprint as a LocalSystem service; files with `DurableFile` suffice |
| ClickHouse for the service's store, D1 for its metadata | §21 |
| The timeline is drawn by the project itself, from theme brushes | The project has no charting library and draws its own lists and toolbars |
| Correlation is a count, labelled as one | Honesty: RemoteHub shows the admin the order of things; it doesn't decide the cause |
| Free-space rule: percent or GB, either opens | Judgement; an admin who wants one sets the other to 0 |

## 21. The database: ClickHouse for the service, and why not elsewhere

### 21.1 How much data

Per device and day, as the design stands (§22.1 has the assumptions):

| Data | Rows or points | Stored (gzip JSON) |
|---|---|---|
| 5-minute performance, 12 metrics × avg/max/min, plus top processes | 288 points × 12 metrics | about 15 KB |
| 1-minute performance (7 days only) | 1,440 × 12 | about 200 KB |
| Events (watched and diff), 50 a day | 50 | about 25 KB |
| Section changes, about one a day (Windows apps update themselves) | 1 snapshot | 20–60 KB |
| **Total for the history** | | **about 80 KB a day** |

| Devices in one environment | 30 days | 90 days | plus 7 days of 1-minute data | `perf_5m` rows a day |
|---|---|---|---|---|
| 10,000 | 24 GB | 72 GB | + 14 GB | 35 million |
| 100,000 | 240 GB | 720 GB | + 140 GB | 346 million |
| 200,000 | 480 GB | 1.4 TB | + 280 GB | 691 million |

These are gzip-JSON sizes; a column store with typed columns holds the performance and event rows in roughly a third
to a half of that (§22.3), the section blobs about the same.

### 21.2 What is asked of it

| Question | Who asks | Shape |
|---|---|---|
| One device's timeline for a day / 90 days | Device tab | A range over `(tenant, device, metric, time)` – a few thousand rows |
| One device's inventory and changes | Device tab | Point lookups in `sections_current`, a range in `events` |
| Which devices have app X, driver Y, KB missing | search, dashboards | A filter over `apps_current` of one tenant – up to millions of rows |
| Open alerts, not heard from, pending restarts | dashboards | `device_state` of one tenant |
| p95 CPU per device over 30 days, the worst 20; crashes per app over 7 days; drivers by version with crash counts | dashboards | Aggregations over tens to hundreds of millions of rows per tenant – the analytical queries |
| Ingestion | the front door | Hundreds of inserts a second at 200,000 devices (§22.3), append-only |
| Retention | background | Delete by age per tenant tier |

That is an analytical workload: append-only, time-ordered, aggregated by tenant, device and time, with a retention.

### 21.3 The candidates for the service

| | ClickHouse | PostgreSQL + TimescaleDB | SQLite / D1 (what the broker has) |
|---|---|---|---|
| Ingest of ~700 million performance rows a day plus events | Made for it: MergeTree, asynchronous inserts, compression per column | Hypertables cope to a few hundred million rows a day with care; compression helps | No – a D1 database holds 10 GB |
| Fleet aggregations over a quarter | Seconds over billions of rows; materialised views keep the dashboards incremental | Continuous aggregates do the same at smaller scale; tens of seconds beyond | No |
| Per-tenant retention | `TTL` with a column expression – one table, every tier | Retention policies per hypertable, not per row; a table per tier or a job | – |
| Multi-tenancy and isolation | Tenant id in the key, row policies, a cluster per region | Row-level security, schemas per tenant possible | – |
| Point lookups and updates (current state, acknowledgements) | Fine with `ReplacingMergeTree`; not a transactional store | Its home ground | – |
| Operations | ClickHouse Cloud, or three nodes per region self-managed | Managed PostgreSQL anywhere; Timescale Cloud | Already there |
| Cost at 200,000 devices (§22.4) | about $2,700 a month on ClickHouse Cloud | Comparable managed instances need more compute for the same aggregations | – |

**Decision: ClickHouse for sections, events, alerts and performance; the broker's D1 for metadata** (tenants,
entitlements, activations, keys, quota counters), where the transactional, small, point-lookup data already lives.
PostgreSQL would carry 10,000 devices well and 100,000 with effort; the owner's target of a couple of hundred thousand
devices in one environment, with dashboards that aggregate a quarter, is where a column store stops being optional.
Open: Cloud or self-managed (§19 Q1).

### 21.4 And the console?

The console stores no history (v1 Q9's answer ended that): a cache of the last overview and 24 hours for up to 50
devices in its encrypted SQLite, a few MB. v1's analysis of DuckDB and an embedded ClickHouse for the console is moot;
if a self-hosted service ever comes, it is the service's image that carries ClickHouse, not the console.

## 22. Data volumes and costs at 1, 4 and 8 hours

Prices are the published ones at the time of writing – Cloudflare Workers Paid $5 a month with 10 million requests and
30 million CPU-ms included, then $0.30 and $0.02 per million; D1 5 GB, 25 billion rows read and 50 million rows written
included, then $0.75 per GB-month, $0.001 and $1.00 per million; no charge for traffic; ClickHouse Cloud Scale tier
$0.2985 per compute unit-hour (8 GiB RAM, 2 vCPU) in AWS us-east-1, Enterprise $0.3903, more in European regions
(Germany Enterprise $0.52), storage $25.30 per TB-month compressed. Check them again before a budget.

### 22.1 Assumptions per device

| Item | Value | Why |
|---|---|---|
| Envelope and hash-only sections | 1.5 KB | §7 |
| 5-minute performance summaries | 1.7 KB per hour covered | 12 metrics × avg/max/min × 12 points, top processes, gzip |
| Section changes | 40 KB a day | about one `apps` change a day (Edge, Teams and Store apps update themselves); other sections rarely |
| Triggered bundles | 3 a day, 15 KB on average | app changes, crashes, updates, alerts; session events and definition updates don't trigger (§5.2) |
| Worker CPU per upload | 10 ms | signature check, decompression within limits, validation, diff, insert |
| Console queries | 0.1 per upload | a tab open for a tenth of devices; dashboards cached a minute |
| Column-store size | 40 % of gzip JSON for performance and events, 100 % for section blobs | typical ClickHouse ratios for typed numeric columns; measured in phase 1 |

### 22.2 Per device

| Interval | Full bundle | On the wire a day | Uploads a day | Uploads a month | Snapshot-only variant, a day |
|---|---|---|---|---|---|
| 1 h | 3.2 KB | 162 KB | 27 | 810 | 119 KB |
| 4 h | 8.4 KB | 135 KB | 9 | 270 | 88 KB |
| 8 h | 15.2 KB | 131 KB | 6 | 180 | 83 KB |

- **The interval saves requests, not bytes.** Three quarters of the bytes are section changes and triggered bundles,
  which happen when they happen; the performance summaries cover the same hours whichever bundle carries them. Going
  from 1 to 8 hours cuts uploads by 4.5× and bytes by 20 %.
- **The snapshot-only variant** – no continuous sampling, one reading per interval – saves about a quarter of the bytes
  and loses every threshold alert: *CPU at or above 80 % for 5 minutes* can't be seen in a reading every hour. Sampling
  stays on the device (it costs the device nothing measurable, §15.2); the interval only decides how often the
  summaries travel. Longer intervals make the timeline later: a 4-hour interval shows the morning's load at noon,
  unless an alert sent it earlier.
- **On the device's link:** 5 MB a month at 1 hour; a metered link needs no longer interval.

### 22.3 Per environment and month

Ingest is what arrives; the store is the 30-day tier plus 7 days of 1-minute data, at the column-store ratio of
§22.1; rows are `perf_5m` plus events. Worker requests are uploads plus console queries.

| Devices | Interval | Uploads a month | Ingest | Store (30 d) | Store (90 d) | Rows a day | Worker requests | Workers |
|---|---|---|---|---|---|---|---|---|
| 10,000 | 1 h | 8.1 M | 46 GB | 15 GB | 34 GB | 35 M | 8.9 M | $5.00 |
| 10,000 | 4 h | 2.7 M | 39 GB | 15 GB | 34 GB | 35 M | 3.0 M | $5.00 |
| 10,000 | 8 h | 1.8 M | 37 GB | 15 GB | 34 GB | 35 M | 2.0 M | $5.00 |
| 100,000 | 1 h | 81 M | 463 GB | 150 GB | 340 GB | 346 M | 89 M | $45 |
| 100,000 | 4 h | 27 M | 386 GB | 150 GB | 340 GB | 346 M | 30 M | $17 |
| 100,000 | 8 h | 18 M | 374 GB | 150 GB | 340 GB | 346 M | 20 M | $11 |
| 200,000 | 1 h | 162 M | 927 GB | 300 GB | 680 GB | 691 M | 178 M | $88 |
| 200,000 | 4 h | 54 M | 772 GB | 300 GB | 680 GB | 691 M | 59 M | $29 |
| 200,000 | 8 h | 36 M | 749 GB | 300 GB | 680 GB | 691 M | 39 M | $17 |

The store is the same whatever the interval – the same summaries, events and changes arrive – and D1's part
(metadata, a few hundred bytes per device) stays inside the plan at every size.

### 22.4 ClickHouse Cloud

Compute is what costs; storage is cents. The sizing below is a judgement from the ingest rate (at 200,000 devices and
1 hour about 1,900 uploads a minute, 700 million small rows a day) and the dashboards' aggregations, to be replaced by
a load test in phase 1:

| Devices | Service (Scale tier, 2 replicas) | Compute a month | Storage a month (30 d / 90 d) | Total, 1 h interval | Per device |
|---|---|---|---|---|---|
| 10,000 | 2 × 1 unit (8 GiB) | $436 | $0.40 / $0.90 | about $440 | $0.044 |
| 100,000 | 2 × 3 units (24 GiB) | $1,308 | $3.80 / $8.60 | about $1,360 | $0.014 |
| 200,000 | 2 × 6 units (48 GiB) | $2,615 | $7.60 / $17 | about $2,700 | $0.014 |

- A second region doubles the fixed part: two tenants' worth of devices in EU and US are two services. The smallest
  Scale service is about $440 a month per region whether it holds 1 or 10,000 devices, so the EU and US clusters are
  the floor of the service's cost: about **$900 a month** before the first device.
- The **interval moves the compute only a little** (ingestion is a minor part of the load next to the dashboards);
  it moves the Workers line by 3–5×, which is tens of dollars. The 8-hour interval is not a cost measure at any size.
- **Enterprise tier** (private networking, customer-managed keys, SSO) is 1.3× the compute; a European region 1.3×
  again. For the EU cluster budget about $570 a month for the smallest service on Enterprise in Germany.
- **Self-managed** (three nodes per region, 8 vCPU / 32 GiB each) is about $900–1,200 a month per region in compute
  on a major cloud at 200,000 devices, plus RemoteHub's operations – cheaper at the top, costlier at the bottom (§19 Q1).
- Traffic out of Cloudflare is free; traffic from the Workers into ClickHouse Cloud is counted by the cloud provider
  the cluster sits in (about 1 TB a month at 200,000 devices and 1 hour, at that provider's rates).

### 22.5 What follows

- At 10,000 devices the whole service costs about $440 a month per region, at 200,000 about $2,700 – **1.4 cents per
  device per month at the top**, and the ClickHouse floor of two regions dominates at the bottom.
- The retention tiers change storage, which is cents, not compute: the price difference between 30 and 90 days is
  not in the infrastructure. A tier is a licence decision.
- **Keep 60 minutes as the default**, because the cost case for a longer interval doesn't exist and the hourly bundle
  is what makes the timeline current without an alert. Offer 4 and 8 hours (and up to a week) for tenants that want
  less chatter.

The figures come from the assumptions of §22.1 and the sizing of §22.4; phase 1 replaces them with measured bundle
sizes from the lab (M-ACT-01 records the first full bundle's size per VM) and a load test of the service, and §22.3–22.4
are recomputed then.
