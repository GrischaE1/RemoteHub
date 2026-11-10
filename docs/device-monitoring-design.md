# RemoteHub – Device monitoring and inventory (DEX)

Status: Draft v6 (2026-11-09), a proposal – nothing is built yet. Written against main as of 0.22.x (agent 0.22, broker
protocol `remotehub-broker/1`). v2 takes in the answers to v1's open questions (§20): the history lives in a
**RemoteHub-hosted, multi-tenant monitoring service** sized for hundreds of thousands of devices, not in the console;
monitoring is **activated from the console** under the tenant's licence; devices behind **jump hosts** upload through
them; the console **queries** the service. v3 makes delivery **event-driven first**: changes travel when they happen, the full sync is a daily hash-only
reconciliation, and only the performance digest has a cadence (§6). v4 makes the bundle **Protobuf, gzip-compressed**
(§7) and spells out where every change notification comes from (§6.7). v5 sits on
[environment-enrollment-design.md](environment-enrollment-design.md): every device has an environment and knows its
monitoring front door from discovery; activation and settings reach it over its broker connection, or over a wake
channel through its jump host's relay where it has none (§6.5, §6.6) – the console never has to connect to a device for
monitoring. v6 closes the last open questions (§20) and adds how the store runs cheaply outside production (§21.5). §19 has what is still open, §21 the database
choice, §22 volumes and costs by interval.
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
| **Collection on the device** | The RemoteHub Agent collects an **inventory** (installed apps, Windows updates, drivers, hardware, operating system, users and groups, configuration – Group Policy and MDM/CSP, services, security state) once as a baseline, then keeps it current **by events**: every section has a watcher, and a change sends that section at once (§6.2). A **full sync** once a day (configurable, 12 hours to a week) reconciles: it carries only the sections' hashes, and the service asks for a section only when its hash differs (§6.1). It collects only once the console has **activated** monitoring on it (§6.5). |
| **Events** | The agent watches the device for things that matter and sends them when they happen: an app installed, updated or removed; a Windows update or a driver installed; a service, account, group or hardware change; a configuration change; an app crash or hang; a blue screen or an unexpected restart; a service that died; disk and hardware errors; Defender detections (§5.2). Everything is **device-initiated**: the service never calls a device, and the console calls one only to activate or to look now. |
| **Performance and thresholds** | The agent samples CPU, memory, disk and network every 10 s, keeps 1- and 5-minute summaries, and raises an **alert** when a value stays above a threshold for a time – by default CPU at or above 80 % for 5 minutes. Thresholds, windows and the metrics watched are set by the admin (§5.4). An alert sends the recent samples and the processes behind them at once; the summaries otherwise travel in a **performance digest** every 4 hours by default (1 to 24; §6.3). |
| **Delivery** | The agent **uploads** every bundle to the **RemoteHub monitoring service** – the broker's front door, with a column store behind it (§9, §10). A device with its own broker connection uploads directly; a device behind a jump host uploads through the jump host's agent, and so on along the chain, as long as one device in it reaches the broker (§9.3). The agent keeps an outbox for the days it can't (§8). The upload's answer carries what the service wants back – a section whose hash differs, settings the console left for the device – so no connection to the device is needed for either (§9.2). |
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
   collected before the tenant's admin activates it (§6.5, §14).
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
| The inventory of a device (apps, patch level, users, configuration) – a map for an attacker | Read by someone without the right; read in transit; read by another tenant; read at RemoteHub beyond what the service needs | Rights on the device (§9.1), TLS everywhere, the device's signature on every upload and the person's access token and role on every query, tenant id on every row and every query (§10.3), region per tenant, access at RemoteHub limited to operations (§14) |
| The service | A device floods it or sends bundles made to crash the parser; a tenant reads another's rows | Quotas per device and tenant, limits on every field at ingestion, tenant-scoped queries only, the device's identity on every upload (§9.2, §14) |
| The console | A device's data misleads the admin or exploits the console's rendering | Everything shown is text, clipped at the service and again in the console; nothing from a bundle is a link, a path or markup (§14) |
| The device | Monitoring slows it down, fills its disk, or opens a new way in | Budget and outbox limits (§8, §15.2); no new listener; the settings endpoint needs a right; the relay endpoint on a jump host forwards only to the service, bounded (§9.3) |
| People at the device | Surveillance beyond what IT needs | §1.1 principle 3, `MonitoringIncludeUsers`, `MonitoringSections`, activation visible on the device (Application log 1404), what is collected listed in `PRIVACY.md` (§18) |
| A jump host used as a relay | Used as an open proxy, or to read the bundles it forwards | It forwards only to the service's fixed path, rate-limited per source; bundles are signed by the device and TLS runs end to end from the device to the service (§9.3) |

## 4. Architecture

```
Device                                   Jump host (optional)            RemoteHub monitoring service                Console(s)
┌──────────────────────────────────┐    ┌──────────────────────────┐    ┌──────────────────────────────────────┐    ┌───────────────────────┐
│ RemoteHubAgent (LocalSystem)     │    │ RemoteHubAgent           │    │ Front door: a Worker of the environment│    │ MonitoringService     │
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
- **The front door is part of the environment.** The device learns its address by discovery when it enrols
  ([environment-enrollment-design.md](environment-enrollment-design.md) §5), next to its broker. The environment already
  knows tenants, devices (their registered keys), consoles, regions and quotas, and runs on Cloudflare Workers with D1.
  Monitoring adds an upload endpoint that verifies the device and writes to ClickHouse, a query API for consoles, and
  the activation and entitlement records in D1 (§10).
- **One way up, one identity.** Every enrolled device has a path to its environment – directly, or, for a device
  behind a jump host, which has no broker connection, through the jump host's environment relay
  ([environment-enrollment-design.md](environment-enrollment-design.md) §10), which forwards bytes to the environment's
  hosts and nothing else. The upload is signed by the device for the service either way (§9.2, §9.3).
- **The service computes the differences.** The device sends inventory sections whole (or their hash when unchanged);
  the service keeps the last snapshot of each section and turns the difference into change events (*Zoom 6.2.1 →
  6.3.0*, *driver igdkmd64 updated*, *GPO "Default Domain Policy" version 42 → 43*). The device does the same diff
  locally only to decide whether a change happened and to name it in the triggered bundle (§6.2), so both agree.
- **The console never needs the device for monitoring.** Activation and settings are written to the service; the
  broker connection – or, behind a jump host, the wake channel through the relay – tells the device something is
  waiting, and the device fetches it (§6.5, §6.6). The console connects
  to a device only for *Refresh now* and *Apply now*, over the pinned agent connection as every agent request.

## 5. What is collected

### 5.1 Inventory sections

Each section is collected whole, as one message of the schema (§7), and sent whole. The sources are listed so the collector can be
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
| `service.changed` | Changes | System: Service Control Manager 7040 (start type changed), 7045 (new service installed); a registry notification on `HKLM\SYSTEM\CurrentControlSet\Services`; then a diff of `services` | Service, what changed (installed, removed, start type, account kind) | `services` |
| `user.changed`, `group.changed` | Changes | Security 4720, 4722, 4725, 4726, 4738, 4740 (accounts) and 4728, 4732, 4733, 4735 (group members) where the audit policy records them; a registry notification on `HKLM\SAM` otherwise (the service runs as LocalSystem, which may read it – confirmed in the lab, M-EVT-09); then a diff of `users` | Account or group (with `MonitoringIncludeUsers`), what changed | `users` |
| `hardware.changed` | Changes | `RegisterDeviceNotification` from the service for device interface arrival and removal, Microsoft-Windows-Kernel-PnP 400/410/420 (device configured, started, removed); then a diff of `hardware` (a new disk, monitor, dock, adapter, memory after a service visit) | Device class and name, added or removed | `hardware` |
| `os.changed` | Changes | Registry notifications on the CBS `RebootPending`, Windows Update `RebootRequired` and `CurrentVersion` keys; then a diff of `os` | Pending restart, build, display version, activation state | `os` |
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
on the device, and **5-minute** summaries (average, maximum, minimum) that travel in the bundles. The
digest carries the 5-minute summaries since the last one (§6.3); an alert bundle carries the 1-minute summaries of the 15 minutes before and
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

## 6. Events first, then the digest, then the daily sync

Order of preference, as the owner set it: a change travels when it happens (device-initiated, §6.2); what has no
event – the performance curve – travels in a digest (§6.3); and a full sync once a day reconciles so nothing stays
wrong for long (§6.1). Nothing polls a device: the service never calls one, and the console calls one only to
activate it or to look at it now.

### 6.1 The full sync: a daily reconciliation

- `monitoring.fullSyncHours` (agent.json, set from the console) or the policy `MonitoringFullSyncHours`: **24 by
  default, 12 to 168**. A value below 12 is invalid and keeps 24.
- It carries **hashes only**: for every enabled section (`MonitoringSections`) the SHA-256 of its current content, plus
  the open alerts and whatever events weren't sent by a trigger. About 1.5 KB. The service compares the hashes with the
  copies it holds and names the sections that differ in the upload's answer (§9.2); the agent sends those whole in a
  partial bundle at once. A differing hash means an event was missed (the watcher wasn't running, a change the watchers
  don't cover) or the service lost a copy; the console sees the change as *between the two syncs*.
- The **first** sync after activation (§6.5) is the baseline and sends every section whole, 5 minutes after activation
  (or 15 minutes after boot, whichever is later). Everything after that is hashes unless asked.
- Each device adds a fixed **jitter** (0–60 minutes, derived from the device id) so a fleet activated at the same
  minute doesn't sync at the same second. A missed sync (the device was off) runs on the next start, after the same
  5 minutes.

### 6.2 Events: changes travel when they happen

- Every section has at least one watcher (§5.2): registry change notifications for `apps`, `config` and `os`; the
  Service Control Manager's events and a registry notification on `Services` for `services`; account and group events
  and a notification on `SAM` for `users`; PnP events and `RegisterDeviceNotification` for `hardware` and `drivers`;
  Windows Update's events for `updates`. A watched event makes the agent **re-collect that section after a 30 s
  debounce** (an installer writes several keys; a Windows Update installs several packages), diff it against the last
  copy, and – only if something changed – send a **partial bundle**: the event(s), the changed section whole, and
  nothing else.
- An event without a section (a crash, a blue screen, a failed service) is sent in a partial bundle with the last
  15 minutes of 1-minute summaries, so the timeline shows the load around it.
- An alert opening sends the alert, the samples of §5.3 and the current `processes`. An alert closing sends the closed
  alert.
- **Flood control:** at most **30 triggered bundles per hour** per device; beyond that, events are kept in the outbox
  and go with the next digest or sync, which says how many were held back. Repeated identical events (the same crash of
  the same program, the same disk error) within 10 minutes are counted into one event with a count. Low-value events –
  sessions, definition updates – never trigger; they ride with the next upload of any kind.
- Nothing is triggered during the first 5 minutes after boot; those events go with the next upload.

### 6.3 The performance digest

The curve has no event, so it has a cadence: `monitoring.digestMinutes` or the policy `MonitoringDigestMinutes`, **240
by default (4 hours), 60 to 1440**. A digest carries the 5-minute summaries since the last one (about 1.7 KB an hour
covered), the untriggered events and the open alerts. At 24 hours it merges with the full sync. The timeline's curve
lags by up to the digest interval; alerts don't, and *Refresh now* (§9.1) shows the device as it is. A tenant that
wants a current curve sets 60; §22 shows what each setting costs.

### 6.4 Sequence numbers

Every bundle gets the next number of a **per-device monotonic sequence** (persisted in the outbox before the bundle is
written, as `agent.json` is written: temp file, flush, replace). The service acknowledges by sequence number and
rejects a repeat, so nothing is stored twice and a gap is visible. A device enrolled again (new identity,
[agent-spec.md](agent-spec.md) §5.3) is a new device at the service; the console shows the old history under the
connection and marks the cut.

### 6.5 Activation

Nothing is collected until the tenant activates the device (v1 Q7: the agent takes the licence and the choice from the
console, not from its install). Every device already has its environment, its registered key and the monitoring front
door's address from enrolment ([environment-enrollment-design.md](environment-enrollment-design.md) §5), so activation
needs no connection from the console to the device:

1. **Entitlement.** RemoteHub sets on the tenant's record at the environment: monitoring licensed, the tier (30, 60 or
   90 days), the device cap, the region. The console reads it with the person's memberships and shows it under
   *Settings → Environment* and *Settings → Monitoring*.
2. **Activate.** The admin chooses *Monitoring → Activate…* on the tenant's folder, a folder or a device
   (§11.3). The console writes the activation – which devices, with which settings – to the service
   (`POST …/activations`, §9.4). Nothing is sent to a device.
3. **The device learns of it** over its broker connection: the broker sends a `wake` of kind `monitoring` to each
   device named, as it sends session offers today; a device that is offline gets it when it connects next. A device
   behind a jump host has no broker connection; it holds a **wake channel** through the jump host's relay to the
   front door instead ([environment-enrollment-design.md](environment-enrollment-design.md) §7.5), and where that is
   down **polls**: every `MonitoringPollMinutes` (15 by default, 5–1440) and with every upload it asks the front door
   through the relay whether anything waits for it. Either way
   the agent then fetches its activation and settings from the front door (`GET …/devices/{id}/activation`, signed
   with its device key, §9.2) – device-initiated, as everything else.
4. **Collect.** The agent writes the activation state to `agent.json` (`monitoring.activated`, the settings version),
   audits 1404 and sends the baseline after 5 minutes (§6.1).
5. **Deactivate.** The same path in reverse: the console writes it, the device is woken, fetches, stops collecting,
   deletes the outbox and the sections, audits 1405; the service marks the device inactive and its rows age out with
   the tier or are deleted at once with *Delete this device's data…*. The policy `MonitoringEnabled` = 0 makes the
   agent ignore an activation and deactivate an active device at the next policy read, reporting why in its next
   upload.

The service refuses uploads from a device whose tenant isn't licensed, is over its cap or whose activation was
revoked (403 with the reason); the agent backs off to the next digest, logs 3500 once per service run, and the Device
tab shows the reason. A device without an environment – a legacy, console-enrolled one – can't be activated; the
Device tab says *Join environment… first*.

### 6.6 Settings without a connection to the device

Settings are **device-initiated** too: the console writes a device's or a folder's settings to the service
(`PUT …/devices/{id}/settings`, §9.4); the broker – or the wake channel through the relay – wakes the device (§6.5 step 3) and it fetches them, or, offline,
it finds them at its next poll or in the answer to its next upload (§9.2). In practice seconds, at the latest the
next digest. The direct
`PUT /v1/monitoring/settings` (§9.1) stays for *Apply now* while the console is connected to the device, and the agent
reports which it has in `status`. Either way the device holds the settings and the policy wins (§12.2).

### 6.7 Where the change notifications come from

The watchers are the heart of event-driven delivery, so their sources are listed here, not left to the implementation.
A notification never produces a bundle by itself: it starts the section's 30 s debounce, the agent re-collects the
section and diffs it, and only a non-empty diff is sent (§6.2). A change without a notification is caught by the daily
sync's hash reconciliation (§6.1).

| Section | Primary source (starts the debounce) | Corroboration and source of the change | Deliberately not used |
|---|---|---|---|
| `apps` | `RegNotifyChangeKeyValue`, subtree, names and values, on `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, the same under `WOW6432Node`, and `…\Uninstall` in every loaded user hive under `HKEY_USERS`; plus the AppX repositories – `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore` and each hive's `Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages`. Every installer that registers an app writes there: MSI, Inno Setup, NSIS, Squirrel, Store and MSIX deployments | MsiInstaller 1033/1034/1035/1036, 11707/11724 (Application log) say *MSI* and name the product; AppXDeploymentServer/Operational 854 says *Store/MSIX* and names the package | WMI `Win32_Product` events – each query makes Windows Installer reconfigure every product; file system watchers on Program Files – noisy, and blind to per-user installs. Portable apps with no registry entry aren't inventoried at all (§5.1) |
| `updates` | Microsoft-Windows-WindowsUpdateClient 19, 20 (System log) and Setup log 1, 2 – `EventLogWatcher` with an XPath filter per id | The Windows Update Agent's history, re-read on the trigger, names KB and result; definition updates are recorded, not triggered | Polling `IUpdateSearcher` – the search is slow and the events are reliable |
| `drivers` | Microsoft-Windows-UserPnp 20001, 20003 and Kernel-PnP 400, 410, 411 | The diff of `Win32_PnPSignedDriver` names old and new version and INF | A notification on the driver store – too broad |
| `services` | `RegNotifyChangeKeyValue` on `HKLM\SYSTEM\CurrentControlSet\Services`, **key names only** (a service added or removed – values change constantly under `Parameters` and would fire all day); Service Control Manager 7040 (start type changed), 7045 (installed) | `EnumServicesStatusEx` and `QueryServiceConfig` re-read in about 10 ms | Watching service values |
| `users` | Security 4720, 4722, 4725, 4726, 4738, 4740 (accounts) and 4728, 4732, 4733, 4735 (group members) where the audit policy records them; otherwise `RegNotifyChangeKeyValue` on `HKLM\SAM\SAM\Domains\Account`, which LocalSystem may read (confirmed in the lab, M-EVT-09); the service control handler's `SERVICE_CONTROL_SESSIONCHANGE` for sign-ins, which also tells the apps watcher a user hive was loaded | `NetUserEnum`, `NetLocalGroupGetMembers` | Polling `NetUserEnum` |
| `hardware` | `RegisterDeviceNotification` with the service's status handle for `DEVICE_NOTIFY_ALL_INTERFACE_CLASSES` (`DBT_DEVICEARRIVAL`, `DBT_DEVICEREMOVECOMPLETE`); Kernel-PnP 400, 410, 420; `WM_POWERBROADCAST`-equivalent `SERVICE_CONTROL_POWEREVENT` for battery and dock changes | The diff of the hardware section | A WMI `__InstanceCreationEvent` on `Win32_PnPEntity` – polling in disguise |
| `config` | `RegNotifyChangeKeyValue`, subtree, on `HKLM\SOFTWARE\Policies`, `HKLM\SOFTWARE\Microsoft\PolicyManager\current`, and each loaded hive's `Software\Policies`; Microsoft-Windows-GroupPolicy/Operational 8004–8007 (processing done – `gpupdate` rewrites values without changing them, which is why the diff decides); the MDM diagnostics provider's sync-completed event (id confirmed in the lab); Windows Defender/Operational 5001/5010/5012, 2000/2001; Windows Firewall 2003–2005 | The diff of the policy and CSP values, the Defender and firewall state | Watching `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History` alone – it changes on every refresh |
| `os` | `RegNotifyChangeKeyValue` on the CBS `RebootPending` and Windows Update `RebootRequired` keys (names – the keys appear and disappear), `CurrentVersion` (values); Kernel-General 12 (start), User32 1074 (restart or shutdown asked) | The diff of the OS section | – |

**Mechanics.** One watcher thread holds every registry notification's event handle in a `WaitForMultipleObjects`
loop (the notifications are opened with `REG_NOTIFY_THREAD_AGNOSTIC`, so they outlive the thread that armed them, and
re-armed after each signal because they are one-shot). `EventLogWatcher` subscriptions call back on their own threads
and hand the event to the same debounce. A user hive that unloads signals once; the watcher drops it and re-opens on
the next session sign-in. Every watcher's failure (a key that can't be opened on an edition that lacks it, an event
log that doesn't exist on Server Core) is logged once and leaves the other watchers running; the daily sync covers
what a missing watcher would have caught.

## 7. Bundles

A bundle is a **Protobuf (proto3) message, gzip-compressed as a whole**, signed by the device for the service (§9.2).
The schema is one `.proto` file, the contract compiled into both ends the way `AgentProtocol.cs` and
`BrokerProtocol.cs` are today – except that here the other end is TypeScript, which is why it is Protobuf and not a
C# file: generated types on both sides, field numbers as the versioning rule, unknown fields ignored.

```protobuf
syntax = "proto3";
package remotehub.monitoring.v1;

message Bundle {
  string device_id = 1;            // lower-case GUID
  string tenant = 2;               // o_…
  uint64 seq = 3;                  // per-device monotonic (§6.4)
  Kind kind = 4;                   // SYNC, DIGEST, PARTIAL
  Detail detail = 5;               // BASELINE, SYNC, DIGEST, SECTION, EVENT, ALERT
  google.protobuf.Timestamp time = 6;      // the device's clock, UTC
  uint64 uptime_ms = 7;            // monotonic, for placing samples when the clock jumped (§5.3)
  string agent_version = 8;
  uint32 settings_version = 9;     // the settings the device has applied (§6.6)
  repeated Section sections = 10;
  repeated Event events = 11;
  repeated Alert alerts = 12;
  Performance performance = 13;
  uint32 held_back = 14;           // events kept for flood control (§6.2)
}

message Section {
  string name = 1;                 // os, hardware, apps, …
  bytes hash = 2;                  // SHA-256 of the canonical content (below)
  google.protobuf.Timestamp collected_at = 3;
  oneof content {                  // absent = hash only (unchanged)
    Os os = 10; Hardware hardware = 11; Apps apps = 12; Updates updates = 13; Drivers drivers = 14;
    Services services = 15; Users users = 16; Config config = 17; AgentInfo agent = 18;
  }
  repeated string unavailable = 4; // sources this edition lacks (§15.1)
}

message Apps { repeated App items = 1; }
message App { string key = 1; string name = 2; string version = 3; string publisher = 4; google.protobuf.Timestamp installed = 5;
              uint64 size = 6; Scope scope = 7; string user = 8; Kind kind = 9; bool system_component = 10; }
// … one message per section, mirroring §5.1

message Event { string id = 1; string kind = 2; google.protobuf.Timestamp time = 3; string lane = 4; uint32 severity = 5;
                string subject = 6; string title = 7; map<string,string> fields = 8; uint32 count = 9; int64 record_id = 10; string log = 11; }
message Alert { string id = 1; string rule = 2; State state = 3; google.protobuf.Timestamp opened = 4; google.protobuf.Timestamp closed = 5;
                float peak = 6; float average = 7; repeated TopProcess top = 8; }
message Performance {
  uint32 step_seconds = 1; google.protobuf.Timestamp from = 2;
  repeated Series series = 3;      // one per metric
  Samples samples = 4;             // the 10 s samples around an alert
}
message Series { string metric = 1; repeated float avg = 2 [packed = true]; repeated float max = 3 [packed = true]; repeated float min = 4 [packed = true]; }
```

- **Compression:** the serialised message is gzipped by the agent (`GZipStream`, level *Optimal*) and sent with
  `Content-Type: application/x-protobuf` and `Content-Encoding: gzip`; the Worker inflates with the built-in
  `DecompressionStream`. Protobuf makes the numeric part – the digest's series – compact on its own; the sections are
  strings (names, publishers, paths, policy values) and compress to a third or a quarter, so the whole message is
  compressed, not only the sections. The signature (§9.2) is over the bytes as sent.
- **Limits** (checked by the service before decoding further, and by the agent before writing): compressed ≤ 4 MB,
  uncompressed ≤ 32 MB (a streaming inflate that stops at the cap), nesting as the schema allows (no recursive
  messages), strings ≤ 1,024 characters (section-specific limits in §5.1), repeated fields as in §5.1, at most 500
  events and 50 alerts per bundle. Protobuf doesn't enforce lengths or counts, so these are an explicit validation
  pass on the Worker (`validate.ts`, generated from the same table as the agent's `BundleLimits.cs`), run on the
  decoded message before anything is stored. A bundle over a limit is refused whole and counted for the device; the
  agent never produces one (it clips while collecting), so a refused bundle means a changed device.
- **Evolution:** field numbers are never reused or renumbered; a removed field is `reserved`. Unknown fields are
  ignored (an older service reads a newer agent's bundle and stores what it knows), unknown `Section.name`s are
  stored as opaque bytes with their hash and shown as *unknown section (agent newer than the service)*. The package
  name carries the major version (`v1`); it changes only when a message changes incompatibly, which the field rules
  make unnecessary in practice.
- **Hashes:** `Section.hash` is SHA-256 over the section message serialised in **canonical form** – fields in number
  order, no unknown fields, default values omitted, repeated items in the order the collector sorts them (by `key`) –
  so the device and the service compute the same hash for the same content and the daily sync is nothing but these
  hashes (§6.1). Both implementations are checked against shared test vectors.
- **The file lives in** `src/RemoteHub.Agent/Monitoring/monitoring.proto`, compiled for C# with `Grpc.Tools`
  (`protoc` at build, no runtime cost) for the agent and the console, and copied into the broker's repository where
  `protobuf-es` generates the TypeScript. A test on each side keeps the two copies identical (a checksum, as
  `BrokerProtocolTests` already does for the broker's test vectors). `Google.Protobuf` (BSD-3-Clause) and `Grpc.Tools`
  (Apache-2.0, build only) get their entries in `THIRD-PARTY-NOTICES.md`.
- **Reading one by hand:** the canonical proto3 JSON mapping (`JsonFormatter` in C#, `toJson` in `protobuf-es`) turns
  a bundle into readable JSON for a lab's evidence, a support case or `RemoteHubAgent.exe monitoring show <seq>`; the
  no-secrets rule applies to that output as to the bundle.
- **The same messages everywhere:** `now` (§9.1) returns a `Bundle` without a sequence number; the service's query API
  (§9.4) answers the console with the same `Section`, `Event`, `Alert` and `Performance` messages (gzip, or JSON through
  the canonical mapping with `Accept: application/json` for debugging), so there is one contract from the device to
  the screen.

## 8. The device outbox

`%ProgramData%\RemoteHub\Agent\monitoring\` – inside the agent's folder, so it has the folder's ACL (SYSTEM and
Administrators only, [agent-spec.md](agent-spec.md) §5.3). No database: the agent has no SQLite dependency, and a
LocalSystem service shouldn't gain a native one for this. Files, written with `DurableFile`:

| File | Contents |
|---|---|
| `state.json` | Activation state and settings version (the front door's address comes from `agent.json`'s `environment`), next `seq`, event-log bookmarks, last hash per section, the thresholds' state, last upload and acknowledged `seq`, settings in force |
| `outbox/<seq>.rhmon` | One bundle each (gzip Protobuf, §7) until the service acknowledges it. Kept at most **7 days or 64 MB**, oldest first – a device without a path for a week keeps its newest full bundle and the console sees a gap, not an outage |
| `sections/<name>.json` | The last copy of each section, for the diffs of §6.2 |
| `perf/<yyyyMMdd>.bin` | The day's 1-minute summaries, a fixed binary record per minute (about 200 bytes), 48 hours kept |

- Uninstall (`RemoteHubAgent.exe cleanup`) and deactivation (§6.5) remove the folder.
- A damaged `state.json` (as `agent.json` in [agent-spec.md](agent-spec.md) §7.2) keeps the activation from
  `agent.json` and restarts the sequence at the service's acknowledged number plus one. Nothing here can stop the
  service from starting – a monitoring error is logged (3500) and monitoring retries at the next interval.

## 9. Wire

### 9.1 The agent's endpoints (console → device)

Same listener, same TLS pin, same HTTP Basic Windows sign-in and `SignInGate` as `/v1/powershell`
([agent-spec.md](agent-spec.md) §4, §6). New rights in `Rights`: **`Monitoring`** (read) and **`MonitoringSettings`**
(*Apply now* for thresholds and intervals), both defaulting to `allowedGroups` like `PowerShell`. Activation itself
needs no right on the device: it is the tenant's decision at the service (§6.5).

| Method | Path | Right | Purpose |
|---|---|---|---|
| GET | `/v1/monitoring/status` | Monitoring | `{enabled, activated, tenant, path (direct / through the relay of <upstream>), lastSeq, acknowledgedSeq, lastUpload, lastFullSync, nextFullSync, nextDigest, fullSyncHours, digestMinutes, settings, settingsSource (policy/console/service/default), settingsVersion, openAlerts, outboxBytes, lastRefusal, time}` – `time` is the device's clock, so the console can note a skew |
| GET | `/v1/monitoring/now?sections=apps,drivers` | Monitoring | Collects the named sections now and the current 10 s samples and returns them to the console without writing a bundle (*Refresh now* in the Device tab, at most once a minute per account); the console shows them and the service isn't involved |
| PUT / GET | `/v1/monitoring/settings` | MonitoringSettings / Monitoring | The device settings of §12.2 as JSON. Values a policy configures are refused with 409 and the policy's name; invalid values 400 with the reason. Written to `agent.json`, audited (1402) |

- Health (`/v1/health`) lists the feature **`monitoring`** when `MonitoringEnabled` is on, so a console knows before
  signing in; an older agent lists nothing and the Device tab says the agent needs updating. It never says whether the
  device is activated – that needs the right.
- Status codes as in [agent-spec.md](agent-spec.md) §6: 401 and 403 from the sign-in, 404 when `MonitoringEnabled` = 0,
  429 from the gate.
- Through a jump host and through the broker these requests travel as every agent request does.

### 9.2 Device → service: the upload

- `POST https://<monitoring front door>/v1/o/{org}/monitoring/upload` – the host discovery gave the device
  ([environment-enrollment-design.md](environment-enrollment-design.md) §5.2) – with the gzip-compressed Protobuf bundle as the body
  (`Content-Type: application/x-protobuf`, `Content-Encoding: gzip`) and the device's signature over
  `remotehub-broker/1|<org>|monitoring|<deviceId>|<seq>|SHA-256(body as sent)` in a header (PS256 with the device
  certificate's key, §6.5 step 3 – the signature the broker's registration already uses, so the Workers verify it
  with the stored public key). TLS to the front door as the broker connection uses it (`BrokerHttpProxy` applies); through a jump host, inside
  the environment relay (§9.3).
- The answer, an `UploadResult` message of the same schema (JSON shown for readability): `{acknowledged: <seq>,
  sections: ["apps", …], settings?: {…}, settingsVersion?: 12, deactivated?: true, reason?: "…"}` – `sections` names sections whose hash differs from the service's copy or that it lacks, which the
  agent sends whole in a partial bundle at once (§6.1); `settings` are the settings waiting at the service for this
  device – the console's per-device or per-folder values, else the tenant's defaults (§6.6) – with a version the agent
  echoes in its next upload so the console sees them applied; `deactivated` ends collection (§6.5). The answer is the
  service's only way to the device, and the device asks for it only by uploading.
- Retry: an upload that fails (network, 5xx, 429) stays in the outbox and is retried with backoff (1, 2, 4 … up to
  60 minutes), oldest first, at most 5 in flight; a 403 (not licensed, over cap, revoked) stops uploads until the next
  digest and is shown in status.
- Quota at the service: 60 uploads per device per hour, 4 MB each; per tenant, the entitlement's device cap and
  200 MB an hour; beyond that 429. The console never uploads on a device's behalf.

### 9.3 Through a jump host: the environment relay

A device behind a jump host has no broker connection. It reaches the environment through its jump host's agent for
its enrolment, its monitoring uploads and its wake channel for activation and settings – the environment relay of
[environment-enrollment-design.md](environment-enrollment-design.md) §10; consoles reach the device itself through the
jump host's tunnel as today. The jump host forwards bytes to the
environment's discovered hosts only, TLS runs from the device to the front door inside the forward, and the upload is
signed by the device, so the relay sees sizes and hosts and can change nothing. Monitoring adds no rule of its own to
it; the limits, the policy `EnvironmentRelay` and the audit (1203, 2202) are the relay's. A chain whose head loses its
path fills outboxes down the chain until it is back; nothing is lost for 7 days (§8).

### 9.4 Console → service: queries, activation and settings

The console talks to the service as the **signed-in person**: the environment's access token plus the console key's
signature over each request ([environment-enrollment-design.md](environment-enrollment-design.md) §8), so every query
is tenant-scoped by the person's membership and role – *Viewer* and above may read, *Admin* and above activate and
change settings:

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/o/{org}/monitoring/entitlement` | Licensed, tier, cap, region, devices active |
| POST / DELETE | `/v1/o/{org}/monitoring/activations` | Activate or deactivate devices or a folder, with settings; the service wakes each device over its broker connection (§6.5) |
| GET | `…/devices/{id}/activation` | **Device-signed**, not console: the device fetches its activation state and settings after a wake or at any upload |
| GET | `/v1/o/{org}/monitoring/devices/{id}/overview` | The tiles of the Device tab: current health, open alerts, last received, last 2 hours of 5-minute summaries |
| GET | `…/devices/{id}/sections/{name}[?at=<time>]` | The current (or a past) copy of a section, and `…/changes?from&to` the diff events |
| GET | `…/devices/{id}/events?from&to&lanes` · `…/alerts?open` · `…/performance?from&to&step&metrics` | The timeline's data, at the step the zoom needs |
| POST | `…/devices/{id}/alerts/{alertId}/acknowledge` · `DELETE …/devices/{id}` | Acknowledge; delete a device's data |
| GET | `/v1/o/{org}/monitoring/dashboards/{name}?folder&from&to` · `…/reports/{name}` | The dashboards and reports of §11.4, computed at the service, paged |
| GET | `/v1/o/{org}/monitoring/search?q=` | Devices by app, version, KB, driver (§11.4) |
| PUT | `/v1/o/{org}/monitoring/defaults` · `…/devices/{id}/settings` · `…/folders/{id}/settings` | The tenant's defaults; settings waiting for a device or every device of a folder, handed over in the next upload's answer (§6.6); a folder's apply to new devices too |

Answers are the schema's messages (§7), gzip-compressed, with the same limits as bundles; tabular dashboard answers
are a `Table` message (columns, typed cells, a page token). The console clips again when it shows them (§14). Nothing is
queried in the background except the overview of devices with an open Device tab and, with *Show alerts in the
navigation*, one call per minute for the tenant's open alerts.

## 10. The service

### 10.1 Shape

| Part | What | Where |
|---|---|---|
| **Front door** | A **Worker of its own** in the broker's repository, next to the session broker's, bound to the same D1 and to KV through service bindings and deployed on its own: ingestion takes hundreds of uploads a second and runs a validator over hostile input, and a bug or a load spike there must not take down session brokering. It verifies device and console signatures, enforces entitlement and quotas, validates bundles against §7, writes to the store, answers queries, runs activation | Cloudflare, every region – serverless, no container |
| **Metadata** | Tenants, entitlements (tier, cap, region), activations, device → tenant and key, members and consoles, quotas' counters | The environment's D1 (today's broker database) |
| **Store** | Sections (current and history), events, alerts, performance; materialised views for the dashboards | **ClickHouse** (open source) – one cluster per region (EU, US) in production, shared by the region's tenants, tenant id on every row; a single self-hosted node for staging and the lab (§21, §21.5) |
| **Jobs** | Diffs of sections into change events at ingestion; retention by TTL; nightly materialised-view refreshes where a view isn't incremental | ClickHouse and a Workers cron |

Nothing in the service is a container in v1: the Workers are serverless and ClickHouse Cloud is managed. A container
appears only with self-hosted ClickHouse (§21.5), a customer-hosted service (§2, later), or an ingestion buffer
beyond what Cloudflare Queues gives (§20).

Ingestion path: the Worker verifies the signature and quota, inflates within the limits, decodes the Protobuf message
with `protobuf-es`, validates (§7), computes the section diffs against the device's current rows, and inserts rows into ClickHouse over its HTTP interface with
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
sections    (tenant, device, name, collected DateTime, hash FixedString(32), data String /* the gzip Protobuf section, §7 */, retention_days)
  ENGINE MergeTree ORDER BY (tenant, device, name, collected) TTL collected + toIntervalDay(retention_days)
sections_current  (tenant, device, name, hash, collected, data) ENGINE ReplacingMergeTree(collected) ORDER BY (tenant, device, name)
apps_current (tenant, device, key, name, version, publisher, scope …) ENGINE ReplacingMergeTree ORDER BY (tenant, device, key)
device_state (tenant, device, last_seen, last_full, interval, path, agent_version, pending_restart, open_alerts, …)
  ENGINE ReplacingMergeTree(last_seen) ORDER BY (tenant, device)
-- materialised views: perf_days (per device, metric, day: avg, max, p95, minutes above threshold),
-- crashes_by_app_7d, pending_restarts, patch_level (newest cumulative update per build), disk_space, not_heard_from
```

- **Retention per tenant** is a column, so one table serves every tier: `TTL time + toIntervalDay(retention_days)`.
  A tier change rewrites the column for the tenant's rows (`ALTER … UPDATE`, background); a downgrade deletes the older
  rows at the next TTL merge, within a day, and the tier dialog says so. The 1-minute and 10 s data
  keep 7 days on every tier.
- **Isolation:** every query the Worker issues starts with `WHERE tenant = {tenant}` from the verified access token's
  membership;
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

### 10.4 Throughput

Where thousands of uploads a second would bite, in the order they cost (§22.3 has the rates):

| Step per upload | Cost | At 200,000 devices and the defaults | What keeps it in hand |
|---|---|---|---|
| Signature check (RSA-3072 PSS, WebCrypto) | about 0.3 ms CPU | 55 a second at the digest peak | Spread across the edge by construction; nothing to do |
| Device key, tenant and entitlement lookup | a D1 read, milliseconds, one database | the first real queue at a few hundred a second | Cached in Workers KV or the Cache API with a 5-minute TTL; quota counters in a Durable Object per tenant |
| Inflate and decode (15 KB) | about 0.1 ms CPU | negligible | Streaming inflate under the §7 caps; Protobuf decoding is a few times faster than JSON parsing |
| Validation and clipping | about 0.2 ms CPU | negligible | – |
| Diff of a changed section | 2–5 ms, a read of the stored copy | about 14 a second on Patch Tuesday | Current sections in `sections_current`, one read per changed section |
| ClickHouse insert | I/O | hundreds of small inserts a second | Asynchronous inserts batch on the server; a Cloudflare Queue in front if the measured p99 passes 2 s (§20) |

Decompression is last on the list: zlib inflates hundreds of MB a second on one core and the aligned-fleet peak is
under a megabyte a second. Nothing here is a single server – each upload is a Worker invocation on the edge node the
device reached.

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
  activate and which have no path (§2), an older agent or a device policy that forbids it (known from its last
  upload or `status`), and writes the activation to the service in one call (§6.5); the dialog then shows each device
  confirm as it is woken or polls (*activated 09:14*, *waiting – offline*, *refused by policy*). *Deactivate…* is the
  same dialog with the data question (*Keep the history until it ages out* / *Delete this device's data now*). Needs
  the role *Admin* or *Owner* in the tenant ([environment-enrollment-design.md](environment-enrollment-design.md) §8.2)
  and `AllowMonitoring` on this PC; no right on the device.
- **Per device:** the Device tab's *Settings* section shows the activation state and path, the full sync and digest
  intervals, the sections collected, and every rule of §5.4 as a row with on/off, threshold and window (`NumberBox`es with the ranges of §5.4),
  with the value's source (*device*, *tenant default*, *policy*). *Apply* writes them to the service for the device's next upload, *Apply now* also sends
  `PUT /v1/monitoring/settings` over the connection (§6.6); a value the policy configures is greyed with the lock and *Set by your organisation*, as everywhere
  ([admx-design.md](admx-design.md) P2). Without the right the section is read-only and says so.
- **For many devices:** right-click a folder → *Monitoring settings…* opens the same form with *Apply to every device in
  this folder* (and its subfolders): written to the service once, picked up by each device at its next upload, with
  the dialog showing which have confirmed (`settingsVersion`). The device holds its settings, so two consoles can't
  disagree about a device; the tenant's defaults apply only where no console set a value.
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
| `AllowMonitoringActivation` | DWORD | 0: this PC may look but not activate, deactivate or change settings, whatever the person's role (1) |
| `AllowMonitoringExport` | DWORD | 0: no report export (1) |

### 12.2 Agent (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`, ADMX category *RemoteHub Agent → Monitoring*)

The same names, without the prefix, are the `monitoring` object in `agent.json` (written by the console's activation
and `PUT /v1/monitoring/settings`); the policy wins and is shown locked (P1). Read when used, as the other agent
policies.

| Value | Type | Meaning (default) |
|---|---|---|
| `MonitoringEnabled` | DWORD | 0: the device can't be activated and an active device is deactivated at the next read; `/v1/monitoring/*` answers 404, health leaves out `monitoring`, the outbox is deleted (1 – *may be activated*; nothing is collected until the console activates) |
| `MonitoringFullSyncHours` | DWORD 12–168 | The daily reconciliation's interval (24; §6.1) |
| `MonitoringDigestMinutes` | DWORD 60–1440 | The performance digest's interval (240; §6.3) |
| `MonitoringPollMinutes` | DWORD 5–1440 | How often a device behind a jump host asks the service for an activation or settings through the relay while its wake channel is down (15; §6.5) |
| `MonitoringSections` | MULTI_SZ | Which sections are collected: any of `os`, `hardware`, `apps`, `updates`, `drivers`, `services`, `users`, `config`, `agent` (all) |
| `MonitoringIncludeUsers` | DWORD | 0: no account names anywhere – sessions, local users, group members, crash and sign-in events say *a user*; per-user app entries and the user's GPOs and CSP values are left out (1) |
| `MonitoringEvents` | DWORD | 0: no event watching – changes are found only by the daily sync's reconciliation (1) |
| `MonitoringPerformance` | DWORD | 0: no sampling, no threshold alerts (1) |
| `MonitoringCpuPercent`, `MonitoringCpuMinutes` | DWORD | 0 turns the rule off; else 50–100 and 1–60 (80, 5) |
| `MonitoringMemoryPercent`, `MonitoringMemoryMinutes` | DWORD | (90, 5) |
| `MonitoringDiskPercent`, `MonitoringDiskMinutes` | DWORD | Disk active time (90, 5) |
| `MonitoringDiskFreePercent`, `MonitoringDiskFreeGB` | DWORD | Below either opens the alert; 0 turns that half off (10, 5) |
| `MonitoringNetworkErrorsPerMinute`, `MonitoringNetworkMinutes` | DWORD | (100, 5) |
| `MonitoringBootSeconds`, `MonitoringSignInSeconds` | DWORD | 0 off (120, 60) |
| `MonitoringCrashCount`, `MonitoringCrashHours` | DWORD | (3, 24) |
| `MonitoringServiceFailures` | DWORD | 0: no alert for a failed service, the event is still recorded (1) |

The service address comes from the environment's discovery and the only allowed environment from the policy
`Environment` ([environment-enrollment-design.md](environment-enrollment-design.md) §9); the relay is governed by
`EnvironmentRelay` there. Rights (subkey `Rights`): `Monitoring`, `MonitoringSettings` (§9.1). `RemoteHubAgent.exe policy` prints them all;
*Settings → Policies* lists them from `PolicyCatalog.cs`; the ADMX test checks the template against it.

No install-time switch and no enrolment-file field: activation comes from the console (§6.5). The MSI and the
`.rhagent` file stay as they are.

## 13. Audit and logging

Agent (`AuditEvent`, the Application log unless `AuditToEventLog` = 0):

| Id | Event | Recorded |
|---|---|---|
| 1400 `MonitoringStarted` | Collection runs (once per service run while activated) | Full sync and digest intervals, sections, path (direct or upstream) |
| 1401 `MonitoringRead` | A console read status, settings or `now` | Account, source address, what – never the content |
| 1402 `MonitoringSettingsChanged` | Settings changed through the endpoint | Account, source, which values and the new values (no secrets exist here) |
| 1403 `MonitoringUploaded` | The first successful upload per service run, and again after a failure | Service, `seq`, path |
| 1404 `MonitoringActivated` / 1405 `MonitoringDeactivated` | Activation changed (fetched from the service after a wake) | Tenant id, settings version; on deactivation whether the data was asked to be deleted. The relay's forwards are audited by the relay (1203, 2202) |
| 2500 `MonitoringAlert` | An alert opened or closed | Rule, value, duration, top programs – also for Intune or a SIEM |
| 2501 `MonitoringRefused` | A read or *Apply now* refused (right, policy), or an activation ignored (policy) | Account, source, reason |
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
  with their registered key and carry the person's access token; every query is tenant-scoped by the membership in
  that token, never by a parameter, and the audit names the person.
- **Tenant isolation at the store:** tenant id on every row, in every `ORDER BY`, in every `WHERE` the Worker issues
  (tested), row policies as the second fence, a region per tenant with no cross-region queries.
- **The service treats bundles as hostile** (TB8): the gzip is read through a size-limited stream (compressed and
  uncompressed limits of §7 enforced while reading, not after), the Protobuf decoded by the generated code (no
  recursion in the schema, unknown fields ignored) and validated against the limits table before any use, every
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
  access token and console key from the environment's sign-in; activation is a record at the service the device
  fetches with its own key. No new secret is stored anywhere.
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
  of a tenant's end, backups' retention, and a security review of the Workers' code paths.

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
| Baseline or a re-collected section | < 30 s CPU, < 100 MB peak, finished within 5 minutes | Sections collected one after another on a thread at `BelowNormal` priority, each with its own stop (WMI 30 s, WU search 60 s); a section that overruns is skipped this time and reported in `agent` |
| Event watching | < 0.1 % CPU | `EventLogWatcher` subscriptions with XPath filters per id, no polling |
| Outbox | ≤ 64 MB, a few hundred KB in practice | §8 |
| Uploads | about 100 KB a day, 10 uploads a day at the defaults | Events only when something changed, a hash-only daily sync, a 4-hour digest; gzip (§22) |
| Relay on a jump host | ≤ 600 forwards an hour, bytes in and out | §9.3 |

The lab measures these (M-PERF-05, M-PERF-06) on the slowest VM (W10-1809) and the result goes into this section.

## 16. Tests

**Unit tests** (`tests/RemoteHub.Tests`, one class per area with a `/// <summary>`):

- `ThresholdStateTests` – a fake clock: opens at 90 % of samples above, not at 4 of 5 minutes, hysteresis, cool-down,
  the boot grace period, a `time.changed` jump doesn't open or close anything.
- `SectionDiffTests` – added, removed, changed apps; the same app in two scopes; a version that goes backwards; a
  driver's INF rename; 10,000 items in under a second (shared with the service's implementation as test vectors).
- `MonitoringBundleTests` – Protobuf round trip; every limit of §7 refused (a 4 MB + 1 byte bundle, a 1,025-character
  name clipped, 501 events refused, an unknown field ignored, an unknown section stored opaque); a gzip bomb (1 GB of
  zeros) stopped at 32 MB; control and bidirectional characters stripped; the signature over the body verified and a
  changed byte refused; the canonical hash equal for the same content in a different item order; the `.proto`'s
  checksum equal to the broker repository's copy.
- `MonitoringActivationTests` – a wake leads to one fetch; an activation fetched for another device is refused; the
  policy makes the agent ignore it and say why; deactivation deletes the outbox and sections; `MonitoringEnabled` = 0
  deactivates; an upload answer carries a pending activation for a device that missed the wake.
- `MonitoringServiceClientTests` – the console's queries are signed and tenant-scoped; the cache is used when offline
  and marked; nothing is queried in the background without an open tab or the navigation setting; answers are clipped.
- `EventKindTests` – each Windows event of §5.2 (recorded XML from the lab) maps to its kind and fields; an event with
  a user name maps to *a user* with `MonitoringIncludeUsers` off.
- `ChangeWatcherTests` – on the CI runner: a value written under a temporary Uninstall key, a service created and
  deleted, a key under `SOFTWARE\Policies` → the right section's debounce fires once for a burst of writes, the
  notification re-arms, a key that can't be opened leaves the other watchers running; a `.reg` import of 1,000 values
  fires one re-collection.
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
| M-ACT-01 | Activation | the harness activates the VM at the lab service → the broker wakes the agent, `status` shows activated within a minute, 1404 audited; within 10 minutes the service has the baseline with every section (its size recorded); health lists `monitoring` | H | 1 |
| M-ACT-02 | Not before activation | a fresh install collects nothing for 30 minutes: no outbox, no 1400, no upload at the lab service | H PS | 1 |
| M-ACT-03 | Deactivation and policy | deactivated at the service → the device is woken, collection stops, outbox gone, 1405; a device offline during activation fetches it at its next broker connection; `MonitoringEnabled` = 0 on an active device → deactivated at the next read, 404, health without `monitoring` | H PS | 1 |
| M-ACT-04 | Rights and identity | an account without `MonitoringSettings` can't *Apply now* (403, 2501) but reads `status`; a device fetching another device's activation with its own key → 404; a legacy device without an environment can't be activated and the Device tab says so | H | 1 |
| M-ACT-05 | Licence at the service | the lab tenant set over its cap or unlicensed → uploads 403, 2503 audited, `status.lastRefusal` names it, the Device tab shows it; licensed again → uploads resume with the next digest | H | 1 |
| M-INV-01 | Sections' truth | the apps the VM has (the agent MSI, a test MSI), its KBs, its drivers, local users, the lab's GPO and the Defender state appear with the right values at the service; nothing of §5.5 in the bundle (scan) | H PS | 1 |
| M-INV-02 | Sync and digest intervals | with `MonitoringFullSyncHours` = 12 and `MonitoringDigestMinutes` = 60 a hash-only sync arrives 12 h ± 1 h after the baseline and a digest every 60 ± 5 minutes; 6 and 30 are refused (invalid, logged once) and the defaults stay | H PS | 2 |
| M-INV-03 | Users off | `MonitoringIncludeUsers` = 0 → no account name anywhere in a full bundle (scan for the VM's known accounts), counts still there | H | 1 |
| M-INV-04 | Reconciliation | the daily sync carries hashes only (under 4 KB); the service asked for none; a section deleted at the service, and a change made with `MonitoringEvents` = 0, are asked for and arrive whole within a minute of the sync | H | 1 |
| M-INV-05 | Settings from the service | the harness writes settings for the device at the lab service → the device applies them with its next upload, echoes `settingsVersion`, `status` says source `console`; a folder's settings reach two devices | H | 1 |
| M-EVT-01 | App installed | install a test MSI → a partial bundle with `app.installed` and the `apps` section at the service within 90 s; uninstall → `app.removed` | H PS | 1 |
| M-EVT-02 | Store app and EXE installer | `Add-AppxPackage` and an Inno Setup test installer → events within 90 s, kind says Store / installer | H S | 2 |
| M-EVT-03 | Driver and update | install a test driver (the lab's signed null driver) → `driver.installed`; install a `.msu` → `update.installed` and `os` pending restart; a definition update → recorded, no triggered bundle | H PS | 2 |
| M-EVT-04 | Crash | a test program that faults → `app.crash` with module and code within 60 s, 3 faults → the crash alert; `app.hang` from a program that stops pumping messages | H S | 1 |
| M-EVT-05 | Blue screen | a forced bug check in the guest → after the restart `os.bluescreen` with the code, `device.started` with the boot duration | H PS C | 1 |
| M-EVT-06 | Service failure | kill a test service's process → `service.failed` and the alert; the agent's own service isn't listed as failed after a lab-initiated restart | H PS | 2 |
| M-EVT-07 | Group Policy | change the lab GPO and `gpupdate` → `config.gpo` with the changed values named within 2 minutes; `gpupdate` without a change → no bundle | H PS | 2 |
| M-EVT-09 | Services, accounts, hardware | a new service, a start type change, a new local user and a group membership, a plugged-in USB disk → each a partial bundle with the changed section within 90 s; the SAM notification path confirmed where the audit policy is off | H PS | 1 |
| M-EVT-08 | Flood | 100 crashes in a minute → identical events counted, at most 30 triggered bundles an hour, the rest in the next full bundle with `heldBack`; 20 locks and unlocks → no triggered bundle | H S | 2 |
| M-PERF-01 | CPU alert | a burner at 100 % on all cores for 6 minutes → the alert opens between 5:00 and 5:30 with the burner on top, the alert bundle holds the 10 s samples; the burner stops → closes within 6 minutes with duration and peak | H S | 1 |
| M-PERF-02 | No false alert | 4 minutes at 100 %, then idle → no alert; 10 minutes alternating 30 s at 100 % and 30 s idle → no alert | H S | 1 |
| M-PERF-03 | Thresholds from the console | set CPU 50 % / 2 min through PUT → status shows `device` as source and the alert opens after 2 minutes of a burner; set `MonitoringCpuPercent` by policy → PUT answers 409, status says `policy`; the tenant default applies where neither is set | H PS | 1 |
| M-PERF-04 | Memory and disk | fill memory to 95 % / hammer the disk for 6 minutes → the alerts; free space under 5 GB → the free-space alert once, not every minute | H S | 2 |
| M-PERF-05 | Budget | over 2 hours on W10-1809 the agent's CPU time grows by less than 60 s and its working set stays under 120 MB; the inventory's log line says under 60 s | PS | 1 |
| M-PERF-06 | Boot and sign-in | restart; sign in as normaluser → `device.started` with a boot duration, `session.signIn` with a duration (confirms the measurement) | H C S | 2 |
| M-RELAY-01 | Through a jump host | **2VM**, the target enrolled through the jump host's environment relay (E-RELAY-01), no broker connection: activation reaches it over the wake channel within a minute, and at its next poll (`MonitoringPollMinutes` = 5) with the channel cut; uploads arrive at the service signed by the target; the jump host audits 1203; `status` says *through the relay of JUMP01*; the jump host's log has no section contents | H | 1 |
| M-RELAY-02 | Relay off | `EnvironmentRelay` = 0 on the jump host → the target's outbox fills and its polls fail quietly, then it drains and a waiting activation arrives when it is 1 again; a bundle over 4 MB is refused by the service, not the relay | H | 1 |
| M-RELAY-03 | Chain and loss | jump host behind a jump host: two hops, uploads arrive; the head's broker connection cut for 20 minutes → outboxes fill, nothing lost, all arrive within a digest interval after it is back | H N | 2 |
| M-CON-01 | Device tab | **2VM**, RemoteHub in the client VM: the tab shows the inventory, the events of M-EVT and the alert of M-PERF-01 on the timeline from the lab service; offline → the cache is shown and marked; screenshots in both themes | U | 1 |
| M-CON-02 | Diff events | install a test app with the watcher off (`MonitoringEvents` = 0) → after the next sync's reconciliation the tab shows *between HH:MM and HH:MM* for the change | U H | 2 |
| M-CON-03 | Dashboards and search | the lab's devices appear in *Open alerts*, *Pending restarts*, *Crashing apps*; `monitoring:` search finds the test app's version; a CSV export has the rows and is audited | U | 2 |
| SEC-MON-01 | Hostile bundle at the service | uploads from a test client with the VM's key: over the limits (size, depth, a gzip bomb, strings with control and RTL characters, a `file://` and an `http://` string, a 100,000-item apps list) → each refused or clipped at the service, counted; the console shows text only, nothing is opened or fetched | H U | 1 |
| SEC-MON-02 | Wrong identity | a bundle signed by device A naming device B, a repeated `seq`, a bundle after deactivation, a token issued for another tenant → refused with the right status, nothing stored under B | H | 1 |
| SEC-MON-03 | Tenant isolation | the lab makes two tenants: console A's queries for B's device id → 404; A's dashboards never list B's devices; a relay on A's jump host forwards a B device's upload and the service files it under B (identity is the signature, not the path) | H | 1 |
| SEC-MON-04 | The relay is not a proxy | a request to `/v1/monitoring/forward` whose body isn't a signed upload is refused without being sent anywhere (the lab service sees nothing); the jump host forwards only to its configured service address | H N | 1 |

A failing collector on a lab VM is a finding for §15.1's skip list, not a reason to lower a limit.

## 17. Phases

| Phase | Content | Done when |
|---|---|---|
| **1 – Inventory and the service** | Agent: collectors (§5.1), outbox (§8), the baseline and the daily hash-only sync (§6.1), the section watchers and partial bundles (§6.2 – the change events of every section, so phase 1 is already event-driven), activation and settings over the environment (§6.5, §6.6 – needs environment-enrollment-design.md phase 1), the upload (§9.2), endpoints `status`, `now`, `settings`, rights, `MonitoringEnabled`, `…FullSyncHours`, `…DigestMinutes`, `…Sections`, `…IncludeUsers`, audit 1400–1405, 2501, 2503, 3500, cleanup. Service: entitlement and activation in D1, the upload with signature and quotas, validation, ClickHouse tables for sections, events (diff), device state, the overview and section queries, TTL by tier, deletion. Console: `MonitoringServiceClient`, the cache, the Device tab with Overview (no sparklines yet), the inventory sections and Events (diff events only), *Activate…* and *Deactivate…*, `AllowMonitoring`, the entitlement in Settings | M-ACT-*, M-INV-*, M-CON-02, SEC-MON-01–03 pass; §15.2 measured |
| **2 – Problems, performance and alerts** | Agent: the problem and session watchers (§5.2), sampler (§5.3), the digest (§6.3), thresholds (§5.4), the remaining policies, 1402, 2500. Service: alerts, performance tables, the timeline queries, tenant defaults. Console: Alerts section, Events with lanes, settings section, folder apply, navigation dots, tenant defaults in Settings | M-EVT-*, M-PERF-* pass |
| **3 – Timeline** | `DeviceTimeline`, the Timeline section, sparklines on the tiles, correlation footer (service-side counts), keyboard and automation | M-CON-01, the U screenshots reviewed |
| **4 – Through jump hosts** | Monitoring over the environment relay (environment-enrollment-design.md phase 3): outbox behaviour down a chain, the lab cases | M-RELAY-*, SEC-MON-04 pass |
| **5 – Monitoring view and notifications** | The rail view, every dashboard of §11.4 with the customer's choice, reports and export, search, the cross-device correlation, the service audit page; alert notifications from the service by e-mail and a generic webhook (per tenant: which rules, which recipients, a throttle of one message per device and rule per hour) | M-CON-03, M-CON-04 (a notification arrives for M-PERF-01's alert) |
| **Later – remediation** | An alert runs a deployed script version (scripts-view-design.md) on the device: which alerts, which scripts, rings and stop conditions, approval, audit – a design of its own, decided for (v1 Q6) | – |
| Later | GPU load; per-process network; a learned baseline per device and *unusual for this device* marks; alerts as toast notifications in the console, Teams and other channels; a Windows event forwarding guide so SIEMs read 2500; a self-hosted service image; on-demand download of the agent's monitoring binaries | – |

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
- [jump-host-design.md](jump-host-design.md) §6: monitoring over the environment relay.
- [local-db-design.md](local-db-design.md) §6: the cache table and the schema version.
- `THIRD-PARTY-NOTICES.md`: `Google.Protobuf`, `Grpc.Tools`, `Microsoft.Management.Infrastructure` and the Windows
  packages the collectors add; `build/` gets the `protoc` step and the proto checksum check.
- `docs/test-lab-cases.md` §new *M* and `test-lab-test-document.md`, with `lab\Update-LabCaseStatus.ps1` after the
  first run; `test-lab-spec.md` §5.3 the service in the lab broker.

## 19. Open questions

Every question of v1–v5 is decided (§20). What remains is measured, not decided: the bundle sizes, the service's
throughput and the ClickHouse sizing of §22 are replaced by lab and load-test figures in phase 1.

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
| Event-driven first: changes travel when they happen, the full sync is a daily hash-only reconciliation, the performance digest every 4 hours | The owner (v2): client-initiated updates wherever possible and a full sync once or twice a day; §22 shows the request count drops to a third at the same information |
| Activation and settings reach the device over its broker connection – or the wake channel through the jump host's relay where it has none, with a poll as the fallback – and a device-signed fetch; the console never connects to a device for monitoring | The owner (v4): the broker is the main device connection point; a device behind a jump host has none ([environment-enrollment-design.md](environment-enrollment-design.md)) |
| Full inventory on the interval, changed sections on a trigger, the service computes diffs | The request ("send the selective information") and robustness – a snapshot can't be out of step |
| Thresholds live on the device, set from the console per tenant default, folder or device, policy wins | The project's P1 and P6 (the device is the boundary); two consoles can't disagree about a device |
| Alerts need 90 % of samples above the threshold and close with hysteresis | Judgement: "over 80 % for 5 min" read as sustained, not averaged |
| Session events and definition updates don't trigger bundles | The cost calculation (§22): dozens a day per device |
| Protobuf with gzip for bundles and the query API | The owner (v3): the cross-language contract (C# agent and console, TypeScript service) generated from one file; gzip stays because the sections are strings; the server's cost was never in the format (§10.4) |
| Change notifications from the registry and the event logs, the diff decides | §6.7: every installer writes the Uninstall keys; `Win32_Product` events and file watchers ruled out |
| No SQLite on the device | The agent's dependency footprint as a LocalSystem service; files with `DurableFile` suffice |
| ClickHouse for the service's store, D1 for its metadata | §21 |
| The timeline is drawn by the project itself, from theme brushes | The project has no charting library and draws its own lists and toolbars |
| Correlation is a count, labelled as one | Honesty: RemoteHub shows the admin the order of things; it doesn't decide the cause |
| Free-space rule: percent or GB, either opens | Judgement; an admin who wants one sets the other to 0 |
| ClickHouse is open source and self-hosted for staging and the lab (one container); production on ClickHouse Cloud or three self-hosted nodes per region, decided when production load is known | The owner (v5): the managed price is too much for staging; the same engine and schema everywhere, so the choice is operational, not architectural (§21.5) |
| The Worker inserts into ClickHouse directly with asynchronous inserts; a queue only if the measured p99 of an upload passes 2 s | The owner (v5) |
| A tier downgrade deletes the older data within a day, said in the dialog | The owner (v5) |
| Support access to a tenant's rows only when an Owner or Admin grants it for 7 days, every read in the tenant's audit | The owner (v5) |
| Alert notifications (e-mail, a generic webhook) ship in phase 5 with the dashboards; Teams and others later | The owner (v5) |
| Sign-in duration is tried in the lab (M-PERF-06) and falls back to boot duration only | The owner (v5) |

## 21. The database: ClickHouse for the service, and why not elsewhere

### 21.1 How much data

Per device and day, as the design stands (§22.1 has the assumptions):

| Data | Rows or points | Stored (compressed bundle) |
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

These are compressed-bundle sizes; a column store with typed columns holds the performance and event rows in roughly a third
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
Cloud or self-hosted for production is decided when the load is known; staging and the lab are self-hosted (§21.5).

### 21.4 And the console?

The console stores no history (v1 Q9's answer ended that): a cache of the last overview and 24 hours for up to 50
devices in its encrypted SQLite, a few MB. v1's analysis of DuckDB and an embedded ClickHouse for the console is moot;
if a self-hosted service ever comes, it is the service's image that carries ClickHouse, not the console.

### 21.5 Running the store cheaply: staging, the lab, and production

ClickHouse is open source (Apache 2.0) and runs as one container; the $440 a month of §22.4 is the managed Cloud
price for a two-replica service, which staging and testing don't need.

| Environment | Store | Cost | Notes |
|---|---|---|---|
| **The lab** | One `clickhouse-server` container on the lab host, next to `wrangler dev` (test-lab-spec §5.3) | none | Started by the lab harness with the environment; a fresh database per run |
| **Staging** (`staging.remotehub.dev`) | One self-hosted node in a container on a small VM (2 vCPU, 8 GB, 100 GB disk) in the region's cloud | about $20–40 a month | Same schema and version as production; no replica – staging data is disposable. Backups to R2 nightly with `clickhouse-backup` so a schema migration can be rehearsed on real data |
| **Production** | ClickHouse Cloud (Scale, two replicas) **or** three self-hosted nodes per region with a keeper | Cloud about $440 a month per region at the start, about $2,700 at 200,000 devices (§22.4); self-hosted about $900–1,200 a month per region at that size plus operations | Decided when production load is known; nothing in the design depends on it. The Worker speaks ClickHouse's HTTP interface either way |

ClickHouse Cloud's *Basic* tier (one replica, idles when nothing queries it) is the managed middle ground for staging
if nobody wants to run a VM: third-party figures put it at about $70 a month at six active hours a day and about
$190 around the clock – to confirm against the current price list, which doesn't publish the monthly number.
Self-hosted is cheaper and is the same engine, so it is the proposal.

Other open-source stores were weighed in §21.3; PostgreSQL with TimescaleDB would serve staging as cheaply and
production to about 100,000 devices, but it isn't the engine production needs at the owner's target, and running two
engines across environments is the one thing to avoid.

## 22. Data volumes and costs by digest interval

Prices are the published ones at the time of writing – Cloudflare Workers Paid $5 a month with 10 million requests and
30 million CPU-ms included, then $0.30 and $0.02 per million; D1 5 GB, 25 billion rows read and 50 million rows written
included, then $0.75 per GB-month, $0.001 and $1.00 per million; no charge for traffic; ClickHouse Cloud Scale tier
$0.2985 per compute unit-hour (8 GiB RAM, 2 vCPU) in AWS us-east-1, Enterprise $0.3903, more in European regions
(Germany Enterprise $0.52), storage $25.30 per TB-month compressed. Check them again before a budget.

### 22.1 Assumptions per device

| Item | Value | Why |
|---|---|---|
| Daily sync, hashes only | 1.5 KB | §6.1 |
| Performance digest | 1.5 KB envelope + 1.7 KB per hour covered | 12 metrics × avg/max/min × 12 points an hour, top processes, gzip |
| Triggered bundles | 3 a day: one `apps` change of 40 KB (Edge, Teams and Store apps update themselves), two of 5 KB (a crash with its 15 minutes of samples, an update, an alert) – 50 KB | §6.2 |
| Worker CPU per upload | 1 ms typical, 5 ms for a changed section | signature check, decompression within limits, validation, diff, insert (§10.4) |
| Console queries | 0.1 per upload | a tab open for a tenth of devices; dashboards cached a minute |
| Column-store size | 40 % of the compressed bundle for performance and events, 100 % for section blobs | typical ClickHouse ratios for typed numeric columns; measured in phase 1 |
| Protobuf against JSON | the same sizes are kept | packed floats make the digest a little smaller and the strings compress the same; the difference is inside the rounding of these estimates |

### 22.2 Per device

The digest interval is the only knob that changes the request count; the daily sync is one small upload and the
triggered bundles are the information itself.

| Digest | Daily sync | On the wire a day | Uploads a day | Uploads a month | Outbox for 7 days offline |
|---|---|---|---|---|---|
| 1 h | 24 h | 128 KB | 28 | 840 | 0.9 MB |
| **4 h (default)** | **24 h** | **101 KB** | **10** | **300** | **0.7 MB** |
| 12 h | 24 h | 95 KB | 6 | 180 | 0.7 MB |
| 24 h (merged with the sync) | 24 h | 92 KB | 4 | 120 | 0.6 MB |
| v2 for comparison: hourly full bundle | 1 h | 162 KB | 27 | 810 | 1.1 MB |

- **Against v2's hourly full bundle, the defaults cut uploads to 37 % and bytes to 62 %** with the same information
  arriving: a change is at the service within a minute of happening, which the hourly model couldn't promise either.
- **The floor is the triggered content** – about 50 KB a day – which is what the admin wants to see. Below the 4-hour
  digest the savings are a few KB and a few uploads; the curve's lag grows instead.
- **The snapshot-only variant** (no continuous sampling) would drop the digest entirely and every threshold alert with
  it: *CPU at or above 80 % for 5 minutes* can't be seen in a reading a day. Sampling stays on the device (§15.2); the
  digest only decides how often the summaries travel.
- **On the device's link:** 3 MB a month at the defaults.

### 22.3 Per environment and month

Ingest is what arrives; the store is the 30-day tier plus 7 days of 1-minute data, at the column-store ratio of
§22.1 – the same whatever the intervals, since the same summaries, events and changes arrive. Worker requests are
uploads plus console queries.

| Devices | Digest | Uploads a month | Ingest | Store (30 d) | Store (90 d) | Worker requests | Workers |
|---|---|---|---|---|---|---|---|
| 10,000 | 1 h | 8.4 M | 37 GB | 15 GB | 34 GB | 9.2 M | $5 |
| 10,000 | 4 h | 3.0 M | 29 GB | 15 GB | 34 GB | 3.3 M | $5 |
| 10,000 | 12 h | 1.8 M | 27 GB | 15 GB | 34 GB | 2.0 M | $5 |
| 100,000 | 1 h | 84 M | 367 GB | 150 GB | 340 GB | 92 M | $31 |
| 100,000 | 4 h | 30 M | 290 GB | 150 GB | 340 GB | 33 M | $12 |
| 100,000 | 12 h | 18 M | 273 GB | 150 GB | 340 GB | 20 M | $8 |
| 200,000 | 1 h | 168 M | 734 GB | 300 GB | 680 GB | 185 M | $61 |
| 200,000 | 4 h | 60 M | 580 GB | 300 GB | 680 GB | 66 M | $23 |
| 200,000 | 12 h | 36 M | 545 GB | 300 GB | 680 GB | 40 M | $14 |

D1's part (metadata, a few hundred bytes per device) stays inside the plan at every size. Peak load: a fleet
activated at the same minute digests inside the same hour – with the 60-minute jitter about 55 uploads a second at
200,000 devices, 6 times a day; Patch Tuesday adds about 14 changed-section uploads a second over 4 hours (§10.4).

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
- The **digest interval moves the compute only a little** (ingestion is a minor part of the load next to the
  dashboards); it moves the Workers line by 2–4×, which is tens of dollars at most. The intervals are chosen for
  freshness and chatter, not for money.
- **Enterprise tier** (private networking, customer-managed keys, SSO) is 1.3× the compute; a European region 1.3×
  again. For the EU cluster budget about $570 a month for the smallest service on Enterprise in Germany.
- **Self-managed** (three nodes per region, 8 vCPU / 32 GiB each) is about $900–1,200 a month per region in compute
  on a major cloud at 200,000 devices, plus RemoteHub's operations – cheaper at the top, costlier at the bottom.
  Staging and the lab run one self-hosted node for tens of dollars or nothing (§21.5).
- Traffic out of Cloudflare is free; traffic from the Workers into ClickHouse Cloud is counted by the cloud provider
  the cluster sits in (about 1 TB a month at 200,000 devices and 1 hour, at that provider's rates).

### 22.5 What follows

- At 10,000 devices the whole service costs about $440 a month per region, at 200,000 about $2,700 – **1.4 cents per
  device per month at the top**, and the ClickHouse floor of two regions dominates at the bottom.
- The retention tiers change storage, which is cents, not compute: the price difference between 30 and 90 days is
  not in the infrastructure. A tier is a licence decision.
- **Defaults: changes by event, the digest every 4 hours, the sync once a day.** A tenant that wants a current
  curve sets the digest to an hour for tens of dollars a month more at 200,000 devices; one that wants the least
  chatter sets it to a day and keeps every change and alert as it happens.

The figures come from the assumptions of §22.1 and the sizing of §22.4; phase 1 replaces them with measured bundle
sizes from the lab (M-ACT-01 records the baseline's size per VM, M-INV-02 a digest's and a sync's) and a load test of the service, and §22.3–22.4
are recomputed then.
