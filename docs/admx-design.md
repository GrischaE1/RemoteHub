# RemoteHub – Full configuration through the registry (ADMX)

Status: Draft v3 (2026-10-16), written against 0.18.11. **Phases 1–4 are built** (§16–§19); phase 5 is not. The principles
are in §2, and the open questions in §14.
Owner: Grischa Ernst

## 1. Goal

An administrator can configure **everything they'd want to decide centrally** in RemoteHub (the console) and the
RemoteHub Agent through the registry, and manage it with Group Policy or Intune from **one** template:
`RemoteHub.admx` with `en-US\RemoteHub.adml`. That covers:

- which features may be used at all, such as RDP, PowerShell, agent installation or network shares;
- every setting in the console's *Settings* window;
- the settings of every connection, such as the RDP redirections and NLA;
- the defaults for agent installations;
- everything the agent does on a device, including what's in `agent.json` today.

**A setting that is configured in the registry can't be changed in the console.** The console shows it locked wherever it
would otherwise be changed.

One device can have both products installed, such as an admin workstation that is reachable through its own agent, or
a jump host someone also works from. The registry layout, the template and the way the values are read keep the two
apart on such a device (§3, §8).

## 2. Principles

| # | Principle |
|---|---|
| P1 | **Configured means locked.** A value set by policy wins over the user's choice. **Not configured** means the user decides, as today. There is no "preset but changeable" mode (§14 Q1). |
| P2 | **Settings are greyed out, actions are hidden.** Where a setting is changed (Settings window, connection editor, install dialog, agent desktop options), a configured setting stays visible, shows the policy's value, and is greyed out with a lock icon and "Set by your organisation". That includes settings that only exist for a blocked feature. Where something is **done** (context menus, toolbar, tab menus, the *+* menu, buttons that start something), an action a policy blocks isn't shown at all (§4.2). |
| P3 | **Blocked means refused everywhere, not only hidden.** Any other way into a blocked feature (a keyboard shortcut, an `.rdp` file, a restored tab, a quick-connect, a jump host route) is refused where the feature starts, with "Turned off by your organisation". The check is in the code that opens the session, not only in the UI. |
| P3a | **Three kinds of console policy:** **features** that can be allowed or blocked, grouped by how RemoteHub reaches the device, with a main switch for the agent and one for WinRM (§5). Then **settings** with a value (§6), and **connection settings** that apply to every connection (§7). The agent has the same split: features (§9.1) and settings (§9.2–§9.7). |
| P3b | **Everything that's configured can be seen in the console:** *Settings → Policies* (§4.4). |
| P4 | **One template, two products, separate keys.** The console uses `SOFTWARE\Policies\RemoteHub` (plus subkeys), and the agent uses `SOFTWARE\Policies\RemoteHub\Agent`. Neither product reads the other's key (§3). |
| P5 | **Machine and user.** Agent policies are Machine only. Console policies are Machine and User, and for each value `HKLM` wins over `HKCU`. |
| P6 | **The device is the security boundary.** A console policy binds this PC's RemoteHub. Whoever really must not reach a device is stopped by the agent's policy on that device, by Windows (firewall, RDP and WinRM settings), or both. Every console explain text that blocks a feature says so, and names the agent policy that enforces it on the device. |
| P7 | **When the console uses an agent, both policies apply.** A feature works only if both allow it. If they set the same thing differently, the device wins (§8). |
| P8 | **Nothing but IT writes to `SOFTWARE\Policies`.** The agent's install-time automatic sign-in switch moves out of the policy key (§11.3). |
| P9 | **Existing value names stay.** The 16 agent values, `Rights` and the 8 console values keep their names and meanings. Deployments made with Group Policy Preferences, Intune or scripts keep working (§12.3). |
| P10 | **Secrets never go in the registry.** No passwords, keys or the shared settings file's password. Connections and credentials are content, not settings, so the shared settings folder distributes them (§6.4). |

## 3. Registry layout

```
HKLM|HKCU\SOFTWARE\Policies\RemoteHub                 console: features (Allow*) and settings
HKLM|HKCU\SOFTWARE\Policies\RemoteHub\Connections     console: connection settings (§7)
HKLM|HKCU\SOFTWARE\Policies\RemoteHub\AgentInstall    console: agent installation defaults (§6.9)
HKLM\SOFTWARE\Policies\RemoteHub\Agent                agent: features and settings (§9)
HKLM\SOFTWARE\Policies\RemoteHub\Agent\Rights         agent: who may do what (§9.2)
HKLM|HKCU\SOFTWARE\Policies\RdpManager                console: SettingsFolder only, legacy, read as today
```

Rules that keep the two products apart:

- **A value belongs to the key it's in.** The console never acts on a value below `\Agent`, and the agent never reads
  anything else. The one exception is display only: *Settings → Policies* lists the agent's values when the agent is
  installed on the same PC (§4.4). `AllowAgent` (console) and `DesktopEnabled` (agent) can both be set on one device without mixing.
- **The root key holds console values only.** Subkey names are reserved per product. A future product gets a subkey of
  its own.
- **No policy element empties a whole key.** The template has no `list` element with `explicitValue` or
  `additive="false"`, because disabling it would delete every value in that key, including the other product's
  neighbours. Lists are `multiText` (REG_MULTI_SZ).
- `HKCU\…\Agent` is never read. The template has no User-class agent policies.

## 4. How the console applies a policy

### 4.1 Reading

```
HKLM\SOFTWARE\Policies\RemoteHub[\sub]  →  HKCU\…  →  the user's setting  →  built-in default
```

- The values are read when they're used (as `AppPolicy` does today), so a `gpupdate` takes effect without a restart,
  except where §6 says "at start".
- An invalid value (out of range, an unknown word, the wrong type) counts as **not configured**. It is written once per
  run to the diagnostic log, and *Settings → Policies* shows it in red.

### 4.2 Locking

**Settings: greyed out, never hidden.**

| Where | What a configured value does |
|---|---|
| *Settings* window | The control stays where it is, shows the policy's value, and is greyed out with a lock icon and "Set by your organisation". Buttons that would change it, such as *Change location…*, are greyed out too. |
| Connection editor (global defaults, folders, connections) | Every field of §7 that is configured shows the policy's value at every level, greyed out with the lock. The value stored in the connection is kept but not used (§4.3). |
| Connection editor, settings of a blocked feature | They stay visible and greyed out, with "Turned off by your organisation". Examples: the *PowerShell remoting* rows with `AllowPowerShellRemoting` = 0; *Use the RemoteHub Agent*, *Remote desktop via* and the automatic sign-in rows with `AllowAgent` = 0. |
| Agent install dialog | Every option of §6.9 that is configured is greyed out with its value. A blocked install method is greyed out, not removed, so the dialog keeps its layout. |
| Agent desktop start panel and its options in the toolbar | Options fixed by §6.8 or §7 are greyed out with their value. |
| *Settings → Policies* | Everything (§4.4) |

**Actions: hidden.**

| Where | What a blocked feature (§5) does |
|---|---|
| Connection and folder context menus, the *+* tab menu, the tab context menu | The entries are not shown, such as *PowerShell*, *File transfer*, *Registry*, *Services*, *Processes*, *Remote desktop*, *Install RemoteHub Agent…*, *Set up PowerShell remoting on the target…*, *Add network share…*, *Move under*, *Save as .rdp file…*, *Connect all in folder* (RDP). A submenu left with nothing in it isn't shown. |
| Tab toolbar | The buttons are not shown, such as *Record*, privacy screen and Ctrl+Alt+Del when the agent desktop is off. |
| Main menu and *Settings* buttons that start something | Not shown, such as *Import .rdp files…*, *Export backup…*, *Import backup…*, *Test PowerShell remoting*. |
| Keyboard shortcuts | They do nothing, and the status bar says "Turned off by your organisation". |
| Remote tools of a connection | A tool is offered only if the connection's transport allows it (§5.3). |

### 4.4 *Settings → Policies*

A new page in the Settings window, after *Logs*. It is always there:

- With nothing configured, it says "No policies are set on this PC".
- With values set, the nav entry shows a small lock icon.

| Part | Content |
|---|---|
| Summary | "N settings on this PC are set by your organisation", plus how many come from the computer (HKLM) and how many from the user (HKCU). |
| List | Every configured value, grouped the way the template groups them (Features → RDP / RemoteHub Agent / PowerShell remoting / …, General, Security & storage, …, Connection settings, Agent installation). Each row has the policy's display name, the value as text ("Blocked", "30 days", "Ask; Pin"), the registry value name, and its source (Computer or User). |
| Effects | Values that are off because of a main switch are listed under it, greyed out: "Off because *Allow the RemoteHub Agent* is off". |
| Problems | Invalid values in red with the reason ("`AutoLockMinutes` = 5000 – allowed 1–1440, ignored"). Conflicts in amber (`RemoteDesktopVia` = Agent with `AllowAgent` = 0). |
| This device's agent | If the RemoteHub Agent is installed on this PC, a second section lists its configured values from `HKLM\…\Agent`. It reads that key directly, read-only, since both are on the same PC (§8). It's only shown, and changes nothing in the console. |
| Buttons | *Copy as text* (for support), *Refresh* (reads the registry again after a `gpupdate`), *Show all settings that can be set* (opens the README's policy reference). |

The page only shows. Nothing on it can be changed, because every value on it comes from the registry.

*Settings overview* links to this page instead of listing the values itself.

### 4.3 Shared settings folder

The policy is per PC. The connections live in the settings file, which can be shared between PCs with different
policies. So:

- The policy never changes the settings file. A connection keeps its own values. The policy wins where the connection
  is **used** (`Inheritance.ResolveOptions`) and where the setting is shown.
- A PC without the policy shows and uses the connection's own values.
- A blocked feature never deletes anything. Network shares, for example, stay in the tree and can't be opened.

## 5. Console features (allow or block)

All are DWORD values in `SOFTWARE\Policies\RemoteHub`:

- **1:** allowed, and the user can't turn it off where a user switch exists.
- **0:** blocked.
- **Not set:** allowed, and the user decides.

The features are grouped by **how RemoteHub reaches the device**, because RDP, the RemoteHub Agent and PowerShell remoting
(WinRM) are separate ways in. The agent and WinRM each have a **main switch**:

- When a main switch is 0, everything below it is off, whatever its own switch says. Its own switches are shown greyed
  out on the *Policies* page, marked "Off because <main switch> is off".
- When a main switch is allowed, each function below it can still be blocked on its own.

#### 5.1 RDP

| Value | Blocks | Device-side partner (P6) |
|---|---|---|
| `AllowRdp` | Remote desktop over RDP. `.rdp` files don't open. | Windows: RDP off on the device |

#### 5.2 RemoteHub Agent

| Value | Blocks | Device-side partner (P6) |
|---|---|---|
| **`AllowAgent`** (main switch) | **Every agent function on this PC:** everything below, jump hosts (§5.5), and the connection settings *Use the RemoteHub Agent* and *Remote desktop via → Agent*. RemoteHub then works only over RDP and WinRM. | The agent's whole policy, `DesktopEnabled` and `PowerShellEnabled` |
| `AllowAgentDesktop` | Remote desktop through the agent | Agent `DesktopEnabled` |
| `AllowAgentPowerShell` | The PowerShell tab through the agent | Agent `PowerShellEnabled`, right `PowerShell` |
| `AllowAgentFileTransfer` | The file transfer tab through the agent | Agent `PowerShellEnabled` (§14 Q4) |
| `AllowAgentRegistry` | The registry tab through the agent | Agent `PowerShellEnabled` |
| `AllowAgentServices` | The services tab through the agent | Agent `PowerShellEnabled` |
| `AllowAgentProcesses` | The processes tab through the agent | Agent `PowerShellEnabled` |
| `AllowAgentInstall` | *Install / update / remove RemoteHub Agent*, every method. For organisations that deploy the agent with Intune or SCCM. | – |
| `AllowCredentialProviderInstall` | *Install / remove credential provider* and the install dialog's *Allow automatic sign-in* | Agent `NoUserBehavior` |
| `DisableAgentAutoSignIn` *(existing, inverted)* | Automatic sign-in through the agent | Agent `NoUserBehavior`, right `AutoSignIn` |

#### 5.3 PowerShell remoting (WinRM)

| Value | Blocks | Device-side partner (P6) |
|---|---|---|
| **`AllowPowerShellRemoting`** (main switch) | **Every WinRM function on this PC:** everything below, the *PowerShell remoting* connection settings (`PsRemoting`, `PsUseSsl`), the remoting check and *Test PowerShell remoting*. Installing the agent over WinRM goes too: only *Through the agent*, *Clipboard* and *Save files* remain. | Windows: WinRM off on the device |
| `AllowRemotingPowerShell` | The PowerShell tab over WinRM | – |
| `AllowRemotingFileTransfer` | The file transfer tab over WinRM | – |
| `AllowRemotingRegistry` | The registry tab over WinRM | – |
| `AllowRemotingServices` | The services tab over WinRM | – |
| `AllowRemotingProcesses` | The processes tab over WinRM | – |
| `AllowRemotingSetup` | *Set up PowerShell remoting on the target…* | – |

A tool tab is offered for a connection only if the transport the connection uses allows it. A connection that uses the
agent gets its file transfer from `AllowAgentFileTransfer`, one that uses WinRM from `AllowRemotingFileTransfer`. With
both main switches off, there are no remote tools at all, only RDP and network shares.

#### 5.4 Connections

| Value | Blocks |
|---|---|
| `AllowQuickConnect` | Connecting to a typed name or address that is not a saved connection (Ctrl+F → Enter) |
| `AllowConnectionEditing` | Creating, editing, moving, duplicating and deleting folders, connections and shares. The tree becomes read-only, for connections IT provides through a shared folder. |

#### 5.5 Jump hosts, network shares, recording

| Value | Blocks | Device-side partner (P6) |
|---|---|---|
| `AllowJumpHosts` | Connections below a jump host don't connect, and *Move under* is hidden. Also off when `AllowAgent` = 0, because a jump host is an agent. | Agent `TunnelEnabled` |
| `AllowNetworkShares` | All network shares: adding, opening, and a server's shares | – |
| `AllowSmb`, `AllowSftp`, `AllowFtp`, `AllowWebDav` | One protocol each. `AllowNetworkShares` = 0 wins over them. | – |
| `DisableRecording` *(existing, inverted)* | Recording tabs | – |

#### 5.6 Passwords, import and export, settings

| Value | Blocks |
|---|---|
| `AllowSavedPasswords` | Storing passwords in connections, folders and saved credentials. They are asked at connect and kept in memory (§14 Q2). |
| `AllowPasswordCopy` | Copying a stored password to the clipboard: the *Copy password* actions and the password prompt's clipboard copy |
| `AllowRdpFileImport` | *Import .rdp files…* |
| `AllowRdpFileExport` | *Save as .rdp file…* |
| `AllowBackupExport` | *Export backup…*, because the file holds every password |
| `AllowBackupImport` | *Import backup…* |
| `AllowSharedSettings` | Choosing a shared settings folder. A `SettingsFolder` policy still applies. |

The four values that already exist keep their name and polarity: `DisableTelemetry`, `DisableUpdateCheck`,
`DisableAgentAutoSignIn` and `DisableRecording` (§12.3). In the template they read the same way as the others (*Allow
usage data*, …), and the template writes the inverted value.

## 6. Console settings

Values in `SOFTWARE\Policies\RemoteHub` unless the section says otherwise. "Where" is the control that gets locked.

### 6.1 General

| Value | Type, range | Default | Where |
|---|---|---|---|
| `Theme` | SZ `Dark`, `Light`, `System` | Dark | Settings → General |
| `SwitchKeys` | SZ, one of the `SwitchHotkeys` preset ids | `Less` | Settings → General |

### 6.2 Updates and usage data

| Value | Type, range | Default | Where |
|---|---|---|---|
| `DisableUpdateCheck` *(existing)* | DWORD 1 = off, 0 = on and locked | user | Settings → General → Updates |
| `UpdateChannel` *(existing)* | SZ `stable`, `beta` | by version | Settings → General → Updates |
| `DisableTelemetry` *(existing)* | DWORD 1 = off, 0 = on and locked (§14 Q3) | user | Settings → General → Privacy |

### 6.3 Security

| Value | Type, range | Default | Where |
|---|---|---|---|
| `StartupProtection` | SZ `Required` or `Off` | user | Settings → Security & storage. `Required`: at start without protection, RemoteHub asks for it to be set up and opens nothing until it is. It can't be turned off. `Off`: it can't be turned on (for kiosk or jump-server PCs). Read at start. |
| `AllowWindowsHello` | DWORD 0/1 | user | *Startup protection…* → Windows Hello |
| `AutoLockMinutes` | DWORD 1–1440 | user (0 = never) | Settings → Security & storage. It needs startup protection. If protection is off, the value is ignored and logged. |

### 6.4 Settings storage

| Value | Type, range | Default | Where |
|---|---|---|---|
| `SettingsLocation` | SZ `Local` or `Shared` | user | *Change location…*. `Shared` needs `SettingsFolder`. Read at start. |
| `SettingsFolder` *(existing)* | SZ, environment variables allowed | – | *Change location…*. Setting it implies `Shared`, as today. |
| `AllowSharedSettings` | see §5 | | |

The shared file's password is never a policy (P10). Each PC asks for it once, as today.

### 6.5 Logs

| Value | Type, range | Default | Where |
|---|---|---|---|
| `AuditRetentionDays` *(existing)* | DWORD 1–36500 | 365 | Settings → Logs |
| `DiagnosticLog` | DWORD 0/1 | 1 | Settings → Logs. This switch is new: 0 writes no diagnostic log. |
| `DiagnosticLogRetentionDays` | DWORD 1–3650 | 30 | Settings → Logs. New: the diagnostic log has no cleanup today. |

### 6.6 Recordings

| Value | Type, range | Default | Where |
|---|---|---|---|
| `DisableRecording` *(existing)* | see §5 | | |
| `RecordingFolder` *(existing)* | SZ, environment variables allowed | `Videos\RemoteHub` | Settings → Recordings |
| `RecordingFps` | DWORD 5, 10, 15, 30, 60 | 10 | Settings → Recordings |
| `RecordingResolution` | SZ `Tab`, `FullHd`, `Uhd4K` | Tab | Settings → Recordings |
| `RecordingUseGpu` | DWORD 0/1 | 0 | Settings → Recordings |
| `RecordingIncludeDetails` | DWORD 0/1 | 0 | Settings → Recordings |

### 6.7 Remote tools

| Value | Type, range | Default | Where |
|---|---|---|---|
| `ProcessRefreshSeconds` | DWORD 0–3600 (0 = only by hand) | 15 | Global defaults → Tools |
| `ServiceRefreshSeconds` | DWORD 0–3600 | 60 | Global defaults → Tools |

### 6.8 Agent remote desktop (console side)

| Value | Type, range | Default | Where |
|---|---|---|---|
| `AgentDesktopQuality` | SZ, a `DesktopQualityPreset` id | Admin | Start panel and toolbar |
| `AgentDesktopConsent` | SZ `Ask`, `Pin`, `None` | last used | Start panel. The device's `AllowedConsentModes` still decide what it accepts. |
| `AgentDesktopViewOnly` | DWORD 1 = always view-only | user | Start panel. `View only` is fixed. |
| `AgentDesktopFitToWindow` | DWORD 0/1 | 1 | Toolbar |
| `AgentDesktopShowInfo` | DWORD 0/1 | 0 | Toolbar |
| `AgentDesktopKeyCombinations` | DWORD 0/1 | 1 | Toolbar |
| `AgentDesktopClipboard` | DWORD 0 off, 1 text, 2 text and images, 3 also files | 3 | Toolbar. The lower of this and the device's `DesktopClipboard` applies. |

### 6.9 Agent installation defaults (`SOFTWARE\Policies\RemoteHub\AgentInstall`)

Locked in the install dialog. `AllowAgentInstall` = 0 (§5) hides the dialog entirely.

| Value | Type, range | Default | Notes |
|---|---|---|---|
| `Methods` | MULTI_SZ of `Remoting`, `ThroughAgent`, `Clipboard`, `SaveFiles` | all | The methods offered. One left: it's fixed. |
| `Port` | DWORD 1–65535 | 58732 (the agent's default port) | Written into the enrollment file. Must match the agent's `ListenPort` policy if one is set (§9.7). |
| `FirewallScope` | SZ: addresses and ranges, comma-separated; empty = any address | empty | The scope of the MSI's firewall rule. |
| `AllowedGroups` | MULTI_SZ: group SIDs or names | Administrators | Written into the enrollment file. The device's `Rights` policy wins over it. |
| `VirtualDisplay` | DWORD 0/1 | 1 | The install option *Virtual display for servers without a monitor*. |
| `AutoSignIn` | DWORD 0/1 | 0 | The install option *Allow automatic sign-in*. 1 only if `AllowCredentialProviderInstall` isn't 0. |
| `UseAgentAfterwards` | DWORD 0/1 | 1 | *Use the agent for this connection afterwards* |

## 7. Connection settings (`SOFTWARE\Policies\RemoteHub\Connections`)

Each value fixes one property of `RdpOptions` for **every** connection. The value names are the property names, so the
code maps them without a table. A configured value replaces whatever the global defaults, folders and the connection
say. It is applied last in `Inheritance.ResolveOptions` and is used by the RDP session, `.rdp` export and the agent
desktop.

| Value | Type, range | Built-in default |
|---|---|---|
| `Resize` | SZ `DynamicResolution`, `SmartSizing`, `Fixed` | DynamicResolution |
| `FixedResolution` | SZ `<w>x<h>` | 1920x1080 |
| `ColorDepth` | DWORD 15, 16, 24, 32 | 32 |
| `UseMultimon` | DWORD 0/1 | 0 |
| `Quality` | SZ `Best`, `Balanced`, `Fast` | Balanced |
| `RedirectClipboard` | DWORD 0/1 | 1 |
| `RedirectDrives` | DWORD 0/1 | 0 |
| `RedirectPrinters` | DWORD 0/1 | 0 |
| `RedirectSmartCards` | DWORD 0/1 | 0 |
| `Audio` | SZ `PlayLocal`, `PlayRemote`, `None` | PlayLocal |
| `ForwardWindowsKeys` | DWORD 0/1 | 1 |
| `EnableNla` | DWORD 0/1 | 1 |
| `ServerAuthentication` | SZ `ConnectWithoutWarning`, `DoNotConnect`, `Warn` | Warn |
| `AdminSession` | DWORD 0/1 | 0 |
| `AutoReconnect` | DWORD 0/1 | 1 |
| `PasswordPrompt` | SZ `Clipboard`, `Off` | Clipboard |
| `PsRemoting` | DWORD 0/1 | 0 |
| `PsUseSsl` | DWORD 0/1 | 0 |
| `UseAgent` | DWORD 0/1 | 0 |
| `RemoteDesktopVia` | SZ `Automatic`, `Rdp`, `Agent` | Automatic |
| `AgentAutoSignIn` | DWORD 0/1 | 0 |
| `AgentAutoSignInEndAction` | SZ `Lock`, `SignOut` | Lock |

Enums are written as words, not numbers, so a policy doesn't depend on the order of a C# enum.

Feature blocks (§5) win over these. `RemoteDesktopVia` = `Agent` with `AllowAgent` or `AllowAgentDesktop` = 0 means no remote desktop,
and *Settings → Policies* flags the conflict.

**Not policies:**

- **Colour tag, icon, address, port, name and notes:** they belong to each connection.
- **`CopyPasswordOnPrompt`:** obsolete, only migrated.

## 8. Both products on one device

| | Console (`…\RemoteHub`, subkeys) | Agent (`…\RemoteHub\Agent`) |
|---|---|---|
| Governs | What the person at **this PC** may do on other devices | What anyone may do **on this device** through its agent |
| Template category | *RemoteHub Console* | *RemoteHub Agent* |
| Class | Machine and User | Machine |

- A device with both installed reads both independently. The console on that device connecting to its own agent
  (loopback) is a normal case: both policies apply.
- **Both must allow:**
  - an agent desktop needs `AllowAgent` and `AllowAgentDesktop` here and `DesktopEnabled` on the device;
  - PowerShell through the agent needs `AllowAgent` and `AllowAgentPowerShell` here and `PowerShellEnabled` and the right `PowerShell` on the
    device;
  - the clipboard is the lower of `AgentDesktopClipboard` and `DesktopClipboard`.
- **The device wins where both set a value:**
  - `AutoSignInEndAction` over the connection's `AgentAutoSignInEndAction`;
  - `AllowedConsentModes` over `AgentDesktopConsent`;
  - the device's `Rights` over the install default `AllowedGroups`.
- **The device's policy locks the console's device actions.** The agent's `Hello` gets a list of the names of its
  configured policy values (names only, never values). The console then locks the matching device actions:
  - `NoUserBehavior` or `Rights\AutoSignIn` configured → *Install / remove credential provider* shows "Set by the
    device's policy";
  - `VirtualDisplay` configured → the virtual display option of an update.
- Each explain text names its partner on the other side, such as "The device can also turn this off: RemoteHub Agent →
  Remote desktop → Allow remote desktop".

## 9. Agent policies (`HKLM\SOFTWARE\Policies\RemoteHub\Agent`)

They are read for every connection, as today, and a value there wins over `agent.json`. If the key exists but can't be
read, nothing is allowed (unchanged). **New** marks values the agent doesn't read yet.

### 9.1 Features

| Value | Type | Default | Blocks |
|---|---|---|---|
| `PowerShellEnabled` **(new)** | DWORD 0/1 | 1 | Every PowerShell session through the agent, and so PowerShell, files, registry, services, processes and updates through the agent. They all run in that session, which is why the agent can't block them one by one (§14 Q4). |
| `DesktopEnabled` | DWORD 0/1 | 1 | The remote desktop. |
| `TunnelEnabled` | DWORD 0/1 | 1 | Use as a jump host. |
| `AuditToEventLog` | DWORD 0/1 | 1 | Audit entries in the Windows Application log. |

### 9.2 Access rights (subkey `Rights`)

The value names are those of `AgentRight`. Each is a MULTI_SZ of group SIDs or names:

- `PowerShell`
- `Desktop`
- `DesktopViewOnly`
- `SecureDesktop`
- `ConsentBypass`
- `PrivacyScreen`
- `Tunnel`
- `AutoSignIn`

They're unchanged ([agent-spec.md §7.1](agent-spec.md)).

**New:** `AllowedGroups` (MULTI_SZ) in the agent key itself. It is the default for every right that isn't set on its
own, and so replaces `allowedGroups` from `agent.json` (§9.7). `DesktopViewOnly`, `ConsentBypass` and `AutoSignIn` stay
"nobody" unless they're set themselves.

### 9.3 Remote desktop

| Value | Type, range | Default |
|---|---|---|
| `MaxFramesPerSecond` | DWORD 1–60 | 60 |
| `MaxQuality` | DWORD 1–100 | 100 |
| `DesktopClipboard` | DWORD 0–3 | 3 |
| `DesktopIdleTimeoutMinutes` | DWORD 0–1440 | 0 |
| `VirtualDisplay` **(new)** | SZ `auto`, `always`, `off` | agent.json (`auto`). The policy can't install the driver: without it, `auto` and `always` do nothing. |

### 9.4 Consent

| Value | Type, range | Default |
|---|---|---|
| `AllowedConsentModes` | MULTI_SZ or `;`-separated SZ of `Ask`, `Pin`, `None` | Ask, Pin |
| `ConsentTimeoutSeconds` | DWORD 5–600 | 30 |

### 9.5 Privacy

| Value | Type, range | Default |
|---|---|---|
| `PrivacyScreen` | DWORD 0–2 | 2 |
| `PrivacyScreenMessage` **(new)** | SZ, at most 120 characters | the built-in text ([privacy-mode-design.md §8](privacy-mode-design.md)) |
| `HiddenApps` | MULTI_SZ | none |

### 9.6 Automatic sign-in and jump host

| Value | Type, range | Default |
|---|---|---|
| `NoUserBehavior` | SZ `SignInScreen`, `AutoSignIn` | agent.json, then `SignInScreen` (§11.3) |
| `AutoSignInRequirePrivacyScreen` | DWORD 0/1 | 0 |
| `AutoSignInEndAction` | SZ `Lock`, `SignOut` | the connection's setting |
| `TunnelTargets`, `TunnelPorts` | MULTI_SZ | anywhere except the device's own special addresses; every port |
| `MaxTunnelStreams` | DWORD 1–1024 | 64 |

### 9.7 Service (today in `agent.json`)

| Value | Type, range | Default | Takes effect |
|---|---|---|---|
| `ListenPort` **(new)** | DWORD 1–65535 | agent.json | At service start. The agent moves its own firewall rule to the new port. Consoles reach the device on the port stored for it, so IT sets `AgentInstall\Port` to the same value and changes existing connections, or the device is unreachable (§14 Q5). |
| `LogRetentionDays` **(new)** | DWORD 1–3650 | agent.json (30) | At the next daily cleanup |
| `MaxSessions` **(new)** | DWORD 1–200 | agent.json (20) | For new sessions |
| `AllowedGroups` **(new)** | MULTI_SZ | agent.json | See §9.2 |

These stay out of the template, for the reasons given:

| Value | Reason |
|---|---|
| `DeviceId`, `CertificateThumbprint` | The device's identity, written by enrollment. A policy would give every device the same identity. |
| `HelperUIAccess` | A support switch from the UIAccess spike. It stays readable for support and is documented in agent-spec as a support value. |
| `SoftwareSASGeneration` | Windows' own policy, in Windows' templates. The agent already respects it. |

## 10. The template

### 10.1 Files and namespace

- **Files:** `policy\RemoteHub.admx` and `policy\en-US\RemoteHub.adml`.
- **Namespace:** prefix `remotehub`, namespace `RemoteHub.Policies.RemoteHub`. There is no reference to `windows.admx`,
  so Intune's *Import ADMX* needs no prerequisite.
- **`supportedOn`:** the template defines its own products, `RemoteHub <version> or later` and
  `RemoteHub Agent <version> or later`. Values that exist today get 0.18.11, and new ones the release that brings them.
- **Policy names** start with their product (`Console_AllowRdp`, `Agent_DesktopEnabled`), so they can't clash.

### 10.2 Categories

```
RemoteHub
├─ RemoteHub Console        (Machine and User)
│  ├─ Features                     §5: RDP, RemoteHub Agent, PowerShell remoting, Connections, Jump hosts and shares, Passwords and import/export
│  ├─ General                      §6.1
│  ├─ Updates and usage data       §6.2
│  ├─ Security                     §6.3
│  ├─ Settings storage             §6.4
│  ├─ Logs                         §6.5
│  ├─ Recordings                   §6.6
│  ├─ Remote tools                 §6.7
│  ├─ Agent remote desktop         §6.8
│  ├─ Agent installation           §6.9
│  └─ Connection settings          §7, sub-groups Display, Devices and resources, Security, Remoting and agent
└─ RemoteHub Agent    (Machine)
   ├─ Features                     §9.1
   ├─ Access rights                §9.2
   ├─ Remote desktop               §9.3
   ├─ Consent                      §9.4
   ├─ Privacy                      §9.5
   ├─ Automatic sign-in            §9.6
   ├─ Jump host                    §9.6
   └─ Service                      §9.7
```

### 10.3 Elements

| Registry | ADMX |
|---|---|
| DWORD 0/1 | Policy-level `enabledValue` 1 and `disabledValue` 0. Both are locked (P1). Not configured removes the value. |
| DWORD number | `decimal` with `minValue` and `maxValue`, the same range as the code. |
| DWORD from a fixed set (`RecordingFps`, `ColorDepth`, `DesktopClipboard`, `PrivacyScreen`) | `enum` with `decimal` items |
| SZ word | `enum` with `string` items |
| SZ text, path | `text`, with `expandable="true"` where the code expands variables |
| MULTI_SZ | `multiText` |
| `AllowedConsentModes` | `enum` of the meaningful combinations as `;`-separated SZ, which the agent already accepts |

One registry value is one policy. The only exceptions are pairs that belong together: `MaxFramesPerSecond` with
`MaxQuality`, and `TunnelTargets` with `TunnelPorts`. That way *Enabled* never fixes a second setting the admin didn't
mean to fix.

Each explain text says:

- which product and side it governs;
- what Enabled, Disabled and Not configured each do;
- the default;
- what the user sees: hidden, or locked in place;
- the partner policy on the other side (§8).

## 11. Code changes

### 11.1 One catalogue

`RemoteHub.Agent\PolicyCatalog.cs` holds every entry of §5–§9. The console links it the way it links the wire contract
(`<Compile Include="..\RemoteHub.Agent\…" Link=…>`). Each entry has:

- the product
- the key
- the value name
- the type and the range or allowed words
- the default
- the version it came in
- for console entries, the `PolicyTarget` the lock uses (§11.2)

Both products read their values through `Policy.Get<T>(entry)`, which returns the value and where it came from (HKLM,
HKCU, local, default) and logs an invalid value once. `AgentPolicy` and `AppPolicy` become thin views over it. Their
existing callers don't change.

### 11.2 Console

- **Features:**
  - `Features.IsAllowed(Feature)` covers §5, including the per-protocol share switches. Each feature knows its main
    switch (`AllowAgent`, `AllowPowerShellRemoting`, `AllowNetworkShares`), and a main switch at 0 makes every feature
    below it false.
  - `Features.IsAllowed(Tool, Connection)` resolves the connection's transport first (agent or WinRM, as
    `Inheritance.UsesAgent` does), then checks that transport's switch for the tool.
  - The menu, toolbar and shortcut builders filter on it and **hide** what is blocked (P2). Settings that belong to a
    blocked feature are **greyed out** through `PolicyLock`, not hidden.
  - Every place that opens a session checks it too: `RdpSession`, `AgentDesktopSession`, `PsSession`, `RemoteToolSession`
    and its views, `ShareConnector.Open`, `JumpTunnels`, `AgentInstaller`, quick-connect, `.rdp` file open, backup
    import and export.
  - A refused open shows "Turned off by your organisation" in the tab.
- **Settings:**
  - Each setting in §6 binds its control through `PolicyLock.Apply(control, entry)`. It sets the value, disables the
    control, and adds the lock icon and text.
  - This replaces the hand-written managed code in `EditorWindow.xaml.cs` (update check, channel, telemetry, recordings,
    audit retention). `Save_Click` skips locked values.
- **Connections:**
  - `AppPolicy.Connections` returns a partial `RdpOptions`.
  - `Inheritance.ResolveOptions` applies it after `FillFrom`.
  - `EditorViewModel` locks those fields at every level.
- **Agent install:** `AgentInstallDialog` and `AgentInstaller` take §6.9.
- **New behaviour behind new values:**
  - startup protection required or off;
  - Windows Hello allowed;
  - read-only tree;
  - no saved passwords;
  - no password copy;
  - diagnostic log on or off, and its retention.
  
  Each is its own change with its own tests (§13).
- **Settings → Policies (§4.4):**
  - A new page `PagePolicySettings` in `EditorWindow`, built from `PolicyCatalog`. It walks every console entry, and for
    an agent installed on this PC every agent entry, and lists those `Policy.Get` reports as configured, invalid or off
    because of a main switch.
  - It shares the text format with *Copy as text*.
  - *Settings overview* links to it.

### 11.3 Agent

- **New values:**
  - `AgentPolicy` gets `PowerShellEnabled`, `AllowedGroups`, `VirtualDisplay`, `PrivacyScreenMessage`,
    `LogRetentionDays`, `MaxSessions` and `ListenPort`, each falling back to `agent.json`.
  - `Refused()` sets `PowerShellEnabled` off.
- **Where they're read:**
  - `AgentHost` checks `PowerShellEnabled` and `MaxSessions`.
  - `AgentLog` reads the retention at each cleanup, through a function like `EventLogEnabled`.
  - `DesktopEndpoint` reads `VirtualDisplay`.
  - The helper gets the privacy text with the overlay.
  - `Program` reads `ListenPort` at start and moves the firewall rule.
- **`Hello`:** it carries the names of the configured values (§8).
- **`RemoteHubAgent.exe policy`:** it prints every value with its source (policy, agent.json, default) and flags
  invalid ones. Support and the lab tests use it.
- **Automatic sign-in moves out of the policy key:**
  - `AutoSignInSetup` writes `noUserBehavior` and `autoSignInGroups` into `agent.json` instead of
    `SOFTWARE\Policies\RemoteHub\Agent`.
  - Today, if IT configures *When nobody is signed in* and later sets it back to Not configured, Windows deletes the
    value the MSI wrote, and automatic sign-in turns off without notice.
  - Migration, once, at service start: if `autosignin.policy` exists and the values it records are still in the policy
    key unchanged, move them to `agent.json`, delete them from the key, and delete the marker. Values IT has changed
    stay where they are.

## 12. Shipping

### 12.1 Delivery

- `release.yml` attaches the templates next to the MSIs: `RemoteHub.admx`, `RemoteHub.adml` (en-US) and
  `RemoteHub-PolicyTemplates-<version>.zip` (the contents of `policy\`), each with a `.sha256`. A missing or invalid
  template fails the release. The README links them and
  explains the Central Store, a single PC's `%WINDIR%\PolicyDefinitions`, and the Intune import.
- **The MSIs don't install the template.** Both would install the same file into `PolicyDefinitions`. On a device with
  both products, an older console and a newer agent would leave whichever template was installed last, and uninstalling
  one would have to keep the file for the other.

### 12.2 Intune

- Use *Devices → Configuration → Import ADMX*, then a profile from *Imported Administrative templates*.
- Agent policies appear under *Computer Configuration*. Console policies appear under both, and the machine scope wins
  (P5).

### 12.3 Compatibility and later releases

- The existing values keep name, key and meaning. There are two exceptions:
  - `DisableTelemetry` = 0 and `DisableUpdateCheck` = 0 now lock the feature **on**. Today they mean "the user decides".
    The release notes say so (§14 Q3).
  - `SettingsFolder` under `…\Policies\RdpManager` stays readable. It is not in the template.
- Never rename or remove a value, or reuse a policy or element id. A value that is no longer read stays in the template
  as "(no longer used from <version>)" until a major version.
- Each release that adds values raises the template's `revision` and adds its `supportedOn` entries.

## 13. Tests

### 13.1 CI

- **Catalogue ↔ ADMX:**
  - every catalogue entry has exactly one policy writing it, with the same key, name, type and range or words;
  - every value the template writes is in the catalogue;
  - agent policies are Machine, console policies Both;
  - no `list` elements with `explicitValue` or `additive="false"`;
  - no policy writes outside its product's keys.
- **ADMX ↔ ADML:** every string and presentation id resolves, is unique, and is used.
- **Semantics:** for each policy, the registry writes for Enabled, Disabled and Not configured go through a fake
  registry into `Policy.Get`, `AgentPolicy` and `AppPolicy`, and the effective value is checked.
- **Lock coverage:** every console setting entry names its `PolicyTarget`. A test checks each target exists (a control
  name in the Settings window, a field in `EditorViewModel`, an option in `AgentInstallDialog`), so no setting can be
  configured without being locked.
- **Feature coverage:** for each feature in §5, a test opens it through every entry point listed in §11.2 with the
  feature blocked, and expects the refusal.
- **Main switches:**
  - With `AllowAgent` = 0, every agent feature is refused, even with its own switch at 1.
  - With `AllowPowerShellRemoting` = 0, every WinRM tool is refused.
  - A tool blocked for one transport still works over the other.
- **Hidden versus greyed:**
  - The context menu, *+* menu and toolbar builders return no entry for a blocked action.
  - The connection editor still returns the rows of a blocked feature, greyed out (P2).
- **Policies page:** with a fake registry that has valid, invalid and main-switch-overridden values, the page model lists
  each with the right group, text, source and state, and *Copy as text* matches it.
- **Precedence and migration:**
  - HKLM over HKCU;
  - policy over the user's setting and `agent.json`;
  - the connection policy applied after inheritance;
  - the automatic sign-in migration (unchanged → moved; changed by IT → kept; no marker → nothing).

### 13.2 Lab

There is no domain in the lab, and none is needed: the cases write the registry values, as C-AUTO and D-PRIV do. New
cases in `docs/test-lab-cases.md`:

| Id | What |
|---|---|
| POL-01 | The template loads in `gpedit.msc` without errors on Windows 10 1809, Windows 11 and Server 2025. Manual, once per release that changes the template. |
| POL-02 | Both products on one VM: each reads only its own key. `DesktopEnabled` = 0 refuses the desktop *to* this VM, and `AllowAgent` = 0 hides every agent action *from* this VM. |
| POL-03 | `RemoteHubAgent.exe policy` shows each source and flags invalid values. |
| POL-04 | The new agent values without a restart, and `ListenPort` after a restart, including the moved firewall rule. |
| POL-05 | `PowerShellEnabled` = 0: PowerShell and every remote tool through the agent are refused and audited, and the desktop still works. |
| POL-06 | Console features: `AllowAgent` = 0 hides every agent action and greys out the agent rows in the editor; `AllowPowerShellRemoting` = 0 does the same for WinRM and its tools; `AllowAgentFileTransfer` = 0 leaves file transfer over WinRM; `AllowRdp`, `AllowAgentInstall` and `AllowNetworkShares` = 0 hide their entries. Each also refuses a restored tab and a quick-connect. |
| POL-07 | Connection settings: `RedirectDrives` = 0 with a connection that has drives on. The session has no drives, and the field is locked in the editor at every level. |
| POL-08 | The automatic sign-in migration on an update from 0.18.11 with `AUTOSIGNIN=1`. |
| POL-09 | The device's policy locks *Install / remove credential provider* in the console (§8). |
| POL-10 | Intune import, if a lab tenant exists; otherwise manual before the first release with the template. |
| POL-11 | *Settings → Policies* on a VM with console and agent: the console values from HKLM and HKCU with their source, the agent's values in their own section, an invalid value in red, and *Refresh* after changing a value. |

## 14. Open questions

| # | Question | Proposal |
|---|---|---|
| Q1 | Should connection settings also have a "preset, but users may change it" mode? | No. P1 says configured means locked. If it's needed later, a second subkey `ConnectionDefaults` with the same names can do it without changing anything here. |
| Q2 | `AllowSavedPasswords` = 0: what happens to passwords already stored? | They're not used while the policy is set. They're removed at the next save after a notice ("Your organisation doesn't allow saved passwords – N were removed"). Not in a shared settings file: the policy may be set on only some PCs, so there they're ignored but kept. |
| Q3 | Can usage data and the update check be forced **on** (`Disable…` = 0 locked)? | Yes, for P1's sake, with a release note. The privacy statement already covers usage data. |
| Q4 | Should the agent block files, registry, services and processes one by one? | Not by switches. They all run in one PowerShell session, so blocking one while allowing PowerShell can be bypassed. Making them separate would need constrained endpoints per tool on the agent, which is its own project. The console switches (§5) hide them, and `PowerShellEnabled` is the device's boundary. |
| Q5 | `ListenPort` by policy cuts off consoles that use the old port. | Accept with the warning in the explain text. A later improvement: the agent keeps answering `Hello` on the old port with a redirect, until the next restart. |
| Q6 | More ADML languages? | `de-DE` after phase 1, once the English texts are settled. |

## 15. Build order

Each phase can be merged and released on its own. The template grows with each one.

| Phase | What | Done when |
|---|---|---|
| 1 – Catalogue and template for what exists | `PolicyCatalog`, `Policy.Get`, the template for the 16 agent values, `Rights` and the 8 console values, `PolicyLock` for the existing managed settings, *Settings → Policies*, the CI tests, the release asset, README and agent-spec | GPO and Intune set everything that exists today, and the console shows it. CI checks template and code against each other. POL-01 and POL-11 pass. |
| 2 – Agent | §9 new values, `policy` command, `Hello` names, automatic sign-in out of the policy key | POL-02 to POL-05, POL-08 and POL-09 pass |
| 3 – Console features | §5 with the main switches and `Features.IsAllowed` at every entry point; actions hidden, settings greyed out | POL-06 passes, and the feature-coverage test is green |
| 4 – Console settings and connections | §6 and §7 through `PolicyLock` and `Inheritance` | POL-07 passes, and the lock-coverage test is green |
| 5 – New console behaviour | Startup protection required or off, Windows Hello, read-only tree, saved passwords, password copy, diagnostic log switch and retention | Each with its own tests. Can be released one by one. |

Phase 1 also covers the *ADMX* item of the credential provider's phase 5
([credential-provider-design.md §13](credential-provider-design.md)), and §7's *ADMX/ADML templates* in
[agent-desktop-draft.md](agent-desktop-draft.md).

## 16. Implementation notes (Phase 1)

What was built, and where it differs from the sections above:

- **Catalogue:** `src\RemoteHub.Agent\PolicyCatalog.cs`, linked into RemoteHub like the wire contract. It has the 8 console
  values, the 17 agent values and the 8 rights, each with key, name, kind (`Number`, `Text`, `List`), range or words,
  group, title and labels for Settings → Policies. `PolicyCatalog.Read` returns the value with its scope (Computer or
  User), or with its problem when it is invalid. An invalid value doesn't hide a valid one in HKCU. `AppPolicy` and
  `SettingsStore.PolicyFolder` read through it. `AgentPolicy.From` takes its names and number ranges from it, and keeps
  its own handling of lists and words, which is unchanged.
- **Behaviour change:** `AuditRetentionDays` above 36500 is now ignored (invalid) instead of being capped at 36500.
- **Template:** `policy\RemoteHub.admx` and `policy\en-US\RemoteHub.adml`, revision 1.0, 32 policies.
  - The four `Disable…` console policies write 1 when Enabled. Disabled *deletes* the value (`<delete />`), so it means
    the same as Not configured, as in 0.18.11. Forcing them on (§12.3, Q3) comes later.
  - `TunnelTargets` and `TunnelPorts` are two policies, not one. An empty list in a combined policy would write an empty
    REG_MULTI_SZ, which the agent reads as "nothing allowed" rather than "not set".
  - The lists (rights, `HiddenApps`, tunnel lists) are required `multiText` elements. To allow nobody a right, the explain
    text says to enter `S-1-0-0` (Nobody).
  - `AllowedConsentModes` is a dropdown of the 7 combinations, written as a `;`-separated REG_SZ.
- **Settings → Policies:** a page after *Logs*, from `PolicyReport`. It has the summary, the values grouped by the
  template's categories with source and registry path, invalid values in red, and the agent's values when the
  `RemoteHubAgent` service is installed on the PC. It also has *Copy as text* and *Refresh*. *Settings overview* got a
  *Policies* section with the count and *Show policies…*.
- **Greyed out, not hidden:**
  - `PolicyLock.Apply` disables a control and gives it a tooltip that points to Settings → Policies. It is used by the
    update, channel, telemetry, audit retention, recording and settings location controls.
  - The connection rows for automatic sign-in (`DisableAgentAutoSignIn`) are now greyed out with "Turned off by your
    organisation" instead of hidden (`OptionRow.PolicyNote`).
  - Lab case C-POL-08 and its test were changed to match; they haven't run yet.
- **Tests:** `PolicyTemplateTests` (14) covers:
  - catalogue ↔ ADMX: one writer per value, types, ranges, every value Enabled or Disabled can write accepted;
  - class and key per product, and no `list` elements;
  - ADMX ↔ ADML: strings and presentations;
  - what the agent does with the template's values;
  - the reader: HKLM over HKCU, invalid values, legacy key, expansion;
  - the Policies page model.
- **Release:** `release.yml` attaches `RemoteHub.admx`, `RemoteHub.adml` and `RemoteHub-PolicyTemplates-<version>.zip`, each
  with a `.sha256`, and fails when a template is missing or isn't valid XML.
- **Checked with the `admx-adml-generator` skill's validator** (`validate_admx.py`, 2026-10-16). It reports no problems
  except one error and one warning, and both are gaps in the validator, so the files stay as they are:
  - *Error: ADML namespace is `http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions`, expected
    `http://www.microsoft.com/GroupPolicy/PolicyDefinitions`.* The namespace is Microsoft's own: 208 of the 225 ADML files
    Windows ships in `%WINDIR%\PolicyDefinitions\en-US` use it, and only 7 use the other one. Windows loads both.
  - *Warning: `Cat_Console_Help` and `Cat_Agent_Help` are never referenced.* They are the `explainText` of the two main
    categories, which the validator doesn't read. Windows' own templates use category `explainText` too (25 of them).
  - When the validator runs again later, these two are expected. Anything else it reports is a real finding.
- **Not run yet:** POL-01 (the template in `gpedit.msc`) and POL-11 (the Policies page in the lab), because the lab was in
  use.

## 17. Implementation notes (Phase 2)

What was built, and where it differs from §9, §8 and §11.3:

- **New agent values** in `PolicyCatalog` and `AgentPolicy`, since 0.18.12:
  - `PowerShellEnabled`, `AllowedGroups`, `VirtualDisplay`, `PrivacyScreenMessage`, `ListenPort`, `LogRetentionDays` and
    `MaxSessions`.
  - `VirtualDisplay`, `ListenPort`, `LogRetentionDays` and `MaxSessions` fall back to agent.json, and `AllowedGroups` to
    `allowedGroups`.
  - `AgentPolicy` is a record now, so `Load` can add `Problems` and `Managed` to what `From` returns.
- **Where they're read:**
  - `AgentHost`: `PowerShellEnabled` before the sign-in, answering 404, and `MaxSessions` at its three checks
    (`TryStartSession`).
  - `AgentLog.RetentionDays`: at each daily cleanup.
  - `DesktopEndpoint`: `VirtualDisplay`, and the privacy heading through `HelperInit.PrivacyHeading` →
    `IHelperUi.SetPrivacyHeading`. The heading travels from the service to the helper only, never from RemoteHub.
  - `Program`: `ListenPort` at start (when the policy key can't be read, agent.json's port), then
    `Enrollment.SetFirewallPort`.
- **Firewall:** `netsh advfirewall firewall set rule … new localport=` keeps the rule's address scope. The current port
  is read from `show rule`. On a Windows with non-English netsh output it can't be read, so the rule is set at every
  start, which is harmless.
- **Invalid values:** written to the log once per value and service run ("Policy MaxSessions: 0 is outside 1–200 –
  ignored, the default applies").
- **`RemoteHubAgent.exe policy`:** `PolicyCommand`. Every agent value, grouped like the template, with `[policy]`,
  `[agent.json]` or `[default]`, invalid values flagged, and a warning when the key can't be read.
- **Health instead of `Hello` (§8):**
  - The device's managed names go into `AgentHealth.Managed`, because RemoteHub's install and update dialog needs them,
    and that dialog has no desktop session.
  - Health needs no sign-in, so it carries only three action names (`autoSignIn`, `virtualDisplay`, `port`), never
    values or other policies. Its features also leave out `powershell` when `PowerShellEnabled` = 0.
- **What RemoteHub does with them:**
  - The install dialog greys out *Port* when the device's policy sets it.
  - *Virtual display* and *Allow automatic sign-in* stay changeable, because they install or remove a driver or the sign-in
    component, which the policy can't do. Their hints say that the device's policy decides when they are used.
  - A 404 on `/v1/powershell` is explained as "turned off on the device (PowerShellEnabled)".
- **Automatic sign-in out of the policy key (§11.3):**
  - `AutoSignInSetup.On` and `Off` write `noUserBehavior` and `autoSignInGroups` into agent.json. Their answers say when a
    policy decides instead.
  - The running service reads the two values from agent.json again for every connection (`AgentConfig.WithCurrentAutoSignIn`),
    because *Install credential provider* doesn't restart it.
  - `Migrate` runs at service start and before `On` and `Off`:
    - values still as the old version wrote them move to agent.json;
    - values IT changed stay in the policy key;
    - values removed since aren't brought back;
    - the marker is deleted after.
- **Template:** revision 1.1, 39 policies, a new category *Features*, and supported on *RemoteHub Agent 0.18.12 or later*.
  The rights' help texts name *Default groups for every right*. The skill's validator again reports only the two known
  gaps (§16).
- **Tests:**
  - `AgentPolicyPhase2Tests` (13): new values and fallbacks, `AllowedGroups`, automatic sign-in with policy precedence,
    `Refused`, health names, the `policy` report, the netsh parsing, `On`/`Off`, live re-read, and the migration (moved,
    kept by IT, nothing to do).
  - `PolicyTemplateTests` also checks `expandable` and `maxLength` of text elements against the catalogue.
- **Not run yet:** POL-02 to POL-05, POL-08 and POL-09 in the lab.

## 18. Implementation notes (Phase 3)

What was built, and where it differs from §5, §4.2 and §11.2:

- **Values:** 29 `Allow…` DWORDs in `PolicyCatalog` (`FeatureEntries`, plus `AllowSharedSettings` under *Settings storage*),
  since 0.18.12. `AllowConnectionEditing`, `AllowSavedPasswords` and `AllowPasswordCopy` are new behaviour and stay in
  phase 5.
- **`Features`** (`src\RemoteHub\Services\Features.cs`): `IsAllowed(Feature)`, `IsAllowed(RemoteTool, viaAgent)`,
  `IsAllowed(ShareProtocol)` and `WhyNot`, which names the switch that blocks ("The RemoteHub Agent is turned off by your
  organisation – see Settings → Policies."). A main switch at 0 wins over its features. `DisableRecording` and
  `DisableAgentAutoSignIn` go through it too; automatic sign-in and jump hosts are below `AllowAgent`.
- **Transport:** with `AllowAgent` = 0, `Inheritance.UsesAgent` is false, so the tools of a connection set to the agent go
  over WinRM, and *Remote desktop through* doesn't count, so the desktop is RDP. With `AllowAgentDesktop` = 0 the same for
  the desktop only.
- **Hidden:** the tree's menu, the *+*, tab group and tab menus (`AddConnectionTools`), the detached window's tools, the
  toolbar's and the PowerShell tab's *File transfer*, the *New* menus (network share, *Import .rdp files…*), *Save as .rdp
  file…*, *Move under* (also on a share; with jump hosts blocked it stays only to move an item out of one), the share
  menu's *Open*, and Settings → *Export backup…* / *Import backup…* (with both gone, the *Backup* section too). With no
  tool allowed over a transport, its test isn't offered either. Separators follow what is left, and a *+* menu with
  nothing left says "Nothing else can be opened". The quick connect hints go when it is blocked.
- **Greyed out:** the editor's *Check automatically after RDP login* and *Use HTTPS* (`AllowPowerShellRemoting`),
  *Connect through* (`AllowAgent`), *Remote desktop through* and the automatic sign-in rows (`AllowAgentDesktop`); the
  install dialog's *Install silently over PowerShell remoting* and *Allow automatic sign-in*; *Shared folder* in the
  settings location window.
- **Refused where it starts:** `MainWindow` checks before it asks for a password. Opening something new shows a dialog
  titled by its topic (`Features.Topic`: "Remote desktop", "RemoteHub Agent", "Network share" …) with `WhyNot`: `OpenRdp`,
  `OpenAgentDesktop`, the tool tabs, quick connect, the remoting test and setup, the agent and credential provider
  installs, shares, `.rdp` import (and RDP when a `.rdp` file is opened), `.rdp` export, backups, and dragging an item
  below a connection. `TryRoute` refuses a connection below a jump host. A reconnect of an open tab fails the tab with
  the reason instead, as any other failure to connect does. The sessions check again: `RdpSession`,
  `AgentDesktopSession`, `PsSession`, `RemoteToolSession` (each tool knows its `RemoteTool`), and `ShareConnector.Open`
  throws.
- **Transport of a tab's tools:** everything that offers file transfer from a PowerShell tab (toolbar, the tab's own
  button, its menus, the detached window) decides by the connection's transport now (`UsesAgent`), as
  `OpenFileTransfer` does.
- **Not greyed out yet:** the RDP rows of the connection editor with `AllowRdp` = 0 (the agent desktop uses some of them),
  and the share editor with a protocol blocked.
- **Differs from §11.2:** there are no restored tabs to refuse (RemoteHub doesn't restore tabs), no status bar for a
  refused shortcut (the dialog says it), and no *Copy password* action yet (the prompt copy is phase 5's
  `AllowPasswordCopy`). The Policies page lists a value below a main switch that is off with *Off because "…" is off*;
  conflicts with §7's connection values come with phase 4.
- **Template:** revision 1.2, 68 policies, a category *Features* under *RemoteHub Console* with *RDP*, *RemoteHub Agent*
  (the old *RemoteHub Agent features*, moved under it), *PowerShell remoting*, *Connections*, *Jump hosts and network
  shares* and *Import and export*; supported on *RemoteHub 0.18.12 or later*.
- **Tests:** `ConsoleFeatureTests` (12): catalogue ↔ switches, nothing set, own value, the `Disable…` values, both main
  switches, a tool over the other transport, shares, the Policies page note, `UsesAgent`, a tool tab refusing in the tab,
  and `ShareConnector`. They set `AppPolicy.Source` and run in a collection of their own without parallel tests.
- **Not run yet:** POL-14 (the console features in the lab) – not automated yet.

## 19. Implementation notes (Phase 4)

What was built, and where it differs from §6, §7 and §11.2:

- **Values**, since 0.18.12: 15 settings of §6 in the root key (`Theme`, `SwitchKeys`, the four recording values,
  `ProcessRefreshSeconds`, `ServiceRefreshSeconds` and the seven `AgentDesktop…` values), 7 install defaults in
  `AgentInstall` (§6.9) and the 22 connection settings in `Connections` (§7). `PolicyEntry.Check` now also takes a fixed
  set of numbers (`ColorDepth`, `RecordingFps`).
- **`DisableUpdateCheck` and `DisableTelemetry` = 0** lock the feature on (Q3): the template's *Disabled* writes 0 now
  instead of deleting the value, and `UpdateService.CheckEnabled` / `Telemetry.Enabled` take `AppPolicy.UpdateCheck` /
  `AppPolicy.Telemetry` over the user's switch.
- **Connections (§7):** `AppPolicy.Connections()` returns a partial `RdpOptions` (words → the enum, 0/1 → bool; a
  `FixedResolution` that isn't `<w>x<h>` is ignored), read again after 2 s at most because it's asked for every row of the
  tree. `Inheritance.ResolveOptions` applies it last (`RdpOptions.OverrideWith`), so the RDP session, `.rdp` export, the
  agent desktop and the tree all use it. `UseAgent` by policy also decides below a jump host.
- **Editor:** the `Row` helper finds the property a row edits by running its setter on an empty `RdpOptions`, so each
  locked row shows the policy's value at every level (global defaults, folders, connections) with "Set by your
  organisation", and `Apply` skips it (`OptionRow.FixedByPolicy`), keeping the stored value (§4.3).
- **Conflicts:** Settings → Policies shows a connection value that asks for a blocked feature in amber
  (`PolicyRow.Conflict`): `RemoteDesktopVia` = Agent or Rdp, `UseAgent`, `AgentAutoSignIn`, `PsRemoting`, `PsUseSsl`.
  With `RemoteDesktopVia` = Agent by policy and the agent desktop blocked, opening a connection is refused instead of
  falling back to RDP.
- **Where they're locked:** Settings → General (theme, keys), *Updates & privacy* (on by policy: only the switch),
  *Recordings*, *Global defaults → Remote tools*, the agent desktop's start panel (picture, consent, view only) and
  toolbar (picture, resolution, details, key combinations), and the install dialog (methods greyed out, not removed;
  port, groups, firewall scope, virtual display, automatic sign-in, *Use the agent afterwards*).
- **New behaviour behind §6.8:** `AgentDesktopViewOnly` = 1 also forces view only in `AgentDesktopSession.Choose`, and
  `AgentDesktopClipboard` caps the clipboard level at the lower of it and the device's. `AgentDesktopConsent` applies only
  when the device offers that mode. `AgentDesktopFitToWindow` picks *Fit to this window* or *The device's own*; a fixed
  size can't be set by policy.
- **Checks:** `PolicyEntry.Rule` rejects a `FixedResolution` that isn't `<w>x<h>` and a `FirewallScope` that isn't an
  address list (";" is taken like ","), so Settings → Policies shows them as invalid. The install dialog can't start when
  none of the methods the policy allows is possible for the connection. The clipboard cap applies both ways: text from
  the device is dropped too, since the device doesn't know this PC's policy. *Settings overview* shows the values in
  effect (theme, keys, refresh intervals, and the global defaults with the connection settings applied).
- **Not done:** `RemoteDesktopVia` = Agent by policy doesn't take away *Remote desktop via RDP* from the menus (only
  `AllowRdp` = 0 does); the policy fixes what *Remote desktop* opens, as the connection setting does.
- **Left for phase 5:** `AutoLockMinutes`, `SettingsLocation` and `StartupProtection` / `AllowWindowsHello` – they are
  read at start and belong to startup protection and the settings store, which phase 5 changes anyway.
- **Template:** revision 1.3, 112 policies; new categories *General*, *Remote tools*, *Agent remote desktop*, *Agent
  installation* and *Connection settings* with *Display*, *Devices and resources*, *Security* and *Remoting and agent*.
- **Tests:** `ConsoleSettingsPolicyTests` (11): precedence over connection, folder and defaults; invalid values and the rules; a copy per caller;
  `UseAgent` below a jump host; the editor's locked row and `Apply`; lock coverage (every connection value has a row that
  locks it, every §6 value a reader in `AppPolicy`); the lock-on values; recordings; and the Policies page conflict.
  `PolicyTemplateTests` now tells the agent's key from the console's `AgentInstall` next to it, and checks that a number box starts inside its range.
- **Not run yet:** POL-15 (connection settings and settings in the lab) – not automated yet.
