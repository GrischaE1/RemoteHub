# RemoteHub Agent – Automatic sign-in (credential provider)

Status: Draft v3 (2026-10-14) – updated after the design review and checked against the code of 0.18.8, which adds privacy mode ([privacy-mode-design.md](privacy-mode-design.md), 0.18.3) and jump hosts ([jump-host-design.md](jump-host-design.md)). All questions in §15 are decided (Q5 changed, Q7–Q9 new, 2026-10-14). Ready for implementation and the separate security review asked for in [agent-desktop-draft.md](agent-desktop-draft.md) §4.4.
Owner: Grischa Ernst

Covers the backlog item "automatic sign-in with stored credentials (credential provider)" from [agent-spec.md](agent-spec.md) §12 and decision 5 in [agent-desktop-draft.md](agent-desktop-draft.md) §2.

**Changes in v3:**
- Privacy mode now exists. A session created by automatic sign-in starts with the privacy screen on (§9.1), and T6 is now handled in v1.
- Automatic sign-in needs a control connection (not view-only) and the `SecureDesktop` right as well as `AutoSignIn` (§5, §6.2).
- The session state is checked again when the provider claims the ticket (§6.2, §6.4).
- The design now says how many pipe instances there are and which LogonUI may connect (§7.1, §7.2).
- When a connection drops, the helper and its privacy screen stay up for 2 minutes so the operator can resume. After that the session is always signed out (§9.2).
- New device policy `AutoSignInRequirePrivacyScreen` (§5, §9.1).
- `SignInRequest` gets its own receive path, because the existing `ReceiveWs` copies every message three times (§10.1).
- The consent decision after sign-in compares SIDs, not names (§6.3).
- The attempt limit is a separate counter keyed by SID, so operators behind the same jump host don't share it (§6.2).
- Audit IDs changed: 1104, 1105 and 2103 are now used by privacy mode (§11).
- Jump hosts are covered in §3, §4 and §10.2.

## 1. Goal

When the operator opens the agent desktop on a device where **nobody is signed in on the console**, RemoteHub can sign in with the connection's stored credentials. The operator then lands on the desktop without typing the password into the sign-in screen.

This is the most sensitive part of the agent. A credential provider runs inside `LogonUI.exe` as LocalSystem and decides who gets an interactive session on the device. A bug here can:
- let an attacker sign in;
- leak passwords;
- break the sign-in screen for everyone.

### 1.1 Security baseline

**Automatic sign-in must not allow anything that an operator with the `SecureDesktop` right couldn't already do by typing the same password into the sign-in screen through the agent desktop, in a control (not view-only) connection.**

Each rule below is there to keep that true. Every design choice is judged against this baseline. The feature adds convenience and must not add reach.

## 2. Non-goals (v1)

- **Unlocking a locked session.** Only `CPUS_LOGON` is supported, not `CPUS_UNLOCK_WORKSTATION`. Unlocking could come later in phase 2 (§12), and only for the session of the same account.
- **Signing in as a different account** from the one that authenticated the agent connection.
- **RDP sessions' sign-in screens and Remote Desktop Services servers.** Only the physical console session is supported. The provider is still loaded into every `LogonUI.exe`, so it must stay inert outside the console session (§7.2, §8.2).
- **Storing anything on the device:**
  - no Winlogon `AutoAdminLogon` / `DefaultPassword` (that's an LSA secret, readable by any admin, and persistent);
  - no LSA secret;
  - no file and no registry value.
- **Changing an expired password, smart card, Windows Hello and FIDO.** A sign-in that needs any of these fails, with a clear reason.
- **Credential UI prompts** (`CPUS_CREDUI`), changing the password (`CPUS_CHANGE_PASSWORD`) and a credential provider filter. The provider never hides or wraps other providers.

## 3. Threat model

| # | Attacker | Goal | Main defences |
|---|---|---|---|
| T1 | On the network, no credentials | Trigger a sign-in, or get passwords | Pinned TLS, Windows sign-in, new right `AutoSignIn` (default: nobody), policy off by default, attempt limit per account (§5, §6) |
| T2 | Operator with a desktop right but not `AutoSignIn`, or connected view-only | Sign in without the right, or gain control through the sign-in | The rights and the view-only flag are checked in the service, not trusted from RemoteHub (§6.2) |
| T3 | Operator with `AutoSignIn` | Sign in as **another** account, or into an occupied session | Only the account that authenticated the connection (same SID); console must be in the `SignIn` state when the ticket is claimed; never ends or takes over a session (§6.3) |
| T4 | Local user without admin rights on the device | Inject credentials into the provider, read them, or squat the pipe | Pipe owned by the service and created at start (`FIRST_PIPE_INSTANCE`), SYSTEM-only DACL, remote clients rejected, both ends check the peer process, one-shot ticket (§7) |
| T5 | Local user / malware without admin rights | Replace or hijack the provider DLL | Program Files ACLs, signed DLL, no DLL dependencies outside System32, fixed CLSID under HKLM (§8.4) |
| T6 | Person at the device | Use the admin session that was just signed in | Privacy screen with local input blocked, on from the start and kept through a dropped connection (§9.1, §9.2); no screen snips while it is up (§9.1); the end action when the connection ends, and **sign-out** 2 minutes after a drop, including after a crash (§9.2, §9.3) |
| T7 | Memory disclosure (dump, crash report, pagefile) | Read the password | Password kept only in pinned buffers, protected with `CryptProtectMemory`, zeroed after use, never in a .NET `string`, never in logs (§10). Plaintext left in the TLS and Kestrel buffer pools is the same as for the Basic sign-in today and is accepted (§10.1). |
| T8 | Compromised RemoteHub PC | — | Out of scope: it already has the stored passwords. Automatic sign-in adds nothing for it. |
| T9 | Local admin or SYSTEM on the device | — | Out of scope: they already control the device. |
| T10 | Bug or crash in the provider | Sign-in screen broken for everyone (denial of service) | Minimal native code, no blocking calls on LogonUI threads, input fuzzed, inert unless a ticket is pending, install is opt-in, policy kill switch (§8) |
| T11 | Compromised jump host on the route | Read the password, or trigger a sign-in | Agent TLS is end to end to the target's pinned certificate ([jump-host-design.md](jump-host-design.md) §1.1 rule 3), so the jump host sees neither the Basic sign-in nor `SignInRequest`. It can only drop or delay traffic. |

## 4. Architecture

```
RemoteHub (operator PC)               Device
┌───────────────────────┐ pinned TLS  ┌──────────────────────────────────────────────┐
│ AgentDesktopSession    │ /v1/desktop │ RemoteHubAgent service (LocalSystem)          │
│  Hello.Rights.AutoSignIn│──────────▶ │  DesktopEndpoint                              │
│  SignInRequest (binary)│ (directly or│   ├ right + policy + session-state checks       │
│  ◀ SignInResult        │ ◀── through │   ├ SID check: same account as the Basic sign-in│
└───────────────────────┘  a jump host)│   └ AutoSignInBroker (one ticket per device)    │
                                     │        │ \\.\pipe\RemoteHubAgent.AutoSignIn      │
                                     │        │ SYSTEM-only, local only, peer-verified  │
                                     │        ▼                                         │
                                     │  LogonUI.exe (console session, SYSTEM)          │
                                     │   └ RemoteHubCredentialProvider.dll (C++)       │
                                     │       KERB_INTERACTIVE_UNLOCK_LOGON → Winlogon/LSA │
                                     │                                                  │
                                     │  RemoteHubAgent.exe desktop (helper) – never     │
                                     │  sees the credentials                            │
                                     └──────────────────────────────────────────────┘
```

The credentials go along exactly one path: RemoteHub → service memory → pipe → provider → LSA. Through a jump host, the TLS stream is only forwarded by the jump host's tunnel and stays encrypted to the target.

They never touch:
- the desktop helper, which processes untrusted remote input;
- the jump host;
- disk or the registry;
- logs;
- the WebSocket JSON serializer, or `ReceiveWs`'s message buffers (§10.1).

## 5. Enabling: four switches, all off by default

Automatic sign-in works only if **all** of these are on.

| Layer | Switch | Default | Why |
|---|---|---|---|
| Install | MSI property `AUTOSIGNIN=1` (also in the `.rhagent` file / install dialog). Without it, the provider DLL **isn't installed or registered** at all. | off | A device that never uses it has no extra code in LogonUI. |
| Device policy | `HKLM\SOFTWARE\Policies\RemoteHub\Agent` `NoUserBehavior` = `AutoSignIn` (REG_SZ). Missing / `SignInScreen` / unreadable → off. | off | IT decides per device (ADMX later). Read for every request. |
| Right | `Rights\AutoSignIn` (REG_MULTI_SZ, group SIDs). | **nobody** (like `ConsentBypass`) | Separate from `SecureDesktop`, so seeing the sign-in screen doesn't imply signing in automatically. |
| Client | Per connection: *Sign in automatically when nobody is signed in* (inherited like the other agent settings, default off). App policy `DisableAgentAutoSignIn` (DWORD 1) in `HKLM/HKCU\SOFTWARE\Policies\RemoteHub` hides it. | off | The operator decides per connection. RemoteHub never sends the password for this unasked. |

**Prerequisites, not switches** (decided, §15 Q9): the account must also hold the `Desktop` right (control) and the `SecureDesktop` right, and the connection must not be view-only. That's the §1.1 baseline: without them the operator couldn't type the password into the sign-in screen either.

**Optional device policy** (decided, §15 Q7): `AutoSignInRequirePrivacyScreen` (DWORD, default 0). With 1, automatic sign-in is off on a device that can't show the privacy screen (policy `PrivacyScreen` = 0, or Windows build < 19041) (§9.1).

- **Set at install, or by IT:** the policy and the right can come from Group Policy / Intune, or from the agent itself.
  - The agent writes them when installed with `AUTOSIGNIN=1`: the install dialog's *Allow automatic sign-in (installs a credential provider)*, or RemoteHub's *Install credential provider*. Both run `RemoteHubAgent.exe auto-sign-in on`, which sets `NoUserBehavior = AutoSignIn` and `Rights\AutoSignIn` = the agent's allowed groups (`agent.json`).
  - A value that already exists is never changed, so IT's policy wins.
  - What the agent wrote is recorded in `%ProgramData%\RemoteHub\Agent\autosignin.policy`. `auto-sign-in off` (`AUTOSIGNIN=0`, *Remove credential provider*, uninstall) removes exactly that, and only while it is unchanged.
  - The install dialog offers it only for the silent ways (WinRM, through the agent): a hand-run MSI needs `AUTOSIGNIN=1` on its command line.
- **Kill switch:** Windows' own policy *Exclude credential providers* (`ExcludedCredentialProviders`) with our CLSID disables the provider on the device without uninstalling.
- **Policy cannot be read:** fail closed. `AgentPolicy.Refused` already turns everything off (including `PrivacyScreen` and `TunnelEnabled` since 0.18.3), and `AutoSignIn` must be included there too.

## 6. Protocol and service-side checks

### 6.1 Protocol changes (`Desktop/DesktopProtocol.cs`, protocol version stays 1, feature-flagged)

- `DesktopRights` is `DesktopRights(Control, SecureDesktop, ConsentBypass, PrivacyScreen = false, PrivacyInputBlock = false)` today. It gets `bool AutoSignIn = false` at the end, so older RemoteHub builds still read it.
  - It's true only if the policy is on, the right is granted, `Control` and `SecureDesktop` are true, the provider is registered and signed, and the pipe is up. With `AutoSignInRequirePrivacyScreen` = 1, the privacy screen must also be available.
  - RemoteHub offers the option only when it's true.
- New feature string `DesktopFeatures.AutoSignIn = "autoSignIn"` in Hello. It's added like `DesktopFeatures.PrivacyScreen`, only when the device can do it.
- `DesktopStart` gets `bool AutoSignIn`. It's the operator's intent, and it's valid only when the selected session is the console in state `SignIn` and `ViewOnly` is false.
- `DesktopStart` also gets `string? AutoSignInEndAction` (`Lock` | `SignOut`), the connection's setting (§9.2). The device policy wins when it's set; anything else, or nothing, means `Lock`.
- New message `SignInRequest` (RemoteHub → agent):
  - binary, **not JSON**: `[type][u16 len][UTF-16LE password bytes]`, at most 1 KB in total;
  - no user name, because the agent uses the account that authenticated (§6.3);
  - sent only after the agent asks for it with status `SignInReady`. RemoteHub never sends it on its own.
- New status states:
  - `SignInReady`: the agent is ready; RemoteHub now sends `SignInRequest`.
  - `SigningIn`.
  - `SignedIn`: the provider reported success and the new session's SID matched (§6.3).
  - `SignInFailed`: carries a reason code (wrong password, account locked, password expired, logon type not granted, restricted hours, smart card required, session changed, timeout, …). The message is generic and never echoes input.
- `AgentPolicy.Refused` and the defaults include `AutoSignIn` = nobody.

> Why the password is sent a second time instead of keeping it from the Basic header (decided, §15 Q4): the agent doesn't keep the Basic sign-in password after `LogonUser`. Keeping it for every connection "just in case" would put every operator's password in memory for the whole session. Sending it again only on explicit request keeps the exposure to a few seconds, and only for connections that use the feature.
>
> Baseline to keep in mind: the Basic sign-in already brings the same password into the service as a .NET `string` on every connection (`AgentHost` `TryBasic` → `WindowsAuthenticator.Authenticate` → `LogonUser` with `LOGON32_LOGON_NETWORK`), which can't be zeroed. The careful handling of `SignInRequest` (§10.1) keeps the *additional* copy short-lived; it doesn't lower exposure below that baseline.

### 6.2 Checks in `DesktopEndpoint` (all in the service; RemoteHub's word is never trusted)

The service checks these **in order**. Any failure → `SignInFailed`, an audit entry, and the connection continues on the plain sign-in screen.

1. `policy.NoUserBehavior == AutoSignIn` and `identity.InAny(policy.Groups(AgentRight.AutoSignIn))`, read again at request time. With `AutoSignInRequirePrivacyScreen` = 1, the privacy screen must also be available (`PrivacyScreenSupported` and policy `PrivacyScreen` ≠ 0).
2. The connection is a control connection (`control` true and `_viewOnly` false) and the account holds `SecureDesktop` (`_secureRight`). Today `DesktopEndpoint` already refuses the sign-in screen without `SecureDesktop` (≈ line 126). The view-only part is new.
3. The provider is registered (HKLM key present, CLSID matches, signature check passed, §8.4) and the broker pipe was created successfully at service start.
4. Session checks:
   - the selected session is the **current console session** (`WTSGetActiveConsoleSessionId`);
   - its state is **`SignIn`**: no user, `LogonUI` running in it;
   - it's not `Locked` or `Disconnected`, and no user is signed in on the console.
   - Use the agent's existing session detection (`DesktopServices`, the `LogonUI` check since 0.15.11). On Windows 11 and Server 2025 the "Press Ctrl+Alt+Delete" screen runs on the **Default** desktop, not Winlogon's, so the desktop name alone isn't a reliable signal.
5. The authenticated account has **no other session on the device** (`WTSEnumerateSessions`, compared by SID). On client Windows, signing in on the console would take over an existing RDP session of that account and disconnect the person using it. → Refused with "This account already has a session on the device – connect to that session instead".
6. Only one pending ticket per device (§7.3). A second request while one is pending → refused.
7. At most one attempt per desktop connection. At most 3 attempts per account per 15 minutes. This protects against account lockout when the stored password is out of date.
   - The counter is a **new, small class** (`AutoSignInGate`), keyed by the account SID, not by source address. The existing `SignInGate` (`AgentHost.cs`) counts failures and parallel attempts per source address for the Basic sign-in. Through a jump host every operator has the jump host's address as their source, so a per-source limit would let one operator's bad password block everyone else.
8. The `SignInRequest` arrives within 30 s of `SignInReady`. Otherwise the request is cancelled.

**Checked again when the ticket is claimed (§7.3):** checks 1, 4 and 5 run once more when the provider's `Claim` arrives, before the ticket is handed out. That can be up to a minute after `SignInRequest`, and someone at the device may have signed in in the meantime. Cancelling the ticket on a state change (§7.3) stays as a second line, but it depends on noticing the change in time, so it isn't enough on its own.

### 6.3 Which account – exactly the authenticated one

- `WindowsAuthenticator.Authenticate` already holds the logon token. Keep the account **SID** in `ShellIdentity` as its own property (`UserSid`). Today it's only mixed into `GroupSids` (`WindowsShell.cs` appends `identity.User` to the group list).
- Keep the name as Windows resolved it, `DOMAIN\user`, which `ShellIdentity.Account` already holds (`WindowsIdentity.Name`). For local accounts it's `COMPUTERNAME\user`.
- The ticket carries that domain and user. The client can't choose it.
- After the provider reports success, the service reads the new console session's user SID (`WTSQueryUserToken` → `TokenUser`). If it doesn't match: **log off that session immediately** (`WTSLogoffSession`), audit error 3101, and end the connection. This is a defence against bugs and races; it should never happen.
- **Consent after the sign-in is decided by SID.** Today `IsSelf` / `SameUser` compare names (`DesktopEndpoint` ≈ line 603). After an automatic sign-in, the service sets `_allowedBy` from the session whose SID it just checked. `IsSelf` gets a SID comparison for sessions whose SID is known, so a name written another way (UPN, short domain name) can't lead to a different decision.
- Restrictions are **not bypassed** and simply fail the sign-in: "Deny log on locally", "Allow log on locally", logon hours, workstation restrictions, account disabled/locked, `Protected Users`, smart card required, Windows Hello for Business-only accounts, and the legal notice (`LegalNoticeText`).
  - The legal notice is shown before the providers. The operator dismisses it through the remote desktop like a local user would. The agent never clicks it away.

### 6.4 Flow

```
RemoteHub                    Service                                  LogonUI + provider
Start{session=console, AutoSignIn, !ViewOnly}
                             checks 6.2 (1–6)
                             helper starts on secure desktop (as today, HoldUserDesktop)
            ◀── Status SignInReady
SignInRequest(pw) ─────────▶ checks 6.2 (7–8); ticket = {SID, domain, user,
                             pw (protected), session, expires +60 s, nonce}
                             _screen = privacy screen (§9.1)
                             SAS if Ctrl+Alt+Del is required (SecureAttention.Send)
                             Pending(nonce, session) over the pipe ───▶ (pipe thread)
                                                       ◀── pipe: Claim(nonce, session)
                             checks 6.2 (1, 4, 5) again
                             verify peer, hand out the ticket once ──▶ Ticket kept protected,
                             zero the service copy                       then CredentialsChanged()
                                                                       GetCredentialCount: 1, auto-logon
                                                                       GetSerialization:
                                                                       KERB_INTERACTIVE_UNLOCK_LOGON
                                                                       zero the buffers
                                                       ◀── pipe: Result(NTSTATUS)  (ReportResult)
                             verify the new session's SID
                             record session in autosignin.json (§9.3)
            ◀── Status SignedIn / SignInFailed(reason)
                             helper follows the console (existing code); _allowedBy from the SID check;
                             privacy screen sent to the new helper (existing code, §9.1)
```

- **Ctrl+Alt+Del:** since 0.15.7 the agent sets `SoftwareSASGeneration = 1` for as long as it's installed (marker `sas.agent`), so the service's `SendSAS` is accepted without a policy change at sign-in time.
- **Consent after sign-in:** the new session belongs to the operator's own account. The existing code already continues without asking when the account that signed in is the operator's own (`DesktopEndpoint` ≈ line 253), and `HoldUserDesktop` releases the picture for this user only. The one change is that the decision uses the SID (§6.3).
  - If anyone else's desktop shows up (a race, fast user switching), the existing ask/PIN rule applies unchanged.

## 7. Local channel: service ⇄ provider

### 7.1 The pipe

- **Name:** `\\.\pipe\RemoteHubAgent.AutoSignIn`.
- **Created when the service starts and before it listens on the port:**
  - The first instance is created with `FILE_FLAG_FIRST_PIPE_INSTANCE`, so nobody can create it first (pipe squatting). If that fails, the feature is off until the service restarts, and that is logged (3102).
  - **Instances:** `nMaxInstances` = 4. After a client connects, the service creates the next instance at once, so one connected or stuck client never leaves the console's LogonUI without a free instance. A client that fails the peer check (§7.2) is disconnected immediately and its instance is reused.
  - Only one connection is **active**: the one from the console session's LogonUI. If a second client passes the peer check for the same session (for example LogonUI was restarted), the newer one replaces the older one.
- **DACL:** `D:P(A;;GA;;;SY)`. SYSTEM only: no Administrators, no interactive users, no `Everyone`. `PIPE_REJECT_REMOTE_CLIENTS`. The owner is SYSTEM.
- **Mode:** message mode and a fixed maximum message size (4 KB). Anything larger, malformed or out of order closes the connection.

### 7.2 Peer checks (both ends)

**The service checks the client** (`GetNamedPipeClientProcessId`, `GetNamedPipeClientSessionId`):
- the image is `%SystemRoot%\System32\LogonUI.exe`, checked through `QueryFullProcessImageNameW` and compared case-insensitively against the real system path, not a string from the client;
- the process token is SYSTEM;
- the client session is the **current console session**. When a ticket exists, it must also be **the ticket's session**.

**The provider checks its own session first:** it connects only when its process runs in the console session (`ProcessIdToSessionId` == `WTSGetActiveConsoleSessionId`). LogonUI in an RDP session (Server, RDS) never opens the pipe and stays inert. Without this, a sign-in screen in an RDP session would take up pipe instances.

**The provider checks the server** (`GetNamedPipeServerProcessId`):
- the PID equals the PID of the service `RemoteHubAgent`, from `QueryServiceStatusEx` through the SCM;
- the service's image path is the installed `RemoteHubAgent.exe` in Program Files;
- the provider connects with `SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION`, so the server can't impersonate the provider beyond identification.

### 7.3 The ticket

- **Single use.** After `Claim` the service deletes it, whether the sign-in then succeeds or not.
- **Bound to:**
  - the session id;
  - a 128-bit random nonce, which the service sends with the "pending" notification and the provider must echo;
  - an expiry of 60 s after `SignInRequest`.
- **Checked again at `Claim`:** §6.2 checks 1, 4 and 5. If any fails, the ticket is deleted without being sent, and the operator gets `SignInFailed` ("session changed").
- **Cancelled when:** it expires, the RemoteHub connection ends, the console session changes or leaves the `SignIn` state, the policy is turned off, or the service stops.
- **Messages:** `Pending(nonce, session)` → `Claim(nonce, session)` → `Ticket(nonce, domain, user, SID, password)` → `Result(nonce, NTSTATUS, substatus)`. No other message types exist. The provider can't ask for anything else, and the service never sends a ticket without a `Claim`.
- **Wire format** (`src/RemoteHub.CredentialProvider/Wire.h`): `[u8 version = 1][u8 type][u16 payload length][payload]`, little-endian, one message per pipe message. Strings are a `u16` length in UTF-16 units followed by the units: names at most 256, the password at most 256. A message that doesn't parse exactly (wrong version or type, a length that doesn't match, bytes left over, an empty user or password) closes the connection. A parsed message written back is byte-for-byte the same, which the fuzzer checks.

## 8. The credential provider (new native project)

### 8.1 Project

- **`src/RemoteHub.CredentialProvider`:**
  - C++20, `vcxproj`, x64 (plus ARM64 if the agent ever ships ARM64);
  - static CRT (`/MT`);
  - no dependencies beyond Windows system DLLs (`secur32`, `credui` only if `CredPackAuthenticationBuffer` is used, `advapi32`, `kernel32`, `wtsapi32`).
- **Written from scratch** following the structure of Microsoft's *V2 Credential Provider Sample*: one credential and no editable fields. No sample code is copied, so nothing goes into THIRD-PARTY-NOTICES.
- **Configurations:** `Release` is the only DLL that goes into the MSI. `Lab` (`RemoteHubCredentialProvider.Lab.dll`, define `RHCP_LAB_SERVER`) accepts the phase 1 test server `RemoteHubCpTestServer.exe` from the same folder instead of the agent service. It's built only by `build-credential-provider.ps1 -Configuration Lab`, used only on lab VMs, and never packaged.
- **Build:** `build/build-credential-provider.ps1` (MSBuild from the Visual Studio C++ tools, found through vswhere; not part of `RemoteHub.sln`, which `dotnet build` can't build with C++ in it). It runs the unit tests in `tests/RemoteHub.CredentialProvider.Tests` and, with `-FuzzSeconds`, the libFuzzer target (configuration `Fuzz`, `/fsanitize=fuzzer,address`). `/Qspectre` needs the Spectre-mitigated libraries (component `Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre`).
- **The server check without extra paths:** the provider expects the agent's exe **next to its own DLL** (both in `Program Files\RemoteHub Agent`), so it needs neither the registry nor a known-folder lookup to find it.
- **Compiler and linker hardening:**
  - `/guard:cf`, `/guard:ehcont`, `/CETCOMPAT`, `/DYNAMICBASE`, `/HIGHENTROPYVA`, `/NXCOMPAT`;
  - `/sdl`, `/W4 /WX`, `/Qspectre`;
  - SAL annotations and code analysis (`/analyze`) in CI.
- **`DllMain` does nothing** beyond `DisableThreadLibraryCalls`. No loader-lock work.

### 8.2 Behaviour

- **`SetUsageScenario`:**
  - `CPUS_LOGON` → active, but only in the console session (§7.2). In any other session it behaves as if no ticket is ever pending.
  - every other scenario (`CPUS_UNLOCK_WORKSTATION`, `CPUS_CREDUI`, `CPUS_CHANGE_PASSWORD`, `CPUS_PLAP`) → `E_NOTIMPL`. The provider then never loads in user processes through CredUI.
- **With no ticket pending, `GetCredentialCount` returns 0.** No tile, no fields, nothing shown, and every other provider works as before. A person at the device can't do anything with it.
- **With a ticket pending:**
  - one tile, "RemoteHub – signing in *DOMAIN\user*…";
  - `pbAutoLogonWithDefault = TRUE`;
  - `GetSerialization` packs `KERB_INTERACTIVE_UNLOCK_LOGON` with `KerbInteractiveLogon` and the Negotiate package (`LsaLookupAuthenticationPackage`), then returns `CPGSR_RETURN_CREDENTIAL_FINISHED`.
- **No password field** (`CPFT_PASSWORD_TEXT`) holds the value, and `GetStringValue` never returns it. The tile has only a label and a status text.
- **Background pipe thread:**
  - Pipe I/O runs on its own thread, and **no LogonUI call ever waits for it**.
  - On `Pending` the thread sends `Claim` at once and keeps the `Ticket` that comes back protected. Only then does it wake LogonUI through `ICredentialProviderEvents::CredentialsChanged(adviseContext)`, so the tile appears only when the ticket is already there.
  - `GetSerialization` uses only the ticket already received. If none is there, it returns `CPGSR_NO_CREDENTIAL_NOT_FINISHED`.
  - The `Ticket` message also carries the account's SID, for `ICredentialProviderCredential2::GetUserSid`. Without one, the tile goes under "Other user".
- **`ReportResult`:**
  - sends the NTSTATUS and substatus to the service;
  - clears the ticket;
  - **never retries** (lockout).
  - Status text: "Automatic sign-in failed" with no detail. The detail goes only to the operator.
- **`UnAdvise` / `Release`:** zero everything and close the pipe.
- **Memory:**
  - The password lives in a `VirtualAlloc`'d buffer, protected with `CryptProtectMemory(CRYPTPROTECTMEMORY_SAME_PROCESS)` until `GetSerialization`, and zeroed with `SecureZeroMemory` after packing.
  - The serialization buffer belongs to LogonUI once returned, which is per the API contract. Our own copies are zeroed.

### 8.3 Robustness (a crash means nobody can sign in)

- The parser is a hand-written, length-checked TLV reader with no allocations based on attacker lengths beyond 4 KB. It is **fuzzed** with libFuzzer in CI.
- Every COM method has a `try/catch` boundary. No exception crosses into LogonUI.
- With the service gone or the pipe broken, the provider is inert, with 0 credentials.
- **Lab test:** stop the service, kill it in the middle of a claim, send malformed pipe messages, flood the pipe, start 50 sign-ins in a row. The sign-in screen must keep working with password and PIN for local users in every case.

### 8.4 Installation and integrity

- **Install location:** `C:\Program Files\RemoteHub Agent\RemoteHubCredentialProvider.dll`. Program Files ACLs: Administrators and SYSTEM write, Users read and execute. **Never** install it in a user-writable or `ProgramData` folder.
- **Registration in the agent MSI** (component conditioned on `AUTOSIGNIN=1`), with WiX `RegistryKey`, not `regsvr32`:
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CLSID}` (default = "RemoteHub Agent");
  - `HKCR\CLSID\{CLSID}\InprocServer32` (default = full path, `ThreadingModel = Apartment`).
  - The CLSID is a fixed GUID that never changes, so IT can exclude it: **`{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}`**.
- **Its own MSI feature, `CredentialProvider`,** which isn't installed by default. Its level is 1000, above `INSTALLLEVEL`; `AUTOSIGNIN=1` sets it to 1 and `AUTOSIGNIN=0` to 0, which rules it out.
- **Upgrades** (decided, §15 Q6), the same rule as `VIRTUALDISPLAY`:
  - `AUTOSIGNIN=1` installs or keeps the provider; `AUTOSIGNIN=0` removes it;
  - no value (an ordinary update) **keeps what's installed**. Windows Installer carries the feature's state over to the new version (`MigrateFeatureStates`), so nothing has to be remembered.
- **Adding or removing it on an installed agent** (RemoteHub: right-click → *Install credential provider* / *Remove credential provider*, `AgentInstaller.ChangeCredentialProvider` over the agent's PowerShell):
  - **Adding:** `msiexec /i <package> ADDLOCAL=CredentialProvider`. RemoteHub uploads its own agent package, because Windows' cached copy of the MSI has no files in it. This only works when the device runs the **same build** (same ProductCode). Any other version would be an update, so RemoteHub refuses with "Update the agent first".
  - **Removing:** `msiexec /i {ProductCode} REMOVE=CredentialProvider`, which needs nothing uploaded.
  - **Both are maintenance installs:** the service isn't stopped and its enrollment is untouched. `AgentEnroll` runs only when the product itself is installed (`NOT Installed`).
  - Still only the signed MSI registers the provider (§8.5).
  - RemoteHub remembers what it last did (`AgentDevice.CredentialProvider`) to choose the menu entry, and audits it (`agent.credential_provider`).
  - Exit code 3010 (the sign-in screen has the DLL loaded) is reported as "after the device restarts".
  - The entry shows only when RemoteHub's agent package contains the feature, so release builds won't show it until phase 5.
- **Uninstall,** or an upgrade with `AUTOSIGNIN=0`, removes both keys and the DLL. `Enrollment.Run cleanup` also deletes the keys, in case MSI didn't.
- **Through a jump host:** installing the agent through a jump host (supported since jump hosts) passes `AUTOSIGNIN` the same way. Nothing in the provider depends on the route.
- **Signing:** the DLL **and** `RemoteHubAgent.exe` must be Authenticode-signed (SHA-256, timestamped).
  - **Blocker:** `.github/workflows/release.yml` has no code signing yet ("Code signing goes here once a certificate is available"). The provider must not ship unsigned. Customers' WDAC/App Control and Smart App Control will block it, and an unsigned DLL in LogonUI can't be told apart from a malicious one.
  - RemoteHub has no signed build manifest of its own. (`build.manifest` / `build.manifest.sig` in the publish folder belong to Microsoft's PowerShell.Native package.) Integrity of updates comes from the SHA-256 of the MSI in the release metadata, which `UpdateService` checks. The DLL ships inside the agent MSI and is covered that way, and by its Authenticode signature.
- **Until there's a certificate (lab only):**
  - Sign the DLL and the agent with a self-signed code-signing certificate that only the lab VMs trust: LocalMachine `Root` + `TrustedPublisher`.
  - That way the signature check below runs unchanged and gets tested too.
  - There's no switch or registry value to skip the check. Such a value would also work on customer devices.
  - The release build fails if the DLL isn't signed by the release certificate.
- The service checks the DLL's Authenticode signature, and that the registry's `InprocServer32` path points to it, before it offers `AutoSignIn` in Hello. If either check fails → off, and it's logged.

### 8.5 Antivirus and EDR

`LogonUI.exe` isn't replaced or changed. It loads the DLL through the documented credential provider API, like Duo, Okta, RSA and Cisco do.

Security tools may still alert, because registering a credential provider is a known attacker technique (MITRE ATT&CK T1556, *Modify Authentication Process*). Typical triggers:
- a write to `…\Authentication\Credential Providers\{CLSID}`, which is a common Sysmon/EDR rule;
- a new DLL with little reputation loaded into LogonUI;
- the agent's general behaviour of a remote access tool (screen capture, `SendInput`, Ctrl+Alt+Del from a service, low-level input hooks for the privacy screen, and tunnelling on a jump host).

Measures:
- **Only where asked for:** install the provider only with `AUTOSIGNIN=1`, so most devices never get the registry write.
- **One known writer:** registration is done by the signed MSI (`msiexec`), never by the service at runtime.
- **One stable identity:** the same publisher certificate for every release, the fixed CLSID, and a fixed file name. Reputation builds over time.
- **Before each release:** submit the signed MSI and DLL to Microsoft (Security Intelligence false-positive submission) and to the major EDR vendors on request.
- **Allowlist data for customers in the README:**
  - the CLSID;
  - the registry key;
  - the DLL path;
  - the publisher;
  - SHA-256 hashes per release;
  - the agent's named pipe.
- **Lab:** install with Microsoft Defender on (real-time protection plus cloud protection). Record every alert in the test report.

## 9. After the sign-in – the unattended session (T6)

An administrator's desktop is now open on the physical console. Anyone standing at the device can see it and, without further measures, use the keyboard and mouse.

### 9.1 Privacy screen from the start

Privacy mode (0.18.3) provides what v2 had to leave out: an overlay on the device's screen that the admin's picture doesn't include, and a local input block. Automatic sign-in uses it as protection, not as an option (decided, §15 Q5):

- **On by default.** When the ticket is created, the service sets `_screen` to `DesktopPrivacyScreen("Message", BlockLocalInput: policy.PrivacyScreen == WithInputBlock)`.
  - The existing code then sends it to the helper that starts in the new session after `Begin` (`DesktopEndpoint` ≈ line 303). It's also kept with the resume token (privacy-mode-design §14).
  - The `Message` style tells the person at the device that IT is working on it: "*DOMAIN\user* is signed in remotely. The screen stays hidden while they work." It doesn't mention Disconnect (0.18.11, see §9.2 *Banner*).
- **No screen snips while it is up** (0.18.11, `SnipGuard`). The overlay is excluded from every capture, so a snip doesn't include it either. Windows then shows the snip's frozen picture full screen, above every topmost window. In manual testing, opening the Snipping Tool in the admin session made the screen visible at the device. While any privacy screen is up (not only after automatic sign-in):
  - Print Screen and Win+Shift+S are dropped by a low-level keyboard hook, for the admin's keys and the device's own.  - A snipping tool that shows a window anyway (`ScreenClippingHost.exe`, `SnippingTool.exe`, `ScreenSketch.exe`), for example one started from the Start menu, is closed as soon as its window appears. This is logged in the agent's log.
  - Alt+Tab and Win+Tab are dropped as well, because their window thumbnails are drawn above topmost windows too.
- **The device's keyboard and mouse are always blocked** in a session created by automatic sign-in (0.18.11), whatever the `PrivacyScreen` level (1 or 2). Ctrl+Alt+Del and Win+L can't be blocked; Windows handles them before any program.
- **The `PrivacyScreen` right isn't needed to have it on.** The service turns it on for the session it created. It is needed to turn it **off**: an operator without the right can't remove it, and loses nothing by that. The overlay lets clicks through, and the admin's picture doesn't include it (privacy-mode-design §14).
  - **Resume must keep it.** Today a resumed connection restores the privacy screen only when the operator holds the right (`DesktopEndpoint` ≈ line 103: `_screen = _privacyRight && … ? resumed.Screen : null`). For a session created by automatic sign-in, the resume entry marks the screen as set by the service, and it is restored regardless of the right.
- **When the device can't show it** (policy `PrivacyScreen` = 0, or Windows build < 19041, `DesktopEndpoint.PrivacyScreenSupported`), automatic sign-in still works, as in v2, with only the end action (no banner since 0.18.11, §9.2). In that case:
  - RemoteHub says so in the start dialog: "The device can't hide its screen – people at the device will see the session".
  - The README and the install dialog name this case as the known limit.
  - IT can turn automatic sign-in off for such devices with `AutoSignInRequirePrivacyScreen` = 1 (§5, §15 Q7).
- **Remaining gaps, from privacy mode itself:**
  - The secure desktop (UAC, Ctrl+Alt+Del screen) isn't covered until the lab test in privacy-mode-design §5.7 closes it.
  - Ctrl+Alt+Del can't be blocked locally, so a person at the device can always lock the session.
  - Between the sign-in and the new helper's `Begin` (start + about 1.5 s to let the session settle, `DesktopEndpoint` ≈ line 246), the new desktop is on the screen without the overlay. The lab measures this gap (§14). If it's longer than the Windows "Welcome"/first-sign-in animation, the helper sends the privacy screen before `Begin` for sessions created by automatic sign-in.

### 9.2 When the connection ends

- **No banner** (changed in 0.18.11). The session belongs to the admin, and nobody at the device owns it, so there is nothing for a person there to hide, show or end. The helper gets `HelperInit.SignedInRemotely` and shows only the privacy screen with its message. This applies to the helper started in the new session while the sign-in is being confirmed, and to a resumed connection. Ctrl+Alt+Del → Lock still works at the device. With no privacy screen (policy `PrivacyScreen` = 0), nothing at the device shows that the admin is working.
- **When the connection ends on purpose,** the session created by automatic sign-in gets the **end action**: it is **locked** (`WTSDisconnectSession`, or `LockWorkStation` from the helper) or **signed out** (`WTSLogoffSession`). Decided (§15 Q2):
  - **`Lock` is the default**, so the operator can come back to open work.
  - **Per connection** in RemoteHub: *When the connection ends: Lock / Sign out*, inherited like the other agent settings, sent in `DesktopStart` (§6.1).
  - **Device policy** `AutoSignInEndAction` (`Lock` | `SignOut`) **wins** when set, so IT can force `SignOut` on shared devices.
  - With `SignOut`, RemoteHub warns before disconnecting that unsaved work in that session is lost.
  - "On purpose" means any of: closed by RemoteHub, Disconnect on the device while RemoteHub is connected, idle timeout, a refusal, and service stop. A **dropped** connection is handled below.
  - **How to lock:** on the console of a client SKU, `WTSDisconnectSession` on the console session shows the lock screen and needs no helper, so it's used first. On Server it disconnects the console session instead; that is just as safe (the next sign-in reconnects to it), but RemoteHub and the audit text say "disconnected" there. `LockWorkStation` from the helper is used only while a helper is running.
- **A dropped connection: covered for 2 minutes, then signed out** (decided, §15 Q8). For a session created by automatic sign-in (the connection is "lost" in `DesktopEndpoint`'s terms, so a resume token was released):
  - **The helper keeps running** in the session for 2 minutes, with the privacy screen and the local input block as they were. It sends no frames and accepts no admin input; it only holds the overlay.
  - **No banner** (see above): the overlay stays as it is until the session is signed out or a resumed connection takes over.
  - **The resume window is 2 minutes** for this session instead of the usual 1 (`DesktopResume`). A resumed connection takes over the running helper: the privacy screen is already up, and work continues where it was.
  - **If nobody resumes within 2 minutes,** the session is **signed out** (`WTSLogoffSession`), **whatever the end action** says. The Lock/SignOut setting and the device policy `AutoSignInEndAction` apply only to connections that end on purpose. Audit 1107 with reason "connection lost".
  - **If the helper itself ends during the 2 minutes** (crash, a local Ctrl+Alt+Del → Lock or Sign out, the console moving to another session), the overlay is gone. The service then signs the session out at once, unless it is already locked or signed out. A lock by the person at the device counts as safe and is left for the 2 minutes to run out.
  - **An exception to privacy mode's rule** "everything ends with the helper" (privacy-mode-design §1.1 rule 5). The rule still holds, because the overlay ends with the helper. What's new is that the helper doesn't end with the connection for these sessions. Privacy-mode-design §14 gets a note.
  - **Why sign out rather than lock:** with the helper holding the overlay, a lock isn't needed during the 2 minutes. After them, nobody is coming back on this connection, and an unattended, auto-created admin session shouldn't stay on the device.

### 9.3 Crash safety

- The service writes the session id + logon time of each automatically signed-in session to `%ProgramData%\RemoteHub\Agent\autosignin.json` (no secrets; ACL SYSTEM + Administrators, same as the rest of the folder). When the connection drops, it adds the time of the drop.
- At service start, and when a desktop connection ends, every listed session that still exists and has no live agent connection is dealt with, and then the entry is removed:
  - a session marked as dropped, or one whose helper is gone, is **signed out** (§9.2);
  - any other session gets the end action.
- If the service restarts during the 2 minutes, the session is signed out at start: it's marked as dropped, and the new service instance doesn't take over the old helper.

## 10. Handling secrets in .NET (service and RemoteHub)

### 10.1 Service

- **A receive path of its own for `SignInRequest`.** Today every client message goes through `ReceiveWs` (`DesktopEndpoint` ≈ line 762). That reads into a new 64 KB `byte[]`, appends to a `MemoryStream`, and returns `ToArray()`: three unpinned copies that are never zeroed. `ClientLoop` is already running when `SignInReady` is sent, so the password would arrive that way. Instead:
  - `ReceiveWs` takes a receive buffer that is allocated once per connection and pinned (`GC.AllocateArray(pinned: true)`).
  - After the first fragment of a message, it looks at the type byte. For `SignInRequest` it reads the rest straight into a second pinned buffer of at most 1 KB, without the `MemoryStream`, and refuses anything larger (protocol error, the connection ends).
  - It zeroes the receive buffer right after copying.
  - Never through `DesktopWire.ReadJson`, `Encoding.GetString` or any `string`.
- **Protect it at once** with `CryptProtectMemory(SAME_PROCESS)`, padded to 16 bytes.
- **Unprotect it only to write it to the pipe,** then zero it. The ticket object is `IDisposable` and zeroes itself in `Dispose` and when it expires.
- **What stays outside our control:** the TLS layer (`SslStream`) and Kestrel's buffer pools decrypt into buffers we can't zero. That's the same exposure as the Basic header on every connection (§6.1), so it adds nothing new. The doc says so rather than claiming more.
- **No dumps:** exclude the agent from Windows Error Reporting full dumps (`WerAddExcludedApplication`), or set `DontShowUI` + `LocalDumps\DumpType=0` for `RemoteHubAgent.exe` at install. Document this. The agent already sends no crash reports.
- **Logs:**
  - `AgentLog` never receives the message.
  - Add a test that greps the log and event log text for the test password after a full flow.

### 10.2 RemoteHub

- **Convert just in time:** stored passwords are sealed in the local database's `secrets` table and are a plain `string` in `CredentialSet.Password` once loaded – there is no `SecureString` path, and that string can't be zeroed. So: encode it into the outgoing binary frame only when sending (no other copy, never through JSON), zero the frame buffer after the send, and don't keep any further copy. A password typed in for this connection follows the same path.
- **Send conditions:**
  - only when the agent sent `SignInReady` in *this* connection;
  - the pinned fingerprint matched (through a jump host: the **target's** fingerprint, end to end, never the jump host's);
  - the connection's setting is on.
  - Never on reconnect without a new `SignInReady`.
- **Missing password:** if the credential has no password (prompt each time), ask in the existing password dialog. Don't store it.
- **RemoteHub audit log:** "Automatic sign-in requested / succeeded / failed (reason)" for the connection, with "via *JUMP01*" when it went through a jump host. Never the password.

## 11. Audit

**Agent (log file + Application log, source *RemoteHub Agent*).** 1104, 1105 and 2103 are now used by privacy mode (`DesktopPrivacyScreen`, `DesktopUserHid`, `DesktopPrivacyRefused`), so the IDs from v2 move:

| ID | Event | Level | Text |
|---|---|---|---|
| 1106 | `AutoSignInSucceeded` | Information | Automatic sign-in: *account* from *source* signed in to console session *n* (privacy screen on / not available) |
| 1107 | `AutoSignInEnded` | Information | Automatic sign-in session *n* locked / disconnected / signed out (reason: end action, connection lost and not resumed within 2 minutes, helper ended after a drop, service start) |
| 2104 | `AutoSignInRefused` | Warning | Automatic sign-in refused: *account* from *source* – *reason* (policy, right, view-only, session state, session changed at claim, existing session, rate limit, timeout) |
| 2105 | `AutoSignInFailed` | Warning | Automatic sign-in failed: *account* – Windows reported *NTSTATUS name* |
| 3101 | `AutoSignInMismatch` | Error | Automatic sign-in: the signed-in account didn't match – session logged off |
| 3102 | `AutoSignInUnavailable` | Error | Automatic sign-in unavailable: pipe, registration or signature check failed (detail) |

*Source* is the address the agent sees. Through a jump host that's the jump host's address; the jump host's own audit (1200–1202) shows who used its tunnel.

**Windows:** Security log 4624 with **logon type 2** (interactive) on the device, plus 4648 where applicable. README note for SIEM: an interactive logon of an admin account on a client device means automatic sign-in when a 1106 event comes right before it.

## 12. Phase 2 (not in v1, design kept open)

- **Unlock the operator's own locked session** (`CPUS_UNLOCK_WORKSTATION`). Only if the locked session's user SID equals the authenticated SID, under the same right, policy and ticket. Also needs the "end action = lock again" rule.
- **RBAC server:** issues `AutoSignIn` in its signed rights token instead of the Windows groups (agent-desktop-draft §4.1).

(Privacy mode, phase 2 in v2, is done and used in §9.1.)

## 13. Implementation checklist

### Build order

Each phase can be tested and merged on its own. Nothing is offered to customers before phase 5: until then the MSI doesn't offer `AUTOSIGNIN` outside the lab, and Hello never reports `AutoSignIn`. Phase 1 comes early because the riskiest unknown is native code in LogonUI on Windows 11 24H2 and Server 2025, and it should be proven before any protocol work is built on it.

| Phase | What | Done when |
|---|---|---|
| **0 – Signing** | Authenticode signing in `release.yml` (the blocker, §8.4); a self-signed lab certificate that only the lab VMs trust; the release build fails if the DLL isn't signed by the release certificate | The agent exe and the provider DLL are signed in CI; the lab VMs trust the lab certificate |
| **1 – Provider spike** | `src/RemoteHub.CredentialProvider` (§8.1–§8.3): hardening flags, `CPUS_LOGON` only, console-session check, `KERB_INTERACTIVE_UNLOCK_LOGON` packing, TLV parser + libFuzzer target, a lab-only test ticket source; MSI component behind `AUTOSIGNIN` (§8.4) | A lab VM signs in a test account through the provider; the sign-in screen survives the robustness tests in §8.3 |
| **2 – Service side** | `AgentPolicy` values and rights, `ShellIdentity.UserSid`, `AutoSignInBroker` (pipe, peer checks, ticket, checks at `Claim`), `AutoSignInGate`, signature check, `SelfTest`, audit IDs | A fake client triggers a full sign-in on a lab VM; the unit tests for the policy matrix, pipe and ticket pass |
| **3 – Protocol and RemoteHub** | Protocol changes (§6.1), the `SignInRequest` receive path (§10.1), the flow (§6.4), consent by SID (§6.3), the RemoteHub setting, start dialog and `AgentDesktopSession`, the ClrMD secrets test | The operator signs in from RemoteHub end to end, directly and through a jump host |
| **4 – After the sign-in** | Privacy screen at start and on resume (§9.1), the 2 minutes after a drop and the sign-out, the end action, `autosignin.json` (§9.2, §9.3) | The physical-console lab cases in §14 pass |
| **5 – Ship** | Install dialog, ADMX, README, `agent-spec.md`, the Defender/EDR lab run (§8.5), the separate security review | Release |

**Status (2026-10-14):**
- **Phase 0:**
  - Done: `build/Sign-Files.ps1`; `build-installer.ps1 -SigningCertificate | -LabSigning` signs both exes and both MSIs, each before it's packed into the next step; `release.yml` imports the PFX from the secrets `SIGNING_CERTIFICATE_PFX` / `SIGNING_CERTIFICATE_PASSWORD` and builds unsigned, with a warning, until they exist; `lab/New-LabSigningCertificate.ps1`; `Install-LabAgent` makes each VM trust the lab certificate.
  - Open: the release certificate itself.
- **Phase 1:**
  - Done: the provider; unit tests; the fuzzer (CI runs it for 60 s); the MSI component behind `AUTOSIGNIN`, which needs a signing certificate (`build-installer.ps1 -CredentialProvider`); the optional `autoSignIn` in `AgentInstaller` (no UI yet); the Lab build and `RemoteHubCpTestServer.exe`.
  - Lab cases C-PROV-01 to 05 pass on Windows 11 26H2 and Server 2025: signed install, inert without a ticket, sign-in through the provider, kept on an update, removed with `AUTOSIGNIN=0`.
  - Open from §8.3/§14: the robustness lab cases (service killed mid-claim, malformed and flooding pipe clients, 50 sign-ins in a row, PIN sign-in with the provider registered) and the packing round trip against `LsaLogonUser`.
- **Phase 2:**
  - Done:
    - `AgentRight.AutoSignIn`, policies `NoUserBehavior` and `AutoSignInRequirePrivacyScreen`, and `ShellIdentity.UserSid`.
    - `IDesktopSessions.UserSid` / `LogOff`.
    - `Desktop/AutoSignIn.cs`: `IAutoSignIn`, `ProtectedSecret`, `AutoSignInWire`, `AutoSignInGate`, the NTSTATUS texts.
    - `Desktop/AutoSignInBroker.cs`: the pipe (§7.1, always created at service start, so the provider can be added to a running agent), client checks (§7.2), Pending → Claim → re-check → Ticket → Result, and availability (registration, `ExcludedCredentialProviders`, Authenticode trusted and **signed by the same certificate as the agent**).
    - Audit 1106, 2104, 2105, 3101, 3102.
  - How Ctrl+Alt+Del is handled: the broker waits 3 s for the provider; if it hasn't connected (the sign-in screen waits for Ctrl+Alt+Del or shows the lock picture), it sends Ctrl+Alt+Del and waits up to 20 s more.
  - Open: `SelfTest` reporting, and `Enrollment cleanup` of the provider keys (the MSI removes them).
- **Phase 3:**
  - Done:
    - Protocol: `DesktopRights.AutoSignIn`, feature `autoSignIn`, `DesktopStart.AutoSignIn`, the binary `SignInRequest` (31), and the states `SignInReady` / `SigningIn` / `SignedIn` / `SignInFailed`.
    - `DesktopEndpoint`: the checks of §6.2, `SignInRequest` read straight into a pinned buffer and protected (never through the `MemoryStream`), the SID check after sign-in with sign-out on a mismatch, and consent by SID (`IsSelfSession`).
    - RemoteHub: setting *Sign in automatically when nobody is signed in* (editor, PowerShell remoting/agent rows), app policy `DisableAgentAutoSignIn`, the checkbox in the agent desktop's start panel, the password sent once on `SignInReady` and its frame wiped after sending, notices, audit `agent.auto_sign_in`.
  - Not done: asking for the password when the connection has none stored (§10.2). RemoteHub says so and the operator signs in on the device's screen.
- **Phase 4:**
  - Done:
    - **Privacy screen from the start:** set when the sign-in begins (Message style; local input blocked when the policy `PrivacyScreen` is 2), so every helper in the new session gets it after `Begin`. The sign-in screen itself isn't covered. It's kept across a resume whatever the account's right (`DesktopResume.Entry.ScreenByService`), and dropped again if the sign-in fails.
    - **End action:** device policy `AutoSignInEndAction` (wins) → `DesktopStart.AutoSignInEndAction` (RemoteHub's setting *When an automatic sign-in's connection ends*) → Lock. Lock = `WTSDisconnectSession` (the lock screen on client Windows; a disconnected session on Server). `SignedIn` carries the end action to RemoteHub.
    - **Dropped connection:** `AutoSignInSessions.Dropped` keeps the helper, with its overlay and input block, for 2 minutes. A task reads and discards its output. The resume token lives as long, and RemoteHub keeps reconnecting that long. A resumed connection starts its own helper and ends the old one once the new one covers the screen. Without a resume, the session is signed out. If the helper ends sooner, it's signed out at once (Disconnect on the banner, a crash), unless the session is locked at the device.
    - **Crash safety:** `%ProgramData%\RemoteHub\Agent\autosignin.json` (session, SID, account, end action, dropped; no secrets). Left entries are dealt with at service start. Every action first checks the session still belongs to the account (SID). Deleted on uninstall.
    - **RemoteHub:** the setting, the end action shown in the `SignedIn` notice, and *Sign out on the device?* before a disconnect that signs out (tab X, middle click, Ctrl+W, toolbar Disconnect, closing the tab group).
    - Audit 1107 for each lock or sign-out.
  - Tests: 10 unit/flow tests. The lab cases (§14, physical-console behaviour) are planned and haven't run yet.
  - Not done: the banner text "Signed in remotely by RemoteHub" (the existing banner shows), and measuring the gap before the overlay appears (§9.1, lab).

### Agent (`src/RemoteHub.Agent`)
- [ ] `AgentPolicy`: add `AgentRight.AutoSignIn` (default nobody), `NoUserBehavior` (`SignInScreen` default | `AutoSignIn`) and `AutoSignInEndAction` (`Lock` default | `SignOut`), plus `AutoSignInRequirePrivacyScreen` (DWORD, default 0). Include them in `Refused`. Add tests in `AgentDesktopTests`.
- [ ] `ShellIdentity`: carry the user SID as `UserSid` (the resolved `DOMAIN\user` is already `Account`).
- [ ] `Desktop/DesktopProtocol.cs`: `DesktopRights.AutoSignIn` (appended after `PrivacyInputBlock`), `DesktopFeatures.AutoSignIn`, `DesktopStart.AutoSignIn` and `DesktopStart.AutoSignInEndAction`, message `SignInRequest` (binary, ≤ 1 KB), states `SignInReady` / `SigningIn` / `SignedIn` / `SignInFailed` + reason codes.
- [ ] New `Desktop/AutoSignInBroker.cs`: the pipe and its instances (§7.1), peer checks (§7.2), ticket and the checks at `Claim` (§7.3), protected memory (§10.1), cancel on every end condition.
- [ ] New `Desktop/AutoSignInSessions.cs`: the end action, the 2 minutes after a drop and the sign-out, and crash safety (§9.2, §9.3).
- [ ] `DesktopEndpoint` / helper: on a dropped connection of an auto-signed-in session, keep the helper with its overlay (no frames, no admin input), the banner countdown, Disconnect = sign out; a resumed connection takes over the running helper.
- [ ] `DesktopResume`: a 2-minute window for auto-signed-in sessions, carrying the running helper.
- [ ] New `AutoSignInGate`: 3 attempts per SID per 15 min (§6.2 check 7).
- [ ] `DesktopEndpoint`:
  - the checks in §6.2, including control and not view-only;
  - the flow in §6.4;
  - `_screen` set for the new session, and kept on resume without the `PrivacyScreen` right (§9.1);
  - `_allowedBy` from the SID check, and `IsSelf` by SID (§6.3);
  - the SID check after sign-in;
  - the `SignInRequest` receive path in `ReceiveWs` (§10.1).
- [ ] `Program.cs`: create the broker before `app.RunAsync()`; run the end action for sessions left over in `autosignin.json`.
- [ ] `AgentLog` / `AuditEvent`: IDs 1106, 1107, 2104, 2105, 3101, 3102.
- [ ] `Enrollment.cs`: `.rhagent` option `autoSignIn`; `cleanup` removes the provider keys and `autosignin.json`.
- [ ] `SelfTest`: report whether the provider is registered and signed, the pipe is up, and the privacy screen is available, without triggering a sign-in.

### Credential provider (`src/RemoteHub.CredentialProvider`, new)
- [ ] Project, hardening flags, static CRT (§8.1).
- [ ] `ICredentialProvider`, `ICredentialProviderCredential2`, `ICredentialProviderSetUserArray` (only to return the right user tile).
- [ ] Console-session check, pipe client thread, server PID and image check, TLV parser (§7, §8.2).
- [ ] `KERB_INTERACTIVE_UNLOCK_LOGON` packing; `ReportResult` → `Result` message.
- [ ] libFuzzer target for the parser; unit tests for packing.

### Installer and build
- [ ] `installer/RemoteHub.Agent.Installer/Package.wxs`: property `AUTOSIGNIN` (Secure, next to `VIRTUALDISPLAY`); a component with the DLL and the two registry keys (CLSID `{718FFFD2-6671-4FA4-9FA8-86D1CD4D84C3}`), conditioned on it; removed when uninstalling or when set to 0; **kept on an update without the property** (§8.4).
- [ ] `build/build-installer.ps1`, `ci.yml`, `release.yml`: build the vcxproj; **Authenticode signing of the DLL and the agent exe (blocker)**; the release build fails if the DLL isn't signed by the release certificate.
- [ ] Agent install dialog (`AgentInstallDialog.cs`): checkbox "Allow automatic sign-in (installs a credential provider)", off, with a short warning text that also names the known limit for devices without the privacy screen (§9.1).

### RemoteHub (`src/RemoteHub`)
- [ ] `RdpOptions`: `AgentAutoSignIn` (bool?, inherited, default false) and `AgentAutoSignInEndAction` (`Lock` default | `SignOut`, inherited) + the editor UI under *PowerShell remoting* next to *Remote desktop through*. Run the ui-consistency process for the editor and tab changes.
- [ ] Disconnect with end action `SignOut`: warn that unsaved work in the signed-in session is lost.
- [ ] `AppPolicy`: `DisableAgentAutoSignIn`.
- [ ] `AgentDesktopSession` / `AgentDesktopView`:
  - the option in the start dialog, only when `Hello.Rights.AutoSignIn` and the start isn't view-only;
  - the notice when the device can't hide its screen (§9.1);
  - handle `SignInReady` → send the password once (§10.2);
  - show the `SignInFailed` reasons;
  - show the privacy screen badge as already on after `SignedIn`.
- [ ] `AuditLog`: the new entries, with the jump host when there is one.

### Docs
- [ ] `agent-spec.md` §12: move automatic sign-in from backlog to implemented; add the right and the policy values.
- [ ] `agent-desktop-draft.md` §4.4/§4.5/§6: point to this document and replace the `NoUserBehavior` row with the final values.
- [ ] `privacy-mode-design.md`: note that automatic sign-in turns the privacy screen on by itself (§9.1), and that its helper outlives a dropped connection for 2 minutes (§9.2).
- [ ] README *Remote desktop through the agent*: the setting, the four switches and the prerequisites, `AutoSignInRequirePrivacyScreen`, the privacy screen, the end action and the sign-out 2 minutes after a drop, the known limit (§9.1), the event IDs.

## 14. Tests

**Unit and integration (in CI):**
- Rights and policy matrix: every combination of install, policy, right, `Control`, `SecureDesktop`, view-only and client setting. The feature is offered and accepted only when all are on.
- Refusals:
  - a view-only start;
  - locked, disconnected and non-console sessions;
  - a user already signed in;
  - an existing session of the same account;
  - a second pending ticket;
  - a request without `SignInReady`;
  - a request after 30 s;
  - the 4th attempt within 15 min for the same SID, while another account from the same source address is still allowed.
- Ticket:
  - single use;
  - expiry;
  - wrong nonce or session;
  - a user signs in at the device between `SignInRequest` and `Claim` → no ticket is sent;
  - cancelled on disconnect, session change or policy change.
- Pipe:
  - a non-SYSTEM client is refused;
  - a client in a session other than the console is refused;
  - a non-LogonUI image is refused;
  - squatting: the pipe is created first by a test process → the feature reports off;
  - one client holds an instance and never sends → the console's client still connects;
  - oversize and malformed messages.
- Secrets:
  - after a full fake flow, the test password doesn't appear in the log file, the event log text, or a managed heap snapshot (`ClrMD`) of the service;
  - in the heap snapshot, no `byte[]` outside the pinned buffers contains the UTF-16LE password, which proves `ReceiveWs` doesn't copy it.
- Privacy screen: after `SignedIn`, the new helper gets `PrivacyScreen` with `BlockLocalInput` as the policy says; an operator without the `PrivacyScreen` right can't turn it off, and still has it after a resumed connection.
- `AutoSignInRequirePrivacyScreen` = 1 with the privacy screen unavailable → `DesktopRights.AutoSignIn` is false.
- Dropped connection (fake clock): the helper stays; a resume at 1:59 takes it over; no resume → signed out at 2:00 with the end action set to `Lock`; the helper ending during the 2 minutes → signed out at once; a service restart with a dropped entry in `autosignin.json` → signed out.
- Provider parser: fuzzing (CI time budget); packing round trip against `LsaLogonUser` in a test harness with a test local account.

**Lab (`docs/test-lab-spec.md`, real devices as LocalSystem):**
- Windows 11 24H2 and Server 2025:
  - local account;
  - domain account;
  - DC unreachable: the agent's own network sign-in fails first (network logons can't use cached credentials), so automatic sign-in must refuse cleanly;
  - wrong / expired password;
  - account locked;
  - "Deny log on locally";
  - legal notice shown;
  - `DisableCAD = 0`, including the "Press Ctrl+Alt+Delete" screen on the Default desktop (Windows 11, Server 2025);
  - end action: `Lock` and `SignOut` from the connection, and the device policy overriding the connection;
  - through a jump host: the same flow works, and a packet capture on the jump host shows only TLS to the target.
- Server 2025 with a user signed in over RDP while the console is on the sign-in screen: the RDP session's LogonUI never connects to the pipe, and automatic sign-in on the console still works.
- Privacy screen after sign-in:
  - measure the time between the new desktop appearing on the device and the overlay (§9.1);
  - with input blocked, local keys and clicks outside the banner have no effect, and Disconnect works;
  - policy `PrivacyScreen` = 0 and a build < 19041: sign-in works and RemoteHub shows the notice.
- Installer: update without `AUTOSIGNIN` keeps the provider; `AUTOSIGNIN=0` removes it and both registry keys.
- Entra ID and hybrid joined devices (not proven yet):
  - Entra ID joined, cloud-only account (`AzureAD\user@tenant`): first check the agent's own sign-in, which uses a network logon.
  - Entra ID joined: the provider's password sign-in through Negotiate/CloudAP, the first sign-in on the device without internet, the passwordless policy, and Windows Hello for Business being required.
  - Hybrid joined with an on-premises account: expected to behave like a domain-joined device.
- Physical-console behaviour:
  - banner;
  - a network drop: the overlay and the input block stay, the banner counts down, resuming within 2 minutes continues in the same desktop, and without a resume the session is signed out;
  - local Ctrl+Alt+Del → Lock during the 2 minutes;
  - lock / sign-out on Disconnect and idle timeout, killing the service, and a reboot during a session.
- Robustness: the provider registered while the service is stopped and while the pipe is flooded → the normal sign-in screen and Windows Hello PIN still work.
- `ExcludedCredentialProviders` with our CLSID → the provider is gone and the agent reports the feature off.

## 15. Security review – decisions

The questions from v1 were decided by the owner on 2026-10-13. Q5 was reopened by privacy mode and decided again, and Q7–Q9 were added and decided, on 2026-10-14. The reviewer may still challenge any of them.

| # | Question | Decision |
|---|---|---|
| 1 | Is SYSTEM + LogonUI image + session id enough to identify the provider at the pipe, or use ALPC / local RPC with a security callback? | **Named pipe**, as in §7: less native code in LogonUI, easier to review and fuzz. |
| 2 | `Lock` or `SignOut` as the default end action? | **`Lock` by default**, configurable **per connection** in RemoteHub; the **device policy wins** when set (§9.2). |
| 3 | Limit the right to non-built-in accounts (refuse RID 500) or a dedicated group? | **No extra rule:** any account that holds the `AutoSignIn` right (default nobody) and authenticated the connection. |
| 4 | Re-send the password, or keep it protected from the Basic sign-in for the connection? | **Re-send on request** (§6.1), keeping the extra copy to seconds, and only for connections that use the feature. |
| 5 | Does T6 (a person at the console) need privacy mode before release? | v2: no, ship with a documented limit. **v3: the privacy screen is on.** Privacy mode now exists, so the session created by automatic sign-in starts with the privacy screen on, and local input is blocked when the policy allows it. Only an operator with the `PrivacyScreen` right can turn it off (§9.1). Where the device can't show it, the v2 behaviour and the documented limit stay. |
| 6 | (added) What does an agent update without `AUTOSIGNIN` do? | **Keeps what's installed**, like `VIRTUALDISPLAY` (§8.4). |
| 7 | (v3) Should IT be able to allow automatic sign-in only where the privacy screen works? | **Yes, by policy, default off:** `AutoSignInRequirePrivacyScreen` (DWORD, default 0). With 1, `DesktopRights.AutoSignIn` is false when the privacy screen isn't available (policy `PrivacyScreen` = 0 or build < 19041) (§5). |
| 8 | (v3) What happens to the session when the connection drops? | **The helper and its privacy screen stay up for 2 minutes, then the session is signed out.** A resume within 2 minutes continues in the same desktop. After a drop the session is always signed out, whatever the end action; Lock/SignOut applies only when the connection ends on purpose (§9.2). |
| 9 | (v3) Does automatic sign-in need the `Control` right and a non-view-only start? | **Yes** (§1.1, §6.2 check 2). A view-only operator couldn't type the password into the sign-in screen, so the baseline requires it. |
